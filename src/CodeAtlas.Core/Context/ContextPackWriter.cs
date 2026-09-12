using System.IO.Compression;
using System.Text;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Core.Context;

/// <summary>
/// Gets a rendered context pack out of CodeAtlas: as one block of text, as a folder, or as
/// a zip.
/// </summary>
/// <remarks>
/// Every destination is one the reader named. Nothing is uploaded, and the analysed
/// repository is untouched — a pack is written where it is asked for and nowhere else.
/// </remarks>
public static class ContextPackWriter
{
    /// <summary>
    /// The pack as a single block of text, for pasting into an agent that has no files.
    /// </summary>
    /// <remarks>
    /// The folder structure survives as a header per file, because the paths are part of
    /// what the pack says: an agent that is told which file it is reading can quote a
    /// location back.
    /// </remarks>
    public static string ToText(IReadOnlyList<ContextPackFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var text = new StringBuilder();

        foreach (var file in files)
        {
            text.Append("===== ").Append(ContextDocuments.FolderName).Append('/').Append(file.Path)
                .AppendLine(" =====").AppendLine()
                .AppendLine(file.Text.TrimEnd())
                .AppendLine();
        }

        return text.ToString();
    }

    /// <summary>
    /// Writes the pack into <c>&lt;directory&gt;/ContextPack</c>.
    /// </summary>
    /// <returns>The folder that was written.</returns>
    public static string WriteDirectory(IReadOnlyList<ContextPackFile> files, string directory)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var root = Path.Combine(directory, ContextDocuments.FolderName);

        foreach (var file in files)
        {
            var destination = Resolve(root, file.Path);

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, file.Text);
        }

        return root;
    }

    /// <summary>Writes the pack as a zip whose single top-level folder is <c>ContextPack</c>.</summary>
    public static string WriteZip(IReadOnlyList<ContextPackFile> files, string zipPath)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);

        if (Path.GetDirectoryName(zipPath) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        foreach (var file in files)
        {
            var entry = archive.CreateEntry(
                $"{ContextDocuments.FolderName}/{file.Path}",
                CompressionLevel.Optimal);

            using var writer = new StreamWriter(entry.Open());
            writer.Write(file.Text);
        }

        return zipPath;
    }

    /// <summary>
    /// Resolves a pack-relative path under <paramref name="root"/>, refusing anything that
    /// would land outside it.
    /// </summary>
    /// <remarks>
    /// Pack paths are derived from file paths on disk, so they are not arbitrary — but a
    /// path that escaped the export folder would write over something the reader did not
    /// choose, and that is not a failure mode worth leaving to the derivation.
    /// </remarks>
    private static string Resolve(string root, string packPath)
    {
        var full = Path.GetFullPath(Path.Combine(root, packPath));
        var prefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;

        return full.StartsWith(prefix, StringComparison.Ordinal)
            ? full
            : throw new InvalidOperationException($"'{packPath}' does not sit inside the pack folder.");
    }
}
