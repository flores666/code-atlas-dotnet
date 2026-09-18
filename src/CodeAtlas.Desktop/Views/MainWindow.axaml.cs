using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AvaloniaEdit.Highlighting;
using CodeAtlas.Core.Workspace;
using CodeAtlas.Desktop.ViewModels;

namespace CodeAtlas.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        OpenFileButton.Click += OnOpenFileClicked;
        OpenFolderButton.Click += OnOpenFolderClicked;

        Code.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("C#");
        Code.Options.HighlightCurrentLine = true;

        // The editor owns a document rather than a bindable string, and revealing a line
        // is an operation on the control, so the open file is pushed into it here rather
        // than bound. This is the whole of what the view model cannot express.
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.Source))
            {
                ShowSource(_viewModel.Source);
            }
        };
    }

    private void ShowSource(SourceViewModel? source)
    {
        if (source is null)
        {
            Code.Clear();
            return;
        }

        Code.Text = source.Text;

        var line = Math.Clamp(source.Line, 1, Code.Document.LineCount);
        Code.TextArea.Caret.Line = line;
        Code.TextArea.Caret.Column = 1;
        Code.ScrollToLine(line);
    }

    /// <summary>
    /// The pickers live here rather than in the view model: they are inherently a
    /// property of the window, and keeping them out of the view model avoids inventing
    /// a service interface with exactly one implementation.
    /// </summary>
    private async void OnOpenFileClicked(object? sender, RoutedEventArgs e)
    {
        WorkspaceButton.Flyout?.Hide();

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a solution or project",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("C# solution or project")
                {
                    Patterns =
                    [
                        $"*{WorkspaceLocator.SolutionExtension}",
                        $"*{WorkspaceLocator.SolutionXmlExtension}",
                        $"*{WorkspaceLocator.ProjectExtension}",
                    ],
                },
            ],
        });

        await OpenAsync(files);
    }

    private async void OnOpenFolderClicked(object? sender, RoutedEventArgs e)
    {
        WorkspaceButton.Flyout?.Hide();

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open a repository or project folder",
            AllowMultiple = false,
        });

        await OpenAsync(folders);
    }

    private async Task OpenAsync(IReadOnlyList<IStorageItem> items)
    {
        if (items.Count > 0 && items[0].TryGetLocalPath() is { } path)
        {
            await _viewModel.OpenAsync(path);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
