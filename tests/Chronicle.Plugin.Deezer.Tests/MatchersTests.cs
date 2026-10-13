using System.Text.Json;
using Chronicle.Plugin.Deezer;

namespace Chronicle.Plugin.Deezer.Tests;

public class MatchersTests
{
    private static readonly MatchSettings Settings = MatchSettings.Default;

    private static List<T> Load<T>(string fixture) => JsonSerializer.Deserialize<ListResponse<T>>(FakeDeezer.Fixture(fixture))!.Data;
    private static AlbumDoc Album(string fixture) => JsonSerializer.Deserialize<AlbumDoc>(FakeDeezer.Fixture(fixture))!;

    private static AlbumDoc Doc(string title, string artist, int tracks = 10, string type = "album", long id = 1) => new()
    {
        Id = id, Title = title, NbTracks = tracks, RecordType = type, Artist = new ArtistDoc { Id = 9, Name = artist },
    };

    private static int ScoreAlbum(AlbumDoc doc, string title, string? artist, int? childCount = null) =>
        Matchers.ScoreAlbum(doc, new AlbumQuery(title, artist, null, childCount), Settings);

    // ── titles ────────────────────────────────────────────────────────────────

    [Fact]
    public void TheSameTitle_ScoresSixty_AnEditionFiftyFive_AContinuationForty()
    {
        int Score(string library, string deezer) =>
            Matchers.TitleScore(TextRules.Analyse(library, true), TextRules.Analyse(deezer), out _);

        Assert.Equal(60, Score("Doubt", "Doubt"));
        Assert.Equal(60, Score("Doubt (Deluxe Edition)", "Doubt (Deluxe Edition)"));
        Assert.Equal(55, Score("Doubt", "Doubt (Deluxe Edition)"));
        Assert.Equal(55, Score("Doubt (2015 Remaster)", "Doubt"));
        Assert.Equal(40, Score("Everybody Else Is Doing It", "Everybody Else Is Doing It, So Why Can't We?"));
        Assert.Equal(0, Score("Doubt", "No Doubt"));          // appears inside: not a match
        Assert.Equal(0, Score("Mirror Image", "The Best of Isaac Asimov (Mirror-Image)"));
        Assert.Equal(0, Score("Doubt", "Heligoland"));
    }

    [Fact]
    public void ADifferentRecording_IsFlagged_AndNeverReachesTheGate()
    {
        Matchers.TitleScore(TextRules.Analyse("Don't (Don Diablo Remix)", true), TextRules.Analyse("Don't"), out var mismatch);
        Assert.True(mismatch);
        Matchers.TitleScore(TextRules.Analyse("Don't", true), TextRules.Analyse("Don't (Don Diablo Remix)"), out mismatch);
        Assert.True(mismatch);
        Matchers.TitleScore(TextRules.Analyse("Don't (Don Diablo Remix)", true), TextRules.Analyse("Don't (Don Diablo Remix)"), out mismatch);
        Assert.False(mismatch);

        var score = ScoreAlbum(Doc("Don't (Don Diablo Remix)", "Ed Sheeran", 1, "single"), "Don't", "Ed Sheeran");
        Assert.True(score is > 0 and < 50, score.ToString());
        Assert.True(ScoreAlbum(Doc("Don't (Don Diablo Remix)", "Ed Sheeran", 1, "single"), "Don't (Don Diablo Remix)", "Ed Sheeran") >= 90);
    }

    [Fact]
    public void ARadioEdit_IsTheSameSongAsTheLibrarySingle()
    {
        // A library single named "TKO" is the radio edit: that is the answer worth having, unlike a remix.
        Assert.True(ScoreAlbum(Doc("TKO (Radio Edit)", "Justin Timberlake", 1, "single"), "TKO", "Justin Timberlake") >= 80);
    }

    // ── artists must agree ────────────────────────────────────────────────────

    [Fact]
    public void AnotherArtistsVersion_IsNeverAtOrAboveTheGate()
    {
        // The same title by someone else is a cover, a karaoke or a tribute, however alike the title.
        Assert.True(ScoreAlbum(Doc("Africa", "Toto Tribute Band"), "Africa", "Toto") < 50);
        Assert.True(ScoreAlbum(Doc("Still Into You", "Dave Winkler", 1, "single"), "Still Into You", "Paramore") <= Matchers.ArtistMismatchCap);
    }

    [Fact]
    public void TheArtistsOfTheLibrary_AreMatchedThroughFolderNoise_AccentsAndBrokenCharacters()
    {
        Assert.True(ScoreAlbum(Doc("Bridge Over Troubled Water", "Simon & Garfunkel", 11), "Bridge Over Troubled Water", "Simon & Garfunkel discography") >= 90);
        Assert.True(ScoreAlbum(Doc("Greatest Hits Of The 20th Century", "Béla Fleck and the Flecktones", 11), "Greatest Hits of the 20th Century", "B?la Fleck and the Flecktones") >= 90);
    }

    [Fact]
    public void WithoutAnArtist_NothingReachesTheGate()
    {
        Assert.True(ScoreAlbum(Doc("Doubt", "Jesus Jones"), "Doubt", null) <= Matchers.NoArtistCap);
    }

    // ── junk ──────────────────────────────────────────────────────────────────

    [Fact]
    public void KaraokeAndTributeReleases_ScoreFarBelowTheReal_One()
    {
        var real = ScoreAlbum(Doc("It's My Life", "Bon Jovi", 3, "single"), "It's My Life", "Bon Jovi");
        var junk = ScoreAlbum(Doc("It's My Life (Karaoke Version)", "La-Le-Lu", 2, "single"), "It's My Life", "Bon Jovi");
        var junkSameArtist = ScoreAlbum(Doc("It's My Life", "Bon Jovi Karaoke Band", 1, "single"), "It's My Life", "Bon Jovi");

        Assert.True(real >= 90);
        Assert.True(junk < 50);
        Assert.True(junkSameArtist < 50);
    }

    [Fact]
    public void JunkIsNotHeldAgainstALibraryItemThatSaysSoItself()
    {
        var karaoke = Doc("Greatest Hits Karaoke", "Singer's Best");
        Assert.True(ScoreAlbum(karaoke, "Greatest Hits Karaoke", "Singer's Best") >= 90);
    }

    [Fact]
    public void ACompilation_IsPenalisedUnlessTheLibraryArtistIsVariousArtists()
    {
        var compile = Doc("Summer Hits", "Various Artists", 20, "compile");
        Assert.True(ScoreAlbum(compile, "Summer Hits", "Paramore") < 50);
        Assert.True(ScoreAlbum(compile, "Summer Hits", "Various Artists") >= 80);
    }

    [Fact]
    public void TheRecordedResultsOfRealSearches_ScoreAsTheyShould()
    {
        // Paramore "Still Into You": only covers by other artists exist as albums of that name; none is accepted.
        Assert.All(Load<AlbumDoc>("search_album_still_into_you.json"),
            d => Assert.True(ScoreAlbum(d, "Still Into You", "Paramore") < 50, $"{d.Title} / {d.Artist?.Name}"));

        // Bon Jovi "It's My Life": the real single is accepted, the other 9 results (covers, karaoke, techno) are not.
        var results = Load<AlbumDoc>("search_album_its_my_life.json")
            .Select(d => (Doc: d, Score: ScoreAlbum(d, "It's My Life", "Bon Jovi"))).ToList();
        Assert.Single(results, r => r.Score >= 50);
        Assert.Equal("Bon Jovi", results.Single(r => r.Score >= 50).Doc.Artist!.Name);

        // Ghost "Prequelle" is not on Deezer: nothing in the title-only search is accepted.
        Assert.All(Load<AlbumDoc>("search_album_prequelle_title_only.json"),
            d => Assert.True(ScoreAlbum(d, "Prequelle", "Ghost") < 50, $"{d.Title} / {d.Artist?.Name}"));
    }

    // ── editions ──────────────────────────────────────────────────────────────

    [Fact]
    public void TheEditionWithTheLibrarysTrackCount_Wins()
    {
        var docs = Load<AlbumDoc>("search_album_doubt.json");
        var eleven = docs.Single(d => d.NbTracks == 11);
        var twentyOne = docs.Single(d => d.NbTracks == 21);

        Assert.True(ScoreAlbum(eleven, "Doubt", "Jesus Jones", 11) > ScoreAlbum(twentyOne, "Doubt", "Jesus Jones", 11));
        Assert.True(ScoreAlbum(twentyOne, "Doubt", "Jesus Jones", 21) > ScoreAlbum(eleven, "Doubt", "Jesus Jones", 21));
    }

    [Fact]
    public void ATrackListThatMatchesWhatTheLibraryHolds_RaisesTheAlbum_AndOneThatDoesNotLowersIt()
    {
        var doubt = Album("album_1499088.json");   // the 11-track edition
        var names = doubt.Tracks!.Data.Take(8).Select(t => t.Title).ToList();
        var query = new AlbumQuery("Doubt", "Jesus Jones", null, 11, names);

        Assert.True(Matchers.EditionBonus(doubt, query, Settings) >= 30);

        var wrongTracks = new AlbumQuery("Doubt", "Jesus Jones", null, 8, ["Alpha", "Beta", "Gamma", "Delta", "Epsilon", "Zeta"]);
        Assert.True(Matchers.EditionBonus(doubt, wrongTracks, Settings) < 0);
    }

    [Fact]
    public void TheEditionBonus_IsBoundedAndNeedsATrackList()
    {
        var query = new AlbumQuery("Doubt", "Jesus Jones", null, 11, ["Trust Me"]);
        Assert.Equal(0, Matchers.EditionBonus(new AlbumDoc { Id = 1, Title = "Doubt" }, query, Settings));
        Assert.InRange(Matchers.EditionBonus(Album("album_1499088.json"), query, Settings), -25, 45);
    }

    [Fact]
    public void Durations_ChooseAnEdition_WhenNoNamesAreKnown()
    {
        var doubt = Album("album_1499088.json");
        var real = doubt.Tracks!.Data.Select(t => t.Duration!.Value + 2).ToList();   // two seconds off each: within the tolerance
        var wrong = doubt.Tracks!.Data.Select(t => t.Duration!.Value + 1000).ToList();

        Assert.True(Matchers.EditionBonus(doubt, new AlbumQuery("Doubt", "Jesus Jones", null, null, null, real), Settings) >= 9);
        Assert.Equal(0, Matchers.EditionBonus(doubt, new AlbumQuery("Doubt", "Jesus Jones", null, null, null, wrong), Settings));
        var small = new AlbumDoc { Id = 1, Title = "T", Tracks = new ListResponse<TrackDoc> { Data = [new TrackDoc { Id = 1, Title = "a", Duration = 100 }, new TrackDoc { Id = 2, Title = "b", Duration = 200 }] } };
        var offByTwo = new AlbumQuery("T", "X", null, null, null, [102, 202]);
        Assert.Equal(10, Matchers.EditionBonus(small, offByTwo, Settings));                                   // within the 5 second tolerance
        Assert.Equal(0, Matchers.EditionBonus(small, offByTwo, Settings with { DurationToleranceSeconds = 1 }));
    }

    // ── artists ───────────────────────────────────────────────────────────────

    [Fact]
    public void TheArtistWithTheNameAndTheFans_Wins()
    {
        var results = Load<ArtistDoc>("search_artist_journey.json").Select(d => (Doc: d, Score: Matchers.ScoreArtist(d, "Journey", Settings))).ToList();

        Assert.All(results.Where(r => r.Doc.Name != "Journey"), r => Assert.Equal(0, r.Score));   // Arizona Journey, AFK Journey ...
        var journeys = results.Where(r => r.Doc.Name == "Journey").OrderByDescending(r => r.Score).ToList();
        Assert.Equal(2, journeys.Count);
        Assert.True(journeys[0].Doc.NbFan > 600_000);
        Assert.True(journeys[0].Score - journeys[1].Score >= 10);
    }

    [Fact]
    public void ASimilarArtistName_IsNotTheArtist()
    {
        var results = Load<ArtistDoc>("search_artist_dream_theater.json");
        Assert.DoesNotContain(results.Where(d => Matchers.ScoreArtist(d, "Dream Theater", Settings) > 0), d => d.Name == "Dream Home Theater");
        Assert.Contains(results, d => Matchers.ScoreArtist(d, "Dream Theater", Settings) >= 75 && d.NbFan > 500_000);
    }

    // ── tracks ────────────────────────────────────────────────────────────────

    private static TrackDoc TrackDocOf(string title, string artist, string album, long id = 5) => new()
    {
        Id = id, Title = title, Artist = new ArtistDoc { Id = 1, Name = artist }, Album = new AlbumRef { Id = 2, Title = album },
    };

    [Fact]
    public void ATrackBySomeoneElse_OrAnotherRecording_IsNotTheTrack()
    {
        var q = new TrackQuery("Pon de Replay", "Rihanna", "Music Of The Sun", 1);
        Assert.True(Matchers.ScoreTrack(TrackDocOf("Pon de Replay", "Rihanna", "Music Of The Sun"), q, Settings) >= 95);
        Assert.True(Matchers.ScoreTrack(TrackDocOf("Pon de Replay", "Chillounga", "Acoustic Lounge Unplugged: The Songs of Rihanna"), q, Settings) < 50);
        Assert.True(Matchers.ScoreTrack(TrackDocOf("Pon De Replay (Remix)", "Rihanna", "Music Of The Sun"), q, Settings) < 50);
        Assert.True(Matchers.ScoreTrack(TrackDocOf("Pon de Replay (Karaoke Version)", "Metro Karaoke Classics", "Karaoke Hits"), q, Settings) < 50);
    }

    [Fact]
    public void TheRecordedTrackSearch_AcceptsOnlyRihannasOwnRecording()
    {
        var q = new TrackQuery("Pon de Replay", "Rihanna", null, null);
        var accepted = Load<TrackDoc>("search_track_pon_de_replay.json").Where(d => Matchers.ScoreTrack(d, q, Settings) >= 50).ToList();

        Assert.NotEmpty(accepted);
        Assert.All(accepted, d => Assert.Equal("Rihanna", d.Artist!.Name));
        Assert.All(accepted, d => Assert.Equal("Pon de Replay", d.Title));
    }

    [Fact]
    public void ATrackIsFoundInsideItsAlbum_ByTitle_AndPositionBreaksATie()
    {
        var discovery = Album("album_302127.json");

        var (track, score) = Matchers.FindTrackInAlbum(discovery, new TrackQuery("One More Time", "Daft Punk", "Discovery", 1));
        Assert.Equal("One More Time", track!.Title);
        Assert.True(score >= 95);   // 60 title + 30 album's artist + 10 for being first on the album
        Assert.Equal("Aerodynamic", Matchers.FindTrackInAlbum(discovery, new TrackQuery("Aerodynamic", "Daft Punk", "Discovery", 2)).Track!.Title);

        var (none, zero) = Matchers.FindTrackInAlbum(discovery, new TrackQuery("A Song That Is Not There", "Daft Punk", "Discovery", 1));
        Assert.Null(none);
        Assert.Equal(0, zero);

        var twin = new AlbumDoc
        {
            Id = 1, Title = "Twins", Tracks = new ListResponse<TrackDoc>
            {
                Data = [new TrackDoc { Id = 10, Title = "Intro", TrackPosition = 1 }, new TrackDoc { Id = 11, Title = "Intro", TrackPosition = 7 }],
            },
        };
        Assert.Equal(11, Matchers.FindTrackInAlbum(twin, new TrackQuery("Intro", "X", "Twins", 7)).Track!.Id);
        Assert.Equal(10, Matchers.FindTrackInAlbum(twin, new TrackQuery("Intro", "X", "Twins", 1)).Track!.Id);
    }

    [Fact]
    public void ARemixOnTheAlbum_IsNotTheLibrarysPlainTrack()
    {
        var album = new AlbumDoc { Id = 1, Title = "A", Tracks = new ListResponse<TrackDoc> { Data = [new TrackDoc { Id = 10, Title = "Song (Club Remix)", TrackPosition = 3 }] } };
        var (track, score) = Matchers.FindTrackInAlbum(album, new TrackQuery("Song", "X", "A", 3));
        Assert.NotNull(track);
        Assert.True(score < 50);
    }

    [Fact]
    public void VariousArtists_IsRecognised()
    {
        Assert.True(Matchers.IsVariousArtists("Various Artists"));
        Assert.True(Matchers.IsVariousArtists("various artists"));
        Assert.False(Matchers.IsVariousArtists("Various"));
        Assert.False(Matchers.IsVariousArtists(null));
    }

    // ── found by measuring 330 real library items against the live service (2026-10-13) ────────────────────────────────

    [Theory]
    [InlineData("Get Born Live", "Get Born (Expanded Edition)")]      // another release: the live album, not the studio one
    [InlineData("Doubt Live", "Doubt")]
    public void ALibraryTitleThatRunsOnInTheSameBreath_IsAnotherRelease(string library, string deezer) =>
        Assert.Equal(0, Matchers.TitleScore(TextRules.Analyse(library, true), TextRules.Analyse(deezer), out _));

    [Fact]
    public void TrailingEditionWords_AreEditions_NotAnotherRelease() =>
        Assert.Equal(55, Matchers.TitleScore(TextRules.Analyse("Heligoland Deluxe Remastered", true), TextRules.Analyse("Heligoland"), out _));

    [Theory]
    [InlineData("Jumper", "Jumper / Graduate")]
    [InlineData("Moving Pictures", "Moving Pictures: Live in Tokyo")]
    [InlineData("Everybody Else Is Doing It", "Everybody Else Is Doing It, So Why Can't We?")]
    public void ATitleContinuedAfterASeparator_IsStillTheRelease(string library, string deezer) =>
        Assert.Equal(40, Matchers.TitleScore(TextRules.Analyse(library, true), TextRules.Analyse(deezer), out _));

    [Fact]
    public void WhatAnAlbumsTracksSay_CanRaiseAScore_ButNeverPastACap()
    {
        // "Jumper (Radio Edit) / Graduate (Remix)" is a different recording of the right song: capped under the gate, and the
        // 2-track count and the matching track names (the edition bonus) must not lift it out of that.
        var doc = Doc("Jumper (Radio Edit) / Graduate (Remix)", "Third Eye Blind", 2, "single");
        var (raw, cap) = Matchers.ScoreAlbumParts(doc, new AlbumQuery("Jumper", "Third Eye Blind", null, 2), Settings);

        Assert.True(raw > 50);
        Assert.Equal(Matchers.RecordingMismatchCap, cap);
        Assert.True(Matchers.ScoreAlbum(doc, new AlbumQuery("Jumper", "Third Eye Blind", null, 2), Settings) <= cap);
    }

    [Fact]
    public void ATrackOfALiveAlbum_IsNotTheStudioTrackOfTheSameName()
    {
        var studio = TrackDocOf("Breathe (In the Air)", "Pink Floyd", "The Dark Side of the Moon");
        var live = TrackDocOf("Breathe (In the Air)", "Pink Floyd", "Live at Earls Court, London");

        Assert.True(Matchers.ScoreTrack(studio, new TrackQuery("Breathe (in the Air)", "Pink Floyd", "Live at Earls Court, London, UK, 1994", 1), Settings) < 50);
        Assert.True(Matchers.ScoreTrack(live, new TrackQuery("Breathe (in the Air)", "Pink Floyd", "Live at Earls Court, London, UK, 1994", 1), Settings) >= 50);
        Assert.True(Matchers.ScoreTrack(studio, new TrackQuery("Breathe (in the Air)", "Pink Floyd", "The Dark Side of the Moon", 1), Settings) >= 90);
        Assert.True(TextRules.IsLiveRelease("MTV Unplugged"));
        Assert.True(TextRules.IsLiveRelease("Live at Wembley"));
        Assert.False(TextRules.IsLiveRelease("Alive"));
        Assert.False(TextRules.IsLiveRelease("Olive Garden"));
    }

    [Fact]
    public void AnEpWithTheSameName_IsTheLibrarysRelease()
    {
        Assert.True(ScoreAlbum(Doc("Volcano Girls EP", "Veruca Salt", 4, "ep"), "Volcano Girls", "Veruca Salt", 4) >= 90);
        Assert.True(ScoreAlbum(Doc("Heligoland Deluxe", "Massive Attack", 14), "Heligoland", "Massive Attack") >= 80);
    }

    [Fact]
    public void ALiveAlbumsTrack_IsTheLiveRecording_EvenWhenItsTitleDoesNotSayLive()
    {
        var live = new TrackDoc { Id = 1, Title = "Horror Show (Live)", Artist = new ArtistDoc { Id = 1, Name = "Third Eye Blind" }, Album = new AlbumRef { Id = 2, Title = "Horror Show (Live)" } };
        var q = new TrackQuery("Horror Show", "Third Eye Blind", "Summer Gods Tour Live 2017", 3);
        Assert.True(Matchers.ScoreTrack(live, q, Settings) >= 50);

        // ...but not the studio album's, and not a live take when the library's album is a studio one.
        var studio = new TrackDoc { Id = 3, Title = "Horror Show", Artist = new ArtistDoc { Id = 1, Name = "Third Eye Blind" }, Album = new AlbumRef { Id = 4, Title = "Out of the Vein" } };
        Assert.True(Matchers.ScoreTrack(studio, q, Settings) < 50);
        Assert.True(Matchers.ScoreTrack(live, new TrackQuery("Horror Show", "Third Eye Blind", "Out of the Vein", 3), Settings) < 50);
    }
}
