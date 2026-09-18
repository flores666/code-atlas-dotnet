using CodeAtlas.Core.Model;

namespace CodeAtlas.Desktop.ViewModels;

/// <summary>
/// A source file, opened read-only in the viewer.
/// </summary>
/// <remarks>
/// Reading the file rather than reconstructing it from the index is the point: the index
/// knows where a declaration is, and what a reader then wants is the code as it is on
/// disk, including everything CodeAtlas does not model.
/// </remarks>
public sealed class SourceViewModel
{
    /// <summary>
    /// Files past this are not opened. A generated file of several megabytes is not
    /// something anyone reads, and loading it would stall the window.
    /// </summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    private SourceViewModel(string filePath, string breadcrumb, string text, int line)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        Breadcrumb = breadcrumb;
        Text = text;
        Line = line;
        LineCount = text.AsSpan().Count('\n') + 1;
    }

    public string FilePath { get; }

    public string FileName { get; }

    /// <summary>The path as a trail, relative to the workspace it was opened from.</summary>
    public string Breadcrumb { get; }

    public string Text { get; }

    /// <summary>The 1-based line to reveal: the declaration this file was opened for.</summary>
    public int Line { get; }

    public int LineCount { get; }

    /// <summary>
    /// Reads the file a symbol is declared in, or returns <c>null</c> when there is
    /// nothing to show. A file that has been moved or deleted since indexing is the
    /// ordinary case here, not an error.
    /// </summary>
    public static SourceViewModel? Load(IndexedSymbol symbol, string? workspaceDirectory)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        if (symbol.FilePath is not { Length: > 0 } path)
        {
            return null;
        }

        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                return null;
            }

            var text = file.Length > MaxBytes
                ? $"// {file.Name} is {file.Length / (1024 * 1024)} MB, which is too large to open here."
                : File.ReadAllText(path);

            return new SourceViewModel(path, Trail(path, workspaceDirectory), text, symbol.Line ?? 1);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Trail(string path, string? workspaceDirectory)
    {
        var relative = workspaceDirectory is { Length: > 0 }
            ? Path.GetRelativePath(workspaceDirectory, path)
            : path;

        // A path that climbed out of the workspace is not a trail through it.
        if (relative.StartsWith("..", StringComparison.Ordinal))
        {
            relative = path;
        }

        return string.Join("  ›  ", relative.Split(Path.DirectorySeparatorChar));
    }
}
