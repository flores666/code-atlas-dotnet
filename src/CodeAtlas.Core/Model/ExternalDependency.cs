namespace CodeAtlas.Core.Model;

/// <summary>The kind of infrastructure sitting behind a boundary.</summary>
public enum ExternalTechnology
{
    HttpApi,
    Grpc,
    Redis,
    MassTransit,
    RabbitMq,
    Kafka,
    FileSystem,
    ObjectStorage,
    WebDav,
}

/// <summary>How the boundary was found, which is also how much is known about it.</summary>
public enum ExternalBinding
{
    /// <summary>A typed client: <c>AddHttpClient&lt;IClient, Client&gt;()</c>, <c>AddGrpcClient&lt;T&gt;()</c>.</summary>
    TypedClient,

    /// <summary>A named client with no type of its own: <c>AddHttpClient("crm")</c>.</summary>
    NamedClient,

    /// <summary>The container hands the client to a type through its constructor.</summary>
    Injection,

    /// <summary>A call into the client's API.</summary>
    Call,
}

/// <summary>
/// One place the application reaches infrastructure it does not own.
/// </summary>
/// <remarks>
/// Recognition is by the fully qualified name of the client type — or of the registration
/// extension that wires it — never by how a call is spelled. This is deliberately not a
/// complete model of any of these frameworks: the point is to find the boundary and say
/// which code sits on this side of it.
/// </remarks>
public sealed record ExternalDependency
{
    public long Id { get; init; }

    public required ExternalTechnology Technology { get; init; }

    public required ExternalBinding Binding { get; init; }

    /// <summary>The client type at the boundary, usually a framework type outside the solution.</summary>
    public required string ClientFullyQualifiedName { get; init; }

    public required string ClientDisplay { get; init; }

    public long? ClientSymbolId { get; init; }

    /// <summary>A named client, topic, queue or bucket, when it was written as a literal.</summary>
    public string? Name { get; init; }

    /// <summary>The solution type on this side of the boundary.</summary>
    public required string ConsumerFullyQualifiedName { get; init; }

    public required string ConsumerDisplay { get; init; }

    public long? ConsumerSymbolId { get; init; }

    public string? ProjectName { get; init; }

    public string? FilePath { get; init; }

    public int? Line { get; init; }

    public RelationProvenance Provenance { get; init; } = RelationProvenance.Exact;

    public string TechnologyLabel => Technology switch
    {
        ExternalTechnology.HttpApi => "HTTP",
        ExternalTechnology.Grpc => "gRPC",
        ExternalTechnology.Redis => "Redis",
        ExternalTechnology.MassTransit => "MassTransit",
        ExternalTechnology.RabbitMq => "RabbitMQ",
        ExternalTechnology.Kafka => "Kafka",
        ExternalTechnology.FileSystem => "File system",
        ExternalTechnology.ObjectStorage => "Object storage",
        _ => "WebDAV",
    };

    /// <summary>What sits on the far side, named as precisely as the source allows.</summary>
    public string Resource => Name is { Length: > 0 } name
        ? $"{TechnologyLabel} · {name}"
        : TechnologyLabel;
}
