using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TransReader.App.Services;

/// <summary>
/// Checks the project's stable GitHub releases and downloads the matching x64
/// installer only after its separately published SHA-256 digest is verified.
/// </summary>
internal sealed class UpdateService
{
    private const string ReleasesEndpoint = "https://api.github.com/repos/Xiaojing2000/TransReader/releases/latest";
    private static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(24);
    private static readonly HttpClient Client = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _updatesRoot;
    private readonly string _statePath;

    public UpdateService(string localRoot)
    {
        _updatesRoot = Path.GetFullPath(Path.Combine(localRoot, "updates"));
        _statePath = Path.GetFullPath(Path.Combine(localRoot, "update-state.json"));
    }

    public string CurrentVersionText { get; } = GetCurrentVersionText();

    public UpdateRelease? LastAvailableRelease { get; private set; }

    public bool IsAutomaticCheckDue()
    {
        try
        {
            if (!File.Exists(_statePath))
            {
                return true;
            }

            var state = JsonSerializer.Deserialize<UpdateCheckState>(File.ReadAllText(_statePath), JsonOptions);
            return state is null || DateTimeOffset.UtcNow - state.LastCheckedAtUtc >= AutomaticCheckInterval;
        }
        catch (Exception ex)
        {
            AppLog.Error("读取更新检查状态", ex);
            return true;
        }
    }

    public void MarkAutomaticCheckAttempted()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var temporaryPath = _statePath + ".tmp";
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(new UpdateCheckState(DateTimeOffset.UtcNow), JsonOptions));
            File.Move(temporaryPath, _statePath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Error("保存更新检查状态", ex);
        }
    }

    public async Task<UpdateRelease?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesEndpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("GitHub 返回了空的版本信息。");

        if (release.Draft || release.Prerelease)
        {
            LastAvailableRelease = null;
            return null;
        }

        var latestVersion = ParseVersion(release.TagName)
            ?? throw new InvalidDataException($"无法识别发布版本号：{release.TagName}");
        var currentVersion = ParseVersion(CurrentVersionText) ?? new Version(0, 0, 0);
        if (latestVersion <= currentVersion)
        {
            LastAvailableRelease = null;
            return null;
        }

        var normalizedVersion = FormatVersion(latestVersion);
        var installerName = $"TransReader-v{normalizedVersion}-win-x64-setup.exe";
        var checksumName = $"TransReader-v{normalizedVersion}-SHA256SUMS.txt";
        var installer = release.Assets.FirstOrDefault(asset =>
            string.Equals(asset.Name, installerName, StringComparison.OrdinalIgnoreCase));
        var checksum = release.Assets.FirstOrDefault(asset =>
            string.Equals(asset.Name, checksumName, StringComparison.OrdinalIgnoreCase));
        if (installer is null || checksum is null)
        {
            throw new InvalidDataException("新版本缺少 Windows x64 安装包或 SHA-256 校验文件。");
        }

        var installerUri = ValidateGitHubUri(installer.DownloadUrl, "安装包");
        var checksumUri = ValidateGitHubUri(checksum.DownloadUrl, "校验文件");
        var releaseNotesUri = ValidateGitHubUri(release.HtmlUrl, "版本说明");
        LastAvailableRelease = new UpdateRelease(
            normalizedVersion,
            installerUri,
            checksumUri,
            installer.Size,
            releaseNotesUri,
            installerName);
        return LastAvailableRelease;
    }

    public async Task<string> DownloadAndVerifyAsync(
        UpdateRelease release,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var releaseDirectory = Path.GetFullPath(Path.Combine(_updatesRoot, release.Version));
        if (!IsWithinDirectory(releaseDirectory, _updatesRoot))
        {
            throw new InvalidOperationException("更新目录无效。");
        }
        Directory.CreateDirectory(releaseDirectory);

        var installerPath = Path.Combine(releaseDirectory, release.InstallerFileName);
        var temporaryPath = installerPath + ".download";
        var expectedHash = await DownloadExpectedHashAsync(release, cancellationToken);

        if (File.Exists(installerPath) && await HasExpectedHashAsync(installerPath, expectedHash, cancellationToken))
        {
            progress?.Report(new UpdateDownloadProgress(release.InstallerSize, release.InstallerSize));
            return installerPath;
        }

        if (File.Exists(installerPath))
        {
            File.Delete(installerPath);
        }
        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }

        try
        {
            using var response = await Client.GetAsync(
                release.InstallerUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength is > 0
                ? response.Content.Headers.ContentLength
                : release.InstallerSize > 0 ? release.InstallerSize : null;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[1024 * 1024];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;
                progress?.Report(new UpdateDownloadProgress(received, totalBytes));
            }
            await destination.FlushAsync(cancellationToken);

            if (!await HasExpectedHashAsync(temporaryPath, expectedHash, cancellationToken))
            {
                throw new InvalidDataException("安装包 SHA-256 校验失败，文件可能不完整或已被篡改。");
            }

            File.Move(temporaryPath, installerPath, overwrite: true);
            return installerPath;
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
            throw;
        }
    }

    public void LaunchInstaller(string installerPath)
    {
        var fullPath = Path.GetFullPath(installerPath);
        if (!IsWithinDirectory(fullPath, _updatesRoot) ||
            !string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(fullPath))
        {
            throw new InvalidOperationException("安装包路径无效。");
        }

        _ = Process.Start(new ProcessStartInfo
        {
            FileName = fullPath,
            Arguments = "/CLOSEAPPLICATIONS",
            UseShellExecute = true
        }) ?? throw new InvalidOperationException("无法启动安装程序。");
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"TransReader/{GetCurrentVersionText()}");
        return client;
    }

    private static string GetCurrentVersionText()
    {
        var assembly = typeof(UpdateService).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var raw = string.IsNullOrWhiteSpace(informational)
            ? assembly.GetName().Version?.ToString(3) ?? "0.0.0"
            : informational;
        var metadataIndex = raw.IndexOf('+');
        return metadataIndex >= 0 ? raw[..metadataIndex] : raw;
    }

    private static Version? ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().TrimStart('v', 'V');
        var suffixIndex = normalized.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0)
        {
            normalized = normalized[..suffixIndex];
        }
        if (!Version.TryParse(normalized, out var version))
        {
            return null;
        }
        return new Version(
            Math.Max(0, version.Major),
            Math.Max(0, version.Minor),
            Math.Max(0, version.Build));
    }

    private static string FormatVersion(Version version) => $"{version.Major}.{version.Minor}.{version.Build}";

    private static Uri ValidateGitHubUri(string value, string description)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{description}下载地址不是可信的 GitHub HTTPS 地址。");
        }
        return uri;
    }

    private static bool IsWithinDirectory(string path, string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> DownloadExpectedHashAsync(
        UpdateRelease release,
        CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync(release.ChecksumUri, cancellationToken);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        foreach (var line in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 ||
                !string.Equals(parts[^1].TrimStart('*'), release.InstallerFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var hash = parts[0];
            if (hash.Length == 64 && hash.All(Uri.IsHexDigit))
            {
                return hash;
            }
        }
        throw new InvalidDataException("SHA-256 校验文件中没有当前安装包的记录。");
    }

    private static async Task<bool> HasExpectedHashAsync(
        string path,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actualHash = await SHA256.HashDataAsync(stream, cancellationToken);
        return string.Equals(Convert.ToHexString(actualHash), expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = string.Empty;

        [JsonPropertyName("assets")]
        public List<GitHubAsset> Assets { get; set; } = [];
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string DownloadUrl { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }

    private sealed record UpdateCheckState(DateTimeOffset LastCheckedAtUtc);
}

internal sealed record UpdateRelease(
    string Version,
    Uri InstallerUri,
    Uri ChecksumUri,
    long InstallerSize,
    Uri ReleaseNotesUri,
    string InstallerFileName);

internal readonly record struct UpdateDownloadProgress(long BytesReceived, long? TotalBytes)
{
    public double? Percentage => TotalBytes is > 0
        ? Math.Clamp(BytesReceived * 100d / TotalBytes.Value, 0d, 100d)
        : null;
}
