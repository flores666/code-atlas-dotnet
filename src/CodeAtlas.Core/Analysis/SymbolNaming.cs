using CodeAtlas.Core.Model;
using Microsoft.CodeAnalysis;

namespace CodeAtlas.Core.Analysis;

/// <summary>
/// Turns Roslyn symbols into the two names CodeAtlas stores: a fully qualified name
/// that identifies a symbol across the whole solution, and a shorter display name.
/// </summary>
public static class SymbolNaming
{
    /// <summary>
    /// Identity. Includes containing namespaces, containing types, type parameters and
    /// parameter types, so overloads stay distinct. Special type aliases are deliberately
    /// <em>not</em> used, keeping names like <c>System.Int32</c> stable.
    /// </summary>
    public static readonly SymbolDisplayFormat FullyQualifiedFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters
                       | SymbolDisplayMemberOptions.IncludeContainingType
                       | SymbolDisplayMemberOptions.IncludeExplicitInterface,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
                          | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.ExpandNullable);

    /// <summary>Presentation. Short name plus a readable parameter list.</summary>
    public static readonly SymbolDisplayFormat DisplayFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters
                       | SymbolDisplayMemberOptions.IncludeExplicitInterface,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
                          | SymbolDisplayParameterOptions.IncludeName
                          | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public static string FullyQualifiedName(ISymbol symbol) =>
        symbol.ToDisplayString(FullyQualifiedFormat);

    public static string Display(ISymbol symbol) =>
        symbol.ToDisplayString(DisplayFormat);

    /// <summary>
    /// The declaration a use of a symbol refers to: the open generic definition, and for
    /// an extension method called as an instance method the method as it was declared.
    /// Identity is stored for declarations, so every edge into one has to name it the
    /// same way or it will not resolve.
    /// </summary>
    public static ISymbol Definition(ISymbol symbol) => symbol switch
    {
        IMethodSymbol { ReducedFrom: { } reduced } => reduced.OriginalDefinition,
        _ => symbol.OriginalDefinition,
    };

    /// <summary>Containing namespace, or <c>null</c> at global scope.</summary>
    public static string? NamespaceOf(ISymbol symbol) =>
        symbol.ContainingNamespace is { IsGlobalNamespace: false } ns
            ? ns.ToDisplayString(FullyQualifiedFormat)
            : null;

    /// <summary>
    /// Maps a Roslyn symbol onto an indexed kind, or <c>null</c> for kinds CodeAtlas
    /// does not index (locals, parameters, type parameters, property accessors, ...).
    /// </summary>
    /// <remarks>
    /// <c>record struct</c> is reported as <see cref="IndexedSymbolKind.Record"/> so the
    /// kind matches the declaring keyword the user wrote.
    /// </remarks>
    public static IndexedSymbolKind? MapKind(ISymbol symbol) => symbol switch
    {
        INamespaceSymbol => IndexedSymbolKind.Namespace,

        INamedTypeSymbol type => type.TypeKind switch
        {
            TypeKind.Interface => IndexedSymbolKind.Interface,
            TypeKind.Enum => IndexedSymbolKind.Enum,
            TypeKind.Delegate => IndexedSymbolKind.Delegate,
            TypeKind.Class when type.IsRecord => IndexedSymbolKind.Record,
            TypeKind.Struct when type.IsRecord => IndexedSymbolKind.Record,
            TypeKind.Class => IndexedSymbolKind.Class,
            TypeKind.Struct => IndexedSymbolKind.Struct,
            _ => null,
        },

        IMethodSymbol method => method.MethodKind switch
        {
            MethodKind.Constructor or MethodKind.StaticConstructor => IndexedSymbolKind.Constructor,
            MethodKind.Ordinary
                or MethodKind.ExplicitInterfaceImplementation
                or MethodKind.UserDefinedOperator
                or MethodKind.Conversion
                or MethodKind.Destructor => IndexedSymbolKind.Method,
            _ => null,
        },

        IPropertySymbol => IndexedSymbolKind.Property,
        IFieldSymbol => IndexedSymbolKind.Field,
        IEventSymbol => IndexedSymbolKind.Event,
        _ => null,
    };
}
