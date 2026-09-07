using System.Globalization;
using CodeAtlas.Core.Analysis;
using CodeAtlas.Core.Model;
using Microsoft.Data.Sqlite;

namespace CodeAtlas.Core.Storage;

/// <summary>
/// A full rebuild of an index, staged in one transaction.
/// </summary>
/// <remarks>
/// Relations arrive naming their endpoints, because a project may reference a symbol
/// from a project that has not been written yet. Both ends are therefore resolved to
/// row ids in <see cref="Complete"/>, once every symbol is present. Abandoning the
/// session without completing rolls the whole rebuild back, leaving the previous index
/// intact.
/// </remarks>
public sealed class IndexWriteSession : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private readonly SqliteCommand _insertProject;
    private readonly SqliteCommand _insertSymbol;
    private readonly SqliteCommand _insertAttribute;
    private readonly SqliteCommand _insertRelation;
    private readonly SqliteCommand _insertDiagnostic;
    private readonly SqliteCommand _insertRegistration;
    private readonly SqliteCommand _insertEndpoint;
    private readonly SqliteCommand _insertEndpointDependency;
    private readonly SqliteCommand _insertEntity;
    private readonly SqliteCommand _insertMigration;
    private readonly SqliteCommand _insertConfiguration;
    private readonly SqliteCommand _insertExternal;
    private bool _completed;

    internal IndexWriteSession(SqliteConnection connection)
    {
        _connection = connection;
        _transaction = connection.BeginTransaction();

        Execute("""
            DELETE FROM relations;
            DELETE FROM service_registrations;
            DELETE FROM endpoint_dependencies;
            DELETE FROM endpoints;
            DELETE FROM data_entities;
            DELETE FROM data_migrations;
            DELETE FROM configuration_usages;
            DELETE FROM external_dependencies;
            DELETE FROM symbol_attributes;
            DELETE FROM symbols;
            DELETE FROM projects;
            DELETE FROM diagnostics;
            DELETE FROM schema_info WHERE key <> 'schema_version';

            CREATE TEMP TABLE staged_relations (
                project_id     INTEGER NOT NULL,
                source_fqn     TEXT    NOT NULL,
                kind           TEXT    NOT NULL,
                target_fqn     TEXT    NOT NULL,
                target_display TEXT    NOT NULL,
                provenance     TEXT    NOT NULL
            );
            """);

        _insertProject = Prepare(
            "INSERT INTO projects (name, file_path, assembly_name, loaded) VALUES (@name, @path, @assembly, @loaded)",
            "@name", "@path", "@assembly", "@loaded");

        _insertSymbol = Prepare(
            """
            INSERT INTO symbols (kind, name, fqn, display, project_id, namespace,
                                 container_fqn, file_path, line, start_column, end_line, accessibility)
            VALUES (@kind, @name, @fqn, @display, @project, @namespace,
                    @container, @file, @line, @column, @endLine, @accessibility)
            """,
            "@kind", "@name", "@fqn", "@display", "@project", "@namespace",
            "@container", "@file", "@line", "@column", "@endLine", "@accessibility");

        _insertAttribute = Prepare(
            "INSERT INTO symbol_attributes (symbol_id, attribute_fqn) VALUES (@symbol, @attribute)",
            "@symbol", "@attribute");

        _insertRelation = Prepare(
            """
            INSERT INTO staged_relations (project_id, source_fqn, kind, target_fqn, target_display, provenance)
            VALUES (@project, @source, @kind, @target, @display, @provenance)
            """,
            "@project", "@source", "@kind", "@target", "@display", "@provenance");

        _insertDiagnostic = Prepare(
            "INSERT INTO diagnostics (severity, project, message) VALUES (@severity, @project, @message)",
            "@severity", "@project", "@message");

        _insertRegistration = Prepare(
            """
            INSERT INTO service_registrations
                (service_fqn, service_display, impl_fqn, impl_display,
                 lifetime, kind, provenance, file_path, line, declaring_member)
            VALUES (@service, @serviceDisplay, @impl, @implDisplay,
                    @lifetime, @kind, @provenance, @file, @line, @member)
            """,
            "@service", "@serviceDisplay", "@impl", "@implDisplay",
            "@lifetime", "@kind", "@provenance", "@file", "@line", "@member");

        _insertEndpoint = Prepare(
            """
            INSERT INTO endpoints
                (http_method, route, handler_display, handler_fqn, declaring_fqn, kind,
                 project_id, file_path, line, requires_auth, allows_anonymous,
                 policies, roles, provenance)
            VALUES (@method, @route, @handler, @handlerFqn, @declaring, @kind,
                    @project, @file, @line, @auth, @anonymous,
                    @policies, @roles, @provenance)
            """,
            "@method", "@route", "@handler", "@handlerFqn", "@declaring", "@kind",
            "@project", "@file", "@line", "@auth", "@anonymous",
            "@policies", "@roles", "@provenance");

        _insertEndpointDependency = Prepare(
            """
            INSERT INTO endpoint_dependencies (endpoint_id, target_fqn, target_display)
            VALUES (@endpoint, @fqn, @display)
            """,
            "@endpoint", "@fqn", "@display");

        _insertEntity = Prepare(
            """
            INSERT INTO data_entities
                (entity_fqn, entity_display, context_fqn, context_display, set_name,
                 table_name, schema_name, config_fqn, config_display, project_id, file_path, line)
            VALUES (@entity, @entityDisplay, @context, @contextDisplay, @set,
                    @table, @schema, @config, @configDisplay, @project, @file, @line)
            """,
            "@entity", "@entityDisplay", "@context", "@contextDisplay", "@set",
            "@table", "@schema", "@config", "@configDisplay", "@project", "@file", "@line");

        _insertMigration = Prepare(
            """
            INSERT INTO data_migrations
                (name, type_fqn, type_display, context_fqn, context_display,
                 tables, project_id, file_path, line)
            VALUES (@name, @type, @typeDisplay, @context, @contextDisplay,
                    @tables, @project, @file, @line)
            """,
            "@name", "@type", "@typeDisplay", "@context", "@contextDisplay",
            "@tables", "@project", "@file", "@line");

        _insertConfiguration = Prepare(
            """
            INSERT INTO configuration_usages
                (access, config_key, options_fqn, options_display,
                 consumer_fqn, consumer_display, project_id, file_path, line, provenance)
            VALUES (@access, @key, @options, @optionsDisplay,
                    @consumer, @consumerDisplay, @project, @file, @line, @provenance)
            """,
            "@access", "@key", "@options", "@optionsDisplay",
            "@consumer", "@consumerDisplay", "@project", "@file", "@line", "@provenance");

        _insertExternal = Prepare(
            """
            INSERT INTO external_dependencies
                (technology, binding, client_fqn, client_display, name,
                 consumer_fqn, consumer_display, project_id, file_path, line, provenance)
            VALUES (@technology, @binding, @client, @clientDisplay, @name,
                    @consumer, @consumerDisplay, @project, @file, @line, @provenance)
            """,
            "@technology", "@binding", "@client", "@clientDisplay", "@name",
            "@consumer", "@consumerDisplay", "@project", "@file", "@line", "@provenance");
    }

    /// <summary>Writes a project row and returns its id, used to scope its symbols.</summary>
    public long AddProject(IndexedProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        Set(_insertProject, "@name", project.Name);
        Set(_insertProject, "@path", project.FilePath);
        Set(_insertProject, "@assembly", project.AssemblyName);
        Set(_insertProject, "@loaded", project.Loaded ? 1 : 0);
        _insertProject.ExecuteNonQuery();

        return LastRowId();
    }

    public void AddSymbols(long projectId, IEnumerable<IndexedSymbol> symbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);

        foreach (var symbol in symbols)
        {
            Set(_insertSymbol, "@kind", symbol.Kind.ToString());
            Set(_insertSymbol, "@name", symbol.Name);
            Set(_insertSymbol, "@fqn", symbol.FullyQualifiedName);
            Set(_insertSymbol, "@display", symbol.Display);
            Set(_insertSymbol, "@project", projectId);
            Set(_insertSymbol, "@namespace", symbol.Namespace);
            Set(_insertSymbol, "@container", symbol.ContainerFullyQualifiedName);
            Set(_insertSymbol, "@file", symbol.FilePath);
            Set(_insertSymbol, "@line", symbol.Line);
            Set(_insertSymbol, "@column", symbol.Column);
            Set(_insertSymbol, "@endLine", symbol.EndLine);
            Set(_insertSymbol, "@accessibility", symbol.Accessibility);
            _insertSymbol.ExecuteNonQuery();

            if (symbol.Attributes.Count == 0)
            {
                continue;
            }

            var symbolId = LastRowId();
            foreach (var attribute in symbol.Attributes)
            {
                Set(_insertAttribute, "@symbol", symbolId);
                Set(_insertAttribute, "@attribute", attribute);
                _insertAttribute.ExecuteNonQuery();
            }
        }
    }

    public void AddRelations(long projectId, IEnumerable<PendingRelation> relations)
    {
        ArgumentNullException.ThrowIfNull(relations);

        foreach (var relation in relations)
        {
            Set(_insertRelation, "@project", projectId);
            Set(_insertRelation, "@source", relation.SourceFullyQualifiedName);
            Set(_insertRelation, "@kind", relation.Kind.ToString());
            Set(_insertRelation, "@target", relation.TargetFullyQualifiedName);
            Set(_insertRelation, "@display", relation.TargetDisplay);
            Set(_insertRelation, "@provenance", relation.Provenance.ToString());
            _insertRelation.ExecuteNonQuery();
        }
    }

    public void AddRegistrations(IEnumerable<ServiceRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);

        foreach (var registration in registrations)
        {
            Set(_insertRegistration, "@service", registration.ServiceFullyQualifiedName);
            Set(_insertRegistration, "@serviceDisplay", registration.ServiceDisplay);
            Set(_insertRegistration, "@impl", registration.ImplementationFullyQualifiedName);
            Set(_insertRegistration, "@implDisplay", registration.ImplementationDisplay);
            Set(_insertRegistration, "@lifetime", registration.Lifetime.ToString());
            Set(_insertRegistration, "@kind", registration.Kind.ToString());
            Set(_insertRegistration, "@provenance", registration.Provenance.ToString());
            Set(_insertRegistration, "@file", registration.FilePath);
            Set(_insertRegistration, "@line", registration.Line);
            Set(_insertRegistration, "@member", registration.DeclaringMember);
            _insertRegistration.ExecuteNonQuery();
        }
    }

    public void AddEndpoints(long projectId, IEnumerable<HttpEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        foreach (var endpoint in endpoints)
        {
            Set(_insertEndpoint, "@method", endpoint.HttpMethod);
            Set(_insertEndpoint, "@route", endpoint.Route);
            Set(_insertEndpoint, "@handler", endpoint.HandlerDisplay);
            Set(_insertEndpoint, "@handlerFqn", endpoint.HandlerFullyQualifiedName);
            Set(_insertEndpoint, "@declaring", endpoint.DeclaringTypeFullyQualifiedName);
            Set(_insertEndpoint, "@kind", endpoint.Kind.ToString());
            Set(_insertEndpoint, "@project", projectId);
            Set(_insertEndpoint, "@file", endpoint.FilePath);
            Set(_insertEndpoint, "@line", endpoint.Line);
            Set(_insertEndpoint, "@auth", endpoint.RequiresAuthorization ? 1 : 0);
            Set(_insertEndpoint, "@anonymous", endpoint.AllowsAnonymous ? 1 : 0);
            Set(_insertEndpoint, "@policies", Join(endpoint.Policies));
            Set(_insertEndpoint, "@roles", Join(endpoint.Roles));
            Set(_insertEndpoint, "@provenance", endpoint.Provenance.ToString());
            _insertEndpoint.ExecuteNonQuery();

            if (endpoint.Dependencies.Count == 0)
            {
                continue;
            }

            var endpointId = LastRowId();
            foreach (var dependency in endpoint.Dependencies)
            {
                Set(_insertEndpointDependency, "@endpoint", endpointId);
                Set(_insertEndpointDependency, "@fqn", dependency.FullyQualifiedName);
                Set(_insertEndpointDependency, "@display", dependency.Display);
                _insertEndpointDependency.ExecuteNonQuery();
            }
        }
    }

    public void AddEntities(long projectId, IEnumerable<EntityMapping> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);

        foreach (var entity in entities)
        {
            Set(_insertEntity, "@entity", entity.EntityFullyQualifiedName);
            Set(_insertEntity, "@entityDisplay", entity.EntityDisplay);
            Set(_insertEntity, "@context", entity.ContextFullyQualifiedName);
            Set(_insertEntity, "@contextDisplay", entity.ContextDisplay);
            Set(_insertEntity, "@set", entity.SetName);
            Set(_insertEntity, "@table", entity.TableName);
            Set(_insertEntity, "@schema", entity.Schema);
            Set(_insertEntity, "@config", entity.ConfigurationFullyQualifiedName);
            Set(_insertEntity, "@configDisplay", entity.ConfigurationDisplay);
            Set(_insertEntity, "@project", projectId);
            Set(_insertEntity, "@file", entity.FilePath);
            Set(_insertEntity, "@line", entity.Line);
            _insertEntity.ExecuteNonQuery();
        }
    }

    public void AddMigrations(long projectId, IEnumerable<DataMigration> migrations)
    {
        ArgumentNullException.ThrowIfNull(migrations);

        foreach (var migration in migrations)
        {
            Set(_insertMigration, "@name", migration.Name);
            Set(_insertMigration, "@type", migration.TypeFullyQualifiedName);
            Set(_insertMigration, "@typeDisplay", migration.TypeDisplay);
            Set(_insertMigration, "@context", migration.ContextFullyQualifiedName);
            Set(_insertMigration, "@contextDisplay", migration.ContextDisplay);
            Set(_insertMigration, "@tables", Join(migration.Tables));
            Set(_insertMigration, "@project", projectId);
            Set(_insertMigration, "@file", migration.FilePath);
            Set(_insertMigration, "@line", migration.Line);
            _insertMigration.ExecuteNonQuery();
        }
    }

    public void AddConfiguration(long projectId, IEnumerable<ConfigurationUsage> usages)
    {
        ArgumentNullException.ThrowIfNull(usages);

        foreach (var usage in usages)
        {
            Set(_insertConfiguration, "@access", usage.Access.ToString());
            Set(_insertConfiguration, "@key", usage.Key);
            Set(_insertConfiguration, "@options", usage.OptionsFullyQualifiedName);
            Set(_insertConfiguration, "@optionsDisplay", usage.OptionsDisplay);
            Set(_insertConfiguration, "@consumer", usage.ConsumerFullyQualifiedName);
            Set(_insertConfiguration, "@consumerDisplay", usage.ConsumerDisplay);
            Set(_insertConfiguration, "@project", projectId);
            Set(_insertConfiguration, "@file", usage.FilePath);
            Set(_insertConfiguration, "@line", usage.Line);
            Set(_insertConfiguration, "@provenance", usage.Provenance.ToString());
            _insertConfiguration.ExecuteNonQuery();
        }
    }

    public void AddExternalDependencies(long projectId, IEnumerable<ExternalDependency> dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);

        foreach (var dependency in dependencies)
        {
            Set(_insertExternal, "@technology", dependency.Technology.ToString());
            Set(_insertExternal, "@binding", dependency.Binding.ToString());
            Set(_insertExternal, "@client", dependency.ClientFullyQualifiedName);
            Set(_insertExternal, "@clientDisplay", dependency.ClientDisplay);
            Set(_insertExternal, "@name", dependency.Name);
            Set(_insertExternal, "@consumer", dependency.ConsumerFullyQualifiedName);
            Set(_insertExternal, "@consumerDisplay", dependency.ConsumerDisplay);
            Set(_insertExternal, "@project", projectId);
            Set(_insertExternal, "@file", dependency.FilePath);
            Set(_insertExternal, "@line", dependency.Line);
            Set(_insertExternal, "@provenance", dependency.Provenance.ToString());
            _insertExternal.ExecuteNonQuery();
        }
    }

    private static string? Join(IReadOnlyList<string> values) =>
        values.Count == 0 ? null : string.Join(", ", values);

    public void AddDiagnostics(IEnumerable<IndexDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        foreach (var diagnostic in diagnostics)
        {
            Set(_insertDiagnostic, "@severity", diagnostic.Severity.ToString());
            Set(_insertDiagnostic, "@project", diagnostic.Project);
            Set(_insertDiagnostic, "@message", diagnostic.Message);
            _insertDiagnostic.ExecuteNonQuery();
        }
    }

    /// <summary>Resolves relation endpoints, stamps the metadata and commits.</summary>
    public void Complete(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ObjectDisposedException.ThrowIf(_completed, this);

        // The source is matched within its own project so that two projects declaring
        // the same name (multi-targeting, linked files) do not cross-link.
        Execute("""
            INSERT OR IGNORE INTO relations (source_symbol_id, kind, target_fqn, target_display, provenance)
            -- An edge seen bound exactly anywhere is exact, even if another occurrence
            -- of it could only be inferred.
            SELECT s.id, r.kind, r.target_fqn, r.target_display,
                   CASE WHEN SUM(r.provenance = 'Exact') > 0 THEN 'Exact' ELSE 'Inferred' END
            FROM staged_relations r
            JOIN symbols s ON s.fqn = r.source_fqn AND s.project_id = r.project_id
            GROUP BY s.id, r.kind, r.target_fqn, r.target_display;

            UPDATE service_registrations SET
                service_symbol_id = (SELECT MIN(s.id) FROM symbols s WHERE s.fqn = service_fqn),
                impl_symbol_id    = (SELECT MIN(s.id) FROM symbols s WHERE s.fqn = impl_fqn);

            UPDATE endpoints SET
                handler_symbol_id = (SELECT MIN(s.id) FROM symbols s WHERE s.fqn = handler_fqn),
                declaring_id      = (SELECT MIN(s.id) FROM symbols s WHERE s.fqn = declaring_fqn);

            UPDATE endpoint_dependencies SET
                symbol_id = (SELECT MIN(s.id) FROM symbols s WHERE s.fqn = target_fqn);

            UPDATE data_entities SET
                entity_symbol_id  = (SELECT MIN(s.id) FROM symbols s WHERE s.fqn = entity_fqn),
                context_symbol_id = (SELECT MIN(s.id) FROM symbols s WHERE s.fqn = context_fqn),
                config_symbol_id  = (SELECT MIN(s.id) FROM symbols s WHERE s.fqn = config_fqn);

            UPDATE data_migrations SET
                type_symbol_id    = (SELECT MIN(s.id) FROM symbols s WHERE s.fqn = type_fqn),
                context_symbol_id = (SELECT MIN(s.id) FROM symbols s WHERE s.fqn = context_fqn);

            UPDATE configuration_usages SET
                options_symbol_id  = (SELECT MIN(s.id) FROM symbols s WHERE s.fqn = options_fqn),
                consumer_symbol_id = (SELECT MIN(s.id) FROM symbols s WHERE s.fqn = consumer_fqn);

            UPDATE external_dependencies SET
                client_symbol_id   = (SELECT MIN(s.id) FROM symbols s WHERE s.fqn = client_fqn),
                consumer_symbol_id = (SELECT MIN(s.id) FROM symbols s WHERE s.fqn = consumer_fqn);

            -- Registrations become graph edges, so the same walk that follows calls and
            -- inheritance also crosses from an interface to what satisfies it. Registering
            -- a type as itself adds no edge: it would be a self-loop.
            INSERT OR IGNORE INTO relations (source_symbol_id, kind, target_fqn, target_display, provenance)
            SELECT service_symbol_id, 'Resolves', impl_fqn, COALESCE(impl_display, impl_fqn),
                   CASE WHEN SUM(provenance = 'Exact') > 0 THEN 'Exact' ELSE 'Inferred' END
            FROM service_registrations
            WHERE service_symbol_id IS NOT NULL
              AND impl_fqn IS NOT NULL
              AND impl_fqn <> service_fqn
            GROUP BY service_symbol_id, impl_fqn, impl_display;

            -- A navigation property from one entity to another is the relationship, and
            -- the compiler already recorded it as the property's type. Deriving the
            -- entity-to-entity edge here costs one join and saves a second analysis pass;
            -- fluent HasOne/HasMany configuration contributes the same edge directly.
            INSERT OR IGNORE INTO relations (source_symbol_id, kind, target_fqn, target_display, provenance)
            SELECT DISTINCT owner.id, 'RelatesToEntity', r.target_fqn, r.target_display, 'Exact'
            FROM relations r
            JOIN symbols property ON property.id = r.source_symbol_id AND property.kind IN ('Property', 'Field')
            JOIN symbols owner    ON owner.fqn = property.container_fqn
            WHERE r.kind = 'ReturnType'
              AND owner.fqn <> r.target_fqn
              AND owner.fqn IN (SELECT entity_fqn FROM data_entities)
              AND r.target_fqn IN (SELECT entity_fqn FROM data_entities);

            UPDATE relations
            SET target_symbol_id = (SELECT MIN(s.id) FROM symbols s WHERE s.fqn = relations.target_fqn);

            DROP TABLE staged_relations;
            """);

        SetSetting(IndexSchema.SourcePathKey, sourcePath);
        SetSetting(IndexSchema.IndexedAtKey, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));

        _transaction.Commit();
        _completed = true;
    }

    private void SetSetting(string key, string value)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = "INSERT OR REPLACE INTO schema_info (key, value) VALUES (@key, @value)";
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@value", value);
        command.ExecuteNonQuery();
    }

    private long LastRowId()
    {
        using var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = "SELECT last_insert_rowid()";
        return (long)command.ExecuteScalar()!;
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private SqliteCommand Prepare(string sql, params string[] parameters)
    {
        var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = sql;

        // Left untyped: each value binds as its own storage class, rather than
        // relying on column affinity to convert integers back from text.
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new SqliteParameter { ParameterName = parameter, Value = DBNull.Value });
        }

        command.Prepare();
        return command;
    }

    private static void Set(SqliteCommand command, string parameter, object? value) =>
        command.Parameters[parameter].Value = value ?? DBNull.Value;

    public void Dispose()
    {
        _insertProject.Dispose();
        _insertSymbol.Dispose();
        _insertAttribute.Dispose();
        _insertRelation.Dispose();
        _insertDiagnostic.Dispose();
        _insertRegistration.Dispose();
        _insertEndpoint.Dispose();
        _insertEndpointDependency.Dispose();
        _insertEntity.Dispose();
        _insertMigration.Dispose();
        _insertConfiguration.Dispose();
        _insertExternal.Dispose();

        // Rolls back when Complete was never reached, preserving the previous index.
        _transaction.Dispose();
    }
}
