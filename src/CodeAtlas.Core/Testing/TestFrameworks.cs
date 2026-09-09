using CodeAtlas.Core.Model;

namespace CodeAtlas.Core.Testing;

/// <summary>
/// The attributes that make a method a test, per framework.
/// </summary>
/// <remarks>
/// A test is recognised by the fully qualified name the compiler bound its attribute to,
/// which is the same rule the rest of the analysis uses for framework types: a method
/// called <c>Test</c> is not a test, and a <c>[Fact]</c> from somewhere else is not
/// xUnit's. Nothing here needs a collector of its own — attributes are already indexed
/// against every symbol — so a test project is simply a project that declares one of
/// these, and a fixture a type that contains one.
/// </remarks>
public static class TestFrameworks
{
    private static readonly IReadOnlyDictionary<string, TestFramework> ByAttribute =
        new Dictionary<string, TestFramework>(StringComparer.Ordinal)
        {
            ["Xunit.FactAttribute"] = TestFramework.XUnit,
            ["Xunit.TheoryAttribute"] = TestFramework.XUnit,

            ["NUnit.Framework.TestAttribute"] = TestFramework.NUnit,
            ["NUnit.Framework.TestCaseAttribute"] = TestFramework.NUnit,
            ["NUnit.Framework.TestCaseSourceAttribute"] = TestFramework.NUnit,
            ["NUnit.Framework.TheoryAttribute"] = TestFramework.NUnit,

            ["Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute"] = TestFramework.MSTest,
            ["Microsoft.VisualStudio.TestTools.UnitTesting.DataTestMethodAttribute"] = TestFramework.MSTest,
        };

    /// <summary>Every attribute that marks a test method, for matching against the index.</summary>
    public static readonly IReadOnlyList<string> MethodAttributes = [.. ByAttribute.Keys];

    /// <summary>
    /// The framework an attribute belongs to. Unrecognised names fall back to xUnit only
    /// because a caller reached here with an attribute the query already matched.
    /// </summary>
    public static TestFramework FrameworkOf(string attributeFullyQualifiedName) =>
        ByAttribute.TryGetValue(attributeFullyQualifiedName, out var framework)
            ? framework
            : TestFramework.XUnit;
}
