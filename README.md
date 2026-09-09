# CodeAtlas.NET

A local-first, read-only semantic explorer for C#/.NET solutions. It loads a solution with
Roslyn, indexes the declarations and exact semantic relations into a local SQLite file, and
lets you search, inspect and map them offline.

**MVP 6** — the semantic code map, how an ASP.NET Core application is composed, where it
meets everything outside itself, how it differs from its Git baseline, what a change to any
of it can affect, and which tests hold it in place. Declarations, calls, references,
implementations, inheritance and type dependencies; a bounded graph of the neighbourhood
around any symbol; DI registrations and the HTTP endpoints they are wired behind; the EF
Core model and what reads and writes it; configuration keys and the options types they
bind; the infrastructure boundaries the application crosses; the working tree's own changes
mapped onto the symbols they touch; deterministic impact analysis over all of it; and the
xUnit, NUnit and MSTest tests that exercise any symbol, including the ones the working tree
has just changed. No runtime tracing or coverage, Kubernetes analysis, message-broker
topology, AI, embeddings or context export.

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

**Git Changes** answers "what have I changed, and what does it touch". It reads the branch,
the working-tree status, the staged and unstaged paths, the untracked ones and the diff
against `HEAD`, and then maps the diff's hunks onto the declarations they fall inside:

```
src/Basket.cs:8   Basket.Add(int)     modified   ->  calls Basket.Total()
                  Basket              modified       called by CheckoutService.Complete()
src/Coupon.cs     Coupon.Percent()    added
src/Legacy.cs     LegacyBasket        removed    (inferred)
```

Select a changed symbol and it opens the way an endpoint or an entity does — on its
callers, callees, implementations, the endpoints above it and the entities below it, which
the index already holds. **Files** shows Git's own account of each path with its diff, and
**History** the recent commits, or the selected file's own. The graph gains a **Changed
code** filter, which narrows it to the symbols the working tree changed.

**Impact** answers "what can this change affect". Start from a method, a type, an
interface, an endpoint, an entity or a symbol the working tree changed, and it walks the
recorded relations outwards:

```
IPricingService                                              High impact
  Direct callers: 3     Indirect callers: 5     Endpoints: 2
  Database entities: 2  External integrations: 1

  Risk surface                    5 of 7 risk properties present
  ! HTTP endpoints reachable      2 endpoints, including GET /orders/{id}
  ! Authorization involved        1 reachable endpoint requires authorization
  ! Persistence involved          2 entities, including Order, Invoice
  ! External integration          1 boundary, including HTTP crm
  ! Shared abstraction            It is an interface, 2 implementations
  · High fan-in                   3 direct callers; 5 or more counts as high
```

Direct and indirect effects stay apart, every impacted symbol carries its distance in
hops, and each risk property is shown with the fact behind it — present or not, so the
surface accounts for what it ruled out as well as what it found.

Any symbol lists its **Related tests** in the details pane, and a changed symbol carries
them too: the tests over what you have just edited, with a warning for a changed method
nothing was found to cover.

```
UserService.Get(int id)             src/Shop/UserService.cs:19        2 tests
    exact     UserServiceTests.Get_returns_the_user()   calls Get(int id)
    exact     UserServiceTests.Archive_is_quiet()       constructs UserService

ReportService.Total()               src/Shop/ReportService.cs:5       1 probable
    probable  ReportServiceTests.Totals_are_produced()  named after ReportService, in a
                                                        project that depends on it

PricingEngine.Quote(decimal net)    src/Shop/Legacy/Pricing.cs:5      no tests found
```

Three tiers are compiler-derived and so exact — the test names the symbol, the fixture
constructs the type that declares it, or another member of the fixture names it. The rest
are read off names, namespaces and project references, and never claim to be exact.

```
dotnet test              # 271 tests, including a real end-to-end indexing run,
                         # change mapping against a real Git working tree, and the
                         # tests found over what that change touched
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
`[controller]`, `[action]` and `[area]` expanded the way the framework expands them, and
`[Route]` and `[Area]` followed up the base-type chain the way the framework inherits them
— a controller whose route lives on an abstract base is routed by that base, and the token
still names the derived controller — and Minimal API registrations whose template is a
constant — including the accumulated `MapGroup` prefixes,
followed through the local a group is usually held in. Conventional routing depends on the route table assembled at start-up and is not guessed
at, so an endpoint that is listed is one that exists.

**A route on the controller is what makes its actions endpoints, verb attribute or not.**
Placing `[Route]` on a controller makes its actions attribute-routed — the framework's own
rule — so a plain `public IActionResult Index()` on a `[Route("search")]` controller is
listed at `/search` for any verb. That is the ordinary shape of an MVC site and skipping it
left whole controllers invisible. The converse is enforced too: an action on a controller
with no route template anywhere is reachable only through the conventional route table, so
nothing is claimed for it rather than listing it at `/`. A `{action}` or `{controller}`
route parameter is resolved per action, because a template such as
`auth/{action=Index}/{id?}` describes one route per action and listing them all under the
raw template tells the reader nothing; a constrained parameter such as
`{action:regex(...)}` is left as written rather than half-resolved.

**A controller's lifecycle members are not actions.** `OnActionExecuting`, `Dispose` and
anything else first declared by `Controller`, `ControllerBase` or `object` is excluded, an
override of it included. Without that, an action needing no verb attribute would be
indistinguishable by shape from a filter hook: both are public instance methods.** A project can reference a symbol from a
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

**A project that does not compile says so.** The dangerous case is not a project MSBuild
cannot load — that is loud — but one that loads and then fails to bind, because
declarations still come through and the symbol count looks healthy while every binding that
needed a missing reference silently resolved to nothing. Endpoints are the clearest
casualty: a controller whose base type did not resolve is not recognisably a controller, and
a `MapGet` whose builder parameter is an error type is not recognisably a route, so a
perfectly ordinary web application reports zero endpoints for a reason that has nothing to
do with its code. Compilation errors are therefore counted per project and reported, with
unresolved references named as such, because their fix is environmental — a targeting pack
that restore never downloaded, or a solution restored by a different SDK than the one doing
the analysis — rather than in the code being read.

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

**Git is read through an allowlist, not a convention.** Every Git call goes through one
runner that will execute only `rev-parse`, `status`, `diff`, `log` and `show` — verbs that
cannot mutate a repository in any of their forms. `commit`, `push`, `pull`, `reset`,
`checkout`, `stash` and `clean` are not merely unimplemented; the allowlist is what makes
them unreachable, including from a future caller who has forgotten the rule, and there are
tests that fail if it is widened. Every invocation also leads with `--no-optional-locks`,
because `git status` otherwise refreshes the index as a side effect, and writing to the
analysed repository — even a write Git considers routine — is exactly what CodeAtlas
promises not to do.

**A diff is joined to the index by coordinates, not by names.** The index is built from the
files on disk, so a declaration's recorded span and the new side of a diff against `HEAD`
are in the same line numbering, and a changed symbol is simply one whose span contains a
line the diff touched. That is why `HEAD` is the baseline: it is both what a reader means
by "what have I changed" and the only one that lines up with what was indexed. Mapping the
whole working tree costs one indexed lookup per changed file and no re-analysis.

**A symbol's span is stored, not just its position.** `end_line` is what makes the join
above possible; the identifier's own location cannot say whether a line belongs to a
declaration. A type's span covers its members, so an edit inside a method reports both the
method and the type — both are true, and both are what a reader asks for.

**A removal is attributed to the line that replaced it.** Hunk line numbers are derived by
walking the body rather than trusting the header, so they are correct at any context
setting; a `-` line has no new-side line of its own and takes the position that now sits
where it was. Without that, deleting the body of a method would attribute the change to
nothing.

**Added is exact; removed is only reported where it can be.** A declaration every line of
which is an added line is an addition — a method written into an existing class reads as
added rather than as a change to the class — and that test needs nothing but the diff.
Removed symbols are read from the baseline text of a *deleted file*, where "everything in
it is gone" needs no matching against indexed names. Naming a declaration removed from a
file that still exists would mean matching baseline syntax against resolved identities,
which syntax alone cannot do reliably, so it is not attempted rather than guessed at.
Everything read from baseline syntax is `Inferred` and never navigable: there is no
compilation behind it, and nothing left to navigate to.

**The changed set is never persisted.** Git state moves whenever the reader touches a file,
so it is computed on demand and the cache file stays a function of the source alone. There
is no file watcher either — refreshing is one click, and a watcher would be a second
source of truth about when the index and the working tree agree.

**"Changed code" filters nodes; every other chip filters edges.** "Only the code that
changed" is a statement about which symbols may appear, not about which kinds of relation
to follow, which is why it is a node restriction on the walk rather than a
`RelationGroupKind`. The root and the seeds are always kept regardless, the walk still
traverses the whole neighbourhood and merely declines to admit what falls outside the set —
so two changed symbols joined through unchanged code still both appear — and the chip is
only offered while there is a changed set to filter by.

**Impact is two closures, not one.** "Who calls this" and "what does this touch" are
different questions, and answering them with one walk would overstate both. The call
closure follows `Calls` alone and is what direct and indirect callers mean; the dependency
closure follows everything that constitutes a dependency — calls, references, implements,
overrides, inherits, injects, resolves and signature types — and is the set whose
endpoints, entities, integrations and configuration the report describes. Every entry is
an edge the compiler recorded, so the same index and symbol always give the same answer
and a reader can check any line of it.

**A type is seeded through its members.** Calls land on methods, not on the class that
declares them, so asking what depends on a service and walking only from the type itself
finds nothing. The root's members seed the walk alongside it, which is what makes "eight
places call this service" answerable at all.

**The resource question runs the other way.** A boundary out of the application, a
configuration read and an entity write are recorded against the component that performs
them — the implementation behind the interface, the client that interface is wired to —
which sits *below* a change rather than above it. Walking only backwards therefore finds
every caller of a service and none of the infrastructure it talks to, which is half the
answer. So the resource facts are gathered over the backwards closure together with what
that closure in turn runs, following calls, injection and container resolution forwards.

**Distance is on every impacted symbol, and it is the shortest path.** The walk is
breadth-first, so a symbol is recorded the first time it is reached. Without it a
transitive list reads as one undifferentiated blast radius, and an effect twenty hops away
is presented as though it were the same claim as one two hops away.

**Risk is a set of facts, not a score.** Seven properties, each shown with the evidence
that decided it: reachable endpoints, public boundary, authorization, persistence, external
integration, fan-in and shared abstraction. The level is a count of how many are present
against thresholds that are public constants, so the UI states the rule it applied rather
than only its verdict, and a reader can disagree with the assessment by checking the facts
under it. Absent signals are shown too, because what a change was found *not* to touch is
part of the answer. Nothing here is learned, weighted or opaque.

**Impact stops where the compiler's knowledge stops.** A dispatch resolved at run time is
not an edge, so it is not walked. In a MediatR-style application the request handlers a
change reaches are found — they inject the same services as anything else — but the HTTP
endpoints above them are not, because nothing statically connects an inline handler that
sends a request to the class that handles it. The endpoint count is then honestly zero
rather than guessed at, which is the same rule the endpoint list itself follows.

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

It does now run Git, which MVP 3 did not, and the constraint is enforced structurally
rather than by convention: a single runner executes only the read-only verbs
(`rev-parse`, `status`, `diff`, `log`, `show`), each invocation leads with
`--no-optional-locks` so even `git status` cannot refresh the index as a side effect, and
`GIT_TERMINAL_PROMPT=0` means nothing can block waiting for credentials. There is no code
path that commits, pushes, pulls, resets, checks out, stashes or cleans, and the allowlist
is what keeps it that way. Finding the working tree is still a walk up the directory tree
looking for `.git`, so a workspace outside Git simply has no baseline — a normal state,
not a failure.
