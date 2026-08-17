using Microsoft.Data.Sqlite;
using TransReader.Core.Library;

namespace TransReader.Core.Tests;

public sealed class SchemaMigratorTests
{
    private static SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    [Fact]
    public async Task ColumnExists_DetectsPresenceAndAbsence()
    {
        await using var connection = OpenConnection();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE t (a TEXT, b TEXT)";
            await command.ExecuteNonQueryAsync();
        }

        Assert.True(await SchemaMigrator.ColumnExistsAsync(connection, "t", "a"));
        Assert.True(await SchemaMigrator.ColumnExistsAsync(connection, "t", "B"));
        Assert.False(await SchemaMigrator.ColumnExistsAsync(connection, "t", "missing"));
    }

    [Fact]
    public async Task AlterAddColumn_IsIdempotent()
    {
        await using var connection = OpenConnection();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE t (a TEXT)";
            await command.ExecuteNonQueryAsync();
        }

        await SchemaMigrator.AlterAddColumnAsync(connection, "t", "x", "TEXT NOT NULL DEFAULT ''");
        await SchemaMigrator.AlterAddColumnAsync(connection, "t", "x", "TEXT NOT NULL DEFAULT ''"); // 幂等：不抛

        Assert.True(await SchemaMigrator.ColumnExistsAsync(connection, "t", "x"));
    }

    [Fact]
    public async Task RunAsync_AppliesOnlyStepsWithinRequestedRangeInOrder()
    {
        await using var connection = OpenConnection();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE t (a TEXT)";
            await command.ExecuteNonQueryAsync();
        }

        var steps = new[]
        {
            new MigrationStep(1, "add-col2", (c, ct) => SchemaMigrator.AlterAddColumnAsync(c, "t", "col2", "TEXT", ct)),
            new MigrationStep(2, "add-col3", (c, ct) => SchemaMigrator.AlterAddColumnAsync(c, "t", "col3", "TEXT", ct))
        };

        await SchemaMigrator.RunAsync(connection, fromVersion: 1, toVersion: 3, steps);

        Assert.True(await SchemaMigrator.ColumnExistsAsync(connection, "t", "col2"));
        Assert.True(await SchemaMigrator.ColumnExistsAsync(connection, "t", "col3"));

        // 在全新表上只跑 2→3：col2 不应出现，col3 应出现。
        await using var connection2 = OpenConnection();
        await using (var command = connection2.CreateCommand())
        {
            command.CommandText = "CREATE TABLE t (a TEXT)";
            await command.ExecuteNonQueryAsync();
        }
        await SchemaMigrator.RunAsync(connection2, fromVersion: 2, toVersion: 3, steps);
        Assert.False(await SchemaMigrator.ColumnExistsAsync(connection2, "t", "col2"));
        Assert.True(await SchemaMigrator.ColumnExistsAsync(connection2, "t", "col3"));
    }

    [Fact]
    public async Task RunAsync_NoopWhenAlreadyAtTargetVersion()
    {
        await using var connection = OpenConnection();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE t (a TEXT)";
            await command.ExecuteNonQueryAsync();
        }
        var called = false;
        var steps = new[]
        {
            new MigrationStep(1, "noop", (_, _) => { called = true; return Task.CompletedTask; })
        };
        await SchemaMigrator.RunAsync(connection, fromVersion: 2, toVersion: 3, steps);
        Assert.False(called);
    }
}
