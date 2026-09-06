using CodeAtlas.Core.Analysis;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;
using Microsoft.Data.Sqlite;

namespace CodeAtlas.Core.Tests;

public class SymbolIndexDatabaseTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("codeatlas-db").FullName;
    private const string Source = "/repo/App.sln";

    private string DatabasePath => Path.Combine(_root, "index.db");

    private static IndexedSymbol Symbol(
        IndexedSymbolKind kind,
        string name,
        string fullyQualifiedName,
        params string[] attributes) => new()
        {
            Kind = kind,
            Name = name,
            FullyQualifiedName = fullyQualifiedName,
            Display = name,
            Namespace = "App",
            FilePath = "/repo/App.cs",
            Line = 10,
            Column = 5,
            Accessibility = "Public",
            Attributes = attributes,
        };

    /// <summary>Writes a small fixed index: an interface, a class implementing it, and a method.</summary>
    private void Seed()
    {
        using var database = SymbolIndexDatabase.Open(DatabasePath);
        using var session = database.BeginRebuild();

        var projectId = session.AddProject(new IndexedProject { Name = "App", AssemblyName = "App" });

        session.AddSymbols(projectId,
        [
            Symbol(IndexedSymbolKind.Interface, "IWidget", "App.IWidget"),
            Symbol(IndexedSymbolKind.Class, "Widget", "App.Widget", "App.MarkedAttribute"),
            Symbol(IndexedSymbolKind.Method, "Run", "App.Widget.Run()") with
            {
                ContainerFullyQualifiedName = "App.Widget",
            },
        ]);

        session.AddRelations(projectId,
        [
            new PendingRelation("App.Widget", RelationKind.Implements, "App.IWidget", "IWidget"),
            new PendingRelation("App.Widget", RelationKind.Inherits, "System.Object", "object"),
            new PendingRelation("App.Widget.Run()", RelationKind.References, "App.IWidget", "IWidget"),
        ]);

        session.AddDiagnostics([new IndexDiagnostic(Model.DiagnosticSeverity.Warning, "App", "something odd")]);
        session.Complete(Source);
    }

    [Fact]
    public void Records_metadata_after_a_completed_rebuild()
    {
        Seed();

        using var database = SymbolIndexDatabase.Open(DatabasePath);
        var metadata = database.ReadMetadata();

        Assert.NotNull(metadata);
        Assert.Equal(IndexSchema.Version, metadata.SchemaVersion);
        Assert.Equal(Source, metadata.SourcePath);
        Assert.Equal(1, metadata.ProjectCount);
        Assert.Equal(3, metadata.SymbolCount);
        Assert.True(database.HasUsableIndexFor(Source));
        Assert.False(database.HasUsableIndexFor("/repo/Other.sln"));
    }

    [Fact]
    public void Reports_no_metadata_for_a_freshly_created_file()
    {
        using var database = SymbolIndexDatabase.Open(DatabasePath);

        Assert.Null(database.ReadMetadata());
        Assert.False(database.HasUsableIndexFor(Source));
    }

    [Fact]
    public void Resolves_relation_targets_inside_the_index_and_keeps_external_ones_by_name()
    {
        Seed();

        using var database = SymbolIndexDatabase.Open(DatabasePath);
        var widget = database.Search("Widget").Single(s => s.Kind == IndexedSymbolKind.Class);
        var details = database.GetDetails(widget.Id);

        Assert.NotNull(details);

        var @interface = Assert.Single(details.Interfaces);
        Assert.True(@interface.IsNavigable);
        Assert.Equal("App.IWidget", @interface.FullyQualifiedName);

        var baseType = Assert.Single(details.BaseTypes);
        Assert.False(baseType.IsNavigable);
        Assert.Equal("System.Object", baseType.FullyQualifiedName);
    }

    [Fact]
    public void Exposes_incoming_relations()
    {
        Seed();

        using var database = SymbolIndexDatabase.Open(DatabasePath);
        var @interface = database.Search("IWidget").Single();
        var details = database.GetDetails(@interface.Id);

        Assert.NotNull(details);
        Assert.Equal("Widget", Assert.Single(details.Implementors).Display);
        Assert.Equal("Run", Assert.Single(details.ReferencedBy).Display);
        Assert.Equal(1, details.ReferencedByTotal);
    }

    [Fact]
    public void Round_trips_attributes_and_source_location()
    {
        Seed();

        using var database = SymbolIndexDatabase.Open(DatabasePath);
        var widget = database.Search("Widget").Single(s => s.Kind == IndexedSymbolKind.Class);
        var details = database.GetDetails(widget.Id);

        Assert.NotNull(details);
        Assert.Equal(["App.MarkedAttribute"], details.Symbol.Attributes);
        Assert.Equal("/repo/App.cs", details.Symbol.FilePath);
        Assert.Equal(10, details.Symbol.Line);
        Assert.Equal(5, details.Symbol.Column);
        Assert.Equal("App", details.Symbol.ProjectName);
    }

    [Fact]
    public void Ranks_exact_matches_before_prefix_matches_before_substring_matches()
    {
        using (var database = SymbolIndexDatabase.Open(DatabasePath))
        {
            using var session = database.BeginRebuild();
            var projectId = session.AddProject(new IndexedProject { Name = "App" });

            session.AddSymbols(projectId,
            [
                Symbol(IndexedSymbolKind.Class, "MyWidgetFactory", "App.MyWidgetFactory"),
                Symbol(IndexedSymbolKind.Class, "WidgetFactory", "App.WidgetFactory"),
                Symbol(IndexedSymbolKind.Class, "Widget", "App.Widget"),
            ]);

            session.Complete(Source);
        }

        using var reader = SymbolIndexDatabase.Open(DatabasePath);

        Assert.Equal(
            ["Widget", "WidgetFactory", "MyWidgetFactory"],
            reader.Search("Widget").Select(s => s.Name));
    }

    [Fact]
    public void Treats_search_wildcards_as_literal_text()
    {
        using (var database = SymbolIndexDatabase.Open(DatabasePath))
        {
            using var session = database.BeginRebuild();
            var projectId = session.AddProject(new IndexedProject { Name = "App" });

            session.AddSymbols(projectId,
            [
                Symbol(IndexedSymbolKind.Field, "_value", "App.C._value"),
                Symbol(IndexedSymbolKind.Field, "other", "App.C.other"),
            ]);

            session.Complete(Source);
        }

        using var reader = SymbolIndexDatabase.Open(DatabasePath);

        // "_" is a LIKE wildcard; unescaped it would match "other" too.
        Assert.Equal(["_value"], reader.Search("_value").Select(s => s.Name));
        Assert.Empty(reader.Search("%"));
    }

    [Fact]
    public void Navigates_the_project_tree()
    {
        Seed();

        using var database = SymbolIndexDatabase.Open(DatabasePath);
        var project = Assert.Single(database.GetProjects());

        Assert.Equal("App", project.Name);
        Assert.True(project.Loaded);
        Assert.Equal(["App"], database.GetNamespaces(project.Id));
        Assert.Equal(["IWidget", "Widget"], database.GetTypes(project.Id, "App").Select(s => s.Name));
        Assert.Equal(["Run"], database.GetMembers("App.Widget").Select(s => s.Name));
    }

    [Fact]
    public void Persists_diagnostics()
    {
        Seed();

        using var database = SymbolIndexDatabase.Open(DatabasePath);
        var diagnostic = Assert.Single(database.GetDiagnostics());

        Assert.Equal(Model.DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("App", diagnostic.Project);
        Assert.Equal("something odd", diagnostic.Message);
    }

    [Fact]
    public void Replaces_all_previous_content_on_rebuild()
    {
        Seed();

        using (var database = SymbolIndexDatabase.Open(DatabasePath))
        {
            using var session = database.BeginRebuild();
            var projectId = session.AddProject(new IndexedProject { Name = "Other" });
            session.AddSymbols(projectId, [Symbol(IndexedSymbolKind.Class, "Fresh", "Other.Fresh")]);
            session.Complete("/repo/Other.sln");
        }

        using var reader = SymbolIndexDatabase.Open(DatabasePath);

        Assert.Empty(reader.Search("Widget"));
        Assert.Equal("Fresh", Assert.Single(reader.Search("Fresh")).Name);
        Assert.Empty(reader.GetDiagnostics());
        Assert.Equal("/repo/Other.sln", reader.ReadMetadata()?.SourcePath);
    }

    [Fact]
    public void Leaves_the_previous_index_intact_when_a_rebuild_is_abandoned()
    {
        Seed();

        using (var database = SymbolIndexDatabase.Open(DatabasePath))
        {
            using var session = database.BeginRebuild();
            var projectId = session.AddProject(new IndexedProject { Name = "Half" });
            session.AddSymbols(projectId, [Symbol(IndexedSymbolKind.Class, "Half", "Half.Half")]);
            // Never completed: e.g. indexing was cancelled or crashed.
        }

        using var reader = SymbolIndexDatabase.Open(DatabasePath);

        Assert.Empty(reader.Search("Half"));
        Assert.Equal(3, reader.ReadMetadata()?.SymbolCount);
    }

    [Fact]
    public void Discards_a_cache_written_by_a_different_schema_version()
    {
        Seed();
        SqliteConnection.ClearAllPools();

        using (var connection = new SqliteConnection($"Data Source={DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE schema_info SET value = '999' WHERE key = 'schema_version'";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();

        using var database = SymbolIndexDatabase.Open(DatabasePath);

        Assert.Null(database.ReadMetadata());
        Assert.Empty(database.Search("Widget"));
    }

    [Fact]
    public void Recreates_a_corrupt_cache_file()
    {
        File.WriteAllText(DatabasePath, "this is not a database");

        using var database = SymbolIndexDatabase.Open(DatabasePath);

        Assert.Null(database.ReadMetadata());
    }

    /// <summary>
    /// A second fixture for the navigation queries: an interface with an implementation,
    /// an override, a call and a constructor that takes a dependency.
    /// </summary>
    private void SeedNavigation()
    {
        using var database = SymbolIndexDatabase.Open(DatabasePath);
        using var session = database.BeginRebuild();

        var projectId = session.AddProject(new IndexedProject { Name = "App", AssemblyName = "App" });

        session.AddSymbols(projectId,
        [
            Symbol(IndexedSymbolKind.Interface, "IClock", "App.IClock"),
            Symbol(IndexedSymbolKind.Class, "Clock", "App.Clock"),
            Symbol(IndexedSymbolKind.Class, "FastClock", "App.FastClock"),
            Symbol(IndexedSymbolKind.Method, "Now", "App.IClock.Now()") with
            {
                ContainerFullyQualifiedName = "App.IClock",
            },
            Symbol(IndexedSymbolKind.Method, "Now", "App.Clock.Now()") with
            {
                ContainerFullyQualifiedName = "App.Clock",
            },
            Symbol(IndexedSymbolKind.Method, "Now", "App.FastClock.Now()") with
            {
                ContainerFullyQualifiedName = "App.FastClock",
            },
            Symbol(IndexedSymbolKind.Class, "Service", "App.Service"),
            Symbol(IndexedSymbolKind.Constructor, ".ctor", "App.Service.Service(App.IClock)") with
            {
                ContainerFullyQualifiedName = "App.Service",
            },
            Symbol(IndexedSymbolKind.Method, "Run", "App.Service.Run()") with
            {
                ContainerFullyQualifiedName = "App.Service",
            },
        ]);

        session.AddRelations(projectId,
        [
            new PendingRelation("App.Clock", RelationKind.Implements, "App.IClock", "IClock"),
            new PendingRelation("App.Clock.Now()", RelationKind.Implements, "App.IClock.Now()", "Now()"),
            new PendingRelation("App.FastClock", RelationKind.Inherits, "App.Clock", "Clock"),
            new PendingRelation("App.FastClock.Now()", RelationKind.Overrides, "App.Clock.Now()", "Now()"),
            new PendingRelation("App.Service.Run()", RelationKind.Calls, "App.IClock.Now()", "Now()"),
            new PendingRelation("App.Service.Run()", RelationKind.References, "App.Clock", "Clock"),
            new PendingRelation(
                "App.Service.Service(App.IClock)", RelationKind.ParameterType, "App.IClock", "IClock"),

            // The collector attributes a constructor's parameters to the declaring type as
            // well, which is the edge the composition graph and this query both read.
            new PendingRelation("App.Service", RelationKind.Injects, "App.IClock", "IClock"),
            new PendingRelation(
                "App.Service.Run()", RelationKind.Calls, "App.Missing()", "Missing()", RelationProvenance.Inferred),
        ]);

        session.Complete(Source);
    }

    private long IdOf(SymbolIndexDatabase database, string fullyQualifiedName) =>
        database.Search(fullyQualifiedName.Split('.')[^1].Split('(')[0])
            .Single(symbol => symbol.FullyQualifiedName == fullyQualifiedName)
            .Id;

    [Fact]
    public void Finds_callers_and_callees()
    {
        SeedNavigation();

        using var database = SymbolIndexDatabase.Open(DatabasePath);
        var run = IdOf(database, "App.Service.Run()");
        var now = IdOf(database, "App.IClock.Now()");

        Assert.Contains(database.FindCallees(run), link => link.FullyQualifiedName == "App.IClock.Now()");
        Assert.Equal(["Run"], database.FindCallers(now).Select(link => link.Display));
    }

    [Fact]
    public void Finds_references_including_call_sites()
    {
        SeedNavigation();

        using var database = SymbolIndexDatabase.Open(DatabasePath);

        // Reading a type is a reference; invoking a member is stored as a call but is
        // still a reference to it.
        Assert.Equal(["Run"], database.FindReferences(IdOf(database, "App.Clock")).Select(l => l.Display));
        Assert.Equal(["Run"], database.FindReferences(IdOf(database, "App.IClock.Now()")).Select(l => l.Display));
    }

    [Fact]
    public void Finds_implementations_of_a_type_and_of_a_member()
    {
        SeedNavigation();

        using var database = SymbolIndexDatabase.Open(DatabasePath);

        Assert.Equal(
            ["Clock"],
            database.FindImplementations(IdOf(database, "App.IClock")).Select(link => link.Display));

        // An interface member's implementations and the overrides of those are the set an
        // editor's "go to implementation" offers.
        Assert.Equal(
            ["Now"],
            database.FindImplementations(IdOf(database, "App.IClock.Now()")).Select(link => link.Display));
        Assert.Equal(
            ["Now"],
            database.FindImplementations(IdOf(database, "App.Clock.Now()")).Select(link => link.Display));
    }

    [Fact]
    public void Finds_derived_types()
    {
        SeedNavigation();

        using var database = SymbolIndexDatabase.Open(DatabasePath);

        Assert.Equal(
            ["FastClock"],
            database.FindDerivedTypes(IdOf(database, "App.Clock")).Select(link => link.Display));
    }

    [Fact]
    public void Reports_the_parameter_types_of_a_types_constructors_as_its_dependencies()
    {
        SeedNavigation();

        using var database = SymbolIndexDatabase.Open(DatabasePath);
        var details = database.GetDetails(IdOf(database, "App.Service"));

        Assert.NotNull(details);
        Assert.Equal(["IClock"], details.Injects.Select(link => link.Display));
    }

    [Fact]
    public void Round_trips_relation_provenance()
    {
        SeedNavigation();

        using var database = SymbolIndexDatabase.Open(DatabasePath);
        var calls = database.FindCallees(IdOf(database, "App.Service.Run()"));

        Assert.Equal(
            RelationProvenance.Exact,
            calls.Single(link => link.FullyQualifiedName == "App.IClock.Now()").Provenance);
        Assert.Equal(
            RelationProvenance.Inferred,
            calls.Single(link => link.FullyQualifiedName == "App.Missing()").Provenance);
    }


    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }
}
