namespace CodeAtlas.Core.Model;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// A problem encountered while loading or indexing. Diagnostics never abort the
/// run: a project that fails to load is reported here and the rest continue.
/// </summary>
public sealed record IndexDiagnostic(
    DiagnosticSeverity Severity,
    string? Project,
    string Message);
