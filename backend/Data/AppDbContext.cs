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
    public DbSet<Playlist> Playlists { get; set; }
    public DbSet<PlaylistSong> PlaylistSongs { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        modelBuilder.Entity<User>().HasIndex(u => u.Username).IsUnique();
        
        modelBuilder.Entity<MediaFile>()
            .HasIndex(m => m.FilePath).IsUnique();
            
        modelBuilder.Entity<MediaFile>()
            .HasIndex(m => m.Artist);
            
        modelBuilder.Entity<MediaFile>()
            .HasIndex(m => m.Album);
    }
}
