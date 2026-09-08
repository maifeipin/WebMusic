using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace WebMusic.Backend.Models;

public class IdentityImportItem
{
    [Key]
    public int Id { get; set; }

    public int BatchId { get; set; }

    [JsonIgnore]
    public IdentityImportBatch? Batch { get; set; }

    public int MediaFileId { get; set; }

    [Required]
    [MaxLength(512)]
    public string TargetTitle { get; set; } = string.Empty;

    [Required]
    [MaxLength(512)]
    public string TargetArtist { get; set; } = string.Empty;

    [MaxLength(512)]
    public string? TargetAlbum { get; set; }

    public double TargetDurationSeconds { get; set; }

    [Required]
    [MaxLength(128)]
    public string MetadataFingerprint { get; set; } = string.Empty;

    [Required]
    [MaxLength(128)]
    public string MatchedRecordingId { get; set; } = string.Empty;

    [MaxLength(128)]
    public string? MatchedReleaseId { get; set; }

    [MaxLength(128)]
    public string? MatchedArtistId { get; set; }

    [Required]
    [MaxLength(512)]
    public string MatchedTitle { get; set; } = string.Empty;

    [Required]
    [MaxLength(512)]
    public string MatchedArtist { get; set; } = string.Empty;

    public double? MatchedDurationSeconds { get; set; }

    public double Confidence { get; set; }

    public string? PreSnapshotJson { get; set; }

    public int? CreatedIdentityId { get; set; }

    [Required]
    [MaxLength(32)]
    public string Status { get; set; } = "Pending"; // Pending, Applied, RolledBack, Failed

    public string? ErrorDetail { get; set; }
}
