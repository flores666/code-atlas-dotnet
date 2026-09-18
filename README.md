# CodeAtlas.NET

A local-first, read-only explorer for C#/.NET solutions. It loads a solution with Roslyn,
indexes the declarations and the compiler-derived edges an execution trace is made of into
a local SQLite file, and lets you browse them and trace any HTTP endpoint offline.

It does three things, which are the three sections in the sidebar:

- **Indexing** — load a `.sln`, `.slnx` or `.csproj` with MSBuild, walk every project with
  Roslyn, write declarations, call edges and dispatch edges to a cache file. The section
  reports what the run could not do.
- **Explorer** — the projects, namespaces, types and members that were indexed, as a tree.
- **Endpoints** — the HTTP entry points found in source. Select one and its execution
  trace is drawn as a numbered vertical flow: the handler, what it calls, and what runs
  behind every interface or abstract member it reaches.

No runtime tracing or coverage, no AI or embeddings, and no cloud provider is contacted at
any point.

## Running

```
dotnet run --project src/CodeAtlas.Desktop
```

The solution name in the middle of the title bar is the picker: it lists the solutions
opened before, and the file and folder pickers that add to them. A folder is resolved to
the best candidate inside it. The first open indexes; later opens reuse the cache until you
press **Reindex**.

The window is three panes, and the strip above them is the open solution's own projects —
the solution's card is all of them, and selecting one scopes the explorer and the endpoint
list to that project:

- **left** — the three sections, and the active one's list.
- **middle** — the source of whatever is selected, read-only, at the declaration. Picking
  an endpoint opens its handler; picking a step of its flow opens the code that step runs.
  **Open in editor** hands the same file to `$CODEATLAS_EDITOR`.
- **right** — the selected endpoint: its request execution flow, and an overview of what
  the index knows about it. It is only there when an endpoint is selected.

## The endpoint trace

Both endpoint shapes are recognised, and both trace the same way.

A controller action is its own entry point:

```
POST /api/PaymentCalculation/GetContract
  PaymentCalculationController.GetContract(GetContractRequest request)
    IContractService.GetContractAsync(GetContractRequest request)
      [implements]  ContractService.GetContractAsync(GetContractRequest request)
        IContractRepository.GetContractAsync(Guid contractId)
          [implements]  ContractRepository.GetContractAsync(Guid contractId)
            IEmbeddedResourceService.GetSqlQuery(string sqlFilename)
              [implements]  EmbeddedResourceService.GetSqlQuery(string sqlFilename)
                EmbeddedResourceService.GetStringFromResource(string resourceName)
```

A Minimal API lambda declares nothing, so its entry points are what was read off the
lambda itself — the services it is handed, then the methods it calls. A method-group
handler (`app.MapDelete("/widgets/{id}", Handlers.Remove)`) is an ordinary entry point:

```
GET /widgets
  IWidgets
  IWidgets.All()
    [implements]  Widgets.All()
      Store.Read()
        Store.Normalise(string[] values)
```

Two hops make a trace, and both come from the compiler: **calls**, which is what the code
does, and **implements/overrides**, which is what a call against an interface or an
abstract member actually reaches. Following the second is what carries a trace past the
interface a controller was handed — without any guess about which implementation the
container binds, because every candidate is listed. A step is marked *shown above* when its
continuation already appears higher up, and *continues deeper* when it runs past the depth
the trace walks.

## Layout

| Project | Contains |
| --- | --- |
| `src/CodeAtlas.Core` | Workspace resolution, Roslyn analysis, the SQLite index, the trace walk. No UI types. |
| `src/CodeAtlas.Desktop` | Avalonia window and view models. The code viewer is AvaloniaEdit. |
| `tests/CodeAtlas.Core.Tests` | Analysis, storage, trace and end-to-end tests. |

## Decisions worth knowing

**The index is a cache, not a database.** It lives in `~/.local/share/CodeAtlas.NET/index`
(local app data), never beside the repository. `IndexSchema.Version` is stamped into every
file; opening a file written by a different version deletes it and reindexes, so there is no
migration code to maintain. A corrupt file is treated the same way.

**Only the edges that are read are stored.** `Calls`, `Implements` and `Overrides`, and
nothing else. References, inheritance, signature types and DI registrations were all
indexed at one point; none of them is part of a trace, and an index that stores what
nothing reads is an index nobody can reason about. References in particular were the bulk
of every file.

**Dispatch is resolved by the compiler, not by the container.** An interface member's
implementations come from Roslyn's own
`FindImplementationForInterfaceMember`, recorded per member. A DI registration would tell
you which one is bound, but reading `AddScoped` calls is a partial reading of a runtime
decision; listing every implementation is complete and never wrong.

**Every edge carries its provenance.** A binding Roslyn resolved to exactly one symbol is
`Exact`. A binding it could not resolve, but which has a single candidate, is kept as
`Inferred` — that keeps a project with errors mapped without dressing a guess up as compiler
truth. Nothing heuristic is ever marked `Exact`.

**The trace is bounded on three axes, not one.** `EndpointTraceBuilder` bounds depth, total
steps, and expands each symbol at most once. The last is what makes a recursive or mutually
recursive chain terminate, and it also stops a helper twenty methods call from being walked
twenty times. The walk reads the index and never Roslyn, so a trace costs a handful of
indexed lookups rather than a recompilation.

**Only statically resolvable routing is captured.** Attribute routing on controllers, and
Minimal API registrations whose route is a literal. Conventional routing depends on the
route table assembled at start-up and a route built at run time is not a literal; neither
is guessed at, so an endpoint that is listed is one that exists.

**A project that does not compile is reported, not hidden.** Declarations still come
through, so the project looks indexed while every binding that needed a missing reference
silently resolved to nothing — an endpoint list that is honestly empty for a reason you
cannot see. *What indexing could not do*, under the index state in the sidebar, is where
that reason lives.

**Failures are contained per project.** A project MSBuild cannot load is absent and
reported; a project whose compilation fails is recorded as unloaded with a diagnostic;
a single unparseable file costs its own analysis and nothing else.

**No ORM and no MVVM framework.** The queries are hand-written SQL over
`Microsoft.Data.Sqlite`, and change notification is a ~30 line `ObservableObject`. Neither
dependency would have earned its place at this size.

**The code viewer is AvaloniaEdit, not a hand-rolled one.** Line numbers, C# syntax
highlighting, selection and search are what a reader expects of a code pane and none of
them is this project's problem. A tokeniser and a virtualised renderer written here would
have been a few hundred lines to reach a worse version of the same thing.

## Opening source at a line

A trace step, an endpoint and a tree row all open their declaration; the OS handler cannot
position a caret. Set `CODEATLAS_EDITOR` to a command line with `{file}` and `{line}`
placeholders to jump to the declaration instead:

```
CODEATLAS_EDITOR='code --goto {file}:{line}'
CODEATLAS_EDITOR='rider --line {line} {file}'
```

## The repository is never written to

CodeAtlas opens the analysed solution read-only and writes only to its own cache directory.
There is no code path that runs Git, or that writes, moves or deletes anything inside the
workspace.
