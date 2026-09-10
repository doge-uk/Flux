using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flux.Core;

namespace Flux.Windows.Updates;

public enum UpdateCheckStatus
{
    NotConfigured,
    UpToDate,
    UpdateAvailable,
    Failed
}

public sealed record UpdatePackage(
    Version Version,
    Uri DownloadUri,
    Uri ReleaseUri,
    string FileName,
    string Sha256,
    long SizeBytes);

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    Version CurrentVersion,
    Version? LatestVersion = null,
    Uri? ReleaseUri = null,
    UpdatePackage? Package = null,
    string? Message = null);

public sealed class GitHubReleaseUpdateService : IDisposable
{
    private const string RepositoryMetadataKey = "FluxUpdateRepository";
    private const long MaximumInstallerBytes = 300L * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly ILogService _log;
    private readonly Version _currentVersion;
    private readonly string? _repository;
    private readonly string _updateDirectory;

    public GitHubReleaseUpdateService(ILogService log)
    {
        _log = log;
        _currentVersion = ReleaseVersion.Normalize(
            Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0, 0));
        _repository = ResolveRepository();
        _updateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Flux",
            "Updates");
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Flux", FormatVersion(_currentVersion)));
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (_repository is null)
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.NotConfigured,
                _currentVersion,
                Message: "No GitHub release repository is configured for this build.");
        }

        using var checkCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        checkCancellation.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var segments = _repository.Split('/');
            var endpoint = $"https://api.github.com/repos/{Uri.EscapeDataString(segments[0])}/{Uri.EscapeDataString(segments[1])}/releases/latest";
            using var response = await _httpClient.GetAsync(endpoint, checkCancellation.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Failed($"GitHub returned {(int)response.StatusCode} ({response.ReasonPhrase}).");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(checkCancellation.Token).ConfigureAwait(false);
            var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(
                    stream,
                    cancellationToken: checkCancellation.Token)
                .ConfigureAwait(false);

            if (release is null || release.Draft || release.Prerelease ||
                !ReleaseVersion.TryParseTag(release.TagName, out var latestVersion))
            {
                return Failed("The latest GitHub release does not contain a valid stable version tag.");
            }

            if (!TryCreateSafeGitHubUri(release.HtmlUrl, out var releaseUri))
            {
                return Failed("GitHub returned an invalid release page URL.");
            }

            if (ReleaseVersion.Compare(latestVersion, _currentVersion) <= 0)
            {
                _log.Info($"Update check completed. Current={_currentVersion}; Latest={latestVersion}; Status=UpToDate.");
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate, _currentVersion, latestVersion, releaseUri);
            }

            var expectedName = $"Flux-{FormatVersion(latestVersion)}-win-x64-setup.exe";
            var asset = release.Assets.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, expectedName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.State, "uploaded", StringComparison.OrdinalIgnoreCase));
            if (asset is null || asset.Size is <= 0 or > MaximumInstallerBytes ||
                !TryCreateSafeGitHubUri(asset.DownloadUrl, out var downloadUri) ||
                !UpdateIntegrity.TryParseGitHubDigest(asset.Digest, out var sha256))
            {
                return Failed($"Flux {FormatVersion(latestVersion)} does not have a verifiable Windows installer yet.");
            }

            var package = new UpdatePackage(
                latestVersion,
                downloadUri,
                releaseUri,
                expectedName,
                sha256,
                asset.Size);
            _log.Info($"Update check completed. Current={_currentVersion}; Latest={latestVersion}; Status=UpdateAvailable.");
            return new UpdateCheckResult(
                UpdateCheckStatus.UpdateAvailable,
                _currentVersion,
                latestVersion,
                releaseUri,
                package);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed("The update check timed out.");
        }
        catch (Exception exception)
        {
            _log.Error("Update check failed.", exception);
            return new UpdateCheckResult(
                UpdateCheckStatus.Failed,
                _currentVersion,
                Message: "Flux could not reach GitHub Releases.");
        }
    }

    public async Task<string> DownloadInstallerAsync(
        UpdatePackage package,
        CancellationToken cancellationToken = default)
    {
        ValidatePackage(package);
        Directory.CreateDirectory(_updateDirectory);
        var destination = Path.Combine(_updateDirectory, package.FileName);
        if (File.Exists(destination) && await UpdateIntegrity.VerifyFileAsync(destination, package.Sha256, cancellationToken))
        {
            return destination;
        }

        var partial = destination + ".download";
        try
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }

            using var response = await _httpClient.GetAsync(
                    package.DownloadUri,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength.HasValue &&
                (contentLength.Value is <= 0 or > MaximumInstallerBytes ||
                 contentLength.Value != package.SizeBytes))
            {
                throw new InvalidOperationException("The installer size did not match the signed release metadata.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var destinationStream = new FileStream(
                partial,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaximumInstallerBytes || total > package.SizeBytes)
                {
                    throw new InvalidOperationException("The installer download exceeded the expected size.");
                }

                hash.AppendData(buffer, 0, read);
                await destinationStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            await destinationStream.FlushAsync(cancellationToken).ConfigureAwait(false);

            var actualHash = Convert.ToHexString(hash.GetHashAndReset());
            if (total != package.SizeBytes || !actualHash.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The installer failed SHA-256 verification and was not opened.");
            }

            File.Move(partial, destination, true);
            _log.Info($"Verified update installer {package.FileName} ({total} bytes).");
            return destination;
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    public async Task<Process> LaunchInstallerAsync(
        UpdatePackage package,
        string installerPath,
        CancellationToken cancellationToken = default)
    {
        ValidatePackage(package);
        var fullPath = Path.GetFullPath(installerPath);
        var safeRoot = Path.GetFullPath(_updateDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(fullPath), package.FileName, StringComparison.OrdinalIgnoreCase) ||
            !await UpdateIntegrity.VerifyFileAsync(fullPath, package.Sha256, cancellationToken))
        {
            throw new InvalidOperationException("The update installer could not be verified before launch.");
        }

        var startInfo = new ProcessStartInfo(fullPath) { UseShellExecute = true };
        startInfo.ArgumentList.Add("/SILENT");
        startInfo.ArgumentList.Add("/SUPPRESSMSGBOXES");
        startInfo.ArgumentList.Add("/NORESTART");
        startInfo.ArgumentList.Add("/CLOSEAPPLICATIONS");
        startInfo.ArgumentList.Add("/RESTARTAPPLICATIONS");
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows could not start the verified Flux installer.");
    }

    public void Dispose() => _httpClient.Dispose();

    private UpdateCheckResult Failed(string message)
    {
        _log.Info($"Update check did not complete: {message}");
        return new UpdateCheckResult(UpdateCheckStatus.Failed, _currentVersion, Message: message);
    }

    private void ValidatePackage(UpdatePackage package)
    {
        if (package.SizeBytes is <= 0 or > MaximumInstallerBytes ||
            !string.Equals(package.FileName, Path.GetFileName(package.FileName), StringComparison.Ordinal) ||
            !package.FileName.EndsWith("-win-x64-setup.exe", StringComparison.OrdinalIgnoreCase) ||
            !TryCreateSafeGitHubUri(package.DownloadUri.AbsoluteUri, out _) ||
            !UpdateIntegrity.IsSha256(package.Sha256))
        {
            throw new InvalidOperationException("The update package metadata is invalid.");
        }
    }

    private static string? ResolveRepository()
    {
        var configured = Environment.GetEnvironmentVariable("FLUX_UPDATE_REPOSITORY");
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = Assembly.GetEntryAssembly()?
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == RepositoryMetadataKey)
                ?.Value;
        }

        return TryNormalizeRepository(configured, out var repository) ? repository : null;
    }

    private static bool TryNormalizeRepository(string? value, out string repository)
    {
        repository = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim().TrimEnd('/');
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            candidate = uri.AbsolutePath.Trim('/');
        }

        if (candidate.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[..^4];
        }

        var segments = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length != 2 || segments.Any(segment => segment.Length > 100 ||
                segment.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_' and not '.')))
        {
            return false;
        }

        repository = $"{segments[0]}/{segments[1]}";
        return true;
    }

    private static bool TryCreateSafeGitHubUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var candidate) &&
            candidate.Scheme == Uri.UriSchemeHttps &&
            candidate.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            uri = candidate;
            return true;
        }

        uri = null!;
        return false;
    }

    private static string FormatVersion(Version version) =>
        $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }

        [JsonPropertyName("draft")]
        public bool Draft { get; init; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; init; }

        [JsonPropertyName("assets")]
        public IReadOnlyList<GitHubAsset> Assets { get; init; } = Array.Empty<GitHubAsset>();
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("browser_download_url")]
        public string? DownloadUrl { get; init; }

        [JsonPropertyName("digest")]
        public string? Digest { get; init; }

        [JsonPropertyName("state")]
        public string? State { get; init; }

        [JsonPropertyName("size")]
        public long Size { get; init; }
    }
}

public static class UpdateIntegrity
{
    public static bool TryParseGitHubDigest(string? value, out string sha256)
    {
        sha256 = string.Empty;
        const string prefix = "sha256:";
        if (value is null || !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var candidate = value[prefix.Length..];
        if (!IsSha256(candidate))
        {
            return false;
        }

        sha256 = candidate.ToUpperInvariant();
        return true;
    }

    public static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    public static async Task<bool> VerifyFileAsync(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path) || !IsSha256(expectedSha256))
        {
            return false;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(
            hash,
            Convert.FromHexString(expectedSha256));
    }
}
