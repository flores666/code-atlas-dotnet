namespace CodeAtlas.Core.Model;

/// <summary>How long the container keeps one instance of a service.</summary>
public enum ServiceLifetime
{
    Singleton,
    Scoped,
    Transient,
}

/// <summary>The shape of a registration, which is what decides how much of it is knowable.</summary>
public enum RegistrationKind
{
    /// <summary>An implementation type named directly, e.g. <c>AddScoped&lt;IClock, Clock&gt;()</c>.</summary>
    ImplementationType,

    /// <summary>A type registered as itself, e.g. <c>AddScoped&lt;Clock&gt;()</c>.</summary>
    Self,

    /// <summary>A lambda builds the instance. What it returns is recovered, never executed.</summary>
    Factory,

    /// <summary>An already-built object is handed to the container.</summary>
    Instance,
}

/// <summary>
/// One <c>IServiceCollection</c> registration found in source.
/// </summary>
/// <remarks>
/// A service may be registered many times; each call site is its own row. The
/// implementation is unknown for a factory whose return type could not be recovered,
/// which is why <see cref="ImplementationFullyQualifiedName"/> is nullable.
/// </remarks>
public sealed record ServiceRegistration
{
    public long Id { get; init; }

    public required string ServiceFullyQualifiedName { get; init; }

    public required string ServiceDisplay { get; init; }

    /// <summary>Row id of the service type when it is declared in the solution.</summary>
    public long? ServiceSymbolId { get; init; }

    public string? ImplementationFullyQualifiedName { get; init; }

    public string? ImplementationDisplay { get; init; }

    public long? ImplementationSymbolId { get; init; }

    public required ServiceLifetime Lifetime { get; init; }

    public required RegistrationKind Kind { get; init; }

    /// <summary>
    /// <see cref="RelationProvenance.Exact"/> when the compiler named the implementation —
    /// a type argument, a <c>typeof</c>, or the type of an instance expression.
    /// <see cref="RelationProvenance.Inferred"/> when it was read out of a factory body,
    /// which CodeAtlas does not execute.
    /// </summary>
    public RelationProvenance Provenance { get; init; } = RelationProvenance.Exact;

    public string? FilePath { get; init; }

    public int? Line { get; init; }

    /// <summary>The method the registration call sits in, e.g. an <c>AddInfrastructure</c> extension.</summary>
    public string? DeclaringMember { get; init; }

    public bool HasImplementation => ImplementationFullyQualifiedName is not null;
}
