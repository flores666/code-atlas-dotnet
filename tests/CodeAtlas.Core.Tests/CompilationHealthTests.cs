using CodeAtlas.Core.Analysis;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Core.Tests;

/// <summary>
/// A project that loads but does not compile has to say so.
/// </summary>
/// <remarks>
/// This is the case that is dangerous precisely because it looks healthy: declarations
/// still come through, so the project count and the symbol count are plausible, while
/// every binding that needed a missing reference silently resolved to nothing. Without a
/// diagnostic the reader is left concluding that their application simply has no
/// endpoints.
/// </remarks>
public class CompilationHealthTests
{
    private static async Task<IReadOnlyList<IndexDiagnostic>> DiagnosticsOf(string source) =>
        (await SymbolCollector.CollectAsync(
            TestProjectFactory.Create("App", ("App.cs", source)),
            TestContext.Current.CancellationToken)).Diagnostics;

    [Fact]
    public async Task Says_nothing_about_a_project_that_compiles()
    {
        var diagnostics = await DiagnosticsOf("""
            namespace App;

            public class Widget
            {
                public int Value() => 1;
            }
            """);

        Assert.Empty(diagnostics);
    }

    /// <summary>
    /// An unresolved reference is called out as such, because the fix is environmental —
    /// a missing targeting pack or an unrestored solution — rather than in the code.
    /// </summary>
    [Fact]
    public async Task Reports_an_unresolved_reference_and_what_it_costs()
    {
        var diagnostics = await DiagnosticsOf("""
            namespace App;

            public class Widget : Microsoft.Missing.Framework.WidgetBase
            {
                public Microsoft.Missing.Framework.Thing Make() => null!;
            }
            """);

        var diagnostic = Assert.Single(diagnostics);

        Assert.Equal(Model.DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("App", diagnostic.Project);
        Assert.Contains("unresolved reference", diagnostic.Message, StringComparison.OrdinalIgnoreCase);

        // Names the consequence, so an empty Endpoints list is explained rather than
        // left to be discovered.
        Assert.Contains("endpoints", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("under-reported", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A project is still indexed for what can be read: the diagnostic degrades the
    /// reported state rather than discarding the work.
    /// </summary>
    [Fact]
    public async Task Still_indexes_what_it_can_read()
    {
        var data = await SymbolCollector.CollectAsync(
            TestProjectFactory.Create("App", ("App.cs", """
                namespace App;

                public class Widget : Microsoft.Missing.Framework.WidgetBase
                {
                    public int Value() => 1;
                }
                """)),
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(data.Diagnostics);
        Assert.Contains(data.Symbols, symbol => symbol.Name == "Widget");
        Assert.Contains(data.Symbols, symbol => symbol.Name == "Value");
    }

    /// <summary>An ordinary compile error is reported without blaming the references.</summary>
    [Fact]
    public async Task Reports_an_ordinary_error_without_calling_it_a_reference_problem()
    {
        var diagnostics = await DiagnosticsOf("""
            namespace App;

            public class Widget
            {
                public int Value() => "not an int";
            }
            """);

        var diagnostic = Assert.Single(diagnostics);

        Assert.Contains("compilation error", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unresolved reference", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }
}
