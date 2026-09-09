using System.Diagnostics;
using System.Globalization;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Core.Diff;

/// <summary>The files a diff touched, or why it could not be read.</summary>
public sealed record GitDiff(IReadOnlyList<ChangedFile> Files, string? Error = null);

/// <summary>
/// Reads the working tree's diff by asking Git for it.
/// </summary>
/// <remarks>
/// <para>
/// One read-only command, run in the repository and never against it: <c>git diff</c>
/// writes nothing, and no other Git operation is invoked. The repository itself stays
/// untouched, which is the promise the rest of CodeAtlas makes too.
/// </para>
/// <para>
/// <c>--unified=0</c> asks for hunks with no context, so every line reported really did
/// change; a hunk with context would drag the neighbouring declarations in with it. A
/// pure deletion reports zero new lines, and is recorded against the line it was removed
/// from — that is where the change is visible in the file as it stands now.
/// </para>
/// </remarks>
public static class GitDiffReader
{
    /// <summary>What the working tree is compared against unless a caller says otherwise.</summary>
    public const string DefaultAgainst = "HEAD";

    public static async Task<GitDiff> ReadAsync(
        string repositoryRoot,
        string against = DefaultAgainst,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(against);

        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in (string[])
                 ["--no-pager", "diff", "--unified=0", "--no-color", "--no-ext-diff", against])
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException("Git did not start.");

            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);

            await Task.WhenAll(output, error).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            return process.ExitCode == 0
                ? new GitDiff(Parse(await output.ConfigureAwait(false)))
                : new GitDiff([], Message(await error.ConfigureAwait(false), against));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new GitDiff([], $"Could not run Git: {e.Message}");
        }
    }

    private static string Message(string error, string against) =>
        error.Trim() is { Length: > 0 } reported
            ? reported
            : $"Git could not diff the working tree against '{against}'.";

    /// <summary>
    /// Turns unified diff text into the lines each file now has that were not there before.
    /// </summary>
    /// <remarks>
    /// The new-side path is taken from the <c>+++</c> header, and only while a file header
    /// is open: with no context lines, added content can start with anything, so position
    /// is what tells a header from a line of code. A file deleted outright has no new side
    /// and contributes nothing — there is no symbol left to relate.
    /// </remarks>
    private static List<ChangedFile> Parse(string diff)
    {
        var files = new List<ChangedFile>();
        var lines = new List<LineRange>();
        string? path = null;
        var inHeader = false;

        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                Flush();
                inHeader = true;
            }
            else if (inHeader && line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                path = NewPath(line[4..].TrimEnd('\r'));
                inHeader = false;
            }
            else if (path is not null && line.StartsWith("@@", StringComparison.Ordinal) &&
                     HunkRange(line) is { } range)
            {
                lines.Add(range);
            }
        }

        Flush();
        return files;

        void Flush()
        {
            if (path is not null && lines.Count > 0)
            {
                files.Add(new ChangedFile(path, [.. lines]));
            }

            path = null;
            lines.Clear();
        }
    }

    private static string? NewPath(string header) =>
        header switch
        {
            "/dev/null" => null,
            ['b', '/', .. var relative] => relative,
            var other => other,
        };

    /// <summary>The new-side range of <c>@@ -old,count +new,count @@</c>.</summary>
    private static LineRange? HunkRange(string header)
    {
        var start = header.IndexOf('+', StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var text = header[(start + 1)..];
        var end = text.IndexOf(' ', StringComparison.Ordinal);
        var span = end < 0 ? text : text[..end];

        var parts = span.Split(',');
        if (!int.TryParse(parts[0], CultureInfo.InvariantCulture, out var first))
        {
            return null;
        }

        var count = parts.Length > 1 && int.TryParse(parts[1], CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 1;

        // A hunk that only removed lines names the line it was removed from, which is
        // where the gap now sits. Clamped: a deletion at the top of a file reports 0.
        return count == 0
            ? new LineRange(Math.Max(1, first), Math.Max(1, first))
            : new LineRange(first, first + count - 1);
    }
}
