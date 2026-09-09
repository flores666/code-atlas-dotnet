using System.Diagnostics;

namespace CodeAtlas.Core.Tests;

/// <summary>
/// A throwaway Git working tree for the change-mapping tests.
/// </summary>
/// <remarks>
/// Writes are done here rather than through anything in CodeAtlas on purpose: CodeAtlas
/// cannot commit, and the tests are the ones creating a baseline for it to read. The
/// commit identity is passed per invocation so the test does not depend on — or touch —
/// the machine's Git configuration.
/// </remarks>
internal sealed class GitWorkingTree : IDisposable
{
    private static readonly string[] Identity =
    [
        "-c", "user.name=CodeAtlas Test",
        "-c", "user.email=test@example.invalid",
        "-c", "commit.gpgsign=false",
    ];

    public GitWorkingTree()
    {
        Root = Path.GetFullPath(Directory.CreateTempSubdirectory("codeatlas-git").FullName);

        // A fixed initial branch keeps the assertions independent of the machine's
        // init.defaultBranch setting.
        Git("init", "--initial-branch=work");
    }

    public string Root { get; }

    /// <summary>True when a usable <c>git</c> is on the path.</summary>
    public static bool IsAvailable { get; } = Probe();

    public string PathOf(string relativePath) =>
        Path.GetFullPath(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    public string Write(string relativePath, string content)
    {
        var path = PathOf(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);

        return path;
    }

    public void Delete(string relativePath) => File.Delete(PathOf(relativePath));

    public void Move(string from, string to)
    {
        var target = PathOf(to);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(PathOf(from), target);
    }

    /// <summary>Stages everything and commits it, creating the baseline to diff against.</summary>
    public void Commit(string message)
    {
        Git("add", "-A");
        Git([.. Identity, "commit", "-m", message]);
    }

    /// <summary>Stages the current content of one path, without committing it.</summary>
    public void Stage(string relativePath) => Git("add", "--", relativePath);

    private void Git(params string[] arguments) => Run(Root, arguments, throwOnFailure: true);

    private static bool Probe()
    {
        try
        {
            return Run(Path.GetTempPath(), ["--version"], throwOnFailure: false) == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static int Run(string workingDirectory, string[] arguments, bool throwOnFailure)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (throwOnFailure && process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed with {process.ExitCode}: {error}");
        }

        return process.ExitCode;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }
}
