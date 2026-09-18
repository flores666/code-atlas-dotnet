using CodeAtlas.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeAtlas.Core.Analysis;

/// <summary>
/// Analyses one Roslyn project: its declarations, the call and dispatch edges between
/// them, and the HTTP entry points it declares.
/// </summary>
/// <remarks>
/// <para>
/// Declarations come from the project's own assembly symbol, so partial types are
/// captured once. Call edges are collected per document from the semantic model that is
/// already being built for the compilation — deliberately not via
/// <c>SymbolFinder.FindReferencesAsync</c>, which is a whole-solution operation.
/// </para>
/// <para>
/// Every edge here is compiler-derived. A binding Roslyn resolved to exactly one symbol
/// is <see cref="RelationProvenance.Exact"/>; a binding it failed to resolve but that has
/// a single candidate is kept as <see cref="RelationProvenance.Inferred"/>, so a project
/// with errors still maps, without that guess being presented as compiler truth.
/// </para>
/// <para>
/// Endpoints are collected here rather than in a pass of their own, because this is
/// where a project's documents are already bound: the semantic model is the expensive
/// part, and walking every document again to rebuild it would cost more than the
/// analysis does.
/// </para>
/// </remarks>
public sealed class SymbolCollector
{
    private readonly List<IndexedSymbol> _symbols = [];
    private readonly List<PendingRelation> _relations = [];
    private readonly HashSet<(string Source, RelationKind Kind, string Target)> _seen = [];
    private readonly List<IndexDiagnostic> _diagnostics = [];
    private readonly EndpointCollector _endpoints;
    private readonly string _projectName;

    private SymbolCollector(string projectName)
    {
        _projectName = projectName;
        _endpoints = new EndpointCollector(projectName);
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

        collector.ReportCompilationErrors(compilation, project.Name, cancellationToken);

        return new ProjectIndexData(
            indexedProject,
            collector._symbols,
            collector._relations,
            collector._diagnostics)
        {
            Endpoints = collector._endpoints.Endpoints,
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

        AddSymbol(type, kind);
        _endpoints.VisitType(type);

        AddInterfaceImplementations(type);

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

            AddOverride(AddSymbol(member, memberKind), member);
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

    private string AddSymbol(ISymbol symbol, IndexedSymbolKind kind)
    {
        var fullyQualifiedName = SymbolNaming.FullyQualifiedName(symbol);
        var (path, line, column) = LocationOf(symbol);

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
            Accessibility = kind is IndexedSymbolKind.Namespace
                ? null
                : symbol.DeclaredAccessibility.ToString(),
        });

        return fullyQualifiedName;
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
    /// True for symbols the index actually holds. Call edges are restricted to these:
    /// edges into the BCL would dwarf the index without saying anything about your code.
    /// Implementation and override edges deliberately keep their unindexed targets, so an
    /// interface declared outside the solution is still named.
    /// </summary>
    private static bool IsIndexedTarget(ISymbol symbol) =>
        SymbolNaming.MapKind(symbol) is not (null or IndexedSymbolKind.Namespace) && IsInSource(symbol);

    private static bool IsInSource(ISymbol symbol) => symbol.Locations.Any(l => l.IsInSource);

    /// <summary>
    /// Where a symbol is declared: the position of its own identifier, which is what an
    /// editor is asked to open.
    /// </summary>
    private static (string? Path, int? Line, int? Column) LocationOf(ISymbol symbol)
    {
        if (symbol.Locations.FirstOrDefault(l => l.IsInSource) is not { } location)
        {
            return (null, null, null);
        }

        var span = location.GetLineSpan();

        return (span.Path, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
    }

    // ---- calls --------------------------------------------------------------

    /// <summary>
    /// Error ids that mean a reference is missing rather than that the code is wrong.
    /// </summary>
    private static readonly IReadOnlySet<string> UnresolvedReferenceIds =
        new HashSet<string>(StringComparer.Ordinal) { "CS0246", "CS0234", "CS0012", "CS0400", "CS1069" };

    /// <summary>
    /// Reports a project that loaded but does not compile.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This earns a diagnostic because the failure is otherwise invisible, and an
    /// unreported one leaves the index quietly wrong rather than obviously empty:
    /// declarations still come through, so the project looks indexed and the symbol count
    /// looks healthy, while every binding that needed a missing reference silently
    /// resolved to nothing.
    /// </para>
    /// <para>
    /// Endpoints are the clearest casualty and so are named explicitly. A controller whose
    /// base type did not resolve is not recognisably a controller; a <c>MapGet</c> whose
    /// builder parameter is an error type is not recognisably a route. Both are then
    /// absent for a reason that has nothing to do with the code being read, which is
    /// precisely the case a reader cannot diagnose on their own.
    /// </para>
    /// </remarks>
    private void ReportCompilationErrors(
        Compilation compilation,
        string projectName,
        CancellationToken cancellationToken)
    {
        var errors = compilation
            .GetDiagnostics(cancellationToken)
            .Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .ToList();

        if (errors.Count == 0)
        {
            return;
        }

        var unresolved = errors.Where(error => UnresolvedReferenceIds.Contains(error.Id)).ToList();

        var message = unresolved.Count > 0
            ? $"{errors.Count} compilation error(s), {unresolved.Count} of them unresolved references " +
              $"(for example: {unresolved[0].GetMessage()}). Types the compiler could not bind are " +
              "invisible to analysis, so endpoints, relations and infrastructure in this project are " +
              "under-reported. This usually means the targeting pack for the project's target framework " +
              "is not installed, or the solution has never been restored. Fix that and reindex."
            : $"{errors.Count} compilation error(s), so this project is only partly analysed " +
              $"(for example: {errors[0].GetMessage()}).";

        _diagnostics.Add(new IndexDiagnostic(Model.DiagnosticSeverity.Warning, projectName, message));
    }

    private async Task VisitDocumentAsync(Document document, CancellationToken cancellationToken)
    {
        if (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not { } root ||
            await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is not { } model)
        {
            return;
        }

        var enclosing = new EnclosingSymbolResolver(model);

        CollectCalls(root, model, enclosing, cancellationToken);
        _endpoints.VisitDocument(root, model, cancellationToken);
    }

    /// <summary>
    /// Records what each member in this document invokes: every call site and every
    /// <c>new</c>, attributed to the member the expression sits inside.
    /// </summary>
    private void CollectCalls(
        SyntaxNode root,
        SemanticModel model,
        EnclosingSymbolResolver enclosing,
        CancellationToken cancellationToken)
    {
        foreach (var node in root.DescendantNodes())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (node is InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax)
            {
                AddCall(node, model, enclosing, cancellationToken);
            }
        }
    }

    private void AddCall(
        SyntaxNode node,
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
        if (target is not IMethodSymbol || !IsIndexedTarget(target))
        {
            return;
        }

        if (enclosing.NameOf(node, cancellationToken) is not { } source ||
            source == SymbolNaming.FullyQualifiedName(target))
        {
            return;
        }

        AddRelation(source, RelationKind.Calls, target, provenance);
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

}
