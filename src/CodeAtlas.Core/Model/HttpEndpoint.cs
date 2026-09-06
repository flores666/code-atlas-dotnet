namespace CodeAtlas.Core.Model;

public enum EndpointKind
{
    /// <summary>An MVC or API controller action.</summary>
    ControllerAction,

    /// <summary>A Minimal API <c>MapGet</c>-style registration.</summary>
    MinimalApi,
}

/// <summary>
/// One HTTP entry point into the application.
/// </summary>
/// <remarks>
/// Only statically resolvable routing is captured: attribute routing on controllers, and
/// Minimal API registrations whose route is a literal. Conventional routing depends on the
/// route table assembled at start-up and is deliberately not guessed at.
/// </remarks>
public sealed record HttpEndpoint
{
    public long Id { get; init; }

    /// <summary>Upper-case verb, or <c>ANY</c> when the registration accepts all of them.</summary>
    public required string HttpMethod { get; init; }

    /// <summary>The combined template, with <c>[controller]</c> and <c>[action]</c> substituted.</summary>
    public required string Route { get; init; }

    /// <summary>Readable handler, e.g. <c>UsersController.Get</c>.</summary>
    public required string HandlerDisplay { get; init; }

    public string? HandlerFullyQualifiedName { get; init; }

    /// <summary>Row id of the handler method; <c>null</c> for an inline lambda.</summary>
    public long? HandlerSymbolId { get; init; }

    /// <summary>The type declaring the handler, which is what carries its injected dependencies.</summary>
    public string? DeclaringTypeFullyQualifiedName { get; init; }

    public long? DeclaringTypeSymbolId { get; init; }

    public required EndpointKind Kind { get; init; }

    public string? ProjectName { get; init; }

    public string? FilePath { get; init; }

    public int? Line { get; init; }

    public bool RequiresAuthorization { get; init; }

    /// <summary>True when <c>[AllowAnonymous]</c> opts this endpoint back out of authorization.</summary>
    public bool AllowsAnonymous { get; init; }

    public IReadOnlyList<string> Policies { get; init; } = [];

    public IReadOnlyList<string> Roles { get; init; } = [];

    /// <summary>Inferred when part of the route or handler could not be resolved exactly.</summary>
    public RelationProvenance Provenance { get; init; } = RelationProvenance.Exact;

    /// <summary>What the authorization metadata amounts to, for display.</summary>
    public string AuthorizationSummary => (RequiresAuthorization, AllowsAnonymous) switch
    {
        (_, true) => "Anonymous",
        (true, _) when Policies.Count > 0 => string.Join(", ", Policies),
        (true, _) when Roles.Count > 0 => string.Join(", ", Roles),
        (true, _) => "Authorized",
        _ => string.Empty,
    };
}
