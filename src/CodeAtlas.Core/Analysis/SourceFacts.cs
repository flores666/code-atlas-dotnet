using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeAtlas.Core.Analysis;

/// <summary>
/// The small readings of bound syntax that every infrastructure collector needs: where a
/// node was written, and which of its arguments the compiler bound to a given parameter.
/// </summary>
internal static class SourceFacts
{
    public static (string? Path, int? Line) LocationOf(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        return span.Path is { Length: > 0 } path ? (path, span.StartLinePosition.Line + 1) : (null, null);
    }

    public static (string? Path, int? Line) LocationOf(ISymbol symbol)
    {
        if (symbol.Locations.FirstOrDefault(location => location.IsInSource) is not { } source)
        {
            return (null, null);
        }

        var span = source.GetLineSpan();
        return (span.Path, span.StartLinePosition.Line + 1);
    }

    /// <summary>The value of an expression the compiler folded to a string constant.</summary>
    public static string? ConstantString(
        ExpressionSyntax expression,
        SemanticModel model,
        CancellationToken cancellationToken) =>
        model.GetConstantValue(expression, cancellationToken) is { HasValue: true, Value: string value }
            ? value
            : null;

    /// <summary>
    /// The argument bound to one parameter, whether it was passed positionally or by name.
    /// Reading the bound method's parameter list rather than an argument position is what
    /// makes <c>AddColumn(name: "Title", table: "Books")</c> and <c>AddColumn("Title",
    /// "Books")</c> the same reading.
    /// </summary>
    public static ExpressionSyntax? ArgumentFor(
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        string parameterName)
    {
        var arguments = invocation.ArgumentList.Arguments;

        for (var i = 0; i < arguments.Count; i++)
        {
            var name = arguments[i].NameColon?.Name.Identifier.ValueText
                       ?? (i < method.Parameters.Length ? method.Parameters[i].Name : null);

            if (name == parameterName)
            {
                return arguments[i].Expression;
            }
        }

        return null;
    }

    /// <summary>The string constant passed for one parameter, when it is one.</summary>
    public static string? StringArgumentFor(
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        string parameterName,
        SemanticModel model,
        CancellationToken cancellationToken) =>
        ArgumentFor(invocation, method, parameterName) is { } expression
            ? ConstantString(expression, model, cancellationToken)
            : null;

    /// <summary>The expression a member-access call was made on, e.g. <c>context.Users</c> in a set call.</summary>
    public static ExpressionSyntax? Receiver(InvocationExpressionSyntax invocation) =>
        (invocation.Expression as MemberAccessExpressionSyntax)?.Expression;

    /// <summary>True for a symbol declared somewhere in the loaded solution.</summary>
    public static bool IsInSource(ISymbol symbol) => symbol.Locations.Any(location => location.IsInSource);

    /// <summary>Matches a type by namespace and name, ignoring how its type arguments print.</summary>
    public static bool IsType(ITypeSymbol? type, string @namespace, string name) =>
        type is INamedTypeSymbol named &&
        named.Name == name &&
        SymbolNaming.NamespaceOf(named.OriginalDefinition) == @namespace;

    /// <summary>True when <paramref name="type"/> is, or derives from, the named type.</summary>
    public static bool DerivesFrom(ITypeSymbol? type, string fullyQualifiedName)
    {
        for (var current = type as INamedTypeSymbol; current is not null; current = current.BaseType)
        {
            if (SymbolNaming.FullyQualifiedName(current.OriginalDefinition) == fullyQualifiedName)
            {
                return true;
            }
        }

        return false;
    }

    public static AttributeData? Attribute(ISymbol symbol, string fullyQualifiedName) =>
        symbol.GetAttributes().FirstOrDefault(attribute =>
            attribute.AttributeClass is { } attributeClass &&
            SymbolNaming.FullyQualifiedName(attributeClass) == fullyQualifiedName);

    public static string? FirstStringArgument(AttributeData attribute) =>
        attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Value is string value
            ? value
            : null;

    public static string? NamedStringArgument(AttributeData attribute, string name) =>
        attribute.NamedArguments.FirstOrDefault(pair => pair.Key == name).Value.Value as string;
}
