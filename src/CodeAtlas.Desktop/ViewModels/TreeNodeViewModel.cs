using System.Collections.ObjectModel;
using CodeAtlas.Core.Model;
using CodeAtlas.Desktop.Mvvm;

namespace CodeAtlas.Desktop.ViewModels;

/// <summary>
/// One row of the project tree. Children are fetched the first time a node is expanded,
/// so opening a large solution does not have to materialise every member up front.
/// </summary>
public sealed class TreeNodeViewModel : ObservableObject
{
    private static readonly TreeNodeViewModel Placeholder = new("Loading...", "", "");

    private readonly Func<Task<IReadOnlyList<TreeNodeViewModel>>>? _loadChildren;
    private bool _isExpanded;
    private bool _loaded;

    private TreeNodeViewModel(
        string title,
        string kind,
        string glyph,
        bool isContainer = false,
        Func<Task<IReadOnlyList<TreeNodeViewModel>>>? loadChildren = null,
        IndexedSymbol? symbol = null)
    {
        Title = title;
        Kind = kind;
        Glyph = glyph;
        IsContainer = isContainer;
        Symbol = symbol;
        _loadChildren = loadChildren;

        if (loadChildren is not null)
        {
            // Gives the node an expander before its real children are known.
            Children.Add(Placeholder);
        }
    }

    public string Title { get; }

    /// <summary>"Project", "Namespace", or the symbol kind.</summary>
    public string Kind { get; }

    /// <summary>The badge glyph for this row's kind.</summary>
    public string Glyph { get; }

    /// <summary>True for rows that hold other symbols; their glyph is weighted heavier.</summary>
    public bool IsContainer { get; }

    /// <summary>Projects head a subtree, so their title stands out from the symbols under it.</summary>
    public bool IsProject => Kind == "Project";

    /// <summary>False only on a project whose compilation failed, which is flagged in the row.</summary>
    public bool IsLoaded { get; private init; } = true;

    /// <summary>The symbol this row stands for, or <c>null</c> for project and namespace rows.</summary>
    public IndexedSymbol? Symbol { get; }

    public ObservableCollection<TreeNodeViewModel> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value) && value)
            {
                _ = LoadChildrenAsync();
            }
        }
    }

    public static TreeNodeViewModel ForProject(
        IndexedProject project,
        Func<Task<IReadOnlyList<TreeNodeViewModel>>> loadChildren) =>
        new(project.Name, "Project", SymbolGlyph.Project, isContainer: true, loadChildren)
        {
            IsLoaded = project.Loaded,
        };

    public static TreeNodeViewModel ForNamespace(
        string @namespace,
        Func<Task<IReadOnlyList<TreeNodeViewModel>>> loadChildren) =>
        new(
            string.IsNullOrEmpty(@namespace) ? "<global namespace>" : @namespace,
            "Namespace",
            SymbolGlyph.Namespace,
            isContainer: true,
            loadChildren);

    public static TreeNodeViewModel ForType(
        IndexedSymbol symbol,
        Func<Task<IReadOnlyList<TreeNodeViewModel>>> loadChildren) =>
        new(
            symbol.Display,
            symbol.Kind.ToString(),
            SymbolGlyph.For(symbol.Kind),
            SymbolGlyph.IsContainer(symbol.Kind),
            loadChildren,
            symbol);

    public static TreeNodeViewModel ForMember(IndexedSymbol symbol) =>
        new(
            symbol.Display,
            symbol.Kind.ToString(),
            SymbolGlyph.For(symbol.Kind),
            SymbolGlyph.IsContainer(symbol.Kind),
            loadChildren: null,
            symbol);

    private async Task LoadChildrenAsync()
    {
        if (_loaded || _loadChildren is null)
        {
            return;
        }

        _loaded = true;

        try
        {
            var children = await _loadChildren();

            Children.Clear();
            foreach (var child in children)
            {
                Children.Add(child);
            }
        }
        catch (Exception e)
        {
            // A tree node that cannot expand must not take the window down with it.
            Children.Clear();
            Children.Add(new TreeNodeViewModel($"Could not load: {e.Message}", "", ""));
        }
    }
}
