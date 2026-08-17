using System.Globalization;

namespace TransReader.Core.Library;

public sealed class LibraryQueryService
{
    private readonly LibraryRepository _repository;

    public LibraryQueryService(LibraryRepository repository) => _repository = repository;

    public async Task<IReadOnlyList<LibraryDocument>> SearchAsync(LibraryQuery query, CancellationToken cancellationToken = default)
    {
        // 导航/筛选条件尽量下推到 SQL，避免全表加载后再内存过滤；
        // 文件存在性（File.Exists）与全文搜索仍需在内存中进行。
        var conditions = new List<string>();
        var parameters = new List<(string Name, object Value)>();
        var needsFileCheck = false;

        void PushStatus(string column, params string[] values)
        {
            var names = values.Select((_, index) => $"$v{conditions.Count + index}").ToArray();
            conditions.Add($"{column} IN ({string.Join(", ", names)})");
            parameters.AddRange(values.Zip(names, (value, name) => (name, (object)value)));
        }

        void PushCutoff(string comparison, DateTime cutoffUtc)
        {
            var name = $"$c{conditions.Count}";
            // last_opened_at 以 ISO-8601 "O" 格式 UTC 字符串存储，字典序比较与 DateTime 比较等价。
            conditions.Add($"d.last_opened_at {comparison} {name}");
            parameters.Add((name, cutoffUtc.ToString("O", CultureInfo.InvariantCulture)));
        }

        var wantsTrash = query.Navigation == LibraryNavigationKind.Trash;
        conditions.Add("d.is_trashed = $trash");
        parameters.Add(("$trash", wantsTrash ? 1 : 0));

        if (query.Navigation == LibraryNavigationKind.Folder && query.FolderId is not null)
        {
            var folders = await _repository.GetFoldersAsync(cancellationToken);
            var folderIds = new HashSet<string>(StringComparer.Ordinal) { query.FolderId };
            if (query.IncludeDescendantFolders)
                folderIds.UnionWith(LibraryRepository.GetDescendantFolderIds(query.FolderId, folders));
            var placeholders = folderIds.Select((_, index) => $"$f{index}").ToArray();
            conditions.Add($"d.folder_id IN ({string.Join(", ", placeholders)})");
            parameters.AddRange(folderIds.Select((id, index) => ($"$f{index}", (object)id)));
        }

        var now = DateTime.UtcNow;
        switch (query.Navigation)
        {
            case LibraryNavigationKind.Favorite: conditions.Add("d.is_favorite = 1"); break;
            case LibraryNavigationKind.ToRead: PushStatus("d.reading_status", LibraryReadingStatus.ToRead.ToString()); break;
            case LibraryNavigationKind.Reading: PushStatus("d.reading_status", LibraryReadingStatus.Reading.ToString()); break;
            case LibraryNavigationKind.Read: PushStatus("d.reading_status", LibraryReadingStatus.Read.ToString()); break;
            case LibraryNavigationKind.NeedsReview: PushStatus("d.analysis_status", LibraryAnalysisStatus.NeedsReview.ToString()); break;
            case LibraryNavigationKind.Unclassified: conditions.Add("d.folder_id IS NULL"); break;
            case LibraryNavigationKind.FileIssue: needsFileCheck = true; break;
            case LibraryNavigationKind.HistoryToday: PushCutoff(">=", DateTime.Now.Date.ToUniversalTime()); break;
            case LibraryNavigationKind.HistoryWeek: PushCutoff(">=", now.AddDays(-7)); break;
            case LibraryNavigationKind.HistoryMonth: PushCutoff(">=", now.AddDays(-30)); break;
            case LibraryNavigationKind.HistoryOlder: PushCutoff("<", now.AddDays(-30)); break;
            case LibraryNavigationKind.HistoryNever: conditions.Add("d.last_opened_at IS NULL"); break;
        }

        switch (query.Filter)
        {
            case LibraryFilterKind.Favorite: conditions.Add("d.is_favorite = 1"); break;
            case LibraryFilterKind.ToRead: PushStatus("d.reading_status", LibraryReadingStatus.ToRead.ToString()); break;
            case LibraryFilterKind.Reading: PushStatus("d.reading_status", LibraryReadingStatus.Reading.ToString()); break;
            case LibraryFilterKind.Read: PushStatus("d.reading_status", LibraryReadingStatus.Read.ToString()); break;
            case LibraryFilterKind.NeedsReview: PushStatus("d.analysis_status", LibraryAnalysisStatus.NeedsReview.ToString()); break;
            case LibraryFilterKind.Unclassified: conditions.Add("d.folder_id IS NULL"); break;
            case LibraryFilterKind.FileIssue: needsFileCheck = true; break;
            case LibraryFilterKind.PendingAnalysis: PushStatus("d.analysis_status",
                LibraryAnalysisStatus.Pending.ToString(), LibraryAnalysisStatus.Failed.ToString()); break;
        }

        IEnumerable<LibraryDocument> source = await _repository.QueryDocumentsAsync(
            string.Join(" AND ", conditions), parameters, cancellationToken);
        if (needsFileCheck) source = source.Where(document => !document.ManagedFileExists);

        var search = query.SearchText.Trim();
        if (search.Length > 0)
        {
            source = source.Where(document =>
                document.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                document.Authors.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (document.PublicationYear?.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                document.FolderPath.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                document.AiSummary.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                document.Tags.Any(tag => tag.Contains(search, StringComparison.OrdinalIgnoreCase)));
        }

        return query.Sort switch
        {
            LibrarySortOrder.Added => source.OrderByDescending(document => document.AddedAt).ToList(),
            LibrarySortOrder.Title => source.OrderBy(document => document.Title, StringComparer.OrdinalIgnoreCase).ToList(),
            LibrarySortOrder.Progress => source.OrderByDescending(document => document.Progress).ThenBy(document => document.Title).ToList(),
            _ => source.OrderByDescending(document => document.LastOpenedAt ?? DateTime.MinValue)
                .ThenByDescending(document => document.AddedAt).ToList()
        };
    }
}
