using CodeAtlas.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeAtlas.Core.Analysis;

/// <summary>
/// Reads <c>IServiceCollection</c> registrations out of source.
/// </summary>
/// <remarks>
/// <para>
/// This reads registration <em>calls</em>; it does not execute configuration code. A call
/// is recognised by binding it to an extension method on
/// <c>Microsoft.Extensions.DependencyInjection.IServiceCollection</c> whose name carries a
/// lifetime, which is why a registration written inside a project's own
/// <c>AddInfrastructure</c> extension is found exactly like one in <c>Program.cs</c>: the
/// walk covers every document, and nothing depends on where the call sits.
/// </para>
/// <para>
/// What a factory returns is recovered from the shape of its body when that is a single
/// object creation, and marked <see cref="RelationProvenance.Inferred"/>. A factory that
/// branches, or builds its result some other way, contributes a registration with no
/// implementation rather than a guess.
/// </para>
/// </remarks>
public sealed class ServiceRegistrationCollector
{
    private const string ServiceCollectionMetadataName =
        "Microsoft.Extensions.DependencyInjection.IServiceCollection";

    /// <summary>
    /// Factories that register a typed client. They are registrations like any other —
    /// transient, which is the lifetime the client factories give a typed client — but
    /// their extra arguments configure the client rather than build the service, so their
    /// type arguments are the only part worth reading.
    /// </summary>
    private static readonly string[] TypedClientFactories = ["AddHttpClient", "AddGrpcClient"];

    private readonly List<ServiceRegistration> _registrations = [];

    public IReadOnlyList<ServiceRegistration> Registrations => _registrations;

    public void VisitDocument(SyntaxNode root, SemanticModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(model);

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (model.GetSymbolInfo(invocation, cancellationToken).Symbol is IMethodSymbol method &&
                LifetimeOf(method) is { } lifetime)
            {
                Add(invocation, method, lifetime, model, cancellationToken);
            }
        }
    }

    /// <summary>
    /// The lifetime a call registers, or <c>null</c> when it is not a registration at all.
    /// </summary>
    /// <remarks>
    /// Matching on the extension's receiver rather than on its declaring class covers the
    /// several static classes the framework spreads these methods across, and rejects an
    /// unrelated method that merely happens to be called <c>AddSingleton</c>.
    /// </remarks>
    private static ServiceLifetime? LifetimeOf(IMethodSymbol method)
    {
        var definition = (method.ReducedFrom ?? method).OriginalDefinition;

        if (!definition.IsExtensionMethod ||
            definition.Parameters.Length == 0 ||
            SymbolNaming.FullyQualifiedName(definition.Parameters[0].Type)
                is not ServiceCollectionMetadataName)
        {
            return null;
        }

        var name = definition.Name;

        // A typed client registration carries no lifetime in its name; the factory always
        // gives one a transient lifetime. Without a type argument it registers a named
        // client and no service at all.
        if (TypedClientFactories.Contains(name, StringComparer.Ordinal))
        {
            return method.TypeArguments.Length > 0 ? ServiceLifetime.Transient : null;
        }

        // TryAdd* differ from Add* only in whether they overwrite, which the index does
        // not model: both mean "this implementation may satisfy this service".
        var suffix =
            name.StartsWith("TryAdd", StringComparison.Ordinal) ? name["TryAdd".Length..] :
            name.StartsWith("Add", StringComparison.Ordinal) ? name["Add".Length..] :
            string.Empty;

        return suffix switch
        {
            "Singleton" => ServiceLifetime.Singleton,
            "Scoped" => ServiceLifetime.Scoped,
            "Transient" => ServiceLifetime.Transient,
            _ => null,
        };
    }

    private void Add(
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        ServiceLifetime lifetime,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        var arguments = invocation.ArgumentList.Arguments;
        var typeArguments = method.TypeArguments;

        // A typed client's arguments configure the client rather than build the service,
        // so only its type arguments are read; every other registration reads both.
        var (service, implementation, kind, provenance) =
            TypedClientFactories.Contains((method.ReducedFrom ?? method).OriginalDefinition.Name, StringComparer.Ordinal)
                ? FromTypeArguments(typeArguments)
                : typeArguments.Length switch
                {
                    // AddScoped<TService, TImplementation>()
                    >= 2 => FromTypeArguments(typeArguments),

                    // AddScoped<T>(), AddScoped<T>(factory), AddSingleton<T>(instance)
                    1 => FromSingleTypeArgument(typeArguments[0], arguments, model, cancellationToken),

                    // AddScoped(typeof(IFoo), typeof(Foo)), AddSingleton(instance)
                    _ => FromArguments(arguments, model, cancellationToken),
                };

        if (service is null)
        {
            return;
        }

        var declaring = invocation.FirstAncestorOrSelf<MemberDeclarationSyntax>();
        var (path, line) = LocationOf(invocation);

        _registrations.Add(new ServiceRegistration
        {
            ServiceFullyQualifiedName = SymbolNaming.FullyQualifiedName(service.OriginalDefinition),
            ServiceDisplay = SymbolNaming.Display(service.OriginalDefinition),
            ImplementationFullyQualifiedName = implementation is null
                ? null
                : SymbolNaming.FullyQualifiedName(implementation.OriginalDefinition),
            ImplementationDisplay = implementation is null
                ? null
                : SymbolNaming.Display(implementation.OriginalDefinition),
            Lifetime = lifetime,
            Kind = kind,
            Provenance = provenance,
            FilePath = path,
            Line = line,
            DeclaringMember = declaring is null
                ? null
                : model.GetDeclaredSymbol(declaring, cancellationToken) is { } owner
                    ? SymbolNaming.Display(owner)
                    : null,
        });
    }

    /// <summary>The service and implementation named directly as type arguments.</summary>
    private static (ITypeSymbol? Service, ITypeSymbol? Implementation, RegistrationKind Kind, RelationProvenance Provenance)
        FromTypeArguments(IReadOnlyList<ITypeSymbol> typeArguments) =>
        typeArguments.Count switch
        {
            0 => (null, null, RegistrationKind.Self, RelationProvenance.Exact),
            1 => (typeArguments[0], typeArguments[0], RegistrationKind.Self, RelationProvenance.Exact),
            _ => (typeArguments[0], typeArguments[1], RegistrationKind.ImplementationType, RelationProvenance.Exact),
        };

    private static (ITypeSymbol? Service, ITypeSymbol? Implementation, RegistrationKind Kind, RelationProvenance Provenance)
        FromSingleTypeArgument(
            ITypeSymbol service,
            SeparatedSyntaxList<ArgumentSyntax> arguments,
            SemanticModel model,
            CancellationToken cancellationToken)
    {
        if (arguments.Count == 0)
        {
            // AddScoped<Clock>(): the service is its own implementation.
            return (service, service, RegistrationKind.Self, RelationProvenance.Exact);
        }

        var argument = arguments[0].Expression;

        if (argument is LambdaExpressionSyntax lambda)
        {
            // Always inferred: the factory is read, never run, so what it returned here is
            // one path through it rather than the whole truth.
            return (service, FactoryResult(lambda, model, cancellationToken),
                RegistrationKind.Factory, RelationProvenance.Inferred);
        }

        // AddSingleton<IClock>(new SystemClock()): the argument's type is the implementation,
        // and the compiler knows it exactly.
        var instance = model.GetTypeInfo(argument, cancellationToken).Type;
        return (service, instance, RegistrationKind.Instance,
            instance is null ? RelationProvenance.Inferred : RelationProvenance.Exact);
    }

    private static (ITypeSymbol? Service, ITypeSymbol? Implementation, RegistrationKind Kind, RelationProvenance Provenance)
        FromArguments(
            SeparatedSyntaxList<ArgumentSyntax> arguments,
            SemanticModel model,
            CancellationToken cancellationToken)
    {
        if (arguments.Count == 0)
        {
            return (null, null, RegistrationKind.Self, RelationProvenance.Exact);
        }

        // AddScoped(typeof(IFoo<>), typeof(Foo<>)) — the open-generic form.
        if (TypeOfOperand(arguments[0].Expression, model, cancellationToken) is { } service)
        {
            var implementation = arguments.Count > 1
                ? TypeOfOperand(arguments[1].Expression, model, cancellationToken)
                : service;

            return (service, implementation,
                arguments.Count > 1 ? RegistrationKind.ImplementationType : RegistrationKind.Self,
                RelationProvenance.Exact);
        }

        // AddSingleton(instance): the service is whatever the argument is.
        var instance = model.GetTypeInfo(arguments[0].Expression, cancellationToken).Type;
        return instance is null
            ? (null, null, RegistrationKind.Instance, RelationProvenance.Inferred)
            : (instance, instance, RegistrationKind.Instance, RelationProvenance.Exact);
    }

    private static ITypeSymbol? TypeOfOperand(
        ExpressionSyntax expression,
        SemanticModel model,
        CancellationToken cancellationToken) =>
        expression is TypeOfExpressionSyntax typeOf
            ? model.GetTypeInfo(typeOf.Type, cancellationToken).Type
            : null;

    /// <summary>
    /// What a factory lambda hands back, when its body is a single object creation. Anything
    /// that branches or delegates is left unknown rather than guessed at.
    /// </summary>
    private static ITypeSymbol? FactoryResult(
        LambdaExpressionSyntax lambda,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        var body = lambda.Body switch
        {
            ExpressionSyntax expression => expression,
            BlockSyntax { Statements: [ReturnStatementSyntax { Expression: { } returned }] } => returned,
            _ => null,
        };

        return body is BaseObjectCreationExpressionSyntax
            ? model.GetTypeInfo(body, cancellationToken).Type
            : null;
    }

    private static (string? Path, int? Line) LocationOf(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        return span.Path is { Length: > 0 } path ? (path, span.StartLinePosition.Line + 1) : (null, null);
    }
}
