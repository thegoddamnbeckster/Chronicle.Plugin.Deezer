using System.Net;
using System.Text;
using System.Text.Json;
using Chronicle.Plugin.Deezer;

namespace Chronicle.Plugin.Deezer.Tests;

/// <summary>A stand-in for api.deezer.com that answers from real recorded responses, records every request, and can fail on demand.</summary>
internal sealed class FakeDeezer : HttpMessageHandler
{
    public static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name), Encoding.UTF8);

    private readonly List<(Func<string, bool> Match, Func<string> Body)> _routes = [];

    /// <summary>Every request, as the unescaped path and query ("/search/album?q=Jesus Jones Doubt&amp;limit=10").</summary>
    public List<string> Urls { get; } = [];
    public int Requests => Urls.Count;

    public HttpStatusCode? FailWith { get; set; }
    public int? RetryAfterSeconds { get; set; }
    public int FailTimes { get; set; } = int.MaxValue;
    /// <summary>An answer sent with HTTP 200 as if it were a record, e.g. Deezer's error body.</summary>
    public string? Answer200 { get; set; }
    public int Answer200Times { get; set; } = int.MaxValue;
    public byte[] ImageBytes { get; set; } = [1, 2, 3];

    public List<TimeSpan> Delays { get; } = [];

    public FakeDeezer Route(Func<string, bool> match, string fixture) { _routes.Add((match, () => Fixture(fixture))); return this; }
    public FakeDeezer RouteBody(Func<string, bool> match, string body) { _routes.Add((match, () => body)); return this; }

    /// <summary>A client with no pacing and no real waiting (the waits are recorded in <see cref="Delays"/>).</summary>
    public DeezerClient Client() =>
        new(new HttpClient(this), TimeSpan.Zero, delay: (span, _) => { Delays.Add(span); return Task.CompletedTask; });

    public static string EmptySearch => "{\"data\":[],\"total\":0}";
    public const string QuotaBody = "{\"error\":{\"type\":\"Exception\",\"message\":\"Quota limit exceeded\",\"code\":4}}";
    public const string BusyBody = "{\"error\":{\"type\":\"Exception\",\"message\":\"Service busy\",\"code\":700}}";
    public const string BadQueryBody = "{\"error\":{\"type\":\"ParameterException\",\"message\":\"Query invalid\",\"code\":600}}";

    /// <summary>The recorded answers for the artists, albums and tracks the tests look up.</summary>
    public static FakeDeezer Catalogue() => new FakeDeezer()
        .Route(u => u.StartsWith("/search/album?q=Jesus Jones Doubt"), "search_album_doubt.json")
        .Route(u => u.StartsWith("/search/album?q=Paramore Still Into You"), "search_album_still_into_you.json")
        .Route(u => u.StartsWith("/search/album?q=Bon Jovi It's My Life") || u.StartsWith("/search/album?q=Bon Jovi It"), "search_album_its_my_life.json")
        .Route(u => u.StartsWith("/search/album?q=Justin Timberlake TKO"), "search_album_tko.json")
        .Route(u => u.StartsWith("/search/album?q=Bela Fleck and the Flecktones Greatest Hits"), "search_album_bela_fleck.json")
        .RouteBody(u => u.StartsWith("/search/album?q=Ghost Prequelle"), EmptySearch)
        .Route(u => u.StartsWith("/search/album?q=Prequelle"), "search_album_prequelle_title_only.json")
        .Route(u => u.StartsWith("/search/album?q=Fleck and the Flecktones Greatest Hits"), "search_album_bela_fleck.json")
        .Route(u => u.StartsWith("/search/album?q=Daft Punk Discovery"), "search_album_discovery.json")
        .Route(u => u.StartsWith("/search/track?q=Daft Punk Aerodynamic"), "search_track_aerodynamic.json")
        .Route(u => u.StartsWith("/search/artist?q=Journey"), "search_artist_journey.json")
        .Route(u => u.StartsWith("/search/artist?q=Dream Theater"), "search_artist_dream_theater.json")
        .Route(u => u.StartsWith("/search/track?q=Rihanna Pon de Replay"), "search_track_pon_de_replay.json")
        .Route(u => u.StartsWith("/album/300501"), "album_300501.json")
        .Route(u => u.StartsWith("/album/1499088"), "album_1499088.json")
        .Route(u => u.StartsWith("/album/302127"), "album_302127.json")
        .Route(u => u.StartsWith("/album/upc:724384960650"), "album_302127.json")
        .Route(u => u.StartsWith("/track/3135556") || u.StartsWith("/track/isrc:GBDUW0000059"), "track_3135556.json")
        .Route(u => u.StartsWith("/artist/27"), "artist_27.json");

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);
        Urls.Add(url);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.RequestUri.Host.EndsWith("dzcdn.net") || request.RequestUri.AbsolutePath.EndsWith("/image"))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ImageBytes) });

        if (FailWith is { } status && FailTimes-- > 0)
        {
            var failure = new HttpResponseMessage(status) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            if (RetryAfterSeconds is { } after) failure.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(after));
            return Task.FromResult(failure);
        }

        if (Answer200 is { } answer && Answer200Times-- > 0)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") });

        foreach (var (match, body) in _routes)
            if (match(url))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body(), Encoding.UTF8, "application/json") });

        // Deezer answers an unknown record with HTTP 200 and a "no data" error, and an unknown search with an empty list.
        var empty = url.StartsWith("/search/") ? EmptySearch : "{\"error\":{\"type\":\"DataException\",\"message\":\"no data\",\"code\":800}}";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(empty, Encoding.UTF8, "application/json") });
    }

    public static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
