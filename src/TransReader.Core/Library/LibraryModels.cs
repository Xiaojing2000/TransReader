namespace TransReader.Core.Library;

public enum LibraryReadingStatus
{
    ToRead,
    Reading,
    Read
}

public enum LibraryAnalysisStatus
{
    Pending,
    Analyzing,
    Ready,
    NeedsReview,
    Failed
}

public enum LibraryNavigationKind
{
    All,
    Favorite,
    ToRead,
    Reading,
    Read,
    NeedsReview,
    Unclassified,
    FileIssue,
    Trash,
    Folder,
    HistoryToday,
    HistoryWeek,
    HistoryMonth,
    HistoryOlder,
    HistoryNever
}

public enum LibrarySortOrder
{
    LastOpened,
    Added,
    Title,
    Progress
}

public enum LibraryFilterKind
{
    All,
    Favorite,
    ToRead,
    Reading,
    Read,
    NeedsReview,
    Unclassified,
    FileIssue,
    PendingAnalysis
}

public sealed record LibraryDocument(
    string Id,
    string ContentHash,
    string ManagedPath,
    string Title,
    string Authors,
    int? PublicationYear,
    uint PageCount,
    string AiSummary,
    string? FolderId,
    string FolderPath,
    IReadOnlyList<string> Tags,
    LibraryReadingStatus ReadingStatus,
    bool IsFavorite,
    LibraryAnalysisStatus AnalysisStatus,
    DateTime AddedAt,
    DateTime? FirstOpenedAt,
    DateTime? LastOpenedAt,
    int OpenCount,
    uint LastPageIndex,
    double Progress,
    long FileSize,
    bool IsTrashed,
    DateTime? TrashedAt,
    bool ManualMetadata,
    bool ManualClassification,
    IReadOnlyList<string> SourcePaths,
    string Domain = "")
{
    public string AuthorsYearLabel => string.Join(" · ", new[]
        {
            string.IsNullOrWhiteSpace(Authors) ? null : Authors,
            PublicationYear?.ToString()
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

    public string TagsLabel => Tags.Count == 0 ? string.Empty : string.Join("  ·  ", Tags);
    public string AddedAtLabel => AddedAt.ToLocalTime().ToString("yyyy-MM-dd");
    public string LastOpenedAtLabel => LastOpenedAt is null
        ? "从未打开"
        : LastOpenedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string ProgressLabel => $"{Math.Round(Progress * 100):0}%";
    public string ReadingStatusLabel => ReadingStatus switch
    {
        LibraryReadingStatus.Reading => "阅读中",
        LibraryReadingStatus.Read => "已读",
        _ => "待读"
    };
    public string AnalysisStatusLabel => AnalysisStatus switch
    {
        LibraryAnalysisStatus.Analyzing => "AI 分析中",
        LibraryAnalysisStatus.Ready => "已归类",
        LibraryAnalysisStatus.NeedsReview => "待确认",
        LibraryAnalysisStatus.Failed => "待分析",
        _ => "等待分析"
    };
    public string FileSizeLabel => FileSize < 1024 * 1024
        ? $"{FileSize / 1024d:0.#} KB"
        : $"{FileSize / 1024d / 1024d:0.#} MB";
    public string SourcePathsLabel => SourcePaths.Count == 0 ? "—" : string.Join("\n", SourcePaths);
    public bool ManagedFileExists => File.Exists(ManagedPath);
    public string ThumbnailPath
    {
        get
        {
            var objectDirectory = Path.GetDirectoryName(ManagedPath);
            var objectsDirectory = objectDirectory is null ? null : Path.GetDirectoryName(objectDirectory);
            var libraryRoot = objectsDirectory is null ? null : Path.GetDirectoryName(objectsDirectory);
            return libraryRoot is null ? string.Empty : Path.Combine(libraryRoot, "thumbnails", $"{ContentHash}.jpg");
        }
    }
    public string? ThumbnailUri => File.Exists(ThumbnailPath) ? new Uri(ThumbnailPath).AbsoluteUri : null;
}

public sealed record LibraryFolder(
    string Id,
    string? ParentId,
    string Name,
    int Depth,
    int SortOrder,
    string CreatedBy,
    DateTime CreatedAt,
    string Path = "",
    int DocumentCount = 0);

public sealed record ClassificationProposal(
    string DocumentId,
    IReadOnlyList<string> SuggestedPath,
    double Confidence,
    string Reason,
    bool NeedsNewFolder,
    string Status,
    string ModelVersion,
    DateTime CreatedAt);

public sealed record LibraryClassificationAnalysis(
    IReadOnlyList<string> SuggestedPath,
    bool NeedsNewFolder,
    double Confidence,
    string Reason,
    string Title,
    string Authors,
    int? PublicationYear,
    string Summary,
    IReadOnlyList<string> Tags,
    string ModelVersion,
    string Domain);

public sealed record LibraryImportResult(LibraryDocument Document, bool WasCreated, bool WasDuplicate);

public sealed record LibraryQuery(
    string SearchText = "",
    LibraryNavigationKind Navigation = LibraryNavigationKind.All,
    string? FolderId = null,
    LibrarySortOrder Sort = LibrarySortOrder.LastOpened,
    bool IncludeDescendantFolders = true,
    LibraryFilterKind Filter = LibraryFilterKind.All);

public sealed record LegacyLibraryIssue(string FilePath, string Title, string Reason, DateTime ImportedAt);
