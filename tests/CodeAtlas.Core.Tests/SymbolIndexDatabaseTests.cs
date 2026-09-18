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

    private static IndexedSymbol Symbol(IndexedSymbolKind kind, string name, string fullyQualifiedName) => new()
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
    };

    /// <summary>
    /// Writes a small fixed index: an interface with one implementation and one override
    /// of it, a service that calls the interface, and one call out of the solution.
    /// </summary>
    private void Seed()
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
            Symbol(IndexedSymbolKind.Method, "Run", "App.Service.Run()") with
            {
                ContainerFullyQualifiedName = "App.Service",
            },
        ]);

        session.AddRelations(projectId,
        [
            new PendingRelation("App.Clock.Now()", RelationKind.Implements, "App.IClock.Now()", "Now()"),
            new PendingRelation("App.FastClock.Now()", RelationKind.Overrides, "App.Clock.Now()", "Now()"),
            new PendingRelation("App.Service.Run()", RelationKind.Calls, "App.IClock.Now()", "Now()"),
            new PendingRelation(
                "App.Service.Run()", RelationKind.Calls, "System.Console.WriteLine()", "WriteLine()"),
        ]);

        session.AddDiagnostics([new IndexDiagnostic(Model.DiagnosticSeverity.Warning, "App", "something odd")]);
        session.Complete(Source);
    }

    private static long IdOf(SymbolIndexDatabase database, string containerFullyQualifiedName, string name) =>
        database.GetMembers(containerFullyQualifiedName).Single(symbol => symbol.Name == name).Id;

    private static long TypeIdOf(SymbolIndexDatabase database, string name) =>
        database.GetTypes(database.GetProjects().Single().Id, "App").Single(symbol => symbol.Name == name).Id;

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
        Assert.Equal(8, metadata.SymbolCount);
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
    public void Navigates_the_project_tree()
    {
        Seed();

        using var database = SymbolIndexDatabase.Open(DatabasePath);
        var project = Assert.Single(database.GetProjects());

        Assert.Equal("App", project.Name);
        Assert.True(project.Loaded);
        Assert.Equal(["App"], database.GetNamespaces(project.Id));
        Assert.Equal(
            ["Clock", "FastClock", "IClock", "Service"],
            database.GetTypes(project.Id, "App").Select(s => s.Name));
        Assert.Equal(["Now"], database.GetMembers("App.Clock").Select(s => s.Name));
    }

    [Fact]
    public void Round_trips_a_source_location()
    {
        Seed();

        using var database = SymbolIndexDatabase.Open(DatabasePath);
        var clock = database.GetSymbol(TypeIdOf(database, "Clock"));

        Assert.NotNull(clock);
        Assert.Equal("/repo/App.cs", clock.FilePath);
        Assert.Equal(10, clock.Line);
        Assert.Equal(5, clock.Column);
        Assert.Equal("App", clock.ProjectName);
    }

    [Fact]
    public void Reads_the_methods_a_member_calls_and_skips_the_ones_outside_the_index()
    {
        Seed();

        using var database = SymbolIndexDatabase.Open(DatabasePath);
        var callees = database.GetCallees(IdOf(database, "App.Service", "Run"));

        // System.Console.WriteLine was called too, and has no declaration to walk into.
        Assert.Equal(["App.IClock.Now()"], callees.Select(symbol => symbol.FullyQualifiedName));
    }

    [Fact]
    public void Reads_what_runs_in_place_of_an_interface_or_overridden_member()
    {
        Seed();

        using var database = SymbolIndexDatabase.Open(DatabasePath);

        Assert.Equal(
            ["App.Clock.Now()"],
            database.GetImplementations(IdOf(database, "App.IClock", "Now"))
                .Select(symbol => symbol.FullyQualifiedName));

        Assert.Equal(
            ["App.FastClock.Now()"],
            database.GetImplementations(IdOf(database, "App.Clock", "Now"))
                .Select(symbol => symbol.FullyQualifiedName));

        Assert.Empty(database.GetImplementations(IdOf(database, "App.FastClock", "Now")));
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
        var project = Assert.Single(reader.GetProjects());

        Assert.Equal("Other", project.Name);
        Assert.Equal("Fresh", Assert.Single(reader.GetTypes(project.Id, "App")).Name);
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

        Assert.Equal("App", Assert.Single(reader.GetProjects()).Name);
        Assert.Equal(8, reader.ReadMetadata()?.SymbolCount);
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
        Assert.Empty(database.GetProjects());
    }

    [Fact]
    public void Recreates_a_corrupt_cache_file()
    {
        File.WriteAllText(DatabasePath, "this is not a database");

        using var database = SymbolIndexDatabase.Open(DatabasePath);

        Assert.Null(database.ReadMetadata());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }
}
