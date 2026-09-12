using System.Collections.ObjectModel;
using System.Globalization;
using CodeAtlas.Core.Context;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;
using CodeAtlas.Desktop.Mvvm;

namespace CodeAtlas.Desktop.ViewModels;

/// <summary>
/// The context builder: what goes into a pack, what it costs, and exactly what would be
/// exported.
/// </summary>
/// <remarks>
/// <para>
/// The pack itself is the model's; this is the choosing. Two things the spec asks for
/// shape the presentation and are not matters of taste: a suggested entry always shows why
/// it is relevant, and the budget is never exceeded quietly — a refused add says so, and a
/// budget lowered under a pack that is already bigger says that too.
/// </para>
/// <para>
/// Nothing leaves the machine. Export writes where the reader points it, and the analysed
/// repository is only ever read.
/// </para>
/// </remarks>
public sealed class ContextPackViewModel : ObservableObject
{
    private readonly List<ContextPackFile> _rendered = [];

    private ContextPackBuilder? _builder;
    private SymbolIndexDatabase? _database;
    private string _root = string.Empty;
    private CancellationTokenSource? _suggesting;

    private IndexedSymbol? _focus;
    private HttpEndpoint? _endpoint;
    private ImpactReport? _impact;
    private RepositoryChanges? _changes;

    private string _taskText = string.Empty;
    private string _budgetText = ContextPackBuilder.DefaultBudgetTokens.ToString(CultureInfo.InvariantCulture);
    private ContextItemViewModel? _selectedItem;
    private ContextPackFileViewModel? _selectedPreviewFile;

    public ContextPackViewModel()
    {
        AddCommand = new RelayCommand(
            parameter => Add((ContextItemKind)parameter!),
            parameter => parameter is ContextItemKind kind && CanCreate(kind));

        AddSuggestionCommand = new RelayCommand(
            parameter => Add((parameter as ContextSuggestionViewModel)?.Item));

        RemoveCommand = new RelayCommand(
            parameter => Remove(parameter as ContextItemViewModel),
            parameter => parameter is ContextItemViewModel);

        ClearCommand = new RelayCommand(_ => Clear(), _ => Items.Count > 0);
    }

    /// <summary>Raised with the sentence explaining what an action did, for the status bar.</summary>
    public event Action<string>? StatusReported;

    /// <summary>What is in the pack, in the order it was added.</summary>
    public ObservableCollection<ContextItemViewModel> Items { get; } = [];

    /// <summary>What CodeAtlas would add, each with the reason it is relevant.</summary>
    public ObservableCollection<ContextSuggestionViewModel> Suggestions { get; } = [];

    /// <summary>The pack as it would be written, one row per file.</summary>
    public ObservableCollection<ContextPackFileViewModel> PreviewFiles { get; } = [];

    public RelayCommand AddCommand { get; }

    public RelayCommand AddSuggestionCommand { get; }

    public RelayCommand RemoveCommand { get; }

    public RelayCommand ClearCommand { get; }

    public bool HasIndex => _builder is not null;

    public bool HasItems => Items.Count > 0;

    public bool HasSuggestions => Suggestions.Count > 0;

    public ContextItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set => SetProperty(ref _selectedItem, value);
    }

    /// <summary>What the agent is being asked to do.</summary>
    public string TaskText
    {
        get => _taskText;
        set
        {
            if (SetProperty(ref _taskText, value) && _builder is { } builder)
            {
                builder.TaskText = value;
                Refresh();
            }
        }
    }

    /// <summary>
    /// The ceiling, in estimated tokens. Blank or zero means no ceiling, which is a choice
    /// rather than the default.
    /// </summary>
    public string BudgetText
    {
        get => _budgetText;
        set
        {
            if (!SetProperty(ref _budgetText, value))
            {
                return;
            }

            if (_builder is { } builder)
            {
                builder.BudgetTokens = ParseBudget(value);
                Refresh();
            }
        }
    }

    public int BudgetTokens => _builder?.BudgetTokens ?? ParseBudget(_budgetText);

    public bool HasBudget => BudgetTokens > 0;

    // ---- size ---------------------------------------------------------------

    public ContextSize Size { get; private set; } = ContextSize.Zero;

    public string CharactersText => Size.Characters.ToString("N0", CultureInfo.CurrentCulture);

    public string LinesText => Size.Lines.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>Approximate, and says so wherever it is shown: there is no tokenizer here.</summary>
    public string TokensText => $"~{Size.Tokens.ToString("N0", CultureInfo.CurrentCulture)}";

    public string BudgetSummary => HasBudget
        ? $"{Size.Tokens:N0} of {BudgetTokens:N0} estimated tokens used"
        : "No budget set; the pack can grow to any size.";

    /// <summary>Portion of the budget used, 0-1, for the meter.</summary>
    public double BudgetFraction => HasBudget
        ? Math.Clamp((double)Size.Tokens / BudgetTokens, 0, 1)
        : 0;

    /// <summary>
    /// True when the pack is already bigger than the budget, which can only happen by
    /// lowering the budget under a pack that exists. Adds are refused before this, so it
    /// is a statement about what the reader just did rather than a silent overrun.
    /// </summary>
    public bool IsOverBudget => HasBudget && Size.Tokens > BudgetTokens;

    public string OverBudgetNotice =>
        $"This pack is ~{Size.Tokens - BudgetTokens:N0} tokens over the budget you set. " +
        "Remove something, or raise the budget.";

    // ---- preview ------------------------------------------------------------

    /// <summary>
    /// The file of the pack being read. Selecting one is how the exact contents are
    /// inspected before export: a pack is a folder, and it is previewed as one.
    /// </summary>
    public ContextPackFileViewModel? SelectedPreviewFile
    {
        get => _selectedPreviewFile;
        set
        {
            if (SetProperty(ref _selectedPreviewFile, value))
            {
                OnPropertyChanged(nameof(PreviewText));
                OnPropertyChanged(nameof(HasPreview));
            }
        }
    }

    /// <summary>The selected file's contents, exactly as they would be written.</summary>
    public string PreviewText => SelectedPreviewFile?.Text ?? string.Empty;

    public bool HasPreview => SelectedPreviewFile is not null;

    // ---- what the shell points it at ----------------------------------------

    public void SetWorkspace(WorkspaceTarget? target)
    {
        _root = target?.GitRoot ?? (target is null ? string.Empty : Path.GetDirectoryName(target.Path) ?? string.Empty);
        _focus = null;
        _endpoint = null;
        _impact = null;
        _changes = null;
        SetDatabase(null);
    }

    /// <summary>
    /// Hands over the index. A pack belongs to one index, so a reindex starts a new one
    /// rather than keeping entries whose row ids may no longer mean the same thing.
    /// </summary>
    public void SetDatabase(SymbolIndexDatabase? database)
    {
        _database = database;
        _builder = database is null
            ? null
            : new ContextPackBuilder(database, _root)
            {
                TaskText = _taskText,
                BudgetTokens = ParseBudget(_budgetText),
            };

        Items.Clear();
        Refresh();
        RaiseTargets();
        RefreshSuggestions();
    }

    /// <summary>The symbol the "add this" buttons act on: whatever the details pane shows.</summary>
    public void SetFocus(IndexedSymbol? symbol)
    {
        _focus = symbol;
        RaiseTargets();
        RefreshSuggestions();
    }

    public void SetEndpoint(HttpEndpoint? endpoint)
    {
        _endpoint = endpoint;
        RaiseTargets();
        RefreshSuggestions();
    }

    public void SetImpact(ImpactReport? report)
    {
        _impact = report;
        RaiseTargets();
        RefreshSuggestions();
    }

    public void SetChanges(RepositoryChanges? changes)
    {
        _changes = changes;
        RaiseTargets();
        RefreshSuggestions();
    }

    // ---- what the buttons say -----------------------------------------------

    public string FocusTitle => _focus?.Display ?? "No symbol selected";

    public string FocusSubtitle => _focus is { } symbol
        ? $"{SymbolGlyph.Keyword(symbol.Kind)} · {symbol.FullyQualifiedName}"
        : "Select a symbol anywhere in CodeAtlas and its callers, implementations, " +
          "dependencies and tests can be added here.";

    public bool HasFocus => _focus is not null;

    public bool HasEndpoint => _endpoint is not null;

    public bool HasImpact => _impact is not null;

    public bool HasChanges => _changes is { Diffs.Count: > 0 };

    public string EndpointTitle => _endpoint is { } endpoint
        ? $"{endpoint.HttpMethod} {endpoint.Route}"
        : "No endpoint selected";

    public string ImpactTitle => _impact is { } report
        ? $"Impact of {report.Root.Display}"
        : "No impact report yet";

    // ---- exporting ----------------------------------------------------------

    /// <summary>The pack as one block of text, which is what the clipboard gets.</summary>
    public string BuildText() => ContextPackWriter.ToText(_rendered);

    /// <summary>Writes the pack into a folder the reader chose.</summary>
    public void ExportToDirectory(string directory)
    {
        Export(() => ContextPackWriter.WriteDirectory(_rendered, directory));
    }

    public void ExportToZip(string zipPath)
    {
        Export(() => ContextPackWriter.WriteZip(_rendered, zipPath));
    }

    public void Report(string message) => StatusReported?.Invoke(message);

    /// <summary>Adds one kind of entry about whatever the builder is currently pointed at.</summary>
    public void Add(ContextItemKind kind) => Add(Create(kind));

    private void Export(Func<string> write)
    {
        if (_rendered.Count == 0)
        {
            StatusReported?.Invoke("There is nothing in the pack to export.");
            return;
        }

        try
        {
            StatusReported?.Invoke($"Wrote the context pack to {write()}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or NotSupportedException or InvalidOperationException)
        {
            StatusReported?.Invoke($"Could not write the context pack: {e.Message}");
        }
    }

    // ---- assembling ---------------------------------------------------------

    private bool CanCreate(ContextItemKind kind) => _builder is not null && kind switch
    {
        ContextItemKind.EndpointFlow => _endpoint is not null,
        ContextItemKind.Impact => _impact is not null,
        ContextItemKind.GitDiff => HasChanges,
        ContextItemKind.Architecture => true,
        _ => _focus is not null,
    };

    private ContextItem? Create(ContextItemKind kind)
    {
        if (_builder is not { } builder)
        {
            return null;
        }

        var id = _focus?.Id;

        return kind switch
        {
            ContextItemKind.Symbol => id is { } symbol ? builder.CreateSymbol(symbol) : null,
            ContextItemKind.SourceFile => builder.CreateSourceFile(_focus?.FilePath ?? string.Empty),
            ContextItemKind.Callers => id is { } symbol ? builder.CreateCallers(symbol) : null,
            ContextItemKind.Implementations => id is { } symbol ? builder.CreateImplementations(symbol) : null,
            ContextItemKind.Dependencies => id is { } symbol ? builder.CreateDependencies(symbol) : null,
            ContextItemKind.RelatedTests => id is { } symbol ? builder.CreateRelatedTests(symbol) : null,
            ContextItemKind.EndpointFlow => builder.CreateEndpointFlow(_endpoint),
            ContextItemKind.Impact => builder.CreateImpact(_impact),
            ContextItemKind.GitDiff => builder.CreateGitDiff(_changes),
            _ => builder.CreateArchitecture(),
        };
    }

    /// <summary>
    /// Adds an entry and says what happened to it. A refusal is reported the same way an
    /// addition is, so a pack never grows or fails to grow without the reader being told.
    /// </summary>
    private void Add(ContextItem? item)
    {
        if (_builder is not { } builder)
        {
            return;
        }

        var result = builder.Add(item);
        StatusReported?.Invoke(result.Message);

        if (result.IsAdded)
        {
            Refresh();
            RefreshSuggestions();
        }
    }

    private void Remove(ContextItemViewModel? row)
    {
        if (row is null || _builder is not { } builder || !builder.Remove(row.Key))
        {
            return;
        }

        StatusReported?.Invoke($"Removed {row.Title} from the pack.");
        Refresh();
        RefreshSuggestions();
    }

    private void Clear()
    {
        _builder?.Clear();
        StatusReported?.Invoke("Emptied the context pack.");
        Refresh();
        RefreshSuggestions();
    }

    /// <summary>
    /// Re-reads the pack: its entries, its exact contents and what those cost. One place,
    /// so the list, the preview and the numbers are always the same pack.
    /// </summary>
    private void Refresh()
    {
        // Keeping the reader on the file they were reading: a pack changes shape with
        // every add, and losing the preview each time would make it unreadable.
        var reading = SelectedPreviewFile?.Path;

        Items.Clear();
        _rendered.Clear();
        PreviewFiles.Clear();

        if (_builder is { } builder)
        {
            foreach (var item in builder.Items)
            {
                Items.Add(new ContextItemViewModel(item));
            }

            _rendered.AddRange(builder.Render());
        }

        // Measured on the rendered files themselves rather than asked of the builder,
        // which would render a second time for the same answer.
        Size = _rendered.Aggregate(ContextSize.Zero, (total, file) => total + file.Size);

        foreach (var file in _rendered)
        {
            PreviewFiles.Add(new ContextPackFileViewModel(file));
        }

        SelectedPreviewFile = PreviewFiles.FirstOrDefault(file => file.Path == reading)
                              ?? PreviewFiles.FirstOrDefault();

        ClearCommand.RaiseCanExecuteChanged();

        foreach (var property in (string[])
                 [
                     nameof(HasIndex), nameof(HasItems), nameof(Size), nameof(CharactersText),
                     nameof(LinesText), nameof(TokensText), nameof(BudgetSummary), nameof(BudgetFraction),
                     nameof(BudgetTokens), nameof(HasBudget), nameof(IsOverBudget), nameof(OverBudgetNotice),
                 ])
        {
            OnPropertyChanged(property);
        }
    }

    /// <summary>
    /// Rebuilds the suggestions off the UI thread. They are built rather than merely named
    /// so each one can say what it is worth before it is taken.
    /// </summary>
    private void RefreshSuggestions()
    {
        _suggesting?.Cancel();
        _suggesting?.Dispose();
        _suggesting = null;

        if (_builder is not { } builder)
        {
            Suggestions.Clear();
            OnPropertyChanged(nameof(HasSuggestions));
            return;
        }

        _suggesting = new CancellationTokenSource();
        var cancellationToken = _suggesting.Token;
        var symbolId = _focus?.Id;
        var impact = _impact;
        var changes = _changes;

        _ = LoadAsync();

        async Task LoadAsync()
        {
            try
            {
                var suggested = await Task.Run(
                    () => builder.Suggest(symbolId, impact, changes),
                    cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();

                Suggestions.Clear();
                foreach (var item in suggested)
                {
                    Suggestions.Add(new ContextSuggestionViewModel(item));
                }

                OnPropertyChanged(nameof(HasSuggestions));
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer selection.
            }
        }
    }

    private void RaiseTargets()
    {
        AddCommand.RaiseCanExecuteChanged();

        foreach (var property in (string[])
                 [
                     nameof(FocusTitle), nameof(FocusSubtitle), nameof(HasFocus), nameof(HasEndpoint),
                     nameof(HasImpact), nameof(HasChanges), nameof(EndpointTitle), nameof(ImpactTitle),
                 ])
        {
            OnPropertyChanged(property);
        }
    }

    /// <summary>Blank or unparsable reads as no budget rather than as zero tokens allowed.</summary>
    private static int ParseBudget(string text) =>
        int.TryParse(text.Replace(",", string.Empty, StringComparison.Ordinal).Trim(),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var budget) && budget > 0
            ? budget
            : 0;
}

/// <summary>One entry of the pack.</summary>
public sealed class ContextItemViewModel(ContextItem item)
{
    public string Key { get; } = item.Key;

    public string Title { get; } = item.Title;

    public string Kind { get; } = item.KindLabel;

    /// <summary>Why it is in the pack, which is what makes the pack auditable.</summary>
    public string Reason { get; } = item.Reason;

    /// <summary>Where it lands in the exported folder.</summary>
    public string Destination { get; } = ContextDocuments.FileName(item.Document);

    public int FileCount { get; } = item.Files.Count;

    public bool HasFiles => FileCount > 0;

    public string FileSummary { get; } = item.Files.Count switch
    {
        0 => string.Empty,
        1 => "1 file",
        var count => $"{count} files",
    };
}

/// <summary>
/// One thing CodeAtlas would add, with the reason and what it would cost.
/// </summary>
/// <remarks>
/// The cost is the entry's own text and files measured on their own; the pack's total will
/// be a little smaller when a file it names is already in the pack, because a file is
/// carried once however many entries want it.
/// </remarks>
public sealed class ContextSuggestionViewModel(ContextItem item)
{
    public ContextItem Item { get; } = item;

    public string Title { get; } = item.Title;

    public string Kind { get; } = item.KindLabel;

    public string Reason { get; } = item.Reason;

    public string CostSummary { get; } = Describe(item);

    private static string Describe(ContextItem item)
    {
        var size = item.Files.Aggregate(
            ContextSize.Of(item.Body),
            (total, file) => total + ContextSize.Of(file.Text));

        var files = item.Files.Count switch
        {
            0 => string.Empty,
            1 => " · 1 file",
            var count => $" · {count} files",
        };

        return $"~{size.Tokens:N0} tokens{files}";
    }
}

/// <summary>One file of the rendered pack, as the preview lists it.</summary>
public sealed class ContextPackFileViewModel(ContextPackFile file)
{
    public string Path { get; } = file.Path;

    public string Text { get; } = file.Text;

    public string Summary { get; } =
        $"{file.Size.Lines:N0} lines · {file.Size.Characters:N0} chars · ~{file.Size.Tokens:N0} tokens";
}
