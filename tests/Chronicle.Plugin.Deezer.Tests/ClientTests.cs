using System.Net;
using Chronicle.Plugin.Deezer;

namespace Chronicle.Plugin.Deezer.Tests;

/// <summary>The rate-limit and error contract of the client. Deezer reports quota, busy and "no data" as HTTP 200 with an error body.</summary>
public class ClientTests
{
    [Fact]
    public async Task AQuotaAnswerSentAsHttp200_WaitsOnce_ThenIsThrownAs429_SoTheItemStaysPending()
    {
        var fake = new FakeDeezer { Answer200 = FakeDeezer.QuotaBody };
        var client = fake.Client();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetArtistAsync(27, CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(2, fake.Requests);                       // one try, one retry, no more
        Assert.Contains(TimeSpan.FromSeconds(5), fake.Delays); // Deezer's quota window is five seconds
    }

    [Fact]
    public async Task AQuotaThatClearsOnTheRetry_ReturnsTheRecord()
    {
        var fake = FakeDeezer.Catalogue();
        fake.Answer200 = FakeDeezer.QuotaBody;
        fake.Answer200Times = 1;

        var artist = await fake.Client().GetArtistAsync(27, CancellationToken.None);

        Assert.Equal("Daft Punk", artist!.Name);
        Assert.Equal(2, fake.Requests);
    }

    [Fact]
    public async Task AnHttp429_WithAShortRetryAfter_IsWaitedOnce()
    {
        var fake = FakeDeezer.Catalogue();
        fake.FailWith = HttpStatusCode.TooManyRequests;
        fake.RetryAfterSeconds = 3;
        fake.FailTimes = 1;

        var artist = await fake.Client().GetArtistAsync(27, CancellationToken.None);

        Assert.NotNull(artist);
        Assert.Contains(TimeSpan.FromSeconds(3), fake.Delays);
        Assert.Equal(2, fake.Requests);
    }

    [Fact]
    public async Task AnHttp429_WithALongRetryAfter_IsThrownNotWaited()
    {
        // Chronicle gives a plugin 25 seconds per call: waiting out a long limit would only turn every item into "not found".
        var fake = new FakeDeezer { FailWith = HttpStatusCode.TooManyRequests, RetryAfterSeconds = 120 };

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => fake.Client().GetArtistAsync(27, CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(1, fake.Requests);
        Assert.DoesNotContain(TimeSpan.FromSeconds(120), fake.Delays);
    }

    [Fact]
    public async Task ServiceBusy_IsAnErrorThatPropagates_NotANotFound()
    {
        var fake = new FakeDeezer { Answer200 = FakeDeezer.BusyBody };

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => fake.Client().GetAlbumAsync(1, CancellationToken.None));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
    }

    [Fact]
    public async Task NoData_IsAnormalNotFound()
    {
        var fake = new FakeDeezer();
        var client = fake.Client();

        Assert.Null(await client.GetAlbumAsync(1, CancellationToken.None));
        Assert.Null(await client.GetTrackByIsrcAsync("XX0000000000", CancellationToken.None));
        Assert.Empty(await client.SearchAlbumsAsync("nothing at all", 10, CancellationToken.None));
    }

    [Fact]
    public async Task AQueryDeezerCannotParse_IsNotFound_NotAFailure()
    {
        var fake = new FakeDeezer { Answer200 = FakeDeezer.BadQueryBody };
        Assert.Empty(await fake.Client().SearchTracksAsync("???", 10, CancellationToken.None));
    }

    [Fact]
    public async Task AnUnknownErrorCode_Propagates()
    {
        var fake = new FakeDeezer { Answer200 = "{\"error\":{\"type\":\"OAuthException\",\"message\":\"Invalid OAuth access token.\",\"code\":300}}" };

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => fake.Client().GetArtistAsync(27, CancellationToken.None));

        Assert.Contains("300", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ServerErrors_Propagate_SoChronicleCanRetry(HttpStatusCode status)
    {
        var fake = new FakeDeezer { FailWith = status };

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => fake.Client().GetArtistAsync(27, CancellationToken.None));

        Assert.Equal(status, ex.StatusCode);
    }

    [Fact]
    public async Task A404_IsNotFound()
    {
        var fake = new FakeDeezer { FailWith = HttpStatusCode.NotFound };
        Assert.Null(await fake.Client().GetArtistAsync(27, CancellationToken.None));
    }

    [Fact]
    public async Task ABodyThatIsNotJson_IsAnError_NotAnEmptyRecord()
    {
        var fake = new FakeDeezer { Answer200 = "<html>maintenance</html>" };
        await Assert.ThrowsAsync<HttpRequestException>(() => fake.Client().GetArtistAsync(27, CancellationToken.None));
    }

    [Fact]
    public async Task Requests_AreSentPlainText_WithTheQueryEscaped()
    {
        var fake = FakeDeezer.Catalogue();
        await fake.Client().SearchAlbumsAsync("Jesus Jones Doubt & more?", 10, CancellationToken.None);

        Assert.Equal("/search/album?q=Jesus Jones Doubt & more?&limit=10", fake.Urls.Single());
        // The wire form is escaped (the fake shows the unescaped path); the field syntax artist:"X" is never used.
        Assert.DoesNotContain("artist:", fake.Urls.Single());
    }

    [Fact]
    public async Task Requests_ArePaced()
    {
        var fake = FakeDeezer.Catalogue();
        var clock = DateTimeOffset.UtcNow;
        var delays = new List<TimeSpan>();
        var client = new DeezerClient(new HttpClient(fake), TimeSpan.FromMilliseconds(150), delay: (span, _) => { delays.Add(span); return Task.CompletedTask; });

        await client.GetArtistAsync(27, CancellationToken.None);
        await client.GetArtistAsync(27, CancellationToken.None);

        Assert.Single(delays);
        Assert.InRange(delays[0], TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(150));
        Assert.True(DateTimeOffset.UtcNow - clock < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TheCountOfRequests_IsTracked()
    {
        var fake = FakeDeezer.Catalogue();
        var client = fake.Client();
        await client.GetArtistAsync(27, CancellationToken.None);
        await client.GetAlbumAsync(302127, CancellationToken.None);
        Assert.Equal(2, client.Requests);
    }

    [Fact]
    public async Task HealthCheck_IsTrueForARealAnswer_FalseForAFailure()
    {
        Assert.True(await FakeDeezer.Catalogue().Client().HealthCheckAsync(CancellationToken.None));
        Assert.False(await new FakeDeezer { FailWith = HttpStatusCode.InternalServerError }.Client().HealthCheckAsync(CancellationToken.None));
        Assert.False(await new FakeDeezer().Client().HealthCheckAsync(CancellationToken.None));   // "no data" for the artist
    }

    [Fact]
    public async Task Images_AreDownloadedOnlyWhenAsked_AndNotPaced()
    {
        var fake = new FakeDeezer { ImageBytes = [9, 9, 9] };
        var bytes = await fake.Client().GetImageBytesAsync("https://cdn-images.dzcdn.net/images/cover/x/1900x1900-000000-80-0-0.jpg", CancellationToken.None);

        Assert.Equal([9, 9, 9], bytes);
        Assert.Empty(fake.Delays);
    }

    [Fact]
    public void ReadError_FindsAnErrorBody_AndOnlyThen()
    {
        Assert.Equal(4, DeezerClient.ReadError(FakeDeezer.QuotaBody)!.Code);
        Assert.Null(DeezerClient.ReadError("{\"id\":27,\"name\":\"Daft Punk\"}"));
        Assert.Null(DeezerClient.ReadError("{\"title\":\"an \\\"error\\\" in the name\"}"));
        Assert.Null(DeezerClient.ReadError("not json but says \"error\""));
    }

    [Fact]
    public void TheUserAgent_NamesThePluginAndItsVersion() => Assert.Equal("Chronicle-Deezer/1.2.3", DeezerClient.UserAgent("1.2.3"));
}
