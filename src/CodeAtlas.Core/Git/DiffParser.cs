using CodeAtlas.Core.Model;

namespace CodeAtlas.Core.Git;

/// <summary>
/// Turns a unified diff into per-file hunks with new-side line numbers.
/// </summary>
/// <remarks>
/// The line numbers are derived by walking each hunk body rather than trusting the range
/// in its header, which is what keeps the result correct at any context setting and gives
/// removals a position in new-file coordinates.
/// </remarks>
public static class DiffParser
{
    private const string FileMarker = "diff --git ";

    public static IReadOnlyList<FileDiff> Parse(string diff)
    {
        if (string.IsNullOrEmpty(diff))
        {
            return [];
        }

        var lines = diff.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var files = new List<FileDiff>();
        var start = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith(FileMarker, StringComparison.Ordinal))
            {
                continue;
            }

            if (start >= 0)
            {
                files.Add(ParseFile(lines[start..i]));
            }

            start = i;
        }

        if (start >= 0)
        {
            files.Add(ParseFile(lines[start..]));
        }

        return files;
    }

    private static FileDiff ParseFile(ReadOnlySpan<string> section)
    {
        string? newPath = null;
        string? oldPath = null;
        var binary = false;
        var hunks = new List<DiffHunk>();

        for (var i = 0; i < section.Length; i++)
        {
            var line = section[i];

            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                oldPath = StripPrefix(line[4..]);
            }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                newPath = StripPrefix(line[4..]);
            }
            else if (line.StartsWith("Binary files", StringComparison.Ordinal) ||
                     line.StartsWith("GIT binary patch", StringComparison.Ordinal))
            {
                binary = true;
            }
            else if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                var end = i + 1;
                while (end < section.Length && !section[end].StartsWith("@@", StringComparison.Ordinal) &&
                       !section[end].StartsWith(FileMarker, StringComparison.Ordinal))
                {
                    end++;
                }

                if (ParseHunk(line, section[(i + 1)..end]) is { } hunk)
                {
                    hunks.Add(hunk);
                }

                i = end - 1;
            }
        }

        // A deletion has no new side, so the old path is the only name it has. The header
        // is the fallback for a rename Git wrote without either marker line.
        var path = newPath ?? oldPath ?? PathFromHeader(section[0]) ?? string.Empty;

        return new FileDiff
        {
            Path = path,
            OldPath = oldPath is not null && !string.Equals(oldPath, path, StringComparison.Ordinal)
                ? oldPath
                : null,
            IsBinary = binary,
            Hunks = hunks,
            Text = string.Join('\n', section),
        };
    }

    /// <summary>
    /// Reads one hunk, recording which new-side lines it touches.
    /// </summary>
    /// <remarks>
    /// A <c>+</c> line is at the cursor and advances it. A <c>-</c> line has no new-side
    /// line of its own, so it records the cursor — the line that now sits where it was —
    /// and leaves the cursor where it is. That is what lets a pure deletion still land
    /// inside the declaration it was deleted from.
    /// </remarks>
    private static DiffHunk? ParseHunk(string header, ReadOnlySpan<string> body)
    {
        if (!TryParseHeader(header, out var oldStart, out var oldCount, out var newStart, out var newCount))
        {
            return null;
        }

        var touched = new List<int>();
        var added = new HashSet<int>();
        var removed = 0;
        var cursor = newStart;

        foreach (var line in body)
        {
            // Only the "no newline" marker is skipped. The ---/+++ header lines cannot
            // occur here — they always precede the first @@ — and skipping them would
            // silently drop a real added line whose content starts with "++", such as
            // "++counter;".
            if (line.StartsWith(@"\ No newline", StringComparison.Ordinal))
            {
                continue;
            }

            switch (line.Length == 0 ? ' ' : line[0])
            {
                case '+':
                    touched.Add(cursor);
                    added.Add(cursor);
                    cursor++;
                    break;

                case '-':
                    removed++;

                    // Clamped because a removal at the very top of a file reports a
                    // new-side start of 0, and there is no line 0 to attribute it to.
                    touched.Add(Math.Max(1, cursor));
                    break;

                default:
                    cursor++;
                    break;
            }
        }

        return new DiffHunk(
            header,
            oldStart,
            oldCount,
            newStart,
            newCount,
            touched.Distinct().Order().ToList(),
            added,
            removed);
    }

    /// <summary>Reads <c>@@ -oldStart,oldCount +newStart,newCount @@</c>; a count may be implicit.</summary>
    private static bool TryParseHeader(
        string header,
        out int oldStart,
        out int oldCount,
        out int newStart,
        out int newCount)
    {
        oldStart = oldCount = newStart = newCount = 0;

        var open = header.IndexOf("@@", StringComparison.Ordinal);
        var close = header.IndexOf("@@", open + 2, StringComparison.Ordinal);
        if (open < 0 || close < 0)
        {
            return false;
        }

        var ranges = header[(open + 2)..close].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var old = ranges.FirstOrDefault(range => range.StartsWith('-'));
        var @new = ranges.FirstOrDefault(range => range.StartsWith('+'));

        return old is not null && @new is not null &&
               TryParseRange(old[1..], out oldStart, out oldCount) &&
               TryParseRange(@new[1..], out newStart, out newCount);
    }

    private static bool TryParseRange(string range, out int start, out int count)
    {
        var comma = range.IndexOf(',');

        if (comma < 0)
        {
            count = 1;
            return int.TryParse(range, out start);
        }

        count = 0;
        return int.TryParse(range[..comma], out start) && int.TryParse(range[(comma + 1)..], out count);
    }

    /// <summary>
    /// Strips the <c>a/</c> or <c>b/</c> Git prefixes and the quoting it applies to a path
    /// containing unusual characters.
    /// </summary>
    private static string? StripPrefix(string value)
    {
        var path = value.Trim();

        if (path is "/dev/null")
        {
            return null;
        }

        if (path.Length > 1 && path[0] is 'a' or 'b' && path[1] == '/')
        {
            path = path[2..];
        }
        else if (path.StartsWith("\"a/", StringComparison.Ordinal) ||
                 path.StartsWith("\"b/", StringComparison.Ordinal))
        {
            path = '"' + path[3..];
        }

        return path.Length > 1 && path[0] == '"' && path[^1] == '"'
            ? path[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal)
                         .Replace("\\\\", "\\", StringComparison.Ordinal)
            : path;
    }

    /// <summary>The new path out of <c>diff --git a/x b/x</c>, used when no marker line carried one.</summary>
    private static string? PathFromHeader(string header)
    {
        var parts = header[FileMarker.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? null : StripPrefix(parts[^1]);
    }
}
