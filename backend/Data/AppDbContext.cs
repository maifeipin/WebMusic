using Microsoft.EntityFrameworkCore;
using WebMusic.Backend.Models;

namespace WebMusic.Backend.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users { get; set; }
    public DbSet<ScanSource> ScanSources { get; set; }
    public DbSet<MediaFile> MediaFiles { get; set; }
    public DbSet<StorageCredential> StorageCredentials { get; set; }
    public DbSet<PlayHistory> PlayHistories { get; set; }
    public DbSet<Favorite> Favorites { get; set; }
    public DbSet<Lyric> Lyrics { get; set; }
    public DbSet<MusicEnrichment> MusicEnrichments { get; set; }
    public DbSet<Playlist> Playlists { get; set; }
    public DbSet<PlaylistSong> PlaylistSongs { get; set; }
    public DbSet<PluginDefinition> Plugins { get; set; }
    public DbSet<EnrichmentJob> EnrichmentJobs { get; set; }
    public DbSet<EnrichmentAttempt> EnrichmentAttempts { get; set; }
    public DbSet<MediaIdentity> MediaIdentities { get; set; }
    public DbSet<MediaTag> MediaTags { get; set; }
    public DbSet<TagEvidence> TagEvidences { get; set; }
    public DbSet<EnrichmentJobItem> EnrichmentJobItems { get; set; }
    public DbSet<ProviderQuotaLedger> ProviderQuotaLedgers { get; set; }
    public DbSet<WorkerSubmission> WorkerSubmissions { get; set; }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Properties<DateTime>().HaveColumnType("timestamp with time zone");
        configurationBuilder.Properties<DateTime?>().HaveColumnType("timestamp with time zone");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        modelBuilder.Entity<Lyric>()
            .Property(l => l.CreatedAt)
            .HasColumnType("timestamp without time zone");

        modelBuilder.Entity<PluginDefinition>()
            .Property(p => p.CreatedAt)
            .HasColumnType("timestamp without time zone");

        modelBuilder.Entity<User>().HasIndex(u => u.Username).IsUnique();
        
        modelBuilder.Entity<MediaFile>()
            .HasIndex(m => m.FilePath).IsUnique();
            
        modelBuilder.Entity<MediaFile>()
            .HasIndex(m => m.Artist);
            
        modelBuilder.Entity<MediaFile>()
            .HasIndex(m => m.Album);

        modelBuilder.Entity<MusicEnrichment>()
            .HasIndex(e => new { e.MediaFileId, e.CreatedAt });

        modelBuilder.Entity<EnrichmentJob>()
            .HasIndex(j => j.Status);

        modelBuilder.Entity<EnrichmentJobItem>()
            .HasIndex(i => new { i.JobId, i.Status });

        modelBuilder.Entity<EnrichmentJobItem>()
            .HasIndex(i => i.MediaFileId);

        modelBuilder.Entity<EnrichmentAttempt>()
            .HasIndex(a => new { a.JobId, a.CreatedAt });

        modelBuilder.Entity<MediaIdentity>()
            .HasIndex(i => new { i.Provider, i.RecordingId });

        modelBuilder.Entity<MediaIdentity>()
            .HasIndex(i => new { i.MediaFileId, i.Provider })
            .IsUnique();

        modelBuilder.Entity<MediaTag>()
            .HasIndex(t => new { t.MediaFileId, t.Namespace, t.Key })
            .IsUnique();

        modelBuilder.Entity<TagEvidence>()
            .HasIndex(e => e.MediaTagId);

        modelBuilder.Entity<ProviderQuotaLedger>()
            .HasKey(p => new { p.Provider, p.Date });

        modelBuilder.Entity<WorkerSubmission>()
            .HasIndex(w => new { w.ItemId, w.SubmissionId })
            .IsUnique();

        modelBuilder.Entity<WorkerSubmission>()
            .HasOne(w => w.Item)
            .WithMany()
            .HasForeignKey(w => w.ItemId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

