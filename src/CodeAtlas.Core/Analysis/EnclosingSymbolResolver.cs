using CodeAtlas.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeAtlas.Core.Analysis;

/// <summary>
/// Resolves the indexed declaration a syntax node was written inside, for one document.
/// </summary>
/// <remarks>
/// <para>
/// A relation belongs to the member it was written in, and the innermost declaration is
/// not always that member: an accessor's <see cref="ISymbol.ContainingSymbol"/> is its
/// type, skipping the very property the relation belongs to, so the walk widens through
/// <see cref="IMethodSymbol.AssociatedSymbol"/> instead. A local function's relations
/// belong to its containing method for the same reason.
/// </para>
/// <para>
/// Results are cached per declaration node, because every relation inside one declaration
/// shares a source. One instance covers one document, which is the scope its semantic
/// model covers.
/// </para>
/// </remarks>
internal sealed class EnclosingSymbolResolver(SemanticModel model)
{
    private readonly Dictionary<SyntaxNode, ISymbol?> _cache = [];

    /// <summary>The indexed symbol a node sits inside, or <c>null</c> when there is none.</summary>
    private ISymbol? Resolve(SyntaxNode node, CancellationToken cancellationToken)
    {
        var declaration = node.FirstAncestorOrSelf<SyntaxNode>(IsDeclarationBoundary);
        if (declaration is null)
        {
            return null;
        }

        if (_cache.TryGetValue(declaration, out var cached))
        {
            return cached;
        }

        var symbol = DeclaredSymbol(declaration, cancellationToken);
        while (symbol is not null && SymbolNaming.MapKind(symbol) is null or IndexedSymbolKind.Namespace)
        {
            symbol = symbol switch
            {
                IMethodSymbol { AssociatedSymbol: { } associated } => associated,
                _ => symbol.ContainingSymbol,
            };
        }

        _cache[declaration] = symbol;
        return symbol;
    }

    public string? NameOf(SyntaxNode node, CancellationToken cancellationToken) =>
        Resolve(node, cancellationToken) is { } symbol ? SymbolNaming.FullyQualifiedName(symbol) : null;

    /// <summary>
    /// The type a node sits in. Configuration and infrastructure edges are attributed to
    /// the type rather than to the member, for the reason <see cref="RelationKind.Injects"/>
    /// is: a component's dependencies belong to the component, and a graph reaches them in
    /// one hop instead of through a method node.
    /// </summary>
    public INamedTypeSymbol? ContainingType(SyntaxNode node, CancellationToken cancellationToken) =>
        Resolve(node, cancellationToken) switch
        {
            INamedTypeSymbol type => type,
            { ContainingType: { } type } => type,
            _ => null,
        };

    private static bool IsDeclarationBoundary(SyntaxNode node) =>
        node is MemberDeclarationSyntax
            or VariableDeclaratorSyntax
            or AccessorDeclarationSyntax
            or LocalFunctionStatementSyntax;

    /// <summary>
    /// A field or event-field declaration carries no symbol of its own. Its declared
    /// type is shared by every declarator, so the first one stands for the group.
    /// </summary>
    private static VariableDeclaratorSyntax? FirstDeclarator(BaseFieldDeclarationSyntax field) =>
        field.Declaration.Variables.FirstOrDefault();

    private ISymbol? DeclaredSymbol(SyntaxNode node, CancellationToken cancellationToken) =>
        node switch
        {
            // Fields and event fields declare their symbol on the declarator, not the
            // declaration, and the declarator is the nearer ancestor either way.
            VariableDeclaratorSyntax declarator => model.GetDeclaredSymbol(declarator, cancellationToken),

            // Reached by references in the declared type, which sits outside the declarator.
            BaseFieldDeclarationSyntax field when FirstDeclarator(field) is { } declarator =>
                model.GetDeclaredSymbol(declarator, cancellationToken),

            AccessorDeclarationSyntax accessor => model.GetDeclaredSymbol(accessor, cancellationToken),
            LocalFunctionStatementSyntax local => model.GetDeclaredSymbol(local, cancellationToken),
            MemberDeclarationSyntax member => model.GetDeclaredSymbol(member, cancellationToken),
            _ => null,
        };
}
