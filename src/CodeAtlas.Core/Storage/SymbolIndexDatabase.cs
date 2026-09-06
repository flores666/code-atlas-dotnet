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
    /// <summary>Cap on rows returned for the incoming-reference list of one symbol.</summary>
    public const int MaxIncomingReferences = 200;

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

    // ---- reading ------------------------------------------------------------

    public IReadOnlyList<IndexedProject> GetProjects()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, name, file_path, assembly_name, loaded
                FROM projects
                ORDER BY name COLLATE NOCASE
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

    /// <summary>
    /// Searches declarations by name. Exact matches rank first, then prefix matches,
    /// then substring matches; shorter names win ties.
    /// </summary>
    public IReadOnlyList<IndexedSymbol> Search(string query, int limit = 200)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return [];
            }

            var trimmed = query.Trim();
            var escaped = Escape(trimmed);

            using var command = _connection.CreateCommand();
            command.CommandText = $"""
                {SelectSymbol}
                WHERE s.name LIKE @contains ESCAPE '\'
                ORDER BY
                    CASE
                        WHEN s.name = @exact COLLATE NOCASE THEN 0
                        WHEN s.name LIKE @prefix ESCAPE '\' THEN 1
                        ELSE 2
                    END,
                    LENGTH(s.name),
                    s.name COLLATE NOCASE,
                    s.fqn
                LIMIT @limit
                """;
            command.Parameters.AddWithValue("@contains", $"%{escaped}%");
            command.Parameters.AddWithValue("@prefix", $"{escaped}%");
            command.Parameters.AddWithValue("@exact", trimmed);
            command.Parameters.AddWithValue("@limit", limit);

            return ReadSymbols(command);
        }
    }

    /// <summary>Escapes the LIKE wildcards so a query such as <c>_Foo</c> is literal.</summary>
    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

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

    public SymbolDetails? GetDetails(long id)
    {
        lock (_gate)
        {
            if (GetSymbol(id) is not { } symbol)
            {
                return null;
            }

            return new SymbolDetails
            {
                Symbol = symbol with { Attributes = GetAttributes(id) },
                BaseTypes = GetOutgoing(id, [RelationKind.Inherits]),
                Interfaces = GetOutgoing(id, [RelationKind.Implements]),
                Overrides = GetOutgoing(id, [RelationKind.Overrides]),
                Calls = GetOutgoing(id, [RelationKind.Calls]),
                References = GetOutgoing(id, [RelationKind.References]),
                ParameterTypes = GetOutgoing(id, [RelationKind.ParameterType]),
                ReturnTypes = GetOutgoing(id, [RelationKind.ReturnType]),
                ConstructorDependencies = GetConstructorDependencies(id),
                DerivedTypes = GetIncoming(id, [RelationKind.Inherits], int.MaxValue),
                Implementors = GetIncoming(id, [RelationKind.Implements], int.MaxValue),
                OverriddenBy = GetIncoming(id, [RelationKind.Overrides], int.MaxValue),
                CalledBy = GetIncoming(id, [RelationKind.Calls], MaxIncomingReferences),
                CalledByTotal = CountIncoming(id, [RelationKind.Calls]),
                ReferencedBy = GetIncoming(id, [RelationKind.References], MaxIncomingReferences),
                ReferencedByTotal = CountIncoming(id, [RelationKind.References]),
            };
        }
    }

    private IReadOnlyList<string> GetAttributes(long symbolId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT attribute_fqn FROM symbol_attributes
            WHERE symbol_id = @id
            ORDER BY attribute_fqn
            """;
        command.Parameters.AddWithValue("@id", symbolId);

        using var reader = command.ExecuteReader();
        var results = new List<string>();

        while (reader.Read())
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }

    private IReadOnlyList<SymbolLink> GetOutgoing(long symbolId, IReadOnlyCollection<RelationKind> kinds)
    {
        using var command = _connection.CreateCommand();
        var kindList = KindList(command, kinds);
        command.CommandText = $"""
            SELECT DISTINCT target_symbol_id, target_fqn, target_display, provenance
            FROM relations
            WHERE source_symbol_id = @id AND kind IN {kindList}
            ORDER BY target_display COLLATE NOCASE, target_fqn
            """;
        command.Parameters.AddWithValue("@id", symbolId);

        return ReadLinks(command);
    }

    private IReadOnlyList<SymbolLink> GetIncoming(long symbolId, IReadOnlyCollection<RelationKind> kinds, int limit)
    {
        using var command = _connection.CreateCommand();
        var kindList = KindList(command, kinds);
        command.CommandText = $"""
            SELECT DISTINCT s.id, s.fqn, s.display, r.provenance
            FROM relations r
            JOIN symbols s ON s.id = r.source_symbol_id
            WHERE r.target_symbol_id = @id AND r.kind IN {kindList}
            ORDER BY s.display COLLATE NOCASE, s.fqn
            LIMIT @limit
            """;
        command.Parameters.AddWithValue("@id", symbolId);
        command.Parameters.AddWithValue("@limit", limit);

        return ReadLinks(command);
    }

    private int CountIncoming(long symbolId, IReadOnlyCollection<RelationKind> kinds)
    {
        using var command = _connection.CreateCommand();
        var kindList = KindList(command, kinds);
        command.CommandText = $"""
            SELECT COUNT(DISTINCT source_symbol_id) FROM relations
            WHERE target_symbol_id = @id AND kind IN {kindList}
            """;
        command.Parameters.AddWithValue("@id", symbolId);

        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The parameter types of a type's own constructors: what an instance of it cannot be
    /// built without. Empty for anything that is not a type.
    /// </summary>
    private IReadOnlyList<SymbolLink> GetConstructorDependencies(long typeId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT r.target_symbol_id, r.target_fqn, r.target_display, r.provenance
            FROM symbols t
            JOIN symbols c ON c.container_fqn = t.fqn AND c.project_id = t.project_id AND c.kind = 'Constructor'
            JOIN relations r ON r.source_symbol_id = c.id AND r.kind = 'ParameterType'
            WHERE t.id = @id
            ORDER BY r.target_display COLLATE NOCASE, r.target_fqn
            """;
        command.Parameters.AddWithValue("@id", typeId);

        return ReadLinks(command);
    }

    // ---- navigation ---------------------------------------------------------

    /// <summary>Members that invoke this symbol.</summary>
    public IReadOnlyList<SymbolLink> FindCallers(long symbolId, int limit = MaxIncomingReferences)
    {
        lock (_gate)
        {
            return GetIncoming(symbolId, [RelationKind.Calls], limit);
        }
    }

    /// <summary>Methods and constructors this symbol invokes.</summary>
    public IReadOnlyList<SymbolLink> FindCallees(long symbolId)
    {
        lock (_gate)
        {
            return GetOutgoing(symbolId, [RelationKind.Calls]);
        }
    }

    /// <summary>
    /// Everything that mentions this symbol. Call sites count: they are references that
    /// happen to be invocations, and are stored as calls only so the two can be told apart.
    /// </summary>
    public IReadOnlyList<SymbolLink> FindReferences(long symbolId, int limit = MaxIncomingReferences)
    {
        lock (_gate)
        {
            return GetIncoming(symbolId, [RelationKind.References, RelationKind.Calls], limit);
        }
    }

    /// <summary>
    /// Types implementing this interface, members implementing this interface member, and
    /// members overriding this one — the same set an editor's "go to implementation" shows.
    /// </summary>
    public IReadOnlyList<SymbolLink> FindImplementations(long symbolId, int limit = int.MaxValue)
    {
        lock (_gate)
        {
            return GetIncoming(symbolId, [RelationKind.Implements, RelationKind.Overrides], limit);
        }
    }

    /// <summary>Types that derive from this type.</summary>
    public IReadOnlyList<SymbolLink> FindDerivedTypes(long symbolId, int limit = int.MaxValue)
    {
        lock (_gate)
        {
            return GetIncoming(symbolId, [RelationKind.Inherits], limit);
        }
    }

    // ---- graph primitives ---------------------------------------------------
    //
    // These exist for NeighborhoodBuilder, which owns how far a walk goes. They answer
    // exactly one hop each, so no query here can grow with the size of the solution.

    public IReadOnlyList<IndexedSymbol> GetSymbols(IReadOnlyCollection<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        lock (_gate)
        {
            if (ids.Count == 0)
            {
                return [];
            }

            using var command = _connection.CreateCommand();
            var idList = IdList(command, ids);
            command.CommandText = $"{SelectSymbol} WHERE s.id IN {idList}";

            return ReadSymbols(command);
        }
    }

    /// <summary>
    /// Edges touching any of <paramref name="ids"/>, in either direction, keeping at most
    /// <paramref name="maxPerSymbol"/> per anchor so one hub symbol cannot fill the graph
    /// on its own. Edges whose far end is outside the index are skipped: they have nothing
    /// to draw.
    /// </summary>
    public IReadOnlyList<RelationEdge> GetIncidentEdges(
        IReadOnlyCollection<long> ids,
        IReadOnlyCollection<RelationKind> kinds,
        int maxPerSymbol)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(kinds);

        lock (_gate)
        {
            if (ids.Count == 0 || kinds.Count == 0)
            {
                return [];
            }

            using var command = _connection.CreateCommand();
            var idList = IdList(command, ids);
            var kindList = KindList(command, kinds);

            command.CommandText = $"""
                WITH incident AS (
                    SELECT source_symbol_id AS anchor, source_symbol_id AS src,
                           target_symbol_id AS dst, kind, provenance
                    FROM relations
                    WHERE source_symbol_id IN {idList} AND kind IN {kindList}
                      AND target_symbol_id IS NOT NULL
                    UNION ALL
                    SELECT target_symbol_id AS anchor, source_symbol_id AS src,
                           target_symbol_id AS dst, kind, provenance
                    FROM relations
                    WHERE target_symbol_id IN {idList} AND kind IN {kindList}
                ),
                ranked AS (
                    SELECT src, dst, kind, provenance,
                           ROW_NUMBER() OVER (PARTITION BY anchor ORDER BY kind, dst, src) AS rank
                    FROM incident
                )
                SELECT src, dst, kind, provenance FROM ranked WHERE rank <= @max
                """;
            command.Parameters.AddWithValue("@max", maxPerSymbol);

            return ReadEdges(command);
        }
    }

    /// <summary>Edges with both ends inside <paramref name="ids"/>, which is what the graph draws.</summary>
    public IReadOnlyList<RelationEdge> GetInternalEdges(
        IReadOnlyCollection<long> ids,
        IReadOnlyCollection<RelationKind> kinds)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(kinds);

        lock (_gate)
        {
            if (ids.Count == 0 || kinds.Count == 0)
            {
                return [];
            }

            using var command = _connection.CreateCommand();
            var idList = IdList(command, ids);
            var kindList = KindList(command, kinds);

            command.CommandText = $"""
                SELECT source_symbol_id, target_symbol_id, kind, provenance
                FROM relations
                WHERE source_symbol_id IN {idList}
                  AND target_symbol_id IN {idList}
                  AND kind IN {kindList}
                """;

            return ReadEdges(command);
        }
    }

    /// <summary>
    /// How many distinct neighbours each symbol has under <paramref name="kinds"/>. The
    /// graph subtracts what it already shows to decide which nodes are worth expanding.
    /// </summary>
    public IReadOnlyDictionary<long, int> CountNeighbours(
        IReadOnlyCollection<long> ids,
        IReadOnlyCollection<RelationKind> kinds)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(kinds);

        lock (_gate)
        {
            var counts = new Dictionary<long, int>();
            if (ids.Count == 0 || kinds.Count == 0)
            {
                return counts;
            }

            using var command = _connection.CreateCommand();
            var idList = IdList(command, ids);
            var kindList = KindList(command, kinds);

            // UNION, not UNION ALL: a pair linked in both directions is one neighbour.
            command.CommandText = $"""
                SELECT anchor, COUNT(*) FROM (
                    SELECT source_symbol_id AS anchor, target_symbol_id AS other
                    FROM relations
                    WHERE source_symbol_id IN {idList} AND kind IN {kindList}
                      AND target_symbol_id IS NOT NULL
                    UNION
                    SELECT target_symbol_id AS anchor, source_symbol_id AS other
                    FROM relations
                    WHERE target_symbol_id IN {idList} AND kind IN {kindList}
                )
                GROUP BY anchor
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                counts[reader.GetInt64(0)] = reader.GetInt32(1);
            }

            return counts;
        }
    }

    public IReadOnlyList<IndexDiagnostic> GetDiagnostics()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT severity, project, message FROM diagnostics ORDER BY id
                """;

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

    private static List<RelationEdge> ReadEdges(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var results = new List<RelationEdge>();

        while (reader.Read())
        {
            results.Add(new RelationEdge(
                reader.GetInt64(0),
                reader.GetInt64(1),
                Enum.Parse<RelationKind>(reader.GetString(2)),
                ParseProvenance(reader.GetString(3))));
        }

        return results;
    }

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

    private static List<SymbolLink> ReadLinks(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var results = new List<SymbolLink>();

        while (reader.Read())
        {
            results.Add(new SymbolLink(
                reader.IsDBNull(0) ? null : reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                ParseProvenance(reader.GetString(3))));
        }

        return results;
    }

    public void Dispose() => _connection.Dispose();
}
