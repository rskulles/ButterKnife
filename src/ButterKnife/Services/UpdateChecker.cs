using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace ButterKnife.Services;

/// <summary>The outcome of a release check: the running version, the newest published one and where to get it.</summary>
public sealed record UpdateCheck(Version Current, Version? Latest, string? Url, string? Error)
{
    public bool IsNewer => Latest is not null && UpdateChecker.IsNewer(Current, Latest);
}

/// <summary>
/// Asks GitHub for the latest release and compares it with the running build. Only called when Settings → General
/// opens (nothing runs in the background), and the answer is cached for a few hours so repeated visits do not hit
/// the API. Any failure is reported in the result rather than thrown.
/// </summary>
public sealed class UpdateChecker(IHttpClientFactory httpClientFactory, ILogger<UpdateChecker> logger)
{
    public const string HttpClientName = "github";

    public const string LatestReleaseUrl = "https://api.github.com/repos/rskulles/ButterKnife/releases/latest";

    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(6);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateCheck? _cached;
    private DateTimeOffset _cachedAt;

    /// <summary>The version this build was published as (`-p:Version=` in the release workflow); 1.0.0 for plain `dotnet run`.</summary>
    public static Version CurrentVersion { get; } = ReadCurrentVersion();

    public async Task<UpdateCheck> CheckAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cached is { Error: null } && DateTimeOffset.UtcNow - _cachedAt < CacheFor)
            {
                return _cached;
            }

            var result = await FetchAsync(cancellationToken);
            if (result.Error is null)
            {
                _cached = result;
                _cachedAt = DateTimeOffset.UtcNow;
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<UpdateCheck> FetchAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ButterKnife", CurrentVersion.ToString()));

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheck(CurrentVersion, null, null, $"GitHub answered {(int)response.StatusCode} {response.StatusCode}.");
            }

            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var tag = json.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            var url = json.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() : null;
            if (ParseTag(tag) is not { } latest)
            {
                return new UpdateCheck(CurrentVersion, null, null, $"Could not read the release tag \"{tag}\".");
            }
            return new UpdateCheck(CurrentVersion, latest, url, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Release check failed");
            return new UpdateCheck(CurrentVersion, null, null, ex is TaskCanceledException ? "GitHub did not answer in time." : ex.Message);
        }
    }

    /// <summary>"v0.3.0" or "0.3.0" → 0.3.0; anything else → null.</summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }
        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }
        return Version.TryParse(text, out var version) ? Normalize(version) : null;
    }

    /// <summary>Compares on major.minor.build with missing parts as 0, so 0.2 and 0.2.0 are the same version.</summary>
    public static bool IsNewer(Version current, Version candidate) => Normalize(candidate) > Normalize(current);

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    private static Version ReadCurrentVersion()
    {
        var assembly = typeof(UpdateChecker).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var text = informational?.Split('+', 2)[0]; // the SDK appends "+<commit>" to the informational version
        if (Version.TryParse(text, out var version))
        {
            return Normalize(version);
        }
        return Normalize(assembly.GetName().Version ?? new Version(0, 0, 0));
    }
}
