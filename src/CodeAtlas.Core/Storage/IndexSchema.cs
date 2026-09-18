namespace CodeAtlas.Core.Storage;

/// <summary>
/// The on-disk shape of a cached index.
/// </summary>
/// <remarks>
/// The index is a derived artefact, never a source of truth, so migration is
/// deliberately not supported: bumping <see cref="Version"/> makes
/// <see cref="SymbolIndexDatabase"/> discard an older file and reindex from scratch.
/// </remarks>
public static class IndexSchema
{
    public const int Version = 8;

    public const string SchemaVersionKey = "schema_version";
    public const string SourcePathKey = "source_path";
    public const string IndexedAtKey = "indexed_at_utc";

    public const string CreateScript = """
        CREATE TABLE schema_info (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE projects (
            id            INTEGER PRIMARY KEY,
            name          TEXT    NOT NULL,
            file_path     TEXT,
            assembly_name TEXT,
            loaded        INTEGER NOT NULL DEFAULT 1
        );

        CREATE TABLE symbols (
            id            INTEGER PRIMARY KEY,
            kind          TEXT    NOT NULL,
            name          TEXT    NOT NULL,
            fqn           TEXT    NOT NULL,
            display       TEXT    NOT NULL,
            project_id    INTEGER REFERENCES projects(id),
            namespace     TEXT,
            container_fqn TEXT,
            file_path     TEXT,
            line          INTEGER,
            start_column  INTEGER,
            accessibility TEXT
        );

        -- The edges an execution trace is walked along: what a member calls, and what
        -- runs in its place when the call was made against an interface or an abstract
        -- member.
        CREATE TABLE relations (
            id               INTEGER PRIMARY KEY,
            source_symbol_id INTEGER NOT NULL REFERENCES symbols(id),
            kind             TEXT    NOT NULL,
            target_symbol_id INTEGER REFERENCES symbols(id),
            target_fqn       TEXT    NOT NULL,
            target_display   TEXT    NOT NULL,
            provenance       TEXT    NOT NULL
        );

        CREATE TABLE endpoints (
            id                INTEGER PRIMARY KEY,
            http_method       TEXT    NOT NULL,
            route             TEXT    NOT NULL,
            handler_display   TEXT    NOT NULL,
            handler_fqn       TEXT,
            handler_symbol_id INTEGER REFERENCES symbols(id),
            kind              TEXT    NOT NULL,
            project_id        INTEGER REFERENCES projects(id),
            file_path         TEXT,
            line              INTEGER,
            requires_auth     INTEGER NOT NULL,
            allows_anonymous  INTEGER NOT NULL,
            -- Authorization names are shown, never matched on, so they stay one column
            -- rather than earning a join table.
            policies          TEXT,
            roles             TEXT,
            provenance        TEXT    NOT NULL
        );

        -- An inline Minimal API handler declares nothing, so the symbols its trace starts
        -- from cannot be stored on it the way a controller action's are. Ordered by id on
        -- read: the collector emits the handler's parameters before its calls.
        CREATE TABLE endpoint_dependencies (
            id             INTEGER PRIMARY KEY,
            endpoint_id    INTEGER NOT NULL REFERENCES endpoints(id) ON DELETE CASCADE,
            target_fqn     TEXT    NOT NULL,
            target_display TEXT    NOT NULL,
            symbol_id      INTEGER REFERENCES symbols(id)
        );

        CREATE TABLE diagnostics (
            id       INTEGER PRIMARY KEY,
            severity TEXT NOT NULL,
            project  TEXT,
            message  TEXT NOT NULL
        );

        CREATE INDEX ix_symbols_fqn       ON symbols(fqn, project_id);
        CREATE INDEX ix_symbols_project   ON symbols(project_id, namespace);
        CREATE INDEX ix_symbols_container ON symbols(container_fqn);

        CREATE UNIQUE INDEX ux_relations_edge   ON relations(source_symbol_id, kind, target_fqn);
        -- A trace step is a lookup by one end and a kind, in both directions.
        CREATE INDEX        ix_relations_source ON relations(source_symbol_id, kind);
        CREATE INDEX        ix_relations_target ON relations(target_symbol_id, kind);

        CREATE INDEX ix_endpoints_route ON endpoints(route);
        CREATE INDEX ix_endpoint_deps   ON endpoint_dependencies(endpoint_id);
        """;
}
