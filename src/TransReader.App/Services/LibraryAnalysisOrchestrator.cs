using TransReader.Core.Library;

namespace TransReader.App.Services;

/// <summary>
/// 文献 AI 分析的编排（从 MainWindow 抽出）：活跃去重 + 状态机 + 分类调用 + 结果应用。
/// UI 刷新（忙状态文案、完成刷新）与重入队经事件回调，避免反向依赖 view 与 LibraryAnalysisQueue。
/// </summary>
internal sealed class LibraryAnalysisOrchestrator
{
    private readonly LibraryRepository _repository;
    private readonly LibraryClassificationService _classification;
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);

    public event Action<string>? BusyChanged;
    public event Action<string, bool, TimeSpan>? ReenqueueRequested;
    public event Action? Completed;

    public LibraryAnalysisOrchestrator(
        LibraryRepository repository,
        LibraryClassificationService classification)
    {
        _repository = repository;
        _classification = classification;
    }

    public async Task AnalyzeAsync(string documentId, bool manual, CancellationToken cancellationToken)
    {
        lock (_active)
        {
            if (!_active.Add(documentId)) return;
        }
        try
        {
            var document = await _repository.FindByIdAsync(documentId);
            if (document is null || document.IsTrashed) return;
            if (!await _classification.IsReadyAsync())
            {
                await _repository.SetAnalysisStatusAsync(documentId, LibraryAnalysisStatus.Pending, cancellationToken);
                return;
            }
            await _repository.SetAnalysisStatusAsync(documentId, LibraryAnalysisStatus.Analyzing, cancellationToken);
            BusyChanged?.Invoke($"AI 正在分析：{document.Title}");
            var folders = await _repository.GetFoldersAsync(cancellationToken);
            var analysis = await _classification.AnalyzeAsync(document, folders,
                manual ? LocalAiPriority.ManualLibraryAnalysis : LocalAiPriority.AutomaticLibraryAnalysis,
                cancellationToken);
            if (analysis is null)
            {
                await _repository.SetAnalysisStatusAsync(documentId, LibraryAnalysisStatus.Failed, cancellationToken);
                return;
            }
            var existingFolder = await _repository.FindFolderByPathAsync(analysis.SuggestedPath, cancellationToken);
            var canApply = !analysis.NeedsNewFolder && existingFolder is not null && analysis.Confidence >= .80;
            await _repository.ApplyAnalysisAsync(documentId, analysis, canApply ? existingFolder!.Id : null,
                canApply ? LibraryAnalysisStatus.Ready : LibraryAnalysisStatus.NeedsReview,
                overwriteManualFields: false, cancellationToken);
        }
        catch (LocalAiNotInstalledException)
        {
            await _repository.SetAnalysisStatusAsync(documentId, LibraryAnalysisStatus.Pending, cancellationToken);
            // 模型可能在卸载/修复中，稍后自动重新入队继续分析（消费方已将其移出 active，不会去重跳过）。
            ReenqueueRequested?.Invoke(documentId, manual, TimeSpan.FromSeconds(30));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppLog.Error($"文献分析失败 {documentId}", ex);
            await _repository.SetAnalysisStatusAsync(documentId, LibraryAnalysisStatus.Failed, CancellationToken.None);
        }
        finally
        {
            lock (_active) _active.Remove(documentId);
            Completed?.Invoke();
        }
    }
}
