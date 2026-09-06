using CodeAtlas.Core.Model;

namespace CodeAtlas.Desktop.ViewModels;

/// <summary>
/// One DI registration, as shown in the details pane. A service with several registrations
/// gets several rows: that is what the container holds.
/// </summary>
public sealed class RegistrationViewModel(ServiceRegistration registration)
{
    public string Lifetime { get; } = registration.Lifetime.ToString();

    public string Implementation { get; } = registration.ImplementationDisplay
        ?? (registration.Kind == RegistrationKind.Factory ? "built by a factory" : "unknown");

    public string Service { get; } = registration.ServiceDisplay;

    /// <summary>Where the registration was written, including the method it sits in.</summary>
    public string Origin { get; } = registration.FilePath is { } path
        ? $"{System.IO.Path.GetFileName(path)}:{registration.Line?.ToString() ?? "?"}"
          + (registration.DeclaringMember is { } member ? $" · {member}" : string.Empty)
        : registration.DeclaringMember ?? string.Empty;

    public bool IsExact { get; } = registration.Provenance == RelationProvenance.Exact;
}
