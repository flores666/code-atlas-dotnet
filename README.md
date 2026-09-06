# CodeAtlas.NET

A local-first, read-only semantic explorer for C#/.NET solutions. It loads a solution with
Roslyn, indexes the declarations and exact semantic relations into a local SQLite file, and
lets you search, inspect and map them offline.

**MVP 1** — the semantic code map. Declarations, calls, references, implementations,
inheritance and type dependencies, all compiler-derived, plus a bounded graph of the
neighbourhood around any symbol. No DI/EF analysis, endpoint understanding, Git diffing,
impact analysis, AI, embeddings, MCP or architecture detection.

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

```
dotnet test              # 67 tests, including a real end-to-end indexing run
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

CodeAtlas opens the analysed solution read-only, runs no Git commands, and writes only to its
own cache directory. Git detection is a walk up the directory tree looking for `.git`.
