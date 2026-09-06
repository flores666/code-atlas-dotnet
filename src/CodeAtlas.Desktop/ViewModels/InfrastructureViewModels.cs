using CodeAtlas.Core.Model;

namespace CodeAtlas.Desktop.ViewModels;

/// <summary>The three infrastructure lists, which are the three graph filters seen as tables.</summary>
public enum InfrastructureView
{
    Database,
    Configuration,
    ExternalServices,
}

/// <summary>Formats the source position shared by every infrastructure row.</summary>
internal static class RowOrigin
{
    public static string Of(string? filePath, int? line) => filePath is { } path
        ? $"{System.IO.Path.GetFileName(path)}:{line?.ToString() ?? "?"}"
        : string.Empty;
}

/// <summary>One EF Core entity, with the context and table it is mapped to.</summary>
public sealed class EntityRowViewModel(EntityMapping mapping)
{
    public EntityMapping Mapping { get; } = mapping;

    public string Entity { get; } = mapping.EntityDisplay;

    /// <summary>The table when it was written out; EF's convention decides the rest.</summary>
    public string Table { get; } = mapping.HasTable ? mapping.QualifiedTable : "by convention";

    public bool HasTable { get; } = mapping.HasTable;

    public string Context { get; } = (mapping.ContextDisplay, mapping.SetName) switch
    {
        ({ } context, { } set) => $"{context}.{set}",
        ({ } context, _) => context,
        _ => string.Empty,
    };

    public string Configuration { get; } = mapping.ConfigurationDisplay ?? string.Empty;

    public bool HasConfiguration { get; } = mapping.ConfigurationDisplay is not null;

    public string Origin { get; } = RowOrigin.Of(mapping.FilePath, mapping.Line);

    /// <summary>The symbol a row opens: the entity itself.</summary>
    public long? SymbolId { get; } = mapping.EntitySymbolId;
}

/// <summary>One migration and the tables its operations name.</summary>
public sealed class MigrationRowViewModel(DataMigration migration)
{
    public string Name { get; } = migration.Name;

    public string Context { get; } = migration.ContextDisplay ?? string.Empty;

    public string Tables { get; } = migration.TableSummary;

    public string Origin { get; } = RowOrigin.Of(migration.FilePath, migration.Line);

    public long? SymbolId { get; } = migration.TypeSymbolId ?? migration.ContextSymbolId;
}

/// <summary>One configuration read: a key, the type it binds, and who reads it.</summary>
public sealed class ConfigurationRowViewModel(ConfigurationUsage usage)
{
    public string Key { get; } = usage.Key ?? usage.OptionsDisplay ?? "IConfiguration";

    public string Access { get; } = usage.Access.ToString();

    public string Options { get; } = usage.OptionsDisplay ?? string.Empty;

    public bool HasOptions { get; } = usage.OptionsDisplay is not null;

    public string Consumer { get; } = usage.ConsumerDisplay ?? string.Empty;

    public string Origin { get; } = RowOrigin.Of(usage.FilePath, usage.Line);

    /// <summary>The options type where there is one, else whoever reads the key.</summary>
    public long? SymbolId { get; } = usage.OptionsSymbolId ?? usage.ConsumerSymbolId;
}

/// <summary>One infrastructure boundary and the component sitting on this side of it.</summary>
public sealed class ExternalRowViewModel(ExternalDependency dependency)
{
    public ExternalTechnology Technology { get; } = dependency.Technology;

    public string TechnologyLabel { get; } = dependency.TechnologyLabel;

    public string Resource { get; } = dependency.Resource;

    public string Consumer { get; } = dependency.ConsumerDisplay;

    public string Client { get; } = dependency.ClientDisplay;

    /// <summary>How it was found, which is also how much is known about it.</summary>
    public string Binding { get; } = dependency.Binding switch
    {
        ExternalBinding.TypedClient => "typed client",
        ExternalBinding.NamedClient => "named client",
        ExternalBinding.Injection => "injected",
        _ => "called",
    };

    public string Origin { get; } = RowOrigin.Of(dependency.FilePath, dependency.Line);

    public long? SymbolId { get; } = dependency.ConsumerSymbolId;
}
