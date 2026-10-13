using System.Text.Json;
using Chronicle.Plugin.Deezer;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.Deezer.Tests;

public class ConfigurationTests
{
    private static DeezerMetadataProvider Configured(Dictionary<string, string> settings)
    {
        var provider = new DeezerMetadataProvider(new FakeDeezer().Client());
        provider.Configure(settings);
        return provider;
    }

    [Fact]
    public void WithNothingSet_TheDefaultsApply()
    {
        var provider = Configured([]);

        Assert.Equal(1900, provider.ImageSize);
        Assert.Equal(5, provider.Match.DurationToleranceSeconds);
        Assert.True(provider.FetchTrackDetails);
        Assert.Equal(MatchSettings.DefaultJunkTerms, provider.Match.JunkTerms);
        Assert.Equal(MatchSettings.DefaultArtistNoise, provider.Match.ArtistNoise);
    }

    [Theory]
    [InlineData("max", 1900)]
    [InlineData("xl", 1000)]
    [InlineData("big", 500)]
    [InlineData("medium", 250)]
    [InlineData("XL", 1000)]
    [InlineData("  big ", 500)]
    [InlineData("gigantic", 1900)]   // unknown: the default, never a failure to start
    [InlineData("", 1900)]
    public void TheCoverSize_IsTheOneChosen(string value, int expected) =>
        Assert.Equal(expected, Configured(new() { [DeezerMetadataProvider.KeyCoverSize] = value }).ImageSize);

    [Theory]
    [InlineData("12", 12)]
    [InlineData("0", 1)]
    [InlineData("-3", 1)]
    [InlineData("900", 60)]
    [InlineData("abc", 5)]
    [InlineData("", 5)]
    public void TheDurationTolerance_IsKeptWithinBounds(string value, int expected) =>
        Assert.Equal(expected, Configured(new() { [DeezerMetadataProvider.KeyDurationTolerance] = value }).Match.DurationToleranceSeconds);

    [Theory]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("true", true)]
    [InlineData("", true)]
    [InlineData("nonsense", true)]
    public void FetchTrackDetails_IsTheOneChosen(string value, bool expected) =>
        Assert.Equal(expected, Configured(new() { [DeezerMetadataProvider.KeyTrackDetails] = value }).FetchTrackDetails);

    [Fact]
    public void TheJunkList_IsWhatTheUserWrote_OrTheDefaultWhenLeftEmpty()
    {
        Assert.Equal(["brand new kind", "Other"], Configured(new() { [DeezerMetadataProvider.KeyJunkTerms] = "brand new kind\r\nOther\n\n" }).Match.JunkTerms);
        Assert.Equal(MatchSettings.DefaultJunkTerms, Configured(new() { [DeezerMetadataProvider.KeyJunkTerms] = "   " }).Match.JunkTerms.ToArray());
    }

    [Fact]
    public void TheArtistFolderWords_AreWhatTheUserWrote_OrTheDefaultWhenLeftEmpty()
    {
        Assert.Equal(["my folder suffix"], Configured(new() { [DeezerMetadataProvider.KeyArtistWords] = "my folder suffix" }).Match.ArtistNoise);
        Assert.Equal(MatchSettings.DefaultArtistNoise, Configured([]).Match.ArtistNoise.ToArray());
    }

    [Fact]
    public void AnEditedJunkList_ChangesWhatIsPenalised()
    {
        var provider = new DeezerMetadataProvider(new FakeDeezer().Client());
        provider.Configure(new Dictionary<string, string> { [DeezerMetadataProvider.KeyJunkTerms] = "deluxe" });   // the user's own idea of junk
        var album = new AlbumDoc { Id = 1, Title = "Africa (Deluxe)", NbTracks = 1, Artist = new ArtistDoc { Id = 2, Name = "Toto" } };
        var query = new AlbumQuery("Africa", "Toto", null);

        Assert.True(Matchers.ScoreAlbum(album, query, MatchSettings.Default) > Matchers.ScoreAlbum(album, query, provider.Match));
    }

    [Fact]
    public void TheSettingsForm_HasEveryKey_WithDefaultsThatMatchTheCode()
    {
        var schema = new DeezerMetadataProvider().GetSettingsSchema().Settings;

        Assert.Equal(["cover_size", "duration_tolerance_seconds", "fetch_track_details", "junk_terms", "artist_folder_words"], schema.Select(s => s.Key).ToArray());
        Assert.Equal(SettingType.Boolean, schema.Single(s => s.Key == "fetch_track_details").Type);
        Assert.Equal("true", schema.Single(s => s.Key == "fetch_track_details").DefaultValue);
        var size = schema.Single(s => s.Key == "cover_size");
        Assert.Equal(SettingType.Dropdown, size.Type);
        Assert.Equal("max", size.DefaultValue);
        Assert.Equal(["max", "xl", "big", "medium"], size.Options.Select(o => o.Value).ToArray());
        Assert.Equal(SettingType.TextArea, schema.Single(s => s.Key == "junk_terms").Type);
        Assert.Equal(MatchSettings.DefaultJunkTerms, TextRules.ParseList(schema.Single(s => s.Key == "junk_terms").DefaultValue));
        Assert.Equal(MatchSettings.DefaultArtistNoise, TextRules.ParseList(schema.Single(s => s.Key == "artist_folder_words").DefaultValue));
        Assert.All(schema, s => Assert.False(s.Required));
    }

    [Fact]
    public void ConfiguringTheRealClient_NamesThePluginInTheUserAgent_AndNeedsNoKey()
    {
        var provider = new DeezerMetadataProvider();
        provider.Configure(new Dictionary<string, string>());   // no key, no account: nothing to be missing

        Assert.Equal("Chronicle-Deezer/1.0.0", provider.ConfiguredUserAgent);
        provider.Dispose();
    }

    [Fact]
    public void ItAcceptsTheDeezerCrossReferencePrefix() =>
        Assert.Equal(["deezer:"], new DeezerMetadataProvider().GetAcceptedCrossRefPrefixes().ToArray());
}

public class MapperTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData(0, null)]
    [InlineData(1, 1)]
    [InlineData(20, 1)]      // never 0 for a track that has a length
    [InlineData(226, 4)]
    [InlineData(330, 6)]
    public void ATrackLength_IsInWholeMinutes(int? seconds, int? minutes) => Assert.Equal(minutes, MetadataMapper.MinutesOf(seconds));

    [Fact]
    public void TheAlternateTitles_AreTheTitleWithoutItsEditionAndDeezersShortForm()
    {
        Assert.Equal(["Doubt"], MetadataMapper.AlternateTitles("Doubt (Deluxe Edition)"));
        Assert.Equal(["Pon de Replay"], MetadataMapper.AlternateTitles("Pon de Replay (Remastered 2015)", "Pon de Replay"));
        Assert.Empty(MetadataMapper.AlternateTitles("Doubt"));
        Assert.Empty(MetadataMapper.AlternateTitles("Doubt", "doubt"));
    }

    [Fact]
    public void AnAlbumFromASearch_HasAPosterBuiltFromItsHash_WhenItCarriesNoUrl()
    {
        var doc = new AlbumDoc { Id = 5, Title = "X", Md5Image = "abc123", Artist = new ArtistDoc { Id = 1, Name = "Y" } };
        var meta = MetadataMapper.AlbumFromSearch(doc, 1000);

        Assert.Equal("https://cdn-images.dzcdn.net/images/cover/abc123/1000x1000-000000-80-0-0.jpg", meta.PosterUrl);
        Assert.Equal("album:5", meta.ExternalId);
        Assert.Equal("Y", meta.Cast.Single().Name);
    }

    [Fact]
    public void ContributorsKeepTheirRoles_AndTheCreditedArtistIsUsedWhenThereAreNone()
    {
        var withContributors = new AlbumDoc
        {
            Id = 1, Title = "X", Artist = new ArtistDoc { Id = 1, Name = "Main One" },
            Contributors = [new ArtistDoc { Id = 1, Name = "Main One", Role = "Main" }, new ArtistDoc { Id = 2, Name = "Guest", Role = "Featured" }],
        };
        var cast = MetadataMapper.Album(withContributors, 1000).Cast;
        Assert.Equal(["Main", "Featured"], cast.Select(c => c.Role).ToArray());
        Assert.Equal("artist:2", cast[1].ExternalPersonId);

        var withoutContributors = new AlbumDoc { Id = 1, Title = "X", Artist = new ArtistDoc { Id = 1, Name = "Main One" } };
        Assert.Equal("Main One", MetadataMapper.Album(withoutContributors, 1000).Cast.Single().Name);
    }

    [Fact]
    public void NothingIsInvented_NoOverview_NoRating_NoTags()
    {
        var album = MetadataMapper.Album(JsonSerializer.Deserialize<AlbumDoc>(FakeDeezer.Fixture("album_302127.json"))!, 1000);

        Assert.Null(album.Overview);
        Assert.Null(album.Rating);
        Assert.Empty(album.Tags);
    }

    [Fact]
    public void ATrackFromAnAlbumList_TakesTheAlbumsCoverAndYear()
    {
        var track = new TrackDoc { Id = 7, Title = "Song", Duration = 200, Album = new AlbumRef { Id = 3, Title = "A", CoverXl = "https://cdn-images.dzcdn.net/images/cover/h/1000x1000-000000-80-0-0.jpg", ReleaseDate = "1999-05-05" } };
        var meta = MetadataMapper.Track(track, null, 500);

        Assert.Equal(1999, meta.Year);
        Assert.Contains("/500x500-", meta.PosterUrl);
        Assert.Equal(3, meta.RuntimeMinutes);
    }
}

public class CacheAndManifestTests
{
    [Fact]
    public void AnEntry_IsRemembered_UntilItExpires()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new TtlCache<string>(TimeSpan.FromMinutes(10), clock: () => now);
        cache.Set("a", "one");

        Assert.True(cache.TryGet("a", out var fresh));
        Assert.Equal("one", fresh);

        now += TimeSpan.FromMinutes(9);
        Assert.True(cache.TryGet("a", out _));

        now += TimeSpan.FromMinutes(2);
        Assert.False(cache.TryGet("a", out _));
    }

    [Fact]
    public void TheCache_IsEmptiedWhenItGrowsTooLarge()
    {
        var cache = new TtlCache<int>(TimeSpan.FromMinutes(1), capacity: 3);
        cache.Set("a", 1); cache.Set("b", 2); cache.Set("c", 3);
        cache.Set("d", 4);

        Assert.False(cache.TryGet("a", out _));
        Assert.True(cache.TryGet("d", out _));
        Assert.Equal(1, cache.Count);
    }

    private static JsonElement Manifest() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "manifest.json"))).RootElement;

    [Fact]
    public void TheManifest_MatchesTheCode()
    {
        var provider = new DeezerMetadataProvider();
        var manifest = Manifest();

        Assert.Equal(provider.PluginId, manifest.GetProperty("plugin_id").GetString());
        Assert.Equal(provider.Name, manifest.GetProperty("name").GetString());
        Assert.Equal(provider.Version, manifest.GetProperty("version").GetString());
        Assert.Equal(provider.Author, manifest.GetProperty("author").GetString());
        Assert.Equal(typeof(DeezerMetadataProvider).FullName, manifest.GetProperty("entry_type").GetString());
        Assert.Equal(["music"], manifest.GetProperty("supported_media_types").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(["music"], provider.GetSupportedMediaTypes().Select(t => t.MediaTypeName).ToArray());
    }

    [Fact]
    public void TheProjectVersion_IsTheCodesVersion()
    {
        var csproj = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Chronicle.Plugin.Deezer.csproj"));
        Assert.Contains($"<Version>{new DeezerMetadataProvider().Version}</Version>", csproj);
    }

    [Fact]
    public void TheManifestTasks_AreTheHostsBuiltInHandlers_WithSensibleSchedules()
    {
        var tasks = Manifest().GetProperty("background_tasks").EnumerateArray().ToList();

        Assert.Equal(["fetch-missing-metadata", "resync-all-metadata"], tasks.Select(t => t.GetProperty("task_id").GetString()).ToArray());
        Assert.True(tasks[0].GetProperty("default_enabled").GetBoolean());
        Assert.False(tasks[1].GetProperty("default_enabled").GetBoolean());   // the weekly re-fetch of everything is opt-in
        Assert.All(tasks, t => Assert.Equal(5, t.GetProperty("default_cron").GetString()!.Split(' ').Length));
    }

    [Fact]
    public void TheFixMatchHint_NamesEveryFormTheParserAccepts()
    {
        var hint = Manifest().GetProperty("fixMatchHint").GetString()!;
        foreach (var form in new[] { "artist:", "album:", "track:", "deezer.com", "isrc:", "upc:" }) Assert.Contains(form, hint);
    }

    [Fact]
    public void TheMusicType_IsDeclaredAsTheOtherMusicPluginsDeclareIt()
    {
        var music = new DeezerMetadataProvider().GetSupportedMediaTypes().Single();

        Assert.Equal(3, music.HierarchyLevels);
        Assert.Equal(["Artist", "Album", "Track"], music.HierarchyLabels);
        Assert.Equal("listened", music.InteractionVerb);
        Assert.Equal(30, music.DefaultPriority);   // after MusicBrainz (10) and Last.fm (20): it fills what they lack
        Assert.False(music.EnrichOnly);
    }

    [Fact]
    public void NoLevelDeclaresAFieldDeezerNeverFills()
    {
        // Deezer has no overview, rating or tags: declaring one would put rows in a queue that can only fail.
        var music = new DeezerMetadataProvider().GetSupportedMediaTypes().Single();
        var declared = music.SupportedFields.Concat(music.LevelFields!.Values.SelectMany(f => f)).ToHashSet();

        Assert.DoesNotContain("overview", declared);
        Assert.DoesNotContain("rating", declared);
        Assert.DoesNotContain("tags", declared);
        Assert.Contains("poster_url", music.SupportedFields);
    }
}
