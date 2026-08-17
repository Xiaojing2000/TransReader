using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace TransReader.Core.Library;

public sealed class LibraryRepository
{
    private const int CurrentSchemaVersion = 2;
    private readonly string _databasePath;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private static readonly Lazy<bool> SqliteInitialized = new(() =>
    {
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
        return true;
    });

    public LibraryRepository(string databasePath)
    {
        _ = SqliteInitialized.Value;
        _databasePath = databasePath;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using (var bootCommand = connection.CreateCommand())
            {
                bootCommand.CommandText = """
                    CREATE TABLE IF NOT EXISTS schema_info (version INTEGER NOT NULL);
                    INSERT INTO schema_info(version) SELECT 0 WHERE NOT EXISTS (SELECT 1 FROM schema_info);
                    """;
                await bootCommand.ExecuteNonQueryAsync(cancellationToken);
            }
            var dbVersion = await ReadSchemaVersionAsync(connection, cancellationToken);
            if (dbVersion == 0)
            {
                // 全新库：建立 v1 基线 schema（幂等 CREATE IF NOT EXISTS，旧库不会重复创建）。
                await using var baseCommand = connection.CreateCommand();
                baseCommand.CommandText = BaseSchemaSql;
                await baseCommand.ExecuteNonQueryAsync(cancellationToken);
            }
            // 迁移从 v1 起（全新库刚建 v1 基线，旧库从其记录版本起）。
            var migrateFrom = dbVersion == 0 ? 1 : dbVersion;
            if (migrateFrom < CurrentSchemaVersion)
            {
                await SchemaMigrator.RunAsync(connection, migrateFrom, CurrentSchemaVersion, null, cancellationToken);
            }
            if (dbVersion != CurrentSchemaVersion)
            {
                await using var versionCommand = connection.CreateCommand();
                versionCommand.CommandText = "UPDATE schema_info SET version = $version";
                versionCommand.Parameters.AddWithValue("$version", CurrentSchemaVersion);
                await versionCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>读取当前 schema 版本（供测试与诊断；要求已 Initialize）。</summary>
    internal async Task<int> ReadSchemaVersionAsync(CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            return await ReadSchemaVersionAsync(connection, cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async Task<int> ReadSchemaVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_info LIMIT 1";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long value ? (int)value : 0;
    }

    private const string BaseSchemaSql = """
        CREATE TABLE IF NOT EXISTS folders (
            id TEXT PRIMARY KEY,
            parent_id TEXT NULL REFERENCES folders(id) ON DELETE RESTRICT,
            name TEXT NOT NULL,
            depth INTEGER NOT NULL CHECK(depth BETWEEN 1 AND 3),
            sort_order INTEGER NOT NULL DEFAULT 0,
            created_by TEXT NOT NULL,
            created_at TEXT NOT NULL,
            UNIQUE(parent_id, name)
        );

        CREATE TABLE IF NOT EXISTS documents (
            id TEXT PRIMARY KEY,
            content_hash TEXT NOT NULL UNIQUE,
            managed_path TEXT NOT NULL UNIQUE,
            title TEXT NOT NULL,
            authors TEXT NOT NULL DEFAULT '',
            publication_year INTEGER NULL,
            page_count INTEGER NOT NULL,
            ai_summary TEXT NOT NULL DEFAULT '',
            folder_id TEXT NULL REFERENCES folders(id) ON DELETE SET NULL,
            reading_status TEXT NOT NULL DEFAULT 'ToRead',
            is_favorite INTEGER NOT NULL DEFAULT 0,
            analysis_status TEXT NOT NULL DEFAULT 'Pending',
            added_at TEXT NOT NULL,
            first_opened_at TEXT NULL,
            last_opened_at TEXT NULL,
            open_count INTEGER NOT NULL DEFAULT 0,
            last_page_index INTEGER NOT NULL DEFAULT 0,
            progress REAL NOT NULL DEFAULT 0,
            file_size INTEGER NOT NULL,
            is_trashed INTEGER NOT NULL DEFAULT 0,
            trashed_at TEXT NULL,
            manual_metadata INTEGER NOT NULL DEFAULT 0,
            manual_classification INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS sources (
            document_id TEXT NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
            file_path TEXT NOT NULL,
            last_seen_at TEXT NOT NULL,
            is_available INTEGER NOT NULL DEFAULT 1,
            PRIMARY KEY(document_id, file_path)
        );

        CREATE TABLE IF NOT EXISTS tags (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL UNIQUE COLLATE NOCASE
        );

        CREATE TABLE IF NOT EXISTS document_tags (
            document_id TEXT NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
            tag_id INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
            PRIMARY KEY(document_id, tag_id)
        );

        CREATE TABLE IF NOT EXISTS classification_proposals (
            document_id TEXT PRIMARY KEY REFERENCES documents(id) ON DELETE CASCADE,
            suggested_path TEXT NOT NULL,
            confidence REAL NOT NULL,
            reason TEXT NOT NULL,
            needs_new_folder INTEGER NOT NULL,
            status TEXT NOT NULL,
            model_version TEXT NOT NULL,
            created_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS legacy_issues (
            file_path TEXT PRIMARY KEY,
            title TEXT NOT NULL,
            reason TEXT NOT NULL,
            imported_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS metadata (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_documents_folder ON documents(folder_id);
        CREATE INDEX IF NOT EXISTS ix_documents_last_opened ON documents(last_opened_at DESC);
        CREATE INDEX IF NOT EXISTS ix_sources_path ON sources(file_path COLLATE NOCASE);
        """;

    public async Task<LibraryDocument?> FindByHashAsync(string hash, CancellationToken cancellationToken = default) =>
        (await LoadDocumentsAsync("d.content_hash = $value", [("$value", hash)], cancellationToken)).SingleOrDefault();

    public async Task<LibraryDocument?> FindByIdAsync(string id, CancellationToken cancellationToken = default) =>
        (await LoadDocumentsAsync("d.id = $value", [("$value", id)], cancellationToken)).SingleOrDefault();

    public async Task<LibraryDocument?> FindByManagedPathAsync(string path, CancellationToken cancellationToken = default) =>
        (await LoadDocumentsAsync("d.managed_path = $value COLLATE NOCASE",
            [("$value", Path.GetFullPath(path))], cancellationToken)).SingleOrDefault();

    public async Task<LibraryDocument?> FindByAnyPathAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var documents = await LoadDocumentsAsync(
            "d.managed_path = $value COLLATE NOCASE OR EXISTS (SELECT 1 FROM sources sx WHERE sx.document_id = d.id AND sx.file_path = $value COLLATE NOCASE)",
            [("$value", fullPath)], cancellationToken);
        return documents.SingleOrDefault();
    }

    public Task<IReadOnlyList<LibraryDocument>> GetAllDocumentsAsync(CancellationToken cancellationToken = default) =>
        LoadDocumentsAsync("1 = 1", [], cancellationToken);

    /// <summary>按给定 SQL WHERE 条件查询文档（供 LibraryQueryService 下推导航/筛选条件）。</summary>
    public Task<IReadOnlyList<LibraryDocument>> QueryDocumentsAsync(
        string where,
        IReadOnlyList<(string Name, object Value)> parameters,
        CancellationToken cancellationToken = default) =>
        LoadDocumentsAsync(where, parameters, cancellationToken);

    public async Task<LibraryDocument> AddImportedDocumentAsync(
        string hash, string managedPath, string sourcePath, string title, uint pageCount, long fileSize,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var now = Utc(DateTime.UtcNow);
            var id = Guid.NewGuid().ToString("N");
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    INSERT INTO documents(id, content_hash, managed_path, title, page_count, added_at, file_size)
                    VALUES($id, $hash, $managed, $title, $pages, $now, $size);
                    """;
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$hash", hash);
                command.Parameters.AddWithValue("$managed", Path.GetFullPath(managedPath));
                command.Parameters.AddWithValue("$title", title);
                command.Parameters.AddWithValue("$pages", (long)pageCount);
                command.Parameters.AddWithValue("$now", now);
                command.Parameters.AddWithValue("$size", fileSize);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await UpsertSourceAsync(connection, (SqliteTransaction)transaction, id, sourcePath, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await FindByIdAsync(id, cancellationToken))!;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task AddSourceAsync(string documentId, string sourcePath, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await UpsertSourceAsync(connection, null, documentId, sourcePath, cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task RecordOpenedAsync(string documentId, uint pageCount, CancellationToken cancellationToken = default)
    {
        await ExecuteWriteAsync("""
            UPDATE documents
            SET first_opened_at = COALESCE(first_opened_at, $now), last_opened_at = $now,
                open_count = open_count + 1, page_count = $pages, is_trashed = 0, trashed_at = NULL
            WHERE id = $id;
            """, cancellationToken, ("$now", Utc(DateTime.UtcNow)), ("$pages", (long)pageCount), ("$id", documentId));
    }

    public async Task UpdateReadingProgressAsync(string documentId, uint pageIndex, uint pageCount, CancellationToken cancellationToken = default)
    {
        var progress = pageCount == 0 ? 0d : Math.Clamp((pageIndex + 1d) / pageCount, 0d, 1d);
        var status = progress >= .98 ? LibraryReadingStatus.Read : pageIndex > 0 ? LibraryReadingStatus.Reading : LibraryReadingStatus.ToRead;
        await ExecuteWriteAsync("""
            UPDATE documents SET last_page_index = $page, progress = $progress,
                reading_status = CASE WHEN reading_status = 'Read' THEN reading_status ELSE $status END
            WHERE id = $id;
            """, cancellationToken, ("$page", (long)pageIndex), ("$progress", progress),
            ("$status", status.ToString()), ("$id", documentId));
    }

    public async Task SetReadingStatusAsync(
        IReadOnlyList<string> documentIds,
        LibraryReadingStatus status,
        CancellationToken cancellationToken = default)
    {
        await ExecuteForDocumentsAsync(
            "UPDATE documents SET reading_status = $value WHERE id = $id;",
            documentIds, cancellationToken, ("$value", status.ToString()));
    }

    public async Task MoveDocumentsAsync(
        IReadOnlyList<string> documentIds,
        string? folderId,
        CancellationToken cancellationToken = default)
    {
        await ExecuteForDocumentsAsync(
            "UPDATE documents SET folder_id = $value, manual_classification = 1 WHERE id = $id;",
            documentIds, cancellationToken, ("$value", (object?)folderId ?? DBNull.Value));
    }

    public async Task SetDocumentsTrashedAsync(
        IReadOnlyList<string> documentIds,
        bool trashed,
        CancellationToken cancellationToken = default)
    {
        await ExecuteForDocumentsAsync(
            "UPDATE documents SET is_trashed = $value, trashed_at = $at WHERE id = $id;",
            documentIds, cancellationToken,
            ("$value", trashed ? 1 : 0),
            ("$at", trashed ? Utc(DateTime.UtcNow) : DBNull.Value));
    }

    public async Task UpdateDocumentAsync(
        string documentId, string title, string authors, int? publicationYear, string summary,
        IReadOnlyList<string> tags, LibraryReadingStatus readingStatus, bool isFavorite,
        bool markManualMetadata = true, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    UPDATE documents SET title = $title, authors = $authors, publication_year = $year,
                        ai_summary = $summary, reading_status = $reading, is_favorite = $favorite,
                        manual_metadata = CASE WHEN $manual = 1 THEN 1 ELSE manual_metadata END
                    WHERE id = $id;
                    """;
                command.Parameters.AddWithValue("$title", title);
                command.Parameters.AddWithValue("$authors", authors);
                command.Parameters.AddWithValue("$year", (object?)publicationYear ?? DBNull.Value);
                command.Parameters.AddWithValue("$summary", summary);
                command.Parameters.AddWithValue("$reading", readingStatus.ToString());
                command.Parameters.AddWithValue("$favorite", isFavorite ? 1 : 0);
                command.Parameters.AddWithValue("$manual", markManualMetadata ? 1 : 0);
                command.Parameters.AddWithValue("$id", documentId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await ReplaceTagsAsync(connection, (SqliteTransaction)transaction, documentId, tags, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task SetAnalysisStatusAsync(string documentId, LibraryAnalysisStatus status, CancellationToken cancellationToken = default) =>
        await ExecuteWriteAsync("UPDATE documents SET analysis_status = $status WHERE id = $id;", cancellationToken,
            ("$status", status.ToString()), ("$id", documentId));

    public async Task ResetInterruptedAnalysesAsync(CancellationToken cancellationToken = default) =>
        await ExecuteWriteAsync("UPDATE documents SET analysis_status = 'Pending' WHERE analysis_status = 'Analyzing';",
            cancellationToken);

    public async Task NormalizeLegacyUnclassifiedFolderAsync(CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                UPDATE documents SET folder_id = NULL
                WHERE folder_id IN (SELECT id FROM folders WHERE parent_id IS NULL AND name = '未分类' AND created_by = 'Migration');
                DELETE FROM folders WHERE parent_id IS NULL AND name = '未分类' AND created_by = 'Migration';
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task ApplyAnalysisAsync(
        string documentId, LibraryClassificationAnalysis analysis, string? folderId,
        LibraryAnalysisStatus status, bool overwriteManualFields,
        CancellationToken cancellationToken = default)
    {
        var current = await FindByIdAsync(documentId, cancellationToken);
        var replaceTags = overwriteManualFields || current?.ManualMetadata != true;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    UPDATE documents SET
                        title = CASE WHEN manual_metadata = 0 OR $overwrite = 1 THEN COALESCE(NULLIF($title, ''), title) ELSE title END,
                        authors = CASE WHEN manual_metadata = 0 OR $overwrite = 1 THEN $authors ELSE authors END,
                        publication_year = CASE WHEN manual_metadata = 0 OR $overwrite = 1 THEN $year ELSE publication_year END,
                        ai_summary = CASE WHEN manual_metadata = 0 OR $overwrite = 1 THEN $summary ELSE ai_summary END,
                        domain = CASE WHEN manual_metadata = 0 OR $overwrite = 1 THEN $domain ELSE domain END,
                        folder_id = CASE WHEN manual_classification = 0 OR $overwrite = 1 THEN $folder ELSE folder_id END,
                        analysis_status = $status
                    WHERE id = $id;
                    """;
                command.Parameters.AddWithValue("$overwrite", overwriteManualFields ? 1 : 0);
                command.Parameters.AddWithValue("$title", analysis.Title);
                command.Parameters.AddWithValue("$authors", analysis.Authors);
                command.Parameters.AddWithValue("$year", (object?)analysis.PublicationYear ?? DBNull.Value);
                command.Parameters.AddWithValue("$summary", analysis.Summary);
                command.Parameters.AddWithValue("$domain", analysis.Domain);
                command.Parameters.AddWithValue("$folder", (object?)folderId ?? DBNull.Value);
                command.Parameters.AddWithValue("$status", status.ToString());
                command.Parameters.AddWithValue("$id", documentId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            if (replaceTags)
                await ReplaceTagsAsync(connection, (SqliteTransaction)transaction, documentId, analysis.Tags, cancellationToken);
            await UpsertProposalAsync(connection, (SqliteTransaction)transaction, documentId, analysis,
                status == LibraryAnalysisStatus.NeedsReview ? "Pending" : "Applied", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task AcceptProposalAsync(string documentId, string? folderId, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                UPDATE documents SET folder_id = $folder, analysis_status = 'Ready', manual_classification = 1 WHERE id = $id;
                UPDATE classification_proposals SET status = 'Accepted' WHERE document_id = $id;
                """;
            command.Parameters.AddWithValue("$folder", (object?)folderId ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", documentId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<ClassificationProposal?> GetProposalAsync(string documentId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT suggested_path, confidence, reason, needs_new_folder, status, model_version, created_at FROM classification_proposals WHERE document_id = $id;";
        command.Parameters.AddWithValue("$id", documentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ClassificationProposal(documentId,
            JsonSerializer.Deserialize<string[]>(reader.GetString(0)) ?? [], reader.GetDouble(1), reader.GetString(2),
            reader.GetInt32(3) != 0, reader.GetString(4), reader.GetString(5), ParseUtc(reader.GetString(6)));
    }

    public async Task<IReadOnlyList<LibraryFolder>> GetFoldersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.id, f.parent_id, f.name, f.depth, f.sort_order, f.created_by, f.created_at,
                   COUNT(d.id)
            FROM folders f LEFT JOIN documents d ON d.folder_id = f.id AND d.is_trashed = 0
            GROUP BY f.id ORDER BY f.depth, f.sort_order, f.name COLLATE NOCASE;
            """;
        var raw = new List<LibraryFolder>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            raw.Add(new LibraryFolder(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetString(5),
                ParseUtc(reader.GetString(6)), DocumentCount: reader.GetInt32(7)));
        }
        var byId = raw.ToDictionary(folder => folder.Id);
        return raw.Select(folder => folder with { Path = BuildFolderPath(folder, byId) }).ToList();
    }

    public async Task<LibraryFolder> CreateFolderAsync(string name, string? parentId, string createdBy = "User", CancellationToken cancellationToken = default)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("目录名称不能为空。", nameof(name));
        if (name.Contains('/') || name.Contains('\\')) throw new ArgumentException("目录名称不能包含路径分隔符。", nameof(name));
        var folders = await GetFoldersAsync(cancellationToken);
        var parent = parentId is null ? null : folders.SingleOrDefault(folder => folder.Id == parentId)
            ?? throw new InvalidOperationException("父目录不存在。");
        var depth = (parent?.Depth ?? 0) + 1;
        if (depth > 3) throw new InvalidOperationException("文献目录最多三级。");
        var existing = folders.FirstOrDefault(folder => folder.ParentId == parentId &&
            string.Equals(folder.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;
        var id = Guid.NewGuid().ToString("N");
        await ExecuteWriteAsync("""
            INSERT INTO folders(id, parent_id, name, depth, sort_order, created_by, created_at)
            VALUES($id, $parent, $name, $depth, 0, $by, $now);
            """, cancellationToken, ("$id", id), ("$parent", (object?)parentId ?? DBNull.Value), ("$name", name),
            ("$depth", depth), ("$by", createdBy), ("$now", Utc(DateTime.UtcNow)));
        return (await GetFoldersAsync(cancellationToken)).Single(folder => folder.Id == id);
    }

    public async Task MoveFolderAsync(string folderId, string? newParentId, CancellationToken cancellationToken = default)
    {
        var folders = await GetFoldersAsync(cancellationToken);
        var folder = folders.SingleOrDefault(item => item.Id == folderId)
            ?? throw new InvalidOperationException("目录不存在。");
        if (folderId == newParentId) throw new InvalidOperationException("目录不能移动到自身。");
        var descendants = GetDescendantFolderIds(folderId, folders);
        if (newParentId is not null && descendants.Contains(newParentId))
            throw new InvalidOperationException("目录不能移动到自己的子目录。");
        var parent = newParentId is null ? null : folders.SingleOrDefault(item => item.Id == newParentId)
            ?? throw new InvalidOperationException("目标父目录不存在。");
        var newDepth = (parent?.Depth ?? 0) + 1;
        var subtreeHeight = descendants.Select(id => folders.Single(item => item.Id == id).Depth - folder.Depth).DefaultIfEmpty(0).Max();
        if (newDepth + subtreeHeight > 3) throw new InvalidOperationException("移动后会超过三级目录限制。");

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var update = connection.CreateCommand())
            {
                update.Transaction = (SqliteTransaction)transaction;
                update.CommandText = "UPDATE folders SET parent_id = $parent, depth = $depth WHERE id = $id;";
                update.Parameters.AddWithValue("$parent", (object?)newParentId ?? DBNull.Value);
                update.Parameters.AddWithValue("$depth", newDepth);
                update.Parameters.AddWithValue("$id", folderId);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            foreach (var descendantId in descendants.OrderBy(id => folders.Single(item => item.Id == id).Depth))
            {
                var descendant = folders.Single(item => item.Id == descendantId);
                await using var update = connection.CreateCommand();
                update.Transaction = (SqliteTransaction)transaction;
                update.CommandText = "UPDATE folders SET depth = $depth WHERE id = $id;";
                update.Parameters.AddWithValue("$depth", newDepth + descendant.Depth - folder.Depth);
                update.Parameters.AddWithValue("$id", descendantId);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task MergeFolderAsync(string sourceFolderId, string targetFolderId, CancellationToken cancellationToken = default)
    {
        if (sourceFolderId == targetFolderId) return;
        var folders = await GetFoldersAsync(cancellationToken);
        var source = folders.SingleOrDefault(folder => folder.Id == sourceFolderId)
            ?? throw new InvalidOperationException("源目录不存在。");
        var target = folders.SingleOrDefault(folder => folder.Id == targetFolderId)
            ?? throw new InvalidOperationException("目标目录不存在。");
        if (GetDescendantFolderIds(sourceFolderId, folders).Contains(targetFolderId))
            throw new InvalidOperationException("不能合并到源目录的子目录。");

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "UPDATE documents SET folder_id = $target WHERE folder_id = $source;";
            command.Parameters.AddWithValue("$target", targetFolderId);
            command.Parameters.AddWithValue("$source", sourceFolderId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }

        foreach (var child in folders.Where(folder => folder.ParentId == sourceFolderId))
        {
            var sameName = (await GetFoldersAsync(cancellationToken)).FirstOrDefault(folder =>
                folder.ParentId == targetFolderId && string.Equals(folder.Name, child.Name, StringComparison.OrdinalIgnoreCase));
            if (sameName is not null) await MergeFolderAsync(child.Id, sameName.Id, cancellationToken);
            else await MoveFolderAsync(child.Id, targetFolderId, cancellationToken);
        }
        await ExecuteWriteAsync("DELETE FROM folders WHERE id = $id;", cancellationToken, ("$id", sourceFolderId));
    }

    public async Task<LibraryFolder?> FindFolderByPathAsync(IReadOnlyList<string> path, CancellationToken cancellationToken = default)
    {
        if (path.Count is 0 or > 3) return null;
        var folders = await GetFoldersAsync(cancellationToken);
        string? parentId = null;
        LibraryFolder? current = null;
        foreach (var segment in path)
        {
            current = folders.FirstOrDefault(folder => folder.ParentId == parentId &&
                string.Equals(folder.Name, segment.Trim(), StringComparison.OrdinalIgnoreCase));
            if (current is null) return null;
            parentId = current.Id;
        }
        return current;
    }

    public async Task<LibraryFolder> EnsureFolderPathAsync(IReadOnlyList<string> path, string createdBy, CancellationToken cancellationToken = default)
    {
        if (path.Count is 0 or > 3) throw new InvalidOperationException("文献目录必须为一至三级。");
        string? parentId = null;
        LibraryFolder? current = null;
        foreach (var segment in path)
        {
            current = await CreateFolderAsync(segment, parentId, createdBy, cancellationToken);
            parentId = current.Id;
        }
        return current!;
    }

    public async Task RenameFolderAsync(string folderId, string name, CancellationToken cancellationToken = default)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\'))
            throw new ArgumentException("请输入有效的目录名称。", nameof(name));
        await ExecuteWriteAsync("UPDATE folders SET name = $name WHERE id = $id;", cancellationToken,
            ("$name", name), ("$id", folderId));
    }

    public async Task DeleteFolderAsync(string folderId, CancellationToken cancellationToken = default)
    {
        var folders = await GetFoldersAsync(cancellationToken);
        var descendants = GetDescendantFolderIds(folderId, folders).Append(folderId).ToArray();
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            foreach (var id in descendants.OrderByDescending(id => folders.FirstOrDefault(f => f.Id == id)?.Depth ?? 0))
            {
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = "UPDATE documents SET folder_id = NULL WHERE folder_id = $id; DELETE FROM folders WHERE id = $id;";
                command.Parameters.AddWithValue("$id", id);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task MoveDocumentAsync(string documentId, string? folderId, bool manual = true, CancellationToken cancellationToken = default) =>
        await ExecuteWriteAsync("UPDATE documents SET folder_id = $folder, manual_classification = $manual WHERE id = $id;",
            cancellationToken, ("$folder", (object?)folderId ?? DBNull.Value), ("$manual", manual ? 1 : 0), ("$id", documentId));

    public async Task SetTrashedAsync(string documentId, bool trashed, CancellationToken cancellationToken = default) =>
        await ExecuteWriteAsync("UPDATE documents SET is_trashed = $trashed, trashed_at = $at WHERE id = $id;", cancellationToken,
            ("$trashed", trashed ? 1 : 0), ("$at", trashed ? Utc(DateTime.UtcNow) : DBNull.Value), ("$id", documentId));

    public async Task DeletePermanentlyAsync(string documentId, CancellationToken cancellationToken = default)
    {
        var document = await FindByIdAsync(documentId, cancellationToken);
        if (document is null) return;
        await ExecuteWriteAsync("DELETE FROM documents WHERE id = $id;", cancellationToken, ("$id", documentId));
        try { if (File.Exists(document.ManagedPath)) File.Delete(document.ManagedPath); } catch (IOException) { }
    }

    public async Task<int> PurgeExpiredTrashAsync(TimeSpan retention, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow.Subtract(retention);
        var expired = (await GetAllDocumentsAsync(cancellationToken))
            .Where(document => document.IsTrashed && document.TrashedAt <= cutoff)
            .ToList();
        foreach (var document in expired)
            await DeletePermanentlyAsync(document.Id, cancellationToken);
        return expired.Count;
    }

    public async Task ClearHistoryAsync(string? documentId = null, CancellationToken cancellationToken = default)
    {
        var where = documentId is null ? "1 = 1" : "id = $id";
        await ExecuteWriteAsync($"UPDATE documents SET first_opened_at = NULL, last_opened_at = NULL, open_count = 0, last_page_index = 0, progress = 0 WHERE {where};",
            cancellationToken, ("$id", (object?)documentId ?? DBNull.Value));
    }

    public async Task ImportLegacyHistoryAsync(
        string documentId,
        DateTime openedAt,
        CancellationToken cancellationToken = default)
    {
        await ExecuteWriteAsync("""
            UPDATE documents SET
                first_opened_at = COALESCE(first_opened_at, $opened),
                last_opened_at = CASE WHEN last_opened_at IS NULL OR last_opened_at < $opened THEN $opened ELSE last_opened_at END,
                open_count = CASE WHEN open_count < 1 THEN 1 ELSE open_count END
            WHERE id = $id;
            """, cancellationToken, ("$opened", Utc(openedAt)), ("$id", documentId));
    }

    public async Task<bool> GetMetadataFlagAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return string.Equals((string?)await command.ExecuteScalarAsync(cancellationToken), "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task SetMetadataFlagAsync(string key, bool value, CancellationToken cancellationToken = default) =>
        await ExecuteWriteAsync("INSERT INTO metadata(key, value) VALUES($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
            cancellationToken, ("$key", key), ("$value", value ? "true" : "false"));

    public async Task AddLegacyIssueAsync(string filePath, string title, string reason, CancellationToken cancellationToken = default) =>
        await ExecuteWriteAsync("""
            INSERT INTO legacy_issues(file_path, title, reason, imported_at) VALUES($path, $title, $reason, $at)
            ON CONFLICT(file_path) DO UPDATE SET title = excluded.title, reason = excluded.reason;
            """, cancellationToken, ("$path", filePath), ("$title", title), ("$reason", reason), ("$at", Utc(DateTime.UtcNow)));

    public async Task<IReadOnlyList<LegacyLibraryIssue>> GetLegacyIssuesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT file_path, title, reason, imported_at FROM legacy_issues ORDER BY imported_at DESC;";
        var issues = new List<LegacyLibraryIssue>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            issues.Add(new LegacyLibraryIssue(reader.GetString(0), reader.GetString(1), reader.GetString(2), ParseUtc(reader.GetString(3))));
        return issues;
    }

    public static IReadOnlySet<string> GetDescendantFolderIds(string folderId, IReadOnlyList<LibraryFolder> folders)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(folderId);
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            foreach (var child in folders.Where(folder => folder.ParentId == parent))
            {
                if (result.Add(child.Id)) queue.Enqueue(child.Id);
            }
        }
        return result;
    }

    private async Task<IReadOnlyList<LibraryDocument>> LoadDocumentsAsync(
        string where, IReadOnlyList<(string Name, object Value)> parameters, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var documents = new List<LibraryDocument>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT d.id, d.content_hash, d.managed_path, d.title, d.authors, d.publication_year,
                       d.page_count, d.ai_summary, d.folder_id, d.reading_status, d.is_favorite,
                       d.analysis_status, d.added_at, d.first_opened_at, d.last_opened_at, d.open_count,
                       d.last_page_index, d.progress, d.file_size, d.is_trashed, d.trashed_at,
                       d.manual_metadata, d.manual_classification, d.domain,
                       COALESCE((WITH RECURSIVE path(id, parent_id, name, full_path) AS (
                           SELECT id, parent_id, name, name FROM folders WHERE parent_id IS NULL
                           UNION ALL SELECT f.id, f.parent_id, f.name, path.full_path || ' / ' || f.name
                           FROM folders f JOIN path ON f.parent_id = path.id
                       ) SELECT full_path FROM path WHERE id = d.folder_id), '') AS folder_path
                FROM documents d WHERE {where};
                """;
            foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                documents.Add(new LibraryDocument(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetInt32(5), checked((uint)reader.GetInt64(6)), reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetString(24), [],
                    ParseEnum(reader.GetString(9), LibraryReadingStatus.ToRead), reader.GetInt32(10) != 0,
                    ParseEnum(reader.GetString(11), LibraryAnalysisStatus.Pending), ParseUtc(reader.GetString(12)),
                    reader.IsDBNull(13) ? null : ParseUtc(reader.GetString(13)),
                    reader.IsDBNull(14) ? null : ParseUtc(reader.GetString(14)), reader.GetInt32(15),
                    checked((uint)reader.GetInt64(16)), reader.GetDouble(17), reader.GetInt64(18), reader.GetInt32(19) != 0,
                    reader.IsDBNull(20) ? null : ParseUtc(reader.GetString(20)), reader.GetInt32(21) != 0,
                    reader.GetInt32(22) != 0, [], reader.IsDBNull(23) ? string.Empty : reader.GetString(23)));
            }
        }
        if (documents.Count == 0) return documents;
        var byId = documents.ToDictionary(document => document.Id);
        var tags = documents.ToDictionary(document => document.Id, _ => new List<string>());
        var sources = documents.ToDictionary(document => document.Id, _ => new List<string>());
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT dt.document_id, t.name FROM document_tags dt JOIN tags t ON t.id = dt.tag_id ORDER BY t.name COLLATE NOCASE;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (tags.TryGetValue(reader.GetString(0), out var list)) list.Add(reader.GetString(1));
        }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT document_id, file_path FROM sources ORDER BY last_seen_at DESC;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (sources.TryGetValue(reader.GetString(0), out var list)) list.Add(reader.GetString(1));
        }
        return documents.Select(document => document with { Tags = tags[document.Id], SourcePaths = sources[document.Id] }).ToList();
    }

    private async Task ExecuteWriteAsync(string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ExecuteForDocumentsAsync(
        string sql,
        IReadOnlyList<string> documentIds,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        if (documentIds.Count == 0) return;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            foreach (var documentId in documentIds.Distinct(StringComparer.Ordinal))
            {
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = sql;
                command.Parameters.AddWithValue("$id", documentId);
                foreach (var parameter in parameters)
                    command.Parameters.AddWithValue(parameter.Name, parameter.Value);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };
        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static async Task UpsertSourceAsync(SqliteConnection connection, SqliteTransaction? transaction,
        string documentId, string sourcePath, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sources(document_id, file_path, last_seen_at, is_available) VALUES($id, $path, $now, 1)
            ON CONFLICT(document_id, file_path) DO UPDATE SET last_seen_at = excluded.last_seen_at, is_available = 1;
            """;
        command.Parameters.AddWithValue("$id", documentId);
        command.Parameters.AddWithValue("$path", Path.GetFullPath(sourcePath));
        command.Parameters.AddWithValue("$now", Utc(DateTime.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ReplaceTagsAsync(SqliteConnection connection, SqliteTransaction transaction,
        string documentId, IReadOnlyList<string> rawTags, CancellationToken cancellationToken)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM document_tags WHERE document_id = $id;";
            delete.Parameters.AddWithValue("$id", documentId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var tag in rawTags.Select(tag => tag.Trim()).Where(tag => tag.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(20))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO tags(name) VALUES($tag) ON CONFLICT(name) DO NOTHING;
                INSERT INTO document_tags(document_id, tag_id) SELECT $id, id FROM tags WHERE name = $tag COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue("$tag", tag);
            command.Parameters.AddWithValue("$id", documentId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task UpsertProposalAsync(SqliteConnection connection, SqliteTransaction transaction,
        string documentId, LibraryClassificationAnalysis analysis, string status, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO classification_proposals(document_id, suggested_path, confidence, reason, needs_new_folder, status, model_version, created_at)
            VALUES($id, $path, $confidence, $reason, $new, $status, $model, $at)
            ON CONFLICT(document_id) DO UPDATE SET suggested_path = excluded.suggested_path,
                confidence = excluded.confidence, reason = excluded.reason, needs_new_folder = excluded.needs_new_folder,
                status = excluded.status, model_version = excluded.model_version, created_at = excluded.created_at;
            """;
        command.Parameters.AddWithValue("$id", documentId);
        command.Parameters.AddWithValue("$path", JsonSerializer.Serialize(analysis.SuggestedPath.Take(3)));
        command.Parameters.AddWithValue("$confidence", Math.Clamp(analysis.Confidence, 0, 1));
        command.Parameters.AddWithValue("$reason", analysis.Reason);
        command.Parameters.AddWithValue("$new", analysis.NeedsNewFolder ? 1 : 0);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$model", analysis.ModelVersion);
        command.Parameters.AddWithValue("$at", Utc(DateTime.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string BuildFolderPath(LibraryFolder folder, IReadOnlyDictionary<string, LibraryFolder> byId)
    {
        var segments = new Stack<string>();
        var current = folder;
        while (true)
        {
            segments.Push(current.Name);
            if (current.ParentId is null || !byId.TryGetValue(current.ParentId, out current!)) break;
        }
        return string.Join(" / ", segments);
    }

    private static T ParseEnum<T>(string value, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(value, true, out var result) ? result : fallback;
    private static string Utc(DateTime value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTime ParseUtc(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}
