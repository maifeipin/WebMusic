using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WebMusic.Backend.Data;
using WebMusic.Backend.Models;
using WebMusic.Backend.Services;
using Xunit;

namespace WebMusic.Backend.Tests;

public class IdentityImportServiceTests : IDisposable
{
    private readonly string _tempReportDir;
    private readonly IConfiguration _configuration;

    public IdentityImportServiceTests()
    {
        _tempReportDir = Path.Combine(Path.GetTempPath(), "webmusic_import_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempReportDir);

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IdentityImport:ReportsDirectory"] = _tempReportDir
            })
            .Build();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempReportDir))
        {
            try { Directory.Delete(_tempReportDir, true); } catch { }
        }
    }

    private AppDbContext CreateInMemoryDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source=file:{dbName}?mode=memory&cache=shared")
            .Options;

        var context = new AppDbContext(options);
        context.Database.OpenConnection();
        context.Database.EnsureCreated();

        context.ScanSources.Add(new ScanSource { Id = 1, Name = "Test", Path = "/music", Type = "local" });
        context.SaveChanges();

        return context;
    }

    private void CreateSyntheticReportAndAuditFiles(string reportId, List<ShadowRunAuditItem> items, List<int>? approvedIds = null)
    {
        var cleanId = IdentityImportService.NormalizeReportId(reportId);
        var reportPath = Path.Combine(_tempReportDir, $"{cleanId}.json");
        var report = new ShadowRunReport(
            Mode: "SHADOW_RUN_ZERO_WRITE",
            TargetNode: "http://127.0.0.1:5050",
            Timestamp: DateTime.UtcNow,
            Summary: new ShadowRunSummary(
                TotalEvaluated: items.Count,
                HighConfidence: items.Count(i => i.Outcome == "HighConfidence"),
                HighConfidenceRate: items.Count > 0 ? (double)items.Count(i => i.Outcome == "HighConfidence") / items.Count : 0,
                Proposed: 0,
                ProposedRate: 0,
                Unmatched: 0,
                UnmatchedRate: 0,
                Failed: 0,
                DerivativeOrClip: 0,
                AverageElapsedMs: 100
            ),
            Samples: new Dictionary<string, List<ShadowRunAuditItem>>
            {
                ["highConfidence"] = items.Where(i => i.Outcome == "HighConfidence").ToList()
            },
            AllItems: items,
            HighConfidenceSha256: "synthetic_high_conf_sha256"
        );

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(reportPath, json);

        // Write corresponding audit manifest
        var auditPath = Path.Combine(_tempReportDir, $"{cleanId}_audit.md");
        var idsList = approvedIds ?? items.Where(i => i.Outcome == "HighConfidence").Select(i => i.MediaId).ToList();
        var auditContent = $@"# Audit Record
- Report: {cleanId}

## 可写入候选 ({idsList.Count})
```text
{string.Join(", ", idsList)}
```
";
        File.WriteAllText(auditPath, auditContent);
    }

    [Fact]
    public void ReportId_WithSeparatorsOrTraversal_IsRejected()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var service = new IdentityImportService(db, NullLogger<IdentityImportService>.Instance, null, _configuration);

        Assert.Throws<ArgumentException>(() => IdentityImportService.NormalizeReportId("../../etc/passwd"));
        Assert.Throws<ArgumentException>(() => IdentityImportService.NormalizeReportId("dir/report"));
        Assert.Throws<ArgumentException>(() => IdentityImportService.NormalizeReportId("dir\\report"));
        Assert.Throws<ArgumentException>(() => IdentityImportService.NormalizeReportId("report;rm -rf"));

        // Valid report IDs pass
        Assert.Equal("valid_report_01", IdentityImportService.NormalizeReportId("valid_report_01"));
        Assert.Equal("shadow_run_round3_1000", IdentityImportService.NormalizeReportId("shadow_run_round3_1000.json"));
    }

    [Fact]
    public async Task AuditManifest_EnforcesApprovedWhitelist_UnapprovedHighConfidenceRejected()
    {
        var dbName = Guid.NewGuid().ToString();
        using var db = CreateInMemoryDbContext(dbName);

        var file1 = new MediaFile { Id = 1, ScanSourceId = 1, Title = "Approved Track", Artist = "Artist", Album = "Album", Duration = TimeSpan.FromSeconds(200), FilePath = "/m/1.mp3" };
        var file2 = new MediaFile { Id = 2, ScanSourceId = 1, Title = "Unapproved Track", Artist = "Artist", Album = "Album", Duration = TimeSpan.FromSeconds(200), FilePath = "/m/2.mp3" };
        db.MediaFiles.AddRange(file1, file2);
        await db.SaveChangesAsync();

        var reportItems = new List<ShadowRunAuditItem>
        {
            new(1, "Approved Track", "Artist", "Album", 200, "Tier1", 1000, "HighConfidence", 1.0, 50, "mbid-1", "Approved Track", "Artist", 200, null, false, null),
            new(2, "Unapproved Track", "Artist", "Album", 200, "Tier1", 1000, "HighConfidence", 1.0, 50, "mbid-2", "Unapproved Track", "Artist", 200, null, false, null)
        };

        // Whitelist ONLY contains track 1, track 2 was rejected during human audit
        CreateSyntheticReportAndAuditFiles("audit_gate_report", reportItems, approvedIds: new List<int> { 1 });

        var service = new IdentityImportService(db, NullLogger<IdentityImportService>.Instance, null, _configuration);

        // Preview batch containing both 1 and 2
        var preview = await service.PreviewBatchAsync(new PreviewIdentityImportBatchRequest("audit_gate_report", new List<int> { 1, 2 }));
        Assert.Equal(2, preview.TotalCandidates);
        Assert.Equal(1, preview.ValidCount);
        Assert.Equal(1, preview.InvalidCount);

        var v2 = preview.Validations.First(v => v.MediaFileId == 2);
        Assert.Contains("not approved in audit manifest", v2.ValidationErrorMessage);

        // Attempting to create draft with unapproved track 2 must fail
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateDraftBatchAsync(new CreateIdentityImportBatchRequest("audit_gate_report", new List<int> { 1, 2 }), "admin"));
        Assert.Contains("validation failed for MediaFile 2", ex.Message);
    }

    [Fact]
    public async Task MissingAuditManifest_ThrowsFileNotFoundException_CannotBeBypassed()
    {
        var dbName = Guid.NewGuid().ToString();
        using var db = CreateInMemoryDbContext(dbName);
        var service = new IdentityImportService(db, NullLogger<IdentityImportService>.Instance, null, _configuration);

        var reportPath = Path.Combine(_tempReportDir, "no_audit_report.json");
        var report = new ShadowRunReport(
            Mode: "SHADOW_RUN_ZERO_WRITE",
            TargetNode: "http://127.0.0.1:5050",
            Timestamp: DateTime.UtcNow,
            Summary: new ShadowRunSummary(1, 1, 1.0, 0, 0, 0, 0, 0, 0, 50),
            Samples: new Dictionary<string, List<ShadowRunAuditItem>>(),
            AllItems: new List<ShadowRunAuditItem>
            {
                new(1, "Title", "Artist", "Album", 200, "Tier1", 1000, "HighConfidence", 1.0, 50, "mbid-1", "Title", "Artist", 200, null, false, null)
            },
            HighConfidenceSha256: "dummy_sha"
        );
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report));

        // Audit manifest "no_audit_report_audit.md" is intentionally NOT created
        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.PreviewBatchAsync(new PreviewIdentityImportBatchRequest("no_audit_report", new List<int> { 1 })));
        Assert.Contains("Audit manifest 'no_audit_report_audit.md' was not found", ex.Message);
    }

    [Fact]
    public async Task PreviewBatch_IdentifiesNonExistentMediaAndExistingIdentities()
    {
        var dbName = Guid.NewGuid().ToString();
        using var db = CreateInMemoryDbContext(dbName);

        // Seed 1 MediaFile with existing MusicBrainzLocal identity
        var file1 = new MediaFile
        {
            Id = 101,
            ScanSourceId = 1,
            Title = "Song A",
            Artist = "Artist A",
            Album = "Album A",
            Duration = TimeSpan.FromSeconds(200),
            FilePath = "/music/song_a.mp3"
        };
        // Seed 1 MediaFile with manual identity
        var file2 = new MediaFile
        {
            Id = 102,
            ScanSourceId = 1,
            Title = "Song B",
            Artist = "Artist B",
            Album = "Album B",
            Duration = TimeSpan.FromSeconds(180),
            FilePath = "/music/song_b.mp3"
        };
        // Seed 1 Clean MediaFile
        var file3 = new MediaFile
        {
            Id = 103,
            ScanSourceId = 1,
            Title = "Song C",
            Artist = "Artist C",
            Album = "Album C",
            Duration = TimeSpan.FromSeconds(240),
            FilePath = "/music/song_c.mp3"
        };

        db.MediaFiles.AddRange(file1, file2, file3);
        db.MediaIdentities.Add(new MediaIdentity
        {
            MediaFileId = 101,
            Provider = "MusicBrainzLocal",
            RecordingId = "rec-101",
            Status = "approved"
        });
        db.MediaIdentities.Add(new MediaIdentity
        {
            MediaFileId = 102,
            Provider = "MusicBrainz",
            RecordingId = "rec-102",
            Status = "manual",
            MatchMethod = "ManualOverride"
        });
        await db.SaveChangesAsync();

        var reportItems = new List<ShadowRunAuditItem>
        {
            new(101, "Song A", "Artist A", "Album A", 200, "Tier1", 1000, "HighConfidence", 1.0, 50, "new-rec-101", "Song A", "Artist A", 200, null, false, null),
            new(102, "Song B", "Artist B", "Album B", 180, "Tier1", 1000, "HighConfidence", 1.0, 50, "new-rec-102", "Song B", "Artist B", 180, null, false, null),
            new(103, "Song C", "Artist C", "Album C", 240, "Tier1", 1000, "HighConfidence", 1.0, 50, "new-rec-103", "Song C", "Artist C", 240, null, false, null),
            new(999, "Ghost Song", "Ghost Artist", null, 100, "Tier1", 1000, "HighConfidence", 1.0, 50, "new-rec-999", "Ghost Song", "Ghost Artist", 100, null, false, null)
        };

        CreateSyntheticReportAndAuditFiles("preview_test_report", reportItems, new List<int> { 101, 102, 103, 999 });

        var service = new IdentityImportService(db, NullLogger<IdentityImportService>.Instance, null, _configuration);
        var request = new PreviewIdentityImportBatchRequest(
            ReportId: "preview_test_report",
            ApprovedItemIds: new List<int> { 101, 102, 103, 999 }
        );

        var preview = await service.PreviewBatchAsync(request);

        Assert.Equal(4, preview.TotalCandidates);
        Assert.Equal(1, preview.ValidCount);
        Assert.Equal(3, preview.InvalidCount);

        var v101 = preview.Validations.First(v => v.MediaFileId == 101);
        Assert.True(v101.HasExistingLocalIdentity);
        Assert.Contains("already has a 'MusicBrainzLocal' identity", v101.ValidationErrorMessage);

        var v102 = preview.Validations.First(v => v.MediaFileId == 102);
        Assert.True(v102.HasConflictingManualIdentity);
        Assert.Contains("conflicting manual identity", v102.ValidationErrorMessage);

        var v103 = preview.Validations.First(v => v.MediaFileId == 103);
        Assert.Null(v103.ValidationErrorMessage);
        Assert.False(string.IsNullOrEmpty(v103.MetadataFingerprint));

        var v999 = preview.Validations.First(v => v.MediaFileId == 999);
        Assert.False(v999.MediaFileExists);
        Assert.Contains("not found", v999.ValidationErrorMessage);
    }

    [Fact]
    public async Task FullLifecycle_Draft_Approve_Apply_Rollback_Succeeds()
    {
        var dbName = Guid.NewGuid().ToString();
        using var db = CreateInMemoryDbContext(dbName);

        var file1 = new MediaFile { Id = 201, ScanSourceId = 1, Title = "Track One", Artist = "Artist One", Album = "Album One", Duration = TimeSpan.FromSeconds(210), FilePath = "/music/1.mp3" };
        var file2 = new MediaFile { Id = 202, ScanSourceId = 1, Title = "Track Two", Artist = "Artist Two", Album = "Album Two", Duration = TimeSpan.FromSeconds(195), FilePath = "/music/2.mp3" };
        db.MediaFiles.AddRange(file1, file2);
        await db.SaveChangesAsync();

        var reportItems = new List<ShadowRunAuditItem>
        {
            new(201, "Track One", "Artist One", "Album One", 210, "Tier1", 1000, "HighConfidence", 1.0, 50, "mbid-201", "Track One", "Artist One", 210, null, false, null),
            new(202, "Track Two", "Artist Two", "Album Two", 195, "Tier1", 1000, "HighConfidence", 0.99, 50, "mbid-202", "Track Two", "Artist Two", 195, null, false, null)
        };

        CreateSyntheticReportAndAuditFiles("lifecycle_report", reportItems, new List<int> { 201, 202 });
        var service = new IdentityImportService(db, NullLogger<IdentityImportService>.Instance, null, _configuration);

        var request = new CreateIdentityImportBatchRequest(
            ReportId: "lifecycle_report",
            ApprovedItemIds: new List<int> { 201, 202 }
        );

        // 1. Create Draft
        var draft = await service.CreateDraftBatchAsync(request, "tester");
        Assert.Equal("Draft", draft.Status);
        Assert.Equal(2, draft.ItemCount);
        Assert.Equal(0, draft.AppliedCount);
        Assert.Equal("MusicBrainzLocal", draft.Provider);
        Assert.StartsWith("Batch-", draft.BatchTag);
        Assert.All(draft.Items, i => Assert.Equal("Pending", i.Status));

        // Attempting to apply Draft directly must fail
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyBatchAsync(draft.Id, "tester"));

        // 2. Approve
        var approved = await service.ApproveBatchAsync(draft.Id, "admin_approver");
        Assert.Equal("Approved", approved.Status);
        Assert.Equal("admin_approver", approved.ApprovedBy);
        Assert.NotNull(approved.ApprovedAt);

        // 3. Apply
        var applied = await service.ApplyBatchAsync(approved.Id, "admin_applier");
        Assert.Equal("Applied", applied.Status);
        Assert.Equal(2, applied.AppliedCount);
        Assert.All(applied.Items, i =>
        {
            Assert.Equal("Applied", i.Status);
            Assert.NotNull(i.CreatedIdentityId);
        });

        // Verify rows exist in MediaIdentities
        var identities = await db.MediaIdentities.Where(i => i.Provider == "MusicBrainzLocal").ToListAsync();
        Assert.Equal(2, identities.Count);
        Assert.All(identities, i =>
        {
            Assert.Equal(draft.BatchTag, i.MatchMethod);
            Assert.Equal("approved", i.Status);
            Assert.Equal("Pending", i.CoverStatus);
            Assert.Equal("Pending", i.LyricsStatus);
        });

        // 4. Rollback
        var rolledBack = await service.RollbackBatchAsync(applied.Id, "admin_rollback");
        Assert.Equal("RolledBack", rolledBack.Status);
        Assert.All(rolledBack.Items, i => Assert.Equal("RolledBack", i.Status));

        // Verify rows are completely purged from MediaIdentities
        var remainingIdentities = await db.MediaIdentities.Where(i => i.Provider == "MusicBrainzLocal").ToListAsync();
        Assert.Empty(remainingIdentities);
    }

    [Fact]
    public async Task MetadataFingerprint_AlbumAndDurationDriftBlocksApply_FileMoveAllowed()
    {
        var dbName = Guid.NewGuid().ToString();
        using var db = CreateInMemoryDbContext(dbName);

        var file = new MediaFile
        {
            Id = 301,
            ScanSourceId = 1,
            Title = "Song Title",
            Artist = "Song Artist",
            Album = "Original Album",
            Duration = TimeSpan.FromSeconds(200),
            FilePath = "/music/original/song.mp3"
        };
        db.MediaFiles.Add(file);
        await db.SaveChangesAsync();

        var reportItems = new List<ShadowRunAuditItem>
        {
            new(301, "Song Title", "Song Artist", "Original Album", 200, "Tier1", 1000, "HighConfidence", 1.0, 50, "rec-301", "Song Title", "Song Artist", 200, null, false, null)
        };
        CreateSyntheticReportAndAuditFiles("drift_test_report", reportItems, new List<int> { 301 });

        var service = new IdentityImportService(db, NullLogger<IdentityImportService>.Instance, null, _configuration);
        var batch = await service.CreateDraftBatchAsync(new CreateIdentityImportBatchRequest("drift_test_report", new List<int> { 301 }), "admin");
        await service.ApproveBatchAsync(batch.Id, "admin");

        // 1. Moving file (FilePath change only) should NOT drift
        file.FilePath = "/music/new_folder/song.mp3";
        await db.SaveChangesAsync();

        var applied = await service.ApplyBatchAsync(batch.Id, "admin");
        Assert.Equal("Applied", applied.Status);
        Assert.Single(await db.MediaIdentities.ToListAsync());

        // Rollback for second test
        await service.RollbackBatchAsync(batch.Id, "admin");

        // 2. Changing Album must cause fingerprint drift and block apply
        var batch2 = await service.CreateDraftBatchAsync(new CreateIdentityImportBatchRequest("drift_test_report", new List<int> { 301 }), "admin");
        await service.ApproveBatchAsync(batch2.Id, "admin");

        file.Album = "Drifted Album Name";
        await db.SaveChangesAsync();

        var exAlbum = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyBatchAsync(batch2.Id, "admin"));
        Assert.Contains("metadata fingerprint changed", exAlbum.Message);

        // 3. Reset album, change duration -> must cause drift and block apply
        file.Album = "Original Album";
        file.Duration = TimeSpan.FromSeconds(250); // Duration drift
        await db.SaveChangesAsync();

        var exDuration = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyBatchAsync(batch2.Id, "admin"));
        Assert.Contains("metadata fingerprint changed", exDuration.Message);
    }

    [Fact]
    public async Task RollbackBatch_RefusesIfIdentityModifiedOrLegacyApplied()
    {
        var dbName = Guid.NewGuid().ToString();
        using var db = CreateInMemoryDbContext(dbName);

        var file = new MediaFile
        {
            Id = 401,
            ScanSourceId = 1,
            Title = "Track",
            Artist = "Artist",
            Album = "Album",
            Duration = TimeSpan.FromSeconds(200),
            FilePath = "/music/song.mp3"
        };
        db.MediaFiles.Add(file);
        await db.SaveChangesAsync();

        var reportItems = new List<ShadowRunAuditItem>
        {
            new(401, "Track", "Artist", "Album", 200, "Tier1", 1000, "HighConfidence", 1.0, 50, "rec-401", "Track", "Artist", 200, null, false, null)
        };
        CreateSyntheticReportAndAuditFiles("rollback_check_report", reportItems, new List<int> { 401 });

        var service = new IdentityImportService(db, NullLogger<IdentityImportService>.Instance, null, _configuration);
        var batch = await service.CreateDraftBatchAsync(new CreateIdentityImportBatchRequest("rollback_check_report", new List<int> { 401 }), "admin");
        await service.ApproveBatchAsync(batch.Id, "admin");
        await service.ApplyBatchAsync(batch.Id, "admin");

        // Check 1: Status modified from approved to rejected
        var iden = await db.MediaIdentities.FirstAsync(i => i.MediaFileId == 401);
        iden.Status = "rejected";
        await db.SaveChangesAsync();

        var exStatus = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RollbackBatchAsync(batch.Id, "admin"));
        Assert.Contains("expected 'approved'", exStatus.Message);

        // Reset status, modify RecordingId
        iden.Status = "approved";
        iden.RecordingId = "tampered-recording-id";
        await db.SaveChangesAsync();

        var exMbid = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RollbackBatchAsync(batch.Id, "admin"));
        Assert.Contains("RecordingId mismatch", exMbid.Message);

        // Reset RecordingId, modify CoverStatus
        iden.RecordingId = "rec-401";
        iden.CoverStatus = "Matched";
        await db.SaveChangesAsync();

        var exCover = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RollbackBatchAsync(batch.Id, "admin"));
        Assert.Contains("resources have been modified", exCover.Message);

        // Check 2: LegacyApplied or Immutable batch refusal
        batch.Status = "LegacyApplied";
        await db.SaveChangesAsync();

        var exLegacy = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RollbackBatchAsync(batch.Id, "admin"));
        Assert.Contains("cannot be rolled back", exLegacy.Message);
    }
}
