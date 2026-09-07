using CodeAtlas.Core.Analysis;
using CodeAtlas.Core.Git;
using CodeAtlas.Core.Graph;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Tests;

/// <summary>
/// The MVP 4 acceptance path against a real Git working tree: change a method, and
/// CodeAtlas names that method as changed and leads from the diff into its semantic
/// dependencies.
/// </summary>
/// <remarks>
/// The index is built from the working tree <em>after</em> the edit, which is the real
/// order of events: CodeAtlas indexes the files on disk, and the diff against
/// <c>HEAD</c> is then in the same line numbering as the declarations it recorded.
/// </remarks>
public class RepositoryChangeTests : IDisposable
{
    private const string Baseline = """
        namespace Shop;

        public class Basket
        {
            private int _total;

            public void Add(int price)
            {
                _total += price;
            }

            public int Total()
            {
                return _total;
            }
        }
        """;

    private readonly GitWorkingTree _tree = new();
    private readonly string _cacheRoot = Directory.CreateTempSubdirectory("codeatlas-git-index").FullName;

    private string IndexPath => Path.Combine(_cacheRoot, "index.db");

    /// <summary>
    /// Indexes the files as they currently are on disk, then reads Git and maps it on.
    /// </summary>
    private async Task<(SymbolIndexDatabase Database, RepositoryChanges Changes)> AnalyseAsync(
        params string[] paths)
    {
        var data = await SymbolCollector.CollectAsync(
            TestProjectFactory.CreateFromFiles("Shop", paths),
            TestContext.Current.CancellationToken);

        using (var writer = SymbolIndexDatabase.Open(IndexPath))
        using (var session = writer.BeginRebuild())
        {
            var projectId = session.AddProject(data.Project);
            session.AddSymbols(projectId, data.Symbols);
            session.AddRelations(projectId, data.Relations);
            session.Complete(Path.Combine(_tree.Root, "Shop.csproj"));
        }

        var database = SymbolIndexDatabase.Open(IndexPath);
        var repository = GitRepository.Open(_tree.Root);
        Assert.NotNull(repository);

        return (database, RepositoryChangeAnalyzer.Analyze(
            repository,
            database,
            TestContext.Current.CancellationToken));
    }

    /// <summary>Commits the baseline, then edits the body of <c>Add</c> in place.</summary>
    private async Task<(SymbolIndexDatabase Database, RepositoryChanges Changes)> ChangeAddAsync()
    {
        var path = _tree.Write("src/Basket.cs", Baseline);
        _tree.Commit("baseline");

        _tree.Write("src/Basket.cs", Baseline.Replace(
            "        _total += price;",
            "        _total += price;\n        _total += Total();",
            StringComparison.Ordinal));

        return await AnalyseAsync(path);
    }

    // ---- the acceptance criterion -------------------------------------------

    [Fact]
    public async Task Names_the_changed_method()
    {
        var (database, changes) = await ChangeAddAsync();
        using var _ = database;

        var changed = changes.Symbols.Single(symbol =>
            symbol.Kind == IndexedSymbolKind.Method && symbol.Display.StartsWith("Add", StringComparison.Ordinal));

        Assert.Equal(SymbolChangeKind.Modified, changed.Change);
        Assert.Equal("Shop.Basket", changed.Container);
        Assert.True(changed.IsNavigable);
    }

    /// <summary>
    /// A change inside a member is also a change to the type that declares it, because the
    /// type's declaration span covers its members. Both are true and both are reported.
    /// </summary>
    [Fact]
    public async Task Names_the_changed_type_around_it()
    {
        var (database, changes) = await ChangeAddAsync();
        using var _ = database;

        Assert.Contains(
            changes.Symbols,
            symbol => symbol.Kind == IndexedSymbolKind.Class && symbol.Display == "Basket");
    }

    /// <summary>
    /// The point of mapping a diff onto symbols: from the changed method the index already
    /// knows what it calls, so the reader gets from "this line moved" to "this is what it
    /// reaches" without another analysis.
    /// </summary>
    [Fact]
    public async Task Leads_from_the_changed_method_to_its_semantic_dependencies()
    {
        var (database, changes) = await ChangeAddAsync();
        using var _ = database;

        var changed = changes.Symbols.Single(symbol =>
            symbol.Kind == IndexedSymbolKind.Method && symbol.Display.StartsWith("Add", StringComparison.Ordinal));

        var details = database.GetDetails(changed.SymbolId!.Value);

        Assert.NotNull(details);
        Assert.Contains(details.Calls, call => call.Display.StartsWith("Total", StringComparison.Ordinal));
    }

    /// <summary>
    /// The other end of the same relation: the method that was called is reachable from the
    /// change, which is how "what else does this affect" gets answered.
    /// </summary>
    [Fact]
    public async Task Reports_the_callers_of_a_symbol_the_change_reaches()
    {
        var (database, changes) = await ChangeAddAsync();
        using var _ = database;

        var total = database
            .GetSymbolsInFile(_tree.PathOf("src/Basket.cs"))
            .Single(symbol => symbol.Name == "Total");

        var callers = database.FindCallers(total.Id);

        Assert.Contains(callers, caller => caller.Display.StartsWith("Add", StringComparison.Ordinal));
        Assert.Contains(changes.SymbolIds, id => id != total.Id);
    }

    // ---- what is left alone -------------------------------------------------

    /// <summary>
    /// A member the diff did not touch is not reported, which is what makes the list worth
    /// reading: a whole-file answer would name everything.
    /// </summary>
    [Fact]
    public async Task Leaves_an_untouched_member_out()
    {
        var (database, changes) = await ChangeAddAsync();
        using var _ = database;

        Assert.DoesNotContain(
            changes.Symbols,
            symbol => symbol.Kind == IndexedSymbolKind.Field && symbol.Display == "_total");
    }

    [Fact]
    public async Task Reports_a_clean_working_tree_as_unchanged()
    {
        var path = _tree.Write("src/Basket.cs", Baseline);
        _tree.Commit("baseline");

        var (database, changes) = await AnalyseAsync(path);
        using var _ = database;

        Assert.True(changes.Status.IsClean);
        Assert.Empty(changes.Symbols);
        Assert.Empty(changes.Diffs);
    }

    // ---- status ------------------------------------------------------------

    [Fact]
    public async Task Reads_the_current_branch()
    {
        var path = _tree.Write("src/Basket.cs", Baseline);
        _tree.Commit("baseline");

        var (database, changes) = await AnalyseAsync(path);
        using var _ = database;

        Assert.Equal("work", changes.Status.Branch);
        Assert.False(changes.Status.IsDetached);
        Assert.NotNull(changes.Status.Head);
    }

    /// <summary>
    /// Staged and unstaged are separate facts about the same path, and a file edited after
    /// being staged is both.
    /// </summary>
    [Fact]
    public async Task Separates_staged_from_unstaged_changes()
    {
        var path = _tree.Write("src/Basket.cs", Baseline);
        _tree.Commit("baseline");

        _tree.Write("src/Basket.cs", Baseline.Replace("_total;", "_total; // staged", StringComparison.Ordinal));
        _tree.Stage("src/Basket.cs");
        _tree.Write("src/Basket.cs", Baseline.Replace("_total;", "_total; // and then edited", StringComparison.Ordinal));

        var (database, changes) = await AnalyseAsync(path);
        using var _ = database;

        var file = Assert.Single(changes.Status.Files);
        Assert.True(file.IsStaged);
        Assert.True(file.IsUnstaged);
        Assert.Equal(GitFileChange.Modified, file.Change);
    }

    [Fact]
    public async Task Lists_an_untracked_file_and_treats_everything_in_it_as_added()
    {
        var basket = _tree.Write("src/Basket.cs", Baseline);
        _tree.Commit("baseline");

        var extra = _tree.Write("src/Coupon.cs", """
            namespace Shop;

            public class Coupon
            {
                public int Percent() => 10;
            }
            """);

        var (database, changes) = await AnalyseAsync(basket, extra);
        using var _ = database;

        Assert.Contains(changes.Status.Untracked, file => file.Path == "src/Coupon.cs");
        Assert.All(
            changes.Symbols.Where(symbol => symbol.FilePath == extra),
            symbol => Assert.Equal(SymbolChangeKind.Added, symbol.Change));
        Assert.Contains(changes.Symbols, symbol => symbol.Display == "Coupon");
    }

    // ---- added and removed --------------------------------------------------

    /// <summary>
    /// A declaration every line of which is new is an addition rather than a modification.
    /// The test is exact — it needs only the diff — which is why it is applied inside a
    /// file that already existed.
    /// </summary>
    [Fact]
    public async Task Calls_a_wholly_new_member_added_rather_than_modified()
    {
        var path = _tree.Write("src/Basket.cs", Baseline);
        _tree.Commit("baseline");

        _tree.Write("src/Basket.cs", Baseline.Replace(
            "    public int Total()",
            """
                public int Count()
                {
                    return 1;
                }

                public int Total()
            """.TrimStart(),
            StringComparison.Ordinal));

        var (database, changes) = await AnalyseAsync(path);
        using var _ = database;

        var added = changes.Symbols.Single(symbol => symbol.Display.StartsWith("Count", StringComparison.Ordinal));
        Assert.Equal(SymbolChangeKind.Added, added.Change);

        // The type around it changed rather than being added: it was already there.
        var type = changes.Symbols.Single(symbol => symbol.Kind == IndexedSymbolKind.Class);
        Assert.Equal(SymbolChangeKind.Modified, type.Change);
    }

    /// <summary>
    /// A deleted file's declarations are read from the baseline, because the index cannot
    /// hold what is no longer on disk. They are named, marked inferred, and are not
    /// navigable.
    /// </summary>
    [Fact]
    public async Task Names_the_declarations_a_deleted_file_took_with_it()
    {
        var basket = _tree.Write("src/Basket.cs", Baseline);
        _tree.Write("src/Coupon.cs", """
            namespace Shop;

            public class Coupon
            {
                public int Percent() => 10;
            }
            """);
        _tree.Commit("baseline");

        _tree.Delete("src/Coupon.cs");

        var (database, changes) = await AnalyseAsync(basket);
        using var _ = database;

        var removed = changes.Symbols.Where(symbol => symbol.Change == SymbolChangeKind.Removed).ToList();

        Assert.Contains(removed, symbol => symbol.Display == "Coupon");
        Assert.Contains(removed, symbol => symbol.Display.StartsWith("Percent", StringComparison.Ordinal));
        Assert.All(removed, symbol =>
        {
            Assert.False(symbol.IsNavigable);
            Assert.Equal(RelationProvenance.Inferred, symbol.Provenance);
        });
    }

    // ---- history ------------------------------------------------------------

    [Fact]
    public async Task Reads_recent_commits_and_the_history_of_one_file()
    {
        var path = _tree.Write("src/Basket.cs", Baseline);
        _tree.Commit("add the basket");
        _tree.Write("src/Coupon.cs", "namespace Shop; public class Coupon;");
        _tree.Commit("add a coupon");

        var (database, _) = await AnalyseAsync(path);
        using var __ = database;

        var repository = GitRepository.Open(_tree.Root);
        Assert.NotNull(repository);

        var recent = repository.GetRecentCommits(10, TestContext.Current.CancellationToken);
        Assert.Equal(["add a coupon", "add the basket"], recent.Select(commit => commit.Subject));
        Assert.All(recent, commit => Assert.Equal("CodeAtlas Test", commit.Author));

        var history = repository.GetFileHistory("src/Basket.cs", 10, TestContext.Current.CancellationToken);
        Assert.Equal(["add the basket"], history.Select(commit => commit.Subject));
    }

    [Fact]
    public async Task Reads_the_baseline_of_a_tracked_file_and_nothing_for_a_new_one()
    {
        var path = _tree.Write("src/Basket.cs", Baseline);
        _tree.Commit("baseline");
        _tree.Write("src/Coupon.cs", "namespace Shop; public class Coupon;");

        var (database, _) = await AnalyseAsync(path);
        using var __ = database;

        var repository = GitRepository.Open(_tree.Root);
        Assert.NotNull(repository);

        Assert.Contains(
            "public void Add(int price)",
            repository.GetBaselineText("src/Basket.cs", TestContext.Current.CancellationToken));
        Assert.Null(repository.GetBaselineText("src/Coupon.cs", TestContext.Current.CancellationToken));
    }

    // ---- the graph filter ---------------------------------------------------

    /// <summary>
    /// "Changed code" narrows which symbols the walk admits, so a neighbour that did not
    /// change drops out while the subject of the question stays.
    /// </summary>
    [Fact]
    public async Task Restricting_the_graph_to_changed_code_keeps_only_changed_symbols()
    {
        var basket = _tree.Write("src/Basket.cs", Baseline);
        var coupon = _tree.Write("src/Coupon.cs", """
            namespace Shop;

            public class Coupon
            {
                public int Apply(Basket basket) => basket.Total();
            }
            """);
        _tree.Commit("baseline");

        // Only Coupon changes, so Basket is a neighbour the filter should exclude.
        _tree.Write("src/Coupon.cs", """
            namespace Shop;

            public class Coupon
            {
                public int Apply(Basket basket) => basket.Total() + 1;
            }
            """);

        var (database, changes) = await AnalyseAsync(basket, coupon);
        using var _ = database;

        var apply = database
            .GetSymbolsInFile(coupon)
            .Single(symbol => symbol.Name == "Apply");

        var unrestricted = NeighborhoodBuilder.Build(database, apply.Id, new GraphOptions { Depth = 1 });
        Assert.Contains(unrestricted.Nodes, node => node.Symbol.Name == "Total");

        var restricted = NeighborhoodBuilder.Build(
            database,
            apply.Id,
            new GraphOptions { Depth = 1, RestrictTo = changes.SymbolIds });

        Assert.Contains(restricted.Nodes, node => node.Symbol.Id == apply.Id);
        Assert.DoesNotContain(restricted.Nodes, node => node.Symbol.Name == "Total");
        Assert.All(
            restricted.Nodes,
            node => Assert.Contains(node.Symbol.Id, changes.SymbolIds));
    }

    public void Dispose()
    {
        _tree.Dispose();

        try
        {
            Directory.Delete(_cacheRoot, recursive: true);
        }
        catch (IOException)
        {
            // Not worth failing a test over.
        }
    }
}
