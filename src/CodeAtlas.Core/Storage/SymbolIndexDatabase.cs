using System.Globalization;
using CodeAtlas.Core.Model;
using Microsoft.Data.Sqlite;

namespace CodeAtlas.Core.Storage;

/// <summary>
/// A connection to one cached index file.
/// </summary>
/// <remarks>
/// Reads are serialised, so one instance can be shared by a UI thread and background
/// queries. A rebuild must use its own instance: SQLite's WAL mode lets that separate
/// connection write while readers continue on the previous content.
/// </remarks>
public sealed class SymbolIndexDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    /// <summary>Guards <see cref="_connection"/>, which SQLite does not allow to be used concurrently.</summary>
    private readonly Lock _gate = new();

    private SymbolIndexDatabase(SqliteConnection connection) => _connection = connection;

    /// <summary>
    /// Opens <paramref name="databasePath"/>, creating it when absent. An existing file
    /// written by a different schema version is discarded so the caller can reindex.
    /// </summary>
    public static SymbolIndexDatabase Open(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(databasePath) && !IsCurrentSchema(databasePath))
        {
            DiscardIncompatible(databasePath);
        }

        var connection = Connect(databasePath);
        try
        {
            if (!TableExists(connection, "schema_info"))
            {
                Create(connection);
            }

            return new SymbolIndexDatabase(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static SqliteConnection Connect(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
        }.ToString());

        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            """;
        pragma.ExecuteNonQuery();

        return connection;
    }

    private static bool IsCurrentSchema(string databasePath)
    {
        try
        {
            using var connection = Connect(databasePath);
            if (!TableExists(connection, "schema_info"))
            {
                return false;
            }

            return ReadSetting(connection, IndexSchema.SchemaVersionKey) is { } value &&
                   int.TryParse(value, CultureInfo.InvariantCulture, out var version) &&
                   version == IndexSchema.Version;
        }
        catch (SqliteException)
        {
            // Truncated or corrupt cache file: treat exactly like a stale schema.
            return false;
        }
    }

    private static void DiscardIncompatible(string databasePath)
    {
        SqliteConnection.ClearAllPools();

        foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static void Create(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = IndexSchema.CreateScript;
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO schema_info (key, value) VALUES (@key, @value)";
            command.Parameters.AddWithValue("@key", IndexSchema.SchemaVersionKey);
            command.Parameters.AddWithValue("@value", IndexSchema.Version.ToString(CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static bool TableExists(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name";
        command.Parameters.AddWithValue("@name", name);
        return command.ExecuteScalar() is not null;
    }

    private static string? ReadSetting(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM schema_info WHERE key = @key";
        command.Parameters.AddWithValue("@key", key);
        return command.ExecuteScalar() as string;
    }

    // ---- metadata -----------------------------------------------------------

    /// <summary>
    /// Describes the completed index in this file, or <c>null</c> when it holds none —
    /// a freshly created file, or one whose indexing run never finished.
    /// </summary>
    public IndexMetadata? ReadMetadata()
    {
        lock (_gate)
        {
            var sourcePath = ReadSetting(_connection, IndexSchema.SourcePathKey);
            var indexedAt = ReadSetting(_connection, IndexSchema.IndexedAtKey);

            if (sourcePath is null ||
                indexedAt is null ||
                !DateTimeOffset.TryParse(indexedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp))
            {
                return null;
            }

            return new IndexMetadata(
                IndexSchema.Version,
                sourcePath,
                timestamp,
                Count("SELECT COUNT(*) FROM projects"),
                Count("SELECT COUNT(*) FROM symbols"));
        }
    }

    /// <summary>True when this file already holds a usable index for <paramref name="sourcePath"/>.</summary>
    public bool HasUsableIndexFor(string sourcePath) =>
        ReadMetadata() is { } metadata &&
        string.Equals(metadata.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase) &&
        metadata.SymbolCount > 0;

    private int Count(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    // ---- writing ------------------------------------------------------------

    /// <summary>
    /// Starts a full rebuild. Existing content stays visible to readers until
    /// <see cref="IndexWriteSession.Complete"/> commits.
    /// </summary>
    public IndexWriteSession BeginRebuild() => new(_connection);

    // ---- the explorer tree --------------------------------------------------

    public IReadOnlyList<IndexedProject> GetProjects()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT p.id, p.name, p.file_path, p.assembly_name, p.loaded,
                       (SELECT COUNT(*) FROM symbols s WHERE s.project_id = p.id)
                FROM projects p
                ORDER BY p.name COLLATE NOCASE
                """;

            using var reader = command.ExecuteReader();
            var results = new List<IndexedProject>();

            while (reader.Read())
            {
                results.Add(new IndexedProject
                {
                    Id = reader.GetInt64(0),
                    Name = reader.GetString(1),
                    FilePath = reader.IsDBNull(2) ? null : reader.GetString(2),
                    AssemblyName = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Loaded = reader.GetInt32(4) != 0,
                    SymbolCount = reader.GetInt32(5),
                });
            }

            return results;
        }
    }

    /// <summary>Distinct namespaces holding declarations in a project, global scope last.</summary>
    public IReadOnlyList<string> GetNamespaces(long projectId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT DISTINCT COALESCE(namespace, '')
                FROM symbols
                WHERE project_id = @project AND kind <> 'Namespace' AND container_fqn IS NULL
                ORDER BY 1 COLLATE NOCASE
                """;
            command.Parameters.AddWithValue("@project", projectId);

            using var reader = command.ExecuteReader();
            var results = new List<string>();

            while (reader.Read())
            {
                results.Add(reader.GetString(0));
            }

            return results;
        }
    }

    /// <summary>Top-level types declared in a project namespace. Pass "" for global scope.</summary>
    public IReadOnlyList<IndexedSymbol> GetTypes(long projectId, string @namespace)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"""
                {SelectSymbol}
                WHERE s.project_id = @project
                  AND COALESCE(s.namespace, '') = @namespace
                  AND s.container_fqn IS NULL
                  AND s.kind <> 'Namespace'
                ORDER BY s.name COLLATE NOCASE, s.fqn
                """;
            command.Parameters.AddWithValue("@project", projectId);
            command.Parameters.AddWithValue("@namespace", @namespace);

            return ReadSymbols(command);
        }
    }

    /// <summary>Members and nested types declared directly inside a type.</summary>
    public IReadOnlyList<IndexedSymbol> GetMembers(string containerFullyQualifiedName)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"""
                {SelectSymbol}
                WHERE s.container_fqn = @container
                ORDER BY s.kind, s.name COLLATE NOCASE, s.fqn
                """;
            command.Parameters.AddWithValue("@container", containerFullyQualifiedName);

            return ReadSymbols(command);
        }
    }

    public IndexedSymbol? GetSymbol(long id)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"{SelectSymbol} WHERE s.id = @id";
            command.Parameters.AddWithValue("@id", id);

            return ReadSymbols(command).FirstOrDefault();
        }
    }

    // ---- one step of an execution trace -------------------------------------
    //
    // Both answer exactly one hop, so neither can grow with the size of the solution.
    // How far a trace walks is EndpointTraceBuilder's business, not theirs.

    /// <summary>The methods and constructors a member invokes.</summary>
    public IReadOnlyList<IndexedSymbol> GetCallees(long symbolId) =>
        Step(symbolId, "r.target_symbol_id", "r.source_symbol_id", RelationKind.Calls);

    /// <summary>
    /// What runs in place of this member: the members implementing it when it is declared
    /// on an interface, and the members overriding it when it is virtual or abstract.
    /// </summary>
    public IReadOnlyList<IndexedSymbol> GetImplementations(long symbolId) =>
        Step(symbolId, "r.source_symbol_id", "r.target_symbol_id", RelationKind.Implements, RelationKind.Overrides);

    /// <summary>
    /// Walks one hop: the symbols reached from <paramref name="symbolId"/> along
    /// <paramref name="kinds"/>, read off whichever end of the edge is the far one.
    /// </summary>
    private IReadOnlyList<IndexedSymbol> Step(
        long symbolId,
        string farEnd,
        string nearEnd,
        params RelationKind[] kinds)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            var kindList = KindList(command, kinds);
            command.CommandText = $"""
                {SelectSymbol}
                JOIN relations r ON {farEnd} = s.id
                WHERE {nearEnd} = @id AND r.kind IN {kindList}
                GROUP BY s.id
                ORDER BY s.display COLLATE NOCASE, s.fqn
                """;
            command.Parameters.AddWithValue("@id", symbolId);

            return ReadSymbols(command);
        }
    }

    // ---- endpoints ----------------------------------------------------------

    public IReadOnlyList<HttpEndpoint> GetEndpoints()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"{SelectEndpoint} ORDER BY e.route COLLATE NOCASE, e.http_method, e.id";

            return ReadEndpoints(command);
        }
    }

    // ---- diagnostics --------------------------------------------------------

    public IReadOnlyList<IndexDiagnostic> GetDiagnostics()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT severity, project, message FROM diagnostics ORDER BY id";

            using var reader = command.ExecuteReader();
            var results = new List<IndexDiagnostic>();

            while (reader.Read())
            {
                results.Add(new IndexDiagnostic(
                    Enum.TryParse<Model.DiagnosticSeverity>(reader.GetString(0), out var severity)
                        ? severity
                        : Model.DiagnosticSeverity.Info,
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetString(2)));
            }

            return results;
        }
    }

    // ---- readers ------------------------------------------------------------

    private const string SelectSymbol = """
        SELECT s.id, s.kind, s.name, s.fqn, s.display, p.name, s.namespace,
               s.container_fqn, s.file_path, s.line, s.start_column, s.accessibility
        FROM symbols s
        LEFT JOIN projects p ON p.id = s.project_id
        """;

    private static List<IndexedSymbol> ReadSymbols(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var results = new List<IndexedSymbol>();

        while (reader.Read())
        {
            results.Add(new IndexedSymbol
            {
                Id = reader.GetInt64(0),
                Kind = Enum.Parse<IndexedSymbolKind>(reader.GetString(1)),
                Name = reader.GetString(2),
                FullyQualifiedName = reader.GetString(3),
                Display = reader.GetString(4),
                ProjectName = reader.IsDBNull(5) ? null : reader.GetString(5),
                Namespace = reader.IsDBNull(6) ? null : reader.GetString(6),
                ContainerFullyQualifiedName = reader.IsDBNull(7) ? null : reader.GetString(7),
                FilePath = reader.IsDBNull(8) ? null : reader.GetString(8),
                Line = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                Column = reader.IsDBNull(10) ? null : reader.GetInt32(10),
                Accessibility = reader.IsDBNull(11) ? null : reader.GetString(11),
            });
        }

        return results;
    }

    private const string SelectEndpoint = """
        SELECT e.id, e.http_method, e.route, e.handler_display, e.handler_fqn, e.handler_symbol_id,
               e.kind, p.name, e.file_path, e.line,
               e.requires_auth, e.allows_anonymous, e.policies, e.roles, e.provenance
        FROM endpoints e
        LEFT JOIN projects p ON p.id = e.project_id
        """;

    private List<HttpEndpoint> ReadEndpoints(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var results = new List<HttpEndpoint>();

        while (reader.Read())
        {
            results.Add(new HttpEndpoint
            {
                Id = reader.GetInt64(0),
                HttpMethod = reader.GetString(1),
                Route = reader.GetString(2),
                HandlerDisplay = reader.GetString(3),
                HandlerFullyQualifiedName = reader.IsDBNull(4) ? null : reader.GetString(4),
                HandlerSymbolId = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                Kind = Enum.Parse<EndpointKind>(reader.GetString(6)),
                ProjectName = reader.IsDBNull(7) ? null : reader.GetString(7),
                FilePath = reader.IsDBNull(8) ? null : reader.GetString(8),
                Line = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                RequiresAuthorization = reader.GetInt32(10) != 0,
                AllowsAnonymous = reader.GetInt32(11) != 0,
                Policies = Split(reader.IsDBNull(12) ? null : reader.GetString(12)),
                Roles = Split(reader.IsDBNull(13) ? null : reader.GetString(13)),
                Provenance = ParseProvenance(reader.GetString(14)),
            });
        }

        reader.Close();

        return WithDependencies(results);
    }

    /// <summary>
    /// Attaches what each inline handler reaches, in one query rather than one per row.
    /// Only a Minimal API lambda has any, so most reads add nothing but the lookup.
    /// </summary>
    private List<HttpEndpoint> WithDependencies(List<HttpEndpoint> endpoints)
    {
        if (endpoints.Count == 0)
        {
            return endpoints;
        }

        using var command = _connection.CreateCommand();
        var idList = IdList(command, endpoints.Select(endpoint => endpoint.Id));
        command.CommandText = $"""
            SELECT d.endpoint_id, d.symbol_id, d.target_fqn, d.target_display
            FROM endpoint_dependencies d
            WHERE d.endpoint_id IN {idList}
            ORDER BY d.endpoint_id, d.id
            """;

        var byEndpoint = new Dictionary<long, List<SymbolLink>>();

        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                if (!byEndpoint.TryGetValue(id, out var links))
                {
                    byEndpoint[id] = links = [];
                }

                links.Add(new SymbolLink(
                    reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetString(3)));
            }
        }

        for (var i = 0; i < endpoints.Count; i++)
        {
            if (byEndpoint.TryGetValue(endpoints[i].Id, out var links))
            {
                endpoints[i] = endpoints[i] with { Dependencies = links };
            }
        }

        return endpoints;
    }

    private static IReadOnlyList<string> Split(string? value) => value is null
        ? []
        : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>An unrecognised value is treated as inferred: never over-claim exactness.</summary>
    private static RelationProvenance ParseProvenance(string value) =>
        Enum.TryParse<RelationProvenance>(value, out var provenance) ? provenance : RelationProvenance.Inferred;

    /// <summary>
    /// Binds a value per element and returns the SQL list to match against. An empty
    /// collection yields a list that matches nothing, rather than invalid SQL.
    /// </summary>
    private static string ParameterList(SqliteCommand command, string prefix, IEnumerable<object> values)
    {
        var names = new List<string>();

        foreach (var value in values)
        {
            var name = $"@{prefix}{names.Count}";
            command.Parameters.AddWithValue(name, value);
            names.Add(name);
        }

        return names.Count == 0 ? "(NULL)" : $"({string.Join(", ", names)})";
    }

    private static string KindList(SqliteCommand command, IEnumerable<RelationKind> kinds) =>
        ParameterList(command, "k", kinds.Select(kind => (object)kind.ToString()));

    private static string IdList(SqliteCommand command, IEnumerable<long> ids) =>
        ParameterList(command, "i", ids.Select(id => (object)id));

    public void Dispose() => _connection.Dispose();
}
