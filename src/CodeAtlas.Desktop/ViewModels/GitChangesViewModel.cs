using System.Collections.ObjectModel;
using System.Globalization;
using CodeAtlas.Core.Git;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;
using CodeAtlas.Desktop.Mvvm;

namespace CodeAtlas.Desktop.ViewModels;

/// <summary>The three cuts the Git Changes section offers, shown one at a time.</summary>
public enum GitChangesView
{
    /// <summary>What changed, in the terms the rest of CodeAtlas speaks.</summary>
    Symbols,

    /// <summary>What changed, in Git's own terms, with the diff of the selected path.</summary>
    Files,

    /// <summary>Recent commits, or the history of the selected file.</summary>
    History,
}

/// <summary>
/// How the working tree differs from its Git baseline.
/// </summary>
/// <remarks>
/// <para>
/// Its own view model rather than more state on the shell, for the same reason the graph
/// is: it owns a query of its own, refreshes on its own schedule, and the shell only needs
/// to know which symbol the reader picked.
/// </para>
/// <para>
/// Nothing here is cached in the index. Git state moves whenever the reader touches a
/// file, so it is read on demand and <see cref="RefreshCommand"/> is what re-reads it —
/// there is no file watcher, and a stale view is one click from being current.
/// </para>
/// </remarks>
public sealed class GitChangesViewModel : ObservableObject
{
    /// <summary>Enough history to orient in, few enough to read without scrolling far.</summary>
    private const int CommitCount = 20;

    private readonly List<ChangedSymbolViewModel> _allSymbols = [];
    private readonly List<ChangedFileViewModel> _allFiles = [];

    private GitRepository? _repository;
    private SymbolIndexDatabase? _database;
    private RepositoryChanges _changes = RepositoryChanges.Empty;
    private CancellationTokenSource? _refresh;

    private GitChangesView _view = GitChangesView.Symbols;
    private ChangedSymbolViewModel? _selectedSymbol;
    private ChangedFileViewModel? _selectedFile;
    private string _filter = string.Empty;
    private string _diffText = string.Empty;
    private string _historyCaption = "Recent commits";
    private bool _isLoading;
    private bool _hasRepository;
    private bool _hasRead;

    public GitChangesViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), _ => _hasRepository && !IsLoading);
        SetViewCommand = new RelayCommand(parameter => View = (GitChangesView)parameter!);
        OpenFileCommand = new RelayCommand(parameter => OpenFile(parameter as ChangedFileViewModel));
    }

    /// <summary>Raised when a changed symbol is picked, so the shell can show its relationships.</summary>
    public event Action<long>? SymbolSelected;

    /// <summary>
    /// Raised when the working tree has been re-read, carrying what it now differs by:
    /// the ids the graph's "Changed code" filter narrows to, and the diff a context pack
    /// is built from.
    /// </summary>
    public event Action<RepositoryChanges>? ChangesUpdated;

    public event Action<string>? StatusReported;

    public ObservableCollection<ChangedSymbolViewModel> Symbols { get; } = [];

    public ObservableCollection<ChangedFileViewModel> Files { get; } = [];

    public ObservableCollection<CommitViewModel> Commits { get; } = [];

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand SetViewCommand { get; }

    public RelayCommand OpenFileCommand { get; }

    /// <summary>False when the workspace is not inside a working tree, which is the empty state.</summary>
    public bool HasRepository
    {
        get => _hasRepository;
        private set
        {
            if (SetProperty(ref _hasRepository, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(StatusSummary));
            }
        }
    }

    public string Branch => _changes.Status.Branch;

    public bool IsDetached => _changes.Status.IsDetached;

    /// <summary>
    /// The one-line account of the working tree: the counts a reader checks before
    /// anything else.
    /// </summary>
    public string StatusSummary
    {
        get
        {
            var status = _changes.Status;

            if (!HasRepository)
            {
                return "This workspace is not inside a Git working tree.";
            }

            // An empty status is not evidence of a clean tree until Git has actually been
            // read, and the first read waits for indexing to finish — long enough that
            // claiming "clean" in the meantime would be a real misstatement.
            if (IsLoading)
            {
                return "Reading the working tree\u2026";
            }

            if (!_hasRead)
            {
                return "Not read yet.";
            }

            if (status.IsClean)
            {
                return "The working tree is clean.";
            }

            var parts = new List<string>();
            Count(status.Staged.Count, "staged");
            Count(status.Modified.Count, "modified");
            Count(status.Untracked.Count, "untracked");

            return string.Join(" · ", parts);

            void Count(int count, string label)
            {
                if (count > 0)
                {
                    parts.Add($"{count} {label}");
                }
            }
        }
    }

    public string Subtitle => HasRepository
        ? $"How the working tree differs from {(IsDetached ? Branch : $"the {Branch} baseline")}."
        : "Open a workspace inside a Git repository to see what has changed.";

    public int ChangedFileCount => _allFiles.Count;

    public int ChangedSymbolCount => _allSymbols.Count;

    public bool HasChangedSymbols => Symbols.Count > 0;

    public bool HasChangedFiles => Files.Count > 0;

    public bool HasCommits => Commits.Count > 0;

    /// <summary>
    /// Why the symbol list is empty while files have changed — a change outside C#, or in
    /// a file this workspace does not index, has nothing to map onto.
    /// </summary>
    public string EmptySymbolsNotice => _allFiles.Count == 0
        ? "Nothing in the working tree differs from the baseline."
        : "The changed files declare nothing this index holds. A change outside C#, or in a " +
          "project that is not part of this workspace, has no symbol to map onto.";

    public GitChangesView View
    {
        get => _view;
        set
        {
            if (SetProperty(ref _view, value))
            {
                foreach (var property in (string[])
                         [nameof(IsSymbolsView), nameof(IsFilesView), nameof(IsHistoryView)])
                {
                    OnPropertyChanged(property);
                }
            }
        }
    }

    public bool IsSymbolsView => View == GitChangesView.Symbols;

    public bool IsFilesView => View == GitChangesView.Files;

    public bool IsHistoryView => View == GitChangesView.History;

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value))
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>The selected file's diff, verbatim. Empty until a file is selected.</summary>
    public string DiffText
    {
        get => _diffText;
        private set
        {
            if (SetProperty(ref _diffText, value))
            {
                OnPropertyChanged(nameof(HasDiff));
            }
        }
    }

    public bool HasDiff => DiffText.Length > 0;

    /// <summary>Says whose history the commit list is showing: the branch, or one file.</summary>
    public string HistoryCaption
    {
        get => _historyCaption;
        private set => SetProperty(ref _historyCaption, value);
    }

    /// <summary>
    /// Selecting a changed symbol shows it, which is what turns a diff into a set of
    /// semantic relationships: its callers, callees, implementations, the endpoints above
    /// it and the entities below it are all already recorded against it.
    /// </summary>
    public ChangedSymbolViewModel? SelectedSymbol
    {
        get => _selectedSymbol;
        set
        {
            if (!SetProperty(ref _selectedSymbol, value) || value is null)
            {
                return;
            }

            if (value.SymbolId is { } id)
            {
                SymbolSelected?.Invoke(id);
            }
            else
            {
                StatusReported?.Invoke(
                    $"{value.Display} was removed, so there is nothing left in the index to explore.");
            }
        }
    }

    /// <summary>
    /// Selecting a file shows its diff and its history: the two things Git knows about a
    /// path that the index does not.
    /// </summary>
    public ChangedFileViewModel? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (SetProperty(ref _selectedFile, value) && value is not null)
            {
                DiffText = value.Diff;
                _ = LoadHistoryAsync(value);
            }
        }
    }

    /// <summary>
    /// Points the section at a workspace, without reading anything yet.
    /// </summary>
    /// <remarks>
    /// A workspace outside Git leaves the section empty rather than in an error state: not
    /// being under version control is a normal condition, not a failure.
    /// </remarks>
    public void SetWorkspace(WorkspaceTarget? target)
    {
        _repository = target?.GitRoot is { } root ? GitRepository.Open(root) : null;
        _database = null;
        HasRepository = _repository is not null;

        Clear();
        RaiseSummary();
    }

    /// <summary>
    /// Hands over the index to map onto and re-reads Git.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="SetWorkspace"/> so opening a workspace reads Git exactly
    /// once — after indexing, when there is something to map changed lines onto. A
    /// <c>null</c> index still produces the file-level answer, which is what keeps the
    /// section useful when indexing failed.
    /// </remarks>
    public void SetIndex(SymbolIndexDatabase? database)
    {
        _database = database;

        if (_repository is not null)
        {
            _ = RefreshAsync();
        }
    }

    /// <summary>Re-reads Git and re-maps it onto the current index.</summary>
    public async Task RefreshAsync()
    {
        if (_repository is not { } repository)
        {
            return;
        }

        _refresh?.Cancel();
        _refresh?.Dispose();
        _refresh = new CancellationTokenSource();
        var cancellationToken = _refresh.Token;

        IsLoading = true;

        try
        {
            var database = _database;
            var (changes, commits) = await Task.Run(
                () => (
                    RepositoryChangeAnalyzer.Analyze(repository, database, cancellationToken),
                    repository.GetRecentCommits(CommitCount, cancellationToken)),
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            _changes = changes;
            Load(changes, commits);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer refresh.
        }
        catch (Exception e)
        {
            // Git being unreadable is worth saying out loud, and is not fatal: every other
            // section still works off the index.
            StatusReported?.Invoke($"Could not read Git state: {e.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Load(RepositoryChanges changes, IReadOnlyList<GitCommit> commits)
    {
        _hasRead = true;

        var diffs = changes.Diffs.ToDictionary(diff => diff.Path, StringComparer.OrdinalIgnoreCase);

        _allFiles.Clear();
        _allFiles.AddRange(changes.Status.Files.Select(file => new ChangedFileViewModel(
            file,
            diffs.TryGetValue(file.Path, out var diff) ? diff : null)));

        _allSymbols.Clear();
        _allSymbols.AddRange(changes.Symbols.Select(symbol => new ChangedSymbolViewModel(symbol)));

        Commits.Clear();
        foreach (var commit in commits)
        {
            Commits.Add(new CommitViewModel(commit));
        }

        HistoryCaption = "Recent commits";
        DiffText = string.Empty;
        _selectedFile = null;
        _selectedSymbol = null;
        OnPropertyChanged(nameof(SelectedFile));
        OnPropertyChanged(nameof(SelectedSymbol));

        ApplyFilter();
        RaiseSummary();

        ChangesUpdated?.Invoke(changes);
    }

    private async Task LoadHistoryAsync(ChangedFileViewModel file)
    {
        if (_repository is not { } repository)
        {
            return;
        }

        // An untracked file has no history to read, so the list keeps the branch's and the
        // caption says why rather than showing commits that are not this file's.
        if (file.Status.IsUntracked)
        {
            HistoryCaption = $"{file.Name} is untracked and has no history yet";
            return;
        }

        var path = file.Status.Path;

        try
        {
            var commits = await Task.Run(() => repository.GetFileHistory(path, CommitCount));

            // Ignored if the reader has moved on to another file in the meantime.
            if (!ReferenceEquals(_selectedFile, file))
            {
                return;
            }

            Commits.Clear();
            foreach (var commit in commits)
            {
                Commits.Add(new CommitViewModel(commit));
            }

            HistoryCaption = $"History of {file.Name}";
            OnPropertyChanged(nameof(HasCommits));
        }
        catch (Exception e)
        {
            StatusReported?.Invoke($"Could not read the history of {file.Name}: {e.Message}");
        }
    }

    private void ApplyFilter()
    {
        var filter = Filter.Trim();

        Symbols.Clear();
        foreach (var symbol in _allSymbols.Where(row =>
                     Matches(filter, row.Display, row.Container, row.Origin, row.ChangeLabel)))
        {
            Symbols.Add(symbol);
        }

        Files.Clear();
        foreach (var file in _allFiles.Where(row => Matches(filter, row.Path, row.ChangeLabel)))
        {
            Files.Add(file);
        }

        foreach (var property in (string[])
                 [nameof(HasChangedSymbols), nameof(HasChangedFiles), nameof(EmptySymbolsNotice)])
        {
            OnPropertyChanged(property);
        }

        static bool Matches(string filter, params string[] fields) =>
            filter.Length == 0 ||
            fields.Any(field => field.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    private void OpenFile(ChangedFileViewModel? file)
    {
        if (file?.Status.FullPath is { } path)
        {
            StatusReported?.Invoke(Core.Workspace.SourceLauncher.Open(path, line: null) ?? $"Opened {path}");
        }
    }

    private void Clear()
    {
        _changes = RepositoryChanges.Empty;
        _hasRead = false;
        _allFiles.Clear();
        _allSymbols.Clear();
        Commits.Clear();
        DiffText = string.Empty;
        HistoryCaption = "Recent commits";
        ApplyFilter();
        ChangesUpdated?.Invoke(RepositoryChanges.Empty);
    }

    private void RaiseSummary()
    {
        foreach (var property in (string[])
                 [
                     nameof(Branch), nameof(IsDetached), nameof(StatusSummary), nameof(Subtitle),
                     nameof(ChangedFileCount), nameof(ChangedSymbolCount), nameof(HasCommits),
                 ])
        {
            OnPropertyChanged(property);
        }
    }
}

/// <summary>One symbol the working tree changed.</summary>
public sealed class ChangedSymbolViewModel(ChangedSymbol changed)
{
    public long? SymbolId { get; } = changed.SymbolId;

    public string Display { get; } = changed.Display;

    public string Glyph { get; } = SymbolGlyph.For(changed.Kind);

    public bool IsContainer { get; } = SymbolGlyph.IsContainer(changed.Kind);

    public string Kind { get; } = SymbolGlyph.Keyword(changed.Kind);

    public string Container { get; } = changed.Container ?? string.Empty;

    public string Origin { get; } = changed.FilePath is { } path
        ? $"{System.IO.Path.GetFileName(path)}:{changed.Line?.ToString() ?? "?"}"
        : string.Empty;

    public SymbolChangeKind Change { get; } = changed.Change;

    public string ChangeLabel { get; } = changed.Change switch
    {
        SymbolChangeKind.Added => "added",
        SymbolChangeKind.Removed => "removed",
        _ => "modified",
    };

    public bool IsAdded => Change == SymbolChangeKind.Added;

    public bool IsRemoved => Change == SymbolChangeKind.Removed;

    /// <summary>
    /// False for a removed declaration: it is named from the baseline and has nothing left
    /// to open.
    /// </summary>
    public bool IsNavigable { get; } = changed.IsNavigable;

    /// <summary>
    /// Set when the row was read from baseline syntax rather than resolved by the compiler,
    /// which the row says rather than leaving the reader to assume otherwise.
    /// </summary>
    public bool IsInferred { get; } = changed.Provenance == RelationProvenance.Inferred;

    /// <summary>How many tests a row lists before deferring the rest to the details pane.</summary>
    private const int InlineTests = 4;

    /// <summary>The tests over this declaration, capped so one row cannot fill the list.</summary>
    public IReadOnlyList<RelatedTestViewModel> Tests { get; } =
        [.. changed.Tests.Take(InlineTests).Select(test => new RelatedTestViewModel(test))];

    public bool HasTests => Tests.Count > 0;

    public int Remaining { get; } = Math.Max(0, changed.Tests.Count - InlineTests);

    public bool HasMore => Remaining > 0;

    public string MoreSummary => $"+{Remaining} more in the details pane";

    /// <summary>A meaningful change with nothing over it, which is what the warning marks.</summary>
    public bool IsUntested { get; } = changed.IsUntested;

    /// <summary>
    /// What the tests amount to, in one phrase. Silent for a change no test was expected
    /// for, so the absence of a summary is never read as an absence of coverage.
    /// </summary>
    public string TestSummary { get; } = (changed.Tests.Count, changed.ExactTests) switch
    {
        (0, _) when changed.IsUntested => "no tests found",
        (0, _) => string.Empty,
        (1, 1) => "1 test",
        (var total, var exact) when exact == total => $"{total} tests",
        (var total, 0) => $"{total} probable",
        var (total, exact) => $"{total} tests · {exact} exact",
    };

    public bool HasTestSummary => TestSummary.Length > 0;
}

/// <summary>One path Git reports as changed, with its diff.</summary>
public sealed class ChangedFileViewModel(GitFileStatus status, FileDiff? diff)
{
    public GitFileStatus Status { get; } = status;

    public string Path { get; } = status.Path;

    public string Name { get; } = System.IO.Path.GetFileName(status.Path);

    /// <summary>The directory, shown under the name so a long path stays scannable.</summary>
    public string Directory { get; } = System.IO.Path.GetDirectoryName(status.Path) ?? string.Empty;

    public string ChangeLabel { get; } = status.Change switch
    {
        GitFileChange.Added => "added",
        GitFileChange.Deleted => "deleted",
        GitFileChange.Renamed => "renamed",
        GitFileChange.Copied => "copied",
        GitFileChange.Untracked => "untracked",
        GitFileChange.Conflicted => "conflicted",
        GitFileChange.TypeChanged => "type changed",
        _ => "modified",
    };

    /// <summary>
    /// Where the change currently lives. Both are possible at once, and which it is
    /// decides what a commit would capture.
    /// </summary>
    public string StageLabel { get; } = (status.IsStaged, status.IsUnstaged) switch
    {
        (true, true) => "staged + unstaged",
        (true, false) => "staged",
        (false, true) => status.IsUntracked ? string.Empty : "unstaged",
        _ => string.Empty,
    };

    public bool HasStageLabel => StageLabel.Length > 0;

    public string Origin { get; } = status.OldPath is { } old ? $"was {old}" : string.Empty;

    public bool HasOrigin => Origin.Length > 0;

    public string Diff { get; } = diff switch
    {
        { IsBinary: true } => "Binary file; there is no text diff to show.",
        { Text.Length: > 0 } text => text.Text,
        _ => status.IsUntracked
            ? "This file is untracked, so it has no baseline to diff against."
            : string.Empty,
    };

    /// <summary>How many lines the hunks add and remove, which is the size of the change.</summary>
    public string Stat { get; } = diff is { Hunks.Count: > 0 }
        ? $"+{diff.Hunks.Sum(hunk => hunk.AddedLines.Count)} " +
          $"−{diff.Hunks.Sum(hunk => hunk.RemovedLineCount)}"
        : string.Empty;

    public bool HasStat => Stat.Length > 0;
}

/// <summary>One commit row.</summary>
public sealed class CommitViewModel(GitCommit commit)
{
    public string ShortHash { get; } = commit.ShortHash;

    public string Subject { get; } = commit.Subject;

    public string Author { get; } = commit.Author;

    public string When { get; } = commit.When == default
        ? string.Empty
        : commit.When.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
}
