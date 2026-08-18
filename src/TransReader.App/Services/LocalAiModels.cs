using System.Diagnostics;
using System.IO.Compression;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using TransReader.Core;
using TransReader.Core.Translation;

namespace TransReader.App.Services;

internal enum LocalAiPriority
{
    ForegroundTranslation = 0,
    ManualLibraryAnalysis = 1,
    AutomaticLibraryAnalysis = 2
}

internal enum LocalModelPurpose
{
    General,
    Translation
}

internal enum LocalAiState
{
    Disabled,
    NotInstalled,
    Installing,
    Installed,
    Starting,
    Ready,
    Error
}

internal sealed record LocalAiStatus(
    LocalAiState State,
    string Message,
    long BytesReceived = 0,
    long TotalBytes = 0)
{
    public double Progress => TotalBytes <= 0 ? 0 : Math.Clamp(BytesReceived / (double)TotalBytes, 0, 1);
}

internal sealed record LocalModelDescriptor(
    string Id,
    string DisplayName,
    string ProviderId,
    LocalModelPurpose Purpose,
    string ModelVersion,
    string ModelFileName,
    long ModelSize,
    string ModelSha256,
    IReadOnlyList<string> ModelUrls,
    string RuntimeVersion,
    string RuntimeFileName,
    long RuntimeSize,
    string RuntimeSha256,
    IReadOnlyList<string> RuntimeUrls,
    string CacheIdentity,
    double Temperature);

internal static class LocalModelCatalog
{
    private const string RuntimeVersion = "llama.cpp-b9632";
    private const string RuntimeFileName = "llama-b9632-bin-win-cpu-x64.zip";
    private const long RuntimeSize = 16_899_258;
    private const string RuntimeSha = "b835d5c5155dd2a5ed748a0351debf2ede0dc9f808757e0429f8700a11832dcd";
    private static readonly string[] RuntimeUrls =
    [
        "https://github.com/ggml-org/llama.cpp/releases/download/b9632/llama-b9632-bin-win-cpu-x64.zip",
        "https://ghproxy.net/https://github.com/ggml-org/llama.cpp/releases/download/b9632/llama-b9632-bin-win-cpu-x64.zip"
    ];

    public static IReadOnlyList<LocalModelDescriptor> All { get; } =
    [
        new LocalModelDescriptor(
            Id: "qwen3-1.7b-q4-k-m",
            DisplayName: "Qwen3 1.7B Q4_K_M（问答 / 文献整理）",
            ProviderId: "local-qwen3-1.7b",
            Purpose: LocalModelPurpose.General,
            ModelVersion: "9bcdc2d70384",
            ModelFileName: "Qwen3-1.7B-Q4_K_M.gguf",
            ModelSize: 1_282_439_264,
            ModelSha256: "a7f6720f68f4a4567ebf7e3257041dd0b72077b518efe56890aec3516b59b9de",
            ModelUrls:
            [
                "https://huggingface.co/second-state/Qwen3-1.7B-GGUF/resolve/9bcdc2d703843e5e820383fe115eb0f7ad586643/Qwen3-1.7B-Q4_K_M.gguf",
                "https://hf-mirror.com/second-state/Qwen3-1.7B-GGUF/resolve/9bcdc2d703843e5e820383fe115eb0f7ad586643/Qwen3-1.7B-Q4_K_M.gguf"
            ],
            RuntimeVersion, RuntimeFileName, RuntimeSize, RuntimeSha, RuntimeUrls,
            CacheIdentity: "local:qwen3-1.7b:q4_k_m:a7f6720f68f4:local-text-v2",
            Temperature: 0.1),
        new LocalModelDescriptor(
            Id: "hy-mt2-1.8b-q4-k-m",
            DisplayName: "Hy-MT2 1.8B Q4_K_M（专业翻译）",
            ProviderId: "local-hy-mt2-1.8b",
            Purpose: LocalModelPurpose.Translation,
            ModelVersion: "1cd5208700ac",
            ModelFileName: "Hy-MT2-1.8B-Q4_K_M.gguf",
            ModelSize: 1_133_080_448,
            ModelSha256: "dc5f44fcf1fa496ee7ad725982c0c8c553a4de00259b53af84c4b89fb0c06699",
            ModelUrls:
            [
                "https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF/resolve/1cd5208700acedef4ef93019b6cfc148b8522d45/Hy-MT2-1.8B-Q4_K_M.gguf",
                "https://hf-mirror.com/tencent/Hy-MT2-1.8B-GGUF/resolve/1cd5208700acedef4ef93019b6cfc148b8522d45/Hy-MT2-1.8B-Q4_K_M.gguf"
            ],
            RuntimeVersion, RuntimeFileName, RuntimeSize, RuntimeSha, RuntimeUrls,
            CacheIdentity: "local:hy-mt2-1.8b:q4_k_m:dc5f44fcf1fa:local-translation-v1",
            Temperature: 0.7)
    ];

    public static LocalModelDescriptor General => All.Single(model => model.Purpose == LocalModelPurpose.General);
    public static LocalModelDescriptor Translation => All.Single(model => model.Purpose == LocalModelPurpose.Translation);
    public static LocalModelDescriptor Current => General;
    public static LocalModelDescriptor? ById(string id) =>
        All.FirstOrDefault(model => model.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Compatibility facade for general-purpose local AI call sites.</summary>
internal static class LocalAiManifest
{
    public static string ProviderId => LocalModelCatalog.General.ProviderId;
    public static string ModelId => LocalModelCatalog.General.Id;
    public static string ModelDisplayName => LocalModelCatalog.General.DisplayName;
    public static string ModelSha256 => LocalModelCatalog.General.ModelSha256;
    public static string CacheIdentity => LocalModelCatalog.General.CacheIdentity;
}

internal sealed class LocalAiNotInstalledException(string message) : Exception(message);

/// <summary>Manages a shared llama.cpp runtime and separately installable local models.</summary>
internal sealed class LocalModelManager : IDisposable
{
    private readonly string _root;
    private readonly string _runtimeDirectory;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly PriorityLeaseQueue<LocalAiPriority> _scheduler = new(3);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Timer _idleTimer;
    private readonly HashSet<string> _validatedModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _serverDiagnosticsGate = new();
    private readonly Queue<string> _serverDiagnostics = new();
    private Process? _server;
    private Uri? _baseUri;
    private string? _serverModelId;
    private DateTime _lastUseUtc = DateTime.UtcNow;
    private DateTime _lastStatusNotificationUtc = DateTime.MinValue;
    private int _activeSessions;
    private int _pendingStarts;
    private bool _enabled;
    private bool _disposed;

    public LocalModelManager(string root)
    {
        _root = Path.GetFullPath(root);
        _runtimeDirectory = Path.Combine(_root, "runtime", LocalModelCatalog.General.RuntimeVersion);
        _idleTimer = new Timer(_ => StopIfIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        Status = new LocalAiStatus(LocalAiState.Disabled, "本地大模型已关闭");
    }

    public event EventHandler<LocalAiStatus>? StatusChanged;
    public LocalAiStatus Status { get; private set; }
    public bool IsEnabled => _enabled;
    public int ActiveContextSize { get; private set; } = 12288;
    public string ServerPath => Path.Combine(_runtimeDirectory, "llama-server.exe");
    public bool IsInstalled => IsModelInstalled(LocalModelCatalog.General.Id);
    public bool AnyModelInstalled => LocalModelCatalog.All.Any(model => IsModelInstalled(model.Id));
    public bool IsTranslationModelInstalled => IsModelInstalled(PreferredTranslationModel.Id);
    public IReadOnlyList<LocalModelDescriptor> Models => LocalModelCatalog.All;
    public LocalModelDescriptor PreferredTranslationModel =>
        IsModelInstalled(LocalModelCatalog.Translation.Id) ? LocalModelCatalog.Translation : LocalModelCatalog.General;

    public long InstalledSize
    {
        get
        {
            try
            {
                var downloads = Path.Combine(_root, "downloads");
                return Directory.Exists(_root)
                    ? Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
                        .Where(path => !path.StartsWith(downloads, StringComparison.OrdinalIgnoreCase))
                        .Sum(path => new FileInfo(path).Length)
                    : 0;
            }
            catch (IOException) { return 0; }
            catch (UnauthorizedAccessException) { return 0; }
        }
    }

    public string GetModelPath(LocalModelDescriptor descriptor) =>
        Path.Combine(_root, "models", descriptor.Id, descriptor.ModelFileName);

    public bool IsModelInstalled(string modelId)
    {
        var descriptor = LocalModelCatalog.ById(modelId);
        if (descriptor is null || !File.Exists(ServerPath)) return false;
        var path = GetModelPath(descriptor);
        return File.Exists(path) && new FileInfo(path).Length == descriptor.ModelSize;
    }

    public void SetEnabled(bool enabled)
    {
        ThrowIfDisposed();
        if (_enabled == enabled) return;
        _enabled = enabled;
        if (!enabled)
        {
            RequestUnload();
            SetStatus(LocalAiState.Disabled, "本地大模型已关闭");
        }
        else
        {
            SetStatus(AnyModelInstalled ? LocalAiState.Installed : LocalAiState.NotInstalled,
                AnyModelInstalled ? "本地模型已安装" : "尚未安装本地模型");
        }
    }

    public async Task InstallAsync(
        string? modelId = null,
        bool forceDownload = false,
        CancellationToken cancellationToken = default)
    {
        var descriptor = modelId is null ? LocalModelCatalog.General :
            LocalModelCatalog.ById(modelId) ?? throw new ArgumentException("未知的本地模型。", nameof(modelId));
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        cancellationToken = operation.Token;
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            StopServer();
            _validatedModels.Remove(descriptor.Id);
            EnsureFreeSpace(descriptor.ModelSize * 2 + 512L * 1024 * 1024);
            Directory.CreateDirectory(_root);
            var downloads = Path.Combine(_root, "downloads");
            Directory.CreateDirectory(downloads);
            var runtimeArchive = Path.Combine(downloads, descriptor.RuntimeFileName);

            SetStatus(LocalAiState.Installing, "正在下载本地推理引擎…", 0, descriptor.RuntimeSize);
            await VerifiedDownloader.DownloadAsync(
                descriptor.RuntimeUrls, runtimeArchive, descriptor.RuntimeSize, descriptor.RuntimeSha256,
                value => SetStatus(LocalAiState.Installing, $"正在从 {value.Host} 下载推理引擎…", value.BytesReceived, value.TotalBytes),
                forceDownload, cancellationToken).ConfigureAwait(false);

            if (forceDownload || !await VerifyRuntimeFilesAsync(runtimeArchive, descriptor, cancellationToken).ConfigureAwait(false))
            {
                SetStatus(LocalAiState.Installing, "正在安装本地推理引擎…");
                InstallRuntime(runtimeArchive);
            }

            var modelPath = GetModelPath(descriptor);
            Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
            SetStatus(LocalAiState.Installing, $"正在下载 {descriptor.DisplayName}…", 0, descriptor.ModelSize);
            var modelDownload = await VerifiedDownloader.DownloadAsync(
                descriptor.ModelUrls, modelPath, descriptor.ModelSize, descriptor.ModelSha256,
                value => SetStatus(LocalAiState.Installing, $"正在从 {value.Host} 下载 {descriptor.DisplayName}…", value.BytesReceived, value.TotalBytes),
                forceDownload, cancellationToken).ConfigureAwait(false);
            _validatedModels.Add(descriptor.Id);
            _enabled = true;
            SetStatus(LocalAiState.Installed, $"{descriptor.DisplayName} 安装完成（来源：{modelDownload.Host}）");
        }
        catch (OperationCanceledException)
        {
            SetStatus(AnyModelInstalled ? LocalAiState.Installed : LocalAiState.NotInstalled, "安装已取消，可稍后继续");
            throw;
        }
        catch (Exception ex)
        {
            SetStatus(LocalAiState.Error, $"本地模型安装失败：{ex.Message}");
            throw;
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task<bool> VerifyAsync(string? modelId = null, CancellationToken cancellationToken = default)
    {
        var descriptor = modelId is null ? LocalModelCatalog.General :
            LocalModelCatalog.ById(modelId) ?? throw new ArgumentException("未知的本地模型。", nameof(modelId));
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetStatus(LocalAiState.Installing, $"正在校验 {descriptor.DisplayName}…");
            var valid = await VerifyDescriptorAsync(descriptor, cancellationToken).ConfigureAwait(false);
            if (valid) _validatedModels.Add(descriptor.Id); else _validatedModels.Remove(descriptor.Id);
            SetStatus(valid ? (_enabled ? LocalAiState.Installed : LocalAiState.Disabled) : LocalAiState.Error,
                valid ? $"{descriptor.DisplayName} 校验通过" : "本地模型文件损坏，请执行安装 / 修复");
            return valid;
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task ReloadAsync(LocalModelPurpose purpose = LocalModelPurpose.General, CancellationToken cancellationToken = default)
    {
        if (!_enabled) throw new InvalidOperationException("本地大模型已关闭。");
        StopServer();
        var descriptor = purpose == LocalModelPurpose.Translation ? PreferredTranslationModel : LocalModelCatalog.General;
        _validatedModels.Remove(descriptor.Id);
        await EnsureRunningAsync(descriptor, cancellationToken).ConfigureAwait(false);
        _lastUseUtc = DateTime.UtcNow;
    }

    public async Task UninstallAsync(CancellationToken cancellationToken = default)
    {
        using var inferenceLease = await _scheduler.AcquireAsync(LocalAiPriority.AutomaticLibraryAnalysis, cancellationToken)
            .ConfigureAwait(false);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StopServer();
            _validatedModels.Clear();
            var directory = new DirectoryInfo(_root);
            if (!directory.Name.Equals("local-ai", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("拒绝删除非 TransReader 本地 AI 目录。");
            if (directory.Exists) directory.Delete(true);
            _enabled = false;
            SetStatus(LocalAiState.Disabled, "本地模型已卸载");
        }
        finally { _lifecycleGate.Release(); }
    }

    public Task<LocalAiSession> OpenSessionAsync(LocalAiPriority priority, CancellationToken cancellationToken) =>
        OpenSessionAsync(priority, LocalModelPurpose.General, cancellationToken);

    public async Task<LocalAiSession> OpenSessionAsync(
        LocalAiPriority priority,
        LocalModelPurpose purpose,
        CancellationToken cancellationToken)
    {
        if (!_enabled) throw new TranslationException("本地大模型已关闭，请在“本地组件”中开启。");
        if (Status.State == LocalAiState.Installing) throw new TranslationException("本地模型正在安装中，请稍候再试。");
        var descriptor = purpose == LocalModelPurpose.Translation ? PreferredTranslationModel : LocalModelCatalog.General;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        cancellationToken = operation.Token;
        Interlocked.Increment(ref _pendingStarts);
        try
        {
            var lease = await _scheduler.AcquireAsync(priority, cancellationToken).ConfigureAwait(false);
            try
            {
                var uri = await EnsureRunningAsync(descriptor, cancellationToken).ConfigureAwait(false);
                _lastUseUtc = DateTime.UtcNow;
                Interlocked.Increment(ref _activeSessions);
                return new LocalAiSession(uri, descriptor, () =>
                {
                    _lastUseUtc = DateTime.UtcNow;
                    Interlocked.Decrement(ref _activeSessions);
                    lease.Dispose();
                });
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
        finally { Interlocked.Decrement(ref _pendingStarts); }
    }

    private async Task<Uri> EnsureRunningAsync(LocalModelDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (!IsModelInstalled(descriptor.Id))
            throw new LocalAiNotInstalledException($"{descriptor.DisplayName} 尚未安装，请在“本地组件”中安装。");
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!_validatedModels.Contains(descriptor.Id))
            {
                SetStatus(LocalAiState.Installing, $"正在校验 {descriptor.DisplayName}…");
                if (!await VerifyDescriptorAsync(descriptor, cancellationToken).ConfigureAwait(false))
                    throw new InvalidDataException("本地模型或运行时校验失败，请执行“安装 / 修复”。");
                _validatedModels.Add(descriptor.Id);
            }
            if (_server is { HasExited: false } && _baseUri is not null && _serverModelId == descriptor.Id) return _baseUri;
            StopServer();
            var contextSize = SelectContextSize(out var degraded);
            ActiveContextSize = contextSize;
            SetStatus(LocalAiState.Starting, degraded
                ? $"正在加载 {descriptor.DisplayName}（已降低上下文长度）…"
                : $"正在加载 {descriptor.DisplayName}…");
            Exception? lastError = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var port = ReservePort();
                var startInfo = new ProcessStartInfo
                {
                    FileName = ServerPath,
                    WorkingDirectory = _runtimeDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                foreach (var argument in new[]
                {
                    "--model", GetModelPath(descriptor), "--host", "127.0.0.1", "--port", port.ToString(),
                    "--ctx-size", contextSize.ToString(), "--threads", Math.Max(1, Math.Min(Environment.ProcessorCount, 8)).ToString(),
                    "--parallel", "1", "--jinja", "--no-webui"
                }) startInfo.ArgumentList.Add(argument);
                ResetServerDiagnostics();
                var server = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                server.OutputDataReceived += (_, args) => AppendServerDiagnostic(args.Data);
                server.ErrorDataReceived += (_, args) => AppendServerDiagnostic(args.Data);
                server.Exited += (_, _) => OnServerExited(server, descriptor);
                try
                {
                    if (!server.Start()) throw new InvalidOperationException("无法启动本地推理服务。");
                    server.BeginOutputReadLine();
                    server.BeginErrorReadLine();
                }
                catch
                {
                    server.Dispose();
                    throw;
                }
                _server = server;
                _serverModelId = descriptor.Id;
                var baseUri = new Uri($"http://127.0.0.1:{port}/v1/");
                _baseUri = baseUri;
                try { return await WaitForReadyAsync(descriptor, server, baseUri, cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
                {
                    lastError = ex;
                    StopServer();
                }
            }
            throw lastError ?? new InvalidOperationException("本地推理服务启动失败。");
        }
        catch (Exception ex)
        {
            SetStatus(LocalAiState.Error, ex.Message);
            throw;
        }
        finally { _lifecycleGate.Release(); }
    }

    private async Task<Uri> WaitForReadyAsync(
        LocalModelDescriptor descriptor,
        Process server,
        Uri baseUri,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (server.HasExited)
                throw new InvalidOperationException(
                    $"本地推理服务启动失败，退出码 {server.ExitCode}。{FormatServerDiagnostics()}");
            try
            {
                using var response = await client.GetAsync(new Uri(baseUri, "models"), cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    SetStatus(LocalAiState.Ready, $"{descriptor.DisplayName} 已就绪");
                    return baseUri;
                }
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException($"本地模型在 90 秒内未能完成加载。{FormatServerDiagnostics()}");
    }

    private async Task<bool> VerifyDescriptorAsync(LocalModelDescriptor descriptor, CancellationToken cancellationToken)
    {
        var runtimeArchive = Path.Combine(_root, "downloads", descriptor.RuntimeFileName);
        return await VerifyRuntimeFilesAsync(runtimeArchive, descriptor, cancellationToken).ConfigureAwait(false) &&
               await VerifiedDownloader.VerifyFileAsync(GetModelPath(descriptor), descriptor.ModelSize,
                   descriptor.ModelSha256, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> VerifyRuntimeFilesAsync(
        string archivePath,
        LocalModelDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        if (!await VerifiedDownloader.VerifyFileAsync(archivePath, descriptor.RuntimeSize,
                descriptor.RuntimeSha256, cancellationToken).ConfigureAwait(false)) return false;
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var installed = Path.Combine(_runtimeDirectory, entry.Name);
                if (!File.Exists(installed) || new FileInfo(installed).Length != entry.Length) return false;
                await using var expectedStream = entry.Open();
                await using var actualStream = new FileStream(installed, FileMode.Open, FileAccess.Read, FileShare.Read,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var expectedHash = await SHA256.HashDataAsync(expectedStream, cancellationToken).ConfigureAwait(false);
                var actualHash = await SHA256.HashDataAsync(actualStream, cancellationToken).ConfigureAwait(false);
                if (!expectedHash.AsSpan().SequenceEqual(actualHash)) return false;
            }
            return File.Exists(ServerPath);
        }
        catch (InvalidDataException) { return false; }
        catch (IOException) { return false; }
    }

    private void InstallRuntime(string archivePath)
    {
        var temporary = $"{_runtimeDirectory}.{Guid.NewGuid():N}.installing";
        if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
        Directory.CreateDirectory(temporary);
        try
        {
            ZipFile.ExtractToDirectory(archivePath, temporary, overwriteFiles: true);
            var nestedServer = Directory.EnumerateFiles(temporary, "llama-server.exe", SearchOption.AllDirectories)
                .FirstOrDefault() ?? throw new InvalidDataException("推理引擎包中未找到 llama-server.exe。");
            var nestedRoot = Path.GetDirectoryName(nestedServer)!;
            if (!Path.GetFullPath(nestedRoot).Equals(Path.GetFullPath(temporary), StringComparison.OrdinalIgnoreCase))
            {
                var flattened = temporary + ".flat";
                Directory.CreateDirectory(flattened);
                foreach (var file in Directory.EnumerateFiles(nestedRoot))
                    File.Copy(file, Path.Combine(flattened, Path.GetFileName(file)), true);
                Directory.Delete(temporary, true);
                Directory.Move(flattened, temporary);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_runtimeDirectory)!);
            if (Directory.Exists(_runtimeDirectory)) Directory.Delete(_runtimeDirectory, true);
            Directory.Move(temporary, _runtimeDirectory);
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    private static int SelectContextSize(out bool degraded)
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status)) { degraded = true; return 8192; }
        var availableCommit = (long)status.ullAvailPageFile;
        const long gib = 1024L * 1024 * 1024;
        var contextSize = availableCommit >= 3L * gib ? 12288
            : availableCommit >= (long)(2.2 * gib) ? 8192
            : throw new InvalidOperationException(
                $"可用内存不足：本地模型至少需要约 2.2 GB 可提交内存，当前仅剩 {availableCommit / (double)gib:0.#} GB。");
        degraded = contextSize < 12288;
        return contextSize;
    }

    private void StopIfIdle()
    {
        if (_disposed || !_enabled || Status.State == LocalAiState.Installing) return;
        if (Volatile.Read(ref _pendingStarts) > 0 || Volatile.Read(ref _activeSessions) > 0) return;
        if (DateTime.UtcNow - _lastUseUtc < TimeSpan.FromMinutes(10) || !_lifecycleGate.Wait(0)) return;
        try
        {
            if (Volatile.Read(ref _pendingStarts) > 0 || Volatile.Read(ref _activeSessions) > 0) return;
            if (StopServer() && AnyModelInstalled)
                SetStatus(LocalAiState.Installed, "本地模型已卸载内存");
        }
        finally { _lifecycleGate.Release(); }
    }

    public void RequestUnload()
    {
        if (_disposed) return;
        if (!_lifecycleGate.Wait(0)) return;
        try { StopServer(); }
        finally { _lifecycleGate.Release(); }
    }

    private bool StopServer()
    {
        var server = Interlocked.Exchange(ref _server, null);
        _baseUri = null;
        _serverModelId = null;
        if (server is null) return false;
        try
        {
            if (!server.HasExited && !server.WaitForExit(2000))
            {
                server.Kill(entireProcessTree: true);
                server.WaitForExit(2000);
            }
        }
        catch { }
        server.Dispose();
        return true;
    }

    private void OnServerExited(Process server, LocalModelDescriptor descriptor)
    {
        if (Interlocked.CompareExchange(ref _server, null, server) != server) return;
        _baseUri = null;
        _serverModelId = null;
        int exitCode;
        try { exitCode = server.ExitCode; }
        catch { exitCode = -1; }
        var message = $"{descriptor.DisplayName} 推理服务意外退出（退出码 {exitCode}）。{FormatServerDiagnostics()}";
        try { server.Dispose(); } catch { }
        if (!_disposed) SetStatus(LocalAiState.Error, message);
    }

    private void ResetServerDiagnostics()
    {
        lock (_serverDiagnosticsGate) _serverDiagnostics.Clear();
    }

    private void AppendServerDiagnostic(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_serverDiagnosticsGate)
        {
            _serverDiagnostics.Enqueue(line.Trim());
            while (_serverDiagnostics.Count > 80) _serverDiagnostics.Dequeue();
        }
    }

    private string FormatServerDiagnostics()
    {
        lock (_serverDiagnosticsGate)
        {
            if (_serverDiagnostics.Count == 0) return string.Empty;
            var text = string.Join(" | ", _serverDiagnostics.TakeLast(12));
            return $" llama.cpp：{text}";
        }
    }

    private void SetStatus(LocalAiState state, string message, long received = 0, long total = 0)
    {
        var previous = Status;
        Status = new LocalAiStatus(state, message, received, total);
        var now = DateTime.UtcNow;
        if (previous.State == state && previous.Message == message && received < total &&
            now - _lastStatusNotificationUtc < TimeSpan.FromMilliseconds(150)) return;
        _lastStatusNotificationUtc = now;
        StatusChanged?.Invoke(this, Status);
        AppLog.Info($"[本地模型] {message}");
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private void EnsureFreeSpace(long bytes)
    {
        var drive = new DriveInfo(Path.GetPathRoot(_root)!);
        if (drive.AvailableFreeSpace < bytes) throw new IOException($"安装本地模型至少需要 {bytes / 1024d / 1024d / 1024d:0.#} GB 可用空间。");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        _idleTimer.Dispose();
        try { _lifecycleGate.Wait(); StopServer(); }
        catch (ObjectDisposedException) { }
        finally { try { _lifecycleGate.Release(); } catch { } }
        _scheduler.Dispose();
        _shutdown.Dispose();
        _lifecycleGate.Dispose();
    }
}

internal sealed class LocalAiSession(Uri baseUri, LocalModelDescriptor descriptor, Action dispose) : IDisposable
{
    private Action? _dispose = dispose;
    public Uri BaseUri { get; } = baseUri;
    public LocalModelDescriptor Descriptor { get; } = descriptor;
    public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
}
