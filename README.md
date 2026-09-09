# CodeAtlas.NET

A local-first, read-only semantic explorer for C#/.NET solutions. It loads a solution with
Roslyn, indexes the declarations and exact semantic relations into a local SQLite file, and
lets you search, inspect and map them offline.

**MVP 6** — the semantic code map, how an ASP.NET Core application is composed, where it
meets everything outside itself, and which tests hold it in place. Declarations, calls,
references, implementations, inheritance and type dependencies; a bounded graph of the
neighbourhood around any symbol; DI registrations and the HTTP endpoints they are wired
behind; the EF Core model and what reads and writes it; configuration keys and the options
types they bind; the infrastructure boundaries the application crosses; and the xUnit,
NUnit and MSTest tests that exercise any symbol — including the ones a working-tree diff
has just changed. No runtime tracing or coverage, Kubernetes analysis, message-broker
topology, AI, embeddings, impact scoring or context export.

## Running

```
dotnet run --project src/CodeAtlas.Desktop
```

Open a `.sln`, `.slnx` or `.csproj`, or point it at a directory and it resolves the best
candidate. The first open indexes; later opens reuse the cache until you press **Reindex**.

Select a symbol anywhere — the explorer, search results, or a relation in the details pane —
and **Graph** maps its neighbourhood: callers, callees, implementations, inheritance and the
types its signature depends on. Depth is 1 by default and configurable; edge kinds filter
independently; a node with more neighbours than are shown offers them behind a `+n` button.

**Endpoints** lists the HTTP entry points found in source. Select one and the graph opens on
its flow — the action, the controller, the services it injects, and what the container
resolves those to:

```
GET /users/{id}  ->  UsersController.Get  ->  IUserService  ->  UserService  ->  IUserRepository
```

**Infrastructure** lists the three things an application depends on that are not code in it:
its EF Core entities with the contexts, tables and migrations behind them; the configuration
keys and options types it binds; and the HTTP, gRPC, cache, broker, storage and file-system
boundaries it crosses. Select a row and the graph opens on what reaches it, so the flow can
be read from either end:

```
GET /orders/{id}  ->  OrdersController  ->  IOrderService  ->  IOrderRepository
                  ->  SqlOrderRepository  ->  ShopContext  ->  Order (sales.orders)

OrderService  ->  ICrmClient  ->  CrmWebServicesClient  ->  HttpClient "crm"  ->  CrmOptions
```

The graph filters by the same three cuts — **Database**, **Configuration**, **External
services** — alongside the existing ones, and a node standing on infrastructure is badged
with the table it maps to or the technology it talks to.

Any symbol lists its **Related tests** in the details pane, and **Changes** reads the
working tree against `HEAD` to say what an edit means: every declaration the diff touched,
the tests over each one, and a warning for a changed method nothing was found to cover.

```
UserService.Get(int id)             src/Shop/UserService.cs:19        2 tests
    exact     UserServiceTests.Get_returns_the_user()   calls Get(int id)
    exact     UserServiceTests.Archive_is_quiet()       constructs UserService

ReportService.Total()               src/Shop/ReportService.cs:5       1 probable
    probable  ReportServiceTests.Totals_are_produced()  named after ReportService, in a
                                                        project that depends on it

PricingEngine.Quote(decimal net)    src/Shop/Legacy/Pricing.cs:5      no tests found
```

```
dotnet test              # 163 tests, including a real end-to-end indexing run
```

Requires the .NET 10 SDK at run time: Roslyn loads projects through the SDK's MSBuild.

## Layout

| Project | Contains |
| --- | --- |
| `src/CodeAtlas.Core` | Everything: workspace resolution, Roslyn analysis, the SQLite index. No UI types. |
| `src/CodeAtlas.Desktop` | Avalonia window and view models. |
| `tests/CodeAtlas.Core.Tests` | Analysis, storage and end-to-end tests. |

## Decisions worth knowing

**The index is a cache, not a database.** It lives in `~/.local/share/CodeAtlas.NET/index`
(local app data), never beside the repository. `IndexSchema.Version` is stamped into every
file; opening a file written by a different version deletes it and reindexes, so there is no
migration code to maintain. A corrupt file is treated the same way.

**A call is stored as a call, not as a reference.** Every invocation and object creation is
recorded as `Calls`, and the callee's own name is then skipped when plain references are
collected, so the pair is never stored twice. "Find references" unions the two, because a
call site is a reference; "find callees" reads only the calls. The same split is what makes a
call graph queryable without a second analysis pass.

**Signature dependencies are unwrapped.** A parameter or return type contributes an edge per
type declared in the solution that it is written out of, so `Task<Order[]>` depends on
`Order`. A constructor's parameter edges are exactly its construction dependencies, which is
why there is no separate relation kind for them.

**Every edge carries its provenance.** A binding Roslyn resolved to exactly one symbol is
`Exact`. A binding it could not resolve, but which has a single candidate, is kept as
`Inferred` — that keeps a project with errors mapped without dressing a guess up as compiler
truth. Nothing heuristic is ever marked `Exact`.

**The graph is bounded on three axes, not one.** `NeighborhoodBuilder` walks depth, total
nodes and neighbours-per-node, and all three are needed: depth alone does not bound a graph in
which one symbol has ten thousand callers. The per-node cap is deliberately low so the first
look at a hub is readable, and expanding a node by hand lifts it for that node. The walk reads
the index and never Roslyn, so a graph query costs a few indexed lookups rather than a
recompilation.

**Composition is read, never executed.** `ServiceRegistrationCollector` recognises a
registration by binding the call to an extension method on `IServiceCollection` whose name
carries a lifetime — not by matching text, and not by where the call sits, which is why a
registration inside a project's own `AddInfrastructure` extension is found exactly like one
in `Program.cs`. What a factory returns is recovered only when its body is a single object
creation, and is always `Inferred`: the factory may branch, and CodeAtlas does not run it.

**A registration becomes a graph edge.** Each one contributes a `Resolves` edge from the
service type to its implementation, and every type contributes `Injects` edges to what its
constructors take. Those two are what let the ordinary MVP 1 walk cross from an interface to
the concrete type behind it, so the endpoint flow is the same graph with a different root
rather than a second traversal.

**`Injects` duplicates the constructor's own parameter edges on purpose.** The edge is
attributed to the *type* rather than to its constructor, so a type reaches the services it
depends on in one hop. Without it the graph would need a constructor node in the middle of
every composition path.

**Only statically resolvable routing is listed.** Attribute routing on controllers, with
`[controller]` and `[action]` expanded the way the framework expands them, and Minimal API
registrations whose template is a constant — including the accumulated `MapGroup` prefixes,
followed through the local a group is usually held in. Conventional routing depends on the
route table assembled at start-up and is not guessed at, so an endpoint that is listed is
one that exists.

**Relations are resolved by name, after the fact.** A project can reference a symbol from a
project that has not been indexed yet, so `SymbolCollector` emits relations naming both
endpoints and `IndexWriteSession.Complete` resolves them to row ids in one pass once every
symbol is present. Targets outside the solution keep their name and simply have no id — that
is how `System.Object` shows up as a base type without being indexed.

**References come from the per-document semantic model, not `SymbolFinder`.** Finding
references across a whole solution is the expensive operation CodeAtlas is avoiding at this
stage. Instead each document is walked once against the semantic model that is being built
anyway, and only bindings the compiler resolved exactly are kept — never `CandidateSymbols`.
Only targets declared inside the solution are recorded; edges into the BCL would dwarf the
index without saying anything about your code.

**A reference belongs to the member it was written in.** An accessor's references are
attributed to its property (via `AssociatedSymbol`, since an accessor's `ContainingSymbol` is
the type), a local function's to its containing method, and a field's declared type to the
field.

**Partial failure is normal.** A project MSBuild cannot load is reported as a diagnostic and
the rest are still indexed; a project whose compilation fails is stored as not-loaded; a file
that cannot be analysed costs only that file. Nothing aborts the run. Cancelling mid-rebuild
rolls the transaction back and leaves the previous index intact.

**Infrastructure is read from the framework's own types, never from how a call is spelled.**
`DbContext`, `DbSet<T>`, `IEntityTypeConfiguration<T>`, the EF metadata builders,
`MigrationBuilder`, `IConfiguration`, `IOptions<T>` and the client types of each recognised
technology are all matched by the fully qualified name the compiler bound. That is why a
`Repository.Add` on something that is not a `DbSet` is not mistaken for an insert, and why
recognising a new broker is a line in a table rather than a new analysis.

**Nothing about the database is reconstructed.** A table name is recorded when it was
written as a literal in `ToTable` or `[Table]`; when EF's naming convention decides it at
run time the mapping simply carries no table, and says so. Usage is coarse on purpose:
reaching a `DbSet` is a read unless the call is one of EF's add, update or remove verbs, and
a migration reports the tables its operations name rather than the schema it produces. No
SQL is assembled and no model is built.

**An entity relationship is the navigation property that already exists.** A property whose
type is another entity is a relationship, and the compiler recorded that as a `ReturnType`
edge while collecting signatures. `IndexWriteSession.Complete` turns those into
entity-to-entity edges in one join, so conventional models map without a second analysis
pass; fluent `HasOne`/`HasMany` configuration contributes the same edge directly, read from
the builder type the call hands back, which names both ends.

**Configuration and infrastructure belong to the type, not to the member.** A read is
attributed to the class it was written in, for the same reason `Injects` is: configuration
and a Redis connection are properties of a component, and the graph should reach them in one
hop rather than through a method node. Only statically discoverable names are kept — a key
composed at run time is left out, and a section path is followed back along the receiver
chain only as far as that chain is literal.

**A typed client is a registration like any other.** `AddHttpClient<IClient, Client>()` and
`AddGrpcClient<T>()` are read by `ServiceRegistrationCollector` as transient registrations,
which is the lifetime the client factories give them. Without that the flow from an
application service to the client behind an interface would stop at the interface. Their
remaining arguments configure the client rather than build the service, so only their type
arguments are read.

**One row per component and technology.** A class that injects a Redis multiplexer and then
calls through the database it hands back crosses one boundary, not two, so external
dependencies are keyed by consumer and technology and the most specific reading wins — a
typed-client registration, which knows the client's name, beats the bare injection of it.
The boundary itself is named by the technology's own client type where the compilation has
it, so every reading of Redis lands on the same symbol rather than on whichever extension
class declared the call.

**A boundary keeps its name even when it is outside the solution.** `UsesExternal` is stored
with an unresolved target the way `Inherits` is, so `System.Net.Http.HttpClient` is listed
against the client that uses it without being indexed. It is not drawn in the graph, which
only draws edges with both ends inside the index — the badge on the node is what carries it
there.

**"Reached from" is a composition walk, not a call walk.** Finding the endpoints above a
resource seeds on what names it, widened to the type that names it, and then follows only
`Injects` and `Resolves` backwards. That chain is what an endpoint's flow is actually made
of and it is short and narrow; following calls backwards would put half the solution in the
answer the first time it crossed a hot method.

**A test is an attribute, not a name.** A method is a test because the compiler bound one
of `Xunit`, `NUnit.Framework` or `Microsoft.VisualStudio.TestTools.UnitTesting`'s attributes
to it — the same rule that recognises a `DbSet` or an `IServiceCollection`. Attributes are
already indexed against every symbol, so detecting tests needs no collector, no table and no
second pass: a fixture is a type that contains one, and a test project is a project that
declares one.

**"Exact" and "probable" are two different claims, and the ladder keeps them apart.** A test
that calls the symbol, a fixture that constructs the class under test, and a fixture member
that names the symbol are edges the compiler recorded, so they are exact. A fixture named
after the type, corroborated or not by a project reference, and a fixture sitting in the
type's namespace are guesses, and are never dressed up as anything else. A fixture's own
members count towards the exact tiers because a test class builds its subject once — in a
field, a constructor or a setup method — and asserts on it from every test.

**The weakest signal is a fallback, not an addition.** Namespace and directory similarity
would attach every fixture in a mirrored namespace to every type in it, so it is dropped the
moment anything stronger is found. Nothing follows calls transitively either, for the reason
"reached from" does not: one hot method would put the whole suite in every answer.

**A diff is read into the index by span, and only the innermost declaration changed.** Every
symbol stores where its declaration ends as well as where it begins, and each one owns the
part of that span its own members do not claim. A changed line in a body therefore reports
the method, a changed attribute or base list reports the type, and neither reports the other.
A changed file the index has never seen is reported as unmapped rather than as untested:
"no tests" would be a statement about CodeAtlas rather than about the code.

**Git is asked for the diff; nothing else is asked of it.** One read-only `git diff
--unified=0`, run in the working tree. Zero context lines matter: a hunk with context would
drag the neighbouring declarations into the answer.

**`record struct` is indexed as `Record`,** matching the keyword that was written rather than
Roslyn's `TypeKind.Struct`.

**Fully qualified names are the symbol identity.** They include parameter types so overloads
stay distinct, and deliberately avoid keyword aliases (`System.Int32`, not `int`) so the names
are stable. The friendlier form is stored separately for display.

**`Microsoft.Build*` is referenced with `ExcludeAssets="runtime"`.** MSBuildLocator loads
MSBuild from the installed SDK at run time; shipping our own copies would shadow the SDK's and
break workspace loading.

**Reads are serialised inside `SymbolIndexDatabase`.** SQLite forbids concurrent use of one
connection, and the UI queries from the thread pool. A rebuild opens its own connection, so
WAL lets it write while readers still see the previous content.

**No ORM and no MVVM framework.** The queries are hand-written SQL over
`Microsoft.Data.Sqlite`, and change notification is a ~30 line `ObservableObject`. Neither
dependency would have earned its place at this size.

## Opening source at a line

Clicking **Open** hands the file to the OS handler, which cannot position a caret. Set
`CODEATLAS_EDITOR` to a command line with `{file}` and `{line}` placeholders to jump to the
declaration instead:

```
CODEATLAS_EDITOR='code --goto {file}:{line}'
CODEATLAS_EDITOR='rider --line {line} {file}'
```

## The repository is never written to

CodeAtlas opens the analysed solution read-only and writes only to its own cache directory.
Git detection is a walk up the directory tree looking for `.git`, and the only Git command
ever run is `git diff`, which reads.
