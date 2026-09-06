using System.Windows.Input;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Desktop.ViewModels;

/// <summary>
/// A related symbol, carrying the command that navigates to it so the item template
/// needs no lookup into an ancestor's data context.
/// </summary>
public sealed class RelationLink(SymbolLink link, ICommand navigateCommand)
{
    public SymbolLink Link { get; } = link;

    public ICommand NavigateCommand { get; } = navigateCommand;

    public string Display => Link.Display;

    public string FullyQualifiedName => Link.FullyQualifiedName;

    /// <summary>False for symbols outside the solution, which have nothing to open.</summary>
    public bool IsNavigable => Link.IsNavigable;

    /// <summary>False for an edge recovered from a binding the compiler could not resolve.</summary>
    public bool IsExact => Link.IsExact;
}

/// <summary>A named list of related symbols, rendered as one section of the details pane.</summary>
public sealed record RelationGroup(string Title, IReadOnlyList<RelationLink> Links);

/// <summary>Presentation of a single symbol and its relations.</summary>
public sealed class SymbolDetailsViewModel
{
    public SymbolDetailsViewModel(SymbolDetails details, ICommand navigateCommand)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(navigateCommand);

        Registrations = details.Registrations.Select(registration => new RegistrationViewModel(registration)).ToList();
        EntityMappings = details.EntityMappings.Select(mapping => new EntityRowViewModel(mapping)).ToList();
        Migrations = details.Migrations.Select(migration => new MigrationRowViewModel(migration)).ToList();
        Configuration = details.Configuration.Select(usage => new ConfigurationRowViewModel(usage)).ToList();
        ExternalDependencies = details.ExternalDependencies
            .Select(dependency => new ExternalRowViewModel(dependency))
            .ToList();
        RelatedEndpoints = details.RelatedEndpoints.Select(endpoint => new EndpointViewModel(endpoint)).ToList();

        Symbol = details.Symbol;

        Header = details.Symbol.Display;
        Kind = SymbolGlyph.Keyword(details.Symbol.Kind);
        Glyph = SymbolGlyph.For(details.Symbol.Kind);
        IsContainer = SymbolGlyph.IsContainer(details.Symbol.Kind);
        FullyQualifiedName = details.Symbol.FullyQualifiedName;
        Project = details.Symbol.ProjectName ?? "-";
        Namespace = string.IsNullOrEmpty(details.Symbol.Namespace)
            ? "<global namespace>"
            : details.Symbol.Namespace;
        Accessibility = details.Symbol.Accessibility ?? "-";
        Attributes = details.Symbol.Attributes;

        Location = details.Symbol.FilePath is { } path
            ? $"{path}:{details.Symbol.Line?.ToString() ?? "?"}"
            : "No source location";

        FileName = details.Symbol.FilePath is { } file
            ? $"{System.IO.Path.GetFileName(file)}:{details.Symbol.Line?.ToString() ?? "?"}"
            : "No source location";

        CanOpenSource = details.Symbol.FilePath is not null;

        // Outgoing relations first — what this symbol depends on — then incoming, which
        // is what depends on it.
        Groups = new (string Title, IReadOnlyList<SymbolLink> Links)[]
        {
            ("Base type", details.BaseTypes),
            ("Implements", details.Interfaces),
            ("Overrides", details.Overrides),
            ("Calls", details.Calls),
            ("Parameter types", details.ParameterTypes),
            ("Return type", details.ReturnTypes),
            ("Constructor dependencies", details.Injects),
            ("Registered implementations", details.Resolves),
            ("Entities", details.DeclaredEntities),
            ("Maps", details.ConfiguredEntities),
            ("Reads entities", details.ReadsEntities),
            ("Writes entities", details.WritesEntities),
            ("Related entities", details.RelatedEntities),
            ("Configuration", details.ReadsConfiguration),
            ("External services", details.UsesExternal),
            ("References", details.References),
            ("Derived types", details.DerivedTypes),
            ("Implemented by", details.Implementors),
            ("Overridden by", details.OverriddenBy),
            ("Injected by", details.InjectedBy),
            ("Registered as", details.ResolvedBy),
            ("Declared by", details.DeclaredBy),
            ("Read by", details.Readers),
            ("Written by", details.Writers),
            ("Services on the way here", details.RelatedServices),
            ("Configuration read by", details.ConfigurationReaders),
            ("Reached from", details.ExternalConsumers),
            (Truncatable("Called by", details.CalledBy.Count, details.CalledByTotal), details.CalledBy),
            (Truncatable("Referenced by", details.ReferencedBy.Count, details.ReferencedByTotal),
                details.ReferencedBy),
        }
        .Where(group => group.Links.Count > 0)
        .Select(group => new RelationGroup(
            group.Title,
            group.Links.Select(link => new RelationLink(link, navigateCommand)).ToList()))
        .ToList();
    }

    public IndexedSymbol Symbol { get; }

    public string Header { get; }

    /// <summary>The C# keyword for the symbol kind, e.g. <c>class</c>.</summary>
    public string Kind { get; }

    public string Glyph { get; }

    public bool IsContainer { get; }

    public string FullyQualifiedName { get; }

    public string Project { get; }

    public string Namespace { get; }

    public string Accessibility { get; }

    public IReadOnlyList<string> Attributes { get; }

    public bool HasAttributes => Attributes.Count > 0;

    /// <summary>Full path and line, shown as the secondary line under <see cref="FileName"/>.</summary>
    public string Location { get; }

    /// <summary>File name and line only, which is what identifies the location at a glance.</summary>
    public string FileName { get; }

    public bool CanOpenSource { get; }

    /// <summary>Only the non-empty sections, so the pane shows no empty headings.</summary>
    public IReadOnlyList<RelationGroup> Groups { get; }

    /// <summary>
    /// The container registrations this symbol takes part in, whether as the service or as
    /// the implementation. More than one is normal and meaningful.
    /// </summary>
    public IReadOnlyList<RegistrationViewModel> Registrations { get; }

    public bool HasRegistrations => Registrations.Count > 0;

    /// <summary>How this entity is mapped, or how this context maps what it owns.</summary>
    public IReadOnlyList<EntityRowViewModel> EntityMappings { get; }

    public bool HasEntityMappings => EntityMappings.Count > 0;

    public IReadOnlyList<MigrationRowViewModel> Migrations { get; }

    public bool HasMigrations => Migrations.Count > 0;

    /// <summary>Configuration this symbol reads, or that binds to it when it is an options type.</summary>
    public IReadOnlyList<ConfigurationRowViewModel> Configuration { get; }

    public bool HasConfiguration => Configuration.Count > 0;

    public IReadOnlyList<ExternalRowViewModel> ExternalDependencies { get; }

    public bool HasExternalDependencies => ExternalDependencies.Count > 0;

    /// <summary>
    /// The HTTP entry points whose flow reaches this symbol. Found by walking composition
    /// backwards, so a resource says which of the application's doors lead to it.
    /// </summary>
    public IReadOnlyList<EndpointViewModel> RelatedEndpoints { get; }

    public bool HasRelatedEndpoints => RelatedEndpoints.Count > 0;

    /// <summary>Names a capped list so a partial answer never reads as a complete one.</summary>
    private static string Truncatable(string title, int shown, int total) =>
        total > shown ? $"{title} ({shown} of {total})" : title;

}

/// <summary>One entry of the recent-workspaces list, bound directly to its command.</summary>
public sealed class RecentWorkspaceViewModel(string path, ICommand openCommand)
{
    public string Path { get; } = path;

    public ICommand OpenCommand { get; } = openCommand;

    public string Name { get; } = System.IO.Path.GetFileName(path);
}
