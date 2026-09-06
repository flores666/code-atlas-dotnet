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

    public static Project Create(string name, params (string FileName, string Source)[] documents)
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

        foreach (var (fileName, source) in documents)
        {
            workspace.AddDocument(DocumentInfo.Create(
                DocumentId.CreateNewId(projectId),
                fileName,
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(source), VersionStamp.Default)),
                filePath: Path.Combine(SourceRoot, fileName)));
        }

        return workspace.CurrentSolution.GetProject(projectId)!;
    }
}
