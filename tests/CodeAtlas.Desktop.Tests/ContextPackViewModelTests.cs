using CodeAtlas.Core.Model;
using CodeAtlas.Desktop.ViewModels;

namespace CodeAtlas.Desktop.Tests;

/// <summary>
/// How the context builder presents a pack. Assembling one is covered against a real index
/// in CodeAtlas.Core.Tests; what matters here is that the presentation keeps the promises
/// the section makes — a reason on every suggested entry, and a budget that is never
/// exceeded quietly.
/// </summary>
public class ContextPackViewModelTests
{
    private static ContextItem Item(ContextItemKind kind = ContextItemKind.Callers) => new()
    {
        Kind = kind,
        Key = "callers:1",
        Title = "Callers of Get(int id)",
        Reason = "3 members call it directly.",
        Body = "## Callers\n\n- `Complete()` — CheckoutService.cs:41\n",
        Files = [new ContextFile("src/Shop/CheckoutService.cs", "class CheckoutService { }", IsTest: false)],
    };

    [Fact]
    public void Starts_with_nothing_to_build_from()
    {
        var context = new ContextPackViewModel();

        Assert.False(context.HasIndex);
        Assert.False(context.HasItems);
        Assert.Empty(context.Items);
        Assert.Empty(context.Suggestions);
        Assert.Equal("No symbol selected", context.FocusTitle);
    }

    /// <summary>Nothing runs without an index behind it, so a stray click is harmless.</summary>
    [Fact]
    public void Adds_nothing_without_an_index()
    {
        var context = new ContextPackViewModel();

        Assert.False(context.AddCommand.CanExecute(ContextItemKind.Symbol));
        Assert.False(context.AddCommand.CanExecute(ContextItemKind.Architecture));

        context.Add(ContextItemKind.Architecture);

        Assert.Empty(context.Items);
    }

    /// <summary>
    /// A blank budget is no ceiling rather than a ceiling of zero, which would refuse
    /// everything the moment the box was cleared.
    /// </summary>
    [Theory]
    [InlineData("60000", 60000)]
    [InlineData("120,000", 120000)]
    [InlineData(" 8000 ", 8000)]
    [InlineData("", 0)]
    [InlineData("not a number", 0)]
    [InlineData("-5", 0)]
    public void Reads_the_budget_as_the_reader_wrote_it(string text, int expected)
    {
        var context = new ContextPackViewModel { BudgetText = text };

        Assert.Equal(expected, context.BudgetTokens);
        Assert.Equal(expected > 0, context.HasBudget);
    }

    [Fact]
    public void Says_there_is_no_ceiling_when_none_is_set()
    {
        var context = new ContextPackViewModel { BudgetText = string.Empty };

        Assert.Contains("No budget", context.BudgetSummary, StringComparison.Ordinal);
        Assert.Equal(0, context.BudgetFraction);
        Assert.False(context.IsOverBudget);
    }

    /// <summary>
    /// The reason travels with the row: a suggestion nobody can judge is not one worth
    /// offering.
    /// </summary>
    [Fact]
    public void Carries_the_reason_and_the_cost_on_a_suggestion()
    {
        var suggestion = new ContextSuggestionViewModel(Item());

        Assert.Equal("Callers", suggestion.Kind);
        Assert.Equal("3 members call it directly.", suggestion.Reason);
        Assert.Contains("tokens", suggestion.CostSummary, StringComparison.Ordinal);
        Assert.Contains("1 file", suggestion.CostSummary, StringComparison.Ordinal);
    }

    /// <summary>An entry says where it will land, so the exported folder holds no surprises.</summary>
    [Fact]
    public void Names_where_an_entry_lands()
    {
        Assert.Equal("ExecutionFlow.md", new ContextItemViewModel(Item()).Destination);
        Assert.Equal(
            "Architecture.md",
            new ContextItemViewModel(Item(ContextItemKind.Architecture)).Destination);
        Assert.Equal(
            "GitDiff.patch",
            new ContextItemViewModel(Item(ContextItemKind.GitDiff)).Destination);
        Assert.Equal("Task.md", new ContextItemViewModel(Item(ContextItemKind.Symbol)).Destination);
    }

    [Fact]
    public void Describes_what_a_pack_file_costs()
    {
        var file = new ContextPackFileViewModel(new ContextPackFile("Task.md", "one\ntwo\n"));

        Assert.Equal("Task.md", file.Path);
        Assert.Contains("3 lines", file.Summary, StringComparison.Ordinal);
        Assert.Contains("8 chars", file.Summary, StringComparison.Ordinal);
    }

    /// <summary>The focus is whatever is being looked at, and the buttons say so.</summary>
    [Fact]
    public void Follows_the_symbol_being_looked_at()
    {
        var context = new ContextPackViewModel();

        context.SetFocus(new IndexedSymbol
        {
            Id = 7,
            Kind = IndexedSymbolKind.Method,
            Name = "Get",
            Display = "Get(int id)",
            FullyQualifiedName = "Shop.UserService.Get(System.Int32)",
        });

        Assert.True(context.HasFocus);
        Assert.Equal("Get(int id)", context.FocusTitle);
        Assert.Contains("Shop.UserService.Get", context.FocusSubtitle, StringComparison.Ordinal);
    }

    /// <summary>A pack is inspected a file at a time, so there is one until there is a pack.</summary>
    [Fact]
    public void Has_nothing_to_preview_until_there_is_a_pack()
    {
        var context = new ContextPackViewModel();

        Assert.False(context.HasPreview);
        Assert.Empty(context.PreviewText);
        Assert.Empty(context.PreviewFiles);
    }

    /// <summary>Closing a workspace leaves no pack behind: a pack belongs to one index.</summary>
    [Fact]
    public void Drops_the_pack_when_the_workspace_closes()
    {
        var context = new ContextPackViewModel();

        context.SetWorkspace(null);

        Assert.False(context.HasIndex);
        Assert.Empty(context.Items);
        Assert.False(context.HasFocus);
    }

    /// <summary>Exporting an empty pack says so rather than writing an empty folder.</summary>
    [Fact]
    public void Says_when_there_is_nothing_to_export()
    {
        var context = new ContextPackViewModel();
        var messages = new List<string>();
        context.StatusReported += messages.Add;

        context.ExportToDirectory(Path.GetTempPath());

        Assert.Contains("nothing in the pack", Assert.Single(messages), StringComparison.OrdinalIgnoreCase);
    }
}
