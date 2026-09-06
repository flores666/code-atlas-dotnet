using CodeAtlas.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeAtlas.Core.Analysis;

/// <summary>
/// Reads how an application gets at its configuration: the keys and sections it names,
/// and the options types it binds them to.
/// </summary>
/// <remarks>
/// <para>
/// Only statically discoverable names are recorded. A key composed at run time is left
/// out rather than guessed at, and a section path is followed back along the receiver
/// chain only as far as that chain is literal — so <c>GetSection("Crm").GetSection(name)</c>
/// reports the outer section and stops.
/// </para>
/// <para>
/// A read is attributed to the type it was written in, not to the member, for the reason
/// <see cref="RelationKind.Injects"/> is attributed to a type: configuration is a property
/// of the component, and the graph should reach it in one hop.
/// </para>
/// </remarks>
public sealed class ConfigurationCollector
{
    private const string Configuration = "Microsoft.Extensions.Configuration";
    private const string Options = "Microsoft.Extensions.Options";
    private const string ConfigurationName = $"{Configuration}.IConfiguration";

    /// <summary>How far a section path is chased back through nested <c>GetSection</c> calls.</summary>
    private const int MaxSectionDepth = 6;

    /// <summary>The options wrappers the container hands to a consumer.</summary>
    private static readonly string[] OptionsAccessors =
        ["IOptions", "IOptionsSnapshot", "IOptionsMonitor", "IOptionsFactory"];

    private readonly List<ConfigurationUsage> _usages = [];
    private readonly HashSet<(string?, string?, ConfigurationAccess, string?)> _seen = [];
    private readonly List<PendingRelation> _relations = [];
    private readonly string? _projectName;

    public ConfigurationCollector(string? projectName) => _projectName = projectName;

    public IReadOnlyList<ConfigurationUsage> Usages => _usages;

    public IReadOnlyList<PendingRelation> Relations => _relations;

    // ---- declarations -------------------------------------------------------

    /// <summary>
    /// Records what a type is handed through its constructor: an options wrapper names the
    /// type it binds, and <c>IConfiguration</c> itself says only that the type reads
    /// configuration under its own steam.
    /// </summary>
    public void VisitType(INamedTypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var (path, line) = SourceFacts.LocationOf(type);

        foreach (var parameter in type.GetMembers()
                     .OfType<IMethodSymbol>()
                     .Where(method => method.MethodKind == MethodKind.Constructor)
                     .SelectMany(method => method.Parameters))
        {
            if (OptionsPayload(parameter.Type) is { } bound)
            {
                Add(ConfigurationAccess.Options, key: null, bound, type, path, line);
            }
            else if (SymbolNaming.FullyQualifiedName(parameter.Type.OriginalDefinition) == ConfigurationName)
            {
                Add(ConfigurationAccess.Provider, key: null, options: null, type, path, line);
            }
        }
    }

    /// <summary>The type an <c>IOptions&lt;T&gt;</c>-shaped wrapper carries.</summary>
    private static ITypeSymbol? OptionsPayload(ITypeSymbol type) =>
        type is INamedTypeSymbol { TypeArguments: [{ } payload] } wrapper &&
        SymbolNaming.NamespaceOf(wrapper.OriginalDefinition) == Options &&
        OptionsAccessors.Contains(wrapper.Name, StringComparer.Ordinal)
            ? payload
            : null;

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

                // configuration["Crm:BaseUrl"]
                case ElementAccessExpressionSyntax access
                    when IsConfiguration(model.GetTypeInfo(access.Expression, cancellationToken).Type) &&
                         access.ArgumentList.Arguments is [{ } argument] &&
                         SourceFacts.ConstantString(argument.Expression, model, cancellationToken) is { } key:
                    AddAt(access, ConfigurationAccess.Value,
                        Join(SectionPath(access.Expression, model, cancellationToken), key),
                        options: null, enclosing, cancellationToken);
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

        var receiver = SourceFacts.Receiver(invocation);
        var literal = invocation.ArgumentList.Arguments.Count > 0
            ? SourceFacts.ConstantString(invocation.ArgumentList.Arguments[0].Expression, model, cancellationToken)
            : null;

        // Environment variables are configuration by another name, and the only thing
        // knowable about one is what it is called.
        if (method is { Name: "GetEnvironmentVariable", ContainingType.Name: "Environment" } &&
            SymbolNaming.NamespaceOf(method.ContainingType) == "System" &&
            literal is { Length: > 0 })
        {
            AddAt(invocation, ConfigurationAccess.Environment, literal, options: null, enclosing, cancellationToken);
            return;
        }

        // Options binding: services.Configure<T>(section), AddOptions<T>(), section.Get<T>().
        if (OptionsTarget(invocation, method, model, cancellationToken) is { } bound)
        {
            AddAt(invocation, ConfigurationAccess.Options, BoundSection(invocation, model, cancellationToken),
                bound, enclosing, cancellationToken);
            return;
        }

        if (!IsConfigurationApi(method, receiver, model, cancellationToken))
        {
            return;
        }

        var access = method.Name switch
        {
            "GetSection" or "GetChildren" or "GetRequiredSection" => ConfigurationAccess.Section,
            "GetValue" or "GetRequiredValue" => ConfigurationAccess.Value,
            "GetConnectionString" => ConfigurationAccess.ConnectionString,
            _ => (ConfigurationAccess?)null,
        };

        if (access is not { } kind)
        {
            return;
        }

        // A GetSection call is only worth a row on its own when nothing further consumes
        // it; the chained forms above already carry the fuller reading.
        var path = kind == ConfigurationAccess.ConnectionString
            ? literal
            : Join(SectionPath(receiver, model, cancellationToken), literal);

        if (path is { Length: > 0 })
        {
            AddAt(invocation, kind, path, options: null, enclosing, cancellationToken);
        }
    }

    /// <summary>
    /// The options type a binding call names: the type argument of <c>Configure&lt;T&gt;</c>,
    /// <c>AddOptions&lt;T&gt;</c> or <c>Get&lt;T&gt;</c>, or the type of the instance handed
    /// to <c>Bind</c>.
    /// </summary>
    private static ITypeSymbol? OptionsTarget(
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        var declaring = method.ContainingType is { } containing
            ? SymbolNaming.NamespaceOf(containing.OriginalDefinition)
            : null;

        var isOptionsApi = declaring is Configuration or Options
                           or "Microsoft.Extensions.DependencyInjection";

        if (!isOptionsApi)
        {
            return null;
        }

        if (method.Name is "Configure" or "AddOptions" or "ConfigureOptions" or "Get" or "BindConfiguration" &&
            method.TypeArguments is [{ } argument])
        {
            return argument;
        }

        return method.Name == "Bind" && invocation.ArgumentList.Arguments.Count > 0
            ? model.GetTypeInfo(invocation.ArgumentList.Arguments[^1].Expression, cancellationToken).Type
            : null;
    }

    /// <summary>The section a binding call reads from, whichever of its parts carries it.</summary>
    private static string? BoundSection(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        var candidates = invocation.ArgumentList.Arguments
            .Select(argument => argument.Expression)
            .Prepend(SourceFacts.Receiver(invocation)!)
            .Where(expression => expression is not null);

        foreach (var candidate in candidates)
        {
            if (SectionPath(candidate, model, cancellationToken) is { Length: > 0 } section)
            {
                return section;
            }

            if (SourceFacts.ConstantString(candidate, model, cancellationToken) is { Length: > 0 } literal)
            {
                return literal;
            }
        }

        return null;
    }

    /// <summary>
    /// The colon-separated path a chain of <c>GetSection</c> calls names, read back from
    /// the innermost call. Stops at the first link that is not a literal.
    /// </summary>
    private static string? SectionPath(
        ExpressionSyntax? expression,
        SemanticModel model,
        CancellationToken cancellationToken,
        int depth = 0)
    {
        if (depth >= MaxSectionDepth || expression is null)
        {
            return null;
        }

        if (expression is not InvocationExpressionSyntax invocation ||
            model.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol
            {
                Name: "GetSection" or "GetRequiredSection"
            } ||
            invocation.ArgumentList.Arguments is not [{ } argument] ||
            SourceFacts.ConstantString(argument.Expression, model, cancellationToken) is not { } section)
        {
            return null;
        }

        return Join(SectionPath(SourceFacts.Receiver(invocation), model, cancellationToken, depth + 1), section);
    }

    private static string? Join(string? prefix, string? suffix) => (prefix, suffix) switch
    {
        ({ Length: > 0 }, { Length: > 0 }) => $"{prefix}:{suffix}",
        ({ Length: > 0 }, _) => prefix,
        _ => suffix,
    };

    private static bool IsConfigurationApi(
        IMethodSymbol method,
        ExpressionSyntax? receiver,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        if (IsConfiguration(method.ContainingType))
        {
            return true;
        }

        // The reading extensions are declared on static classes, so what makes the call a
        // configuration call is what it was made on.
        return method.ContainingType is { } declaring &&
               SymbolNaming.NamespaceOf(declaring.OriginalDefinition) == Configuration &&
               receiver is not null &&
               IsConfiguration(model.GetTypeInfo(receiver, cancellationToken).Type);
    }

    private static bool IsConfiguration(ITypeSymbol? type) =>
        type is not null &&
        (SymbolNaming.FullyQualifiedName(type.OriginalDefinition) == ConfigurationName ||
         type.AllInterfaces.Any(@interface =>
             SymbolNaming.FullyQualifiedName(@interface.OriginalDefinition) == ConfigurationName));

    // ---- accumulation -------------------------------------------------------

    private void AddAt(
        SyntaxNode node,
        ConfigurationAccess access,
        string? key,
        ITypeSymbol? options,
        EnclosingSymbolResolver enclosing,
        CancellationToken cancellationToken)
    {
        var (path, line) = SourceFacts.LocationOf(node);
        Add(access, key, options, enclosing.ContainingType(node, cancellationToken), path, line);
    }

    private void Add(
        ConfigurationAccess access,
        string? key,
        ITypeSymbol? options,
        INamedTypeSymbol? consumer,
        string? path,
        int? line)
    {
        var optionsDefinition = options?.OriginalDefinition;
        var optionsName = optionsDefinition is INamedTypeSymbol named && SourceFacts.IsInSource(named)
            ? SymbolNaming.FullyQualifiedName(named)
            : null;

        var consumerName = consumer is not null && SourceFacts.IsInSource(consumer)
            ? SymbolNaming.FullyQualifiedName(consumer)
            : null;

        if (key is null && optionsName is null && consumerName is null)
        {
            return;
        }

        if (!_seen.Add((key, optionsName, access, consumerName)))
        {
            return;
        }

        _usages.Add(new ConfigurationUsage
        {
            Access = access,
            Key = key,
            OptionsFullyQualifiedName = optionsName,
            OptionsDisplay = optionsName is null ? null : SymbolNaming.Display(optionsDefinition!),
            ConsumerFullyQualifiedName = consumerName,
            ConsumerDisplay = consumerName is null ? null : SymbolNaming.Display(consumer!),
            ProjectName = _projectName,
            FilePath = path,
            Line = line,
        });

        if (consumerName is not null && optionsName is not null && consumerName != optionsName)
        {
            _relations.Add(new PendingRelation(
                consumerName,
                RelationKind.ReadsConfiguration,
                optionsName,
                SymbolNaming.Display(optionsDefinition!)));
        }
    }
}
