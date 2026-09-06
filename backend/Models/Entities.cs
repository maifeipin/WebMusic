using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace WebMusic.Backend.Models;

public class User
{
    [Key]
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public bool IsAdmin { get; set; }
    public string? Role { get; set; } = "User";
}

public class StorageCredential
{
    [Key]
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty; // Friendly name
    public string ProviderType { get; set; } = "SMB"; // SMB, WANPAN, GDRIVE
    public string Host { get; set; } = string.Empty; // IP or Domain
    public string AuthData { get; set; } = "{}"; // JSON blob

    public int? UserId { get; set; }
    [JsonIgnore]
    public User? User { get; set; }
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

    public int? UserId { get; set; }
    [JsonIgnore]
    public User? User { get; set; }
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

    [JsonIgnore]
    public List<Favorite> Favorites { get; set; } = new();
    [JsonIgnore]
    public List<PlaylistSong> PlaylistSongs { get; set; } = new();
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

/// <summary>
/// An immutable audit trail for automatic metadata enrichment.  It deliberately
/// records the external source and match score instead of silently overwriting
/// user-curated data.
/// </summary>
public class MusicEnrichment
{
    [Key]
    public int Id { get; set; }
    public int MediaFileId { get; set; }
    [JsonIgnore]
    public MediaFile? MediaFile { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string? ExternalId { get; set; }
    public double Confidence { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class EnrichmentJob
{
    [Key]
    public string Id { get; set; } = string.Empty; // BatchId
    public string Scope { get; set; } = "Favorites";
    public int? RequestedByUserId { get; set; }
    public int Total { get; set; }
    public int Processed { get; set; }
    public int Updated { get; set; }
    public int Unmatched { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public int Cursor { get; set; } // Batch in-flight progress index (0..Total)
    public int? CatalogBookmarkId { get; set; } // Bookmark MediaFileId for catalog scans
    public string Status { get; set; } = "Queued"; // Queued, Processing, Completed, Failed, Paused
    public string SongIdsJson { get; set; } = "[]"; // Serialized song IDs for restart recovery
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
}

public class EnrichmentJobItem
{
    [Key]
    public int Id { get; set; }
    public string JobId { get; set; } = string.Empty;
    public int MediaFileId { get; set; }
    [JsonIgnore]
    public MediaFile? MediaFile { get; set; }
    public string Status { get; set; } = "Pending"; // Pending, Leased, Completed, Failed, Skipped
    public string? InputFingerprint { get; set; }
    public string CoverStatus { get; set; } = "Pending"; // Pending, Matched, NoAsset, Failed
    public string LyricsStatus { get; set; } = "Pending"; // Pending, Matched, NoAsset, Failed
    public string? WorkerNodeId { get; set; }
    public DateTime? LeasedAt { get; set; }
    public DateTime? LeaseExpiresAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Outcome { get; set; }
    public string? Detail { get; set; }
    public string? StagedCoverPath { get; set; }
}

public class EnrichmentAttempt
{
    [Key]
    public int Id { get; set; }
    public string JobId { get; set; } = string.Empty;
    public int MediaFileId { get; set; }
    [JsonIgnore]
    public MediaFile? MediaFile { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string? RequestKey { get; set; }
    public string? InputFingerprint { get; set; }
    public int? HTTPStatus { get; set; }
    public string Outcome { get; set; } = string.Empty; // Updated, Unmatched, Skipped, Failed
    public double Confidence { get; set; }
    public int RetryCount { get; set; }
    public string Detail { get; set; } = string.Empty;
    public DateTime? RetryAfter { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class MediaIdentity
{
    [Key]
    public int Id { get; set; }
    public int MediaFileId { get; set; }
    [JsonIgnore]
    public MediaFile? MediaFile { get; set; }
    public string Provider { get; set; } = "MusicBrainz";
    public string? RecordingId { get; set; }
    public string? ReleaseId { get; set; }
    public string? ArtistId { get; set; }
    public string? ISRC { get; set; }
    public string? AcoustId { get; set; }
    public string MatchMethod { get; set; } = "MetadataFuzzy";
    public double Confidence { get; set; }
    public string Status { get; set; } = "approved"; // approved, proposed, rejected
    public string CoverStatus { get; set; } = "Pending"; // Pending, Matched, NoAsset, Failed
    public string LyricsStatus { get; set; } = "Pending"; // Pending, Matched, NoAsset, Failed
    public DateTime MatchedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastVerifiedAt { get; set; }
}

public class MediaTag
{
    [Key]
    public int Id { get; set; }
    public int MediaFileId { get; set; }
    [JsonIgnore]
    public MediaFile? MediaFile { get; set; }
    public string Namespace { get; set; } = string.Empty; // chart / popularity / soundtrack / cultural / genre
    public string Key { get; set; } = string.Empty;       // peak / listeners / classic 等
    public string Value { get; set; } = string.Empty;
    public double? NumericValue { get; set; }
    public double Confidence { get; set; } = 1.0;
    public string Status { get; set; } = "approved";      // proposed / approved / rejected
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    [JsonIgnore]
    public List<TagEvidence> Evidences { get; set; } = new();
}

public class TagEvidence
{
    [Key]
    public int Id { get; set; }
    public int MediaTagId { get; set; }
    [JsonIgnore]
    public MediaTag? MediaTag { get; set; }
    public string Source { get; set; } = string.Empty;     // musicbrainz / billboard / spotify / lastfm / tmdb
    public string? SourceId { get; set; }
    public string? EvidenceUrl { get; set; }
    public string? EvidenceText { get; set; }
    public DateTime RetrievedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public string? RawPayload { get; set; }                // JSON string
}

public class ProviderQuotaLedger
{
    public string Provider { get; set; } = "MusicBrainz";
    public string Date { get; set; } = string.Empty; // "YYYY-MM-DD"
    public int DailyLimit { get; set; } = 2000;
    public int ReservedUnits { get; set; }
    public int ConsumedUnits { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class WorkerSubmission
{
    [Key]
    public int Id { get; set; }
    public int ItemId { get; set; }
    [JsonIgnore]
    public EnrichmentJobItem? Item { get; set; }
    public string SubmissionId { get; set; } = string.Empty;
    public string PayloadHash { get; set; } = string.Empty;
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
}

