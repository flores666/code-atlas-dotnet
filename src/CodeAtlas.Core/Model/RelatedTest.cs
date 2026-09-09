namespace CodeAtlas.Core.Model;

/// <summary>The test frameworks CodeAtlas recognises, identified by the attributes they declare.</summary>
public enum TestFramework
{
    XUnit,
    NUnit,
    MSTest,
}

/// <summary>
/// How a test was tied to a production symbol, in descending order of confidence.
/// </summary>
/// <remarks>
/// The first three are compiler-derived: the index holds an edge the compiler resolved
/// from the test, or from the fixture that carries it, to the symbol itself. The rest are
/// read off names and project structure, and are never presented as exact.
/// </remarks>
public enum TestRelationStrategy
{
    /// <summary>The test method itself calls or names the symbol.</summary>
    DirectReference,

    /// <summary>The test, or its fixture, constructs the type that declares the symbol.</summary>
    ClassUnderTest,

    /// <summary>Another member of the fixture — a constructor, a setup method, a field — names the symbol.</summary>
    MemberReference,

    /// <summary>
    /// The fixture is named after the declaring type, and its project depends on the
    /// project that declares it. A build-time dependency is what separates a name that
    /// means something from one that merely coincides.
    /// </summary>
    ProjectDependency,

    /// <summary>The fixture is named after the declaring type, with nothing corroborating it.</summary>
    NamingConvention,

    /// <summary>
    /// The fixture sits in the declaring type's namespace, or beside its file, inside a
    /// project that depends on it. The weakest signal there is, and a fallback: it is
    /// only reported for a symbol nothing else reached.
    /// </summary>
    NamespaceSimilarity,
}

/// <summary>
/// How much a relationship is worth. <see cref="Exact"/> means the compiler recorded the
/// edge; a naming or layout heuristic is always <see cref="Probable"/>.
/// </summary>
public enum TestConfidence
{
    Exact,
    Probable,
}

/// <summary>One test method found in the index.</summary>
public sealed record TestMethod
{
    public required long SymbolId { get; init; }

    /// <summary>The method as written, e.g. <c>Get_returns_the_user(int id)</c>.</summary>
    public required string Display { get; init; }

    public required string FullyQualifiedName { get; init; }

    public required TestFramework Framework { get; init; }

    /// <summary>The fixture declaring it. Every test method has one; C# has no free functions.</summary>
    public string? ClassDisplay { get; init; }

    public long? ClassSymbolId { get; init; }

    public string? ProjectName { get; init; }

    public string? FilePath { get; init; }

    public int? Line { get; init; }

    /// <summary>Fixture and method together, which is how a test is named in a runner.</summary>
    public string QualifiedDisplay => ClassDisplay is { Length: > 0 } fixture
        ? $"{fixture}.{Display}"
        : Display;
}

/// <summary>
/// A test that exercises a symbol, and what made CodeAtlas say so.
/// </summary>
/// <param name="Subject">
/// What the evidence is actually about: the symbol itself for a direct reference, and the
/// type that declares it for everything found through the fixture.
/// </param>
public sealed record RelatedTest(TestMethod Test, TestRelationStrategy Strategy, string Subject)
{
    public TestConfidence Confidence => Strategy switch
    {
        TestRelationStrategy.DirectReference
            or TestRelationStrategy.ClassUnderTest
            or TestRelationStrategy.MemberReference => TestConfidence.Exact,
        _ => TestConfidence.Probable,
    };

    public bool IsExact => Confidence == TestConfidence.Exact;

    /// <summary>Why this test is listed, phrased so a reader can judge it themselves.</summary>
    public string Reason => Strategy switch
    {
        TestRelationStrategy.DirectReference => $"calls {Subject}",
        TestRelationStrategy.ClassUnderTest => $"constructs {Subject}",
        TestRelationStrategy.MemberReference => $"its fixture uses {Subject}",
        TestRelationStrategy.ProjectDependency => $"named after {Subject}, in a project that depends on it",
        TestRelationStrategy.NamingConvention => $"named after {Subject}",
        _ => $"declared alongside {Subject}",
    };
}
