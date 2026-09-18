using System.Windows.Input;

namespace CodeAtlas.Desktop.ViewModels;

/// <summary>
/// One entry of the workspace picker in the title bar. The name is what a reader
/// recognises; the path is what tells two same-named solutions apart.
/// </summary>
public sealed class RecentWorkspaceViewModel(string path, ICommand openCommand)
{
    public string Path { get; } = path;

    public string Name { get; } = System.IO.Path.GetFileNameWithoutExtension(path);

    public string Directory { get; } = System.IO.Path.GetDirectoryName(path) ?? path;

    public ICommand OpenCommand { get; } = openCommand;
}
