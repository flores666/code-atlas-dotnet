using System.Diagnostics;
using System.Text;

namespace CodeAtlas.Core.Git;

/// <summary>The outcome of one <c>git</c> invocation.</summary>
/// <param name="ExitCode">Non-zero is not always a failure: an absent baseline path is a 128.</param>
public sealed record GitCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>Output with the single trailing newline Git adds removed.</summary>
    public string Text => StandardOutput.TrimEnd('\n', '\r');

    public IEnumerable<string> Lines => Text.Length == 0
        ? []
        : Text.Split('\n').Select(line => line.TrimEnd('\r'));
}

/// <summary>
/// Runs <c>git</c> against a working tree and hands back its output.
/// </summary>
/// <remarks>
/// <para>
/// CodeAtlas is read-only, and this is the one place that could break that, so the
/// constraint is enforced structurally rather than by convention: only the verbs in
/// <see cref="ReadOnlyVerbs"/> can be run at all, and every one of them is incapable of
/// altering a repository in any form. <c>commit</c>, <c>push</c>, <c>pull</c>,
/// <c>reset</c>, <c>checkout</c>, <c>stash</c> and <c>clean</c> are not omitted by
/// oversight — the allowlist is what makes them unreachable, including from a future
/// caller that has forgotten the rule.
/// </para>
/// <para>
/// <c>--no-optional-locks</c> leads every invocation because <c>git status</c> otherwise
/// refreshes the index as a side effect, and writing to the analysed repository — even a
/// write Git considers routine — is precisely what CodeAtlas promises not to do.
/// </para>
/// </remarks>
public static class GitCommandRunner
{
    /// <summary>
    /// Verbs that cannot mutate a repository in any of their forms. A verb belongs here
    /// only if that is true of <em>every</em> way it can be invoked, so no argument the
    /// callers below pass can turn a read into a write.
    /// </summary>
    private static readonly IReadOnlySet<string> ReadOnlyVerbs = new HashSet<string>(StringComparer.Ordinal)
    {
        "rev-parse",
        "status",
        "diff",
        "log",
        "show",
    };

    /// <summary>
    /// Long enough for a large diff, short enough that a wedged Git cannot hold a query
    /// open forever.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>True when the verb is one this runner is willing to execute.</summary>
    public static bool IsReadOnly(string verb) => ReadOnlyVerbs.Contains(verb);

    /// <summary>
    /// Runs a read-only Git command in <paramref name="workingTreeRoot"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The verb is not read-only. This is a programming error rather than a runtime
    /// condition: it means a caller tried to make CodeAtlas write to the repository.
    /// </exception>
    public static GitCommandResult Run(
        string workingTreeRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingTreeRoot);
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.Count == 0 || !IsReadOnly(arguments[0]))
        {
            throw new ArgumentException(
                $"'{(arguments.Count == 0 ? string.Empty : arguments[0])}' is not one of the read-only Git " +
                "commands CodeAtlas is allowed to run. CodeAtlas never writes to the analysed repository.",
                nameof(arguments));
        }

        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingTreeRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        startInfo.ArgumentList.Add("--no-optional-locks");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Nothing here should ever ask for credentials or open a pager; both would
        // otherwise hang a query that has no terminal to answer on.
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_PAGER"] = "cat";
        startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start git.");

        // Both streams are drained before waiting: a diff large enough to fill the pipe
        // buffer would otherwise deadlock against a process that cannot finish writing.
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);

        if (!process.WaitForExit(Timeout))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"git {arguments[0]} did not finish within {Timeout.TotalSeconds:N0}s.");
        }

        Task.WaitAll([standardOutput, standardError], cancellationToken);

        return new GitCommandResult(process.ExitCode, standardOutput.Result, standardError.Result);
    }
}
