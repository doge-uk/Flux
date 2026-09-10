using System.Net.Http.Headers;
using System.Reflection;
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

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    Version CurrentVersion,
    Version? LatestVersion = null,
    Uri? ReleaseUri = null,
    string? Message = null);

public sealed class GitHubReleaseUpdateService : IDisposable
{
    private const string RepositoryMetadataKey = "FluxUpdateRepository";
    private readonly HttpClient _httpClient;
    private readonly ILogService _log;
    private readonly Version _currentVersion;
    private readonly string? _repository;

    public GitHubReleaseUpdateService(ILogService log)
    {
        _log = log;
        _currentVersion = ReleaseVersion.Normalize(
            Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0, 0));
        _repository = ResolveRepository();
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Flux", $"{_currentVersion.Major}.{_currentVersion.Minor}.{_currentVersion.Build}"));
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

        try
        {
            var segments = _repository.Split('/');
            var endpoint = $"https://api.github.com/repos/{Uri.EscapeDataString(segments[0])}/{Uri.EscapeDataString(segments[1])}/releases/latest";
            using var response = await _httpClient.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var message = $"GitHub returned {(int)response.StatusCode} ({response.ReasonPhrase}).";
                _log.Info($"Update check did not complete: {message}");
                return new UpdateCheckResult(UpdateCheckStatus.Failed, _currentVersion, Message: message);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (release is null || release.Draft || release.Prerelease ||
                !ReleaseVersion.TryParseTag(release.TagName, out var latestVersion))
            {
                const string message = "The latest GitHub release does not contain a valid stable version tag.";
                _log.Info($"Update check did not complete: {message}");
                return new UpdateCheckResult(UpdateCheckStatus.Failed, _currentVersion, Message: message);
            }

            if (!TryCreateSafeReleaseUri(release.HtmlUrl, out var releaseUri))
            {
                const string message = "GitHub returned an invalid release page URL.";
                _log.Info($"Update check did not complete: {message}");
                return new UpdateCheckResult(UpdateCheckStatus.Failed, _currentVersion, Message: message);
            }

            var status = ReleaseVersion.Compare(latestVersion, _currentVersion) > 0
                ? UpdateCheckStatus.UpdateAvailable
                : UpdateCheckStatus.UpToDate;
            _log.Info($"Update check completed. Current={_currentVersion}; Latest={latestVersion}; Status={status}.");
            return new UpdateCheckResult(status, _currentVersion, latestVersion, releaseUri);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            const string message = "The update check timed out.";
            _log.Info(message);
            return new UpdateCheckResult(UpdateCheckStatus.Failed, _currentVersion, Message: message);
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

    public void Dispose() => _httpClient.Dispose();

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

    private static bool TryCreateSafeReleaseUri(string? value, out Uri? releaseUri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps &&
            uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            releaseUri = uri;
            return true;
        }

        releaseUri = null;
        return false;
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
    }
}
