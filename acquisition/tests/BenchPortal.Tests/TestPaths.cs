namespace BenchPortal.Tests;

internal static class TestPaths
{
    /// <summary>Path of a file in the repository's shared config/ folder.</summary>
    public static string Config(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "config")))
        {
            dir = dir.Parent;
        }
        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("config/ not found"), "config", name);
    }

    public static string TempDatabase() => $"Data Source={Path.Combine(Path.GetTempPath(), $"bench_test_{Guid.NewGuid():N}.db")}";
}
