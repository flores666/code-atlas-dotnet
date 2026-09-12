using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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
        EmptyStateOpenButton.Click += OnOpenFileClicked;

        // Zoom and pan describe where the reader is looking, so they stay in the view.
        GraphZoomInButton.Click += (_, _) => GraphSurface.ZoomIn();
        GraphZoomOutButton.Click += (_, _) => GraphSurface.ZoomOut();
        GraphFitButton.Click += (_, _) => GraphSurface.ResetView();

        ContextCopyButton.Click += OnContextCopyClicked;
        ContextExportFolderButton.Click += OnContextExportFolderClicked;
        ContextExportZipButton.Click += OnContextExportZipClicked;
    }

    /// <summary>Ctrl+K puts the caret in the global search box, as the hint in it promises.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.K && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    /// <summary>
    /// The pickers live here rather than in the view model: they are inherently a
    /// property of the window, and keeping them out of the view model avoids inventing
    /// a service interface with exactly one implementation.
    /// </summary>
    private async void OnOpenFileClicked(object? sender, RoutedEventArgs e)
    {
        OpenButton.Flyout?.Hide();

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
        OpenButton.Flyout?.Hide();

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

    /// <summary>
    /// The clipboard and the pickers are properties of the window, which is why the
    /// context pack is handed out from here: the view model renders it, and the window
    /// puts it where the reader asked for it. Nothing is sent anywhere.
    /// </summary>
    private async void OnContextCopyClicked(object? sender, RoutedEventArgs e)
    {
        var context = _viewModel.Context;

        if (Clipboard is not { } clipboard)
        {
            context.Report("This platform has no clipboard; export the pack to a folder instead.");
            return;
        }

        await clipboard.SetTextAsync(context.BuildText());
        context.Report("Copied the context pack to the clipboard.");
    }

    private async void OnContextExportFolderClicked(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Where should the ContextPack folder be written?",
            AllowMultiple = false,
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            _viewModel.Context.ExportToDirectory(path);
        }
    }

    private async void OnContextExportZipClicked(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the context pack",
            SuggestedFileName = "ContextPack.zip",
            DefaultExtension = "zip",
            FileTypeChoices = [new FilePickerFileType("Zip archive") { Patterns = ["*.zip"] }],
        });

        if (file?.TryGetLocalPath() is { } path)
        {
            _viewModel.Context.ExportToZip(path);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
