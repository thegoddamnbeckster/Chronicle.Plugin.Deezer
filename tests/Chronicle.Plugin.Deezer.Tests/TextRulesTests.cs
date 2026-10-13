using Chronicle.Plugin.Deezer;

namespace Chronicle.Plugin.Deezer.Tests;

public class TextRulesTests
{
    private static readonly IReadOnlyList<string> Noise = MatchSettings.DefaultArtistNoise;

    // ── normalising and the library's broken characters ───────────────────────

    [Theory]
    [InlineData("Simon & Garfunkel", "Simon and Garfunkel")]
    [InlineData("The Beatles", "Beatles")]
    [InlineData("Béla Fleck", "Bela Fleck")]
    [InlineData("AC/DC", "AC DC")]
    [InlineData("Don't Stop", "Dont Stop")]
    public void TheSameNameWrittenDifferently_HasOneKey(string a, string b) => Assert.Equal(TextRules.Normalise(a), TextRules.Normalise(b));

    [Theory]
    [InlineData("B?la Fleck", "Béla Fleck")]
    [InlineData("Don?t Stop", "Don't Stop")]
    [InlineData("blink?182", "blink-182")]
    [InlineData("blink?182", "blink182")]
    [InlineData("Everybody Else Is Doing It, So Why Can�t We", "Everybody Else Is Doing It, So Why Can't We")]
    public void ABrokenCharacterInTheLibrary_MatchesTheOneDeezerHas(string library, string deezer) =>
        Assert.True(TextRules.KeyEquals(TextRules.Normalise(library, libraryText: true), TextRules.Normalise(deezer)), library);

    [Fact]
    public void ABrokenCharacter_DoesNotMakeEverythingMatch()
    {
        Assert.False(TextRules.KeyEquals(TextRules.Normalise("B?la Fleck", true), TextRules.Normalise("Bob Fleck")));
        Assert.False(TextRules.KeyEquals(TextRules.Normalise("B?la Fleck", true), TextRules.Normalise("Bla Flick")));
    }

    [Fact]
    public void ARealQuestionMarkInDeezersText_IsPunctuation_NotAWildcard()
    {
        // "Who? Where? Why?" is a real title; only the library's text is read with wildcards, and only inside a word.
        Assert.Equal(TextRules.Normalise("Who Where Why"), TextRules.Normalise("Who? Where? Why?"));
        Assert.Equal(TextRules.Normalise("Who Where Why"), TextRules.Normalise("Who? Where? Why?", libraryText: true));
    }

    [Fact]
    public void Continuation_IsNotContainment()
    {
        var doubt = TextRules.Normalise("Doubt");
        Assert.True(TextRules.ProviderContinues(doubt, TextRules.Normalise("Doubt (Deluxe)")));
        Assert.False(TextRules.ProviderContinues(doubt, TextRules.Normalise("No Doubt")));       // appears inside, does not begin with
        Assert.False(TextRules.ProviderContinues(doubt, doubt));
        Assert.True(TextRules.LibraryContinues(TextRules.Normalise("Doubt Deluxe"), doubt));
    }

    // ── edition and recording qualifiers ──────────────────────────────────────

    [Theory]
    [InlineData("Doubt (Deluxe Edition)", "Doubt")]
    [InlineData("The Edges Of Twilight (2015 Remaster)", "The Edges Of Twilight")]
    [InlineData("Paramore (Deluxe Edition) [2013]", "Paramore")]
    [InlineData("Heligoland (Bonus Track Version)", "Heligoland")]
    [InlineData("Come Back Clean - Remastered 2009", "Come Back Clean")]
    [InlineData("Bridge Over Troubled Water - 40th Anniversary Edition", "Bridge Over Troubled Water")]
    [InlineData("Sad Song (feat. Someone)", "Sad Song")]
    [InlineData("Rush (1991)", "Rush")]
    public void EditionAndCreditQualifiers_AreNotPartOfTheName(string title, string plain) => Assert.Equal(plain, TextRules.Analyse(title).Plain);

    [Theory]
    [InlineData("Don't (Don Diablo Remix)")]
    [InlineData("Sad Song (Mauldeth Road West demo, 2024)")]
    [InlineData("Africa (RAC Remix)")]
    [InlineData("It's My Life (Orchestral Version)")]
    [InlineData("Supersonic - Live at Wembley")]
    [InlineData("Song [Acoustic]")]
    public void RecordingQualifiers_ChangeWhichRecordingItIs(string title) => Assert.NotEqual("", TextRules.Analyse(title).Recording);

    [Theory]
    [InlineData("Doubt")]
    [InlineData("Doubt (Deluxe Edition)")]
    [InlineData("Song (feat. Someone)")]
    [InlineData("Song (Remastered 2015)")]
    [InlineData("TKO (Radio Edit)")]
    [InlineData("TKO - Single Version")]
    [InlineData("Song (Album Version)")]
    [InlineData("Song (Original Mix)")]
    public void EditionAndCreditQualifiers_DoNotChangeTheRecording(string title) => Assert.Equal("", TextRules.Analyse(title).Recording);

    [Theory]
    [InlineData("Volcano Girls EP", "Volcano Girls")]
    [InlineData("Heligoland Deluxe", "Heligoland")]
    [InlineData("Heligoland Deluxe Edition", "Heligoland")]
    [InlineData("Doubt Remastered 2015", "Doubt")]
    [InlineData("Doubt (Deluxe) EP", "Doubt")]
    public void ATrailingEditionWord_WithoutBrackets_IsNotPartOfTheName(string title, string plain) => Assert.Equal(plain, TextRules.Analyse(title).Plain);

    [Theory]
    [InlineData("The EP")]            // never reduced to nothing
    [InlineData("Single Ladies")]     // a leading word is not an edition
    [InlineData("Get Born Live")]     // live is not an edition: it is another release
    public void AnEditionWordThatIsPartOfTheTitle_Stays(string title) => Assert.Contains(title.Split(' ')[^1], TextRules.Analyse(title).Plain.Split(' '));

    [Fact]
    public void AGroupThatIsNeitherEditionNorRecording_StaysPartOfTheName()
    {
        Assert.Equal("Hot Sauce Committee (Pt. 2)", TextRules.Analyse("Hot Sauce Committee (Pt. 2)").Plain);
        Assert.NotEqual(TextRules.Analyse("Hot Sauce Committee").Core, TextRules.Analyse("Hot Sauce Committee (Pt. 2)").Core);
    }

    [Fact]
    public void TheSameRecordingQualifier_IsRecognisedWhateverItsCase_AndFromLibraryText()
    {
        Assert.Equal(TextRules.Analyse("Don't (Don Diablo Remix)").Recording, TextRules.Analyse("Don?t (Don Diablo remix)", libraryText: true).Recording);
    }

    // ── artist names ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Simon & Garfunkel discography", "Simon & Garfunkel")]
    [InlineData("Beastie Boys discography", "Beastie Boys")]
    [InlineData("The Chemical Brothers - Discography", "The Chemical Brothers")]
    [InlineData("Pink Floyd (Complete Discography)", "Pink Floyd")]
    [InlineData("Queen Greatest Hits", "Queen")]
    [InlineData("Weezer (1994)", "Weezer")]
    [InlineData("Journey", "Journey")]
    [InlineData("Collection", "Collection")]   // never reduced to nothing
    public void FolderNoise_IsRemovedFromAnArtistName(string library, string expected) => Assert.Equal(expected, TextRules.CleanArtistName(library, Noise));

    [Fact]
    public void AnAlbumKeepsTheWordsAnArtistLoses()
    {
        // CleanArtistName is only ever applied to artists: an album's own title is analysed, never cleaned.
        Assert.Equal("Greatest Hits", TextRules.Analyse("Greatest Hits").Plain);
        Assert.Equal("Discography", TextRules.Analyse("Discography").Plain);
    }

    [Theory]
    [InlineData("Simon & Garfunkel discography", "Simon & Garfunkel", true)]
    [InlineData("Simon & Garfunkel", "Simon and Garfunkel", true)]
    [InlineData("B?la Fleck and the Flecktones", "Béla Fleck and the Flecktones", true)]
    [InlineData("Dream Theater", "Dream Home Theater", false)]
    [InlineData("Journey", "Arizona Journey", false)]
    [InlineData("Eric Clapton", "Eric Clapton Tribute Band", false)]
    public void SameArtist_NeedsAnEqualName(string library, string deezer, bool expected) => Assert.Equal(expected, TextRules.SameArtist(library, deezer, Noise));

    // ── junk ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("TKO (Karaoke Version)", true)]
    [InlineData("Fast Car: Tribute & Karaoke to Jon", true)]
    [InlineData("GHOSTTOWN (TECHNO SPED UP)", true)]
    [InlineData("Ghosttown (8D Audio)", true)]
    [InlineData("Ghosttown (Nightcore)", true)]
    [InlineData("Lullaby Versions of Metallica", true)]
    [InlineData("Metro Karaoke Classics", true)]
    [InlineData("Ozzy Osbourne the Prince of Darkness (In the Style of)", true)]
    [InlineData("Tributary", false)]                 // whole words only
    [InlineData("Nightcore Radio", true)]
    [InlineData("Doubt", false)]
    [InlineData("Heligoland", false)]
    public void Junk_IsFoundAsWholeWordsOrPhrases(string text, bool expected) =>
        Assert.Equal(expected, TextRules.ContainsJunk(text, MatchSettings.DefaultJunkTerms));

    [Fact]
    public void AnEditedList_IsWhatIsUsed()
    {
        Assert.False(TextRules.ContainsJunk("TKO (Karaoke Version)", ["nightcore"]));
        Assert.True(TextRules.ContainsJunk("A Brand New Kind of Junk", ["brand new kind"]));
        Assert.False(TextRules.ContainsJunk("anything", []));
    }

    [Fact]
    public void ParseList_TakesLinesAndCommas_DropsBlanksAndRepeats()
    {
        Assert.Equal(["karaoke", "tribute", "8d audio"], TextRules.ParseList("karaoke\r\n\r\n tribute,\n8d audio\nKaraoke"));
        Assert.Empty(TextRules.ParseList(null));
        Assert.Empty(TextRules.ParseList("  \n "));
    }

    // ── images and dates ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://cdn-images.dzcdn.net/images/cover/5718f7c81c27e0b2417e2a4c45224f8a/1000x1000-000000-80-0-0.jpg", 1900,
        "https://cdn-images.dzcdn.net/images/cover/5718f7c81c27e0b2417e2a4c45224f8a/1900x1900-000000-80-0-0.jpg")]
    [InlineData("https://cdn-images.dzcdn.net/images/artist/638e69b9caaf9f9f3f8826febea7b543/500x500-000000-80-0-0.jpg", 250,
        "https://cdn-images.dzcdn.net/images/artist/638e69b9caaf9f9f3f8826febea7b543/250x250-000000-80-0-0.jpg")]
    [InlineData("https://api.deezer.com/album/302127/image", 1000, "https://api.deezer.com/album/302127/image")]   // not a sized address: left alone
    public void ImageUrls_AreAskedAtTheChosenSize(string url, int size, string expected) => Assert.Equal(expected, TextRules.ImageAtSize(url, size));

    [Fact]
    public void ImageUrls_CanBeBuiltFromTheHash()
    {
        Assert.Equal("https://cdn-images.dzcdn.net/images/cover/abc123/1900x1900-000000-80-0-0.jpg", TextRules.ImageFromHash("abc123", "cover", 1900));
        Assert.Null(TextRules.ImageFromHash("", "cover", 1900));
        Assert.Null(TextRules.ImageAtSize(null, 1900));
    }

    [Theory]
    [InlineData("2001-03-07", 2001)]
    [InlineData("1991-01-28", 1991)]
    [InlineData("0000-00-00", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("abc", null)]
    public void YearOf_ReadsTheStartOfADate(string? date, int? expected) => Assert.Equal(expected, TextRules.YearOf(date));
}
