using WebMusic.Backend.Models;

namespace WebMusic.Backend.Services;

public record CreateIdentityImportBatchRequest(
    string ReportId,
    List<int> ApprovedItemIds
);

public record PreviewIdentityImportBatchRequest(
    string ReportId,
    List<int> ApprovedItemIds
);

public record IdentityImportItemValidation(
    int MediaFileId,
    bool MediaFileExists,
    bool HasExistingLocalIdentity,
    bool HasConflictingManualIdentity,
    string TargetTitle,
    string TargetArtist,
    string? TargetAlbum,
    double TargetDurationSeconds,
    string MetadataFingerprint,
    string MatchedRecordingId,
    double Confidence,
    string? ValidationErrorMessage
);

public record IdentityImportPreviewResult(
    string ReportId,
    string SourceReportSha256,
    string AuditManifestSha256,
    int TotalCandidates,
    int ValidCount,
    int InvalidCount,
    List<IdentityImportItemValidation> Validations
);

public interface IIdentityImportService
{
    Task<IdentityImportPreviewResult> PreviewBatchAsync(PreviewIdentityImportBatchRequest request, CancellationToken cancellationToken = default);
    Task<IdentityImportBatch> CreateDraftBatchAsync(CreateIdentityImportBatchRequest request, string username, CancellationToken cancellationToken = default);
    Task<IdentityImportBatch> ApproveBatchAsync(int batchId, string username, CancellationToken cancellationToken = default);
    Task<IdentityImportBatch> ApplyBatchAsync(int batchId, string username, CancellationToken cancellationToken = default);
    Task<IdentityImportBatch> RollbackBatchAsync(int batchId, string username, CancellationToken cancellationToken = default);
    Task<IdentityImportBatch> BackfillLegacyPilotRound2Async(string username = "admin_pilot", CancellationToken cancellationToken = default);
    Task<List<IdentityImportBatch>> GetBatchesAsync(CancellationToken cancellationToken = default);
    Task<IdentityImportBatch?> GetBatchAsync(int batchId, CancellationToken cancellationToken = default);
}
