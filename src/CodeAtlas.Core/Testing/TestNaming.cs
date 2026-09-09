using CodeAtlas.Core.Model;

namespace CodeAtlas.Core.Testing;

/// <summary>
/// The heuristics that tie a fixture to the type it is presumably about, when the
/// compiler recorded no edge between them.
/// </summary>
/// <remarks>
/// Everything here reads names and layout, so everything here yields
/// <see cref="TestConfidence.Probable"/>. It is kept apart from the queries for exactly
/// that reason: the exact tiers are SQL over recorded edges, and this is the guesswork,
/// in one place where it can be read and judged.
/// </remarks>
public static class TestNaming
{
    /// <summary>
    /// What a fixture's name is allowed to carry over the type it covers. Ordered longest
    /// first so <c>UserServiceUnitTests</c> strips to <c>UserService</c> rather than to
    /// <c>UserServiceUnit</c>.
    /// </summary>
    private static readonly string[] Suffixes =
    [
        "IntegrationTests", "AcceptanceTests", "UnitTests", "TestFixture", "Specification",
        "Tests", "Specs", "Spec", "Test", "Should", "Fixture",
    ];

    private static readonly string[] Prefixes = ["Tests", "Test", "When"];

    /// <summary>Namespace segments that exist only to separate tests from what they test.</summary>
    private static readonly string[] TestSegments =
    [
        "Tests", "Test", "UnitTests", "IntegrationTests", "AcceptanceTests", "Specs", "Spec",
    ];

    /// <summary>
    /// Ties a fixture to a type, or returns <c>null</c> when nothing about it does.
    /// </summary>
    /// <param name="dependsOnType">
    /// Whether the fixture's project can see the type's at build time. It does not create
    /// a relationship on its own — every test in a referencing project would qualify —
    /// but it is what separates a name that means something from one that coincides, and
    /// it is required outright for the weakest signal.
    /// </param>
    public static TestRelationStrategy? Match(IndexedSymbol fixture, IndexedSymbol type, bool dependsOnType)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(type);

        if (NamesType(fixture.Name, type.Name) || NamesFile(fixture.FilePath, type.Name))
        {
            return dependsOnType
                ? TestRelationStrategy.ProjectDependency
                : TestRelationStrategy.NamingConvention;
        }

        return dependsOnType && (SharesNamespace(fixture.Namespace, type.Namespace) ||
                                 SharesDirectory(fixture.FilePath, type.FilePath))
            ? TestRelationStrategy.NamespaceSimilarity
            : null;
    }

    /// <summary>True when a fixture name is a type's name wearing a test affix.</summary>
    public static bool NamesType(string fixtureName, string typeName)
    {
        ArgumentNullException.ThrowIfNull(fixtureName);
        ArgumentNullException.ThrowIfNull(typeName);

        if (typeName.Length == 0)
        {
            return false;
        }

        foreach (var suffix in Suffixes)
        {
            if (fixtureName.Length > suffix.Length &&
                fixtureName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                Matches(fixtureName[..^suffix.Length], typeName))
            {
                return true;
            }
        }

        foreach (var prefix in Prefixes)
        {
            if (fixtureName.Length > prefix.Length &&
                fixtureName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                Matches(fixtureName[prefix.Length..], typeName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The same rule read off the file rather than the type, which catches a fixture whose
    /// name says what it asserts rather than what it covers.
    /// </summary>
    private static bool NamesFile(string? fixturePath, string typeName) =>
        fixturePath is { Length: > 0 } path &&
        NamesType(Path.GetFileNameWithoutExtension(path), typeName);

    /// <summary>
    /// True when two namespaces are the same once the segments that only mark a namespace
    /// as belonging to tests are dropped, so <c>Shop.Billing.Tests</c> meets
    /// <c>Shop.Billing</c> and <c>Shop.Tests.Billing</c> does too.
    /// </summary>
    public static bool SharesNamespace(string? fixtureNamespace, string? typeNamespace)
    {
        var type = Normalise(typeNamespace);

        return type.Length > 0 && Normalise(fixtureNamespace).SequenceEqual(type, StringComparer.Ordinal);
    }

    /// <summary>A fixture written in the same directory as the type, which some repositories do.</summary>
    private static bool SharesDirectory(string? fixturePath, string? typePath) =>
        fixturePath is { Length: > 0 } fixture && typePath is { Length: > 0 } type &&
        Path.GetDirectoryName(fixture) is { Length: > 0 } directory &&
        string.Equals(directory, Path.GetDirectoryName(type), StringComparison.Ordinal);

    private static string[] Normalise(string? @namespace) => @namespace is null
        ? []
        : [.. @namespace
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => !TestSegments.Contains(segment, StringComparer.OrdinalIgnoreCase))];

    /// <summary>Compares a stripped fixture name, tolerating the separator a strip leaves behind.</summary>
    private static bool Matches(string stripped, string typeName) =>
        stripped.Trim('_').Equals(typeName, StringComparison.OrdinalIgnoreCase);
}
