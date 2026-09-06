using System;
using Microsoft.EntityFrameworkCore;
using WebMusic.Backend.Data;
using WebMusic.Backend.Services;
using Xunit;

namespace WebMusic.Backend.Tests;

public class SchemaFingerprintVerifierTests
{
    [Fact]
    public void VerifyFingerprint_PassesWhenAllTablesExist()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared")
            .Options;

        using var db = new AppDbContext(options);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();

        var result = SchemaFingerprintVerifier.VerifyFingerprint(db);
        Assert.True(result.Success);
        Assert.Equal(16, result.VerifiedTables.Count);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ApplyBaseline_RecordsBaselineMigrationRecord()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared")
            .Options;

        using var db = new AppDbContext(options);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();

        SchemaFingerprintVerifier.ApplyBaseline(db);

        using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '" + SchemaFingerprintVerifier.BaselineMigrationId + "';";
        var count = Convert.ToInt32(cmd.ExecuteScalar());
        Assert.Equal(1, count);
    }
}
