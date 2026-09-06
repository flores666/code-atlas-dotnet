using CodeAtlas.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeAtlas.Core.Analysis;

/// <summary>
/// Finds where the application crosses out to infrastructure it does not own.
/// </summary>
/// <remarks>
/// <para>
/// A boundary is recognised by the fully qualified name of the client type the compiler
/// bound — <c>StackExchange.Redis.*</c>, <c>Confluent.Kafka.*</c>,
/// <c>System.Net.Http.HttpClient</c> and so on — or by the registration extension that
/// wires one up. That is deliberately not a model of any of these frameworks: the useful
/// answer is which of your types sits on this side of the boundary, not what it sends.
/// </para>
/// <para>
/// The consumer is the type the call or injection was written in, and one row is kept per
/// component and technology, so a class that injects a Redis multiplexer and then calls
/// through the database it hands back says "CartCache talks to Redis" once. The most
/// specific way it was found wins: a typed-client registration names the client and the
/// name it was given, and so beats the bare injection of it.
/// </para>
/// </remarks>
public sealed class ExternalDependencyCollector
{
    /// <summary>Registration extensions that wire up infrastructure without naming its type.</summary>
    private static readonly Dictionary<string, ExternalTechnology> Registrations = new(StringComparer.Ordinal)
    {
        ["AddHttpClient"] = ExternalTechnology.HttpApi,
        ["AddGrpcClient"] = ExternalTechnology.Grpc,
        ["AddStackExchangeRedisCache"] = ExternalTechnology.Redis,
        ["AddMassTransit"] = ExternalTechnology.MassTransit,
    };

    /// <summary>Types in <c>System.IO</c> that actually touch the disk.</summary>
    private static readonly string[] FileSystemTypes =
    [
        "System.IO.File", "System.IO.Directory", "System.IO.FileInfo", "System.IO.DirectoryInfo",
        "System.IO.FileStream", "System.IO.StreamReader", "System.IO.StreamWriter",
        "System.IO.FileSystemWatcher", "System.IO.DriveInfo",
    ];

    /// <summary>
    /// The type that best stands for each technology's boundary, so every reading of one
    /// lands on the same client rather than on whichever extension class declared the
    /// call. Resolved against the compilation, and skipped when it is not referenced.
    /// </summary>
    private static readonly Dictionary<ExternalTechnology, string> CanonicalClients = new()
    {
        [ExternalTechnology.HttpApi] = "System.Net.Http.HttpClient",
        [ExternalTechnology.Grpc] = "Grpc.Core.ClientBase`1",
        [ExternalTechnology.Redis] = "StackExchange.Redis.IConnectionMultiplexer",
        [ExternalTechnology.MassTransit] = "MassTransit.IBus",
    };

    private readonly Dictionary<(string Consumer, ExternalTechnology Technology), ExternalDependency> _dependencies = [];
    private readonly string? _projectName;

    public ExternalDependencyCollector(string? projectName) => _projectName = projectName;

    public IReadOnlyList<ExternalDependency> Dependencies => _dependencies.Values
        .OrderBy(dependency => dependency.Technology)
        .ThenBy(dependency => dependency.ConsumerDisplay, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>One edge per boundary, from the component to the client type it reaches.</summary>
    public IReadOnlyList<PendingRelation> Relations => _dependencies.Values
        .Select(dependency => new PendingRelation(
            dependency.ConsumerFullyQualifiedName,
            RelationKind.UsesExternal,
            dependency.ClientFullyQualifiedName,
            dependency.ClientDisplay))
        .ToList();

    // ---- declarations -------------------------------------------------------

    public void VisitType(INamedTypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);

        // A generated gRPC client derives from ClientBase: the type is itself the boundary.
        if (type.BaseType is { } baseType && TechnologyOf(baseType) is { } inherited)
        {
            Add(inherited, ExternalBinding.TypedClient, baseType, type, name: null, SourceFacts.LocationOf(type));
        }

        foreach (var parameter in type.GetMembers()
                     .OfType<IMethodSymbol>()
                     .Where(method => method.MethodKind == MethodKind.Constructor)
                     .SelectMany(method => method.Parameters))
        {
            if (TechnologyOf(parameter.Type) is { } injected)
            {
                Add(injected, ExternalBinding.Injection, parameter.Type, type, name: null, SourceFacts.LocationOf(type));
            }
        }
    }

    // ---- bodies -------------------------------------------------------------

    internal void VisitDocument(
        SyntaxNode root,
        SemanticModel model,
        EnclosingSymbolResolver enclosing,
        CancellationToken cancellationToken)
    {
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (model.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method)
            {
                continue;
            }

            if (Registrations.TryGetValue(method.Name, out var registered))
            {
                AddRegistration(invocation, method, registered, model, enclosing, cancellationToken);
                continue;
            }

            // Any call into a recognised client type is a crossing, wherever it was written.
            if (method.ContainingType is { } containing &&
                TechnologyOf(containing) is { } technology &&
                enclosing.ContainingType(invocation, cancellationToken) is { } consumer)
            {
                Add(technology, ExternalBinding.Call, containing.OriginalDefinition, consumer,
                    name: null, SourceFacts.LocationOf(invocation));
            }
        }
    }

    /// <summary>
    /// Reads a factory registration. The client it configures is the type argument when
    /// there is one — that is the typed-client form the flow is read along — and otherwise
    /// only the name it was given.
    /// </summary>
    private void AddRegistration(
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        ExternalTechnology technology,
        SemanticModel model,
        EnclosingSymbolResolver enclosing,
        CancellationToken cancellationToken)
    {
        var name = invocation.ArgumentList.Arguments
            .Select(argument => SourceFacts.ConstantString(argument.Expression, model, cancellationToken))
            .FirstOrDefault(value => value is { Length: > 0 });

        var location = SourceFacts.LocationOf(invocation);

        // AddHttpClient<IClient, Client>() registers the implementation as the client.
        var consumer = method.TypeArguments switch
        {
            [_, INamedTypeSymbol implementation] => implementation,
            [INamedTypeSymbol only] => only,
            _ => null,
        };

        var client = ClientTypeOf(technology, method, model);

        if (consumer is not null && SourceFacts.IsInSource(consumer))
        {
            Add(technology, ExternalBinding.TypedClient, client, consumer, name, location);
            return;
        }

        // A named client with no type of its own still marks a boundary; it belongs to
        // whatever registered it.
        if (name is { Length: > 0 } && enclosing.ContainingType(invocation, cancellationToken) is { } owner)
        {
            Add(technology, ExternalBinding.NamedClient, client, owner, name, location);
        }
    }

    /// <summary>
    /// The type that stands for the boundary a registration wires up: the technology's own
    /// client where the compilation has it, and otherwise the extension method's declaring
    /// type, which is the most precise thing left.
    /// </summary>
    private static ITypeSymbol? ClientTypeOf(ExternalTechnology technology, IMethodSymbol method, SemanticModel model) =>
        (CanonicalClients.TryGetValue(technology, out var metadataName)
            ? model.Compilation.GetTypeByMetadataName(metadataName)
            : null)
        ?? method.ContainingType;

    // ---- recognition --------------------------------------------------------

    /// <summary>
    /// The technology a type belongs to, following its base chain so a generated client
    /// counts as the framework it derives from.
    /// </summary>
    private static ExternalTechnology? TechnologyOf(ITypeSymbol type)
    {
        for (var current = type.OriginalDefinition as INamedTypeSymbol; current is not null; current = current.BaseType)
        {
            if (TechnologyOfName(SymbolNaming.FullyQualifiedName(current)) is { } technology)
            {
                return technology;
            }
        }

        return null;
    }

    private static ExternalTechnology? TechnologyOfName(string fullyQualifiedName) => fullyQualifiedName switch
    {
        "System.Net.Http.HttpClient" or "System.Net.Http.IHttpClientFactory" or "System.Net.Http.HttpMessageInvoker" =>
            ExternalTechnology.HttpApi,
        _ when Under(fullyQualifiedName, "StackExchange.Redis") => ExternalTechnology.Redis,
        _ when Under(fullyQualifiedName, "Microsoft.Extensions.Caching.StackExchangeRedis") => ExternalTechnology.Redis,
        _ when Under(fullyQualifiedName, "Grpc.Core") || Under(fullyQualifiedName, "Grpc.Net.Client") =>
            ExternalTechnology.Grpc,
        _ when Under(fullyQualifiedName, "MassTransit") => ExternalTechnology.MassTransit,
        _ when Under(fullyQualifiedName, "RabbitMQ.Client") => ExternalTechnology.RabbitMq,
        _ when Under(fullyQualifiedName, "Confluent.Kafka") => ExternalTechnology.Kafka,
        _ when Under(fullyQualifiedName, "Amazon.S3") || Under(fullyQualifiedName, "Minio") =>
            ExternalTechnology.ObjectStorage,
        _ when Under(fullyQualifiedName, "WebDav") => ExternalTechnology.WebDav,
        _ when FileSystemTypes.Contains(fullyQualifiedName, StringComparer.Ordinal) => ExternalTechnology.FileSystem,
        _ => null,
    };

    private static bool Under(string fullyQualifiedName, string @namespace) =>
        fullyQualifiedName.StartsWith(@namespace, StringComparison.Ordinal) &&
        fullyQualifiedName.Length > @namespace.Length &&
        fullyQualifiedName[@namespace.Length] == '.';

    // ---- accumulation -------------------------------------------------------

    /// <summary>How much a way of finding a boundary tells you; the most specific wins.</summary>
    private static int Precedence(ExternalBinding binding) => binding switch
    {
        ExternalBinding.TypedClient => 3,
        ExternalBinding.NamedClient => 2,
        ExternalBinding.Injection => 1,
        _ => 0,
    };

    private void Add(
        ExternalTechnology technology,
        ExternalBinding binding,
        ITypeSymbol? client,
        INamedTypeSymbol consumer,
        string? name,
        (string? Path, int? Line) location)
    {
        if (client is null || !SourceFacts.IsInSource(consumer))
        {
            return;
        }

        var clientDefinition = client.OriginalDefinition;
        var clientName = SymbolNaming.FullyQualifiedName(clientDefinition);
        var consumerName = SymbolNaming.FullyQualifiedName(consumer);

        if (clientName == consumerName)
        {
            return;
        }

        var candidate = new ExternalDependency
        {
            Technology = technology,
            Binding = binding,
            ClientFullyQualifiedName = clientName,
            ClientDisplay = SymbolNaming.Display(clientDefinition),
            Name = name,
            ConsumerFullyQualifiedName = consumerName,
            ConsumerDisplay = SymbolNaming.Display(consumer),
            ProjectName = _projectName,
            FilePath = location.Path,
            Line = location.Line,
        };

        var key = (consumerName, technology);
        if (!_dependencies.TryGetValue(key, out var existing) ||
            Precedence(binding) > Precedence(existing.Binding))
        {
            _dependencies[key] = candidate with { Name = candidate.Name ?? existing?.Name };
        }
        else if (existing.Name is null && name is not null)
        {
            _dependencies[key] = existing with { Name = name };
        }
    }
}
