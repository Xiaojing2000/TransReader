using System.IO.Compression;
using System.Text.Json;
using TransReader.Core.Ocr;

namespace TransReader.App.Services;

internal enum OcrComponentState
{
    Disabled,
    NotInstalled,
    Installing,
    Installed,
    Starting,
    Ready,
    Error
}

internal sealed record OcrComponentStatus(
    OcrComponentState State,
    string Message,
    long BytesReceived = 0,
    long TotalBytes = 0)
{
    public double Progress => TotalBytes <= 0 ? 0 : Math.Clamp(BytesReceived / (double)TotalBytes, 0, 1);
}

internal sealed class OcrComponentUnavailableException(string message) : InvalidOperationException(message);

internal sealed record OcrPayloadFile(string Path, long Size, string Sha256);
internal sealed record OcrPayloadManifest(int SchemaVersion, string ComponentVersion, string Architecture, List<OcrPayloadFile> Files);

/// <summary>Installs and verifies the optional PaddleOCR runtime under LocalAppData.</summary>
internal sealed class OcrComponentManager : IDisposable
{
    public const string ComponentVersion = "paddleocr-ppocrv5-mobile-cpu-v2";
    public const string PackageFileName = "TransReader-OCR-PP-OCRv5-mobile-win-x64.zip";
    public const long PackageSize = 117_642_621;
    public const string PackageSha256 = "65a2d6f910395688fe51816159817af48088de4485d191fc8774536906bae4dd";

    private static readonly IReadOnlyList<string> PackageUrls =
    [
        $"https://github.com/Xiaojing2000/TransReader/releases/download/v0.3.2/{PackageFileName}",
        $"https://ghproxy.net/https://github.com/Xiaojing2000/TransReader/releases/download/v0.3.2/{PackageFileName}"
    ];

    private readonly string _root;
    private readonly string _versionDirectory;
    private readonly string _downloadPath;
    private readonly string _bootstrapDirectory;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private bool _enabled;
    private bool _disposed;
    private string _lastSuccessfulSource = string.Empty;

    public OcrComponentManager(string root, string? bootstrapDirectory = null)
    {
        _root = Path.GetFullPath(root);
        _versionDirectory = Path.Combine(_root, "versions", ComponentVersion);
        _downloadPath = Path.Combine(_root, "downloads", PackageFileName);
        _bootstrapDirectory = Path.GetFullPath(bootstrapDirectory ?? AppContext.BaseDirectory);
        Status = new OcrComponentStatus(OcrComponentState.Disabled, "OCR 已关闭");
    }

    public event EventHandler<OcrComponentStatus>? StatusChanged;
    public event Action? ComponentChanging;
    public OcrComponentStatus Status { get; private set; }
    public bool IsEnabled => _enabled;
    public string LastSuccessfulSource => _lastSuccessfulSource;
    public bool IsInstalled => RequiredInstalledFiles().All(File.Exists);
    public long InstalledSize => DirectorySize(_versionDirectory);
    public OcrRuntimePaths RuntimePaths => new(
        Path.Combine(_versionDirectory, "TransOcrNative.Host.exe"),
        Path.Combine(_versionDirectory, "models"),
        Path.Combine(_versionDirectory, "OCR.yaml"));

    public void SetEnabled(bool enabled)
    {
        ThrowIfDisposed();
        if (_enabled == enabled) return;
        _enabled = enabled;
        if (!enabled)
        {
            ComponentChanging?.Invoke();
            SetStatus(OcrComponentState.Disabled, "OCR 已关闭");
        }
        else
        {
            SetStatus(IsInstalled ? OcrComponentState.Installed : OcrComponentState.NotInstalled,
                IsInstalled ? "OCR 组件已安装" : "OCR 组件尚未安装");
        }
    }

    public async Task InstallOrRepairAsync(CancellationToken cancellationToken = default) =>
        await InstallCoreAsync(forceDownload: false, importPath: null, cancellationToken).ConfigureAwait(false);

    public async Task ForceReinstallAsync(CancellationToken cancellationToken = default) =>
        await InstallCoreAsync(forceDownload: true, importPath: null, cancellationToken).ConfigureAwait(false);

    public async Task ImportAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        packagePath = Path.GetFullPath(packagePath);
        if (!await VerifiedDownloader.VerifyFileAsync(packagePath, PackageSize, PackageSha256, cancellationToken)
                .ConfigureAwait(false))
            throw new InvalidDataException("离线 OCR 组件的版本、大小或 SHA-256 不匹配。");
        _lastSuccessfulSource = "本地离线包";
        await InstallCoreAsync(forceDownload: false, importPath: packagePath, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> VerifyAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            SetStatus(OcrComponentState.Installing, "正在校验 OCR 组件…");
            var valid = await VerifyDirectoryAsync(_versionDirectory, cancellationToken).ConfigureAwait(false);
            SetStatus(valid
                    ? (_enabled ? OcrComponentState.Installed : OcrComponentState.Disabled)
                    : OcrComponentState.Error,
                valid ? (_enabled ? "OCR 组件校验通过" : "OCR 已关闭，组件校验通过") : "OCR 组件损坏，请执行安装 / 修复");
            return valid;
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task<bool> TryMigrateLegacyAsync(CancellationToken cancellationToken = default)
    {
        if (IsInstalled)
        {
            AppLog.Info("[OCR 组件] 已存在版本化组件，无需迁移旧版 OCR");
            return true;
        }
        var manifest = await LoadTrustedManifestAsync(cancellationToken).ConfigureAwait(false);
        var missingLegacyFiles = manifest.Files
            .Where(file => !file.Path.Equals("THIRD_PARTY_NOTICES.md", StringComparison.OrdinalIgnoreCase))
            .Where(file => !File.Exists(Path.Combine(
                _bootstrapDirectory, file.Path.Replace('/', Path.DirectorySeparatorChar))))
            .Select(file => file.Path)
            .ToArray();
        if (missingLegacyFiles.Length > 0)
        {
            AppLog.Info($"[OCR 组件] 未发现可迁移的完整旧版 OCR（目录：{_bootstrapDirectory}；缺少：{string.Join(", ", missingLegacyFiles)}）");
            return false;
        }

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ComponentChanging?.Invoke();
            SetStatus(OcrComponentState.Installing, "正在迁移已有 OCR 组件…");
            var temporary = NewTemporaryDirectory();
            Directory.CreateDirectory(temporary);
            try
            {
                foreach (var file in manifest.Files)
                {
                    var source = file.Path.Equals("THIRD_PARTY_NOTICES.md", StringComparison.OrdinalIgnoreCase)
                        ? Path.Combine(_bootstrapDirectory, "THIRD_PARTY_NOTICES.md")
                        : Path.Combine(_bootstrapDirectory, file.Path.Replace('/', Path.DirectorySeparatorChar));
                    var destination = SafeDestination(temporary, file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(source, destination, true);
                }
                CopyBootstrap(temporary);
                var invalidFiles = await GetInvalidPayloadFilesAsync(temporary, cancellationToken).ConfigureAwait(false);
                if (invalidFiles.Count > 0)
                {
                    SetStatus(OcrComponentState.Error,
                        $"旧版 OCR 迁移校验失败：{string.Join(", ", invalidFiles)}；可使用“安装 / 修复”恢复");
                    return false;
                }
                ActivateDirectory(temporary);
                foreach (var file in manifest.Files.Where(file => !file.Path.Equals("THIRD_PARTY_NOTICES.md", StringComparison.OrdinalIgnoreCase)))
                {
                    var legacy = Path.Combine(_bootstrapDirectory, file.Path.Replace('/', Path.DirectorySeparatorChar));
                    try { if (File.Exists(legacy)) File.Delete(legacy); } catch (IOException) { }
                }
                DeleteEmptyLegacyModels();
                _enabled = true;
                SetStatus(OcrComponentState.Installed, "已迁移并校验旧版 OCR 组件");
                return true;
            }
            finally { TryDeleteDirectory(temporary); }
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task UninstallAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ComponentChanging?.Invoke();
            if (Directory.Exists(_versionDirectory)) Directory.Delete(_versionDirectory, true);
            _enabled = false;
            SetStatus(OcrComponentState.Disabled, "OCR 组件已卸载");
        }
        finally { _lifecycleGate.Release(); }
    }

    internal void MarkStarting() => SetStatus(OcrComponentState.Starting, "正在加载 PaddleOCR…");
    internal void MarkReady() => SetStatus(OcrComponentState.Ready, "PaddleOCR 已就绪");
    internal void MarkError(Exception exception) => SetStatus(OcrComponentState.Error, exception.Message);

    private async Task InstallCoreAsync(bool forceDownload, string? importPath, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        cancellationToken = linked.Token;
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ComponentChanging?.Invoke();
            if (!forceDownload && importPath is null &&
                await VerifyDirectoryAsync(_versionDirectory, cancellationToken).ConfigureAwait(false))
            {
                CopyBootstrap(_versionDirectory);
                _enabled = true;
                SetStatus(OcrComponentState.Installed, "OCR 组件已完整，无需重新下载");
                return;
            }

            string packagePath;
            if (importPath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_downloadPath)!);
                if (!Path.GetFullPath(importPath).Equals(Path.GetFullPath(_downloadPath), StringComparison.OrdinalIgnoreCase))
                    File.Copy(importPath, _downloadPath, true);
                packagePath = _downloadPath;
                SetStatus(OcrComponentState.Installing, "正在导入离线 OCR 组件…");
            }
            else
            {
                EnsureFreeSpace(PackageSize * 2 + 64L * 1024 * 1024);
                SetStatus(OcrComponentState.Installing, "正在下载 OCR 组件…", 0, PackageSize);
                var download = await VerifiedDownloader.DownloadAsync(
                    PackageUrls, _downloadPath, PackageSize, PackageSha256,
                    value => SetStatus(OcrComponentState.Installing,
                        $"正在从 {value.Host} 下载 OCR 组件…", value.BytesReceived, value.TotalBytes),
                    forceDownload, cancellationToken).ConfigureAwait(false);
                _lastSuccessfulSource = download.Host;
                packagePath = _downloadPath;
            }

            SetStatus(OcrComponentState.Installing, "正在安装并校验 OCR 组件…");
            var temporary = NewTemporaryDirectory();
            Directory.CreateDirectory(temporary);
            try
            {
                await ExtractSafelyAsync(packagePath, temporary, cancellationToken).ConfigureAwait(false);
                CopyBootstrap(temporary);
                if (!await VerifyDirectoryAsync(temporary, cancellationToken).ConfigureAwait(false))
                    throw new InvalidDataException("OCR 组件解压后的文件校验失败。");
                ActivateDirectory(temporary);
            }
            finally { TryDeleteDirectory(temporary); }

            _enabled = true;
            SetStatus(OcrComponentState.Installed, string.IsNullOrEmpty(_lastSuccessfulSource)
                ? "OCR 组件安装完成"
                : $"OCR 组件安装完成（来源：{_lastSuccessfulSource}）");
        }
        catch (OperationCanceledException)
        {
            SetStatus(IsInstalled ? OcrComponentState.Installed : OcrComponentState.NotInstalled, "OCR 安装已取消");
            throw;
        }
        catch (Exception ex)
        {
            SetStatus(OcrComponentState.Error, $"OCR 组件安装失败：{ex.Message}");
            throw;
        }
        finally { _lifecycleGate.Release(); }
    }

    private async Task<bool> VerifyDirectoryAsync(string directory, CancellationToken cancellationToken)
    {
        var invalidFiles = await GetInvalidPayloadFilesAsync(directory, cancellationToken).ConfigureAwait(false);
        return invalidFiles.Count == 0;
    }

    private async Task<IReadOnlyList<string>> GetInvalidPayloadFilesAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory)) return ["组件目录不存在"];
        OcrPayloadManifest manifest;
        try { manifest = await LoadTrustedManifestAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { return [$"组件清单无效：{ex.Message}"]; }
        var invalid = new List<string>();
        foreach (var file in manifest.Files)
        {
            var path = SafeDestination(directory, file.Path);
            if (!await VerifiedDownloader.VerifyFileAsync(path, file.Size, file.Sha256, cancellationToken).ConfigureAwait(false))
                invalid.Add(file.Path);
        }
        foreach (var file in new[] { "TransOcrNative.Host.exe", "TransOcrNative.dll", "OCR.yaml", "OcrPayloadManifest.json" })
        {
            if (!File.Exists(Path.Combine(directory, file))) invalid.Add(file);
        }
        return invalid;
    }

    private async Task<OcrPayloadManifest> LoadTrustedManifestAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_bootstrapDirectory, "OcrPayloadManifest.json");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var manifest = await JsonSerializer.DeserializeAsync<OcrPayloadManifest>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("OCR 组件清单无效。");
        if (manifest.SchemaVersion != 1 || manifest.ComponentVersion != ComponentVersion || manifest.Architecture != "win-x64")
            throw new InvalidDataException("OCR 组件清单版本不匹配。");
        return manifest;
    }

    private void CopyBootstrap(string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var name in new[] { "TransOcrNative.Host.exe", "TransOcrNative.dll", "OCR.yaml", "OcrPayloadManifest.json" })
        {
            var source = Path.Combine(_bootstrapDirectory, name);
            if (!File.Exists(source)) throw new FileNotFoundException($"缺少 OCR 启动文件：{name}", source);
            File.Copy(source, Path.Combine(destination, name), true);
        }
    }

    private static async Task ExtractSafelyAsync(string packagePath, string destination, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var path = SafeDestination(destination, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var input = entry.Open();
            await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }
    }

    private void ActivateDirectory(string temporary)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_versionDirectory)!);
        var backup = _versionDirectory + ".previous";
        TryDeleteDirectory(backup);
        if (Directory.Exists(_versionDirectory)) Directory.Move(_versionDirectory, backup);
        try
        {
            Directory.Move(temporary, _versionDirectory);
            TryDeleteDirectory(backup);
        }
        catch
        {
            if (!Directory.Exists(_versionDirectory) && Directory.Exists(backup)) Directory.Move(backup, _versionDirectory);
            throw;
        }
    }

    private string NewTemporaryDirectory() =>
        Path.Combine(_root, "versions", $"{ComponentVersion}.{Guid.NewGuid():N}.installing");

    private static string SafeDestination(string root, string relative)
    {
        var rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var destination = Path.GetFullPath(Path.Combine(rootPath, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!destination.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("OCR 组件包含不安全的文件路径。");
        return destination;
    }

    private IEnumerable<string> RequiredInstalledFiles()
    {
        yield return Path.Combine(_versionDirectory, "TransOcrNative.Host.exe");
        yield return Path.Combine(_versionDirectory, "TransOcrNative.dll");
        yield return Path.Combine(_versionDirectory, "OCR.yaml");
        yield return Path.Combine(_versionDirectory, "paddle_inference.dll");
        yield return Path.Combine(_versionDirectory, "models", "PP-OCRv5_mobile_det_infer", "inference.pdiparams");
        yield return Path.Combine(_versionDirectory, "models", "PP-OCRv5_mobile_rec_infer", "inference.pdiparams");
    }

    private void DeleteEmptyLegacyModels()
    {
        foreach (var name in new[] { "PP-OCRv5_mobile_det_infer", "PP-OCRv5_mobile_rec_infer" })
        {
            var path = Path.Combine(_bootstrapDirectory, "models", name);
            TryDeleteDirectory(path);
        }
        var models = Path.Combine(_bootstrapDirectory, "models");
        try { if (Directory.Exists(models) && !Directory.EnumerateFileSystemEntries(models).Any()) Directory.Delete(models); } catch { }
    }

    private static long DirectorySize(string path)
    {
        try { return Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length) : 0; }
        catch { return 0; }
    }

    private void EnsureFreeSpace(long requiredBytes)
    {
        var root = Path.GetPathRoot(_root) ?? throw new IOException("无法确定 OCR 组件所在磁盘。");
        var available = new DriveInfo(root).AvailableFreeSpace;
        if (available < requiredBytes)
            throw new IOException($"磁盘空间不足：至少需要 {requiredBytes / 1048576d:0} MB，当前可用 {available / 1048576d:0} MB。");
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (IOException) { }
    }

    private void SetStatus(OcrComponentState state, string message, long received = 0, long total = 0)
    {
        var next = new OcrComponentStatus(state, message, received, total);
        if (Status == next) return;
        Status = next;
        StatusChanged?.Invoke(this, Status);
        AppLog.Info($"[OCR 组件] {message}");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        _shutdown.Dispose();
        _lifecycleGate.Dispose();
    }
}
