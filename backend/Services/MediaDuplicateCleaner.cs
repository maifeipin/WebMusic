using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WebMusic.Backend.Data;
using WebMusic.Backend.Models;

namespace WebMusic.Backend.Services;

public record DedupeCandidateItem(
    int MediaFileId,
    string FilePath,
    string Title,
    string Artist,
    string Album,
    double DurationSeconds,
    long SizeBytes,
    double ApproxBitrateKbps,
    int KeepScore,
    bool IsProtected,
    string Reason
);

public record DedupeGroupItem(
    string Stage,
    string GroupKey,
    int GroupSize,
    int KeptId,
    List<DedupeCandidateItem> Members
);

public record DedupeReport(
    string Stage,
    int TotalFiles,
    int TotalGroups,
    int RemoveCount,
    int ProtectedSkipCount,
    List<DedupeGroupItem> Groups
);

public record DedupeApplyResult(
    int RemovedCount,
    string ReportSha256,
    string RollbackManifestPath
);

/// <summary>
/// Four-stage duplicate cleaner.
/// Stage 1 (byte-hash): identical FileHash — byte-identical files.
/// Stage 2 (exact-metadata): identical Title+Artist+Album with duration cluster <= 3s.
/// Stage 3 (mbid): identical MusicBrainz RecordingId, conservative album constraint.
/// Stage 4 (fuzzy): normalized Title+Artist with 5s duration bucket — report only.
/// </summary>
public static class MediaDuplicateCleaner
{
    private const double ExactMetadataDurationToleranceSeconds = 3.0;
    private const double FuzzyDurationBucketSeconds = 5.0;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static async Task<DedupeReport> RunDryRunAsync(
        AppDbContext db,
        string stage,
        CancellationToken cancellationToken = default)
    {
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? readOnlyTx = null;
        try
        {
            if (db.Database.IsNpgsql())
            {
                readOnlyTx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
                await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY;", cancellationToken);
            }

            var files = await db.MediaFiles
                .AsNoTracking()
                .Select(m => new
                {
                    m.Id,
                    m.FilePath,
                    m.Title,
                    m.Artist,
                    m.Album,
                    m.Genre,
                    m.Year,
                    m.Duration,
                    m.SizeBytes,
                    m.CoverArt,
                    m.FileHash,
                    m.AddedAt,
                    PlaylistCount = m.PlaylistSongs.Count,
                    FavoriteCount = m.Favorites.Count,
                    PlayCount = db.PlayHistories.Count(h => h.MediaFileId == m.Id),
                    HasIdentity = db.MediaIdentities.Any(i => i.MediaFileId == m.Id)
                })
                .ToListAsync(cancellationToken);

            var context = files
                .Select(f => new MediaFileScored(
                    f.Id,
                    f.FilePath,
                    f.Title ?? string.Empty,
                    f.Artist ?? string.Empty,
                    f.Album ?? string.Empty,
                    f.Genre ?? string.Empty,
                    f.Year,
                    f.Duration,
                    f.SizeBytes,
                    f.CoverArt,
                    f.FileHash ?? string.Empty,
                    f.AddedAt,
                    f.PlaylistCount + f.FavoriteCount > 0 || f.PlayCount > 0,
                    f.PlayCount,
                    f.HasIdentity,
                    ComputeKeepScore(f.SizeBytes, f.Duration, f.Album, f.Genre, f.Year, f.CoverArt, f.HasIdentity, f.AddedAt)))
                .ToList();

            List<List<MediaFileScored>> clusters = stage.ToLowerInvariant() switch
            {
                "byte-hash" => ClusterByByteHash(context),
                "exact-metadata" => ClusterByExactMetadata(context),
                "mbid" => await ClusterByMbidAsync(db, context, cancellationToken),
                "fuzzy" => ClusterByFuzzy(context),
                _ => throw new ArgumentException($"Unknown stage '{stage}'. Use byte-hash | exact-metadata | mbid | fuzzy.")
            };

            var groups = new List<DedupeGroupItem>();
            var removeCount = 0;
            var protectedSkip = 0;

            foreach (var cluster in clusters)
            {
                var ordered = cluster.OrderByDescending(c => c.KeepScore).ThenBy(c => c.Id).ToList();
                var protectedMembers = ordered.Where(c => c.IsProtected).ToList();

                // If every member is referenced (playlist/favorite/play), keep all.
                if (protectedMembers.Count == ordered.Count)
                {
                    protectedSkip += ordered.Count;
                    continue;
                }

                // Winner: highest-score UNPROTECTED member. Protected members are never removed.
                var winner = ordered.First(c => !c.IsProtected);
                var losers = ordered.Where(c => c.Id != winner.Id && !c.IsProtected).ToList();

                removeCount += losers.Count;
                protectedSkip += protectedMembers.Count(p => p.Id != winner.Id);

                groups.Add(new DedupeGroupItem(
                    Stage: stage,
                    GroupKey: ClusterKey(stage, cluster),
                    GroupSize: cluster.Count,
                    KeptId: winner.Id,
                    Members: cluster.Select(c => new DedupeCandidateItem(
                        MediaFileId: c.Id,
                        FilePath: c.FilePath,
                        Title: c.Title,
                        Artist: c.Artist,
                        Album: c.Album,
                        DurationSeconds: Math.Round(c.Duration.TotalSeconds, 1),
                        SizeBytes: c.SizeBytes,
                        ApproxBitrateKbps: c.ApproxBitrateKbps,
                        KeepScore: c.KeepScore,
                        IsProtected: c.IsProtected,
                        Reason: c.Id == winner.Id ? "KEEP (winner)" : c.IsProtected ? "KEEP (referenced)" : "REMOVE (lower score)"
                    )).OrderByDescending(m => m.KeepScore).ThenBy(m => m.MediaFileId).ToList()
                ));
            }

            return new DedupeReport(
                Stage: stage,
                TotalFiles: context.Count,
                TotalGroups: groups.Count,
                RemoveCount: removeCount,
                ProtectedSkipCount: protectedSkip,
                Groups: groups.OrderByDescending(g => g.Members.Count).ThenBy(g => g.GroupKey).ToList()
            );
        }
        finally
        {
            if (readOnlyTx != null)
            {
                await readOnlyTx.RollbackAsync(cancellationToken);
            }
        }
    }

    public static async Task<string> SaveReportWithSha256Async(DedupeReport report, string outFile, CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(outFile);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(report, JsonOptions);
        await File.WriteAllTextAsync(outFile, json, cancellationToken);

        var fileBytes = await File.ReadAllBytesAsync(outFile, cancellationToken);
        var sha256Hex = Convert.ToHexString(SHA256.HashData(fileBytes)).ToLowerInvariant();
        await File.WriteAllTextAsync(outFile + ".sha256", $"{sha256Hex}  {Path.GetFileName(outFile)}\n", cancellationToken);
        return sha256Hex;
    }

    public static async Task<DedupeApplyResult> ApplyAsync(
        AppDbContext db,
        string reportPath,
        string expectedReportSha,
        int expectedRemoveCount,
        string rollbackManifestPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(reportPath))
        {
            throw new FileNotFoundException($"Dedupe dry run report not found: {reportPath}");
        }

        var fileBytes = await File.ReadAllBytesAsync(reportPath, cancellationToken);
        var actualSha = Convert.ToHexString(SHA256.HashData(fileBytes)).ToLowerInvariant();
        if (!string.Equals(actualSha, expectedReportSha.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Report SHA-256 verification failed! Expected '{expectedReportSha}', actual '{actualSha}'.");
        }

        var report = JsonSerializer.Deserialize<DedupeReport>(fileBytes, JsonOptions)
                     ?? throw new InvalidOperationException("Failed to deserialize dedupe report.");

        var removeItems = report.Groups
            .SelectMany(g => g.Members)
            .Where(m => m.Reason.StartsWith("REMOVE", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (removeItems.Count != expectedRemoveCount)
        {
            throw new InvalidOperationException($"Report remove count ({removeItems.Count}) does not match expected count ({expectedRemoveCount}).");
        }

        if (report.Stage == "fuzzy")
        {
            throw new InvalidOperationException("Stage 'fuzzy' is report-only and cannot be applied.");
        }

        var removeIds = removeItems.Select(x => x.MediaFileId).ToList();

        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);

        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync("LOCK TABLE \"MediaIdentities\" IN SHARE ROW EXCLUSIVE MODE;", cancellationToken);
            var p0 = new Npgsql.NpgsqlParameter("p0", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer) { Value = removeIds.ToArray() };
            await db.Database.ExecuteSqlRawAsync("SELECT \"Id\" FROM \"MediaFiles\" WHERE \"Id\" = ANY(@p0) FOR UPDATE", new[] { p0 }, cancellationToken);
        }

        // Real-time reference check: any new playlist/favorite/play reference aborts the batch.
        var nowReferenced = await db.PlaylistSongs.Where(s => removeIds.Contains(s.MediaFileId)).Select(s => s.MediaFileId).ToListAsync(cancellationToken);
        nowReferenced.AddRange(await db.Favorites.Where(f => removeIds.Contains(f.MediaFileId)).Select(f => f.MediaFileId).ToListAsync(cancellationToken));
        nowReferenced.AddRange(await db.PlayHistories.Where(h => removeIds.Contains(h.MediaFileId)).Select(h => h.MediaFileId).ToListAsync(cancellationToken));
        if (nowReferenced.Count > 0)
        {
            throw new InvalidOperationException($"Cannot apply: remove candidates gained references since dry run: {string.Join(", ", nowReferenced.Distinct().Take(10))}...");
        }

        var mediaList = await db.MediaFiles.Where(m => removeIds.Contains(m.Id)).ToListAsync(cancellationToken);
        if (mediaList.Count != removeItems.Count)
        {
            throw new InvalidOperationException($"Found {mediaList.Count} MediaFiles, expected {removeItems.Count}.");
        }
        var mediaById = mediaList.ToDictionary(m => m.Id);
        var rollbackEntries = new List<object>();

        // Delete dependants in FK-safe order, then the MediaFile row itself.
        var tags = await db.MediaTags.Where(t => removeIds.Contains(t.MediaFileId)).ToListAsync(cancellationToken);
        db.MediaTags.RemoveRange(tags);

        var references = await db.MediaExternalReferences.Where(r => removeIds.Contains(r.MediaFileId)).ToListAsync(cancellationToken);
        db.MediaExternalReferences.RemoveRange(references);

        var identities = await db.MediaIdentities.Where(i => removeIds.Contains(i.MediaFileId)).ToListAsync(cancellationToken);
        db.MediaIdentities.RemoveRange(identities);

        var scanStates = await db.MediaIdentityScanStates.Where(s => removeIds.Contains(s.MediaFileId)).ToListAsync(cancellationToken);
        db.MediaIdentityScanStates.RemoveRange(scanStates);

        var lyrics = await db.Lyrics.Where(l => removeIds.Contains(l.MediaFileId)).ToListAsync(cancellationToken);
        db.Lyrics.RemoveRange(lyrics);

        var enrichments = await db.MusicEnrichments.Where(e => removeIds.Contains(e.MediaFileId)).ToListAsync(cancellationToken);
        db.MusicEnrichments.RemoveRange(enrichments);

        var favorites = await db.Favorites.Where(f => removeIds.Contains(f.MediaFileId)).ToListAsync(cancellationToken);
        db.Favorites.RemoveRange(favorites);

        var playlistSongs = await db.PlaylistSongs.Where(s => removeIds.Contains(s.MediaFileId)).ToListAsync(cancellationToken);
        db.PlaylistSongs.RemoveRange(playlistSongs);

        var playHistories = await db.PlayHistories.Where(h => removeIds.Contains(h.MediaFileId)).ToListAsync(cancellationToken);
        db.PlayHistories.RemoveRange(playHistories);

        db.MediaFiles.RemoveRange(mediaList);

        foreach (var item in removeItems)
        {
            var media = mediaById[item.MediaFileId];
            rollbackEntries.Add(new
            {
                MediaFileId = item.MediaFileId,
                FilePath = item.FilePath,
                Title = item.Title,
                Artist = item.Artist,
                Album = item.Album,
                DurationSeconds = item.DurationSeconds,
                SizeBytes = item.SizeBytes,
                FileHash = media.FileHash,
                Year = media.Year,
                Genre = media.Genre,
                AddedAt = media.AddedAt,
                KeptId = report.Groups.First(g => g.Members.Any(m => m.MediaFileId == item.MediaFileId)).KeptId,
                Stage = report.Stage
            });
        }

        var removedRows = await db.SaveChangesAsync(cancellationToken);
        if (removedRows < removeItems.Count)
        {
            throw new InvalidOperationException($"Database updated {removedRows} rows, expected at least {removeItems.Count}. Aborting transaction.");
        }

        await tx.CommitAsync(cancellationToken);

        var manifestDir = Path.GetDirectoryName(rollbackManifestPath);
        if (!string.IsNullOrWhiteSpace(manifestDir))
        {
            Directory.CreateDirectory(manifestDir);
        }
        var manifestJson = JsonSerializer.Serialize(new
        {
            AppliedAt = DateTime.UtcNow,
            Stage = report.Stage,
            ReportSha256 = actualSha,
            TotalRemoved = removeItems.Count,
            RollbackEntries = rollbackEntries
        }, JsonOptions);
        var tempPath = rollbackManifestPath + ".tmp." + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(tempPath, manifestJson, cancellationToken);
        File.Move(tempPath, rollbackManifestPath, overwrite: true);

        return new DedupeApplyResult(removeItems.Count, actualSha, rollbackManifestPath);
    }

    private static int ComputeKeepScore(long sizeBytes, TimeSpan duration, string? album, string? genre, int year, string? coverArt, bool hasIdentity, DateTime addedAt)
    {
        int score = 0;

        // Quality dimension: approximate bitrate (SizeBytes*8 / Duration).
        var durationSeconds = duration.TotalSeconds;
        if (durationSeconds > 0)
        {
            var kbps = sizeBytes * 8.0 / durationSeconds / 1000.0;
            score += (int)Math.Round(Math.Min(kbps, 320.0) / 320.0 * 100.0); // 0..100
        }

        // Metadata completeness.
        var albumValue = album ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(albumValue) && !albumValue.Trim().Equals("Unknown Album", StringComparison.OrdinalIgnoreCase)) score += 20;
        var genreValue = genre ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(genreValue)) score += 5;
        if (year > 0) score += 5;
        if (!string.IsNullOrWhiteSpace(coverArt)) score += 10;

        // MusicBrainz identity.
        if (hasIdentity) score += 15;

        // Prefer more recent additions (fresher source).
        var ageDays = (DateTime.UtcNow - addedAt).TotalDays;
        if (ageDays < 90) score += 5;

        return score;
    }

    private static List<List<MediaFileScored>> ClusterByByteHash(List<MediaFileScored> files)
    {
        return files
            .Where(f => !string.IsNullOrWhiteSpace(f.FileHash) && !f.FileHash.StartsWith("nohash", StringComparison.OrdinalIgnoreCase))
            .GroupBy(f => f.FileHash)
            .Where(g => g.Count() > 1)
            .Select(g => g.ToList())
            .ToList();
    }

    private static List<List<MediaFileScored>> ClusterByExactMetadata(List<MediaFileScored> files)
    {
        var clusters = new List<List<MediaFileScored>>();
        foreach (var group in files.GroupBy(f => new { f.Title, f.Artist, f.Album }).Where(g => g.Count() > 1))
        {
            var ordered = group.OrderBy(f => f.Duration.TotalSeconds).ToList();
            var current = new List<MediaFileScored> { ordered[0] };
            for (var i = 1; i < ordered.Count; i++)
            {
                if (ordered[i].Duration.TotalSeconds - current[0].Duration.TotalSeconds <= ExactMetadataDurationToleranceSeconds)
                {
                    current.Add(ordered[i]);
                }
                else
                {
                    if (current.Count > 1) clusters.Add(current);
                    current = new List<MediaFileScored> { ordered[i] };
                }
            }
            if (current.Count > 1) clusters.Add(current);
        }
        return clusters;
    }

    private static async Task<List<List<MediaFileScored>>> ClusterByMbidAsync(AppDbContext db, List<MediaFileScored> files, CancellationToken cancellationToken)
    {
        var recordingIds = await db.MediaIdentities
            .AsNoTracking()
            .Where(i => i.RecordingId != null && i.Provider == "MusicBrainzLocal")
            .Select(i => new { i.MediaFileId, i.RecordingId })
            .ToListAsync(cancellationToken);

        var idToRecording = recordingIds
            .GroupBy(r => r.MediaFileId)
            .ToDictionary(g => g.Key, g => g.First().RecordingId!);

        var byRecording = files
            .Where(f => idToRecording.ContainsKey(f.Id))
            .GroupBy(f => idToRecording[f.Id])
            .Where(g => g.Count() > 1)
            .ToList();

        var clusters = new List<List<MediaFileScored>>();
        foreach (var group in byRecording)
        {
            var members = group.ToList();
            var namedGroups = members
                .Where(m => !IsUnknownAlbum(m.Album))
                .GroupBy(m => m.Album.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            var unknownMembers = members.Where(m => IsUnknownAlbum(m.Album)).ToList();

            if (namedGroups.Count == 0)
            {
                // Only Unknown Album members: one cluster.
                if (members.Count > 1) clusters.Add(members);
                continue;
            }

            foreach (var (albumName, cluster) in namedGroups)
            {
                // Attach each Unknown-album member to the nearest (duration) named cluster.
                var attach = unknownMembers
                    .Where(u => !clusters.Any(c => c.Contains(u)) && !namedGroups.Values.Any(l => l.Contains(u)))
                    .OrderBy(u => Math.Abs(u.Duration.TotalSeconds - cluster.Average(c => c.Duration.TotalSeconds)))
                    .FirstOrDefault();
                var fullCluster = cluster.ToList();
                if (attach != null) fullCluster.Add(attach);
                if (fullCluster.Count > 1) clusters.Add(fullCluster);
            }
        }
        return clusters;
    }

    private static bool IsUnknownAlbum(string album)
    {
        return string.IsNullOrWhiteSpace(album) || album.Trim().Equals("Unknown Album", StringComparison.OrdinalIgnoreCase);
    }

    private static List<List<MediaFileScored>> ClusterByFuzzy(List<MediaFileScored> files)
    {
        var clusters = new List<List<MediaFileScored>>();
        foreach (var group in files.GroupBy(f => new { T = NormalizeFuzzy(f.Title), A = NormalizeFuzzy(f.Artist) }).Where(g => g.Key.T.Length > 0 && g.Count() > 1))
        {
            var ordered = group.OrderBy(f => f.Duration.TotalSeconds).ToList();
            var current = new List<MediaFileScored> { ordered[0] };
            for (var i = 1; i < ordered.Count; i++)
            {
                if (ordered[i].Duration.TotalSeconds - current[0].Duration.TotalSeconds <= FuzzyDurationBucketSeconds)
                {
                    current.Add(ordered[i]);
                }
                else
                {
                    if (current.Count > 1) clusters.Add(current);
                    current = new List<MediaFileScored> { ordered[i] };
                }
            }
            if (current.Count > 1) clusters.Add(current);
        }
        return clusters;
    }

    private static string NormalizeFuzzy(string value)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in (value ?? string.Empty).ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch) || (ch >= '\u4e00' && ch <= '\u9fff'))
            {
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }

    private static string ClusterKey(string stage, List<MediaFileScored> cluster) => stage.ToLowerInvariant() switch
    {
        "byte-hash" => cluster[0].FileHash,
        "exact-metadata" => $"{cluster[0].Title} | {cluster[0].Artist} | {cluster[0].Album}",
        "mbid" => cluster[0].Title,
        _ => $"{NormalizeFuzzy(cluster[0].Title)} | {NormalizeFuzzy(cluster[0].Artist)}"
    };

    private sealed record MediaFileScored(
        int Id,
        string FilePath,
        string Title,
        string Artist,
        string Album,
        string Genre,
        int Year,
        TimeSpan Duration,
        long SizeBytes,
        string? CoverArt,
        string FileHash,
        DateTime AddedAt,
        bool IsProtected,
        int PlayCount,
        bool HasIdentity,
        int KeepScore)
    {
        public double ApproxBitrateKbps => Duration.TotalSeconds > 0 ? Math.Round(SizeBytes * 8.0 / Duration.TotalSeconds / 1000.0, 0) : 0;
    }
}
