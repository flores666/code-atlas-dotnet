namespace CodeAtlas.Core.Model;

/// <summary>How Git reports one path differing from the baseline.</summary>
public enum GitFileChange
{
    Modified,
    Added,
    Deleted,
    Renamed,
    Copied,
    TypeChanged,

    /// <summary>Present on disk and not tracked at all, so it has no baseline.</summary>
    Untracked,

    /// <summary>A merge left this path with conflicting sides.</summary>
    Conflicted,
}

/// <summary>
/// One path Git reports as differing from <c>HEAD</c>.
/// </summary>
/// <remarks>
/// Staged and unstaged are not exclusive: porcelain status carries a code for the index
/// and one for the working tree, and a file edited after being staged has both.
/// </remarks>
public sealed record GitFileStatus
{
    /// <summary>Repository-relative path, as Git spells it.</summary>
    public required string Path { get; init; }

    /// <summary>Absolute path, or <c>null</c> when the working tree root is unknown.</summary>
    public string? FullPath { get; init; }

    public required GitFileChange Change { get; init; }

    /// <summary>The path this file was renamed or copied from.</summary>
    public string? OldPath { get; init; }

    /// <summary>True when the change is present in the index.</summary>
    public bool IsStaged { get; init; }

    /// <summary>True when the working tree differs from the index.</summary>
    public bool IsUnstaged { get; init; }

    /// <summary>True for a path Git is not tracking, which therefore has no baseline.</summary>
    public bool IsUntracked => Change == GitFileChange.Untracked;

    public bool IsCSharp => Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The state of the working tree relative to its Git baseline.</summary>
/// <param name="Branch">
/// The checked-out branch, or the short commit when <paramref name="IsDetached"/>.
/// </param>
/// <param name="Head">The short hash of <c>HEAD</c>, or <c>null</c> in a repository with no commits.</param>
public sealed record GitStatus(
    string Branch,
    bool IsDetached,
    string? Head,
    IReadOnlyList<GitFileStatus> Files)
{
    public static GitStatus Empty { get; } = new("(unknown)", IsDetached: false, Head: null, []);

    public bool IsClean => Files.Count == 0;

    public IReadOnlyList<GitFileStatus> Staged =>
        Files.Where(file => file.IsStaged).ToList();

    /// <summary>Tracked files whose working-tree content differs from the index.</summary>
    public IReadOnlyList<GitFileStatus> Modified =>
        Files.Where(file => file.IsUnstaged && !file.IsUntracked).ToList();

    public IReadOnlyList<GitFileStatus> Untracked =>
        Files.Where(file => file.IsUntracked).ToList();
}

/// <summary>One commit, as read from the log.</summary>
public sealed record GitCommit(
    string Hash,
    string ShortHash,
    string Author,
    DateTimeOffset When,
    string Subject);

/// <summary>
/// One hunk of a unified diff.
/// </summary>
/// <remarks>
/// <see cref="TouchedLines"/> is in <em>new-file</em> coordinates, which is what makes a
/// hunk comparable with an indexed declaration: the index was built from the working tree,
/// so a symbol's recorded span and the new side of the diff are the same coordinate space.
/// A removal contributes the new-side line that now sits where the removed text was, so
/// deleting the body of a method still attributes the hunk to that method.
/// </remarks>
public sealed record DiffHunk(
    string Header,
    int OldStart,
    int OldCount,
    int NewStart,
    int NewCount,
    IReadOnlyList<int> TouchedLines,
    IReadOnlySet<int> AddedLines,
    int RemovedLineCount)
{
    public bool HasRemovals => RemovedLineCount > 0;
}

/// <summary>The diff of one path, kept with the text so it can also simply be read.</summary>
public sealed record FileDiff
{
    public required string Path { get; init; }

    public string? OldPath { get; init; }

    /// <summary>True when Git reported a binary difference, which has no hunks to map.</summary>
    public bool IsBinary { get; init; }

    public IReadOnlyList<DiffHunk> Hunks { get; init; } = [];

    /// <summary>This file's section of the diff, verbatim, for display.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Every new-side line the hunks touch.</summary>
    public IReadOnlyList<int> TouchedLines =>
        Hunks.SelectMany(hunk => hunk.TouchedLines).Distinct().Order().ToList();
}

/// <summary>How a symbol differs from the baseline.</summary>
public enum SymbolChangeKind
{
    /// <summary>Its declaration span contains changed lines.</summary>
    Modified,

    /// <summary>Every line of its declaration is new, or its whole file is.</summary>
    Added,

    /// <summary>
    /// It was declared in a file the baseline had and the working tree does not.
    /// Read from the baseline text rather than from the index, so it is never navigable.
    /// </summary>
    Removed,
}

/// <summary>
/// One symbol the working tree changed.
/// </summary>
/// <remarks>
/// <see cref="SymbolId"/> is <c>null</c> for a removed declaration, exactly as
/// <see cref="SymbolLink"/> carries a name for a symbol outside the index: the
/// declaration is known by name and there is nothing left to navigate to.
/// </remarks>
public sealed record ChangedSymbol
{
    public long? SymbolId { get; init; }

    public required string Display { get; init; }

    public required IndexedSymbolKind Kind { get; init; }

    public required SymbolChangeKind Change { get; init; }

    /// <summary>Declaring type or namespace, shown to disambiguate same-named members.</summary>
    public string? Container { get; init; }

    public string? FilePath { get; init; }

    public int? Line { get; init; }

    /// <summary>
    /// <see cref="RelationProvenance.Exact"/> when the mapping came from the diff and the
    /// index. A removed declaration is <see cref="RelationProvenance.Inferred"/>: it was
    /// read from baseline syntax alone, with no compilation behind it.
    /// </summary>
    public RelationProvenance Provenance { get; init; } = RelationProvenance.Exact;

    public bool IsNavigable => SymbolId is not null;

    /// <summary>
    /// The tests that exercise this declaration, best evidence first. Empty for a removed
    /// one: it was read from the baseline and has no id to look anything up by.
    /// </summary>
    public IReadOnlyList<RelatedTest> Tests { get; init; } = [];

    /// <summary>
    /// True for a change worth having a test: a method or constructor of production code.
    /// Everything else — a type header, a field, an interface member, a test's own body —
    /// is still listed, and simply never warned about.
    /// </summary>
    public bool IsMeaningful { get; init; }

    /// <summary>A meaningful change nothing was found to cover, which is what earns a warning.</summary>
    public bool IsUntested => IsMeaningful && Tests.Count == 0;

    public int ExactTests => Tests.Count(test => test.IsExact);
}

/// <summary>
/// How the working tree differs from its Git baseline, in both file and symbol terms.
/// </summary>
/// <remarks>
/// Never persisted. Git state moves independently of the index, so this is computed on
/// demand and the cache file stays a function of the source alone.
/// </remarks>
public sealed record RepositoryChanges(
    GitStatus Status,
    IReadOnlyList<FileDiff> Diffs,
    IReadOnlyList<ChangedSymbol> Symbols)
{
    public static RepositoryChanges Empty { get; } = new(GitStatus.Empty, [], []);

    /// <summary>Ids of the changed symbols the index holds, which is what the graph filters on.</summary>
    public IReadOnlySet<long> SymbolIds =>
        Symbols.Select(symbol => symbol.SymbolId).OfType<long>().ToHashSet();
}
