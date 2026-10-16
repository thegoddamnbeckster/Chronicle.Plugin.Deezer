using System.Net;
using System.Text.Json;
using Serilog;

namespace Chronicle.Plugin.Deezer;

/// <summary>
/// HTTP wrapper for Deezer's public API (https://api.deezer.com). No key or account is needed for the read endpoints used here.
///
/// Rate limit: Deezer documents 50 requests per 5 seconds per IP. Requests are paced at least <see cref="Interval"/> apart (about
/// 6 a second; measured throughput is lower, because each answer takes a few hundred milliseconds). Chronicle gives a plugin 25
/// seconds per call, so a quota answer is never waited out beyond <see cref="MaxRetryWait"/>: it is thrown as an HTTP 429, which
/// Chronicle reads as "the provider is rate limited" and answers by leaving the item Pending instead of marking it not found.
///
/// Deezer reports many problems as HTTP 200 with a JSON body {"error":{"code":N}}: 4 is the quota, 700 is "service busy" (both
/// become a 429/503 exception), 800 is "no data" (a normal "not found", returned as null), 600 (or 500, "wrong parameter") is a query Deezer could not parse
/// (logged and treated as "not found": the plugin sent something Deezer disliked, which must not fail the whole pass), and
/// anything else is an error that propagates. An unchecked 200 would have read as "found, nothing in it".
///
/// Searches use plain text only: Deezer's field syntax (artist:"X" track:"Y") returned nothing for every sampled track.
/// </summary>
internal sealed class DeezerClient : IDisposable
{
    public const string BaseUrl = "https://api.deezer.com";
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan MaxRetryWait = TimeSpan.FromSeconds(8);

    public const int CodeQuota = 4;
    public const int CodeServiceBusy = 700;
    public const int CodeBadQuery = 600;
    /// <summary>Deezer's ParameterException ("Wrong parameter", "empty parameter"): a query it cannot use, such as one that is nothing but a symbol.</summary>
    public const int CodeBadParameter = 500;
    public const int CodeNoData = 800;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly ILogger _log;
    private readonly TimeSpan _interval;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _last = DateTimeOffset.MinValue;

    /// <summary>How many requests have been sent (the tests check each call's budget against this).</summary>
    public int Requests { get; private set; }

    public DeezerClient(HttpClient http, TimeSpan? interval = null, ILogger? log = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _delay = delay ?? Task.Delay;
        _interval = interval ?? Interval;
        _log = log ?? Serilog.Core.Logger.None;
    }

    public static string UserAgent(string version) => $"Chronicle-Deezer/{version}";

    // ── searches ──────────────────────────────────────────────────────────────

    public async Task<List<AlbumDoc>> SearchAlbumsAsync(string query, int limit, CancellationToken ct) =>
        (await GetAsync<ListResponse<AlbumDoc>>($"/search/album?q={Uri.EscapeDataString(query)}&limit={limit}", ct).ConfigureAwait(false))?.Data ?? [];

    public async Task<List<TrackDoc>> SearchTracksAsync(string query, int limit, CancellationToken ct) =>
        (await GetAsync<ListResponse<TrackDoc>>($"/search/track?q={Uri.EscapeDataString(query)}&limit={limit}", ct).ConfigureAwait(false))?.Data ?? [];

    public async Task<List<ArtistDoc>> SearchArtistsAsync(string query, int limit, CancellationToken ct) =>
        (await GetAsync<ListResponse<ArtistDoc>>($"/search/artist?q={Uri.EscapeDataString(query)}&limit={limit}", ct).ConfigureAwait(false))?.Data ?? [];

    /// <summary>The titles of an artist's albums (used to tell same-named artists apart by what the library holds).</summary>
    public async Task<List<AlbumDoc>> ArtistAlbumsAsync(long artistId, int limit, CancellationToken ct) =>
        (await GetAsync<ListResponse<AlbumDoc>>($"/artist/{artistId}/albums?limit={limit}", ct).ConfigureAwait(false))?.Data ?? [];

    // ── records ───────────────────────────────────────────────────────────────

    public Task<AlbumDoc?> GetAlbumAsync(long id, CancellationToken ct) => GetAsync<AlbumDoc>($"/album/{id}", ct);

    public Task<TrackDoc?> GetTrackAsync(long id, CancellationToken ct) => GetAsync<TrackDoc>($"/track/{id}", ct);

    public Task<ArtistDoc?> GetArtistAsync(long id, CancellationToken ct) => GetAsync<ArtistDoc>($"/artist/{id}", ct);

    public Task<TrackDoc?> GetTrackByIsrcAsync(string isrc, CancellationToken ct) => GetAsync<TrackDoc>($"/track/isrc:{Uri.EscapeDataString(isrc)}", ct);

    public Task<AlbumDoc?> GetAlbumByUpcAsync(string upc, CancellationToken ct) => GetAsync<AlbumDoc>($"/album/upc:{Uri.EscapeDataString(upc)}", ct);

    // ── images ────────────────────────────────────────────────────────────────

    /// <summary>Downloads one image. Only called when the application asks for the picture; never paced, never in bulk.</summary>
    public Task<byte[]> GetImageBytesAsync(string url, CancellationToken ct) => _http.GetByteArrayAsync(url, ct);

    // ── health ────────────────────────────────────────────────────────────────

    public async Task<bool> HealthCheckAsync(CancellationToken ct)
    {
        try
        {
            // Daft Punk: a stable, small record.
            var artist = await GetArtistAsync(27, ct).ConfigureAwait(false);
            return artist is { Id: > 0 };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning(ex, "Deezer health check failed");
            return false;
        }
    }

    // ── transport ─────────────────────────────────────────────────────────────

    private async Task<T?> GetAsync<T>(string pathAndQuery, CancellationToken ct) where T : class
    {
        for (var attempt = 0; ; attempt++)
        {
            using var response = await SendAsync(pathAndQuery, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound) return null;

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var wait = RetryAfter(response);
                if (attempt == 0 && wait <= MaxRetryWait)
                {
                    _log.Warning("Deezer: rate limited; waiting {Seconds}s before one retry", wait.TotalSeconds);
                    await _delay(wait, ct).ConfigureAwait(false);
                    continue;
                }
                throw RateLimited(wait);
            }

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Deezer answered {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (ReadError(body) is { } error)
            {
                switch (error.Code)
                {
                    case CodeQuota:
                        if (attempt == 0 && MaxRetryWait >= TimeSpan.FromSeconds(5))
                        {
                            // Deezer's quota window is 5 seconds: one short wait usually clears it.
                            _log.Warning("Deezer: quota reached; waiting 5s before one retry");
                            await _delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                            continue;
                        }
                        throw RateLimited(TimeSpan.FromSeconds(5));
                    case CodeServiceBusy:
                        throw new HttpRequestException("Deezer says its service is busy", null, HttpStatusCode.ServiceUnavailable);
                    case CodeNoData:
                        return null;
                    case CodeBadQuery:
                    case CodeBadParameter:
                        _log.Warning("Deezer could not parse the query for {Path}: {Message}", pathAndQuery, error.Message);
                        return null;
                    default:
                        throw new HttpRequestException($"Deezer reported error {error.Code}: {error.Message}", null, response.StatusCode);
                }
            }

            try
            {
                return JsonSerializer.Deserialize<T>(body, Json);
            }
            catch (JsonException ex)
            {
                throw new HttpRequestException("Deezer returned an answer this plugin could not read", ex, response.StatusCode);
            }
        }
    }

    private static HttpRequestException RateLimited(TimeSpan wait) =>
        new($"Deezer is rate limiting requests (retry in about {wait.TotalSeconds:0}s)", null, HttpStatusCode.TooManyRequests);

    /// <summary>The error object of an answer, or null when the answer is not an error.</summary>
    internal static ErrorBody? ReadError(string body)
    {
        // Every real answer is a JSON object; only look for "error" when the text says so, to avoid parsing large answers twice.
        if (!body.Contains("\"error\"", StringComparison.Ordinal)) return null;
        try
        {
            return JsonSerializer.Deserialize<ErrorEnvelope>(body, Json)?.Error;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TimeSpan RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta;
        if (header?.Date is { } date)
        {
            var until = date - DateTimeOffset.UtcNow;
            return until > TimeSpan.Zero ? until : TimeSpan.Zero;
        }
        return TimeSpan.FromSeconds(30);
    }

    private async Task<HttpResponseMessage> SendAsync(string pathAndQuery, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = _interval - (DateTimeOffset.UtcNow - _last);
            if (wait > TimeSpan.Zero) await _delay(wait, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + pathAndQuery);
            request.Headers.Accept.ParseAdd("application/json");
            Requests++;
            var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            _last = DateTimeOffset.UtcNow;
            return response;
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => _gate.Dispose();
}
