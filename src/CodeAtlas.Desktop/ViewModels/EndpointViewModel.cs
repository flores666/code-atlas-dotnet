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

    /// <summary>False for a handler with no declaration to open, such as an inline lambda.</summary>
    public bool CanExplore => endpoint.HandlerSymbolId is not null;
}
