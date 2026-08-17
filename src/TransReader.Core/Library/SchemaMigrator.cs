using Microsoft.Data.Sqlite;

namespace TransReader.Core.Library;

internal readonly record struct MigrationStep(int FromVersion, string Name, Func<SqliteConnection, CancellationToken, Task> Apply);

/// <summary>
/// SQLite schema 迁移 runner：按版本号顺序跑幂等步骤。ALTER ADD COLUMN 对已存在列会抛异常，
/// 故每步加列前必须先查 pragma 判断列是否存在。新增列时在 <see cref="BuiltInSteps"/> 追加
/// (fromVersion, "add-xxx", (c, ct) => AlterAddColumnAsync(c, "documents", "xxx", "TEXT", ct))。
/// </summary>
internal static class SchemaMigrator
{
    /// <summary>内置迁移步骤（v1→v2、v2→v3…）。当前 schema 版本为 2。</summary>
    public static IReadOnlyList<MigrationStep> BuiltInSteps { get; } =
    [
        new MigrationStep(1, "add-documents-domain", (c, ct) => AlterAddColumnAsync(c, "documents", "domain", "TEXT", ct))
    ];

    /// <summary>运行 fromVersion..toVersion（不含）区间内的步骤，按 FromVersion 升序。</summary>
    public static async Task RunAsync(
        SqliteConnection connection,
        int fromVersion,
        int toVersion,
        IReadOnlyList<MigrationStep>? steps = null,
        CancellationToken cancellationToken = default)
    {
        if (toVersion <= fromVersion) return;
        var ordered = (steps ?? BuiltInSteps)
            .Where(step => step.FromVersion >= fromVersion && step.FromVersion < toVersion)
            .OrderBy(step => step.FromVersion)
            .ThenBy(step => step.Name, StringComparer.Ordinal)
            .ToList();
        foreach (var step in ordered)
        {
            await step.Apply(connection, cancellationToken);
        }
    }

    /// <summary>判断表是否已有该列（SQLite ALTER ADD COLUMN 对已存在列会报错，必须先查）。</summary>
    public static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        string table,
        string column,
        CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        // PRAGMA 不接受绑定参数；表名来自受控常量，不来自用户输入，故用插值。
        command.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            // PRAGMA table_info 第 2 列（索引 1）为列名。
            if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>幂等加列：列已存在则跳过，不存在则 ALTER ADD COLUMN。表名/列名/类型来自受控常量，不来自用户输入。</summary>
    public static async Task AlterAddColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        string typeDefinition,
        CancellationToken cancellationToken = default)
    {
        if (await ColumnExistsAsync(connection, table, column, cancellationToken))
        {
            return;
        }
        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {typeDefinition}";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
