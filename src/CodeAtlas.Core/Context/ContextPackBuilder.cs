using System.Globalization;
using System.Text;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Context;

/// <summary>
/// Assembles a context pack: a small, auditable bundle of what CodeAtlas already knows
/// about a change, meant to be handed to an external coding agent by hand.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here calls a model or a service, and nothing is generated. Every entry is a
/// projection of the index, the working tree's diff, or an impact report, and every entry
/// carries the reason it is relevant — so a developer can read a pack and disagree with
/// it, which is the only way a pack is worth handing on.
/// </para>
/// <para>
/// Source code appears in a pack exactly once. Entries describe relations in names and
/// locations, and the files themselves sit under <see cref="ContextFile.SourceFolder"/>
/// and <see cref="ContextFile.TestFolder"/>. That is what keeps a pack from costing three
/// copies of the same class, and it is enforced here rather than left to whoever adds the
/// entries.
/// </para>
/// <para>
/// The budget is a refusal, not a trim. <see cref="Add"/> reports that an entry would take
/// the pack past <see cref="BudgetTokens"/> and leaves the pack alone; silently dropping
/// content or silently overrunning would both leave the reader with a pack that is not
/// what it says it is.
/// </para>
/// </remarks>
public sealed class ContextPackBuilder
{
    /// <summary>
    /// Rows and files one entry may carry.
    /// </summary>
    /// <remarks>
    /// A shared abstraction has hundreds of callers, and a pack holding all of their files
    /// is not a focused pack. The cap is stated in the entry's own text whenever it bites,
    /// so a partial list never reads as a complete one.
    /// </remarks>
    public const int MaxRelatedSymbols = 25;

    /// <summary>Rows per section of the architecture summary, which is a summary.</summary>
    public const int MaxArchitectureRows = 40;

    /// <summary>Hops of composition followed behind an endpoint.</summary>
    public const int MaxFlowDepth = 4;

    /// <summary>
    /// The default ceiling, in estimated tokens.
    /// </summary>
    /// <remarks>
    /// Large enough for a focused pack — a handful of files, their tests and the documents
    /// around them — and small enough that a pack which has quietly grown into half a
    /// solution is stopped rather than pasted. It is a starting point, not a limit of the
    /// tool: the reader sets their own.
    /// </remarks>
    public const int DefaultBudgetTokens = 60_000;

    private static readonly RelationKind[] CompositionKinds =
        [RelationKind.Injects, RelationKind.Resolves];

    private readonly SymbolIndexDatabase _database;
    private readonly string _root;
    private readonly List<ContextItem> _items = [];

    /// <param name="root">
    /// The directory pack paths are relative to — the working tree when there is one, so a
    /// pack reads in the same paths the agent will be given.
    /// </param>
    public ContextPackBuilder(SymbolIndexDatabase database, string root)
    {
        ArgumentNullException.ThrowIfNull(database);

        _database = database;
        _root = string.IsNullOrWhiteSpace(root) ? Directory.GetCurrentDirectory() : root;
    }

    /// <summary>What the agent is being asked to do. Written into <c>Task.md</c> verbatim.</summary>
    public string TaskText { get; set; } = string.Empty;

    /// <summary>The ceiling in estimated tokens; zero or less means no ceiling.</summary>
    public int BudgetTokens { get; set; } = DefaultBudgetTokens;

    public bool HasBudget => BudgetTokens > 0;

    public IReadOnlyList<ContextItem> Items => _items;

    /// <summary>How big the pack currently is, measured on what would actually be written.</summary>
    public ContextSize Size => Measure(_items);

    public bool Contains(string key) => _items.Any(item => item.Key == key);

    // ---- assembling ---------------------------------------------------------

    /// <summary>
    /// Adds an entry, unless it is already present or would take the pack past its budget.
    /// </summary>
    public ContextAddResult Add(ContextItem? item)
    {
        if (item is null)
        {
            return new ContextAddResult(
                ContextAddOutcome.Empty,
                "There is nothing in the index to add for that.");
        }

        if (Contains(item.Key))
        {
            return new ContextAddResult(
                ContextAddOutcome.AlreadyPresent,
                $"{item.Title} is already in the pack.");
        }

        var projected = Measure([.. _items, item]);

        if (HasBudget && projected.Tokens > BudgetTokens)
        {
            return new ContextAddResult(
                ContextAddOutcome.ExceedsBudget,
                $"{item.Title} would take the pack to ~{projected.Tokens:N0} tokens, " +
                $"past the {BudgetTokens:N0} budget. It was not added.");
        }

        _items.Add(item);

        return new ContextAddResult(
            ContextAddOutcome.Added,
            $"Added {item.Title}. The pack is now ~{projected.Tokens:N0} tokens.");
    }

    public bool Remove(string key) => _items.RemoveAll(item => item.Key == key) > 0;

    public void Clear() => _items.Clear();

    // ---- entries ------------------------------------------------------------

    /// <summary>One declaration, with the file it is written in.</summary>
    public ContextItem? CreateSymbol(long symbolId) =>
        _database.GetSymbol(symbolId) is { } symbol ? CreateSymbol(symbol) : null;

    private ContextItem CreateSymbol(IndexedSymbol symbol)
    {
        var body = new StringBuilder()
            .Append("## ").AppendLine(symbol.Display)
            .AppendLine()
            .Append("- Kind: ").AppendLine(symbol.Kind.ToString().ToLowerInvariant())
            .Append("- Fully qualified name: `").Append(symbol.FullyQualifiedName).AppendLine("`")
            .Append("- Project: ").AppendLine(symbol.ProjectName ?? "unknown")
            .Append("- Declared at: ").AppendLine(Origin(symbol))
            .ToString();

        return new ContextItem
        {
            Kind = ContextItemKind.Symbol,
            Key = $"symbol:{symbol.Id}",
            Title = symbol.Display,
            Reason = "The declaration the task is about.",
            Body = body,
            Files = Files(symbol),
        };
    }

    /// <summary>One file, verbatim.</summary>
    public ContextItem? CreateSourceFile(string absolutePath)
    {
        if (ReadFile(absolutePath, isTest: false) is not { } file)
        {
            return null;
        }

        return new ContextItem
        {
            Kind = ContextItemKind.SourceFile,
            Key = $"file:{file.RelativePath}",
            Title = file.RelativePath,
            Reason = "Added as a whole file.",
            Files = [file],
        };
    }

    public ContextItem? CreateCallers(long symbolId) =>
        _database.GetDetails(symbolId) is { } details ? CreateCallers(details) : null;

    private ContextItem? CreateCallers(SymbolDetails details)
    {
        var callers = Resolve(details.CalledBy);

        if (callers.Count == 0)
        {
            return null;
        }

        var body = Section(
            $"Callers of `{details.Symbol.Display}`",
            Counted(callers.Count, details.CalledByTotal, "member", "call it directly."),
            callers);

        return new ContextItem
        {
            Kind = ContextItemKind.Callers,
            Key = $"callers:{details.Symbol.Id}",
            Title = $"Callers of {details.Symbol.Display}",
            Reason = Counted(callers.Count, details.CalledByTotal, "member", "call it directly."),
            Body = body,
            Files = Files(callers),
        };
    }

    public ContextItem? CreateImplementations(long symbolId) =>
        _database.GetDetails(symbolId) is { } details ? CreateImplementations(details) : null;

    private ContextItem? CreateImplementations(SymbolDetails details)
    {
        var links = details.Implementors
            .Concat(details.OverriddenBy)
            .Concat(details.DerivedTypes)
            .Concat(details.Resolves)
            .ToList();

        var implementations = Resolve(links);

        if (implementations.Count == 0)
        {
            return null;
        }

        var reason = $"{Plural(implementations.Count, "type or member")} implement, override, " +
                     $"derive from or are registered behind it.";

        return new ContextItem
        {
            Kind = ContextItemKind.Implementations,
            Key = $"implementations:{details.Symbol.Id}",
            Title = $"Implementations of {details.Symbol.Display}",
            Reason = reason,
            Body = Section($"Implementations of `{details.Symbol.Display}`", reason, implementations),
            Files = Files(implementations),
        };
    }

    public ContextItem? CreateDependencies(long symbolId) =>
        _database.GetDetails(symbolId) is { } details ? CreateDependencies(details) : null;

    private ContextItem? CreateDependencies(SymbolDetails details)
    {
        var links = details.Calls
            .Concat(details.Injects)
            .Concat(details.ParameterTypes)
            .Concat(details.ReturnTypes)
            .ToList();

        var dependencies = Resolve(links);

        if (dependencies.Count == 0)
        {
            return null;
        }

        var reason = $"It calls, is constructed with, or names {Plural(dependencies.Count, "symbol")} " +
                     "in its signature.";

        return new ContextItem
        {
            Kind = ContextItemKind.Dependencies,
            Key = $"dependencies:{details.Symbol.Id}",
            Title = $"Dependencies of {details.Symbol.Display}",
            Reason = reason,
            Body = Section($"Dependencies of `{details.Symbol.Display}`", reason, dependencies),
            Files = Files(dependencies),
        };
    }

    public ContextItem? CreateRelatedTests(long symbolId) =>
        _database.GetDetails(symbolId) is { } details ? CreateRelatedTests(details) : null;

    private ContextItem? CreateRelatedTests(SymbolDetails details)
    {
        var tests = details.RelatedTests.Take(MaxRelatedSymbols).ToList();

        if (tests.Count == 0)
        {
            return null;
        }

        var exact = tests.Count(test => test.IsExact);
        var reason = exact == tests.Count
            ? $"{Plural(tests.Count, "test")} exercise it, all on edges the compiler recorded."
            : $"{Plural(tests.Count, "test")} exercise it; {exact} on edges the compiler recorded, " +
              $"the rest read off names and project structure.";

        var body = new StringBuilder()
            .Append("## Tests over `").Append(details.Symbol.Display).AppendLine("`")
            .AppendLine()
            .AppendLine(reason)
            .AppendLine();

        foreach (var test in tests)
        {
            body.Append("- `").Append(test.Test.QualifiedDisplay).Append("` — ")
                .Append(test.IsExact ? "exact" : "probable").Append(": ").Append(test.Reason)
                .Append(" — ").AppendLine(Origin(test.Test.FilePath, test.Test.Line));
        }

        var files = tests
            .Select(test => ReadFile(test.Test.FilePath, isTest: true))
            .OfType<ContextFile>()
            .DistinctBy(file => file.RelativePath)
            .ToList();

        return new ContextItem
        {
            Kind = ContextItemKind.RelatedTests,
            Key = $"tests:{details.Symbol.Id}",
            Title = $"Tests over {details.Symbol.Display}",
            Reason = reason,
            Body = body.ToString(),
            Files = files,
        };
    }

    /// <summary>
    /// An HTTP entry point and the composition behind it.
    /// </summary>
    /// <remarks>
    /// The same walk the graph's endpoint flow is: what the handler is handed, and what the
    /// container puts behind each of those. Calls are deliberately not followed — the flow
    /// of an endpoint is how it is wired, and following calls would put half the solution
    /// in the pack the first time it crossed a hot method.
    /// </remarks>
    public ContextItem? CreateEndpointFlow(HttpEndpoint? endpoint)
    {
        if (endpoint is null)
        {
            return null;
        }

        var flow = WalkFlow(endpoint);

        var body = new StringBuilder()
            .Append("## ").Append(endpoint.HttpMethod).Append(' ').AppendLine(endpoint.Route)
            .AppendLine()
            .Append("- Handler: `").Append(endpoint.HandlerDisplay).Append("` — ")
            .AppendLine(Origin(endpoint.FilePath, endpoint.Line));

        if (endpoint.AuthorizationSummary is { Length: > 0 } authorization)
        {
            body.Append("- Authorization: ").AppendLine(authorization);
        }

        if (endpoint.Provenance == RelationProvenance.Inferred)
        {
            body.AppendLine("- Part of this route or handler could not be resolved exactly.");
        }

        if (flow.Count > 0)
        {
            body.AppendLine().AppendLine("What it is wired to:").AppendLine();

            foreach (var (symbol, depth) in flow)
            {
                body.Append(new string(' ', (depth - 1) * 2))
                    .Append("- `").Append(symbol.Display).Append("` — ").AppendLine(Origin(symbol));
            }
        }
        else
        {
            body.AppendLine().AppendLine("Nothing indexed sits behind it.");
        }

        var reason = flow.Count > 0
            ? $"{endpoint.HttpMethod} {endpoint.Route} reaches {Plural(flow.Count, "indexed symbol")}."
            : $"{endpoint.HttpMethod} {endpoint.Route} is the entry point.";

        return new ContextItem
        {
            Kind = ContextItemKind.EndpointFlow,
            Key = $"endpoint:{endpoint.Id}",
            Title = $"{endpoint.HttpMethod} {endpoint.Route}",
            Reason = reason,
            Body = body.ToString(),
            Files = Files(flow.Select(step => step.Symbol).ToList()),
        };
    }

    /// <summary>The working tree's own diff, which is the change the task is about.</summary>
    public ContextItem? CreateGitDiff(RepositoryChanges? changes)
    {
        if (changes is null || changes.Diffs.Count == 0)
        {
            return null;
        }

        var patch = new StringBuilder();
        foreach (var diff in changes.Diffs.Where(diff => diff.Text.Length > 0))
        {
            patch.AppendLine(diff.Text.TrimEnd('\n'));
        }

        if (patch.Length == 0)
        {
            return null;
        }

        var status = changes.Status;
        var reason = $"{Plural(changes.Diffs.Count, "file")} differ from " +
                     $"{(status.IsDetached ? status.Branch : $"the {status.Branch} baseline")}, " +
                     $"touching {Plural(changes.Symbols.Count, "declaration")}.";

        return new ContextItem
        {
            Kind = ContextItemKind.GitDiff,
            Key = "git-diff",
            Title = "Working tree diff",
            Reason = reason,
            Body = patch.ToString(),
        };
    }

    /// <summary>What a change to the analysed symbol can affect, as already reported.</summary>
    public ContextItem? CreateImpact(ImpactReport? report)
    {
        if (report is null)
        {
            return null;
        }

        var body = new StringBuilder()
            .Append("## What a change to `").Append(report.Root.Display).AppendLine("` can affect")
            .AppendLine()
            .Append("Risk: ").Append(report.Risk.Level.ToString().ToLowerInvariant())
            .Append(" — ").AppendLine(report.Risk.Rationale)
            .AppendLine();

        foreach (var (label, count) in report.Summary)
        {
            body.Append("- ").Append(label).Append(": ").Append(count.ToString(CultureInfo.InvariantCulture))
                .AppendLine();
        }

        if (report.Truncated)
        {
            body.AppendLine("- A cap stopped the walk, so the real blast radius is larger than this.");
        }

        body.AppendLine().AppendLine("### Risk surface").AppendLine();
        foreach (var signal in report.Risk.Signals)
        {
            body.Append(signal.IsPresent ? "- **" : "- ").Append(signal.Title)
                .Append(signal.IsPresent ? "**: " : ": ").AppendLine(signal.Evidence);
        }

        AppendImpacted(body, "Direct callers", report.DirectCallers);
        AppendImpacted(body, "Indirect callers", report.IndirectCallers);

        if (report.Endpoints.Count > 0)
        {
            body.AppendLine().AppendLine("### Potentially affected endpoints").AppendLine();
            foreach (var endpoint in report.Endpoints.Take(MaxRelatedSymbols))
            {
                body.Append("- `").Append(endpoint.HttpMethod).Append(' ').Append(endpoint.Route)
                    .Append("` — ").AppendLine(endpoint.HandlerDisplay);
            }
        }

        if (report.Entities.Count > 0)
        {
            body.AppendLine().AppendLine("### Database entities reached").AppendLine();
            foreach (var entity in report.Entities.Take(MaxRelatedSymbols))
            {
                body.Append("- ").Append(entity.EntityDisplay)
                    .AppendLine(entity.HasTable ? $" — {entity.QualifiedTable}" : string.Empty);
            }
        }

        if (report.ExternalIntegrations.Count > 0)
        {
            body.AppendLine().AppendLine("### External integrations reached").AppendLine();
            foreach (var dependency in report.ExternalIntegrations.Take(MaxRelatedSymbols))
            {
                body.Append("- ").Append(dependency.ConsumerDisplay).Append(" → ")
                    .AppendLine(dependency.Resource);
            }
        }

        return new ContextItem
        {
            Kind = ContextItemKind.Impact,
            Key = $"impact:{report.Root.Id}",
            Title = $"Impact of {report.Root.Display}",
            Reason = $"{report.Impacted.Count} symbols can be affected, up to " +
                     $"{report.MaxDistance} hop(s) away; risk reads as " +
                     $"{report.Risk.Level.ToString().ToLowerInvariant()}.",
            Body = body.ToString(),
        };
    }

    /// <summary>
    /// How the solution is put together, as the index already has it: its projects, what
    /// the container resolves, what it persists, what it configures and where it crosses
    /// out of itself.
    /// </summary>
    public ContextItem CreateArchitecture()
    {
        var projects = _database.GetProjects();
        var registrations = _database.GetRegistrations();
        var entities = _database.GetEntities();
        var configuration = _database.GetConfigurationUsages();
        var external = _database.GetExternalDependencies();
        var endpoints = _database.GetEndpoints();

        var body = new StringBuilder()
            .AppendLine("## Projects").AppendLine();

        foreach (var project in projects.Take(MaxArchitectureRows))
        {
            body.Append("- ").Append(project.Name)
                .AppendLine(project.Loaded ? string.Empty : " (did not load; its symbols are absent)");
        }

        More(body, projects.Count, MaxArchitectureRows, "project");

        body.AppendLine().AppendLine("## Composition").AppendLine()
            .Append(Plural(endpoints.Count, "HTTP endpoint")).Append(", ")
            .Append(Plural(registrations.Count, "container registration")).AppendLine(".")
            .AppendLine();

        foreach (var registration in registrations.Take(MaxArchitectureRows))
        {
            body.Append("- ").Append(registration.Lifetime.ToString().ToLowerInvariant()).Append(' ')
                .Append(registration.ServiceDisplay).Append(" → ")
                .Append(registration.ImplementationDisplay ?? "unknown (built by a factory)")
                .AppendLine(registration.Provenance == RelationProvenance.Exact
                    ? string.Empty
                    : " (inferred from a factory body)");
        }

        More(body, registrations.Count, MaxArchitectureRows, "registration");

        if (entities.Count > 0)
        {
            body.AppendLine().AppendLine("## Persistence").AppendLine();
            foreach (var entity in entities.Take(MaxArchitectureRows))
            {
                body.Append("- ").Append(entity.EntityDisplay)
                    .Append(entity.HasTable ? $" → {entity.QualifiedTable}" : " → table decided by convention")
                    .AppendLine(entity.ContextDisplay is { Length: > 0 } context ? $" ({context})" : string.Empty);
            }

            More(body, entities.Count, MaxArchitectureRows, "entity");
        }

        if (external.Count > 0)
        {
            body.AppendLine().AppendLine("## External boundaries").AppendLine();
            foreach (var dependency in external.Take(MaxArchitectureRows))
            {
                body.Append("- ").Append(dependency.ConsumerDisplay).Append(" → ")
                    .AppendLine(dependency.Resource);
            }

            More(body, external.Count, MaxArchitectureRows, "boundary");
        }

        if (configuration.Count > 0)
        {
            body.AppendLine().AppendLine("## Configuration").AppendLine();
            foreach (var usage in configuration.Take(MaxArchitectureRows))
            {
                body.Append("- ").Append(usage.Key ?? usage.OptionsDisplay ?? "(unnamed)")
                    .AppendLine(usage.ConsumerDisplay is { Length: > 0 } consumer
                        ? $" — read by {consumer}"
                        : string.Empty);
            }

            More(body, configuration.Count, MaxArchitectureRows, "configuration key");
        }

        return new ContextItem
        {
            Kind = ContextItemKind.Architecture,
            Key = "architecture",
            Title = "Architecture summary",
            Reason = $"{Plural(projects.Count, "project")}, {Plural(registrations.Count, "registration")} " +
                     $"and {Plural(external.Count, "external boundary", "external boundaries")}, " +
                     "as the index already holds them.",
            Body = body.ToString(),
        };
    }

    // ---- suggestions --------------------------------------------------------

    /// <summary>
    /// What CodeAtlas would put in a pack about <paramref name="symbolId"/>, each with the
    /// reason it is relevant.
    /// </summary>
    /// <remarks>
    /// Suggestions are built, not merely named, so the reader sees what they would actually
    /// be adding — and what it costs — before adding it. Nothing is added automatically:
    /// the point of the section is a pack someone chose.
    /// </remarks>
    public IReadOnlyList<ContextItem> Suggest(
        long? symbolId,
        ImpactReport? impact = null,
        RepositoryChanges? changes = null)
    {
        var suggestions = new List<ContextItem?>();

        if (symbolId is { } id && _database.GetDetails(id) is { } details)
        {
            suggestions.Add(CreateSymbol(details.Symbol));
            suggestions.Add(CreateRelatedTests(details));
            suggestions.Add(CreateCallers(details));
            suggestions.Add(CreateImplementations(details));
            suggestions.Add(CreateDependencies(details));

            foreach (var endpoint in details.RelatedEndpoints.Take(3))
            {
                suggestions.Add(CreateEndpointFlow(endpoint));
            }
        }

        suggestions.Add(CreateImpact(impact));
        suggestions.Add(CreateGitDiff(changes));
        suggestions.Add(CreateArchitecture());

        return suggestions
            .OfType<ContextItem>()
            .DistinctBy(item => item.Key)
            .Where(item => !Contains(item.Key))
            .ToList();
    }

    // ---- rendering ----------------------------------------------------------

    /// <summary>
    /// The pack exactly as it would be exported.
    /// </summary>
    /// <remarks>
    /// Rendering is what the size is measured on and what the preview shows, so the number
    /// on screen, the text in the preview and the bytes on disk cannot drift apart.
    /// </remarks>
    public IReadOnlyList<ContextPackFile> Render() => Render(_items);

    private IReadOnlyList<ContextPackFile> Render(IReadOnlyList<ContextItem> items)
    {
        var files = new List<ContextPackFile> { new(ContextDocuments.FileName(ContextDocument.Task), Task(items)) };

        foreach (var document in (ContextDocument[])
                 [ContextDocument.Architecture, ContextDocument.ExecutionFlow,
                  ContextDocument.Impact, ContextDocument.GitDiff])
        {
            var sections = items.Where(item => item.Document == document && item.Body.Length > 0).ToList();

            if (sections.Count == 0)
            {
                continue;
            }

            var text = new StringBuilder();

            // The patch is a patch: a Markdown heading over it would stop it applying.
            if (document != ContextDocument.GitDiff)
            {
                text.Append("# ").AppendLine(ContextDocuments.Title(document)).AppendLine();
            }

            foreach (var section in sections)
            {
                text.AppendLine(section.Body.TrimEnd()).AppendLine();
            }

            files.Add(new ContextPackFile(ContextDocuments.FileName(document), text.ToString()));
        }

        foreach (var file in PackFiles(items))
        {
            files.Add(new ContextPackFile(file.PackPath, file.Text));
        }

        return files;
    }

    /// <summary>Every file the pack carries, once each, in a stable order.</summary>
    private static IReadOnlyList<ContextFile> PackFiles(IReadOnlyList<ContextItem> items) => items
        .SelectMany(item => item.Files)
        .DistinctBy(file => file.RelativePath)
        .OrderBy(file => file.IsTest)
        .ThenBy(file => file.RelativePath, StringComparer.Ordinal)
        .ToList();

    /// <summary>
    /// <c>Task.md</c>: what is being asked, and an account of everything in the pack with
    /// the reason it is there.
    /// </summary>
    private string Task(IReadOnlyList<ContextItem> items)
    {
        var text = new StringBuilder()
            .AppendLine("# Task").AppendLine()
            .AppendLine(string.IsNullOrWhiteSpace(TaskText)
                ? "_No task was described. Say what you want changed, then read the context below._"
                : TaskText.Trim())
            .AppendLine()
            .AppendLine("## What is in this pack").AppendLine()
            .AppendLine(
                "Assembled by CodeAtlas from a local semantic index of this solution. Every entry " +
                "below is a compiler-derived fact or a Git fact, with the reason it was included. " +
                "Source files appear once each, under the folders named at the end.")
            .AppendLine();

        foreach (var item in items)
        {
            text.Append("- **").Append(item.KindLabel).Append(" — ").Append(item.Title).Append("**: ")
                .AppendLine(item.Reason);

            if (item.Document != ContextDocument.Task)
            {
                text.Append("  See `").Append(ContextDocuments.FileName(item.Document)).AppendLine("`.");
            }
        }

        if (items.Count == 0)
        {
            text.AppendLine("_Nothing has been added yet._");
        }

        var sections = items.Where(item => item.Document == ContextDocument.Task && item.Body.Length > 0).ToList();

        foreach (var section in sections)
        {
            text.AppendLine().AppendLine(section.Body.TrimEnd());
        }

        var files = PackFiles(items);

        if (files.Count > 0)
        {
            text.AppendLine().AppendLine("## Files in this pack").AppendLine();

            foreach (var file in files)
            {
                text.Append("- `").Append(file.PackPath).AppendLine("`");
            }
        }

        return text.ToString();
    }

    private ContextSize Measure(IReadOnlyList<ContextItem> items) => Render(items)
        .Aggregate(ContextSize.Zero, (total, file) => total + file.Size);

    // ---- reading the index --------------------------------------------------

    /// <summary>
    /// The composition behind an endpoint: what it is handed, and what the container puts
    /// behind that, breadth-first so the nearest hop is listed first.
    /// </summary>
    private List<(IndexedSymbol Symbol, int Depth)> WalkFlow(HttpEndpoint endpoint)
    {
        var seeds = endpoint.FlowSeeds;
        var seen = new HashSet<long>(seeds);
        var flow = new List<(IndexedSymbol, int)>();
        var frontier = seeds.ToList();

        for (var depth = 1; depth <= MaxFlowDepth && frontier.Count > 0; depth++)
        {
            var symbols = _database.GetSymbols(frontier);

            foreach (var symbol in symbols)
            {
                flow.Add((symbol, depth));
            }

            if (flow.Count >= MaxRelatedSymbols)
            {
                break;
            }

            frontier = _database.GetDependencies(frontier, CompositionKinds)
                .Where(seen.Add)
                .ToList();
        }

        return flow.Take(MaxRelatedSymbols).ToList();
    }

    /// <summary>Turns relation links into the declarations behind them, capped.</summary>
    private List<IndexedSymbol> Resolve(IEnumerable<SymbolLink> links)
    {
        var ids = links
            .Select(link => link.SymbolId)
            .OfType<long>()
            .Distinct()
            .Take(MaxRelatedSymbols)
            .ToList();

        return ids.Count == 0 ? [] : _database.GetSymbols(ids).ToList();
    }

    private IReadOnlyList<ContextFile> Files(IndexedSymbol symbol) =>
        ReadFile(symbol.FilePath, isTest: false) is { } file ? [file] : [];

    private IReadOnlyList<ContextFile> Files(IReadOnlyList<IndexedSymbol> symbols) => symbols
        .Select(symbol => ReadFile(symbol.FilePath, isTest: false))
        .OfType<ContextFile>()
        .DistinctBy(file => file.RelativePath)
        .ToList();

    /// <summary>
    /// Reads a file for the pack. A file that has moved or cannot be read costs only
    /// itself: the entry still describes the relation, which is the part the index knows.
    /// </summary>
    private ContextFile? ReadFile(string? absolutePath, bool isTest)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
        {
            return null;
        }

        try
        {
            return new ContextFile(RelativePath(absolutePath), File.ReadAllText(absolutePath), isTest);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The path a file is filed under, relative to the workspace root.
    /// </summary>
    /// <remarks>
    /// A file outside the root — a linked file, or a project elsewhere on disk — keeps its
    /// own path with the volume root stripped, so it stays unique and stays inside the
    /// exported folder rather than escaping it.
    /// </remarks>
    private string RelativePath(string absolutePath)
    {
        var relative = Path.GetRelativePath(_root, absolutePath);

        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            relative = absolutePath[(Path.GetPathRoot(absolutePath)?.Length ?? 0)..];
        }

        return relative.Replace('\\', '/').TrimStart('/');
    }

    // ---- text ---------------------------------------------------------------

    private static string Section(string heading, string lead, IReadOnlyList<IndexedSymbol> symbols)
    {
        var text = new StringBuilder()
            .Append("## ").AppendLine(heading)
            .AppendLine()
            .AppendLine(lead)
            .AppendLine();

        foreach (var symbol in symbols)
        {
            text.Append("- `").Append(symbol.Display).Append('`');

            if (symbol.ContainerFullyQualifiedName is { Length: > 0 } container)
            {
                text.Append(" in ").Append(container);
            }

            text.Append(" — ").AppendLine(Origin(symbol));
        }

        return text.ToString();
    }

    private static void AppendImpacted(StringBuilder body, string heading, IReadOnlyList<ImpactedSymbol> symbols)
    {
        if (symbols.Count == 0)
        {
            return;
        }

        body.AppendLine().Append("### ").AppendLine(heading).AppendLine();

        foreach (var impacted in symbols.Take(MaxRelatedSymbols))
        {
            body.Append("- `").Append(impacted.Symbol.Display).Append("` — ")
                .Append(impacted.Distance).Append(" hop(s) — ")
                .AppendLine(Origin(impacted.Symbol));
        }

        More(body, symbols.Count, MaxRelatedSymbols, "more");
    }

    private static void More(StringBuilder body, int total, int shown, string noun)
    {
        if (total > shown)
        {
            body.Append("- … and ").Append(total - shown).Append(' ').Append(noun)
                .AppendLine(total - shown == 1 ? " not listed." : "s not listed.");
        }
    }

    private static string Origin(IndexedSymbol symbol) => Origin(symbol.FilePath, symbol.Line);

    private static string Origin(string? filePath, int? line) => filePath is { Length: > 0 } path
        ? $"{Path.GetFileName(path)}:{line?.ToString(CultureInfo.InvariantCulture) ?? "?"}"
        : "no source location";

    private static string Plural(int count, string noun, string? plural = null) =>
        count == 1 ? $"1 {noun}" : $"{count} {plural ?? noun + "s"}";

    private static string Counted(int shown, int total, string noun, string tail) => total > shown
        ? $"{Plural(total, noun)} {tail} The nearest {shown} are listed."
        : $"{Plural(shown, noun)} {tail}";
}
