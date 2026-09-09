using CodeAtlas.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeAtlas.Core.Git;

/// <summary>
/// Reads the declarations out of a baseline source file by parsing its syntax alone.
/// </summary>
/// <remarks>
/// <para>
/// This exists for one question the index cannot answer: what a <em>deleted</em> file used
/// to declare. Those symbols are absent from the index by construction — Roslyn never saw
/// the file, because it is no longer on disk — so the only place their names survive is the
/// baseline text Git still has.
/// </para>
/// <para>
/// It is a parse, not a compilation: there is no semantic model, no references and no
/// project behind it, so a name here is the name as it was written rather than a resolved
/// identity. That is why everything it produces is
/// <see cref="RelationProvenance.Inferred"/> and never navigable. Deliberately used only
/// for whole-file deletions, where "everything in it is gone" is exact and no matching
/// against indexed names — which syntax alone cannot do reliably — is required.
/// </para>
/// </remarks>
public static class BaselineDeclarations
{
    /// <summary>
    /// Every type and member declared in <paramref name="source"/>, as removed symbols.
    /// </summary>
    public static IReadOnlyList<ChangedSymbol> ReadRemoved(string source, string? filePath)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return [];
        }

        var walker = new DeclarationWalker(filePath);
        walker.Visit(CSharpSyntaxTree.ParseText(source).GetRoot());

        return walker.Declarations;
    }

    private sealed class DeclarationWalker(string? filePath) : CSharpSyntaxWalker
    {
        private readonly List<string> _container = [];

        public List<ChangedSymbol> Declarations { get; } = [];

        public override void VisitNamespaceDeclaration(NamespaceDeclarationSyntax node) =>
            InScope(node.Name.ToString(), () => base.VisitNamespaceDeclaration(node));

        public override void VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node) =>
            InScope(node.Name.ToString(), () => base.VisitFileScopedNamespaceDeclaration(node));

        public override void VisitClassDeclaration(ClassDeclarationSyntax node) =>
            VisitType(node, IndexedSymbolKind.Class, () => base.VisitClassDeclaration(node));

        public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) =>
            VisitType(node, IndexedSymbolKind.Interface, () => base.VisitInterfaceDeclaration(node));

        public override void VisitStructDeclaration(StructDeclarationSyntax node) =>
            VisitType(node, IndexedSymbolKind.Struct, () => base.VisitStructDeclaration(node));

        /// <summary>
        /// Reported as <see cref="IndexedSymbolKind.Record"/> for both <c>record</c> and
        /// <c>record struct</c>, matching the keyword the author wrote — the same rule the
        /// indexer follows.
        /// </summary>
        public override void VisitRecordDeclaration(RecordDeclarationSyntax node) =>
            VisitType(node, IndexedSymbolKind.Record, () => base.VisitRecordDeclaration(node));

        public override void VisitEnumDeclaration(EnumDeclarationSyntax node) =>
            VisitType(node, IndexedSymbolKind.Enum, () => base.VisitEnumDeclaration(node));

        public override void VisitDelegateDeclaration(DelegateDeclarationSyntax node) =>
            Add(node.Identifier.Text, IndexedSymbolKind.Delegate, node);

        public override void VisitMethodDeclaration(MethodDeclarationSyntax node) =>
            Add(node.Identifier.Text + Parameters(node.ParameterList), IndexedSymbolKind.Method, node);

        public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node) =>
            Add(node.Identifier.Text + Parameters(node.ParameterList), IndexedSymbolKind.Constructor, node);

        public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node) =>
            Add(node.Identifier.Text, IndexedSymbolKind.Property, node);

        public override void VisitIndexerDeclaration(IndexerDeclarationSyntax node) =>
            Add("this" + Parameters(node.ParameterList, '[', ']'), IndexedSymbolKind.Property, node);

        public override void VisitEventDeclaration(EventDeclarationSyntax node) =>
            Add(node.Identifier.Text, IndexedSymbolKind.Event, node);

        public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
        {
            foreach (var variable in node.Declaration.Variables)
            {
                Add(variable.Identifier.Text, IndexedSymbolKind.Field, variable);
            }
        }

        public override void VisitEventFieldDeclaration(EventFieldDeclarationSyntax node)
        {
            foreach (var variable in node.Declaration.Variables)
            {
                Add(variable.Identifier.Text, IndexedSymbolKind.Event, variable);
            }
        }

        private void VisitType(TypeDeclarationSyntax node, IndexedSymbolKind kind, Action visitChildren)
        {
            Add(node.Identifier.Text, kind, node);
            InScope(node.Identifier.Text, visitChildren);
        }

        private void VisitType(EnumDeclarationSyntax node, IndexedSymbolKind kind, Action visitChildren)
        {
            Add(node.Identifier.Text, kind, node);
            InScope(node.Identifier.Text, visitChildren);
        }

        private void InScope(string name, Action visitChildren)
        {
            _container.Add(name);
            visitChildren();
            _container.RemoveAt(_container.Count - 1);
        }

        private void Add(string display, IndexedSymbolKind kind, SyntaxNode node) =>
            Declarations.Add(new ChangedSymbol
            {
                Display = display,
                Kind = kind,
                Change = SymbolChangeKind.Removed,
                Container = _container.Count == 0 ? null : string.Join('.', _container),
                FilePath = filePath,
                Line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                Provenance = RelationProvenance.Inferred,
            });

        /// <summary>
        /// The parameter types as written, which is what keeps two overloads apart in a
        /// list the reader is scanning.
        /// </summary>
        private static string Parameters(BaseParameterListSyntax? list, char open = '(', char close = ')') =>
            $"{open}{string.Join(", ", list?.Parameters.Select(p => p.Type?.ToString() ?? p.Identifier.Text) ?? [])}{close}";
    }
}
