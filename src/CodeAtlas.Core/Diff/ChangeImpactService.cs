using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Diff;

/// <summary>
/// Reads a working-tree diff back into the index: which declarations it changed, and
/// which tests exercise them.
/// </summary>
/// <remarks>
/// The index is the only thing consulted about the code, so the answer is exactly as
/// current as the last indexing run. A changed file the index does not know is reported
/// as such rather than dropped: for a file that has not been indexed, "no tests" would be
/// a conclusion about CodeAtlas rather than about the code.
/// </remarks>
public sealed class ChangeImpactService(SymbolIndexDatabase database)
{
    private readonly SymbolIndexDatabase _database =
        database ?? throw new ArgumentNullException(nameof(database));

    public async Task<ChangeImpact> AnalyseAsync(
        string repositoryRoot,
        string against = GitDiffReader.DefaultAgainst,
        CancellationToken cancellationToken = default)
    {
        var diff = await GitDiffReader.ReadAsync(repositoryRoot, against, cancellationToken).ConfigureAwait(false);

        if (diff.Error is { } error)
        {
            return new ChangeImpact([], [], error);
        }

        var testProjects = _database.GetTestProjects().ToHashSet(StringComparer.Ordinal);
        var changes = new List<ChangedSymbol>();
        var unmapped = new List<string>();

        foreach (var file in diff.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = Path.GetFullPath(Path.Combine(repositoryRoot, file.Path));
            var declarations = _database.GetSymbolsInFile(path);

            if (declarations.Count == 0)
            {
                if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    unmapped.Add(file.Path);
                }

                continue;
            }

            foreach (var symbol in ChangedIn(declarations, file.Lines))
            {
                changes.Add(new ChangedSymbol(
                    symbol,
                    _database.FindRelatedTests(symbol.Id),
                    IsMeaningful(symbol, declarations, testProjects)));
            }
        }

        return new ChangeImpact(changes, unmapped);
    }

    /// <summary>
    /// The declarations a set of changed lines lands in, innermost only.
    /// </summary>
    /// <remarks>
    /// A symbol's span contains everything nested in it, so a changed line inside a method
    /// sits inside the method, its type, and any type around that. Only the innermost
    /// declaration is the one that changed, and it is found by giving each symbol the part
    /// of its span that none of its own members claim: a body change reaches the method, a
    /// change to the type's header or to one of its attributes reaches the type, and
    /// neither reports the other.
    /// </remarks>
    private static IEnumerable<IndexedSymbol> ChangedIn(
        IReadOnlyList<IndexedSymbol> declarations,
        IReadOnlyList<LineRange> changed)
    {
        var members = declarations
            .Where(symbol => symbol.ContainerFullyQualifiedName is not null)
            .ToLookup(symbol => symbol.ContainerFullyQualifiedName!, StringComparer.Ordinal);

        foreach (var symbol in declarations)
        {
            // A namespace spans the whole file and is never what a diff changed.
            if (symbol.Kind is IndexedSymbolKind.Namespace || symbol.Line is not { } start)
            {
                continue;
            }

            var own = Exclude(
                new LineRange(start, Math.Max(start, symbol.EndLine ?? start)),
                members[symbol.FullyQualifiedName].Select(SpanOf));

            if (own.Any(part => changed.Any(part.Overlaps)))
            {
                yield return symbol;
            }
        }
    }

    private static LineRange SpanOf(IndexedSymbol symbol)
    {
        var start = symbol.Line ?? 0;
        return new LineRange(start, Math.Max(start, symbol.EndLine ?? start));
    }

    /// <summary>What is left of a span once the spans nested inside it are taken out.</summary>
    private static List<LineRange> Exclude(LineRange span, IEnumerable<LineRange> nested)
    {
        var remaining = new List<LineRange> { span };

        foreach (var hole in nested.OrderBy(range => range.Start))
        {
            var next = new List<LineRange>(remaining.Count + 1);

            foreach (var part in remaining)
            {
                if (!part.Overlaps(hole))
                {
                    next.Add(part);
                    continue;
                }

                if (part.Start < hole.Start)
                {
                    next.Add(new LineRange(part.Start, hole.Start - 1));
                }

                if (part.End > hole.End)
                {
                    next.Add(new LineRange(hole.End + 1, part.End));
                }
            }

            remaining = next;
        }

        return remaining;
    }

    /// <summary>
    /// Whether a change is one a test should have covered: a method or constructor of
    /// production code that carries behaviour.
    /// </summary>
    /// <remarks>
    /// Types, fields and properties change for reasons a test cannot be expected to see; an
    /// interface member declares no behaviour to exercise, its implementations do; and a
    /// record's constructor is the shape of its data rather than something a test could
    /// catch going wrong. A test project's own code is excluded outright — warning that a
    /// test has no test is noise. The container is read from the same file, which is where
    /// a member's own type is declared.
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
