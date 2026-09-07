using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace CodeAtlas.Core.Tests;

/// <summary>
/// Builds in-memory Roslyn projects so the analysis layer can be tested without
/// MSBuild, restores or files on disk.
/// </summary>
internal static class TestProjectFactory
{
    private const string SourceRoot = "/repo/src";

    private static readonly IReadOnlyList<MetadataReference> RuntimeReferences =
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
        .ToList();

    /// <summary>
    /// Builds a project over files that really exist on disk, reading their current
    /// content.
    /// </summary>
    /// <remarks>
    /// The change-mapping tests need this rather than <see cref="Create"/>: a diff is
    /// mapped by file path and line, so the index has to be built from the same paths Git
    /// reports and from the content that is on disk right now.
    /// </remarks>
    public static Project CreateFromFiles(string name, params string[] absolutePaths) =>
        Build(name, absolutePaths.Select(path => (path, File.ReadAllText(path))).ToArray());

    public static Project Create(string name, params (string FileName, string Source)[] documents) =>
        Build(
            name,
            documents.Select(document => (
                Path.Combine(SourceRoot, document.FileName),
                document.Source)).ToArray());

    private static Project Build(string name, (string Path, string Source)[] documents)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();

        var project = workspace.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Default,
            name,
            assemblyName: name,
            LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
            metadataReferences: RuntimeReferences));

        foreach (var (path, source) in documents)
        {
            workspace.AddDocument(DocumentInfo.Create(
                DocumentId.CreateNewId(projectId),
                Path.GetFileName(path),
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(source), VersionStamp.Default)),
                filePath: path));
        }

        return workspace.CurrentSolution.GetProject(projectId)!;
    }
}
