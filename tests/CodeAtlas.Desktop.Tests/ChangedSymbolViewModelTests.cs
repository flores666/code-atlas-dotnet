using CodeAtlas.Core.Model;
using CodeAtlas.Desktop.ViewModels;

namespace CodeAtlas.Desktop.Tests;

/// <summary>
/// How the tests over a changed symbol read in the list. What relates a test to a symbol
/// is covered against a real index in CodeAtlas.Core.Tests; what matters here is that a
/// row never overstates what was found.
/// </summary>
public class ChangedSymbolViewModelTests
{
    private static ChangedSymbol Changed(bool isMeaningful, params RelatedTest[] tests) => new()
    {
        SymbolId = 7,
        Display = "Get(int id)",
        Kind = IndexedSymbolKind.Method,
        Change = SymbolChangeKind.Modified,
        Container = "Shop.UserService",
        FilePath = "/repo/src/Shop/UserService.cs",
        Line = 19,
        Tests = tests,
        IsMeaningful = isMeaningful,
    };

    private static RelatedTest Test(int id, TestRelationStrategy strategy) => new(
        new TestMethod
        {
            SymbolId = id,
            Display = $"Case_{id}()",
            FullyQualifiedName = $"Shop.Tests.UserServiceTests.Case_{id}()",
            Framework = TestFramework.XUnit,
            ClassDisplay = "UserServiceTests",
        },
        strategy,
        "UserService");

    [Fact]
    public void Counts_the_exact_tests_apart_from_the_probable_ones()
    {
        var row = new ChangedSymbolViewModel(Changed(
            isMeaningful: true,
            Test(1, TestRelationStrategy.DirectReference),
            Test(2, TestRelationStrategy.NamingConvention)));

        Assert.Equal("2 tests · 1 exact", row.TestSummary);
        Assert.False(row.IsUntested);
        Assert.Equal("UserServiceTests.Case_1()", row.Tests[0].Name);
        Assert.True(row.Tests[0].IsExact);
        Assert.Equal("probable", row.Tests[1].Confidence);
        Assert.Equal("named after UserService", row.Tests[1].Reason);
    }

    [Fact]
    public void Says_so_plainly_when_only_guesses_were_found()
    {
        var row = new ChangedSymbolViewModel(Changed(
            isMeaningful: true,
            Test(1, TestRelationStrategy.NamespaceSimilarity)));

        Assert.Equal("1 probable", row.TestSummary);
        Assert.False(row.IsUntested);
    }

    [Fact]
    public void Warns_only_about_a_meaningful_change_with_nothing_over_it()
    {
        var warned = new ChangedSymbolViewModel(Changed(isMeaningful: true));
        var quiet = new ChangedSymbolViewModel(Changed(isMeaningful: false));

        Assert.True(warned.IsUntested);
        Assert.Equal("no tests found", warned.TestSummary);

        Assert.False(quiet.IsUntested);
        Assert.False(quiet.HasTestSummary);
    }

    [Fact]
    public void Defers_a_long_list_to_the_details_pane()
    {
        var row = new ChangedSymbolViewModel(Changed(
            isMeaningful: true,
            [.. Enumerable.Range(1, 6).Select(id => Test(id, TestRelationStrategy.DirectReference))]));

        Assert.Equal(4, row.Tests.Count);
        Assert.True(row.HasMore);
        Assert.Equal("+2 more in the details pane", row.MoreSummary);
    }
}
