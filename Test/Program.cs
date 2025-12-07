using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;

namespace SmbDiag
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== WebMusic V2 Diagnostic Tool ===");
            
            // Run DB Update
            DbUpdater.UpdateSchema();

            using var db = new TestDbContext();
            // Ensure connection
            try {
                if (!db.Database.CanConnect()) {
                    Console.WriteLine("Cannot connect to database.");
                    return;
                }
            } catch (Exception ex) {
                 Console.WriteLine($"DB Connection Error: {ex.Message}");
                 return;
            }

            var sources = db.ScanSources.Include(s => s.StorageCredential).ToList();
            Console.WriteLine($"Found {sources.Count} sources.");

            var smbTester = new SmbScannerTest();

            foreach (var source in sources)
            {
                if (source.Type == "SMB" || (source.StorageCredential != null && source.StorageCredential.ProviderType == "SMB"))
                {
                    smbTester.Run(source);
                }
                else
                {
                    Console.WriteLine($"Skipping non-SMB source: {source.Name} ({source.Type})");
                }
            }
        }
    }
}
