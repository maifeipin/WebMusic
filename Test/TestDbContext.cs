using Microsoft.EntityFrameworkCore;
using System.IO;

namespace SmbDiag
{
    public class TestDbContext : DbContext
    {
        public DbSet<ScanSource> ScanSources { get; set; }
        public DbSet<StorageCredential> StorageCredentials { get; set; }
        public DbSet<MediaFile> MediaFiles { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            // Point to the backend DB
            var dbPath = Path.Combine(Directory.GetCurrentDirectory(), "../backend/webmusic.db");
            optionsBuilder.UseSqlite($"Data Source={dbPath}");
        }
    }
}
