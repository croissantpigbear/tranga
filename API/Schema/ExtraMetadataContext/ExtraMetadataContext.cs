using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace API.Schema.ExtraMetadataContext;

/// <summary>
/// Extra details for a <see cref="MangaContext.Manga"/> gathered once from MangaUpdates and MangaDex
/// (writers, artists, publishers, genres, content rating, ...), stored as JSON.
/// </summary>
public class MangaExtraMetadata
{
    /// <summary><see cref="MangaContext.Manga"/>.Key</summary>
    [StringLength(64)]
    public required string MangaId { get; set; }

    /// <summary>JSON of <see cref="API.ExtraMetadata.ExtraMetadataData"/></summary>
    public required string Data { get; set; }

    /// <summary>When the details were fetched (UTC)</summary>
    public DateTime FetchedAt { get; set; }
}

/// <summary>
/// Separate, single-table context so the extra details don't touch the main Manga schema or its migrations.
/// Rows of deleted Manga are removed by <see cref="API.Workers.PeriodicWorkers.FillExtraMetadataWorker"/>.
/// </summary>
public class ExtraMetadataContext(DbContextOptions<ExtraMetadataContext> options) : TrangaBaseContext<ExtraMetadataContext>(options)
{
    public DbSet<MangaExtraMetadata> Entries { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        // The migration for this context is small and hand-maintained; don't refuse to migrate over cosmetic model differences.
        optionsBuilder.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MangaExtraMetadata>(b =>
        {
            b.ToTable("MangaExtraMetadata");
            b.HasKey(e => e.MangaId);
            b.Property(e => e.MangaId).HasMaxLength(64);
            b.Property(e => e.Data).HasColumnType("jsonb");
        });
    }
}
