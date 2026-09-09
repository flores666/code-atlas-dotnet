using CodeAtlas.Core.Model;
using CodeAtlas.Desktop.ViewModels;

namespace CodeAtlas.Desktop.Tests;

/// <summary>
/// How a changed symbol reads in the list. What relates a test to a symbol is covered
/// against a real index in CodeAtlas.Core.Tests; what matters here is that a row never
/// overstates what was found.
/// </summary>
public class ChangedSymbolViewModelTests
{
    private static readonly IndexedSymbol Method = new()
    {
        Id = 7,
        Kind = IndexedSymbolKind.Method,
        Name = "Get",
        Display = "Get(int id)",
        FullyQualifiedName = "Shop.UserService.Get(System.Int32)",
        ContainerFullyQualifiedName = "Shop.UserService",
        FilePath = "/repo/src/Shop/UserService.cs",
        Line = 19,
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
        var row = new ChangedSymbolViewModel(new ChangedSymbol(
            Method,
            [
                Test(1, TestRelationStrategy.DirectReference),
                Test(2, TestRelationStrategy.NamingConvention),
            ],
            IsMeaningful: true));

        Assert.Equal("2 tests · 1 exact", row.Summary);
        Assert.False(row.IsUntested);
        Assert.Equal("UserServiceTests.Case_1()", row.Tests[0].Name);
        Assert.True(row.Tests[0].IsExact);
        Assert.Equal("probable", row.Tests[1].Confidence);
        Assert.Equal("named after UserService", row.Tests[1].Reason);
    }

    [Fact]
    public void Says_so_plainly_when_only_guesses_were_found()
    {
        var row = new ChangedSymbolViewModel(new ChangedSymbol(
            Method,
            [Test(1, TestRelationStrategy.NamespaceSimilarity)],
            IsMeaningful: true));

        Assert.Equal("1 probable", row.Summary);
        Assert.False(row.IsUntested);
    }

    [Fact]
    public void Warns_only_about_a_meaningful_change_with_nothing_over_it()
    {
        var warned = new ChangedSymbolViewModel(new ChangedSymbol(Method, [], IsMeaningful: true));
        var quiet = new ChangedSymbolViewModel(new ChangedSymbol(Method, [], IsMeaningful: false));

        Assert.True(warned.IsUntested);
        Assert.Equal("no tests found", warned.Summary);

        Assert.False(quiet.IsUntested);
        Assert.False(quiet.HasSummary);
    }

    [Fact]
    public void Defers_a_long_list_to_the_details_pane()
    {
        var row = new ChangedSymbolViewModel(new ChangedSymbol(
            Method,
            [.. Enumerable.Range(1, 6).Select(id => Test(id, TestRelationStrategy.DirectReference))],
            IsMeaningful: true));

        Assert.Equal(4, row.Tests.Count);
        Assert.True(row.HasMore);
        Assert.Equal("+2 more in the details pane", row.MoreSummary);
    }
}
