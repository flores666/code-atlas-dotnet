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
                Injects = GetOutgoing(id, [RelationKind.Injects]),
                Resolves = GetOutgoing(id, [RelationKind.Resolves]),
                InjectedBy = GetIncoming(id, [RelationKind.Injects], int.MaxValue),
                ResolvedBy = GetIncoming(id, [RelationKind.Resolves], int.MaxValue),
                DerivedTypes = GetIncoming(id, [RelationKind.Inherits], int.MaxValue),
                Implementors = GetIncoming(id, [RelationKind.Implements], int.MaxValue),
                OverriddenBy = GetIncoming(id, [RelationKind.Overrides], int.MaxValue),
                CalledBy = GetIncoming(id, [RelationKind.Calls], MaxIncomingReferences),
                CalledByTotal = CountIncoming(id, [RelationKind.Calls]),
                ReferencedBy = GetIncoming(id, [RelationKind.References], MaxIncomingReferences),
                ReferencedByTotal = CountIncoming(id, [RelationKind.References]),

                DeclaredEntities = GetOutgoing(id, [RelationKind.DeclaresEntity]),
                ConfiguredEntities = GetOutgoing(id, [RelationKind.ConfiguresEntity]),
                DeclaredBy = GetIncoming(id, [RelationKind.DeclaresEntity, RelationKind.ConfiguresEntity], int.MaxValue),
                RelatedEntities = GetOutgoing(id, [RelationKind.RelatesToEntity]),
                ReadsEntities = GetOutgoing(id, [RelationKind.ReadsEntity]),
                WritesEntities = GetOutgoing(
                    id, [RelationKind.CreatesEntity, RelationKind.ModifiesEntity, RelationKind.DeletesEntity]),
                Readers = GetIncoming(id, [RelationKind.ReadsEntity], MaxIncomingReferences),
                Writers = GetIncoming(
                    id,
                    [RelationKind.CreatesEntity, RelationKind.ModifiesEntity, RelationKind.DeletesEntity],
                    MaxIncomingReferences),

                ReadsConfiguration = GetOutgoing(id, [RelationKind.ReadsConfiguration]),
                ConfigurationReaders = GetIncoming(id, [RelationKind.ReadsConfiguration], int.MaxValue),
                UsesExternal = GetOutgoing(id, [RelationKind.UsesExternal]),
                ExternalConsumers = GetIncoming(id, [RelationKind.UsesExternal], int.MaxValue),

                Registrations =
                [
                    .. ReadRegistrations($"{SelectRegistration} WHERE service_symbol_id = @id ORDER BY id", id),
                    .. ReadRegistrations($"{SelectRegistration} WHERE impl_symbol_id = @id ORDER BY id", id),
                ],
                EntityMappings = ReadEntities(
                    $"{SelectEntity} WHERE e.entity_symbol_id = @id OR e.context_symbol_id = @id " +
                    "OR e.config_symbol_id = @id ORDER BY e.entity_display COLLATE NOCASE, e.id", id),
                Migrations = ReadMigrations(
                    $"{SelectMigration} WHERE m.context_symbol_id = @id OR m.type_symbol_id = @id " +
                    "ORDER BY m.name, m.id", id),
                Configuration = ReadConfiguration(
                    $"{SelectConfiguration} WHERE c.options_symbol_id = @id OR c.consumer_symbol_id = @id " +
                    "ORDER BY c.config_key COLLATE NOCASE, c.id", id),
                ExternalDependencies = ReadExternal(
                    $"{SelectExternal} WHERE x.consumer_symbol_id = @id OR x.client_symbol_id = @id " +
                    "ORDER BY x.technology, x.id", id),

                RelatedEndpoints = FindUpstreamEndpoints(id),
                RelatedServices = FindUpstreamServices(id),
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

    // ---- composition --------------------------------------------------------

    /// <summary>
    /// Every DI registration in the workspace, ordered so one service's registrations sit
    /// together.
    /// </summary>
    public IReadOnlyList<ServiceRegistration> GetRegistrations()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"{SelectRegistration} ORDER BY service_display COLLATE NOCASE, service_fqn, id";

            return ReadRegistrations(command);
        }
    }

    /// <summary>
    /// What is registered for one service type. Several rows are normal: a service may be
    /// registered more than once, and the container keeps them all.
    /// </summary>
    public IReadOnlyList<ServiceRegistration> GetRegistrationsForService(long serviceSymbolId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"{SelectRegistration} WHERE service_symbol_id = @id ORDER BY id";
            command.Parameters.AddWithValue("@id", serviceSymbolId);

            return ReadRegistrations(command);
        }
    }

    /// <summary>Every EF Core entity the index found, ordered by name.</summary>
    public IReadOnlyList<EntityMapping> GetEntities() =>
        Read(() => ReadEntities($"{SelectEntity} ORDER BY e.entity_display COLLATE NOCASE, e.id"));

    public IReadOnlyList<DataMigration> GetMigrations() =>
        Read(() => ReadMigrations($"{SelectMigration} ORDER BY m.name, m.id"));

    /// <summary>Every configuration read, ordered so one key's readers sit together.</summary>
    public IReadOnlyList<ConfigurationUsage> GetConfigurationUsages() =>
        Read(() => ReadConfiguration(
            $"{SelectConfiguration} ORDER BY c.config_key COLLATE NOCASE, c.options_display COLLATE NOCASE, c.id"));

    /// <summary>Every infrastructure boundary, grouped by the technology behind it.</summary>
    public IReadOnlyList<ExternalDependency> GetExternalDependencies() =>
        Read(() => ReadExternal(
            $"{SelectExternal} ORDER BY x.technology, x.consumer_display COLLATE NOCASE, x.id"));

    /// <summary>The registrations that name a type as an implementation of something.</summary>
    public IReadOnlyList<ServiceRegistration> GetRegistrationsForImplementation(long implementationSymbolId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"{SelectRegistration} WHERE impl_symbol_id = @id ORDER BY id";
            command.Parameters.AddWithValue("@id", implementationSymbolId);

            return ReadRegistrations(command);
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

    /// <summary>
    /// The services an endpoint's handler can reach directly: what its declaring type is
    /// constructed with. For a Minimal API lambda there is no declaring type and so no
    /// dependencies to report.
    /// </summary>
    public IReadOnlyList<SymbolLink> GetEndpointDependencies(long endpointId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT DISTINCT r.target_symbol_id, r.target_fqn, r.target_display, r.provenance
                FROM endpoints e
                JOIN relations r ON r.source_symbol_id = e.declaring_id AND r.kind = 'Injects'
                WHERE e.id = @id
                ORDER BY r.target_display COLLATE NOCASE, r.target_fqn
                """;
            command.Parameters.AddWithValue("@id", endpointId);

            return ReadLinks(command);
        }
    }

    // ---- flow ---------------------------------------------------------------

    /// <summary>
    /// The components a symbol can be reached from, walked backwards along composition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The seed is what names the symbol, widened to the type that names it: a flow is
    /// read between components, and an entity is read by a repository <em>method</em>
    /// whose consumers are wired to its type.
    /// </para>
    /// <para>
    /// From there only <c>Injects</c> and <c>Resolves</c> are followed. That chain is what
    /// an endpoint's flow is actually made of, and it is short and narrow; following calls
    /// backwards would not be, and one hot method would put the whole solution in the
    /// answer.
    /// </para>
    /// </remarks>
    private const string UpstreamReach = """
        WITH RECURSIVE
        seed(id) AS (
            SELECT @id
            UNION
            SELECT COALESCE(owner.id, member.id)
            FROM relations r
            JOIN symbols member ON member.id = r.source_symbol_id
            LEFT JOIN symbols owner ON owner.fqn = member.container_fqn
            WHERE r.target_symbol_id = @id
              AND r.kind IN ('ReadsEntity', 'CreatesEntity', 'ModifiesEntity', 'DeletesEntity',
                             'DeclaresEntity', 'ConfiguresEntity', 'ReadsConfiguration',
                             'UsesExternal', 'Injects', 'Resolves', 'Calls')
        ),
        reach(id, depth) AS (
            SELECT id, 0 FROM seed
            UNION
            SELECT r.source_symbol_id, reach.depth + 1
            FROM reach
            JOIN relations r ON r.target_symbol_id = reach.id
            WHERE reach.depth < @depth AND r.kind IN ('Injects', 'Resolves')
        )
        """;

    /// <summary>How far the composition chain from an endpoint down to a resource is followed.</summary>
    private const int MaxFlowDepth = 6;

    /// <summary>HTTP endpoints whose flow reaches this symbol.</summary>
    private List<HttpEndpoint> FindUpstreamEndpoints(long symbolId, int limit = 50)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            {UpstreamReach}
            {SelectEndpoint}
            WHERE e.declaring_id IN (SELECT id FROM reach)
               OR e.handler_symbol_id IN (SELECT id FROM reach)
            ORDER BY e.route COLLATE NOCASE, e.http_method, e.id
            LIMIT @limit
            """;
        command.Parameters.AddWithValue("@id", symbolId);
        command.Parameters.AddWithValue("@depth", MaxFlowDepth);
        command.Parameters.AddWithValue("@limit", limit);

        return ReadEndpoints(command);
    }

    /// <summary>Registered services on the composition path down to this symbol.</summary>
    private List<SymbolLink> FindUpstreamServices(long symbolId, int limit = 50)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            {UpstreamReach}
            SELECT DISTINCT s.id, s.fqn, s.display, 'Exact'
            FROM reach
            JOIN symbols s ON s.id = reach.id
            WHERE s.id <> @id
              AND EXISTS (
                  SELECT 1 FROM service_registrations g
                  WHERE g.service_symbol_id = s.id OR g.impl_symbol_id = s.id)
            ORDER BY s.display COLLATE NOCASE, s.fqn
            LIMIT @limit
            """;
        command.Parameters.AddWithValue("@id", symbolId);
        command.Parameters.AddWithValue("@depth", MaxFlowDepth);
        command.Parameters.AddWithValue("@limit", limit);

        return ReadLinks(command);
    }

    /// <summary>
    /// A short infrastructure label per symbol, for the graph to badge its nodes with:
    /// the table an entity maps to, the technology a boundary type talks to, or the fact
    /// that a type is bound from configuration. Only symbols that have one are returned.
    /// </summary>
    public IReadOnlyDictionary<long, string> GetInfrastructureLabels(IReadOnlyCollection<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        lock (_gate)
        {
            var labels = new Dictionary<long, string>();
            if (ids.Count == 0)
            {
                return labels;
            }

            using var command = _connection.CreateCommand();
            var idList = IdList(command, ids);

            // Ordered least to most specific: a later row overwrites an earlier one, so an
            // entity that names its table reads as the table rather than as "entity".
            command.CommandText = $"""
                SELECT id, label FROM (
                    SELECT options_symbol_id AS id, 'options' AS label, 0 AS rank
                    FROM configuration_usages
                    WHERE options_symbol_id IN {idList}
                    UNION ALL
                    SELECT entity_symbol_id,
                           CASE WHEN table_name IS NULL THEN 'entity'
                                WHEN schema_name IS NULL THEN table_name
                                ELSE schema_name || '.' || table_name END,
                           CASE WHEN table_name IS NULL THEN 1 ELSE 2 END
                    FROM data_entities
                    WHERE entity_symbol_id IN {idList}
                )
                ORDER BY rank
                """;

            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (!reader.IsDBNull(0))
                    {
                        labels[reader.GetInt64(0)] = reader.GetString(1);
                    }
                }
            }

            using var boundaries = _connection.CreateCommand();
            var boundaryIds = IdList(boundaries, ids);
            boundaries.CommandText = $"""
                SELECT consumer_symbol_id, technology, name
                FROM external_dependencies
                WHERE consumer_symbol_id IN {boundaryIds}
                ORDER BY id
                """;

            using (var reader = boundaries.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (reader.IsDBNull(0))
                    {
                        continue;
                    }

                    var technology = Enum.TryParse<ExternalTechnology>(reader.GetString(1), out var parsed)
                        ? parsed
                        : ExternalTechnology.HttpApi;

                    labels[reader.GetInt64(0)] = new ExternalDependency
                    {
                        Technology = technology,
                        Binding = ExternalBinding.Call,
                        ClientFullyQualifiedName = string.Empty,
                        ClientDisplay = string.Empty,
                        ConsumerFullyQualifiedName = string.Empty,
                        ConsumerDisplay = string.Empty,
                        Name = reader.IsDBNull(2) ? null : reader.GetString(2),
                    }.Resource;
                }
            }

            return labels;
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

    private const string SelectRegistration = """
        SELECT id, service_fqn, service_display, service_symbol_id,
               impl_fqn, impl_display, impl_symbol_id,
               lifetime, kind, provenance, file_path, line, declaring_member
        FROM service_registrations
        """;

    private static List<ServiceRegistration> ReadRegistrations(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var results = new List<ServiceRegistration>();

        while (reader.Read())
        {
            results.Add(new ServiceRegistration
            {
                Id = reader.GetInt64(0),
                ServiceFullyQualifiedName = reader.GetString(1),
                ServiceDisplay = reader.GetString(2),
                ServiceSymbolId = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                ImplementationFullyQualifiedName = reader.IsDBNull(4) ? null : reader.GetString(4),
                ImplementationDisplay = reader.IsDBNull(5) ? null : reader.GetString(5),
                ImplementationSymbolId = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                Lifetime = Enum.Parse<ServiceLifetime>(reader.GetString(7)),
                Kind = Enum.Parse<RegistrationKind>(reader.GetString(8)),
                Provenance = ParseProvenance(reader.GetString(9)),
                FilePath = reader.IsDBNull(10) ? null : reader.GetString(10),
                Line = reader.IsDBNull(11) ? null : reader.GetInt32(11),
                DeclaringMember = reader.IsDBNull(12) ? null : reader.GetString(12),
            });
        }

        return results;
    }

    private const string SelectEndpoint = """
        SELECT e.id, e.http_method, e.route, e.handler_display, e.handler_fqn, e.handler_symbol_id,
               e.declaring_fqn, e.declaring_id, e.kind, p.name, e.file_path, e.line,
               e.requires_auth, e.allows_anonymous, e.policies, e.roles, e.provenance
        FROM endpoints e
        LEFT JOIN projects p ON p.id = e.project_id
        """;

    private static List<HttpEndpoint> ReadEndpoints(SqliteCommand command)
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
                DeclaringTypeFullyQualifiedName = reader.IsDBNull(6) ? null : reader.GetString(6),
                DeclaringTypeSymbolId = reader.IsDBNull(7) ? null : reader.GetInt64(7),
                Kind = Enum.Parse<EndpointKind>(reader.GetString(8)),
                ProjectName = reader.IsDBNull(9) ? null : reader.GetString(9),
                FilePath = reader.IsDBNull(10) ? null : reader.GetString(10),
                Line = reader.IsDBNull(11) ? null : reader.GetInt32(11),
                RequiresAuthorization = reader.GetInt32(12) != 0,
                AllowsAnonymous = reader.GetInt32(13) != 0,
                Policies = Split(reader.IsDBNull(14) ? null : reader.GetString(14)),
                Roles = Split(reader.IsDBNull(15) ? null : reader.GetString(15)),
                Provenance = ParseProvenance(reader.GetString(16)),
            });
        }

        return results;
    }

    private const string SelectEntity = """
        SELECT e.id, e.entity_fqn, e.entity_display, e.entity_symbol_id,
               e.context_fqn, e.context_display, e.context_symbol_id, e.set_name,
               e.table_name, e.schema_name, e.config_fqn, e.config_display, e.config_symbol_id,
               p.name, e.file_path, e.line
        FROM data_entities e
        LEFT JOIN projects p ON p.id = e.project_id
        """;

    private static List<EntityMapping> ReadEntities(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var results = new List<EntityMapping>();

        while (reader.Read())
        {
            results.Add(new EntityMapping
            {
                Id = reader.GetInt64(0),
                EntityFullyQualifiedName = reader.GetString(1),
                EntityDisplay = reader.GetString(2),
                EntitySymbolId = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                ContextFullyQualifiedName = reader.IsDBNull(4) ? null : reader.GetString(4),
                ContextDisplay = reader.IsDBNull(5) ? null : reader.GetString(5),
                ContextSymbolId = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                SetName = reader.IsDBNull(7) ? null : reader.GetString(7),
                TableName = reader.IsDBNull(8) ? null : reader.GetString(8),
                Schema = reader.IsDBNull(9) ? null : reader.GetString(9),
                ConfigurationFullyQualifiedName = reader.IsDBNull(10) ? null : reader.GetString(10),
                ConfigurationDisplay = reader.IsDBNull(11) ? null : reader.GetString(11),
                ConfigurationSymbolId = reader.IsDBNull(12) ? null : reader.GetInt64(12),
                ProjectName = reader.IsDBNull(13) ? null : reader.GetString(13),
                FilePath = reader.IsDBNull(14) ? null : reader.GetString(14),
                Line = reader.IsDBNull(15) ? null : reader.GetInt32(15),
            });
        }

        return results;
    }

    private const string SelectMigration = """
        SELECT m.id, m.name, m.type_fqn, m.type_display, m.type_symbol_id,
               m.context_fqn, m.context_display, m.context_symbol_id, m.tables,
               p.name, m.file_path, m.line
        FROM data_migrations m
        LEFT JOIN projects p ON p.id = m.project_id
        """;

    private static List<DataMigration> ReadMigrations(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var results = new List<DataMigration>();

        while (reader.Read())
        {
            results.Add(new DataMigration
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                TypeFullyQualifiedName = reader.GetString(2),
                TypeDisplay = reader.GetString(3),
                TypeSymbolId = reader.IsDBNull(4) ? null : reader.GetInt64(4),
                ContextFullyQualifiedName = reader.IsDBNull(5) ? null : reader.GetString(5),
                ContextDisplay = reader.IsDBNull(6) ? null : reader.GetString(6),
                ContextSymbolId = reader.IsDBNull(7) ? null : reader.GetInt64(7),
                Tables = Split(reader.IsDBNull(8) ? null : reader.GetString(8)),
                ProjectName = reader.IsDBNull(9) ? null : reader.GetString(9),
                FilePath = reader.IsDBNull(10) ? null : reader.GetString(10),
                Line = reader.IsDBNull(11) ? null : reader.GetInt32(11),
            });
        }

        return results;
    }

    private const string SelectConfiguration = """
        SELECT c.id, c.access, c.config_key, c.options_fqn, c.options_display, c.options_symbol_id,
               c.consumer_fqn, c.consumer_display, c.consumer_symbol_id,
               p.name, c.file_path, c.line, c.provenance
        FROM configuration_usages c
        LEFT JOIN projects p ON p.id = c.project_id
        """;

    private static List<ConfigurationUsage> ReadConfiguration(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var results = new List<ConfigurationUsage>();

        while (reader.Read())
        {
            results.Add(new ConfigurationUsage
            {
                Id = reader.GetInt64(0),
                Access = Enum.Parse<ConfigurationAccess>(reader.GetString(1)),
                Key = reader.IsDBNull(2) ? null : reader.GetString(2),
                OptionsFullyQualifiedName = reader.IsDBNull(3) ? null : reader.GetString(3),
                OptionsDisplay = reader.IsDBNull(4) ? null : reader.GetString(4),
                OptionsSymbolId = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                ConsumerFullyQualifiedName = reader.IsDBNull(6) ? null : reader.GetString(6),
                ConsumerDisplay = reader.IsDBNull(7) ? null : reader.GetString(7),
                ConsumerSymbolId = reader.IsDBNull(8) ? null : reader.GetInt64(8),
                ProjectName = reader.IsDBNull(9) ? null : reader.GetString(9),
                FilePath = reader.IsDBNull(10) ? null : reader.GetString(10),
                Line = reader.IsDBNull(11) ? null : reader.GetInt32(11),
                Provenance = ParseProvenance(reader.GetString(12)),
            });
        }

        return results;
    }

    private const string SelectExternal = """
        SELECT x.id, x.technology, x.binding, x.client_fqn, x.client_display, x.client_symbol_id,
               x.name, x.consumer_fqn, x.consumer_display, x.consumer_symbol_id,
               p.name, x.file_path, x.line, x.provenance
        FROM external_dependencies x
        LEFT JOIN projects p ON p.id = x.project_id
        """;

    private static List<ExternalDependency> ReadExternal(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var results = new List<ExternalDependency>();

        while (reader.Read())
        {
            results.Add(new ExternalDependency
            {
                Id = reader.GetInt64(0),
                Technology = Enum.Parse<ExternalTechnology>(reader.GetString(1)),
                Binding = Enum.Parse<ExternalBinding>(reader.GetString(2)),
                ClientFullyQualifiedName = reader.GetString(3),
                ClientDisplay = reader.GetString(4),
                ClientSymbolId = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                Name = reader.IsDBNull(6) ? null : reader.GetString(6),
                ConsumerFullyQualifiedName = reader.GetString(7),
                ConsumerDisplay = reader.GetString(8),
                ConsumerSymbolId = reader.IsDBNull(9) ? null : reader.GetInt64(9),
                ProjectName = reader.IsDBNull(10) ? null : reader.GetString(10),
                FilePath = reader.IsDBNull(11) ? null : reader.GetString(11),
                Line = reader.IsDBNull(12) ? null : reader.GetInt32(12),
                Provenance = ParseProvenance(reader.GetString(13)),
            });
        }

        return results;
    }

    /// <summary>Runs a query under the read gate, which is what makes one instance shareable.</summary>
    private T Read<T>(Func<T> query)
    {
        lock (_gate)
        {
            return query();
        }
    }

    /// <summary>Binds an optional row id and hands the command to one of the readers above.</summary>
    private List<T> Query<T>(string sql, long? id, Func<SqliteCommand, List<T>> read)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;

        if (id is { } value)
        {
            command.Parameters.AddWithValue("@id", value);
        }

        return read(command);
    }

    private List<ServiceRegistration> ReadRegistrations(string sql, long? id = null) =>
        Query(sql, id, ReadRegistrations);

    private List<EntityMapping> ReadEntities(string sql, long? id = null) => Query(sql, id, ReadEntities);

    private List<DataMigration> ReadMigrations(string sql, long? id = null) => Query(sql, id, ReadMigrations);

    private List<ConfigurationUsage> ReadConfiguration(string sql, long? id = null) =>
        Query(sql, id, ReadConfiguration);

    private List<ExternalDependency> ReadExternal(string sql, long? id = null) => Query(sql, id, ReadExternal);

    private static IReadOnlyList<string> Split(string? value) => value is null
        ? []
        : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

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
