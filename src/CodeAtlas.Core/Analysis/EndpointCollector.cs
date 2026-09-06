using CodeAtlas.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeAtlas.Core.Analysis;

/// <summary>
/// Finds the HTTP entry points of an ASP.NET Core application.
/// </summary>
/// <remarks>
/// <para>
/// Two shapes are recognised, and both are read from what the compiler resolved.
/// Controller actions come from the type walk, because their routing and authorization
/// live in attributes on symbols. Minimal API endpoints come from the document walk,
/// because they are calls.
/// </para>
/// <para>
/// Only statically resolvable routing is captured. Conventional routing depends on the
/// route table assembled at start-up, and a Minimal API route built at run time is not a
/// literal; neither is guessed at, so an endpoint that is listed is one that exists.
/// </para>
/// </remarks>
public sealed class EndpointCollector
{
    private const string Mvc = "Microsoft.AspNetCore.Mvc";

    /// <summary>How far the walk chases a route group through locals before giving up.</summary>
    private const int MaxPrefixDepth = 8;

    private static readonly string[] ControllerBaseTypes =
        [$"{Mvc}.ControllerBase", $"{Mvc}.Controller"];

    /// <summary>Minimal API map methods, and the verb each one registers.</summary>
    private static readonly Dictionary<string, string> MapMethods = new(StringComparer.Ordinal)
    {
        ["MapGet"] = "GET",
        ["MapPost"] = "POST",
        ["MapPut"] = "PUT",
        ["MapPatch"] = "PATCH",
        ["MapDelete"] = "DELETE",
        ["MapMethods"] = "ANY",
    };

    private readonly List<HttpEndpoint> _endpoints = [];
    private readonly string? _projectName;

    public EndpointCollector(string? projectName) => _projectName = projectName;

    public IReadOnlyList<HttpEndpoint> Endpoints => _endpoints;

    // ---- controllers --------------------------------------------------------

    public void VisitType(INamedTypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (!IsController(type))
        {
            return;
        }

        var prefixes = RouteTemplates(type, type, action: null);
        var controllerAuthorization = ReadAuthorization(type);

        foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
        {
            if (!IsAction(method))
            {
                continue;
            }

            var authorization = controllerAuthorization.Combine(ReadAuthorization(method));
            var (path, line) = LocationOf(method);

            foreach (var (verb, template) in HttpMappings(method))
            {
                var suffixes = template is null
                    ? RouteTemplates(method, type, method)
                    : [SubstituteTokens(template, type, method)];

                foreach (var prefix in prefixes.Count > 0 ? prefixes : [string.Empty])
                {
                    foreach (var suffix in suffixes.Count > 0 ? suffixes : [string.Empty])
                    {
                        _endpoints.Add(new HttpEndpoint
                        {
                            HttpMethod = verb,
                            Route = Combine(prefix, suffix),
                            HandlerDisplay = $"{type.Name}.{method.Name}",
                            HandlerFullyQualifiedName = SymbolNaming.FullyQualifiedName(method),
                            DeclaringTypeFullyQualifiedName = SymbolNaming.FullyQualifiedName(type),
                            Kind = EndpointKind.ControllerAction,
                            ProjectName = _projectName,
                            FilePath = path,
                            Line = line,
                            RequiresAuthorization = authorization.Required,
                            AllowsAnonymous = authorization.Anonymous,
                            Policies = authorization.Policies,
                            Roles = authorization.Roles,
                        });
                    }
                }
            }
        }
    }

    private static bool IsController(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Class || type.IsAbstract)
        {
            return false;
        }

        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (ControllerBaseTypes.Contains(SymbolNaming.FullyQualifiedName(current), StringComparer.Ordinal))
            {
                return true;
            }
        }

        // A POCO controller: no framework base type, but the attribute declares intent.
        return HasAttribute(type, $"{Mvc}.ApiControllerAttribute");
    }

    private static bool IsAction(IMethodSymbol method) =>
        method is
        {
            MethodKind: MethodKind.Ordinary,
            DeclaredAccessibility: Accessibility.Public,
            IsStatic: false,
            IsImplicitlyDeclared: false,
        }
        && !HasAttribute(method, $"{Mvc}.NonActionAttribute");

    /// <summary>
    /// The verb and template of each <c>[HttpGet]</c>-style attribute. One method may carry
    /// several, and each is its own endpoint.
    /// </summary>
    private static List<(string Verb, string? Template)> HttpMappings(IMethodSymbol method)
    {
        var mappings = new List<(string, string?)>();

        foreach (var attribute in method.GetAttributes())
        {
            if (attribute.AttributeClass is not { } attributeClass ||
                SymbolNaming.NamespaceOf(attributeClass) != Mvc)
            {
                continue;
            }

            var name = attributeClass.Name;
            if (name.StartsWith("Http", StringComparison.Ordinal) &&
                name.EndsWith("Attribute", StringComparison.Ordinal))
            {
                var verb = name["Http".Length..^"Attribute".Length].ToUpperInvariant();
                mappings.Add((verb, FirstStringArgument(attribute)));
            }
            else if (name == "RouteAttribute" && mappings.Count == 0)
            {
                // A bare [Route] on an action accepts every verb.
                mappings.Add(("ANY", FirstStringArgument(attribute)));
            }
        }

        return mappings;
    }

    /// <summary>Every <c>[Route]</c> template on a symbol, with its tokens substituted.</summary>
    private static List<string> RouteTemplates(ISymbol symbol, INamedTypeSymbol controller, IMethodSymbol? action) =>
        symbol.GetAttributes()
            .Where(attribute => attribute.AttributeClass is { Name: "RouteAttribute" } routeClass &&
                                SymbolNaming.NamespaceOf(routeClass) == Mvc)
            .Select(FirstStringArgument)
            .OfType<string>()
            .Select(template => SubstituteTokens(template, controller, action))
            .ToList();

    /// <summary>
    /// Replaces the routing tokens ASP.NET expands at start-up. <c>[controller]</c> is the
    /// type name without its suffix, which is the convention the framework itself applies.
    /// </summary>
    private static string SubstituteTokens(string template, INamedTypeSymbol controller, IMethodSymbol? action)
    {
        var name = controller.Name.EndsWith("Controller", StringComparison.Ordinal)
            ? controller.Name[..^"Controller".Length]
            : controller.Name;

        var result = template.Replace("[controller]", name, StringComparison.OrdinalIgnoreCase);

        if (action is not null)
        {
            result = result.Replace("[action]", action.Name, StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    /// <summary>
    /// Joins a controller prefix with an action template. An action template rooted with
    /// <c>/</c> or <c>~/</c> replaces the prefix outright, as it does at run time.
    /// </summary>
    private static string Combine(string prefix, string suffix)
    {
        if (suffix.StartsWith('/') || suffix.StartsWith("~/", StringComparison.Ordinal))
        {
            return Normalise(suffix.TrimStart('~'));
        }

        return Normalise(prefix.Length == 0 ? suffix : $"{prefix.TrimEnd('/')}/{suffix}");
    }

    private static string Normalise(string route) => "/" + route.Trim('/');

    // ---- minimal API --------------------------------------------------------

    public void VisitDocument(SyntaxNode root, SemanticModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(model);

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (verb, mapped) = MapCall(invocation, model, cancellationToken);
            if (mapped is null)
            {
                continue;
            }

            if (LiteralRoute(invocation, model, cancellationToken) is not { } route)
            {
                // A route assembled at run time is not statically resolvable.
                continue;
            }

            var handler = HandlerOf(invocation, model, cancellationToken);
            var (path, line) = LocationOf(invocation);

            _endpoints.Add(new HttpEndpoint
            {
                HttpMethod = verb,
                Route = Normalise($"{Prefix(invocation, model, cancellationToken)}/{route.Trim('/')}"),
                HandlerDisplay = handler is null ? "inline handler" : SymbolNaming.Display(handler),
                HandlerFullyQualifiedName = handler is null ? null : SymbolNaming.FullyQualifiedName(handler),
                DeclaringTypeFullyQualifiedName = handler?.ContainingType is { } container
                    ? SymbolNaming.FullyQualifiedName(container)
                    : null,
                Kind = EndpointKind.MinimalApi,
                ProjectName = _projectName,
                FilePath = path,
                Line = line,
                RequiresAuthorization = RequiresAuthorization(invocation, out var policies),
                AllowsAnonymous = AllowsAnonymous(invocation),
                Policies = policies,

                // A lambda handler has no symbol to follow, so the endpoint is real but its
                // flow cannot be traced any further.
                Provenance = handler is null ? RelationProvenance.Inferred : RelationProvenance.Exact,
            });
        }
    }

    private static (string Verb, IMethodSymbol? Method) MapCall(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        if (model.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method ||
            !MapMethods.TryGetValue(method.Name, out var verb))
        {
            return (string.Empty, null);
        }

        // Anchored to the routing abstraction, so an unrelated MapGet is not mistaken for one.
        var definition = (method.ReducedFrom ?? method).OriginalDefinition;
        var isRouting = definition.Parameters.Length > 0 &&
                        SymbolNaming.FullyQualifiedName(definition.Parameters[0].Type)
                            .StartsWith("Microsoft.AspNetCore.Routing.IEndpointRoute", StringComparison.Ordinal);

        return isRouting ? (verb, method) : (string.Empty, null);
    }

    private static string? LiteralRoute(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        CancellationToken cancellationToken) =>
        invocation.ArgumentList.Arguments.Count > 0
            ? ConstantString(invocation.ArgumentList.Arguments[0].Expression, model, cancellationToken)
            : null;

    /// <summary>
    /// The accumulated <c>MapGroup</c> prefixes this call sits under. Groups nest, so the
    /// receiver chain is walked rather than only its nearest link.
    /// </summary>
    private static string Prefix(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        CancellationToken cancellationToken,
        int depth = 0)
    {
        // Groups nest, and a group is usually held in a local before it is mapped onto, so
        // the walk follows both the receiver chain and one level of local at a time. The
        // depth guard keeps a cyclic or pathological chain from running away.
        var receiver = Receiver(invocation.Expression);

        while (depth < MaxPrefixDepth)
        {
            switch (receiver)
            {
                case InvocationExpressionSyntax call
                    when model.GetSymbolInfo(call, cancellationToken).Symbol is IMethodSymbol { Name: "MapGroup" } &&
                         LiteralRoute(call, model, cancellationToken) is { } group:
                    return Prefix(call, model, cancellationToken, depth + 1) + "/" + group.Trim('/');

                case InvocationExpressionSyntax call:
                    receiver = Receiver(call.Expression);
                    break;

                case IdentifierNameSyntax identifier
                    when LocalInitializer(identifier, model, cancellationToken) is { } initializer:
                    receiver = initializer;
                    depth++;
                    break;

                default:
                    return string.Empty;
            }
        }

        return string.Empty;
    }

    private static ExpressionSyntax? Receiver(ExpressionSyntax expression) =>
        (expression as MemberAccessExpressionSyntax)?.Expression;

    /// <summary>
    /// What a local was assigned, when it was assigned once at its declaration. A local
    /// reassigned elsewhere is not statically resolvable and is left alone.
    /// </summary>
    private static ExpressionSyntax? LocalInitializer(
        IdentifierNameSyntax identifier,
        SemanticModel model,
        CancellationToken cancellationToken) =>
        model.GetSymbolInfo(identifier, cancellationToken).Symbol is ILocalSymbol local &&
        local.DeclaringSyntaxReferences is [var reference] &&
        reference.GetSyntax(cancellationToken) is VariableDeclaratorSyntax { Initializer.Value: { } value }
            ? value
            : null;

    private static IMethodSymbol? HandlerOf(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        if (invocation.ArgumentList.Arguments.Count < 2)
        {
            return null;
        }

        // The last argument is the handler; MapMethods takes the verb list in between.
        var handler = invocation.ArgumentList.Arguments[^1].Expression;
        var info = model.GetSymbolInfo(handler, cancellationToken);

        // A method group passed as a delegate binds to a member group rather than to one
        // symbol; a group of exactly one is still an unambiguous handler. A lambda binds to
        // a symbol too, but one that is not declared anywhere and cannot be navigated to.
        return (info.Symbol ?? info.CandidateSymbols.SingleOrDefault()) is IMethodSymbol handlerMethod &&
               handlerMethod.MethodKind is not (MethodKind.LambdaMethod or MethodKind.AnonymousFunction)
            ? handlerMethod
            : null;
    }

    /// <summary>Reads <c>.RequireAuthorization()</c> off the chain the map call is part of.</summary>
    private static bool RequiresAuthorization(InvocationExpressionSyntax invocation, out IReadOnlyList<string> policies)
    {
        var found = new List<string>();
        var required = false;

        foreach (var call in Chain(invocation))
        {
            if (call is not { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "RequireAuthorization" } })
            {
                continue;
            }

            required = true;
            found.AddRange(call.ArgumentList.Arguments
                .Select(argument => argument.Expression)
                .OfType<LiteralExpressionSyntax>()
                .Select(literal => literal.Token.ValueText));
        }

        policies = found;
        return required;
    }

    private static bool AllowsAnonymous(InvocationExpressionSyntax invocation) =>
        Chain(invocation).Any(call =>
            call is { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "AllowAnonymous" } });

    /// <summary>The invocations chained onto a call, e.g. the builder calls after a <c>MapGet</c>.</summary>
    private static IEnumerable<InvocationExpressionSyntax> Chain(InvocationExpressionSyntax invocation)
    {
        for (SyntaxNode? node = invocation.Parent; node is not null; node = node.Parent)
        {
            if (node is InvocationExpressionSyntax call)
            {
                yield return call;
            }
            else if (node is not MemberAccessExpressionSyntax)
            {
                yield break;
            }
        }
    }

    // ---- attributes ---------------------------------------------------------

    private readonly record struct Authorization(
        bool Required,
        bool Anonymous,
        IReadOnlyList<string> Policies,
        IReadOnlyList<string> Roles)
    {
        /// <summary>
        /// Layers an action's metadata over its controller's, which is how ASP.NET itself
        /// treats them: requirements accumulate, and <c>[AllowAnonymous]</c> wins outright.
        /// </summary>
        public Authorization Combine(Authorization other) => new(
            Required || other.Required,
            Anonymous || other.Anonymous,
            [.. Policies.Concat(other.Policies).Distinct(StringComparer.Ordinal)],
            [.. Roles.Concat(other.Roles).Distinct(StringComparer.Ordinal)]);
    }

    private static Authorization ReadAuthorization(ISymbol symbol)
    {
        var required = false;
        var anonymous = false;
        var policies = new List<string>();
        var roles = new List<string>();

        foreach (var attribute in symbol.GetAttributes())
        {
            switch (attribute.AttributeClass?.Name)
            {
                case "AuthorizeAttribute":
                    required = true;

                    // [Authorize("policy")] and [Authorize(Policy = "policy")] mean the same.
                    if (FirstStringArgument(attribute) is { } positional)
                    {
                        policies.Add(positional);
                    }

                    foreach (var (name, value) in attribute.NamedArguments)
                    {
                        if (value.Value is not string text || text.Length == 0)
                        {
                            continue;
                        }

                        switch (name)
                        {
                            case "Policy":
                                policies.Add(text);
                                break;
                            case "Roles":
                                roles.AddRange(text.Split(',', StringSplitOptions.TrimEntries
                                                             | StringSplitOptions.RemoveEmptyEntries));
                                break;
                        }
                    }

                    break;

                case "AllowAnonymousAttribute":
                    anonymous = true;
                    break;
            }
        }

        return new Authorization(
            required,
            anonymous,
            policies.Distinct(StringComparer.Ordinal).ToList(),
            roles.Distinct(StringComparer.Ordinal).ToList());
    }

    private static bool HasAttribute(ISymbol symbol, string fullyQualifiedName) =>
        symbol.GetAttributes().Any(attribute =>
            attribute.AttributeClass is { } attributeClass &&
            SymbolNaming.FullyQualifiedName(attributeClass) == fullyQualifiedName);

    private static string? FirstStringArgument(AttributeData attribute) =>
        attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Value is string value
            ? value
            : null;

    private static string? ConstantString(
        ExpressionSyntax expression,
        SemanticModel model,
        CancellationToken cancellationToken) =>
        model.GetConstantValue(expression, cancellationToken) is { HasValue: true, Value: string value }
            ? value
            : null;

    private static (string? Path, int? Line) LocationOf(ISymbol symbol)
    {
        if (symbol.Locations.FirstOrDefault(location => location.IsInSource) is not { } source)
        {
            return (null, null);
        }

        var span = source.GetLineSpan();
        return (span.Path, span.StartLinePosition.Line + 1);
    }

    private static (string? Path, int? Line) LocationOf(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        return span.Path is { Length: > 0 } path ? (path, span.StartLinePosition.Line + 1) : (null, null);
    }
}
