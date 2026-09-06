using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using WebMusic.Backend.Data;

namespace WebMusic.Backend.Services;

public static class SchemaFingerprintVerifier
{
    public const string BaselineMigrationId = "20260906112053_Initial_EnrichmentBaseline";
    public const string ProductVersion = "8.0.10";

    public static readonly string[] RequiredTables = new[]
    {
        "Users", "ScanSources", "StorageCredentials", "MediaFiles",
        "PlayHistories", "Favorites", "Lyrics", "MusicEnrichments",
        "Playlists", "PlaylistSongs", "Plugins", "EnrichmentJobs",
        "EnrichmentAttempts", "MediaIdentities", "MediaTags", "TagEvidences"
    };

    // Full schema definition of production baseline: (ColumnName, DataType, IsNullable)
    public static readonly Dictionary<string, (string Name, string DataType, bool IsNullable)[]> ExpectedColumns =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["EnrichmentAttempts"] = new[]
            {
                ("Id", "integer", false),
                ("JobId", "text", false),
                ("MediaFileId", "integer", false),
                ("Provider", "text", false),
                ("RequestKey", "text", true),
                ("HTTPStatus", "integer", true),
                ("Outcome", "text", false),
                ("Confidence", "double precision", false),
                ("RetryCount", "integer", false),
                ("Detail", "text", false),
                ("CreatedAt", "timestamp with time zone", false)
            },
            ["EnrichmentJobs"] = new[]
            {
                ("Id", "text", false),
                ("Scope", "text", false),
                ("RequestedByUserId", "integer", true),
                ("Total", "integer", false),
                ("Processed", "integer", false),
                ("Updated", "integer", false),
                ("Unmatched", "integer", false),
                ("Skipped", "integer", false),
                ("Failed", "integer", false),
                ("Cursor", "integer", false),
                ("Status", "text", false),
                ("SongIdsJson", "text", false),
                ("StartedAt", "timestamp with time zone", false),
                ("FinishedAt", "timestamp with time zone", true)
            },
            ["Favorites"] = new[]
            {
                ("Id", "integer", false),
                ("UserId", "integer", false),
                ("MediaFileId", "integer", false),
                ("CreatedAt", "timestamp with time zone", false)
            },
            ["Lyrics"] = new[]
            {
                ("Id", "integer", false),
                ("MediaFileId", "integer", false),
                ("Content", "text", true),
                ("Language", "text", true),
                ("Source", "text", true),
                ("Version", "text", true),
                ("CreatedAt", "timestamp without time zone", true)
            },
            ["MediaFiles"] = new[]
            {
                ("Id", "integer", false),
                ("FilePath", "text", false),
                ("Title", "text", false),
                ("Artist", "text", false),
                ("Album", "text", false),
                ("Genre", "text", false),
                ("Year", "integer", false),
                ("Duration", "interval", false),
                ("SizeBytes", "bigint", false),
                ("FileHash", "text", false),
                ("ParentPath", "text", false),
                ("AddedAt", "timestamp with time zone", false),
                ("ScanSourceId", "integer", false),
                ("CoverArt", "text", true)
            },
            ["MediaIdentities"] = new[]
            {
                ("Id", "integer", false),
                ("MediaFileId", "integer", false),
                ("Provider", "text", false),
                ("RecordingId", "text", true),
                ("ReleaseId", "text", true),
                ("ArtistId", "text", true),
                ("ISRC", "text", true),
                ("AcoustId", "text", true),
                ("MatchMethod", "text", false),
                ("Confidence", "double precision", false),
                ("Status", "text", false),
                ("MatchedAt", "timestamp with time zone", false),
                ("LastVerifiedAt", "timestamp with time zone", true)
            },
            ["MediaTags"] = new[]
            {
                ("Id", "integer", false),
                ("MediaFileId", "integer", false),
                ("Namespace", "text", false),
                ("Key", "text", false),
                ("Value", "text", false),
                ("NumericValue", "double precision", true),
                ("Confidence", "double precision", false),
                ("Status", "text", false),
                ("CreatedAt", "timestamp with time zone", false),
                ("UpdatedAt", "timestamp with time zone", false)
            },
            ["MusicEnrichments"] = new[]
            {
                ("Id", "integer", false),
                ("MediaFileId", "integer", false),
                ("Provider", "text", false),
                ("ExternalId", "text", true),
                ("Confidence", "double precision", false),
                ("Status", "text", false),
                ("Details", "text", false),
                ("CreatedAt", "timestamp with time zone", false)
            },
            ["PlayHistories"] = new[]
            {
                ("Id", "integer", false),
                ("UserId", "integer", false),
                ("MediaFileId", "integer", false),
                ("PlayedAt", "timestamp with time zone", false)
            },
            ["PlaylistSongs"] = new[]
            {
                ("Id", "integer", false),
                ("PlaylistId", "integer", false),
                ("MediaFileId", "integer", false),
                ("AddedAt", "timestamp with time zone", false)
            },
            ["Playlists"] = new[]
            {
                ("Id", "integer", false),
                ("Name", "text", false),
                ("UserId", "integer", false),
                ("CoverArt", "text", true),
                ("CreatedAt", "timestamp with time zone", false),
                ("Type", "text", false),
                ("ShareToken", "text", true),
                ("ShareExpiresAt", "timestamp with time zone", true),
                ("SharePassword", "text", true)
            },
            ["Plugins"] = new[]
            {
                ("Id", "integer", false),
                ("Name", "text", false),
                ("Description", "text", true),
                ("BaseUrl", "text", true),
                ("EntryPath", "text", true),
                ("Icon", "text", true),
                ("IsEnabled", "boolean", true),
                ("CreatedAt", "timestamp without time zone", false)
            },
            ["ScanSources"] = new[]
            {
                ("Id", "integer", false),
                ("Name", "text", false),
                ("Path", "text", false),
                ("Type", "text", false),
                ("StorageCredentialId", "integer", true),
                ("UserId", "integer", true)
            },
            ["StorageCredentials"] = new[]
            {
                ("Id", "integer", false),
                ("Name", "text", false),
                ("ProviderType", "text", false),
                ("Host", "text", false),
                ("AuthData", "text", false),
                ("UserId", "integer", true)
            },
            ["TagEvidences"] = new[]
            {
                ("Id", "integer", false),
                ("MediaTagId", "integer", false),
                ("Source", "text", false),
                ("SourceId", "text", true),
                ("EvidenceUrl", "text", true),
                ("EvidenceText", "text", true),
                ("RetrievedAt", "timestamp with time zone", false),
                ("ExpiresAt", "timestamp with time zone", true),
                ("RawPayload", "text", true)
            },
            ["Users"] = new[]
            {
                ("Id", "integer", false),
                ("Username", "text", false),
                ("PasswordHash", "text", false),
                ("IsAdmin", "boolean", false)
            }
        };

    // Expected foreign keys: (SourceTable, SourceColumn, TargetTable, TargetColumn, DeleteRule)
    public static readonly (string SourceTable, string SourceCol, string TargetTable, string TargetCol, string DeleteRule)[] ExpectedForeignKeys = new[]
    {
        ("Favorites", "UserId", "Users", "Id", "CASCADE"),
        ("Favorites", "MediaFileId", "MediaFiles", "Id", "CASCADE"),
        ("MediaFiles", "ScanSourceId", "ScanSources", "Id", "CASCADE"),
        ("PlayHistories", "UserId", "Users", "Id", "CASCADE"),
        ("PlayHistories", "MediaFileId", "MediaFiles", "Id", "CASCADE"),
        ("PlaylistSongs", "PlaylistId", "Playlists", "Id", "CASCADE"),
        ("PlaylistSongs", "MediaFileId", "MediaFiles", "Id", "CASCADE"),
        ("ScanSources", "StorageCredentialId", "StorageCredentials", "Id", "NO ACTION"),
        ("ScanSources", "UserId", "Users", "Id", "NO ACTION"),
        ("StorageCredentials", "UserId", "Users", "Id", "NO ACTION"),
        ("Lyrics", "MediaFileId", "MediaFiles", "Id", "CASCADE")
    };

    // Expected indexes: (Table, IndexName, IsUnique, Columns)
    public static readonly (string Table, string IndexName, bool IsUnique, string Columns)[] ExpectedIndexes = new[]
    {
        ("EnrichmentAttempts", "IX_EnrichmentAttempts_JobId_CreatedAt", false, "JobId, CreatedAt"),
        ("EnrichmentJobs", "IX_EnrichmentJobs_Status", false, "Status"),
        ("Favorites", "IX_Favorites_MediaFileId", false, "MediaFileId"),
        ("Favorites", "IX_Favorites_UserId", false, "UserId"),
        ("MediaFiles", "IX_MediaFiles_Album", false, "Album"),
        ("MediaFiles", "IX_MediaFiles_Artist", false, "Artist"),
        ("MediaFiles", "IX_MediaFiles_FilePath", true, "FilePath"),
        ("MediaFiles", "IX_MediaFiles_ScanSourceId", false, "ScanSourceId"),
        ("MediaIdentities", "IX_MediaIdentities_MediaFileId_Provider", true, "MediaFileId, Provider"),
        ("MediaIdentities", "IX_MediaIdentities_Provider_RecordingId", false, "Provider, RecordingId"),
        ("MediaTags", "IX_MediaTags_MediaFileId_Namespace_Key", true, "MediaFileId, Namespace, Key"),
        ("MusicEnrichments", "IX_MusicEnrichments_MediaFileId_CreatedAt", false, "MediaFileId, CreatedAt"),
        ("PlayHistories", "IX_PlayHistories_MediaFileId", false, "MediaFileId"),
        ("PlayHistories", "IX_PlayHistories_UserId", false, "UserId"),
        ("PlaylistSongs", "IX_PlaylistSongs_MediaFileId", false, "MediaFileId"),
        ("PlaylistSongs", "IX_PlaylistSongs_PlaylistId", false, "PlaylistId"),
        ("ScanSources", "IX_ScanSources_StorageCredentialId", false, "StorageCredentialId"),
        ("TagEvidences", "IX_TagEvidences_MediaTagId", false, "MediaTagId"),
        ("Users", "IX_Users_Username", true, "Username")
    };

    // Expected defaults: (Table, Column, SubstringMatch)
    public static readonly (string Table, string Column, string DefaultContains)[] ExpectedDefaults = new[]
    {
        ("Users", "IsAdmin", "false"),
        ("Playlists", "Type", "normal"),
        ("EnrichmentJobs", "SongIdsJson", "[]"),
        ("Plugins", "IsEnabled", "true")
    };

    public class VerificationResult
    {
        public bool Success { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<string> VerifiedTables { get; set; } = new();
    }

    public static VerificationResult VerifyFingerprint(AppDbContext db)
    {
        var result = new VerificationResult();
        var conn = db.Database.GetDbConnection();
        var shouldClose = false;

        try
        {
            if (conn.State != ConnectionState.Open)
            {
                conn.Open();
                shouldClose = true;
            }

            if (db.Database.IsNpgsql())
            {
                // 1. Verify all 16 tables exist in public schema
                using var cmdTables = conn.CreateCommand();
                cmdTables.CommandText = @"
                    SELECT table_name
                    FROM information_schema.tables
                    WHERE table_schema = 'public' AND table_type = 'BASE TABLE'
                      AND table_name != '__EFMigrationsHistory';";

                var existingTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var reader = cmdTables.ExecuteReader())
                {
                    while (reader.Read()) existingTables.Add(reader.GetString(0));
                }

                foreach (var table in RequiredTables)
                {
                    if (!existingTables.Contains(table))
                    {
                        result.Errors.Add($"Missing required table: '{table}'");
                    }
                    else
                    {
                        result.VerifiedTables.Add(table);
                    }
                }

                // 2. Comprehensive column-by-column equality verification across all 16 tables
                using var cmdCols = conn.CreateCommand();
                cmdCols.CommandText = @"
                    SELECT table_name, column_name, data_type, is_nullable, column_default
                    FROM information_schema.columns
                    WHERE table_schema = 'public';";

                var actualCols = new Dictionary<string, (string DataType, bool IsNullable, string? DefaultVal)>(StringComparer.OrdinalIgnoreCase);
                using (var reader = cmdCols.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var key = $"{reader.GetString(0)}.{reader.GetString(1)}";
                        var dt = reader.GetString(2);
                        var isNull = reader.GetString(3).Equals("YES", StringComparison.OrdinalIgnoreCase);
                        var def = reader.IsDBNull(4) ? null : reader.GetString(4);
                        actualCols[key] = (dt, isNull, def);
                    }
                }

                foreach (var kvp in ExpectedColumns)
                {
                    var tableName = kvp.Key;
                    var expectedColNames = kvp.Value.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

                    // Check for missing columns and property mismatches
                    foreach (var expCol in kvp.Value)
                    {
                        var colKey = $"{tableName}.{expCol.Name}";
                        if (!actualCols.TryGetValue(colKey, out var act))
                        {
                            result.Errors.Add($"Missing column '{colKey}' on table '{tableName}'.");
                            continue;
                        }

                        if (!act.DataType.Equals(expCol.DataType, StringComparison.OrdinalIgnoreCase))
                        {
                            result.Errors.Add($"Column '{colKey}' data type mismatch: expected '{expCol.DataType}', got '{act.DataType}'.");
                        }

                        if (act.IsNullable != expCol.IsNullable)
                        {
                            result.Errors.Add($"Column '{colKey}' nullability mismatch: expected is_nullable={expCol.IsNullable}, got {act.IsNullable}.");
                        }
                    }

                    // Equality check: Reject unexpected extra columns on baseline tables
                    var actualColNamesForTable = actualCols.Keys
                        .Where(k => k.StartsWith($"{tableName}.", StringComparison.OrdinalIgnoreCase))
                        .Select(k => k.Substring(tableName.Length + 1))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    var extraCols = actualColNamesForTable.Except(expectedColNames).ToList();
                    foreach (var extra in extraCols)
                    {
                        result.Errors.Add($"Table '{tableName}' contains unexpected extra column: '{extra}'.");
                    }
                }

                // 3. Verify primary keys exist for each required table
                using var cmdPk = conn.CreateCommand();
                cmdPk.CommandText = @"
                    SELECT tc.table_name, ccu.column_name
                    FROM information_schema.table_constraints tc
                    JOIN information_schema.constraint_column_usage ccu ON tc.constraint_name = ccu.constraint_name
                    WHERE tc.table_schema = 'public' AND tc.constraint_type = 'PRIMARY KEY';";

                var tablePks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var reader = cmdPk.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        tablePks.Add($"{reader.GetString(0)}.{reader.GetString(1)}");
                    }
                }

                foreach (var table in RequiredTables)
                {
                    if (!tablePks.Contains($"{table}.Id"))
                    {
                        result.Errors.Add($"Table '{table}' is missing expected PRIMARY KEY constraint on 'Id'.");
                    }
                }

                // 4. Verify all expected Foreign Keys exist with correct target and delete rule
                using var cmdFk = conn.CreateCommand();
                cmdFk.CommandText = @"
                    SELECT
                        kcu.table_name AS source_table,
                        kcu.column_name AS source_column,
                        ccu.table_name AS target_table,
                        ccu.column_name AS target_column,
                        rc.delete_rule
                    FROM information_schema.table_constraints tc
                    JOIN information_schema.key_column_usage kcu
                        ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema
                    JOIN information_schema.constraint_column_usage ccu
                        ON ccu.constraint_name = tc.constraint_name AND ccu.table_schema = tc.table_schema
                    JOIN information_schema.referential_constraints rc
                        ON rc.constraint_name = tc.constraint_name AND rc.constraint_schema = tc.table_schema
                    WHERE tc.constraint_type = 'FOREIGN KEY' AND tc.table_schema = 'public';";

                var actualFks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                using (var reader = cmdFk.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var fkKey = $"{reader.GetString(0)}.{reader.GetString(1)}->{reader.GetString(2)}.{reader.GetString(3)}";
                        var delRule = reader.GetString(4);
                        actualFks[fkKey] = delRule;
                    }
                }

                var expectedFkKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var fk in ExpectedForeignKeys)
                {
                    var fkKey = $"{fk.SourceTable}.{fk.SourceCol}->{fk.TargetTable}.{fk.TargetCol}";
                    expectedFkKeys.Add(fkKey);

                    if (!actualFks.TryGetValue(fkKey, out var actualDelRule))
                    {
                        result.Errors.Add($"Missing foreign key: {fkKey}");
                    }
                    else if (!actualDelRule.Equals(fk.DeleteRule, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Errors.Add($"Foreign key '{fkKey}' delete rule mismatch: expected '{fk.DeleteRule}', got '{actualDelRule}'.");
                    }
                }

                // Reject unexpected foreign keys originating from baseline tables
                foreach (var actualFk in actualFks)
                {
                    var sourceTable = actualFk.Key.Split('.')[0];
                    if (RequiredTables.Contains(sourceTable, StringComparer.OrdinalIgnoreCase) && !expectedFkKeys.Contains(actualFk.Key))
                    {
                        result.Errors.Add($"Table '{sourceTable}' contains unexpected extra foreign key: '{actualFk.Key}'.");
                    }
                }

                // 5. Verify all expected indexes exist in pg_indexes with exact uniqueness and column list
                using var cmdIdx = conn.CreateCommand();
                cmdIdx.CommandText = @"
                    SELECT
                        t.relname AS table_name,
                        i.relname AS index_name,
                        ix.indisunique AS is_unique,
                        string_agg(a.attname, ', ' ORDER BY array_position(ix.indkey, a.attnum)) AS column_names
                    FROM pg_class t
                    JOIN pg_index ix ON t.oid = ix.indrelid
                    JOIN pg_class i ON i.oid = ix.indexrelid
                    JOIN pg_namespace n ON n.oid = t.relnamespace
                    JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = ANY(ix.indkey)
                    WHERE n.nspname = 'public' AND NOT ix.indisprimary
                      AND t.relname IN ('EnrichmentAttempts', 'EnrichmentJobs', 'Favorites', 'Lyrics', 'MediaFiles',
                                        'MediaIdentities', 'MediaTags', 'MusicEnrichments', 'PlayHistories',
                                        'PlaylistSongs', 'Playlists', 'Plugins', 'ScanSources', 'StorageCredentials',
                                        'TagEvidences', 'Users')
                    GROUP BY t.relname, i.relname, ix.indisunique;";

                var actualIndexes = new Dictionary<string, (bool IsUnique, string Columns)>(StringComparer.OrdinalIgnoreCase);
                using (var reader = cmdIdx.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var idxKey = $"{reader.GetString(0)}.{reader.GetString(1)}";
                        var isUniq = reader.GetBoolean(2);
                        var cols = reader.GetString(3);
                        actualIndexes[idxKey] = (isUniq, cols);
                    }
                }

                var expectedIndexKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var idx in ExpectedIndexes)
                {
                    var idxKey = $"{idx.Table}.{idx.IndexName}";
                    expectedIndexKeys.Add(idxKey);

                    if (!actualIndexes.TryGetValue(idxKey, out var actIdx))
                    {
                        result.Errors.Add($"Missing index: '{idx.IndexName}' on table '{idx.Table}'.");
                    }
                    else
                    {
                        if (actIdx.IsUnique != idx.IsUnique)
                        {
                            result.Errors.Add($"Index '{idxKey}' uniqueness mismatch: expected is_unique={idx.IsUnique}, got {actIdx.IsUnique}.");
                        }
                        if (!actIdx.Columns.Equals(idx.Columns, StringComparison.OrdinalIgnoreCase))
                        {
                            result.Errors.Add($"Index '{idxKey}' columns mismatch: expected '({idx.Columns})', got '({actIdx.Columns})'.");
                        }
                    }
                }

                // Reject unexpected extra indexes on baseline tables
                foreach (var actualIdx in actualIndexes)
                {
                    var tbl = actualIdx.Key.Split('.')[0];
                    if (RequiredTables.Contains(tbl, StringComparer.OrdinalIgnoreCase) && !expectedIndexKeys.Contains(actualIdx.Key))
                    {
                        result.Errors.Add($"Table '{tbl}' contains unexpected extra index: '{actualIdx.Key}'.");
                    }
                }

                // 6. Verify key column defaults
                foreach (var def in ExpectedDefaults)
                {
                    var colKey = $"{def.Table}.{def.Column}";
                    if (actualCols.TryGetValue(colKey, out var act))
                    {
                        if (string.IsNullOrEmpty(act.DefaultVal) || !act.DefaultVal.Contains(def.DefaultContains, StringComparison.OrdinalIgnoreCase))
                        {
                            result.Errors.Add($"Column '{colKey}' default mismatch: expected substring '{def.DefaultContains}', got '{act.DefaultVal}'.");
                        }
                    }
                }
            }
            else if (db.Database.IsSqlite())
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";
                var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read()) tables.Add(reader.GetString(0));
                }

                foreach (var table in RequiredTables)
                {
                    if (!tables.Contains(table))
                    {
                        result.Errors.Add($"Missing SQLite table: '{table}'");
                    }
                    else
                    {
                        result.VerifiedTables.Add(table);
                    }
                }
            }

            result.Success = result.Errors.Count == 0;
            return result;
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Database connection or query error: {ex.Message}");
            result.Success = false;
            return result;
        }
        finally
        {
            if (shouldClose) conn.Close();
        }
    }

    public static void ApplyBaseline(AppDbContext db)
    {
        if (db.Database.IsNpgsql())
        {
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE IF NOT EXISTS ""__EFMigrationsHistory"" (
                    ""MigrationId"" character varying(150) NOT NULL PRIMARY KEY,
                    ""ProductVersion"" character varying(32) NOT NULL
                );
                INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                VALUES ('" + BaselineMigrationId + @"', '" + ProductVersion + @"')
                ON CONFLICT (""MigrationId"") DO NOTHING;");
        }
        else if (db.Database.IsSqlite())
        {
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE IF NOT EXISTS ""__EFMigrationsHistory"" (
                    ""MigrationId"" TEXT NOT NULL PRIMARY KEY,
                    ""ProductVersion"" TEXT NOT NULL
                );
                INSERT OR IGNORE INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                VALUES ('" + BaselineMigrationId + @"', '" + ProductVersion + @"');");
        }
    }
}
