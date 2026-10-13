using Chronicle.Plugin.Deezer;

namespace Chronicle.Plugin.Deezer.Tests;

public class DzRefTests
{
    [Theory]
    [InlineData("artist:27", "Artist", "27")]
    [InlineData("album:302127", "Album", "302127")]
    [InlineData("track:3135556", "Track", "3135556")]
    [InlineData("deezer:album:302127", "Album", "302127")]
    [InlineData("Album: 302127", "Album", "302127")]
    [InlineData("https://www.deezer.com/album/302127", "Album", "302127")]
    [InlineData("https://www.deezer.com/us/album/302127", "Album", "302127")]
    [InlineData("https://www.deezer.com/en-us/artist/27?utm_source=x", "Artist", "27")]
    [InlineData("https://www.deezer.com/fr/track/3135556", "Track", "3135556")]
    [InlineData("https://api.deezer.com/album/302127", "Album", "302127")]
    [InlineData("isrc:GBDUW0000059", "Isrc", "GBDUW0000059")]
    [InlineData("ISRC:gb-duw-00-00059", "Isrc", "GBDUW0000059")]
    [InlineData("GBDUW0000059", "Isrc", "GBDUW0000059")]
    [InlineData("upc:724384960650", "Upc", "724384960650")]
    [InlineData("deezer:upc: 7243-8496-0650", "Upc", "724384960650")]
    public void WhatIsRecognised(string text, string kind, string value)
    {
        var reference = DzRef.Parse(text);
        Assert.NotNull(reference);
        Assert.Equal(kind, reference!.Kind.ToString());
        Assert.Equal(value, reference.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("27")]                     // Deezer's numbers overlap across kinds: refused as ambiguous
    [InlineData("302127")]
    [InlineData("artist:abc")]
    [InlineData("artist:0")]
    [InlineData("playlist:123")]
    [InlineData("isrc:123")]
    [InlineData("upc:12")]
    [InlineData("upc:abc")]
    [InlineData("https://www.example.com/album/302127")]
    [InlineData("tt0903747")]
    [InlineData("musicbrainz:2386cd66-e923-4e8e-bf14-2eebe2e9b973")]
    public void SomethingElse_IsRefused(string? text) => Assert.Null(DzRef.Parse(text));

    [Theory]
    [InlineData("album:302127", "album:302127")]
    [InlineData("https://www.deezer.com/us/track/3135556", "track:3135556")]
    [InlineData("isrc:gbduw0000059", "isrc:GBDUW0000059")]
    [InlineData("upc:724384960650", "upc:724384960650")]
    public void TheStoredForm_CarriesTheKind(string text, string stored) => Assert.Equal(stored, DzRef.Parse(text)!.Stored);
}
