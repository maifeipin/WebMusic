
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmbDiag
{
    public class StorageCredential
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string ProviderType { get; set; } = "SMB";
        public string Host { get; set; } = string.Empty;
        public string AuthData { get; set; } = "{}";
    }

    public class ScanSource
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Type { get; set; } = "SMB";
        public int? StorageCredentialId { get; set; }
        public StorageCredential? StorageCredential { get; set; }
    }

    public class MediaFile
    {
        public int Id { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string Album { get; set; } = string.Empty;
        public string Genre { get; set; } = string.Empty;
        public int Year { get; set; }
        public TimeSpan Duration { get; set; }
        public long SizeBytes { get; set; }
        public string FileHash { get; set; } = string.Empty;
        public string ParentPath { get; set; } = string.Empty;
        public DateTime AddedAt { get; set; } = DateTime.UtcNow;
        public int ScanSourceId { get; set; }
    }
}
