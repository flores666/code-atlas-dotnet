using System.Text.Json;

namespace CodeAtlas.Core.Storage;

/// <summary>
/// The most recently opened workspaces, newest first, persisted next to the index cache.
/// </summary>
public sealed class RecentWorkspaces
{
    public const int MaxEntries = 10;

    private readonly string _filePath;

    public RecentWorkspaces(string? filePath = null) =>
        _filePath = filePath ?? Path.Combine(IndexCache.RootDirectory, "recent.json");

    /// <summary>
    /// Reads the list, dropping entries whose file has since been deleted or moved.
    /// A missing or unreadable file simply yields an empty list.
    /// </summary>
    public IReadOnlyList<string> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_filePath)) is { } entries
                ? entries.Where(File.Exists).Take(MaxEntries).ToList()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>Moves <paramref name="path"/> to the front and returns the new list.</summary>
    public IReadOnlyList<string> Add(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var entries = Load()
            .Where(e => !string.Equals(e, fullPath, StringComparison.OrdinalIgnoreCase))
            .Prepend(fullPath)
            .Take(MaxEntries)
            .ToList();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(entries));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A recent-list that cannot be saved must never block opening a workspace.
        }

        return entries;
    }
}
