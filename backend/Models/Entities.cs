using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace WebMusic.Backend.Models;

public class User
{
    [Key]
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
}

public class StorageCredential
{
    [Key]
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty; // Friendly name
    public string ProviderType { get; set; } = "SMB"; // SMB, WANPAN, GDRIVE
    public string Host { get; set; } = string.Empty; // IP or Domain
    public string AuthData { get; set; } = "{}"; // JSON blob
}

public class ScanSource
{
    [Key]
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty; // SMB path like smb://server/share
    public string Type { get; set; } = "SMB";
    
    // Credentials
    public int? StorageCredentialId { get; set; }
    public StorageCredential? StorageCredential { get; set; }
}

public class MediaFile
{
    [Key]
    public int Id { get; set; }
    public string FilePath { get; set; } = string.Empty; // Full SMB path
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public int Year { get; set; }
    public TimeSpan Duration { get; set; }
    public string? CoverArt { get; set; } // Custom cover art path (SMB path)
    public long SizeBytes { get; set; }
    public string FileHash { get; set; } = string.Empty; // For deduplication (e.g. partial MD5)
    public string ParentPath { get; set; } = string.Empty; // For tree view
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    
    // Foreign Key to Source? Maybe not strictly needed if path contains it, but useful
    public int ScanSourceId { get; set; }
    public ScanSource? ScanSource { get; set; }
}

public class PlayHistory
{
    [Key]
    public int Id { get; set; }
    public int UserId { get; set; }
    [JsonIgnore]
    public User? User { get; set; }
    public int MediaFileId { get; set; }
    public MediaFile? MediaFile { get; set; }
    public DateTime PlayedAt { get; set; } = DateTime.UtcNow;
}

public class Favorite
{
    [Key]
    public int Id { get; set; }
    public int UserId { get; set; }
    [JsonIgnore]
    public User? User { get; set; }
    public int MediaFileId { get; set; }
    public MediaFile? MediaFile { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Playlist
{
    [Key]
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int UserId { get; set; }
    public string? CoverArt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Playlist type: "normal" for user playlists, "shared" for share-created playlists.
    /// </summary>
    public string Type { get; set; } = "normal";
    
    /// <summary>
    /// Share token for public access. Null means not shared.
    /// </summary>
    public string? ShareToken { get; set; }

    /// <summary>
    /// Expiration time for the share link. Null means never expires (legacy data).
    /// </summary>
    public DateTime? ShareExpiresAt { get; set; }

    /// <summary>
    /// Optional password for accessing the shared playlist.
    /// </summary>
    public string? SharePassword { get; set; }
    
    [JsonIgnore]
    public List<PlaylistSong> PlaylistSongs { get; set; } = new();
}

public class PlaylistSong
{
    [Key]
    public int Id { get; set; }
    public int PlaylistId { get; set; }
    [JsonIgnore]
    public Playlist? Playlist { get; set; }
    
    public int MediaFileId { get; set; }
    public MediaFile? MediaFile { get; set; }
    
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}

public class Lyric
{
    [Key]
    public int Id { get; set; }
    
    // Linking to MediaFile (Use standard int ID)
    public int MediaFileId { get; set; }
    [JsonIgnore]
    public MediaFile? MediaFile { get; set; }

    public string Content { get; set; } = string.Empty; // LRC format preferred
    public string Language { get; set; } = "unknown";
    public string Source { get; set; } = "AI"; // "AI", "Manual", "Gemini"
    public string Version { get; set; } = "v1"; // e.g. "whisper-tiny"
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

