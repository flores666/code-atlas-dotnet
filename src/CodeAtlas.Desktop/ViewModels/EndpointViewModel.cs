using CodeAtlas.Core.Model;

namespace CodeAtlas.Desktop.ViewModels;

/// <summary>One row of the endpoints list, and the subject of the details panel.</summary>
public sealed class EndpointViewModel(HttpEndpoint endpoint)
{
    public HttpEndpoint Endpoint { get; } = endpoint;

    public string HttpMethod { get; } = endpoint.HttpMethod;

    public string Route { get; } = endpoint.Route;

    public string Handler { get; } = endpoint.HandlerDisplay;

    /// <summary>Source position, which is what tells two same-named handlers apart.</summary>
    public string Origin { get; } = SymbolGlyph.Origin(endpoint.FilePath, endpoint.Line);

    public string Authorization { get; } = endpoint.AuthorizationSummary;

    public bool HasAuthorization => Authorization.Length > 0;

    /// <summary>True for the anonymous case, which is the one worth reading as an exception.</summary>
    public bool IsAnonymous { get; } = endpoint.AllowsAnonymous;

    // The verb decides the badge's colour. Four booleans rather than a converter, because
    // a style class is what the theme keys off and this is the whole of the mapping.
    public bool IsGet { get; } = endpoint.HttpMethod is "GET" or "HEAD";

    public bool IsPost { get; } = endpoint.HttpMethod is "POST";

    public bool IsPut { get; } = endpoint.HttpMethod is "PUT" or "PATCH";

    public bool IsDelete { get; } = endpoint.HttpMethod is "DELETE";

    /// <summary>What the index knows about this endpoint, for the overview tab.</summary>
    public IReadOnlyList<DetailRow> Details { get; } =
    [
        new("Method", endpoint.HttpMethod),
        new("Route", endpoint.Route),
        new("Handler", endpoint.HandlerFullyQualifiedName ?? "inline handler"),
        new("Kind", endpoint.Kind == EndpointKind.ControllerAction ? "Controller action" : "Minimal API"),
        new("Project", endpoint.ProjectName ?? "-"),
        new("Source", SymbolGlyph.Origin(endpoint.FilePath, endpoint.Line) is { Length: > 0 } origin
            ? origin
            : "not located"),
        new("Authorization", endpoint.AuthorizationSummary is { Length: > 0 } authorization
            ? authorization
            : "none declared"),
        new("Routing", endpoint.Provenance == RelationProvenance.Exact
            ? "resolved exactly"
            : "partly inferred"),
    ];
}

/// <summary>One labelled fact, as the overview tab lists them.</summary>
public sealed record DetailRow(string Label, string Value);
