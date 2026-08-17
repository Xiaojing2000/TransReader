using TransReader.App.Services;
using TransReader.Core.Translation;

namespace TransReader.App.Views;

/// <summary>
/// AI 中心设置视图（SettingsView）的宿主注入契约：视图不反向依赖 MainWindow，
/// 所有服务与回调由 MainWindow 装配时传入；保存动作由视图直接调用 Store 完成，
/// 任何保存发生后视图触发 <c>SettingsChanged</c> 事件，由宿主重新加载并刷新。
/// </summary>
internal sealed record SettingsViewContext(
    /// <summary>设置存储（含预设/自定义端点/问答来源/文献开关/兜底等全部读写 API）。</summary>
    TranslationSettingsStore SettingsStore,
    /// <summary>本地模型管理器（安装/校验/卸载/状态事件）。</summary>
    LocalModelManager LocalModels,
    /// <summary>在线翻译用量统计。</summary>
    TranslationUsageStore UsageStore,
    /// <summary>当前文献库中等待自动分析的篇数（文献库整理状态展示）。</summary>
    Func<int> GetPendingAnalysisCount,
    /// <summary>本地模型安装完成后调用：把等待中的文献重新入队分析。</summary>
    Func<Task> EnqueuePendingAnalysesAsync);
