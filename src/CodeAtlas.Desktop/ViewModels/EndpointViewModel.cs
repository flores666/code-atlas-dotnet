using CodeAtlas.Core.Model;

namespace CodeAtlas.Desktop.ViewModels;

/// <summary>One row of the endpoints list.</summary>
public sealed class EndpointViewModel(HttpEndpoint endpoint)
{
    public HttpEndpoint Endpoint { get; } = endpoint;

    public string HttpMethod { get; } = endpoint.HttpMethod;

    public string Route { get; } = endpoint.Route;

    public string Handler { get; } = endpoint.HandlerDisplay;

    /// <summary>Source position, which is what tells two same-named handlers apart.</summary>
    public string Origin { get; } = endpoint.FilePath is { } path
        ? $"· {System.IO.Path.GetFileName(path)}:{endpoint.Line?.ToString() ?? "?"}"
        : string.Empty;

    public string Authorization { get; } = endpoint.AuthorizationSummary;

    public bool HasAuthorization => Authorization.Length > 0;

    /// <summary>True for the anonymous case, which is the one worth reading as an exception.</summary>
    public bool IsAnonymous { get; } = endpoint.AllowsAnonymous;

    /// <summary>
    /// True when the registration was located in source. An endpoint whose flow cannot be
    /// mapped is still worth opening, so this is what keeps that door open.
    /// </summary>
    public bool HasSource => endpoint.FilePath is { Length: > 0 };
}
