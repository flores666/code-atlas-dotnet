using CodeAtlas.Core.Model;
using CodeAtlas.Desktop.ViewModels;

namespace CodeAtlas.Desktop.Tests;

/// <summary>
/// How the impact section presents a report. The analysis itself is covered against a
/// real index in CodeAtlas.Core.Tests; what matters here is that the presentation keeps
/// the distinctions the report makes — direct from transitive, and a risk level from the
/// facts under it.
/// </summary>
public class ImpactViewModelTests
{
    private static IndexedSymbol Symbol(string name, int id) => new()
    {
        Id = id,
        Kind = IndexedSymbolKind.Method,
        Name = name,
        Display = $"{name}()",
        FullyQualifiedName = $"Shop.Service.{name}()",
        ContainerFullyQualifiedName = "Shop.Service",
        FilePath = "/repo/Service.cs",
        Line = 12,
    };

    [Fact]
    public void Starts_with_nothing_analysed()
    {
        var impact = new ImpactViewModel();

        Assert.False(impact.HasReport);
        Assert.Empty(impact.Summary);
        Assert.Empty(impact.RiskSignals);
        Assert.Equal("No symbol analysed", impact.RootTitle);
        Assert.False(impact.AnalyzeCommand.CanExecute(null));
    }

    /// <summary>Nothing runs until a database is behind it, so a stray click is harmless.</summary>
    [Fact]
    public void Analysing_without_an_index_reports_nothing()
    {
        var impact = new ImpactViewModel();

        impact.Analyze(42);
        impact.IsActive = true;

        Assert.False(impact.HasReport);
    }

    /// <summary>
    /// Distance travels with the row, which is what stops a transitive effect from reading
    /// like a direct one.
    /// </summary>
    [Fact]
    public void Carries_the_hop_count_on_each_row()
    {
        var direct = new ImpactRowViewModel(new ImpactedSymbol(Symbol("Total", 1), 1));
        var distant = new ImpactRowViewModel(new ImpactedSymbol(Symbol("Report", 2), 4));

        Assert.Equal("1 hop", direct.DistanceText);
        Assert.Equal("4 hops", distant.DistanceText);
        Assert.Equal("Shop.Service", direct.Container);
        Assert.Equal("Service.cs:12", direct.Origin);
        Assert.True(direct.IsNavigable);
    }

    /// <summary>A relation the index already resolved is one hop away by definition.</summary>
    [Fact]
    public void Treats_a_resolved_relation_as_one_hop()
    {
        var row = new ImpactRowViewModel(new SymbolLink(7, "Shop.PricingService", "PricingService"));

        Assert.Equal(1, row.Distance);
        Assert.Equal("PricingService", row.Display);
        Assert.True(row.IsNavigable);
    }

    /// <summary>A link with no symbol behind it cannot be opened, and says so.</summary>
    [Fact]
    public void Marks_an_unresolved_relation_as_not_navigable()
    {
        var row = new ImpactRowViewModel(new SymbolLink(null, "System.IDisposable", "IDisposable"));

        Assert.False(row.IsNavigable);
    }

    /// <summary>
    /// Every signal is shown with its evidence whether or not it fired, so the surface
    /// accounts for what it ruled out as well as what it found.
    /// </summary>
    [Fact]
    public void Shows_a_signal_with_its_evidence_either_way()
    {
        var present = new RiskSignalViewModel(new RiskSignal(
            RiskSignalKind.Persistence, IsPresent: true, "Persistence involved", "2 entities."));
        var absent = new RiskSignalViewModel(new RiskSignal(
            RiskSignalKind.Authorization, IsPresent: false, "Authorization involved", "No reachable endpoint."));

        Assert.True(present.IsPresent);
        Assert.Equal("2 entities.", present.Evidence);

        Assert.False(absent.IsPresent);
        Assert.Equal("No reachable endpoint.", absent.Evidence);
        Assert.NotEqual(present.Marker, absent.Marker);
    }

    /// <summary>A count of zero is stated rather than hidden: it is part of the answer.</summary>
    [Fact]
    public void States_an_empty_count()
    {
        var empty = new ImpactCountViewModel("External integrations", 0);
        var some = new ImpactCountViewModel("Direct callers", 8);

        Assert.True(empty.IsEmpty);
        Assert.False(some.IsEmpty);
        Assert.Equal(8, some.Count);
    }
}

/// <summary>
/// The risk level is a stated count of present signals, so it can be checked rather than
/// taken on trust.
/// </summary>
public class RiskSurfaceTests
{
    private static RiskSurface Surface(int present)
    {
        var signals = Enum.GetValues<RiskSignalKind>()
            .Select((kind, index) => new RiskSignal(kind, index < present, kind.ToString(), "evidence"))
            .ToList();

        return new RiskSurface(signals);
    }

    [Theory]
    [InlineData(0, RiskLevel.Low)]
    [InlineData(2, RiskLevel.Low)]
    [InlineData(3, RiskLevel.Moderate)]
    [InlineData(4, RiskLevel.Moderate)]
    [InlineData(5, RiskLevel.High)]
    [InlineData(7, RiskLevel.High)]
    public void Follows_the_stated_thresholds(int present, RiskLevel expected) =>
        Assert.Equal(expected, Surface(present).Level);

    [Fact]
    public void States_the_rule_it_applied()
    {
        var surface = Surface(present: 4);

        Assert.Equal(4, surface.PresentCount);
        Assert.Contains("4 of 7", surface.Rationale);
        Assert.Contains(RiskSurface.HighThreshold.ToString(), surface.Rationale);
    }
}
