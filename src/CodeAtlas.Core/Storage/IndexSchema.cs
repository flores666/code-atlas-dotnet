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
    public const int Version = 5;

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

        CREATE TABLE service_registrations (
            id                INTEGER PRIMARY KEY,
            service_fqn       TEXT    NOT NULL,
            service_display   TEXT    NOT NULL,
            service_symbol_id INTEGER REFERENCES symbols(id),
            impl_fqn          TEXT,
            impl_display      TEXT,
            impl_symbol_id    INTEGER REFERENCES symbols(id),
            lifetime          TEXT    NOT NULL,
            kind              TEXT    NOT NULL,
            provenance        TEXT    NOT NULL,
            file_path         TEXT,
            line              INTEGER,
            declaring_member  TEXT
        );

        CREATE TABLE endpoints (
            id                INTEGER PRIMARY KEY,
            http_method       TEXT    NOT NULL,
            route             TEXT    NOT NULL,
            handler_display   TEXT    NOT NULL,
            handler_fqn       TEXT,
            handler_symbol_id INTEGER REFERENCES symbols(id),
            declaring_fqn     TEXT,
            declaring_id      INTEGER REFERENCES symbols(id),
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

        -- An inline Minimal API handler declares nothing, so the symbols its flow starts
        -- from cannot be stored on it the way a controller action's are. Ordered by id on
        -- read: the collector emits the handler's parameters before its calls, and the
        -- first of them is where the flow is rooted.
        CREATE TABLE endpoint_dependencies (
            id          INTEGER PRIMARY KEY,
            endpoint_id INTEGER NOT NULL REFERENCES endpoints(id) ON DELETE CASCADE,
            target_fqn  TEXT    NOT NULL,
            target_display TEXT NOT NULL,
            symbol_id   INTEGER REFERENCES symbols(id)
        );

        CREATE TABLE data_entities (
            id                INTEGER PRIMARY KEY,
            entity_fqn        TEXT    NOT NULL,
            entity_display    TEXT    NOT NULL,
            entity_symbol_id  INTEGER REFERENCES symbols(id),
            context_fqn       TEXT,
            context_display   TEXT,
            context_symbol_id INTEGER REFERENCES symbols(id),
            set_name          TEXT,
            table_name        TEXT,
            schema_name       TEXT,
            config_fqn        TEXT,
            config_display    TEXT,
            config_symbol_id  INTEGER REFERENCES symbols(id),
            project_id        INTEGER REFERENCES projects(id),
            file_path         TEXT,
            line              INTEGER
        );

        CREATE TABLE data_migrations (
            id                INTEGER PRIMARY KEY,
            name              TEXT    NOT NULL,
            type_fqn          TEXT    NOT NULL,
            type_display      TEXT    NOT NULL,
            type_symbol_id    INTEGER REFERENCES symbols(id),
            context_fqn       TEXT,
            context_display   TEXT,
            context_symbol_id INTEGER REFERENCES symbols(id),
            -- Affected tables are listed, never matched on, so they stay one column
            -- rather than earning a join table.
            tables            TEXT,
            project_id        INTEGER REFERENCES projects(id),
            file_path         TEXT,
            line              INTEGER
        );

        CREATE TABLE configuration_usages (
            id                 INTEGER PRIMARY KEY,
            access             TEXT    NOT NULL,
            config_key         TEXT,
            options_fqn        TEXT,
            options_display    TEXT,
            options_symbol_id  INTEGER REFERENCES symbols(id),
            consumer_fqn       TEXT,
            consumer_display   TEXT,
            consumer_symbol_id INTEGER REFERENCES symbols(id),
            project_id         INTEGER REFERENCES projects(id),
            file_path          TEXT,
            line               INTEGER,
            provenance         TEXT    NOT NULL
        );

        CREATE TABLE external_dependencies (
            id                 INTEGER PRIMARY KEY,
            technology         TEXT    NOT NULL,
            binding            TEXT    NOT NULL,
            client_fqn         TEXT    NOT NULL,
            client_display     TEXT    NOT NULL,
            client_symbol_id   INTEGER REFERENCES symbols(id),
            name               TEXT,
            consumer_fqn       TEXT    NOT NULL,
            consumer_display   TEXT    NOT NULL,
            consumer_symbol_id INTEGER REFERENCES symbols(id),
            project_id         INTEGER REFERENCES projects(id),
            file_path          TEXT,
            line               INTEGER,
            provenance         TEXT    NOT NULL
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

        CREATE INDEX ix_registrations_service ON service_registrations(service_fqn);
        CREATE INDEX ix_registrations_symbol  ON service_registrations(service_symbol_id);
        CREATE INDEX ix_registrations_impl    ON service_registrations(impl_symbol_id);
        CREATE INDEX ix_endpoints_handler     ON endpoints(handler_symbol_id);
        CREATE INDEX ix_endpoints_route       ON endpoints(route);
        CREATE INDEX ix_endpoint_deps         ON endpoint_dependencies(endpoint_id);

        CREATE INDEX ix_entities_fqn        ON data_entities(entity_fqn);
        CREATE INDEX ix_entities_symbol     ON data_entities(entity_symbol_id);
        CREATE INDEX ix_entities_context    ON data_entities(context_symbol_id);
        CREATE INDEX ix_migrations_context  ON data_migrations(context_symbol_id);
        CREATE INDEX ix_configuration_opts  ON configuration_usages(options_symbol_id);
        CREATE INDEX ix_configuration_owner ON configuration_usages(consumer_symbol_id);
        CREATE INDEX ix_external_consumer   ON external_dependencies(consumer_symbol_id);
        CREATE INDEX ix_external_client     ON external_dependencies(client_symbol_id);
        """;
}
