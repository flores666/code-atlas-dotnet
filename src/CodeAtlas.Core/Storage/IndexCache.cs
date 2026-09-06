using System.Security.Cryptography;
using System.Text;

namespace CodeAtlas.Core.Storage;

/// <summary>
/// Locates the local cache file for a workspace. Caches live under the user's local
/// application data — never beside the analysed repository, which stays untouched.
/// </summary>
public static class IndexCache
{
    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodeAtlas.NET",
        "index");

    /// <summary>
    /// A stable per-workspace path. The hash keeps two solutions of the same name apart;
    /// the readable prefix keeps the cache directory browsable.
    /// </summary>
    public static string GetDatabasePath(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var fullPath = Path.GetFullPath(sourcePath);
        var key = OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];

        return Path.Combine(RootDirectory, $"{Sanitize(Path.GetFileNameWithoutExtension(fullPath))}-{hash}.db");
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);

        foreach (var c in name)
        {
            builder.Append(invalid.Contains(c) ? '_' : c);
        }

        return builder.Length == 0 ? "workspace" : builder.ToString();
    }
}
