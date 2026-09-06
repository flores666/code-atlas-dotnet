using System.Diagnostics;

namespace CodeAtlas.Core.Workspace;

/// <summary>
/// Opens a source file in an external editor. CodeAtlas only ever reads the repository;
/// what the editor then does is the user's business.
/// </summary>
public static class SourceLauncher
{
    /// <summary>
    /// Names an editor command line, so a file can be opened at a specific line. The
    /// placeholders <c>{file}</c> and <c>{line}</c> are substituted, for example
    /// <c>code --goto {file}:{line}</c>. Without it the file opens at the top in
    /// whichever application the OS associates with it.
    /// </summary>
    public const string EditorEnvironmentVariable = "CODEATLAS_EDITOR";

    /// <summary>Opens <paramref name="filePath"/>, positioned at <paramref name="line"/> when the editor supports it.</summary>
    /// <returns><c>null</c> on success, otherwise a message describing why nothing opened.</returns>
    public static string? Open(string filePath, int? line)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return "This symbol has no source file.";
        }

        if (!File.Exists(filePath))
        {
            return $"The source file no longer exists: {filePath}";
        }

        try
        {
            var editor = Environment.GetEnvironmentVariable(EditorEnvironmentVariable);

            using var _ = string.IsNullOrWhiteSpace(editor)
                ? Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true })
                : Process.Start(BuildEditorStartInfo(editor, filePath, line));

            return null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return $"Could not open '{filePath}': {e.Message}";
        }
    }

    private static ProcessStartInfo BuildEditorStartInfo(string editor, string filePath, int? line)
    {
        var parts = editor.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var startInfo = new ProcessStartInfo(parts[0]) { UseShellExecute = false };

        foreach (var part in parts.Skip(1))
        {
            startInfo.ArgumentList.Add(part
                .Replace("{file}", filePath, StringComparison.Ordinal)
                .Replace("{line}", (line ?? 1).ToString(), StringComparison.Ordinal));
        }

        // An editor command that names no placeholder still needs the file itself.
        if (!editor.Contains("{file}", StringComparison.Ordinal))
        {
            startInfo.ArgumentList.Add(filePath);
        }

        return startInfo;
    }
}
