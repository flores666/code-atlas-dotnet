using CodeAtlas.Core.Model;
using CodeAtlas.Desktop.ViewModels;

namespace CodeAtlas.Desktop.Tests;

/// <summary>
/// How the graph section answers a click in the endpoints list. The walk itself is
/// covered against a real index in CodeAtlas.Core.Tests; what matters here is that a
/// selection always reframes the section on something the reader can see.
/// </summary>
public class GraphViewModelTests
{
    private static readonly IndexedSymbol Handler = new()
    {
        Id = 42,
        Kind = IndexedSymbolKind.Method,
        Name = "GetAsync",
        Display = "GetAsync(Guid id)",
        FullyQualifiedName = "Shop.Api.UsersController.GetAsync(System.Guid)",
    };

    private static HttpEndpoint Endpoint(long? handlerId, long? declaringId, EndpointKind kind) => new()
    {
        HttpMethod = "GET",
        Route = "/api/users/{id}",
        HandlerDisplay = handlerId is null ? "inline handler" : "UsersController.GetAsync",
        HandlerSymbolId = handlerId,
        DeclaringTypeSymbolId = declaringId,
        Kind = kind,
    };

    [Fact]
    public void Frames_a_controller_endpoint_on_its_handler()
    {
        var graph = new GraphViewModel();

        graph.FocusEndpoint(Endpoint(handlerId: 42, declaringId: 7, EndpointKind.ControllerAction), Handler);

        Assert.True(graph.HasRoot);
        Assert.False(graph.HasEndpointNotice);

        // The route is what the reader picked, so it titles the section rather than the
        // method they never named.
        Assert.Equal("GET /api/users/{id}", graph.RootTitle);
        Assert.Contains(Handler.FullyQualifiedName, graph.RootSubtitle);
    }

    /// <summary>
    /// A flow asks a narrower question than the default neighbourhood: how the endpoint is
    /// wired and what it reaches, deep enough to land on implementations rather than on
    /// the interfaces one hop before them.
    /// </summary>
    [Fact]
    public void Narrows_the_filters_and_depth_to_the_flow()
    {
        var graph = new GraphViewModel();

        graph.FocusEndpoint(Endpoint(handlerId: 42, declaringId: 7, EndpointKind.ControllerAction), Handler);

        Assert.Equal(GraphOptions.EndpointFlowDepth, graph.Depth);

        Assert.True(graph.ShowCalls);
        Assert.True(graph.ShowComposition);
        Assert.True(graph.ShowImplementations);
        Assert.True(graph.ShowDatabase);
        Assert.True(graph.ShowConfiguration);
        Assert.True(graph.ShowExternalServices);

        // These answer a different question and would bury the flow.
        Assert.False(graph.ShowReferences);
        Assert.False(graph.ShowTypeDependencies);
        Assert.False(graph.ShowInheritance);
    }

    /// <summary>
    /// An inline Minimal API handler is declared nowhere, so its flow is rooted at the
    /// first thing the lambda reaches instead of at a handler symbol. It is still the
    /// endpoint the reader picked, so the route still titles the section.
    /// </summary>
    [Fact]
    public void Frames_an_inline_endpoint_on_what_its_handler_reaches()
    {
        var graph = new GraphViewModel();
        var service = Handler with { Id = 9, Kind = IndexedSymbolKind.Interface, Display = "IUserService" };

        graph.FocusEndpoint(
            Endpoint(handlerId: null, declaringId: null, EndpointKind.MinimalApi),
            service,
            seeds: [11, 12]);

        Assert.True(graph.HasRoot);
        Assert.False(graph.HasEndpointNotice);
        Assert.Equal("GET /api/users/{id}", graph.RootTitle);
        Assert.Equal(GraphOptions.EndpointFlowDepth, graph.Depth);
    }

    /// <summary>
    /// The endpoint is real, but nothing it reaches is indexed. Selecting it has to say
    /// so where the graph would have been.
    /// </summary>
    [Fact]
    public void Explains_an_endpoint_whose_handler_has_no_declaration()
    {
        var graph = new GraphViewModel();

        graph.FocusEndpoint(Endpoint(handlerId: null, declaringId: null, EndpointKind.MinimalApi), root: null);

        Assert.True(graph.HasEndpointNotice);
        Assert.False(string.IsNullOrWhiteSpace(graph.EndpointNotice));

        // Reframed, not ignored: the section names the endpoint that was picked.
        Assert.Equal("GET /api/users/{id}", graph.RootTitle);
        Assert.False(graph.HasRoot);
        Assert.Empty(graph.Nodes);
    }

    [Fact]
    public void Clears_the_notice_once_something_can_be_mapped()
    {
        var graph = new GraphViewModel();
        graph.FocusEndpoint(Endpoint(handlerId: null, declaringId: null, EndpointKind.MinimalApi), root: null);

        graph.FocusEndpoint(Endpoint(handlerId: 42, declaringId: 7, EndpointKind.ControllerAction), Handler);

        Assert.False(graph.HasEndpointNotice);
        Assert.Null(graph.EndpointNotice);
    }
}
