using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace CodeAtlas.Core.Tests;

/// <summary>One project of an in-memory solution: its documents, and what it is built against.</summary>
internal sealed record TestProjectSpec(
    string Name,
    IReadOnlyList<(string FileName, string Source)> Documents,
    IReadOnlyList<string> References);

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

    public static Project Create(string name, params (string FileName, string Source)[] documents) =>
        CreateSolution(SourceRoot, new TestProjectSpec(name, documents, []))[0];

    /// <summary>
    /// Builds a project over files that really exist on disk, reading their current
    /// content.
    /// </summary>
    /// <remarks>
    /// The change-mapping tests need this rather than <see cref="Create"/>: a diff is
    /// mapped by file path and line, so the index has to be built from the same paths Git
    /// reports and from the content that is on disk right now. The paths are absolute
    /// already, so they are rooted at nothing further.
    /// </remarks>
    public static Project CreateFromFiles(string name, params string[] absolutePaths) =>
        CreateSolution(
            sourceRoot: string.Empty,
            new TestProjectSpec(
                name,
                [.. absolutePaths.Select(path => (path, File.ReadAllText(path)))],
                []))[0];

    /// <summary>
    /// Several projects in one solution, each able to reference the ones named before it.
    /// </summary>
    /// <param name="sourceRoot">
    /// What document paths are rooted at. A test that reads its files back — a diff against
    /// a real working tree — passes that tree, or nothing when its documents already carry
    /// absolute paths; the rest pass nothing real.
    /// </param>
    public static IReadOnlyList<Project> CreateSolution(string sourceRoot, params TestProjectSpec[] projects)
    {
        var workspace = new AdhocWorkspace();
        var ids = projects.ToDictionary(
            project => project.Name, _ => ProjectId.CreateNewId(), StringComparer.Ordinal);

        foreach (var project in projects)
        {
            var projectId = ids[project.Name];

            workspace.AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Default,
                project.Name,
                assemblyName: project.Name,
                LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
                metadataReferences: RuntimeReferences,
                projectReferences: project.References.Select(name => new ProjectReference(ids[name]))));

            foreach (var (fileName, source) in project.Documents)
            {
                workspace.AddDocument(DocumentInfo.Create(
                    DocumentId.CreateNewId(projectId),
                    Path.GetFileName(fileName),
                    loader: TextLoader.From(TextAndVersion.Create(SourceText.From(source), VersionStamp.Default)),
                    filePath: Path.Combine(sourceRoot, fileName)));
            }
        }

        return [.. projects.Select(project => workspace.CurrentSolution.GetProject(ids[project.Name])!)];
    }
}
