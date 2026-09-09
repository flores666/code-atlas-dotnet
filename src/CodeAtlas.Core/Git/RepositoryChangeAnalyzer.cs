using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Git;

/// <summary>
/// Works out which indexed symbols the working tree has changed.
/// </summary>
/// <remarks>
/// <para>
/// The join is a coordinate one, not a name one. The index was built from the files on
/// disk, so a declaration's recorded span and the new side of a diff against <c>HEAD</c>
/// are in the same line numbering, and a changed symbol is simply one whose span contains
/// a line the diff touched. No re-analysis, no second pass over Roslyn: mapping a diff
/// costs one indexed lookup per changed file.
/// </para>
/// <para>
/// A type's span covers its members, so an edit inside a method reports both the method
/// and the type. That is deliberate — both are true, and both are what a reader asks for.
/// </para>
/// <para>
/// Every changed declaration is then related to the tests over it, which is the question a
/// diff actually raises: not only what moved, but what was holding it. A changed method of
/// production code that nothing covers is marked, and is the only thing warned about — a
/// type header or a test's own body changing says nothing about coverage.
/// </para>
/// </remarks>
public static class RepositoryChangeAnalyzer
{
    /// <summary>
    /// Reads the repository's state and maps it onto the index.
    /// </summary>
    /// <param name="database">
    /// The index to map onto. When <c>null</c> the file-level answer is still produced:
    /// the status and the diff are Git's, and do not depend on anything having been indexed.
    /// </param>
    public static RepositoryChanges Analyze(
        GitRepository repository,
        SymbolIndexDatabase? database,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);

        var status = repository.GetStatus(cancellationToken);
        var diffs = repository.GetDiff(cancellationToken);

        if (database is null)
        {
            return new RepositoryChanges(status, diffs, []);
        }

        var byPath = diffs.ToDictionary(diff => diff.Path, StringComparer.OrdinalIgnoreCase);
        var testProjects = database.GetTestProjects().ToHashSet(StringComparer.Ordinal);
        var symbols = new List<ChangedSymbol>();

        foreach (var file in status.Files.Where(file => file.IsCSharp))
        {
            cancellationToken.ThrowIfCancellationRequested();

            symbols.AddRange(file.Change switch
            {
                // Nothing the index holds can describe a file that is gone, so the only
                // account of it left is the baseline text Git still has.
                GitFileChange.Deleted => Removed(repository, file, cancellationToken),

                // An untracked file has no baseline and so no diff. Everything in it is new.
                GitFileChange.Untracked => Whole(database, file, SymbolChangeKind.Added, testProjects),

                _ => byPath.TryGetValue(file.Path, out var diff)
                    ? Mapped(database, file, diff, testProjects)
                    : [],
            });
        }

        return new RepositoryChanges(
            status,
            diffs,
            symbols
                .OrderBy(symbol => symbol.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(symbol => symbol.Line ?? 0)
                .Select(symbol => symbol.SymbolId is { } id
                    ? symbol with { Tests = database.FindRelatedTests(id) }
                    : symbol)
                .ToList());
    }

    /// <summary>
    /// The symbols one file's hunks fall inside.
    /// </summary>
    /// <remarks>
    /// A declaration every line of which is an added line is an addition — a method written
    /// into an existing class reads as added rather than as a change to it. That test is
    /// exact and needs nothing but the diff, which is why added symbols are reported inside
    /// modified files while removed ones are not: naming a declaration that is no longer
    /// there would take a second analysis of the baseline, and a guess dressed up as a
    /// symbol is worse than a file-level answer.
    /// </remarks>
    private static IEnumerable<ChangedSymbol> Mapped(
        SymbolIndexDatabase database,
        GitFileStatus file,
        FileDiff diff,
        IReadOnlySet<string> testProjects)
    {
        if (diff.IsBinary || file.FullPath is not { } path)
        {
            return [];
        }

        var touched = diff.TouchedLines;
        if (touched.Count == 0)
        {
            return [];
        }

        var added = diff.Hunks.SelectMany(hunk => hunk.AddedLines).ToHashSet();

        var declarations = database.GetSymbolsInFile(path);

        return declarations
            .Where(symbol => touched.Any(symbol.Contains))
            .Select(symbol => Describe(
                symbol,
                IsWhollyAdded(symbol, added) ? SymbolChangeKind.Added : SymbolChangeKind.Modified,
                declarations,
                testProjects))
            .ToList();
    }

    /// <summary>Every symbol in a file, all changed the same way.</summary>
    private static IEnumerable<ChangedSymbol> Whole(
        SymbolIndexDatabase database,
        GitFileStatus file,
        SymbolChangeKind change,
        IReadOnlySet<string> testProjects)
    {
        if (file.FullPath is not { } path)
        {
            return [];
        }

        var declarations = database.GetSymbolsInFile(path);
        return declarations
            .Select(symbol => Describe(symbol, change, declarations, testProjects))
            .ToList();
    }

    /// <summary>
    /// What a deleted file used to declare, read from its baseline. Nothing here is
    /// navigable: the declarations no longer exist.
    /// </summary>
    private static IEnumerable<ChangedSymbol> Removed(
        GitRepository repository,
        GitFileStatus file,
        CancellationToken cancellationToken) =>
        repository.GetBaselineText(file.Path, cancellationToken) is { } baseline
            ? BaselineDeclarations.ReadRemoved(baseline, file.FullPath ?? file.Path)
            : [];

    private static bool IsWhollyAdded(IndexedSymbol symbol, IReadOnlySet<int> added)
    {
        if (symbol.Line is not { } start)
        {
            return false;
        }

        for (var line = start; line <= (symbol.EndLine ?? start); line++)
        {
            if (!added.Contains(line))
            {
                return false;
            }
        }

        return true;
    }

    private static ChangedSymbol Describe(
        IndexedSymbol symbol,
        SymbolChangeKind change,
        IReadOnlyList<IndexedSymbol> declarations,
        IReadOnlySet<string> testProjects) => new()
    {
        SymbolId = symbol.Id,
        Display = symbol.Display,
        Kind = symbol.Kind,
        Change = change,
        Container = symbol.ContainerFullyQualifiedName ?? symbol.Namespace,
        FilePath = symbol.FilePath,
        Line = symbol.Line,
        IsMeaningful = IsMeaningful(symbol, declarations, testProjects),
    };

    /// <summary>
    /// Whether a changed declaration is one a missing test would be worth saying anything
    /// about.
    /// </summary>
    /// <remarks>
    /// Only a method or constructor carries behaviour to cover, and only in production
    /// code: a test project's own edits are the tests. An interface member has no body to
    /// exercise, and a record's generated constructor is the compiler's, not the author's.
    /// Everything ruled out here is still listed as changed — it simply never earns a
    /// warning, because a warning nobody can act on trains readers to ignore all of them.
    /// </remarks>
    private static bool IsMeaningful(
        IndexedSymbol symbol,
        IReadOnlyList<IndexedSymbol> declarations,
        IReadOnlySet<string> testProjects)
    {
        if (symbol.Kind is not (IndexedSymbolKind.Method or IndexedSymbolKind.Constructor) ||
            (symbol.ProjectName is { } project && testProjects.Contains(project)))
        {
            return false;
        }

        var container = declarations
            .FirstOrDefault(other => other.FullyQualifiedName == symbol.ContainerFullyQualifiedName)?
            .Kind;

        return container is not IndexedSymbolKind.Interface &&
               (symbol.Kind is not IndexedSymbolKind.Constructor || container is not IndexedSymbolKind.Record);
    }
}
