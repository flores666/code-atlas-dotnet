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
    private bool _completed;

    internal IndexWriteSession(SqliteConnection connection)
    {
        _connection = connection;
        _transaction = connection.BeginTransaction();

        Execute("""
            DELETE FROM relations;
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
                                 container_fqn, file_path, line, start_column, accessibility)
            VALUES (@kind, @name, @fqn, @display, @project, @namespace,
                    @container, @file, @line, @column, @accessibility)
            """,
            "@kind", "@name", "@fqn", "@display", "@project", "@namespace",
            "@container", "@file", "@line", "@column", "@accessibility");

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

        // Rolls back when Complete was never reached, preserving the previous index.
        _transaction.Dispose();
    }
}
