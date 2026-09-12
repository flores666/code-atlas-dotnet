using CodeAtlas.Core.Model;
using CodeAtlas.Desktop.ViewModels;

namespace CodeAtlas.Desktop.Tests;

/// <summary>
/// How the graph section behaves once it is told what changed. The mapping itself is
/// covered against a real working tree in CodeAtlas.Core.Tests; what matters here is that
/// the chip is only offered when it can do something, and never leaves the graph filtered
/// by a set that no longer exists.
/// </summary>
public class ChangedCodeFilterTests
{
    [Fact]
    public void Does_not_offer_the_filter_in_a_clean_working_tree()
    {
        var graph = new GraphViewModel();

        Assert.False(graph.CanFilterChanged);
        Assert.False(graph.ShowChangedOnly);
    }

    [Fact]
    public void Offers_the_filter_once_something_has_changed()
    {
        var graph = new GraphViewModel();

        graph.SetChangedSymbols(new HashSet<long> { 1, 2 });

        Assert.True(graph.CanFilterChanged);
    }

    /// <summary>
    /// A filter that can no longer be satisfied is dropped rather than left on: the chip
    /// disappears with the changed set, and a filter the toolbar has stopped showing must
    /// not still be emptying the graph.
    /// </summary>
    [Fact]
    public void Drops_the_filter_when_the_changed_set_empties()
    {
        var graph = new GraphViewModel();
        graph.SetChangedSymbols(new HashSet<long> { 1 });
        graph.ShowChangedOnly = true;

        graph.SetChangedSymbols(new HashSet<long>());

        Assert.False(graph.CanFilterChanged);
        Assert.False(graph.ShowChangedOnly);
    }

    [Fact]
    public void Keeps_the_filter_while_there_is_still_a_changed_set()
    {
        var graph = new GraphViewModel();
        graph.SetChangedSymbols(new HashSet<long> { 1 });
        graph.ShowChangedOnly = true;

        graph.SetChangedSymbols(new HashSet<long> { 5, 6 });

        Assert.True(graph.ShowChangedOnly);
    }
}

/// <summary>
/// The Git Changes section with no repository behind it, which is a normal state rather
/// than a failure and has to read as one.
/// </summary>
public class GitChangesViewModelTests
{
    [Fact]
    public void Says_a_workspace_outside_git_has_no_baseline()
    {
        var changes = new GitChangesViewModel();

        changes.SetWorkspace(new WorkspaceTarget
        {
            Kind = WorkspaceTargetKind.Solution,
            Path = "/somewhere/App.sln",
            DisplayName = "App",
        });

        Assert.False(changes.HasRepository);
        Assert.Contains("not inside a Git", changes.StatusSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(changes.Symbols);
        Assert.Empty(changes.Files);
        Assert.False(changes.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public void Clears_itself_when_the_workspace_closes()
    {
        var changes = new GitChangesViewModel();
        var cleared = new List<RepositoryChanges>();
        changes.ChangesUpdated += updated => cleared.Add(updated);

        changes.SetWorkspace(null);

        Assert.False(changes.HasRepository);
        Assert.Empty(changes.Commits);
        Assert.Single(cleared);
        Assert.Empty(cleared[0].SymbolIds);
    }

    [Fact]
    public void Starts_on_the_changed_symbols_view()
    {
        var changes = new GitChangesViewModel();

        Assert.True(changes.IsSymbolsView);
        Assert.False(changes.IsFilesView);
        Assert.False(changes.IsHistoryView);
    }

    [Fact]
    public void Switches_view()
    {
        var changes = new GitChangesViewModel();

        changes.SetViewCommand.Execute(GitChangesView.Files);

        Assert.True(changes.IsFilesView);
        Assert.False(changes.IsSymbolsView);
    }
}
