using CodeAtlas.Core.Model;
using CodeAtlas.Core.Trace;

namespace CodeAtlas.Desktop.ViewModels;

/// <summary>
/// One step of the request execution flow. The trace arrives in reading order, so a step
/// only has to say how far in it sits and where it comes in the sequence.
/// </summary>
public sealed class TraceStepViewModel
{
    /// <summary>How far one hop moves a step to the right.</summary>
    private const double IndentPerDepth = 16;

    /// <summary>Width of the elbow joining a step to its parent's rail.</summary>
    private const double ElbowWidth = 12;

    public TraceStepViewModel(TraceStep step, int number)
    {
        ArgumentNullException.ThrowIfNull(step);

        Number = number;
        Symbol = step.Symbol;
        Display = step.Symbol.Display;
        Glyph = SymbolGlyph.For(step.Symbol.Kind);
        IsContainer = SymbolGlyph.IsContainer(step.Symbol.Kind);
        Container = step.Symbol.ContainerFullyQualifiedName ?? step.Symbol.Namespace ?? string.Empty;
        Origin = SymbolGlyph.Origin(step.Symbol.FilePath, step.Symbol.Line);

        IsEntry = step.Kind == TraceStepKind.Entry;
        GutterWidth = (step.Depth * IndentPerDepth) + ElbowWidth;

        // Only dispatch is worth naming: a call is the default way one step follows
        // another, and labelling every step "calls" would say nothing.
        Relation = step.Kind == TraceStepKind.Implements ? "implements" : string.Empty;

        Note = step.End switch
        {
            TraceStepEnd.Repeated => "shown above",
            TraceStepEnd.Deeper => "continues deeper",
            _ => string.Empty,
        };
    }

    /// <summary>Its position in the flow, 1-based, as the numbered disc shows it.</summary>
    public int Number { get; }

    public IndexedSymbol Symbol { get; }

    public string Display { get; }

    public string Glyph { get; }

    public bool IsContainer { get; }

    /// <summary>The declaring type, which is what identifies a bare method name.</summary>
    public string Container { get; }

    public string Origin { get; }

    /// <summary>True for a step the request enters at, which reads as the head of its tree.</summary>
    public bool IsEntry { get; }

    /// <summary>
    /// Width of the space left of the card: the indent this step's depth earns, plus room
    /// for the elbow that joins it to the rail of the step it follows from.
    /// </summary>
    public double GutterWidth { get; }

    public bool HasGutter => !IsEntry;

    public string Relation { get; }

    public bool HasRelation => Relation.Length > 0;

    /// <summary>Why the trace stops here, when it stops for a reason worth saying.</summary>
    public string Note { get; }

    public bool HasNote => Note.Length > 0;

    public bool CanOpenSource => Symbol.FilePath is { Length: > 0 };
}
