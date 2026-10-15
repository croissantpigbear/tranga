using API.MangaConnectors;
using API.Schema.MangaContext.MetadataFetchers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace API.Schema.MangaContext;

public class MangaContext(DbContextOptions<MangaContext> options) : TrangaBaseContext<MangaContext>(options)
{
    public DbSet<Manga> Mangas { get; set; }
    public DbSet<FileLibrary> FileLibraries { get; set; }
    public DbSet<Chapter> Chapters { get; set; }
    public DbSet<Author> Authors { get; set; }
    public DbSet<MangaTag> Tags { get; set; }
    public DbSet<MangaConnectorId<Manga>> MangaConnectorToManga { get; set; }
    public DbSet<MangaConnectorId<Chapter>> MangaConnectorToChapter { get; set; }
    public DbSet<MetadataEntry> MetadataEntries { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        //MangaConnector Types
        modelBuilder.Entity<MangaConnector>()
            .HasDiscriminator(c => c.Name)
            .HasValue<Global>("Global")
            .HasValue<AsuraComic>("AsuraComic")
            .HasValue<MangaDex>("MangaDex")
            .HasValue<Mangaworld>("Mangaworld")
            .HasValue<WeebCentral>("WeebCentral");

        //Manga has many Chapters
        modelBuilder.Entity<Manga>()
            .HasMany<Chapter>(m => m.Chapters)
            .WithOne(c => c.ParentManga)
            .HasForeignKey(c => c.ParentMangaId)
            .OnDelete(DeleteBehavior.Cascade);
        //Chapter has MangaConnectorIds
        modelBuilder.Entity<Chapter>()
            .HasMany<MangaConnectorId<Chapter>>(c => c.MangaConnectorIds)
            .WithOne(id => id.Obj)
            .HasForeignKey(id => id.ObjId)
            .OnDelete(DeleteBehavior.Cascade);
        //Manga owns MangaAltTitles
        modelBuilder.Entity<Manga>()
            .OwnsMany<AltTitle>(m => m.AltTitles)
            .WithOwner();
        modelBuilder.Entity<Manga>()
            .Navigation(m => m.AltTitles)
            .AutoInclude();
        //Manga owns Links
        modelBuilder.Entity<Manga>()
            .OwnsMany<Link>(m => m.Links)
            .WithOwner();
        modelBuilder.Entity<Manga>()
            .Navigation(m => m.Links)
            .AutoInclude();
        //Manga has many Tags associated with many Obj
        modelBuilder.Entity<Manga>()
            .HasMany<MangaTag>(m => m.MangaTags)
            .WithMany()
            .UsingEntity("MangaTagToManga",
                l => l.HasOne(typeof(MangaTag)).WithMany().HasForeignKey("MangaTagIds")
                    .HasPrincipalKey(nameof(MangaTag.Tag)),
                r => r.HasOne(typeof(Manga)).WithMany().HasForeignKey("MangaIds").HasPrincipalKey(nameof(Manga.Key)),
                j => j.HasKey("MangaTagIds", "MangaIds")
            );
        modelBuilder.Entity<Manga>()
            .Navigation(m => m.MangaTags)
            .AutoInclude();
        //Manga has many Authors associated with many Obj
        modelBuilder.Entity<Manga>()
            .HasMany<Author>(m => m.Authors)
            .WithMany()
            .UsingEntity("AuthorToManga",
                l => l.HasOne(typeof(Author)).WithMany().HasForeignKey("AuthorIds").HasPrincipalKey(nameof(Author.Key)),
                r => r.HasOne(typeof(Manga)).WithMany().HasForeignKey("MangaIds").HasPrincipalKey(nameof(Manga.Key)),
                j => j.HasKey("AuthorIds", "MangaIds")
            );
        modelBuilder.Entity<Manga>()
            .Navigation(m => m.Authors)
            .AutoInclude();
        //Manga has many MangaIds
        modelBuilder.Entity<Manga>()
            .HasMany<MangaConnectorId<Manga>>(m => m.MangaConnectorIds)
            .WithOne(id => id.Obj)
            .HasForeignKey(id => id.ObjId)
            .OnDelete(DeleteBehavior.Cascade);


        //FileLibrary has many Mangas
        modelBuilder.Entity<FileLibrary>()
            .HasMany<Manga>()
            .WithOne(m => m.Library)
            .HasForeignKey(m => m.LibraryId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<MetadataFetcher>()
            .HasDiscriminator<string>(nameof(MetadataEntry))
            .HasValue<MyAnimeList>(nameof(MyAnimeList));
        //MetadataEntry
        modelBuilder.Entity<MetadataEntry>()
            .HasOne<Manga>(entry => entry.Manga)
            .WithMany()
            .OnDelete(DeleteBehavior.Cascade);
    }

    /// <summary>
    /// Finds a Manga already in the database that is the same series as <paramref name="other"/>: same name, a shared
    /// alternative title, or a shared link. Two series are never treated as the same when
    /// <list type="bullet">
    /// <item>both are on the same website under different pages (e.g. a series and its separate "[R18]" version), or</item>
    /// <item>their names mark different editions (R18, uncensored, colored, official, novel, …) and they share no link.</item>
    /// </list>
    /// </summary>
    /// <param name="other">Manga being added</param>
    /// <param name="otherId">Its page on the website it was found on, if known</param>
    /// <param name="ct"></param>
    public async Task<string?> FindMangaLike(Manga other, CancellationToken ct, MangaConnectorId<Manga>? otherId = null)
    {
        var mangas = await MangaWithMetadata().Select(m => new
        {
            Id = m.Key,
            m.Name,
            AltTitles = m.AltTitles.Select(a => a.Title).ToList(),
            Links = m.Links.Select(l => l.LinkUrl).ToList(),
            Pages = m.MangaConnectorIds.Select(id => new { id.MangaConnectorName, id.IdOnConnectorSite }).ToList()
        }).ToListAsync(ct);

        // The same page on the same website is always the same series
        if (otherId is not null && mangas.FirstOrDefault(m => m.Pages.Any(p => p.MangaConnectorName == otherId.MangaConnectorName
                                                                             && p.IdOnConnectorSite == otherId.IdOnConnectorSite)) is { } samePage)
            return samePage.Id;

        HashSet<string> otherTags = EditionTags(other.Name);
        foreach (var m in mangas)
        {
            bool sameLink = m.Links.Any(l => other.Links.Any(ol => ol.LinkUrl == l));
            bool sameName = m.Id == other.Key;
            bool sharedAltTitle = m.AltTitles.Any(t => other.AltTitles.Any(ot => ot.Title.Equals(t)));
            if (!sameLink && !sameName && !sharedAltTitle)
                continue;
            // A different page on the same website is a different series
            if (otherId is not null && m.Pages.Any(p => p.MangaConnectorName == otherId.MangaConnectorName
                                                       && p.IdOnConnectorSite != otherId.IdOnConnectorSite))
                continue;
            // "Title" and "Title [R18]" / "Title (Colored)" are different editions, unless they share a link
            if (!sameLink && !EditionTags(m.Name).SetEquals(otherTags))
                continue;
            return m.Id;
        }
        return null;
    }

    private static readonly System.Text.RegularExpressions.Regex EditionRex = new(
        @"\b(r ?-?18|adult|uncensored|censored|colou?r(?:ed)?|full ?colou?r|official|novel|light ?novel|anthology|one ?-?shot|doujin(shi)?|remake|webtoon|spin ?-?off)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Edition markers in a series name ("R18", "Colored", "Official", …), normalized</summary>
    internal static HashSet<string> EditionTags(string name) => EditionRex.Matches(name)
        .Select(m => new string(m.Value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray())
            .Replace("colour", "color").Replace("colored", "color").Replace("doujinshi", "doujin"))
        .ToHashSet();

    public IIncludableQueryable<Manga, ICollection<AltTitle>> MangaWithMetadata() =>
        Mangas
            .Include(m => m.Library)
            .Include(m => m.Authors)
            .Include(m => m.MangaTags)
            .Include(m => m.Links)
            .Include(m => m.AltTitles);

    public IIncludableQueryable<Manga, ICollection<MangaConnectorId<Manga>>> MangaIncludeAll() =>
        MangaWithMetadata()
            .Include(m => m.Chapters)
            .Include(m => m.MangaConnectorIds);
}
