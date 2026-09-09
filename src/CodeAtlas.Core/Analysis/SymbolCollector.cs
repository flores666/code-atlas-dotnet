using CodeAtlas.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeAtlas.Core.Analysis;

/// <summary>
/// Analyses one Roslyn project: its declarations, the semantic relations between them,
/// and how the application it belongs to is composed.
/// </summary>
/// <remarks>
/// <para>
/// Declarations come from the project's own assembly symbol, so partial types are
/// captured once. Call and reference edges are collected per document from the semantic
/// model that is already being built for the compilation — deliberately not via
/// <c>SymbolFinder.FindReferencesAsync</c>, which is a whole-solution operation.
/// </para>
/// <para>
/// Every edge here is compiler-derived. A binding Roslyn resolved to exactly one symbol
/// is <see cref="RelationProvenance.Exact"/>; a binding it failed to resolve but that has
/// a single candidate is kept as <see cref="RelationProvenance.Inferred"/>, so a project
/// with errors still maps, without that guess being presented as compiler truth.
/// </para>
/// <para>
/// DI registrations, HTTP endpoints, the EF Core model, configuration reads and external
/// dependencies are collected here rather than in passes of their own, because this is
/// where a project's documents are already bound: the semantic model is the expensive
/// part, and walking every document again to rebuild it would cost more than all of the
/// analysis does.
/// </para>
/// </remarks>
public sealed class SymbolCollector
{
    /// <summary>Guards against pathological generic nesting while unwrapping type dependencies.</summary>
    private const int MaxTypeNesting = 4;

    private readonly List<IndexedSymbol> _symbols = [];
    private readonly List<PendingRelation> _relations = [];
    private readonly HashSet<(string Source, RelationKind Kind, string Target)> _seen = [];
    private readonly List<IndexDiagnostic> _diagnostics = [];
    private readonly ServiceRegistrationCollector _registrations = new();
    private readonly EndpointCollector _endpoints;
    private readonly EntityFrameworkCollector _persistence;
    private readonly ConfigurationCollector _configuration;
    private readonly ExternalDependencyCollector _external;
    private readonly string _projectName;

    private SymbolCollector(string projectName)
    {
        _projectName = projectName;
        _endpoints = new EndpointCollector(projectName);
        _persistence = new EntityFrameworkCollector(projectName);
        _configuration = new ConfigurationCollector(projectName);
        _external = new ExternalDependencyCollector(projectName);
    }

    public static async Task<ProjectIndexData> CollectAsync(Project project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);

        var collector = new SymbolCollector(project.Name);
        var indexedProject = new IndexedProject
        {
            Name = project.Name,
            FilePath = project.FilePath,
            AssemblyName = project.AssemblyName,
        };

        var references = project.ProjectReferences
            .Select(reference => project.Solution.GetProject(reference.ProjectId)?.Name)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Compilation? compilation;
        try
        {
            compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Failed(indexedProject, $"Compilation failed: {e.Message}");
        }

        if (compilation is null)
        {
            return Failed(indexedProject, "The project produced no compilation and was skipped.");
        }

        collector.VisitNamespace(compilation.Assembly.GlobalNamespace, cancellationToken);

        foreach (var document in project.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await collector.VisitDocumentAsync(document, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // A single unparseable or otherwise broken file must not lose the project.
                collector._diagnostics.Add(new IndexDiagnostic(
                    Model.DiagnosticSeverity.Warning,
                    project.Name,
                    $"Could not analyse '{document.FilePath ?? document.Name}': {e.Message}"));
            }
        }

        // Merged last, through the same guard the collector's own edges pass, so a
        // relation found twice by two readings is still stored once.
        foreach (var relation in collector._persistence.Relations
                     .Concat(collector._configuration.Relations)
                     .Concat(collector._external.Relations))
        {
            collector.AddPending(relation);
        }

        return new ProjectIndexData(
            indexedProject,
            collector._symbols,
            collector._relations,
            collector._diagnostics)
        {
            ProjectReferences = references,
            Registrations = collector._registrations.Registrations,
            Endpoints = collector._endpoints.Endpoints,
            Entities = collector._persistence.Entities,
            Migrations = collector._persistence.Migrations,
            Configuration = collector._configuration.Usages,
            ExternalDependencies = collector._external.Dependencies,
        };

        static ProjectIndexData Failed(IndexedProject project, string message) =>
            new(project with { Loaded = false }, [], [],
                [new IndexDiagnostic(Model.DiagnosticSeverity.Error, project.Name, message)]);
    }

    // ---- declarations -------------------------------------------------------

    private void VisitNamespace(INamespaceSymbol @namespace, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!@namespace.IsGlobalNamespace)
        {
            AddSymbol(@namespace, IndexedSymbolKind.Namespace);
        }

        foreach (var member in @namespace.GetMembers())
        {
            switch (member)
            {
                case INamespaceSymbol child:
                    VisitNamespace(child, cancellationToken);
                    break;
                case INamedTypeSymbol type:
                    VisitType(type, cancellationToken);
                    break;
            }
        }
    }

    private void VisitType(INamedTypeSymbol type, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (type.IsImplicitlyDeclared || SymbolNaming.MapKind(type) is not { } kind || !IsInSource(type))
        {
            return;
        }

        var fullyQualifiedName = AddSymbol(type, kind);
        _endpoints.VisitType(type);
        _persistence.VisitType(type);
        _configuration.VisitType(type);
        _external.VisitType(type);

        if (type.BaseType is { } baseType)
        {
            AddRelation(fullyQualifiedName, RelationKind.Inherits, baseType);
        }

        foreach (var @interface in type.Interfaces)
        {
            AddRelation(fullyQualifiedName, RelationKind.Implements, @interface);
        }

        AddInterfaceImplementations(type);

        // A delegate declares its signature on its invoke method, not on itself.
        if (type.DelegateInvokeMethod is { } invoke)
        {
            AddSignatureRelations(fullyQualifiedName, invoke);
        }

        foreach (var member in type.GetMembers())
        {
            if (member is INamedTypeSymbol nested)
            {
                VisitType(nested, cancellationToken);
                continue;
            }

            // Skips property accessors, record-synthesised members, backing fields and
            // the implicit default constructor: none of these were written by anyone.
            if (member.IsImplicitlyDeclared || SymbolNaming.MapKind(member) is not { } memberKind || !IsInSource(member))
            {
                continue;
            }

            var memberFullyQualifiedName = AddSymbol(member, memberKind);
            AddOverride(memberFullyQualifiedName, member);
            AddSignatureRelations(memberFullyQualifiedName, member);

            // What a constructor takes, attributed to the type rather than the constructor,
            // so the composition graph reaches a service in one hop.
            if (member is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor)
            {
                foreach (var parameter in constructor.Parameters)
                {
                    AddTypeDependency(fullyQualifiedName, RelationKind.Injects, parameter.Type, 0);
                }
            }
        }
    }

    /// <summary>
    /// Links the members that satisfy an interface to the interface members they satisfy.
    /// </summary>
    /// <remarks>
    /// Walking <see cref="INamedTypeSymbol.AllInterfaces"/> also reaches interfaces
    /// inherited from a base type; only implementations declared on <paramref name="type"/>
    /// itself are recorded, so a base class's implementation is attributed once rather
    /// than again for every type that inherits it.
    /// </remarks>
    private void AddInterfaceImplementations(INamedTypeSymbol type)
    {
        foreach (var @interface in type.AllInterfaces)
        {
            foreach (var interfaceMember in @interface.GetMembers())
            {
                if (SymbolNaming.MapKind(interfaceMember) is null ||
                    type.FindImplementationForInterfaceMember(interfaceMember) is not { } implementation ||
                    !SymbolEqualityComparer.Default.Equals(implementation.ContainingType, type) ||
                    SymbolNaming.MapKind(implementation) is null ||
                    !IsInSource(implementation))
                {
                    continue;
                }

                AddRelation(
                    SymbolNaming.FullyQualifiedName(implementation),
                    RelationKind.Implements,
                    interfaceMember);
            }
        }
    }

    private void AddOverride(string sourceFullyQualifiedName, ISymbol member)
    {
        ISymbol? overridden = member switch
        {
            IMethodSymbol method => method.OverriddenMethod,
            IPropertySymbol property => property.OverriddenProperty,
            IEventSymbol @event => @event.OverriddenEvent,
            _ => null,
        };

        if (overridden is not null)
        {
            AddRelation(sourceFullyQualifiedName, RelationKind.Overrides, overridden);
        }
    }

    /// <summary>
    /// Records the types a member's signature depends on: its parameters, and the type it
    /// yields. A constructor's parameter edges are exactly its construction dependencies.
    /// </summary>
    private void AddSignatureRelations(string sourceFullyQualifiedName, ISymbol member)
    {
        switch (member)
        {
            case IMethodSymbol method:
                AddParameters(sourceFullyQualifiedName, method.Parameters);
                if (!method.ReturnsVoid && method.MethodKind is not (MethodKind.Constructor or MethodKind.StaticConstructor))
                {
                    AddTypeDependency(sourceFullyQualifiedName, RelationKind.ReturnType, method.ReturnType, 0);
                }

                break;

            case IPropertySymbol property:
                AddParameters(sourceFullyQualifiedName, property.Parameters);
                AddTypeDependency(sourceFullyQualifiedName, RelationKind.ReturnType, property.Type, 0);
                break;

            case IFieldSymbol field:
                AddTypeDependency(sourceFullyQualifiedName, RelationKind.ReturnType, field.Type, 0);
                break;

            case IEventSymbol @event:
                AddTypeDependency(sourceFullyQualifiedName, RelationKind.ReturnType, @event.Type, 0);
                break;
        }
    }

    private void AddParameters(string sourceFullyQualifiedName, IEnumerable<IParameterSymbol> parameters)
    {
        foreach (var parameter in parameters)
        {
            AddTypeDependency(sourceFullyQualifiedName, RelationKind.ParameterType, parameter.Type, 0);
        }
    }

    /// <summary>
    /// Records a dependency on a type, unwrapping the constructions it is written inside:
    /// <c>Task&lt;Order[]&gt;</c> depends on <c>Order</c>. Only types declared in the
    /// solution are recorded, for the same reason references are.
    /// </summary>
    private void AddTypeDependency(string sourceFullyQualifiedName, RelationKind kind, ITypeSymbol type, int nesting)
    {
        if (nesting > MaxTypeNesting)
        {
            return;
        }

        switch (type)
        {
            case IArrayTypeSymbol array:
                AddTypeDependency(sourceFullyQualifiedName, kind, array.ElementType, nesting + 1);
                return;

            case IPointerTypeSymbol pointer:
                AddTypeDependency(sourceFullyQualifiedName, kind, pointer.PointedAtType, nesting + 1);
                return;

            case INamedTypeSymbol named:
                var definition = named.OriginalDefinition;
                if (IsIndexedTarget(definition) &&
                    !string.Equals(SymbolNaming.FullyQualifiedName(definition), sourceFullyQualifiedName, StringComparison.Ordinal))
                {
                    AddRelation(sourceFullyQualifiedName, kind, definition);
                }

                foreach (var argument in named.TypeArguments)
                {
                    AddTypeDependency(sourceFullyQualifiedName, kind, argument, nesting + 1);
                }

                return;
        }
    }

    private string AddSymbol(ISymbol symbol, IndexedSymbolKind kind)
    {
        var fullyQualifiedName = SymbolNaming.FullyQualifiedName(symbol);
        var (path, line, column, endLine) = LocationOf(symbol);

        _symbols.Add(new IndexedSymbol
        {
            Kind = kind,
            Name = symbol.Name,
            FullyQualifiedName = fullyQualifiedName,
            Display = SymbolNaming.Display(symbol),
            ProjectName = _projectName,
            Namespace = SymbolNaming.NamespaceOf(symbol),
            ContainerFullyQualifiedName = symbol.ContainingType is { } container
                ? SymbolNaming.FullyQualifiedName(container)
                : null,
            FilePath = path,
            Line = line,
            Column = column,
            EndLine = endLine,
            Accessibility = kind is IndexedSymbolKind.Namespace
                ? null
                : symbol.DeclaredAccessibility.ToString(),
            Attributes = symbol.GetAttributes()
                .Select(a => a.AttributeClass)
                .OfType<INamedTypeSymbol>()
                .Select(SymbolNaming.FullyQualifiedName)
                .Distinct(StringComparer.Ordinal)
                .ToList(),
        });

        return fullyQualifiedName;
    }

    /// <summary>Stores a relation a sub-collector already resolved to two names.</summary>
    private void AddPending(PendingRelation relation)
    {
        if (_seen.Add((relation.SourceFullyQualifiedName, relation.Kind, relation.TargetFullyQualifiedName)))
        {
            _relations.Add(relation);
        }
    }

    private void AddRelation(
        string sourceFullyQualifiedName,
        RelationKind kind,
        ISymbol target,
        RelationProvenance provenance = RelationProvenance.Exact)
    {
        var definition = target.OriginalDefinition;
        var targetFullyQualifiedName = SymbolNaming.FullyQualifiedName(definition);

        if (!_seen.Add((sourceFullyQualifiedName, kind, targetFullyQualifiedName)))
        {
            return;
        }

        _relations.Add(new PendingRelation(
            sourceFullyQualifiedName,
            kind,
            targetFullyQualifiedName,
            SymbolNaming.Display(definition),
            provenance));
    }

    /// <summary>
    /// True for symbols the index actually holds. Usage edges are restricted to these:
    /// edges into the BCL would dwarf the index without saying anything about your code.
    /// Inheritance and implementation edges deliberately keep their unindexed targets, so
    /// a base type such as <c>System.Object</c> is still named.
    /// </summary>
    private static bool IsIndexedTarget(ISymbol symbol) =>
        SymbolNaming.MapKind(symbol) is not (null or IndexedSymbolKind.Namespace) && IsInSource(symbol);

    private static bool IsInSource(ISymbol symbol) => symbol.Locations.Any(l => l.IsInSource);

    /// <summary>
    /// Where a declaration starts, and where it ends.
    /// </summary>
    /// <remarks>
    /// The start is the identifier, which is what an editor should open on. The end comes
    /// from the declaration syntax in that same file, so it covers the body: a symbol's
    /// span is what a diff hunk is matched against, and a method whose span stopped at its
    /// name would never be reported as changed. A partial type contributes several
    /// declarations, and the one holding the identifier is the one measured.
    /// </remarks>
    private static (string? Path, int? Line, int? Column, int? EndLine) LocationOf(ISymbol symbol)
    {
        if (symbol.Locations.FirstOrDefault(l => l.IsInSource) is not { } location)
        {
            return (null, null, null, null);
        }

        var span = location.GetLineSpan();
        var start = span.StartLinePosition.Line + 1;

        var declaration = symbol.DeclaringSyntaxReferences
            .FirstOrDefault(reference => reference.Span.Contains(location.SourceSpan));

        var end = declaration is null
            ? start
            : declaration.SyntaxTree.GetLineSpan(declaration.Span).EndLinePosition.Line + 1;

        return (span.Path, start, span.StartLinePosition.Character + 1, Math.Max(start, end));
    }

    // ---- calls and references -----------------------------------------------

    /// <summary>
    /// Binds one document once and hands it to every consumer that needs a semantic model.
    /// </summary>
    private async Task VisitDocumentAsync(Document document, CancellationToken cancellationToken)
    {
        if (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not { } root ||
            await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is not { } model)
        {
            return;
        }

        var enclosing = new EnclosingSymbolResolver(model);

        CollectBodyRelations(root, model, enclosing, cancellationToken);
        _registrations.VisitDocument(root, model, cancellationToken);
        _endpoints.VisitDocument(root, model, cancellationToken);
        _persistence.VisitDocument(root, model, enclosing, cancellationToken);
        _configuration.VisitDocument(root, model, enclosing, cancellationToken);
        _external.VisitDocument(root, model, enclosing, cancellationToken);
    }

    private void CollectBodyRelations(
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
                    AddBodyRelation(invocation, RelationKind.Calls, model, enclosing, cancellationToken);
                    break;

                case BaseObjectCreationExpressionSyntax creation:
                    AddBodyRelation(creation, RelationKind.Calls, model, enclosing, cancellationToken);
                    break;

                // The callee's own name is skipped: the invocation above already recorded
                // it, as a call rather than as a plain mention.
                case SimpleNameSyntax name when !IsCallee(name):
                    AddBodyRelation(name, RelationKind.References, model, enclosing, cancellationToken);
                    break;
            }
        }
    }

    private void AddBodyRelation(
        SyntaxNode node,
        RelationKind kind,
        SemanticModel model,
        EnclosingSymbolResolver enclosing,
        CancellationToken cancellationToken)
    {
        var (bound, provenance) = Bind(model, node, cancellationToken);
        if (bound is null)
        {
            return;
        }

        var target = SymbolNaming.Definition(bound);

        // A call must resolve to a method; anything else came from a syntax shape that
        // only looks like one, and is not recorded as a call.
        if (kind is RelationKind.Calls && target is not IMethodSymbol)
        {
            return;
        }

        if (!IsIndexedTarget(target))
        {
            return;
        }

        if (enclosing.NameOf(node, cancellationToken) is not { } source ||
            source == SymbolNaming.FullyQualifiedName(target))
        {
            return;
        }

        AddRelation(source, kind, target, provenance);
    }

    /// <summary>
    /// Resolves a syntax node to the symbol it means. An exact binding is the normal case;
    /// when the compiler could not choose, a lone candidate is still worth an edge, marked
    /// as inferred. Several candidates are a guess and are dropped.
    /// </summary>
    private static (ISymbol? Symbol, RelationProvenance Provenance) Bind(
        SemanticModel model,
        SyntaxNode node,
        CancellationToken cancellationToken)
    {
        var info = model.GetSymbolInfo(node, cancellationToken);

        if (info.Symbol is { } exact)
        {
            return (exact, RelationProvenance.Exact);
        }

        return info.CandidateSymbols.Length == 1
            ? (info.CandidateSymbols[0], RelationProvenance.Inferred)
            : (null, RelationProvenance.Exact);
    }

    /// <summary>True when this name is the thing being invoked, rather than a mention of it.</summary>
    private static bool IsCallee(SimpleNameSyntax name) => name.Parent switch
    {
        InvocationExpressionSyntax invocation => invocation.Expression == name,
        MemberAccessExpressionSyntax access when access.Name == name =>
            access.Parent is InvocationExpressionSyntax invocation && invocation.Expression == access,
        MemberBindingExpressionSyntax binding when binding.Name == name =>
            binding.Parent is InvocationExpressionSyntax invocation && invocation.Expression == binding,
        _ => false,
    };

    /// <summary>Reduces extension-method invocations and constructed generics to their definition.</summary>
}
