namespace CodeAtlas.Core.Model;

/// <summary>A half-open-free, inclusive run of lines a diff touched in one file.</summary>
public readonly record struct LineRange(int Start, int End)
{
    public bool Contains(int line) => line >= Start && line <= End;

    public bool Overlaps(LineRange other) => Start <= other.End && other.Start <= End;
}

/// <summary>The lines a diff changed in one file, keyed by its path in the working tree.</summary>
public sealed record ChangedFile(string Path, IReadOnlyList<LineRange> Lines);

/// <summary>
/// A symbol a diff touched, and the tests that exercise it.
/// </summary>
/// <param name="IsMeaningful">
/// True for a change worth having a test: a method or constructor of production code.
/// Everything else — a type header, a field, a test's own body — is still listed, and
/// simply never warned about.
/// </param>
public sealed record ChangedSymbol(
    IndexedSymbol Symbol,
    IReadOnlyList<RelatedTest> Tests,
    bool IsMeaningful)
{
    /// <summary>A meaningful change nothing was found to cover, which is what earns a warning.</summary>
    public bool IsUntested => IsMeaningful && Tests.Count == 0;

    public int ExactTests => Tests.Count(test => test.IsExact);
}

/// <summary>
/// What a working-tree diff means for the index: the symbols it changed, the tests over
/// them, and what could not be read.
/// </summary>
/// <param name="UnmappedFiles">
/// Changed C# files holding no indexed symbol. Normally a file added or renamed since the
/// last index, so it is reported rather than silently dropped: a missing file is the one
/// case where "no tests" would be the wrong conclusion.
/// </param>
/// <param name="Error">Why nothing could be read, when the diff itself failed.</param>
public sealed record ChangeImpact(
    IReadOnlyList<ChangedSymbol> Changes,
    IReadOnlyList<string> UnmappedFiles,
    string? Error = null)
{
    public static readonly ChangeImpact Empty = new([], []);

    public IReadOnlyList<ChangedSymbol> Untested =>
        [.. Changes.Where(change => change.IsUntested)];
}
