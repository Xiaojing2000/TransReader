using TransReader.Core.Library;

namespace TransReader.Core.Tests;

public sealed class LibraryRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"transreader-library-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task EnsureImportedAsync_DeduplicatesIdenticalContentAndKeepsSources()
    {
        var repository = await CreateRepositoryAsync();
        var service = new LibraryIngestionService(Path.Combine(_root, "library"), repository);
        var first = CreatePdfLikeFile("first.pdf", "same content");
        var second = CreatePdfLikeFile("second.pdf", "same content");

        var imported = await service.EnsureImportedAsync(first, 10);
        var duplicate = await service.EnsureImportedAsync(second, 10);

        Assert.True(imported.WasCreated);
        Assert.True(duplicate.WasDuplicate);
        Assert.Equal(imported.Document.Id, duplicate.Document.Id);
        Assert.True(File.Exists(imported.Document.ManagedPath));
        var stored = await repository.FindByIdAsync(imported.Document.Id);
        Assert.NotNull(stored);
        Assert.Equal(2, stored.SourcePaths.Count);
    }

    [Fact]
    public async Task FolderHierarchy_RejectsFourthLevel()
    {
        var repository = await CreateRepositoryAsync();
        var first = await repository.CreateFolderAsync("计算机", null);
        var second = await repository.CreateFolderAsync("人工智能", first.Id);
        var third = await repository.CreateFolderAsync("机器学习", second.Id);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.CreateFolderAsync("深度学习", third.Id));

        Assert.Contains("最多三级", error.Message);
    }

    [Fact]
    public async Task Query_FolderIncludesDescendants_AndHistoryIsIndependent()
    {
        var repository = await CreateRepositoryAsync();
        var service = new LibraryIngestionService(Path.Combine(_root, "library"), repository);
        var parent = await repository.CreateFolderAsync("经济", null);
        var child = await repository.CreateFolderAsync("金融", parent.Id);
        var imported = await service.EnsureImportedAsync(CreatePdfLikeFile("paper.pdf", "financial paper"), 20);
        await repository.MoveDocumentAsync(imported.Document.Id, child.Id);
        await repository.RecordOpenedAsync(imported.Document.Id, 20);
        await repository.UpdateReadingProgressAsync(imported.Document.Id, 9, 20);
        var query = new LibraryQueryService(repository);

        var inParent = await query.SearchAsync(new LibraryQuery(Navigation: LibraryNavigationKind.Folder, FolderId: parent.Id));
        var today = await query.SearchAsync(new LibraryQuery(Navigation: LibraryNavigationKind.HistoryToday));

        Assert.Single(inParent);
        Assert.Single(today);
        Assert.Equal(.5, today[0].Progress, 3);
        Assert.Equal(LibraryReadingStatus.Reading, today[0].ReadingStatus);
    }

    [Fact]
    public async Task TrashAndHistoryOperations_DoNotDeleteManagedFile()
    {
        var repository = await CreateRepositoryAsync();
        var service = new LibraryIngestionService(Path.Combine(_root, "library"), repository);
        var imported = await service.EnsureImportedAsync(CreatePdfLikeFile("paper.pdf", "trash test"), 2);
        await repository.RecordOpenedAsync(imported.Document.Id, 2);

        await repository.ClearHistoryAsync(imported.Document.Id);
        await repository.SetTrashedAsync(imported.Document.Id, true);

        var stored = await repository.FindByIdAsync(imported.Document.Id);
        Assert.NotNull(stored);
        Assert.True(stored.IsTrashed);
        Assert.Equal(0, stored.OpenCount);
        Assert.True(File.Exists(stored.ManagedPath));
    }

    [Fact]
    public async Task PermanentDelete_RemovesRecordAndManagedCopyButKeepsSource()
    {
        var repository = await CreateRepositoryAsync();
        var service = new LibraryIngestionService(Path.Combine(_root, "library"), repository);
        var source = CreatePdfLikeFile("source.pdf", "permanent delete");
        var imported = await service.EnsureImportedAsync(source, 3);
        var managed = imported.Document.ManagedPath;
        await repository.SetTrashedAsync(imported.Document.Id, true);

        await repository.DeletePermanentlyAsync(imported.Document.Id);

        Assert.Null(await repository.FindByIdAsync(imported.Document.Id));
        Assert.False(File.Exists(managed));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task MoveAndMergeFolders_PreserveDocumentsAndDepthLimit()
    {
        var repository = await CreateRepositoryAsync();
        var service = new LibraryIngestionService(Path.Combine(_root, "library"), repository);
        var source = await repository.CreateFolderAsync("旧目录", null);
        var child = await repository.CreateFolderAsync("子目录", source.Id);
        var target = await repository.CreateFolderAsync("新目录", null);
        var imported = await service.EnsureImportedAsync(CreatePdfLikeFile("move.pdf", "move folder"), 5);
        await repository.MoveDocumentAsync(imported.Document.Id, source.Id);

        await repository.MoveFolderAsync(child.Id, target.Id);
        await repository.MergeFolderAsync(source.Id, target.Id);

        var stored = await repository.FindByIdAsync(imported.Document.Id);
        var folders = await repository.GetFoldersAsync();
        Assert.Equal(target.Id, stored!.FolderId);
        Assert.DoesNotContain(folders, folder => folder.Id == source.Id);
        Assert.Equal(2, folders.Single(folder => folder.Id == child.Id).Depth);
    }

    [Fact]
    public async Task ResetInterruptedAnalyses_MovesAnalyzingBackToPending()
    {
        var repository = await CreateRepositoryAsync();
        var service = new LibraryIngestionService(Path.Combine(_root, "library"), repository);
        var imported = await service.EnsureImportedAsync(CreatePdfLikeFile("interrupted.pdf", "analysis"), 3);
        await repository.SetAnalysisStatusAsync(imported.Document.Id, LibraryAnalysisStatus.Analyzing);

        await repository.ResetInterruptedAnalysesAsync();

        var stored = await repository.FindByIdAsync(imported.Document.Id);
        Assert.NotNull(stored);
        Assert.Equal(LibraryAnalysisStatus.Pending, stored.AnalysisStatus);
    }

    [Fact]
    public async Task ApplyAnalysis_DoesNotOverwriteManualMetadataOrTagsByDefault()
    {
        var repository = await CreateRepositoryAsync();
        var service = new LibraryIngestionService(Path.Combine(_root, "library"), repository);
        var imported = await service.EnsureImportedAsync(CreatePdfLikeFile("manual.pdf", "manual fields"), 3);
        await repository.UpdateDocumentAsync(imported.Document.Id, "我的标题", "我的作者", 2024,
            "我的摘要", ["我的标签"], LibraryReadingStatus.Reading, true);
        var analysis = new LibraryClassificationAnalysis([], false, .95, "分析结果", "AI 标题", "AI 作者",
            2020, "AI 摘要", ["AI 标签"], "test", "math");

        await repository.ApplyAnalysisAsync(imported.Document.Id, analysis, null,
            LibraryAnalysisStatus.Ready, overwriteManualFields: false);

        var stored = await repository.FindByIdAsync(imported.Document.Id);
        Assert.NotNull(stored);
        Assert.Equal("我的标题", stored.Title);
        Assert.Equal(["我的标签"], stored.Tags);
        Assert.Equal(string.Empty, stored.Domain);
    }

    [Fact]
    public async Task ApplyAnalysis_PersistsDomainAndRespectsManualFlag()
    {
        var repository = await CreateRepositoryAsync();
        var service = new LibraryIngestionService(Path.Combine(_root, "library"), repository);
        var imported = await service.EnsureImportedAsync(CreatePdfLikeFile("domain.pdf", "domain fields"), 3);
        var analysis = new LibraryClassificationAnalysis([], false, .9, "分析结果", "标题", "作者",
            null, "摘要", [], "test", "math");

        await repository.ApplyAnalysisAsync(imported.Document.Id, analysis, null,
            LibraryAnalysisStatus.Ready, overwriteManualFields: false);
        Assert.Equal("math", (await repository.FindByIdAsync(imported.Document.Id))!.Domain);

        // manual_metadata = 1 且未强制：domain 受保护不被覆盖。
        await repository.UpdateDocumentAsync(imported.Document.Id, "我的标题", "我的作者", 2024,
            "我的摘要", ["我的标签"], LibraryReadingStatus.Reading, true);
        await repository.ApplyAnalysisAsync(imported.Document.Id, analysis with { Domain = "history" }, null,
            LibraryAnalysisStatus.Ready, overwriteManualFields: false);
        Assert.Equal("math", (await repository.FindByIdAsync(imported.Document.Id))!.Domain);

        // 强制覆盖：domain 随其他 AI 字段一并更新。
        await repository.ApplyAnalysisAsync(imported.Document.Id, analysis with { Domain = "history" }, null,
            LibraryAnalysisStatus.Ready, overwriteManualFields: true);
        Assert.Equal("history", (await repository.FindByIdAsync(imported.Document.Id))!.Domain);
    }

    [Fact]
    public async Task BatchOperations_UpdateOnlyRequestedDocuments()
    {
        var repository = await CreateRepositoryAsync();
        var service = new LibraryIngestionService(Path.Combine(_root, "library"), repository);
        var folder = await repository.CreateFolderAsync("批量目录", null);
        var first = await service.EnsureImportedAsync(CreatePdfLikeFile("batch-a.pdf", "a"), 2);
        var second = await service.EnsureImportedAsync(CreatePdfLikeFile("batch-b.pdf", "b"), 2);

        await repository.SetReadingStatusAsync([first.Document.Id], LibraryReadingStatus.Read);
        await repository.MoveDocumentsAsync([first.Document.Id], folder.Id);
        await repository.SetDocumentsTrashedAsync([first.Document.Id], true);

        var changed = await repository.FindByIdAsync(first.Document.Id);
        var untouched = await repository.FindByIdAsync(second.Document.Id);
        Assert.Equal(LibraryReadingStatus.Read, changed!.ReadingStatus);
        Assert.Equal(folder.Id, changed.FolderId);
        Assert.True(changed.IsTrashed);
        Assert.Equal(LibraryReadingStatus.ToRead, untouched!.ReadingStatus);
        Assert.False(untouched.IsTrashed);
    }

    [Fact]
    public async Task ImportLegacyHistory_PreservesTimestampAndDoesNotDuplicateOpenCount()
    {
        var repository = await CreateRepositoryAsync();
        var service = new LibraryIngestionService(Path.Combine(_root, "library"), repository);
        var imported = await service.EnsureImportedAsync(CreatePdfLikeFile("legacy.pdf", "legacy"), 4);
        var openedAt = new DateTime(2025, 5, 10, 12, 30, 0, DateTimeKind.Local);

        await repository.ImportLegacyHistoryAsync(imported.Document.Id, openedAt);
        await repository.ImportLegacyHistoryAsync(imported.Document.Id, openedAt);

        var stored = await repository.FindByIdAsync(imported.Document.Id);
        Assert.NotNull(stored);
        Assert.Equal(1, stored.OpenCount);
        Assert.Equal(openedAt.ToUniversalTime(), stored.LastOpenedAt);
    }

    [Fact]
    public async Task NormalizeLegacyUnclassifiedFolder_MovesDocumentsToVirtualUnclassified()
    {
        var repository = await CreateRepositoryAsync();
        var service = new LibraryIngestionService(Path.Combine(_root, "library"), repository);
        var folder = await repository.CreateFolderAsync("未分类", null, "Migration");
        var imported = await service.EnsureImportedAsync(CreatePdfLikeFile("normalize.pdf", "normalize"), 1);
        await repository.MoveDocumentAsync(imported.Document.Id, folder.Id, manual: false);

        await repository.NormalizeLegacyUnclassifiedFolderAsync();

        Assert.Null((await repository.FindByIdAsync(imported.Document.Id))!.FolderId);
        Assert.DoesNotContain(await repository.GetFoldersAsync(), item => item.Id == folder.Id);
    }

    [Fact]
    public async Task Query_NavigationAndFilterPushdown_MatchesInMemorySemantics()
    {
        var repository = await CreateRepositoryAsync();
        var service = new LibraryIngestionService(Path.Combine(_root, "library"), repository);
        var first = await service.EnsureImportedAsync(CreatePdfLikeFile("status-a.pdf", "a"), 2);
        var second = await service.EnsureImportedAsync(CreatePdfLikeFile("status-b.pdf", "b"), 2);
        var third = await service.EnsureImportedAsync(CreatePdfLikeFile("status-c.pdf", "c"), 2);
        await repository.SetAnalysisStatusAsync(first.Document.Id, LibraryAnalysisStatus.NeedsReview);
        await repository.SetAnalysisStatusAsync(second.Document.Id, LibraryAnalysisStatus.Failed);
        await repository.SetReadingStatusAsync([first.Document.Id], LibraryReadingStatus.Reading);
        var query = new LibraryQueryService(repository);

        var review = await query.SearchAsync(new LibraryQuery(Navigation: LibraryNavigationKind.NeedsReview));
        var pending = await query.SearchAsync(new LibraryQuery(Filter: LibraryFilterKind.PendingAnalysis));
        var reading = await query.SearchAsync(new LibraryQuery(Filter: LibraryFilterKind.Reading));
        var all = await query.SearchAsync(new LibraryQuery());

        Assert.Single(review);
        Assert.Equal(first.Document.Id, review[0].Id);
        Assert.Equal(2, pending.Count);
        Assert.Contains(pending, document => document.Id == second.Document.Id);
        Assert.Contains(pending, document => document.Id == third.Document.Id);
        Assert.Single(reading);
        Assert.Equal(first.Document.Id, reading[0].Id);
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public async Task Query_TrashNavigationSeparatesTrashedAndUntrashed()
    {
        var repository = await CreateRepositoryAsync();
        var service = new LibraryIngestionService(Path.Combine(_root, "library"), repository);
        var imported = await service.EnsureImportedAsync(CreatePdfLikeFile("trash-nav.pdf", "trash"), 2);
        await repository.SetTrashedAsync(imported.Document.Id, true);
        var query = new LibraryQueryService(repository);

        var normal = await query.SearchAsync(new LibraryQuery());
        var trash = await query.SearchAsync(new LibraryQuery(Navigation: LibraryNavigationKind.Trash));

        Assert.Empty(normal);
        Assert.Single(trash);
        Assert.Equal(imported.Document.Id, trash[0].Id);
    }

    [Fact]
    public async Task Query_SearchTextMatchesTitleAndTags()
    {
        var repository = await CreateRepositoryAsync();
        var service = new LibraryIngestionService(Path.Combine(_root, "library"), repository);
        var imported = await service.EnsureImportedAsync(CreatePdfLikeFile("search-me.pdf", "search"), 2);
        await repository.UpdateDocumentAsync(imported.Document.Id, "量子力学讲义", "张三", 2024,
            "摘要", ["物理", "教材"], LibraryReadingStatus.ToRead, true);
        var query = new LibraryQueryService(repository);

        var byTitle = await query.SearchAsync(new LibraryQuery(SearchText: "量子"));
        var byTag = await query.SearchAsync(new LibraryQuery(SearchText: "教材"));
        var byYear = await query.SearchAsync(new LibraryQuery(SearchText: "2024"));

        Assert.Single(byTitle);
        Assert.Single(byTag);
        Assert.Single(byYear);
    }

    [Fact]
    public async Task Initialize_RecordsLatestSchemaVersionForFreshDatabase()
    {
        var repository = await CreateRepositoryAsync();
        Assert.Equal(2, await repository.ReadSchemaVersionAsync());
    }

    [Fact]
    public async Task Reinitialize_IsIdempotentAndPreservesData()
    {
        var repository = await CreateRepositoryAsync();
        var folder = await repository.CreateFolderAsync("保留目录", null);
        var before = await repository.ReadSchemaVersionAsync();

        await repository.InitializeAsync(); // 二次初始化不得破坏数据或抛错
        await repository.InitializeAsync();

        Assert.Equal(before, await repository.ReadSchemaVersionAsync());
        var folders = await repository.GetFoldersAsync();
        Assert.Contains(folders, item => item.Id == folder.Id);
    }

    private async Task<LibraryRepository> CreateRepositoryAsync()
    {
        Directory.CreateDirectory(_root);
        var repository = new LibraryRepository(Path.Combine(_root, "library.db"));
        await repository.InitializeAsync();
        return repository;
    }
    private string CreatePdfLikeFile(string name, string content)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
