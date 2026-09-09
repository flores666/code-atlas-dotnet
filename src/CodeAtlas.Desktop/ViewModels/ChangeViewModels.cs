using CodeAtlas.Core.Model;

namespace CodeAtlas.Desktop.ViewModels;

/// <summary>One test over a symbol, with the evidence that put it there.</summary>
public sealed class RelatedTestViewModel(RelatedTest related)
{
    public long SymbolId { get; } = related.Test.SymbolId;

    /// <summary>Fixture and method, the way a runner names a test.</summary>
    public string Name { get; } = related.Test.QualifiedDisplay;

    /// <summary>Why this test is listed, so the reader can judge a probable one themselves.</summary>
    public string Reason { get; } = related.Reason;

    /// <summary>
    /// <c>exact</c> only for an edge the compiler recorded. A naming or layout match is
    /// always <c>probable</c>, and says so.
    /// </summary>
    public string Confidence { get; } = related.IsExact ? "exact" : "probable";

    public bool IsExact { get; } = related.IsExact;

    /// <summary>The framework it runs under, and where it is written.</summary>
    public string Source { get; } = Framework(related.Test.Framework) +
        (RowOrigin.Of(related.Test.FilePath, related.Test.Line) is { Length: > 0 } origin
            ? $" · {origin}"
            : string.Empty);

    private static string Framework(TestFramework framework) => framework switch
    {
        TestFramework.NUnit => "NUnit",
        TestFramework.MSTest => "MSTest",
        _ => "xUnit",
    };
}

/// <summary>One declaration a diff changed, and the tests that reach it.</summary>
public sealed class ChangedSymbolViewModel
{
    /// <summary>How many tests a row lists before deferring the rest to the details pane.</summary>
    private const int InlineTests = 4;

    public ChangedSymbolViewModel(ChangedSymbol change)
    {
        ArgumentNullException.ThrowIfNull(change);

        SymbolId = change.Symbol.Id;
        Display = change.Symbol.Display;
        Container = change.Symbol.ContainerFullyQualifiedName ?? change.Symbol.Namespace ?? string.Empty;
        Origin = RowOrigin.Of(change.Symbol.FilePath, change.Symbol.Line);
        Glyph = SymbolGlyph.For(change.Symbol.Kind);
        IsContainer = SymbolGlyph.IsContainer(change.Symbol.Kind);
        IsUntested = change.IsUntested;

        Tests = [.. change.Tests.Take(InlineTests).Select(test => new RelatedTestViewModel(test))];
        Remaining = Math.Max(0, change.Tests.Count - Tests.Count);

        Summary = (change.Tests.Count, change.ExactTests) switch
        {
            (0, _) when change.IsMeaningful => "no tests found",
            (0, _) => string.Empty,
            (1, 1) => "1 test",
            (var total, var exact) when exact == total => $"{total} tests",
            (var total, 0) => $"{total} probable",
            var (total, exact) => $"{total} tests · {exact} exact",
        };
    }

    public long SymbolId { get; }

    public string Display { get; }

    public string Container { get; }

    public string Origin { get; }

    public string Glyph { get; }

    public bool IsContainer { get; }

    /// <summary>A meaningful change with nothing over it, which is what the warning marks.</summary>
    public bool IsUntested { get; }

    public string Summary { get; }

    public bool HasSummary => Summary.Length > 0;

    public IReadOnlyList<RelatedTestViewModel> Tests { get; }

    public bool HasTests => Tests.Count > 0;

    public int Remaining { get; }

    public bool HasMore => Remaining > 0;

    public string MoreSummary => $"+{Remaining} more in the details pane";
}
