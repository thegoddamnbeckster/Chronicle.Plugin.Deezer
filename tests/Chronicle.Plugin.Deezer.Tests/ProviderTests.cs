using System.Net;
using System.Text.Json;
using Chronicle.Plugin.Deezer;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.Deezer.Tests;

public class ProviderTests
{
    private static DeezerMetadataProvider Provider(FakeDeezer fake, MatchSettings? settings = null, int size = 1900) =>
        new(fake.Client(), settings, size);

    private static MediaSearchContext Artist(string name, IReadOnlyList<string>? albums = null) =>
        new(name, HierarchyLevel: 0, IsRealHierarchyPosition: true, MediaTypeName: "music", ChildNames: albums);

    private static MediaSearchContext Album(string title, string artist, int? children = null, IReadOnlyList<string>? tracks = null, int? year = null) =>
        new(title, Year: year, ParentName: artist, HierarchyLevel: 1, IsRealHierarchyPosition: true, MediaTypeName: "music",
            ChildCount: children, ChildNames: tracks);

    private static MediaSearchContext Track(string title, string album, string artist, int? number = null, IReadOnlyList<string>? siblings = null) =>
        new(title, ParentName: album, GrandparentName: artist, ItemNumber: number, HierarchyLevel: 2, IsRealHierarchyPosition: true,
            MediaTypeName: "music", SiblingNames: siblings);

    // ── artists ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnArtist_IsFoundByName_InOneRequest_PopularNamesakeFirst()
    {
        var fake = FakeDeezer.Catalogue();

        var results = await Provider(fake).SearchAsync(Artist("Journey"));

        Assert.Equal(1, fake.Requests);
        Assert.Equal("/search/artist?q=Journey&limit=10", fake.Urls[0]);
        Assert.Equal(2, results.Count);                         // the two artists called Journey; nothing similar
        Assert.True(results[0].Score > results[1].Score);
        Assert.StartsWith("artist:", results[0].Metadata.ExternalId);
        Assert.Equal("deezer", results[0].Metadata.Source);
        Assert.All(results, r => Assert.Equal("Journey", r.Metadata.Title));
    }

    [Fact]
    public async Task ASimilarArtistName_FindsNothing()
    {
        var fake = FakeDeezer.Catalogue();
        var results = await Provider(fake).SearchAsync(Artist("Dream Theater"));

        Assert.DoesNotContain(results, r => r.Metadata.Title == "Dream Home Theater");
        Assert.All(results, r => Assert.Contains("Dream Theater", r.Metadata.Title));
    }

    [Fact]
    public async Task AnArtistFolderCalledDiscography_IsSearchedAndMatchedWithoutTheWord_AndKeepsTheLibrarysWordingAsAnAlias()
    {
        var fake = FakeDeezer.Catalogue();
        var results = await Provider(fake).SearchAsync(Artist("Journey discography"));

        Assert.Equal("/search/artist?q=Journey&limit=10", fake.Urls[0]);
        Assert.NotEmpty(results);
        Assert.Contains("Journey discography", results[0].Metadata.AlternateNames);   // the host's name gate accepts the library's wording
    }

    [Fact]
    public async Task NamesakesCloseInScore_AreToldApartByTheAlbumsTheLibraryHolds()
    {
        var fake = new FakeDeezer()
            .RouteBody(u => u.StartsWith("/search/artist"),
                "{\"data\":[{\"id\":1,\"name\":\"Ghost\",\"nb_fan\":200000},{\"id\":2,\"name\":\"Ghost\",\"nb_fan\":150000}]}")
            .RouteBody(u => u.StartsWith("/artist/1/albums"), "{\"data\":[{\"id\":10,\"title\":\"Opus Eponymous\"},{\"id\":11,\"title\":\"Meliora\"}]}")
            .RouteBody(u => u.StartsWith("/artist/2/albums"), "{\"data\":[{\"id\":20,\"title\":\"Some Other Record\"}]}");

        var results = await Provider(fake).SearchAsync(Artist("Ghost", ["Meliora", "Opus Eponymous"]));

        Assert.Equal(3, fake.Requests);   // the search and the catalogues of the two close candidates
        Assert.Equal("artist:1", results[0].Metadata.ExternalId);
        Assert.True(results[0].Score - results[1].Score >= 10);
    }

    [Fact]
    public async Task ASingleClearCandidate_CostsNoCatalogueRequest()
    {
        var fake = FakeDeezer.Catalogue();
        await Provider(fake).SearchAsync(Artist("Dream Theater", ["Awake"]));
        Assert.Equal(1, fake.Requests);
    }

    // ── albums ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnAlbum_IsSearchedAsArtistAndTitle_AndTheEditionWithTheLibrarysTracksWins()
    {
        var fake = FakeDeezer.Catalogue();

        var results = await Provider(fake).SearchAsync(Album("Doubt", "Jesus Jones", children: 11));

        Assert.Equal("/search/album?q=Jesus Jones Doubt&limit=10", fake.Urls[0]);
        Assert.InRange(fake.Requests, 2, 4);
        Assert.Equal("album:1499088", results[0].Metadata.ExternalId);       // the 11-track edition
        Assert.True(results[0].Score >= 90);
        Assert.Equal(["Rock"], results[0].Metadata.Genres);                  // the close candidates were fetched in full
        Assert.Equal(1991, results[0].Metadata.Year);
    }

    [Fact]
    public async Task TheOtherEdition_WinsWhenThatIsWhatTheLibraryHolds()
    {
        var fake = FakeDeezer.Catalogue();
        var results = await Provider(fake).SearchAsync(Album("Doubt", "Jesus Jones", children: 21));
        Assert.Equal("album:300501", results[0].Metadata.ExternalId);
    }

    [Fact]
    public async Task TheTrackNamesTheLibraryHolds_ChooseTheEdition_EvenWithoutACount()
    {
        var fake = FakeDeezer.Catalogue();
        var names = new[] { "Trust Me", "Who? Where? Why?", "International Bright Young Thing", "I'm Burning", "Right Here Right Now" };
        var results = await Provider(fake).SearchAsync(Album("Doubt", "Jesus Jones", tracks: names));
        Assert.True(results[0].Score >= 90);
    }

    [Fact]
    public async Task AnAlbumNotOnDeezer_IsNotFound_AfterTheTitleOnlyFallback_WithoutWrongMatches()
    {
        var fake = FakeDeezer.Catalogue();

        var results = await Provider(fake).SearchAsync(Album("Prequelle", "Ghost"));

        Assert.Equal(2, fake.Requests);                                       // "Ghost Prequelle" finds nothing, then "Prequelle" alone
        Assert.Equal("/search/album?q=Prequelle&limit=25", fake.Urls[1]);
        Assert.DoesNotContain(results, r => r.Score >= 50);                   // Prequel, PREQUEL ... by other artists are not it
    }

    [Fact]
    public async Task AKaraokeAndTributeFlood_DoesNotBuryTheRealRelease()
    {
        var fake = FakeDeezer.Catalogue();
        var results = await Provider(fake).SearchAsync(Album("It's My Life", "Bon Jovi", children: 3));

        var accepted = results.Where(r => r.Score >= 50).ToList();
        Assert.Single(accepted);
        Assert.Contains("Bon Jovi", accepted[0].Metadata.Cast.Select(c => c.Name));
    }

    [Fact]
    public async Task ARadioEditSingle_AnswersTheLibrarysPlainSingle()
    {
        var fake = FakeDeezer.Catalogue();
        var results = await Provider(fake).SearchAsync(Album("TKO", "Justin Timberlake", children: 1, year: 2013));
        Assert.True(results[0].Score >= 80);
        Assert.Equal("TKO (Radio Edit)", results[0].Metadata.Title);
        Assert.Contains("TKO", results[0].Metadata.AlternateNames);
    }

    [Fact]
    public async Task ABrokenCharacterInTheLibrary_IsLeftOutOfTheQuery_AndStillMatches()
    {
        var fake = FakeDeezer.Catalogue();
        var results = await Provider(fake).SearchAsync(Album("Greatest Hits of the 20th Century", "B?la Fleck and the Flecktones"));

        Assert.StartsWith("/search/album?q=Fleck and the Flecktones Greatest Hits of the 20th Century", fake.Urls[0]);
        Assert.True(results[0].Score >= 90);
    }

    [Fact]
    public async Task EveryCall_StaysWithinFourRequests()
    {
        foreach (var context in new[]
        {
            Album("Doubt", "Jesus Jones", children: 11), Album("Prequelle", "Ghost"), Album("It's My Life", "Bon Jovi"),
            Artist("Journey"), Track("Pon de Replay", "Music Of The Sun", "Rihanna", 1), Track("Aerodynamic", "Discovery", "Daft Punk", 2),
        })
        {
            var fake = FakeDeezer.Catalogue();
            await Provider(fake).SearchAsync(context);
            Assert.True(fake.Requests <= 4, $"{context.Name}: {fake.Requests} requests");
        }
    }

    // ── tracks ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ATrack_IsFoundInsideItsAlbum_AndTheNextTrackOnThatAlbumCostsNothing()
    {
        var fake = FakeDeezer.Catalogue();
        var provider = Provider(fake);
        var siblings = new[] { "One More Time", "Digital Love", "Harder, Better, Faster, Stronger" };

        var first = await provider.SearchAsync(Track("Aerodynamic", "Discovery", "Daft Punk", 2, siblings));
        var afterFirst = fake.Requests;
        var second = await provider.SearchAsync(Track("Digital Love", "Discovery", "Daft Punk", 3, siblings));

        Assert.Equal("Aerodynamic", first[0].Metadata.Title);
        Assert.StartsWith("track:", first[0].Metadata.ExternalId);
        Assert.True(first[0].Score >= 90);
        Assert.InRange(afterFirst, 2, 3);                    // the album search and the album in full
        Assert.Equal(afterFirst, fake.Requests);             // nothing for the second track of the same album
        Assert.Equal("Digital Love", second[0].Metadata.Title);
    }

    [Fact]
    public async Task AnAlbumThatIsNotOnDeezer_IsRememberedAsNotThere_SoItsOtherTracksDoNotSearchAgain()
    {
        var fake = FakeDeezer.Catalogue();
        var provider = Provider(fake);

        await provider.SearchAsync(Track("Pon de Replay", "Music Of The Sun", "Rihanna", 1));
        var afterFirst = fake.Requests;
        await provider.SearchAsync(Track("Pon de Replay", "Music Of The Sun", "Rihanna", 1));

        // The album lookup is remembered; only the track search itself (which is not cached) is made again.
        Assert.True(fake.Requests - afterFirst <= 2);
    }

    [Fact]
    public async Task ATrackWhoseAlbumIsNotFound_FallsBackToASearchOfItsOwn()
    {
        var fake = FakeDeezer.Catalogue();

        var results = await Provider(fake).SearchAsync(Track("Pon de Replay", "Music Of The Sun", "Rihanna", 1));

        Assert.Contains(fake.Urls, u => u.StartsWith("/search/track?q=Rihanna Pon de Replay"));
        Assert.True(results[0].Score >= 90);
        Assert.Equal("Pon de Replay", results[0].Metadata.Title);
        Assert.All(results.Where(r => r.Score >= 50), r => Assert.Contains("Rihanna", r.Metadata.Cast.Select(c => c.Name)));
        Assert.True(fake.Requests <= 4);
    }

    [Fact]
    public async Task ARemixOfTheTrack_IsNotAcceptedForThePlainTrack()
    {
        var fake = FakeDeezer.Catalogue();
        var results = await Provider(fake).SearchAsync(Track("Pon de Replay", "Music Of The Sun", "Rihanna", 1));
        Assert.DoesNotContain(results.Where(r => r.Score >= 50), r => r.Metadata.Title.Contains("Remix"));
    }

    [Fact]
    public async Task ARemixOfTheRightTitle_StaysBelowTheGate_EvenWhenItsTracksMatchTheLibrarys()
    {
        // "Jumper": the library holds a 2-track single; Deezer has a 2-track release of a REMIX with the same track count.
        var fake = new FakeDeezer()
            .RouteBody(u => u.StartsWith("/search/album?q=Third Eye Blind Jumper"),
                "{\"data\":[{\"id\":7,\"title\":\"Jumper (Radio Edit) / Graduate (Remix)\",\"nb_tracks\":2,\"record_type\":\"single\",\"artist\":{\"id\":3,\"name\":\"Third Eye Blind\"}}]}")
            .RouteBody(u => u.StartsWith("/album/7"),
                "{\"id\":7,\"title\":\"Jumper (Radio Edit) / Graduate (Remix)\",\"nb_tracks\":2,\"record_type\":\"single\",\"artist\":{\"id\":3,\"name\":\"Third Eye Blind\"}," +
                "\"tracks\":{\"data\":[{\"id\":1,\"title\":\"Jumper\",\"duration\":270},{\"id\":2,\"title\":\"Graduate (Remix)\",\"duration\":200}]}}");

        var context = Album("Jumper", "Third Eye Blind", children: 2, tracks: ["Jumper", "Graduate (Remix)"]);
        var results = await Provider(fake).SearchAsync(context);

        Assert.All(results, r => Assert.True(r.Score < 50, r.Score.ToString()));
    }

    [Fact]
    public async Task ATrackFoundOnItsAlbum_CostsOneMoreRequestToFetchInFull_ByDefault()
    {
        var fake = FakeDeezer.Catalogue();
        var provider = Provider(fake);
        var found = await provider.SearchAsync(Track("Harder, Better, Faster, Stronger", "Discovery", "Daft Punk", 4));
        var afterSearch = fake.Requests;

        var full = await provider.GetByIdAsync(found[0].Metadata.ExternalId);

        Assert.Equal(afterSearch + 1, fake.Requests);                       // the track record itself (the album is already remembered)
        Assert.StartsWith("/track/3135556", fake.Urls[^1]);
        Assert.Equal("GBDUW0000059", full.ExtendedData!.Value.GetProperty("isrc").GetString());
        Assert.Equal(["Electro"], full.Genres);
    }

    [Fact]
    public async Task WithFetchTrackDetailsOff_ATrackFoundOnItsAlbum_CostsNothingMore()
    {
        var fake = FakeDeezer.Catalogue();
        var provider = new DeezerMetadataProvider(fake.Client());
        provider.Configure(new Dictionary<string, string> { [DeezerMetadataProvider.KeyTrackDetails] = "false" });
        var found = await provider.SearchAsync(Track("Harder, Better, Faster, Stronger", "Discovery", "Daft Punk", 4));
        var afterSearch = fake.Requests;

        var full = await provider.GetByIdAsync(found[0].Metadata.ExternalId);

        Assert.Equal(afterSearch, fake.Requests);
        Assert.Equal("Harder, Better, Faster, Stronger", full.Title);
        Assert.Equal(["Electro"], full.Genres);                             // from the album already held
        Assert.False(full.ExtendedData!.Value.TryGetProperty("isrc", out var isrc) && isrc.ValueKind == JsonValueKind.String);
    }

    [Fact]
    public async Task WithFetchTrackDetailsOff_AnIdTypedByHand_IsStillFetchedInFull()
    {
        var fake = FakeDeezer.Catalogue();
        var provider = new DeezerMetadataProvider(fake.Client());
        provider.Configure(new Dictionary<string, string> { [DeezerMetadataProvider.KeyTrackDetails] = "false" });

        var full = await provider.GetByIdAsync("track:3135556");

        Assert.Equal("GBDUW0000059", full.ExtendedData!.Value.GetProperty("isrc").GetString());
    }

    // ── identifiers and kinds ─────────────────────────────────────────────────

    [Fact]
    public async Task AnIdTheItemAlreadyHolds_IsFetched_AsAnIdentifierMatch()
    {
        var fake = FakeDeezer.Catalogue();
        var context = Album("Whatever the library calls it", "Daft Punk") with
        {
            KnownExternalIds = new Dictionary<string, string> { ["deezer"] = "album:302127" },
        };

        var results = await Provider(fake).SearchAsync(context);

        Assert.Single(results);
        Assert.Equal(100, results[0].Score);
        Assert.True(results[0].IdentifierMatch);
        Assert.Equal("Discovery", results[0].Metadata.Title);
        Assert.Equal(1, fake.Requests);
        Assert.StartsWith("/album/302127", fake.Urls[0]);
    }

    [Theory]
    [InlineData(0, "album:302127")]   // an album id on an artist
    [InlineData(1, "artist:27")]      // an artist id on an album
    [InlineData(2, "album:302127")]   // an album id on a track
    public async Task AnIdOfTheWrongKind_IsNotUsed(int level, string stored)
    {
        var fake = FakeDeezer.Catalogue();
        var context = new MediaSearchContext("Nothing In Particular", ParentName: "Nobody", GrandparentName: "Nobody", HierarchyLevel: level,
            IsRealHierarchyPosition: true, MediaTypeName: "music", KnownExternalIds: new Dictionary<string, string> { ["deezer"] = stored });

        var results = await Provider(fake).SearchAsync(context);

        Assert.DoesNotContain(fake.Urls, u => u.StartsWith("/album/302127") || u.StartsWith("/artist/27"));
        Assert.DoesNotContain(results, r => r.IdentifierMatch);
    }

    [Fact]
    public async Task OtherMediaTypes_AreNotSearched_AtAll()
    {
        var fake = FakeDeezer.Catalogue();
        var results = await Provider(fake).SearchAsync(new MediaSearchContext("Oathbringer", HierarchyLevel: 2, MediaTypeName: "books"));

        Assert.Empty(results);
        Assert.Equal(0, fake.Requests);
    }

    [Fact]
    public async Task FreeText_SearchesArtistsAlbumsAndTracks_AndDoesNotCapAnything()
    {
        var fake = FakeDeezer.Catalogue()
            .Route(u => u.StartsWith("/search/artist?q=Discovery"), "search_artist_journey.json")
            .Route(u => u.StartsWith("/search/album?q=Discovery"), "search_album_discovery.json");

        var results = await Provider(fake).SearchAsync(new MediaSearchContext("Discovery"));   // no real hierarchy position: a typed query

        Assert.Equal(3, fake.Requests);
        Assert.Contains(results, r => r.Metadata.ExternalId == "album:302127" && r.Score >= 60);
    }

    [Fact]
    public async Task ATitleOnly_SearchWithNoTitle_DoesNothing()
    {
        var fake = FakeDeezer.Catalogue();
        Assert.Empty(await Provider(fake).SearchAsync(new MediaSearchContext("  ", HierarchyLevel: 1, ParentName: "X", IsRealHierarchyPosition: true)));
        Assert.Equal(0, fake.Requests);
    }

    // ── rate limits and errors ────────────────────────────────────────────────

    [Fact]
    public async Task AQuota_DuringASearch_IsThrownAs429_SoTheItemStaysPending()
    {
        var fake = new FakeDeezer { Answer200 = FakeDeezer.QuotaBody };

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Provider(fake).SearchAsync(Album("Doubt", "Jesus Jones")));

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
    }

    [Fact]
    public async Task AServerError_DuringASearch_Propagates()
    {
        var fake = new FakeDeezer { FailWith = HttpStatusCode.BadGateway };
        await Assert.ThrowsAsync<HttpRequestException>(() => Provider(fake).SearchAsync(Artist("Journey")));
    }

    // ── fetch by id ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AnArtist_ByID_HasItsPictureAtTheChosenSize()
    {
        var fake = FakeDeezer.Catalogue();

        var large = await Provider(fake).GetByIdAsync("artist:27");
        var small = await Provider(FakeDeezer.Catalogue(), size: 250).GetByIdAsync("artist:27");

        Assert.Equal("Daft Punk", large.Title);
        Assert.Contains("/1900x1900-", large.PosterUrl);
        Assert.Contains("/250x250-", small.PosterUrl);
    }

    [Fact]
    public async Task AnAlbum_ByID_IsComplete()
    {
        var album = await Provider(FakeDeezer.Catalogue()).GetByIdAsync("album:302127");

        Assert.Equal("album:302127", album.ExternalId);
        Assert.Equal("Discovery", album.Title);
        Assert.Equal(2001, album.Year);
        Assert.Equal(["Electro"], album.Genres);
        Assert.Contains("/1900x1900-", album.PosterUrl);
        Assert.Contains(album.Cast, c => c.Name == "Daft Punk" && c.ExternalPersonId!.StartsWith("artist:"));
        var data = album.ExtendedData!.Value;
        Assert.Equal("724384960650", data.GetProperty("upc").GetString());
        Assert.Equal("724384960650", data.GetProperty("ids").GetProperty("upc").GetString());
        Assert.Equal("Daft Life Ltd./ADA France", data.GetProperty("label").GetString());
        Assert.Equal("album", data.GetProperty("record_type").GetString());
        Assert.Equal(14, data.GetProperty("tracks").GetArrayLength());
        Assert.Equal("One More Time", data.GetProperty("tracks")[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task ATrack_ByID_CarriesItsAlbumsGenres_AndItsCodes()
    {
        var fake = FakeDeezer.Catalogue();

        var track = await Provider(fake).GetByIdAsync("track:3135556");

        Assert.Equal("Harder, Better, Faster, Stronger", track.Title);
        Assert.Equal(2001, track.Year);
        Assert.Equal(4, track.RuntimeMinutes);                          // 226 seconds
        Assert.Equal(["Electro"], track.Genres);                        // from the album: a track has none of its own
        Assert.Equal("GBDUW0000059", track.ExtendedData!.Value.GetProperty("ids").GetProperty("isrc").GetString());
        Assert.Equal(226, track.ExtendedData!.Value.GetProperty("duration_seconds").GetInt32());
        Assert.Equal(4, track.ExtendedData!.Value.GetProperty("track_position").GetInt32());
        Assert.Equal(2, fake.Requests);                                  // the track and its album
    }

    [Theory]
    [InlineData("isrc:GBDUW0000059", "Harder, Better, Faster, Stronger")]
    [InlineData("upc:724384960650", "Discovery")]
    [InlineData("https://www.deezer.com/us/album/302127", "Discovery")]
    [InlineData("deezer:track:3135556", "Harder, Better, Faster, Stronger")]
    public async Task OtherWaysToNameIt_Work(string reference, string title) =>
        Assert.Equal(title, (await Provider(FakeDeezer.Catalogue()).GetByIdAsync(reference)).Title);

    [Theory]
    [InlineData("27")]
    [InlineData("nonsense")]
    [InlineData("")]
    public async Task AnIdThatCannotBeRead_IsEmpty_WithoutARequest(string reference)
    {
        var fake = FakeDeezer.Catalogue();
        var result = await Provider(fake).GetByIdAsync(reference);

        Assert.Equal("", result.Title);
        Assert.Equal(0, fake.Requests);
    }

    [Fact]
    public async Task AnIdDeezerHasNoDataFor_IsEmpty_NotAnError()
    {
        var fake = new FakeDeezer();
        Assert.Equal("", (await Provider(fake).GetByIdAsync("album:1")).Title);
        Assert.Equal("", (await Provider(fake).GetByIdAsync("artist:1")).Title);
        Assert.Equal("", (await Provider(fake).GetByIdAsync("track:1")).Title);
    }

    [Fact]
    public async Task WhatWasJustFetched_IsRemembered()
    {
        var fake = FakeDeezer.Catalogue();
        var provider = Provider(fake);

        await provider.GetByIdAsync("album:302127");
        await provider.GetByIdAsync("album:302127");
        await provider.GetByIdAsync("artist:27");
        await provider.GetByIdAsync("artist:27");

        Assert.Equal(2, fake.Requests);
    }

    [Fact]
    public async Task ATracksAlbumThatCannotBeRead_DoesNotLoseTheTrack()
    {
        var fake = FakeDeezer.Catalogue();
        fake.RouteBody(u => u.StartsWith("/album/302127"), "{\"error\":{\"type\":\"Exception\",\"message\":\"x\",\"code\":999}}");
        // Routes added later lose to earlier ones: build a catalogue where the album answer is the broken one.
        var broken = new FakeDeezer()
            .RouteBody(u => u.StartsWith("/album/302127"), "{\"error\":{\"type\":\"Exception\",\"message\":\"x\",\"code\":999}}")
            .Route(u => u.StartsWith("/track/3135556"), "track_3135556.json");

        var track = await Provider(broken).GetByIdAsync("track:3135556");

        Assert.Equal("Harder, Better, Faster, Stronger", track.Title);
        Assert.Empty(track.Genres);
    }

    [Fact]
    public async Task NothingExpiringIsStored_NoPreviewLink_NoToken()
    {
        var provider = Provider(FakeDeezer.Catalogue());
        var json = JsonSerializer.Serialize(new[]
        {
            await provider.GetByIdAsync("track:3135556"), await provider.GetByIdAsync("album:302127"), await provider.GetByIdAsync("artist:27"),
        });

        Assert.DoesNotContain("hdnea", json);
        Assert.DoesNotContain("preview", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("track_token", json);
        Assert.DoesNotContain("cdnt-preview", json);
    }

    // ── images and health ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://cdn-images.dzcdn.net/images/cover/x/1900x1900-000000-80-0-0.jpg", true)]
    [InlineData("https://e-cdns-images.dzcdn.net/images/artist/x/500x500-000000-80-0-0.jpg", true)]
    [InlineData("https://api.deezer.com/album/302127/image", true)]
    [InlineData("http://cdn-images.dzcdn.net/images/cover/x.jpg", false)]
    [InlineData("https://evil.example.com/dzcdn.net/x.jpg", false)]
    [InlineData("https://notdzcdn.net/x.jpg", false)]
    [InlineData("file:///c:/secrets.txt", false)]
    [InlineData("not a url", false)]
    public async Task OnlyDeezersOwnImageHosts_AreFetched(string url, bool allowed)
    {
        var provider = Provider(new FakeDeezer { ImageBytes = [1, 2, 3] });
        if (allowed) Assert.Equal([1, 2, 3], await provider.GetImageAsync(url));
        else await Assert.ThrowsAsync<ArgumentException>(() => provider.GetImageAsync(url));
    }

    [Fact]
    public async Task Health_IsTrueWhenDeezerAnswers_AndFalseWhenNotConfigured()
    {
        Assert.True(await Provider(FakeDeezer.Catalogue()).HealthCheckAsync());
        Assert.False(await new DeezerMetadataProvider().HealthCheckAsync());
    }

    [Fact]
    public async Task AnUnconfiguredPlugin_RefusesToSearch()
    {
        var provider = new DeezerMetadataProvider();
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SearchAsync(Artist("Journey")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetByIdAsync("artist:27"));
    }
}
