namespace CodeAtlas.Core.Model;

/// <summary>
/// What one entry of a context pack contributes.
/// </summary>
/// <remarks>
/// Every kind is a projection of something the index already holds, which is what makes a
/// pack reproducible: nothing here is generated, summarised by a model, or fetched from
/// anywhere. A kind decides which document an entry writes into and what source files it
/// pulls along with it.
/// </remarks>
public enum ContextItemKind
{
    /// <summary>One declaration, and the file it is written in.</summary>
    Symbol,

    /// <summary>One source file, verbatim.</summary>
    SourceFile,

    /// <summary>What calls a symbol.</summary>
    Callers,

    /// <summary>What implements, overrides or derives from a symbol.</summary>
    Implementations,

    /// <summary>What a symbol calls and is constructed with.</summary>
    Dependencies,

    /// <summary>An HTTP entry point and the flow behind it.</summary>
    EndpointFlow,

    /// <summary>The tests that exercise a symbol, with the evidence for each.</summary>
    RelatedTests,

    /// <summary>The working tree's diff against its Git baseline.</summary>
    GitDiff,

    /// <summary>What a change to a symbol can affect.</summary>
    Impact,

    /// <summary>The solution's projects, wiring and boundaries.</summary>
    Architecture,
}

/// <summary>The documents a pack is made of, one per subject.</summary>
public enum ContextDocument
{
    Task,
    Architecture,
    ExecutionFlow,
    Impact,
    GitDiff,
}

public static class ContextDocuments
{
    /// <summary>The folder a pack is exported as.</summary>
    public const string FolderName = "ContextPack";

    public static string FileName(ContextDocument document) => document switch
    {
        ContextDocument.Architecture => "Architecture.md",
        ContextDocument.ExecutionFlow => "ExecutionFlow.md",
        ContextDocument.Impact => "Impact.md",
        ContextDocument.GitDiff => "GitDiff.patch",
        _ => "Task.md",
    };

    /// <summary>The heading each document opens with.</summary>
    public static string Title(ContextDocument document) => document switch
    {
        ContextDocument.Architecture => "Architecture",
        ContextDocument.ExecutionFlow => "Execution flow",
        ContextDocument.Impact => "Impact",
        ContextDocument.GitDiff => "Working tree diff",
        _ => "Task",
    };
}

/// <summary>
/// One source file carried by a pack, captured when it was added.
/// </summary>
/// <remarks>
/// A file appears in a pack exactly once, under <see cref="PackPath"/>, no matter how many
/// entries asked for it. The documents therefore name files and never quote them: source
/// code is duplicated nowhere, which is the whole reason a pack is smaller than the sum of
/// what went into it.
/// </remarks>
public sealed record ContextFile(string RelativePath, string Text, bool IsTest)
{
    public const string SourceFolder = "RelevantFiles";

    public const string TestFolder = "RelatedTests";

    /// <summary>Where this file sits inside the pack.</summary>
    public string PackPath => $"{(IsTest ? TestFolder : SourceFolder)}/{RelativePath}";
}

/// <summary>
/// One entry of a context pack.
/// </summary>
/// <param name="Reason">
/// Why this entry is relevant, in terms the reader can check. Carried on every entry —
/// chosen by hand or suggested — because a pack a developer cannot audit is one they
/// cannot trust to hand to an agent.
/// </param>
public sealed record ContextItem
{
    public required ContextItemKind Kind { get; init; }

    /// <summary>Identity within a pack. Adding the same key twice is a no-op.</summary>
    public required string Key { get; init; }

    public required string Title { get; init; }

    public required string Reason { get; init; }

    /// <summary>
    /// This entry's section of its document, in Markdown. Never contains source code:
    /// code lives under <see cref="ContextFile.SourceFolder"/> and is referred to by path.
    /// </summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>The files this entry brings with it.</summary>
    public IReadOnlyList<ContextFile> Files { get; init; } = [];

    public ContextDocument Document => Kind switch
    {
        ContextItemKind.Architecture => ContextDocument.Architecture,
        ContextItemKind.EndpointFlow or ContextItemKind.Callers
            or ContextItemKind.Implementations or ContextItemKind.Dependencies =>
            ContextDocument.ExecutionFlow,
        ContextItemKind.Impact => ContextDocument.Impact,
        ContextItemKind.GitDiff => ContextDocument.GitDiff,
        _ => ContextDocument.Task,
    };

    public string KindLabel => Kind switch
    {
        ContextItemKind.Symbol => "Symbol",
        ContextItemKind.SourceFile => "Source file",
        ContextItemKind.Callers => "Callers",
        ContextItemKind.Implementations => "Implementations",
        ContextItemKind.Dependencies => "Dependencies",
        ContextItemKind.EndpointFlow => "Endpoint flow",
        ContextItemKind.RelatedTests => "Related tests",
        ContextItemKind.GitDiff => "Git diff",
        ContextItemKind.Impact => "Impact",
        _ => "Architecture",
    };
}

/// <summary>
/// How big a pack is, in the three units a reader needs.
/// </summary>
/// <remarks>
/// <see cref="Tokens"/> is an estimate and says so: one token per
/// <see cref="CharactersPerToken"/> characters is the usual rule of thumb for English and
/// for code, and CodeAtlas has no tokenizer and contacts no service that could supply one.
/// It is the right order of magnitude for a budget, and it is never presented as exact.
/// </remarks>
public readonly record struct ContextSize(int Characters, int Lines, int Tokens)
{
    public const int CharactersPerToken = 4;

    public static ContextSize Zero => default;

    public static ContextSize Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
        {
            return Zero;
        }

        var lines = 1;
        foreach (var character in text)
        {
            if (character == '\n')
            {
                lines++;
            }
        }

        return new ContextSize(text.Length, lines, EstimateTokens(text.Length));
    }

    public static int EstimateTokens(int characters) =>
        (characters + CharactersPerToken - 1) / CharactersPerToken;

    public static ContextSize operator +(ContextSize left, ContextSize right) => new(
        left.Characters + right.Characters,
        left.Lines + right.Lines,
        left.Tokens + right.Tokens);
}

/// <summary>What happened to an attempt to add an entry.</summary>
public enum ContextAddOutcome
{
    Added,

    /// <summary>The same entry is already in the pack.</summary>
    AlreadyPresent,

    /// <summary>There was nothing to add: the index holds no such relation.</summary>
    Empty,

    /// <summary>Adding it would take the pack past its budget, so it was not added.</summary>
    ExceedsBudget,
}

/// <summary>
/// The outcome of adding an entry, with the sentence explaining it.
/// </summary>
/// <remarks>
/// A refused add is reported rather than absorbed: a budget that is quietly exceeded, or
/// quietly enforced by dropping content, is worse than no budget at all.
/// </remarks>
public sealed record ContextAddResult(ContextAddOutcome Outcome, string Message)
{
    public bool IsAdded => Outcome == ContextAddOutcome.Added;
}

/// <summary>One file of a rendered pack: where it sits, and exactly what is in it.</summary>
public sealed record ContextPackFile(string Path, string Text)
{
    public ContextSize Size => ContextSize.Of(Text);
}
