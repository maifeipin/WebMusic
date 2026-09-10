using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace WebMusic.Backend.Models;

/// <summary>
/// Durable progress for the local MusicBrainz identity scanner. It remains
/// separate from MediaIdentity so rejected/retryable outcomes are auditable;
/// only an explicit v3 auto-apply run may create an approved identity.
/// </summary>
public class MediaIdentityScanState
{
    [Key]
    public int Id { get; set; }

    public int MediaFileId { get; set; }

    [JsonIgnore]
    public MediaFile? MediaFile { get; set; }

    [Required]
    [MaxLength(64)]
    public string InputFingerprint { get; set; } = string.Empty;

    [Required]
    [MaxLength(48)]
    public string Outcome { get; set; } = "Pending"; // Matched, Unmatched, Skipped, Failed

    public double? Confidence { get; set; }

    [MaxLength(64)]
    public string? RecordingId { get; set; }

    [MaxLength(64)]
    public string PolicyVersion { get; set; } = "LocalAutoEligibility:v3";

    [MaxLength(128)]
    public string? MirrorVersion { get; set; }

    [MaxLength(128)]
    public string? LastReportSha256 { get; set; }

    public int AttemptCount { get; set; }
    public DateTime LastScannedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RetryAfter { get; set; }

    [MaxLength(1024)]
    public string? LastError { get; set; }
}

/// <summary>
/// Provider-neutral external identity.  One media file may have references to
/// several providers and subject types (for example a MusicBrainz recording and
/// a Last.fm track) without changing the MediaIdentity approval workflow.
/// </summary>
public class MediaExternalReference
{
    [Key]
    public int Id { get; set; }

    public int MediaFileId { get; set; }

    [JsonIgnore]
    public MediaFile? MediaFile { get; set; }

    [Required]
    [MaxLength(64)]
    public string Provider { get; set; } = string.Empty;

    [Required]
    [MaxLength(32)]
    public string SubjectType { get; set; } = string.Empty; // Recording, ReleaseGroup, Track

    [Required]
    [MaxLength(128)]
    public string ExternalId { get; set; } = string.Empty;

    [MaxLength(1024)]
    public string? CanonicalUrl { get; set; }

    [Required]
    [MaxLength(64)]
    public string MatchMethod { get; set; } = string.Empty;

    public double? MatchConfidence { get; set; }

    [Required]
    [MaxLength(32)]
    public string Status { get; set; } = "approved";

    [MaxLength(64)]
    public string? MetadataFingerprint { get; set; }

    public string? MetadataJson { get; set; }
    public DateTime VerifiedAt { get; set; } = DateTime.UtcNow;

    public List<MediaExternalSignal> Signals { get; set; } = new();
}

/// <summary>
/// Time-stamped provider data.  Values are public/provider-derived only; this
/// table must never contain favourites, play history or any other user signal.
/// </summary>
public class MediaExternalSignal
{
    [Key]
    public int Id { get; set; }

    public int MediaExternalReferenceId { get; set; }

    [JsonIgnore]
    public MediaExternalReference? ExternalReference { get; set; }

    [Required]
    [MaxLength(64)]
    public string SignalKey { get; set; } = string.Empty;

    public double? RawValue { get; set; }
    public double? NormalizedScore { get; set; }
    public long? SampleSize { get; set; }
    public string? RawMetricsJson { get; set; }

    [Required]
    [MaxLength(64)]
    public string AlgorithmVersion { get; set; } = string.Empty;

    [MaxLength(1024)]
    public string? SourceUrl { get; set; }

    [MaxLength(128)]
    public string? SourcePayloadHash { get; set; }

    public DateTime ObservedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RefreshAfter { get; set; }

    [Required]
    [MaxLength(32)]
    public string Status { get; set; } = "observed";

    [MaxLength(1024)]
    public string? LastError { get; set; }
}
