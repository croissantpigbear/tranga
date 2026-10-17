using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using API.ComicInfo;
using API.Downloads;
using API.ExtraMetadata;
using API.MangaConnectors;
using API.Schema.ActionsContext;
using API.Schema.ActionsContext.Actions;
using API.Schema.ExtraMetadataContext;
using API.Schema.MangaContext;
using API.Schema.NotificationsContext;
using API.Workers.PeriodicWorkers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Binarization;
using static System.IO.UnixFileMode;

namespace API.Workers.MangaDownloadWorkers;

/// <summary>
/// Downloads single chapter for Manga from Mangaconnector
/// </summary>
/// <param name="chId"></param>
/// <param name="dependsOn"></param>
public class DownloadChapterFromMangaconnectorWorker(MangaConnectorId<Chapter> chId, IEnumerable<BaseWorker>? dependsOn = null)
    : BaseWorkerWithContexts(dependsOn)
{
    public readonly string ChapterIdId = chId.Key;
    /// <summary>Website the chapter is downloaded from (for the per-website download limit)</summary>
    public readonly string MangaConnectorName = chId.MangaConnectorName;

    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private MangaContext MangaContext = null!;
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private ActionsContext ActionsContext = null!;
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private NotificationsContext NotificationsContext = null!;
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private ExtraMetadataContext ExtraMetadataContext = null!;
    private DownloadProgressReporter? ProgressReporter;

    protected override void SetContexts(IServiceScope serviceScope)
    {
        MangaContext = GetContext<MangaContext>(serviceScope);
        ActionsContext = GetContext<ActionsContext>(serviceScope);
        NotificationsContext = GetContext<NotificationsContext>(serviceScope);
        ExtraMetadataContext = GetContext<ExtraMetadataContext>(serviceScope);
        ProgressReporter = serviceScope.ServiceProvider.GetService<DownloadProgressReporter>();
    }
    
    /// <summary>
    /// Full ComicInfo.xml (series, credits, genres, age rating… from the stored extra details); falls back to Tranga's
    /// basic one if anything goes wrong, so a download never fails because of metadata.
    /// </summary>
    private async Task<string> BuildComicInfo(Chapter chapter, int pageCount, string sourceName)
    {
        try
        {
            ExtraMetadataData? extra = await ExtraMetadataContext.Entries.AsNoTracking()
                .Where(e => e.MangaId == chapter.ParentMangaId)
                .Select(e => e.Data)
                .FirstOrDefaultAsync(CancellationToken) is { } json ? ExtraMetadataStore.Parse(json) : null;
            return ComicInfoBuilder.Build(chapter, chapter.ParentManga, extra, pageCount, sourceName);
        }
        catch (Exception e)
        {
            Log.Warn($"Full ComicInfo.xml failed, writing the basic one: {e.Message}");
            return chapter.GetComicInfoXmlString();
        }
    }

    /// <summary>
    /// Loads the series' authors, tags, links and alternative titles for ComicInfo.xml. Uses a tracking query (it fills in
    /// the tracked series), because EF can't explicitly load many-to-many collections without tracking ("Include path
    /// 'Manga->Authors' results in a cycle"). A failure here never stops the download; ComicInfo.xml is then written
    /// without those details.
    /// </summary>
    private async Task LoadMangaDetails(Manga manga)
    {
        try
        {
            if (MangaContext.Entry(manga).State != EntityState.Detached)
            {
                await MangaContext.Mangas
                    .Include(m => m.Authors)
                    .Include(m => m.MangaTags)
                    .Include(m => m.Links)
                    .Include(m => m.AltTitles)
                    .Where(m => m.Key == manga.Key)
                    .LoadAsync(CancellationToken);
            }
            else
            {
                // Not tracked, so it's never saved: safe to fill in directly
                IQueryable<Manga> one = MangaContext.Mangas.AsNoTracking().Where(m => m.Key == manga.Key);
                manga.Authors = await one.SelectMany(m => m.Authors).ToListAsync(CancellationToken);
                manga.MangaTags = await one.SelectMany(m => m.MangaTags).ToListAsync(CancellationToken);
                manga.Links = await one.SelectMany(m => m.Links).ToListAsync(CancellationToken);
                manga.AltTitles = await one.SelectMany(m => m.AltTitles).ToListAsync(CancellationToken);
            }
        }
        catch (Exception e)
        {
            Log.Warn($"Couldn't read the details of {manga} for ComicInfo.xml ({e.Message}); writing it without them.");
            // Empty (not missing) so ComicInfo.xml and file naming still work; adds nothing to the database
            manga.Authors ??= [];
            manga.MangaTags ??= [];
            manga.Links ??= [];
            manga.AltTitles ??= [];
        }
    }

    /// <summary>Tries per page before the chapter counts as failed</summary>
    private const int PageAttempts = 3;

    /// <summary>Why this download failed (set before giving up on it), for <see cref="DownloadFailures"/></summary>
    private string? _failure;
    private bool _succeeded;
    private MangaConnectorId<Chapter>? _chapterId;

    protected override async Task<BaseWorker[]> DoWorkInternal()
    {
        try
        {
            BaseWorker[] next = await Download();
            if (_succeeded && _chapterId is not null)
                DownloadFailures.RecordSuccess(_chapterId.ObjId);
            else if (_failure is not null)
                await RecordFailure(_failure);
            return next;
        }
        catch (Exception e)
        {
            // Stopped by the user: not a failure. Otherwise (timeout, error) count it.
            if (State != WorkerExecutionState.Cancelled)
                await RecordFailure(e is OperationCanceledException
                    ? $"Took longer than {Constants.WorkerTimeout.TotalMinutes:0} minutes and was stopped"
                    : $"Error: {e.Message}");
            Log.Error(e);
            return [];
        }
    }

    private async Task RecordFailure(string reason)
    {
        if (_chapterId?.Obj is not { } chapter)
            return;
        try
        {
            DownloadFailures.Entry entry = DownloadFailures.RecordFailure(_chapterId, chapter, reason);
            if (!entry.GaveUp)
            {
                Log.Warn($"{chapter.ParentManga?.Name} Ch. {chapter.ChapterNumber} from {_chapterId.MangaConnectorName} failed " +
                         $"(attempt {entry.Attempts} of {DownloadFailures.MaxAttempts}): {reason}. Next try {entry.NextRetryUtc:HH:mm} UTC");
                return;
            }
            string? other = await DownloadFailures.UseOtherWebsite(MangaContext, _chapterId, CancellationToken.None);
            Log.Warn($"{chapter.ParentManga?.Name} Ch. {chapter.ChapterNumber} from {_chapterId.MangaConnectorName} failed " +
                     $"{DownloadFailures.MaxAttempts} times: {reason}. " + (other is null ? "Gave up." : $"Downloading it from {other} instead."));
            if (other is null && DownloadFailures.NotifyWhenGivingUp)
            {
                await NotificationsContext.Notifications.AddAsync(new Notification(
                    "Chapter download failed",
                    $"{chapter.ParentManga?.Name} Ch. {chapter.ChapterNumber} ({_chapterId.MangaConnectorName}): {reason}. " +
                    "Retry it from Activity → Download queue.",
                    NotificationUrgency.High), CancellationToken.None);
                await NotificationsContext.Sync(CancellationToken.None, GetType(), "Download failed");
            }
        }
        catch (Exception e)
        {
            Log.Warn($"Couldn't record the failed download: {e.Message}");
        }
    }

    private async Task<BaseWorker[]> Download()
    {
        Log.Debug($"Downloading chapter for MangaConnectorId {ChapterIdId}...");
        // Getting MangaConnector info
        if (await MangaContext.MangaConnectorToChapter
                .Include(id => id.Obj)
                .ThenInclude(c => c.ParentManga)
                .ThenInclude(m => m.Library)
                .FirstOrDefaultAsync(c => c.Key == ChapterIdId, CancellationToken) is not { } mangaConnectorId)
        {
            Log.Error("Could not get MangaConnectorId.");
            return [];
        }
        
        // A copy of this download that waited in the queue while another one failed: wait for the next try instead
        if (DownloadFailures.IsWaiting(ChapterIdId, DateTime.UtcNow))
        {
            Log.Debug($"{mangaConnectorId} is waiting for its next try; not downloading it now.");
            return [];
        }

        // Check if Chapter already exists...
        if (await mangaConnectorId.Obj.CheckDownloaded(MangaContext, CancellationToken))
        {
            Log.Warn("Chapter already exists!");
            return [];
        }
        
        if (!Tranga.TryGetMangaConnector(mangaConnectorId.MangaConnectorName, out MangaConnector? mangaConnector))
        {
            Log.Error("Could not get MangaConnector.");
            return [];
        }
        
        Log.Debug($"Downloading chapter for MangaConnectorId {mangaConnectorId}...");
        
        Chapter chapter = mangaConnectorId.Obj;
        _chapterId = mangaConnectorId;
        if (chapter.ParentManga.LibraryId is null)
        {
            Log.Info($"Library is not set for {chapter.ParentManga} {chapter}");
            return [];
        }
        
        Log.Info($"Getting imageUrls for chapter {chapter}");
        if (ProgressReporter is not null)
            await ProgressReporter.ReportPhaseChanged(this, chapter.Key, chapter.ParentMangaId,
                chapter.ParentManga.Name, chapter.ChapterNumber, DownloadPhase.FetchingUrls);

        string[] imageUrls = mangaConnector.GetChapterImageUrls(mangaConnectorId);
        if (imageUrls.Length < 1)
        {
            Log.Info($"No imageUrls for chapter {chapter}");
            _failure = "No pages found (the website may have removed the chapter, changed its layout, or blocked Tranga)";
            return [];
        }

        if (chapter.FullArchiveFilePath is not { } saveArchiveFilePath)
        {
            string reason;
            try
            {
                reason = $"no folder for {chapter.ParentManga.FullDirectoryPath}";
            }
            catch (UnauthorizedAccessException e)
            {
                reason = $"{e.Message} Tranga's user can't write to the manga folder; give it ownership (Unraid: chown -R 99:100 /mnt/user/Manga).";
            }
            catch (Exception e)
            {
                reason = $"{e.GetType().Name}: {e.Message}";
            }
            Log.Error($"Failed getting saveArchiveFilePath for {chapter.ParentManga} {chapter}: {reason}");
            _failure = $"Couldn't make the series folder: {reason}";
            return [];
        }
        Log.Debug($"Chapter path: {saveArchiveFilePath}");
        
        //Check if Publication Directory already exists
        string? directoryPath = Path.GetDirectoryName(saveArchiveFilePath);
        if (directoryPath is null)
        {
            Log.Error($"Directory path could not be found: {saveArchiveFilePath}");
            this.Fail();
            return [];
        }
        if (!Directory.Exists(directoryPath))
        {
            Log.Info($"Creating publication Directory: {directoryPath}");
            Directory.CreateDirectory(directoryPath);
        }

        Log.Info($"Downloading images: {chapter}");
        // Each page is saved as soon as it arrives, so a retry only fetches the missing ones
        int pageCount = imageUrls.Length;
        string pagesDir = PartialDownloads.Prepare(chapter.Key, mangaConnectorId.MangaConnectorName, pageCount);
        int done = Enumerable.Range(0, pageCount).Count(i => PartialDownloads.HasPage(PartialDownloads.PagePath(pagesDir, i)));
        if (done > 0)
            Log.Info($"{done} of {pageCount} pages of {chapter} were downloaded earlier; fetching the rest");
        ConcurrentDictionary<int, string> failedPages = new();
        using SemaphoreSlim slots = new(Math.Clamp(Tranga.Settings.PagesAtOnce, 1, 8));
        using SemaphoreSlim progressGate = new(1, 1);

        async Task DownloadPage(int index)
        {
            string path = PartialDownloads.PagePath(pagesDir, index);
            if (PartialDownloads.HasPage(path))
                return;
            await slots.WaitAsync(CancellationToken);
            try
            {
                string error = "";
                for (int attempt = 1; attempt <= PageAttempts; attempt++)
                {
                    if (attempt > 1)
                        await Task.Delay(TimeSpan.FromSeconds(3 * (attempt - 1)), CancellationToken); // 3 s, then 6 s
                    try
                    {
                        if (await mangaConnector.DownloadImage(imageUrls[index], CancellationToken) is not { } stream)
                        {
                            error = "the website didn't send the image";
                            continue;
                        }
                        await using (stream)
                        {
                            Stream processed = await ProcessImage(stream, CancellationToken);
                            await using (processed)
                            {
                                string part = path + ".part";
                                await using (FileStream file = File.Create(part))
                                {
                                    processed.Position = 0;
                                    await processed.CopyToAsync(file, CancellationToken);
                                }
                                File.Move(part, path, true);
                            }
                        }
                        int now = Interlocked.Increment(ref done);
                        if (ProgressReporter is not null)
                        {
                            await progressGate.WaitAsync(CancellationToken);
                            try
                            {
                                await ProgressReporter.ReportProgress(this, chapter.Key, chapter.ParentMangaId,
                                    chapter.ParentManga.Name, chapter.ChapterNumber, now, pageCount, DownloadPhase.DownloadingImages);
                            }
                            finally
                            {
                                progressGate.Release();
                            }
                        }
                        return;
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        error = e.Message;
                    }
                    Log.Debug($"Page {index + 1} of {chapter}: try {attempt} of {PageAttempts} failed ({error})");
                }
                failedPages[index] = error;
                Log.Error($"Failed to download page {index + 1} of {chapter}: {imageUrls[index]} ({error})");
            }
            finally
            {
                slots.Release();
            }
        }

        await Task.WhenAll(Enumerable.Range(0, pageCount).Select(DownloadPage));
        if (!failedPages.IsEmpty)
        {
            (int firstPage, string firstError) = failedPages.OrderBy(f => f.Key).Select(f => (f.Key, f.Value)).First();
            _failure = failedPages.Count == 1
                ? $"Page {firstPage + 1} of {pageCount} couldn't be downloaded after {PageAttempts} tries ({firstError})"
                : $"{failedPages.Count} of {pageCount} pages couldn't be downloaded after {PageAttempts} tries each " +
                  $"(first: page {firstPage + 1}, {firstError})";
            if (pageCount - failedPages.Count > 0)
                _failure += $". The {pageCount - failedPages.Count} pages that worked are kept for the next try";
            if (ProgressReporter is not null)
                await ProgressReporter.ReportFailed(this, chapter.Key, chapter.ParentMangaId,
                    chapter.ParentManga.Name, chapter.ChapterNumber);
            return [];
        }
        
        await CopyCoverFromCacheToDownloadLocation(chapter.ParentManga);
        
        Log.Debug($"Loading collections {chapter}");
        await LoadMangaDetails(chapter.ParentManga);

        if (File.Exists(saveArchiveFilePath))
        {
            Log.Info($"Archive {saveArchiveFilePath} already existed, overwriting.");
            File.Delete(saveArchiveFilePath);
        }

        //Create cbz archive
        if (ProgressReporter is not null)
            await ProgressReporter.ReportPhaseChanged(this, chapter.Key, chapter.ParentMangaId,
                chapter.ParentManga.Name, chapter.ChapterNumber, DownloadPhase.PackagingArchive);
        try
        {
            Log.Debug($"Creating archive: {saveArchiveFilePath}");
            //ZIP-it and ship-it
            using ZipArchive archive = ZipFile.Open(saveArchiveFilePath, ZipArchiveMode.Create);

            if (Constants.CreateComicInfoXml)
            {
                Log.Debug("Writing ComicInfo.xml");
                Stream comicStream = archive.CreateEntry("ComicInfo.xml").Open();
                string comicInfo = await BuildComicInfo(chapter, pageCount, mangaConnectorId.MangaConnectorName);
                await comicStream.WriteAsync(Encoding.UTF8.GetBytes(comicInfo), CancellationToken);
                await comicStream.DisposeAsync();
            }
            else
                Log.Debug("Skipping ComicInfo.xml. CREATE_COMICINFO_XML is set to false");
            
            for (int i = 0; i < pageCount; i++)
            {
                Log.Debug($"Packaging images to archive {chapter} , image {i}");
                Stream zipStream = archive.CreateEntry($"{i}.jpg").Open();
                await using (FileStream page = File.OpenRead(PartialDownloads.PagePath(pagesDir, i)))
                    await page.CopyToAsync(zipStream, CancellationToken);
                await zipStream.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            if (ex is OperationCanceledException)
                throw;
            _failure = $"Couldn't save the chapter file: {ex.Message}";
        }
        if (_failure is not null)
        {
            try
            {
                if (File.Exists(saveArchiveFilePath))
                    File.Delete(saveArchiveFilePath); // don't leave a half-written archive
            }
            catch (Exception e)
            {
                Log.Warn($"Couldn't remove {saveArchiveFilePath}: {e.Message}");
            }
            return [];
        }

        chapter.Downloaded = true;
        _succeeded = true;
        PartialDownloads.Delete(chapter.Key);
        chapter.FileName = new FileInfo(saveArchiveFilePath).Name;
        if(await MangaContext.Sync(CancellationToken, GetType(), "Downloading complete") is { success: false } chapterContextException)
            Log.Error($"Failed to save database changes: {chapterContextException.exceptionMessage}");
        
        Log.Debug($"Downloaded chapter {chapter}.");

        if (ProgressReporter is not null)
            await ProgressReporter.ReportCompleted(this, chapter.Key, chapter.ParentMangaId,
                chapter.ParentManga.Name, chapter.ChapterNumber, imageUrls.Length);

        await ActionsContext.Actions.AddAsync(new ChapterDownloadedActionRecord(chapter.ParentManga, chapter));
        if(await ActionsContext.Sync(CancellationToken, GetType(), "Download complete") is { success: false } actionsContextException)
            Log.Error($"Failed to save database changes: {actionsContextException.exceptionMessage}");

        await NotificationsContext.Notifications.AddAsync(new Notification(
            "Chapter downloaded",
            $"{chapter.ParentManga.Name} Ch. {chapter.ChapterNumber} - {chapter.FileName}"
            ), CancellationToken);
        if(await NotificationsContext.Sync(CancellationToken, GetType(), "Download complete") is { success: false } notificationsContextException)
            Log.Error($"Failed to save database changes: {notificationsContextException.exceptionMessage}");

        bool refreshLibrary = await CheckLibraryRefresh();
        if(refreshLibrary)
            Log.Info($"Condition {Tranga.Settings.LibraryRefreshSetting} met.");

        return refreshLibrary? [new RefreshLibrariesWorker()] : [];
    }

    private async Task<bool> CheckLibraryRefresh() => Tranga.Settings.LibraryRefreshSetting switch
    {
        LibraryRefreshSetting.AfterAllFinished => await AllDownloadsFinished(),
        LibraryRefreshSetting.AfterMangaFinished => await MangaContext.MangaConnectorToChapter.Include(chId => chId.Obj).Where(chId => chId.UseForDownload).AllAsync(chId => chId.Obj.Downloaded, CancellationToken),
        LibraryRefreshSetting.AfterEveryChapter => true,
        LibraryRefreshSetting.WhileDownloading => await AllDownloadsFinished() ||  DateTime.UtcNow.Subtract(RefreshLibrariesWorker.LastRefresh).TotalMinutes > Tranga.Settings.RefreshLibraryWhileDownloadingEveryMinutes,
        _ => true
    };
    private async Task<bool> AllDownloadsFinished() => (await StartNewChapterDownloadsWorker.GetMissingChapters(MangaContext, CancellationToken)).Count == 0;
    
    private async Task<Stream> ProcessImage(Stream imageStream, CancellationToken? cancellationToken = null)
    {
        Log.Debug("Processing image");
        imageStream.Position = 0;
        if (!Tranga.Settings.BlackWhiteImages && Tranga.Settings.ImageCompression == 100)
        {
            Log.Debug("No processing requested for image");
            return imageStream;
        }

        MemoryStream processedImage = new ();
        try
        {
            using Image image = await Image.LoadAsync(imageStream, cancellationToken ?? CancellationToken.None);
            Log.Debug("Image loaded");
            if (Tranga.Settings.BlackWhiteImages)
                image.Mutate(i => i.ApplyProcessor(new AdaptiveThresholdProcessor()));
            await image.SaveAsJpegAsync(processedImage, new JpegEncoder()
            {
                Quality = Tranga.Settings.ImageCompression
            });
            Log.Debug("Image processed");
            processedImage.Position = 0;
            return processedImage;
        }
        catch (Exception e)
        {
            if (e is UnknownImageFormatException or NotSupportedException)
            {
                //If the Image-Format is not processable by ImageSharp, we can't modify it.
                Log.Debug("Unable to process image: Not supported image format");
            }else if (e is InvalidImageContentException)
            {
                Log.Debug("Unable to process image: Invalid Content");
            }
            else
            {
                Log.Error(e);
            }
            await imageStream.CopyToAsync(processedImage);
            processedImage.Position = 0;
            return processedImage;
        }
    }
    
    private async Task CopyCoverFromCacheToDownloadLocation(Manga manga)
    {
        Log.Debug($"Copying cover for {manga}");

        manga = await MangaContext.MangaWithMetadata().Include(m => m.MangaConnectorIds).FirstAsync(m => m.Key == manga.Key, CancellationToken);
        string publicationFolder;
        try
        {
            Log.Debug("Checking Manga directory exists...");
            //Check if Publication already has a Folder and cover
            publicationFolder = manga.FullDirectoryPath;

            Log.Debug("Checking cover already exists...");
            DirectoryInfo dirInfo = new(publicationFolder);
            if (dirInfo.EnumerateFiles()
                .Any(info => info.Name.Contains("cover", StringComparison.InvariantCultureIgnoreCase)))
            {
                Log.Debug($"Cover already exists at {publicationFolder}");
                return;
            }
        }
        catch (Exception e)
        {
            Log.Error(e);
            return;
        }

        if (manga.CoverFileNameInCache is not { } coverFileNameInCache)
        {
            MangaConnectorId<Manga> mangaConnectorId = manga.MangaConnectorIds.First();
            if (!Tranga.TryGetMangaConnector(mangaConnectorId.MangaConnectorName, out MangaConnector? mangaConnector))
            {
                Log.Error($"MangaConnector with name {mangaConnectorId.MangaConnectorName} could not be found");
                return;
            }
            
            coverFileNameInCache = mangaConnector.SaveCoverImageToCache(mangaConnectorId);
            manga.CoverFileNameInCache = coverFileNameInCache;
            if (await MangaContext.Sync(CancellationToken, reason: "Update cover filename") is { success: false } result)
                Log.Error($"Couldn't update cover filename {result.exceptionMessage}");
        }
        if (coverFileNameInCache is null)
        {
            Log.Error($"File {coverFileNameInCache} does not exist and failed to download cover");
            return;
        }
        
        string fullCoverPath = Path.Join(TrangaSettings.CoverImageCacheOriginal, coverFileNameInCache);
        string newFilePath = Path.Join(publicationFolder, $"cover.{Path.GetFileName(coverFileNameInCache).Split('.')[^1]}" );
        File.Copy(fullCoverPath, newFilePath, true);
        Log.Debug($"Copied cover from {fullCoverPath} to {newFilePath}");
    }

    public override string ToString() => $"{base.ToString()} {ChapterIdId}";
}