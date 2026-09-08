using System.ComponentModel.DataAnnotations;

namespace WebMusic.Backend.Models;

public class IdentityImportBatch
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(128)]
    public string BatchTag { get; set; } = string.Empty;

    [Required]
    [MaxLength(128)]
    public string SourceReportSha256 { get; set; } = string.Empty;

    [Required]
    [MaxLength(128)]
    public string AuditManifestSha256 { get; set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    public string Provider { get; set; } = "MusicBrainzLocal";

    [Required]
    [MaxLength(32)]
    public string Status { get; set; } = "Draft"; // Draft, Approved, Applied, RolledBack

    public int ItemCount { get; set; }

    public int AppliedCount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(64)]
    public string CreatedBy { get; set; } = string.Empty;

    public DateTime? ApprovedAt { get; set; }

    [MaxLength(64)]
    public string? ApprovedBy { get; set; }

    public DateTime? AppliedAt { get; set; }

    [MaxLength(64)]
    public string? AppliedBy { get; set; }

    public DateTime? RolledBackAt { get; set; }

    [MaxLength(64)]
    public string? RolledBackBy { get; set; }

    public List<IdentityImportItem> Items { get; set; } = new();
}
