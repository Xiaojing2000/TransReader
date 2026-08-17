using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using TransReader.Core.Translation;
using TransReader.Core;

namespace TransReader.App.Services;

internal enum LocalAiPriority
{
    ForegroundTranslation = 0,
    ManualLibraryAnalysis = 1,
    AutomaticLibraryAnalysis = 2
}

internal enum LocalAiState
{
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

/// <summary>本地模型描述符。清单化结构，便于未来多模型并存与按需加载/卸载。
/// URL 为有序源列表（官方源 + 镜像）；镜像文件与官方同源，SHA256/大小校验值不变。</summary>
internal sealed record LocalModelDescriptor(
    string Id,
    string DisplayName,
    string ProviderId,
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
    string CacheIdentity);

/// <summary>本地模型清单。当前仅含一个 CPU 模型；新增模型在此追加描述符即可。</summary>
internal static class LocalModelCatalog
{
    public static IReadOnlyList<LocalModelDescriptor> All { get; } =
    [
        new LocalModelDescriptor(
            Id: "qwen3-1.7b-q4-k-m",
            DisplayName: "Qwen3 1.7B Q4_K_M",
            ProviderId: "local-qwen3-1.7b",
            ModelVersion: "9bcdc2d70384",
            ModelFileName: "Qwen3-1.7B-Q4_K_M.gguf",
            ModelSize: 1_282_439_264,
            ModelSha256: "a7f6720f68f4a4567ebf7e3257041dd0b72077b518efe56890aec3516b59b9de",
            ModelUrls:
            [
                "https://huggingface.co/second-state/Qwen3-1.7B-GGUF/resolve/9bcdc2d703843e5e820383fe115eb0f7ad586643/Qwen3-1.7B-Q4_K_M.gguf",
                "https://hf-mirror.com/second-state/Qwen3-1.7B-GGUF/resolve/9bcdc2d703843e5e820383fe115eb0f7ad586643/Qwen3-1.7B-Q4_K_M.gguf"
            ],
            RuntimeVersion: "llama.cpp-b9632",
            RuntimeFileName: "llama-b9632-bin-win-cpu-x64.zip",
            RuntimeSize: 16_899_258,
            RuntimeSha256: "b835d5c5155dd2a5ed748a0351debf2ede0dc9f808757e0429f8700a11832dcd",
            RuntimeUrls:
            [
                "https://github.com/ggml-org/llama.cpp/releases/download/b9632/llama-b9632-bin-win-cpu-x64.zip",
                "https://gh-proxy.com/https://github.com/ggml-org/llama.cpp/releases/download/b9632/llama-b9632-bin-win-cpu-x64.zip"
            ],
            CacheIdentity: "local:qwen3-1.7b:q4_k_m:a7f6720f68f4:local-text-v2")
    ];

    public static LocalModelDescriptor Current => All[0];
    public static LocalModelDescriptor? ById(string id) =>
        All.FirstOrDefault(descriptor => string.Equals(descriptor.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>当前本地模型各字段的稳定访问入口（委托到 <see cref="LocalModelCatalog"/>，保持旧调用点不变）。</summary>
internal static class LocalAiManifest
{
    public static string ProviderId => LocalModelCatalog.Current.ProviderId;
    public static string ModelId => LocalModelCatalog.Current.Id;
    public static string ModelDisplayName => LocalModelCatalog.Current.DisplayName;
    public static string ModelVersion => LocalModelCatalog.Current.ModelVersion;
    public static string ModelFileName => LocalModelCatalog.Current.ModelFileName;
    public static long ModelSize => LocalModelCatalog.Current.ModelSize;
    public static string ModelSha256 => LocalModelCatalog.Current.ModelSha256;
    public static IReadOnlyList<string> ModelUrls => LocalModelCatalog.Current.ModelUrls;
    public static string RuntimeVersion => LocalModelCatalog.Current.RuntimeVersion;
    public static string RuntimeFileName => LocalModelCatalog.Current.RuntimeFileName;
    public static long RuntimeSize => LocalModelCatalog.Current.RuntimeSize;
    public static string RuntimeSha256 => LocalModelCatalog.Current.RuntimeSha256;
    public static IReadOnlyList<string> RuntimeUrls => LocalModelCatalog.Current.RuntimeUrls;
    public static string CacheIdentity => LocalModelCatalog.Current.CacheIdentity;
}

internal sealed class LocalAiNotInstalledException(string message) : Exception(message);

internal sealed class LocalModelManager : IDisposable
{
    private static readonly HttpClient DownloadClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly string _root;
    private readonly string _runtimeDirectory;
    private readonly string _modelDirectory;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly PriorityLeaseQueue<LocalAiPriority> _scheduler = new(3);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Timer _idleTimer;
    private Process? _server;
    private Uri? _baseUri;
    private DateTime _lastUseUtc = DateTime.UtcNow;
    private DateTime _lastStatusNotificationUtc = DateTime.MinValue;
    private int _activeSessions;
    private int _pendingStarts;
    private bool _validatedThisRun;
    private bool _disposed;

    public LocalModelManager(string root)
    {
        _root = root;
        _runtimeDirectory = Path.Combine(root, "runtime", LocalAiManifest.RuntimeVersion);
        _modelDirectory = Path.Combine(root, "models", LocalAiManifest.ModelId);
        _idleTimer = new Timer(_ => StopIfIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        Status = IsInstalled
            ? new LocalAiStatus(LocalAiState.Installed, "本地模型已安装")
            : new LocalAiStatus(LocalAiState.NotInstalled, "尚未安装本地模型");
    }

    public event EventHandler<LocalAiStatus>? StatusChanged;
    public LocalAiStatus Status { get; private set; }
    /// <summary>当前运行中的推理服务实际上下文长度（由 SelectContextSize 按内存选定；未启动时为满档 12288）。</summary>
    public int ActiveContextSize { get; private set; } = 12288;
    public bool IsInstalled => File.Exists(ServerPath) &&
                               File.Exists(ModelPath) &&
                               new FileInfo(ModelPath).Length == LocalAiManifest.ModelSize;
    public string ModelPath => Path.Combine(_modelDirectory, LocalAiManifest.ModelFileName);
    public string ServerPath => Path.Combine(_runtimeDirectory, "llama-server.exe");
    public long InstalledSize
    {
        get
        {
            try
            {
                // downloads 里的安装包残档不计入占用显示。
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

    public async Task InstallAsync(CancellationToken cancellationToken = default)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        cancellationToken = operation.Token;
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            StopServer();
            _validatedThisRun = false;
            EnsureFreeSpace(3L * 1024 * 1024 * 1024);
            SetStatus(LocalAiState.Installing, "正在下载本地推理引擎…");
            Directory.CreateDirectory(_root);
            var downloads = Path.Combine(_root, "downloads");
            Directory.CreateDirectory(downloads);
            // 清理 prior-runtime-version 残档：删除与当前版本不符的旧 zip（版本升级后遗留）。
            // 保留当前 RuntimeFileName 供 VerifyAsync 哈希校验。
            foreach (var stale in Directory.EnumerateFiles(downloads, "*.zip"))
            {
                if (!Path.GetFileName(stale).Equals(LocalAiManifest.RuntimeFileName, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(stale); } catch (IOException) { }
                }
            }
            var runtimeArchive = Path.Combine(downloads, LocalAiManifest.RuntimeFileName);
            await DownloadVerifiedAsync(LocalAiManifest.RuntimeUrls, runtimeArchive,
                LocalAiManifest.RuntimeSize, LocalAiManifest.RuntimeSha256, cancellationToken).ConfigureAwait(false);

            SetStatus(LocalAiState.Installing, "正在安装本地推理引擎…");
            var temporaryRuntime = $"{_runtimeDirectory}.{Guid.NewGuid():N}.installing";
            if (Directory.Exists(temporaryRuntime)) Directory.Delete(temporaryRuntime, true);
            Directory.CreateDirectory(temporaryRuntime);
            ZipFile.ExtractToDirectory(runtimeArchive, temporaryRuntime, overwriteFiles: true);
            var nestedServer = Directory.EnumerateFiles(temporaryRuntime, "llama-server.exe", SearchOption.AllDirectories)
                .FirstOrDefault() ?? throw new InvalidDataException("推理引擎包中未找到 llama-server.exe。");
            var nestedRoot = Path.GetDirectoryName(nestedServer)!;
            if (!Path.GetFullPath(nestedRoot).Equals(Path.GetFullPath(temporaryRuntime), StringComparison.OrdinalIgnoreCase))
            {
                var flattened = $"{temporaryRuntime}.flat";
                Directory.CreateDirectory(flattened);
                foreach (var file in Directory.EnumerateFiles(nestedRoot)) File.Copy(file, Path.Combine(flattened, Path.GetFileName(file)));
                Directory.Delete(temporaryRuntime, true);
                Directory.Move(flattened, temporaryRuntime);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_runtimeDirectory)!);
            if (Directory.Exists(_runtimeDirectory)) Directory.Delete(_runtimeDirectory, true);
            Directory.Move(temporaryRuntime, _runtimeDirectory);

            SetStatus(LocalAiState.Installing, "正在下载 Qwen3 1.7B 本地模型…");
            Directory.CreateDirectory(_modelDirectory);
            await DownloadVerifiedAsync(LocalAiManifest.ModelUrls, ModelPath,
                LocalAiManifest.ModelSize, LocalAiManifest.ModelSha256, cancellationToken).ConfigureAwait(false);
            _validatedThisRun = true;
            SetStatus(LocalAiState.Installed, "本地模型安装完成");
        }
        catch (OperationCanceledException)
        {
            SetStatus(IsInstalled ? LocalAiState.Installed : LocalAiState.NotInstalled, "安装已取消，可稍后继续");
            throw;
        }
        catch (Exception ex)
        {
            SetStatus(LocalAiState.Error, $"本地模型安装失败：{ex.Message}");
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<bool> VerifyAsync(CancellationToken cancellationToken = default)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        cancellationToken = operation.Token;
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsInstalled) return false;
            SetStatus(LocalAiState.Installing, "正在校验本地模型…");
            var valid = await VerifyFileAsync(ModelPath, LocalAiManifest.ModelSize, LocalAiManifest.ModelSha256, cancellationToken).ConfigureAwait(false) &&
                        await VerifyFileAsync(Path.Combine(_root, "downloads", LocalAiManifest.RuntimeFileName),
                            LocalAiManifest.RuntimeSize, LocalAiManifest.RuntimeSha256, cancellationToken).ConfigureAwait(false);
            _validatedThisRun = valid;
            SetStatus(valid ? LocalAiState.Installed : LocalAiState.Error,
                valid ? "本地模型校验通过" : "本地模型文件损坏，请修复安装");
            return valid;
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task UninstallAsync(CancellationToken cancellationToken = default)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        cancellationToken = operation.Token;
        // 以最低优先级排队，等当前/已排队的推理任务结束后再卸载，避免杀掉进行中的分析。
        using var inferenceLease = await _scheduler.AcquireAsync(
            LocalAiPriority.AutomaticLibraryAnalysis, cancellationToken).ConfigureAwait(false);
        SetStatus(LocalAiState.Installing, "正在等待当前推理任务完成…");
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StopServer();
            _validatedThisRun = false;
            var fullRoot = Path.GetFullPath(_root);
            var expectedParent = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TransReader"));
            if (!fullRoot.StartsWith(expectedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("拒绝删除非 TransReader 本地 AI 目录。");
            if (Directory.Exists(fullRoot)) Directory.Delete(fullRoot, true);
            SetStatus(LocalAiState.NotInstalled, "本地模型已卸载");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<LocalAiSession> OpenSessionAsync(LocalAiPriority priority, CancellationToken cancellationToken)
    {
        if (Status.State == LocalAiState.Installing)
            throw new TranslationException("本地模型正在安装中，请稍候再试。");
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        cancellationToken = operation.Token;
        // 先标记"正在建立会话"，避免空闲定时器在服务器刚启动完成时将其杀掉（见 StopIfIdle）。
        Interlocked.Increment(ref _pendingStarts);
        try
        {
            var lease = await _scheduler.AcquireAsync(priority, cancellationToken).ConfigureAwait(false);
            try
            {
                var uri = await EnsureRunningAsync(cancellationToken).ConfigureAwait(false);
                _lastUseUtc = DateTime.UtcNow;
                Interlocked.Increment(ref _activeSessions);
                return new LocalAiSession(uri, () =>
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
        finally
        {
            Interlocked.Decrement(ref _pendingStarts);
        }
    }

    private async Task<Uri> EnsureRunningAsync(CancellationToken cancellationToken)
    {
        if (!IsInstalled)
            throw new LocalAiNotInstalledException("本地 AI 尚未安装。请在翻译设置的“本地 AI”区域下载安装。");
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!_validatedThisRun)
            {
                SetStatus(LocalAiState.Installing, "正在校验本地模型完整性…");
                var valid = await VerifyFileAsync(
                    ModelPath, LocalAiManifest.ModelSize, LocalAiManifest.ModelSha256, cancellationToken).ConfigureAwait(false) &&
                    await VerifyFileAsync(
                        Path.Combine(_root, "downloads", LocalAiManifest.RuntimeFileName),
                        LocalAiManifest.RuntimeSize, LocalAiManifest.RuntimeSha256, cancellationToken).ConfigureAwait(false);
                if (!valid) throw new InvalidDataException("本地模型或运行时校验失败，请执行“安装 / 修复”。");
                _validatedThisRun = true;
            }
            if (_server is { HasExited: false } && _baseUri is not null) return _baseUri;
            StopServer();
            var contextSize = SelectContextSize(out var degraded);
            ActiveContextSize = contextSize;
            SetStatus(LocalAiState.Starting, degraded
                ? "正在加载 Qwen3 1.7B（可用内存有限，已自动降低上下文长度）…"
                : "正在加载 Qwen3 1.7B…");
            // ReservePort 存在"端口释放后可能被抢占"的窗口，启动失败时换新端口重试一次。
            Exception? lastError = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var port = ReservePort();
                var threads = Math.Max(1, Math.Min(Environment.ProcessorCount, 8));
                var startInfo = new ProcessStartInfo
                {
                    FileName = ServerPath,
                    WorkingDirectory = _runtimeDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                foreach (var argument in new[]
                         {
                             "--model", ModelPath, "--host", "127.0.0.1", "--port", port.ToString(),
                             "--ctx-size", contextSize.ToString(), "--threads", threads.ToString(), "--parallel", "1", "--jinja", "--no-webui"
                         }) startInfo.ArgumentList.Add(argument);
                _server = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动本地推理服务。");
                _baseUri = new Uri($"http://127.0.0.1:{port}/v1/");
                try
                {
                    return await WaitForReadyAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
                {
                    lastError = ex;
                    StopServer();
                }
            }
            SetStatus(LocalAiState.Error, lastError?.Message ?? "本地推理服务启动失败。");
            throw lastError ?? new InvalidOperationException("本地推理服务启动失败。");
        }
        catch (Exception ex)
        {
            SetStatus(LocalAiState.Error, ex.Message);
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// 启动前按可用提交内存选择上下文长度。模型权重走 mmap 文件映射（不占提交内存，物理页由系统
    /// 按需换入/换出），真正吃内存的是 KV cache 与计算缓冲——因此按提交内存（物理 + 分页文件可提交余量）
    /// 评估，而不是按空闲物理内存设固定门槛。档位下限 8192：本地分块的提示词最坏约 4900 tokens
    /// （见 LocalTranslationChunker 预算注释），再留 3072 输出预算，4096 档必然超窗被静默滑窗，不提供。
    /// </summary>
    private static int SelectContextSize(out bool degraded)
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            degraded = true; // 查询失败不阻塞启动，取低档
            return 8192;
        }
        var availableCommit = (long)status.ullAvailPageFile;
        const long gib = 1024L * 1024 * 1024;
        // 估算：KV(f16) 12288≈1.4GB、8192≈0.9GB，另计约 0.9GB 权重常驻下限+计算缓冲与系统余量。
        var contextSize = availableCommit >= (long)(3.0 * gib) ? 12288
            : availableCommit >= (long)(2.2 * gib) ? 8192
            : throw new InvalidOperationException(
                $"可用内存不足：本地模型至少需要约 2.2 GB 可提交内存，当前仅剩 {availableCommit / 1024d / 1024d / 1024d:0.#} GB。请关闭其他大型程序后重试。");
        degraded = contextSize < 12288;
        return contextSize;
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

    private async Task<Uri> WaitForReadyAsync(CancellationToken cancellationToken)
    {
        using var healthClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_server is { HasExited: true })
            {
                throw new InvalidOperationException($"本地推理服务启动失败，退出码 {_server.ExitCode}。");
            }
            try
            {
                using var response = await healthClient.GetAsync(
                    new Uri(_baseUri!, "models"), cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    SetStatus(LocalAiState.Ready, "本地 Qwen3 1.7B 已就绪");
                    return _baseUri!;
                }
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("本地模型在 90 秒内未能完成加载。");
    }

    /// <summary>
    /// 多源下载：按源顺序尝试，每源最多 2 次（退避 1s；源间退避 3s）；读取带 30s 停滞检测。
    /// `.partial` 断点跨源保留（镜像同源文件，Range 续传有效）；全部源失败时聚合报错。
    /// </summary>
    private async Task DownloadVerifiedAsync(IReadOnlyList<string> urls, string destination, long expectedSize,
        string expectedSha256, CancellationToken cancellationToken)
    {
        if (await VerifyFileAsync(destination, expectedSize, expectedSha256, cancellationToken).ConfigureAwait(false)) return;
        var partial = destination + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (await VerifyFileAsync(partial, expectedSize, expectedSha256, cancellationToken).ConfigureAwait(false))
        {
            File.Move(partial, destination, true);
            return;
        }

        var failures = new List<string>();
        for (var source = 0; source < urls.Count; source++)
        {
            var host = new Uri(urls[source]).Host;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await DownloadFromSourceAsync(urls[source], host, partial, expectedSize, cancellationToken).ConfigureAwait(false);
                    if (await VerifyFileAsync(partial, expectedSize, expectedSha256, cancellationToken).ConfigureAwait(false))
                    {
                        File.Move(partial, destination, true);
                        return;
                    }
                    // 完整下载但校验失败：内容不可信，直接换源（本源不再重试）。
                    failures.Add($"{host}：下载内容校验失败");
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures.Add($"{host}：{ex.Message}");
                    if (attempt == 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            if (source + 1 < urls.Count)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            }
        }
        throw new IOException($"下载 {Path.GetFileName(destination)} 失败，已尝试全部 {urls.Count} 个镜像源：{string.Join("；", failures)}");
    }

    private async Task DownloadFromSourceAsync(string url, string host, string partial, long expectedSize,
        CancellationToken cancellationToken)
    {
        var existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (existing >= expectedSize)
        {
            File.Delete(partial);
            existing = 0;
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
        using var response = await DownloadClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (existing > 0 && response.StatusCode != HttpStatusCode.PartialContent)
        {
            existing = 0;
        }
        response.EnsureSuccessStatusCode();
        SetStatus(LocalAiState.Installing, $"正在从 {host} 下载…", existing, expectedSize);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(partial, existing == 0 ? FileMode.Create : FileMode.Append,
            FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[128 * 1024];
        var received = existing;
        while (true)
        {
            // 停滞检测：30 秒无任何字节视为源故障，保留断点换源重试（DownloadClient 为无限超时）。
            var read = await input.ReadAsync(buffer, cancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (read <= 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            SetStatus(LocalAiState.Installing, $"正在从 {host} 下载…", received, expectedSize);
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Close();
    }

    private static async Task<bool> VerifyFileAsync(string path, long size, string sha256, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != size) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash).Equals(sha256, StringComparison.OrdinalIgnoreCase);
    }

    private void EnsureFreeSpace(long bytes)
    {
        var path = Path.GetPathRoot(Path.GetFullPath(_root))!;
        if (new DriveInfo(path).AvailableFreeSpace < bytes)
            throw new IOException("安装本地 AI 至少需要 3 GB 可用磁盘空间。");
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private void StopIfIdle()
    {
        if (_disposed) return;
        if (Status.State == LocalAiState.Installing) return;
        if (Volatile.Read(ref _pendingStarts) > 0) return;
        if (Volatile.Read(ref _activeSessions) > 0) return;
        if (DateTime.UtcNow - _lastUseUtc < TimeSpan.FromMinutes(10)) return;
        try
        {
            _lifecycleGate.Wait();
            try
            {
                // 拿锁期间可能有新会话建立（服务器刚启动完成），必须重新检查。
                if (_disposed) return;
                if (Status.State == LocalAiState.Installing) return;
                if (Volatile.Read(ref _pendingStarts) > 0) return;
                if (Volatile.Read(ref _activeSessions) > 0) return;
                if (DateTime.UtcNow - _lastUseUtc < TimeSpan.FromMinutes(10)) return;
                StopServer();
                if (IsInstalled) SetStatus(LocalAiState.Installed, "本地模型已卸载内存");
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }
        catch (ObjectDisposedException)
        {
            // Dispose 可能在与本回调并发的过程中释放信号量，忽略即可。
        }
    }

    /// <summary>
    /// 切回在线模式时主动卸载本地推理服务（常驻约 1.5–2.5GB）。有活跃/启动中会话或拿不到
    /// 生命周期锁时跳过——由 10 分钟空闲定时器（StopIfIdle）兜底，绝不阻塞 UI。
    /// </summary>
    public void RequestUnload()
    {
        if (_disposed) return;
        if (Volatile.Read(ref _pendingStarts) > 0 || Volatile.Read(ref _activeSessions) > 0) return;
        if (!_lifecycleGate.Wait(0)) return;
        try
        {
            // 拿锁后复检：等待期间可能有新会话建立。
            if (_disposed) return;
            if (Volatile.Read(ref _pendingStarts) > 0 || Volatile.Read(ref _activeSessions) > 0) return;
            StopServer();
            if (Status.State is LocalAiState.Ready or LocalAiState.Starting)
            {
                SetStatus(LocalAiState.Installed, "本地模型已卸载内存");
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void StopServer()
    {
        var server = Interlocked.Exchange(ref _server, null);
        _baseUri = null;
        if (server is null) return;
        try
        {
            if (!server.HasExited)
            {
                server.CloseMainWindow();
                if (!server.WaitForExit(2000)) server.Kill(entireProcessTree: true);
            }
        }
        catch { }
        server.Dispose();
    }

    private void SetStatus(LocalAiState state, string message, long received = 0, long total = 0)
    {
        var previous = Status;
        Status = new LocalAiStatus(state, message, received, total);
        var now = DateTime.UtcNow;
        if (previous.State == state && previous.Message == message &&
            received < total && now - _lastStatusNotificationUtc < TimeSpan.FromMilliseconds(150)) return;
        _lastStatusNotificationUtc = now;
        StatusChanged?.Invoke(this, Status);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        _idleTimer.Dispose();
        // StopIfIdle 回调可能正阻塞在信号量上：先等其释放，再释放信号量本身。
        try { _lifecycleGate.Wait(); }
        catch (ObjectDisposedException) { }
        try { StopServer(); }
        finally
        {
            try { _lifecycleGate.Release(); }
            catch (ObjectDisposedException) { }
        }
        _scheduler.Dispose();
        _shutdown.Dispose();
        _lifecycleGate.Dispose();
    }
}

internal sealed class LocalAiSession(Uri baseUri, Action dispose) : IDisposable
{
    private Action? _dispose = dispose;
    public Uri BaseUri { get; } = baseUri;
    public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
}
