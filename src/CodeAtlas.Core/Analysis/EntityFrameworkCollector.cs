using CodeAtlas.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeAtlas.Core.Analysis;

/// <summary>
/// Reads the EF Core model out of source: what the entities are, how they are mapped,
/// which migrations touch which tables, and which code reads or writes them.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is anchored on EF's own types — <c>DbContext</c>, <c>DbSet&lt;T&gt;</c>,
/// <c>IEntityTypeConfiguration&lt;T&gt;</c>, the metadata builders and
/// <c>MigrationBuilder</c> — bound by the compiler, never on how a call is spelled.
/// </para>
/// <para>
/// No SQL is reconstructed. A table name is recorded when it was written as a literal in
/// <c>ToTable</c> or <c>[Table]</c>; when EF's naming convention decides it at run time,
/// the mapping simply carries no table. Usage is likewise coarse on purpose: reaching a
/// <c>DbSet</c> is a read unless the call is one of EF's add, update or remove verbs.
/// </para>
/// </remarks>
public sealed class EntityFrameworkCollector
{
    private const string Ef = "Microsoft.EntityFrameworkCore";
    private const string Builders = "Microsoft.EntityFrameworkCore.Metadata.Builders";
    private const string Schema = "System.ComponentModel.DataAnnotations.Schema";

    private const string DbContextName = $"{Ef}.DbContext";
    private const string MigrationName = $"{Ef}.Migrations.Migration";
    private const string MigrationBuilderName = $"{Ef}.Migrations.MigrationBuilder";
    private const string MigrationAttributeName = $"{Ef}.Migrations.MigrationAttribute";
    private const string DbContextAttributeName = $"{Ef}.Infrastructure.DbContextAttribute";
    private const string TableAttributeName = $"{Schema}.TableAttribute";

    /// <summary>EF's own change-tracking verbs, and what each one does to an entity.</summary>
    private static readonly Dictionary<string, RelationKind> Verbs = new(StringComparer.Ordinal)
    {
        ["Add"] = RelationKind.CreatesEntity,
        ["AddAsync"] = RelationKind.CreatesEntity,
        ["AddRange"] = RelationKind.CreatesEntity,
        ["AddRangeAsync"] = RelationKind.CreatesEntity,
        ["Update"] = RelationKind.ModifiesEntity,
        ["UpdateRange"] = RelationKind.ModifiesEntity,
        ["ExecuteUpdate"] = RelationKind.ModifiesEntity,
        ["ExecuteUpdateAsync"] = RelationKind.ModifiesEntity,
        ["Remove"] = RelationKind.DeletesEntity,
        ["RemoveRange"] = RelationKind.DeletesEntity,
        ["ExecuteDelete"] = RelationKind.DeletesEntity,
        ["ExecuteDeleteAsync"] = RelationKind.DeletesEntity,
        ["Find"] = RelationKind.ReadsEntity,
        ["FindAsync"] = RelationKind.ReadsEntity,
    };

    /// <summary>The fluent calls that state a relationship between two entity types.</summary>
    private static readonly string[] RelationshipVerbs = ["HasOne", "HasMany", "WithOne", "WithMany"];

    private readonly Dictionary<string, EntityMapping> _entities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DataMigration> _migrations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SortedSet<string>> _migrationTables = new(StringComparer.Ordinal);
    private readonly List<PendingRelation> _relations = [];
    private readonly string? _projectName;

    public EntityFrameworkCollector(string? projectName) => _projectName = projectName;

    /// <summary>One row per entity, with everything found about it merged together.</summary>
    public IReadOnlyList<EntityMapping> Entities => _entities.Values
        .OrderBy(entity => entity.EntityDisplay, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public IReadOnlyList<DataMigration> Migrations => _migrations.Values
        .Select(migration => _migrationTables.TryGetValue(migration.TypeFullyQualifiedName, out var tables)
            ? migration with { Tables = tables.ToList() }
            : migration)
        .OrderBy(migration => migration.Name, StringComparer.Ordinal)
        .ToList();

    public IReadOnlyList<PendingRelation> Relations => _relations;

    // ---- declarations -------------------------------------------------------

    public void VisitType(INamedTypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (SourceFacts.DerivesFrom(type, DbContextName))
        {
            VisitContext(type);
        }

        foreach (var @interface in type.AllInterfaces)
        {
            if (SourceFacts.IsType(@interface, Ef, "IEntityTypeConfiguration") &&
                @interface.TypeArguments is [{ } configured])
            {
                var (path, line) = SourceFacts.LocationOf(type);
                Update(configured, mapping => mapping with
                {
                    ConfigurationFullyQualifiedName = SymbolNaming.FullyQualifiedName(type),
                    ConfigurationDisplay = SymbolNaming.Display(type),
                    FilePath = mapping.FilePath ?? path,
                    Line = mapping.Line ?? line,
                });

                Relate(SymbolNaming.FullyQualifiedName(type), RelationKind.ConfiguresEntity, configured);
            }
        }

        if (SourceFacts.Attribute(type, TableAttributeName) is { } table)
        {
            // A [Table] attribute is EF's own, so the type carrying it is an entity even
            // when no context in this project exposes it.
            Update(type, mapping => mapping with
            {
                TableName = SourceFacts.FirstStringArgument(table) ?? mapping.TableName,
                Schema = SourceFacts.NamedStringArgument(table, "Schema") ?? mapping.Schema,
            });
        }

        if (SourceFacts.DerivesFrom(type, MigrationName))
        {
            VisitMigration(type);
        }
    }

    private void VisitContext(INamedTypeSymbol context)
    {
        var contextFullyQualifiedName = SymbolNaming.FullyQualifiedName(context);

        foreach (var property in context.GetMembers().OfType<IPropertySymbol>())
        {
            if (EntityOfSet(property.Type) is not { } entity)
            {
                continue;
            }

            var (path, line) = SourceFacts.LocationOf(property);
            Update(entity, mapping => mapping with
            {
                ContextFullyQualifiedName = contextFullyQualifiedName,
                ContextDisplay = SymbolNaming.Display(context),
                SetName = property.Name,
                FilePath = mapping.FilePath ?? path,
                Line = mapping.Line ?? line,
            });

            Relate(contextFullyQualifiedName, RelationKind.DeclaresEntity, entity);
        }
    }

    private void VisitMigration(INamedTypeSymbol type)
    {
        var fullyQualifiedName = SymbolNaming.FullyQualifiedName(type);
        var context = SourceFacts.Attribute(type, DbContextAttributeName)?.ConstructorArguments is [{ Value: INamedTypeSymbol declared }]
            ? declared
            : null;

        var (path, line) = SourceFacts.LocationOf(type);

        _migrations[fullyQualifiedName] = new DataMigration
        {
            Name = SourceFacts.Attribute(type, MigrationAttributeName) is { } migration
                ? SourceFacts.FirstStringArgument(migration) ?? type.Name
                : type.Name,
            TypeFullyQualifiedName = fullyQualifiedName,
            TypeDisplay = SymbolNaming.Display(type),
            ContextFullyQualifiedName = context is null ? null : SymbolNaming.FullyQualifiedName(context),
            ContextDisplay = context is null ? null : SymbolNaming.Display(context),
            ProjectName = _projectName,
            FilePath = path,
            Line = line,
        };
    }

    // ---- bodies -------------------------------------------------------------

    internal void VisitDocument(
        SyntaxNode root,
        SemanticModel model,
        EnclosingSymbolResolver enclosing,
        CancellationToken cancellationToken)
    {
        foreach (var node in root.DescendantNodes())
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (node)
            {
                case InvocationExpressionSyntax invocation:
                    VisitInvocation(invocation, model, enclosing, cancellationToken);
                    break;

                // `context.Users` reached for anything but a change-tracking call is a read.
                case MemberAccessExpressionSyntax access when !IsVerbReceiver(access, model, cancellationToken):
                    AddRead(access, model, enclosing, cancellationToken);
                    break;
            }
        }
    }

    private void VisitInvocation(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        EnclosingSymbolResolver enclosing,
        CancellationToken cancellationToken)
    {
        if (model.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method)
        {
            return;
        }

        var container = method.ContainingType?.OriginalDefinition;

        if (container is not null && SymbolNaming.FullyQualifiedName(container) == MigrationBuilderName)
        {
            AddMigrationTables(invocation, method, model, enclosing, cancellationToken);
            return;
        }

        // Everything else worth reading is declared by EF, whether on a builder itself or
        // on one of the relational extensions over it.
        if (IsEntityFramework(container))
        {
            AddModelConfiguration(invocation, method, model, enclosing, cancellationToken);

            if (Verbs.TryGetValue(method.Name, out var kind) &&
                EntityOfCall(invocation, method, model, cancellationToken) is { } entity)
            {
                Relate(enclosing.NameOf(invocation, cancellationToken), kind, entity);
                return;
            }
        }

        // `context.Set<Order>()` yields a set the same way a DbSet property does.
        AddRead(invocation, model, enclosing, cancellationToken);
    }

    /// <summary>Records reaching a <c>DbSet</c> as a query against its entity.</summary>
    private void AddRead(
        ExpressionSyntax expression,
        SemanticModel model,
        EnclosingSymbolResolver enclosing,
        CancellationToken cancellationToken)
    {
        if (EntityOfSet(model.GetTypeInfo(expression, cancellationToken).Type) is { } entity)
        {
            Relate(enclosing.NameOf(expression, cancellationToken), RelationKind.ReadsEntity, entity);
        }
    }

    /// <summary>
    /// True when this access is only the receiver of a change-tracking call, which the
    /// invocation branch records with the verb it actually is.
    /// </summary>
    private static bool IsVerbReceiver(
        MemberAccessExpressionSyntax access,
        SemanticModel model,
        CancellationToken cancellationToken) =>
        access.Parent is MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax call } outer &&
        outer.Expression == access &&
        Verbs.ContainsKey(outer.Name.Identifier.ValueText) &&
        model.GetSymbolInfo(call, cancellationToken).Symbol is IMethodSymbol method &&
        IsEntityFramework(method.ContainingType?.OriginalDefinition);

    private void AddModelConfiguration(
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        SemanticModel model,
        EnclosingSymbolResolver enclosing,
        CancellationToken cancellationToken)
    {
        var owner = enclosing.ContainingType(invocation, cancellationToken);

        // modelBuilder.Entity<Order>() — the type that wrote it maps that entity.
        if (method.Name == "Entity" && method.TypeArguments is [{ } configured])
        {
            Touch(configured);
            if (owner is not null)
            {
                Relate(SymbolNaming.FullyQualifiedName(owner), RelationKind.ConfiguresEntity, configured);
            }
        }

        if (method.Name == "ToTable" &&
            EntityOfBuilder(SourceFacts.Receiver(invocation), model, cancellationToken) is { } mapped)
        {
            Update(mapped, mapping => mapping with
            {
                TableName = SourceFacts.StringArgumentFor(invocation, method, "name", model, cancellationToken)
                            ?? mapping.TableName,
                Schema = SourceFacts.StringArgumentFor(invocation, method, "schema", model, cancellationToken)
                         ?? mapping.Schema,
            });
        }

        // HasMany(...).WithOne(...) hands back a builder named for both ends, which is
        // where the two related entities are stated exactly.
        if (RelationshipVerbs.Contains(method.Name, StringComparer.Ordinal) &&
            method.ReturnType is INamedTypeSymbol { TypeArguments: [{ } left, { } right] } returned &&
            SymbolNaming.NamespaceOf(returned.OriginalDefinition) == Builders)
        {
            Relate(SymbolNaming.FullyQualifiedName(left.OriginalDefinition), RelationKind.RelatesToEntity, right);
            Relate(SymbolNaming.FullyQualifiedName(right.OriginalDefinition), RelationKind.RelatesToEntity, left);
        }
    }

    private void AddMigrationTables(
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        SemanticModel model,
        EnclosingSymbolResolver enclosing,
        CancellationToken cancellationToken)
    {
        if (enclosing.ContainingType(invocation, cancellationToken) is not { } owner)
        {
            return;
        }

        // Operations on a table take it as `table`; operations on the table itself name it
        // as `name`. Anything else — raw Sql, for one — names no table and reports none.
        var table = SourceFacts.StringArgumentFor(invocation, method, "table", model, cancellationToken)
                    ?? (method.Name.EndsWith("Table", StringComparison.Ordinal)
                        ? SourceFacts.StringArgumentFor(invocation, method, "name", model, cancellationToken)
                        : null);

        if (table is { Length: > 0 })
        {
            var key = SymbolNaming.FullyQualifiedName(owner);
            (_migrationTables.TryGetValue(key, out var tables)
                ? tables
                : _migrationTables[key] = new SortedSet<string>(StringComparer.Ordinal)).Add(table);
        }
    }

    // ---- type readings ------------------------------------------------------

    /// <summary>The entity a <c>DbSet&lt;T&gt;</c> holds, or <c>null</c> for anything else.</summary>
    private static ITypeSymbol? EntityOfSet(ITypeSymbol? type) =>
        SourceFacts.IsType(type, Ef, "DbSet") && type is INamedTypeSymbol { TypeArguments: [{ } entity] }
            ? entity
            : null;

    /// <summary>The entity an <c>EntityTypeBuilder&lt;T&gt;</c> configures.</summary>
    private static ITypeSymbol? EntityOfBuilder(
        ExpressionSyntax? receiver,
        SemanticModel model,
        CancellationToken cancellationToken) =>
        receiver is not null &&
        model.GetTypeInfo(receiver, cancellationToken).Type is INamedTypeSymbol { TypeArguments: [{ } entity] } builder &&
        SymbolNaming.NamespaceOf(builder.OriginalDefinition) == Builders
            ? entity
            : null;

    /// <summary>
    /// Which entity a change-tracking call acts on: the set it was made on, the type
    /// argument it was given, the query it continues, or the instance it was handed.
    /// </summary>
    private static ITypeSymbol? EntityOfCall(
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        var receiver = SourceFacts.Receiver(invocation) is { } expression
            ? model.GetTypeInfo(expression, cancellationToken).Type
            : null;

        return EntityOfSet(receiver)
               ?? (method.TypeArguments is [{ } argument] ? argument : null)
               ?? ElementOfQuery(receiver)
               ?? (invocation.ArgumentList.Arguments.Count > 0
                   ? model.GetTypeInfo(invocation.ArgumentList.Arguments[0].Expression, cancellationToken).Type
                   : null);
    }

    private static ITypeSymbol? ElementOfQuery(ITypeSymbol? type) =>
        type is INamedTypeSymbol named &&
        named.AllInterfaces.Prepend(named)
            .FirstOrDefault(candidate => SourceFacts.IsType(candidate, "System.Linq", "IQueryable")) is
            { TypeArguments: [{ } element] }
            ? element
            : null;

    private static bool IsEntityFramework(INamedTypeSymbol? type) =>
        type is not null &&
        SymbolNaming.FullyQualifiedName(type).StartsWith($"{Ef}.", StringComparison.Ordinal);

    // ---- accumulation -------------------------------------------------------

    private void Touch(ITypeSymbol entity) => Update(entity, mapping => mapping);

    private void Update(ITypeSymbol entity, Func<EntityMapping, EntityMapping> update)
    {
        var definition = entity.OriginalDefinition;
        if (definition is not INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Struct } named ||
            !SourceFacts.IsInSource(named))
        {
            return;
        }

        var fullyQualifiedName = SymbolNaming.FullyQualifiedName(named);
        var (path, line) = SourceFacts.LocationOf(named);

        _entities[fullyQualifiedName] = update(_entities.TryGetValue(fullyQualifiedName, out var existing)
            ? existing
            : new EntityMapping
            {
                EntityFullyQualifiedName = fullyQualifiedName,
                EntityDisplay = SymbolNaming.Display(named),
                ProjectName = _projectName,
                FilePath = path,
                Line = line,
            });
    }

    private void Relate(string? source, RelationKind kind, ITypeSymbol target)
    {
        var definition = target.OriginalDefinition;

        if (source is null || definition is not INamedTypeSymbol named || !SourceFacts.IsInSource(named))
        {
            return;
        }

        var targetName = SymbolNaming.FullyQualifiedName(named);
        if (targetName == source)
        {
            return;
        }

        Touch(named);
        _relations.Add(new PendingRelation(source, kind, targetName, SymbolNaming.Display(named)));
    }
}
