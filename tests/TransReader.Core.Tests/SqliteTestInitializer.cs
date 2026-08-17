using System.Runtime.CompilerServices;

namespace TransReader.Core.Tests;

internal static class SqliteTestInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
    }
}
