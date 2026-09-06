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
    public const int Version = 2;

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

        CREATE TABLE symbol_attributes (
            symbol_id     INTEGER NOT NULL REFERENCES symbols(id),
            attribute_fqn TEXT    NOT NULL
        );

        CREATE TABLE relations (
            id               INTEGER PRIMARY KEY,
            source_symbol_id INTEGER NOT NULL REFERENCES symbols(id),
            kind             TEXT    NOT NULL,
            target_symbol_id INTEGER REFERENCES symbols(id),
            target_fqn       TEXT    NOT NULL,
            target_display   TEXT    NOT NULL,
            provenance       TEXT    NOT NULL
        );

        CREATE TABLE diagnostics (
            id       INTEGER PRIMARY KEY,
            severity TEXT NOT NULL,
            project  TEXT,
            message  TEXT NOT NULL
        );

        CREATE INDEX ix_symbols_name      ON symbols(name COLLATE NOCASE);
        CREATE INDEX ix_symbols_fqn       ON symbols(fqn, project_id);
        CREATE INDEX ix_symbols_project   ON symbols(project_id, namespace);
        CREATE INDEX ix_symbols_container ON symbols(container_fqn);

        CREATE INDEX        ix_attributes_symbol ON symbol_attributes(symbol_id);
        CREATE UNIQUE INDEX ux_relations_edge    ON relations(source_symbol_id, kind, target_fqn);
        CREATE INDEX        ix_relations_target  ON relations(target_symbol_id, kind);
        CREATE INDEX        ix_relations_edge_id ON relations(source_symbol_id, target_symbol_id);
        """;
}
