using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.Deezer.Tests;

public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DEEZER_LIVE") != "1") Skip = "Set DEEZER_LIVE=1 to run against the real Deezer.";
    }
}

public sealed class LiveTheoryAttribute : TheoryAttribute
{
    public LiveTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("DEEZER_LIVE") != "1") Skip = "Set DEEZER_LIVE=1 to run against the real Deezer.";
    }
}

/// <summary>
/// Runs only when DEEZER_LIVE=1: talks to the real api.deezer.com, paced as in production, to prove the queries, the field names
/// and the matching work on today's data and not just on the recorded fixtures. The items are real ones from the library.
/// </summary>
public class LiveTests
{
    private static DeezerMetadataProvider Real()
    {
        var provider = new DeezerMetadataProvider();
        provider.Configure(new Dictionary<string, string>());
        return provider;
    }

    private static MediaSearchContext Artist(string name) => new(name, HierarchyLevel: 0, IsRealHierarchyPosition: true, MediaTypeName: "music");

    private static MediaSearchContext Album(string title, string artist, int? children = null) =>
        new(title, ParentName: artist, HierarchyLevel: 1, IsRealHierarchyPosition: true, MediaTypeName: "music", ChildCount: children);

    private static MediaSearchContext Track(string title, string album, string artist, int? number = null) =>
        new(title, ParentName: album, GrandparentName: artist, ItemNumber: number, HierarchyLevel: 2, IsRealHierarchyPosition: true, MediaTypeName: "music");

    [LiveFact]
    public async Task HealthCheck_IsTrue() => Assert.True(await Real().HealthCheckAsync());

    [LiveTheory]
    [InlineData("Journey")]
    [InlineData("Daft Punk")]
    [InlineData("Slipknot")]
    [InlineData("Simon & Garfunkel discography")]
    [InlineData("B?la Fleck and the Flecktones")]
    public async Task TheLibraryArtists_AreFound_WithAPicture(string name)
    {
        var results = await Real().SearchAsync(Artist(name));
        Assert.NotEmpty(results);
        Assert.True(results[0].Score >= 60, name);
        Assert.NotNull(results[0].Metadata.PosterUrl);
    }

    [LiveTheory]
    [InlineData("Tracy Chapman", "Tracy Chapman")]
    [InlineData("Nine Inch Nails", "March of the Pigs")]
    [InlineData("Rush", "Roll the Bones")]
    [InlineData("Incubus", "8")]
    [InlineData("Herbie Hancock", "Mr. Hands")]
    [InlineData("Jesus Jones", "Doubt")]
    [InlineData("Bon Jovi", "It's My Life (2003)")]
    [InlineData("Skrillex", "Supersonic (My Existence)")]
    [InlineData("Simon & Garfunkel discography", "Bridge Over Troubled Water")]
    public async Task TheLibraryAlbums_AreFound(string artist, string title)
    {
        var results = await Real().SearchAsync(Album(title, artist));
        Assert.True(results.Count > 0 && results[0].Score >= 50, $"{artist} / {title}");
        Assert.NotNull(results[0].Metadata.PosterUrl);
    }

    [LiveTheory]
    [InlineData("Ghost", "Prequelle")]
    [InlineData("Ludacris", "Blow It Out")]
    [InlineData("Phil Collins", "One More Night (single edit by someone else)")]
    public async Task AlbumsDeezerDoesNotHaveByThatArtist_AreNotMatchedToSomeoneElses(string artist, string title)
    {
        var results = await Real().SearchAsync(Album(title, artist));
        Assert.DoesNotContain(results, r => r.Score >= 50 && !r.Metadata.Cast.Any(c => c.Name.Contains(artist, StringComparison.OrdinalIgnoreCase)));
    }

    [LiveFact]
    public async Task ATrack_IsFoundInsideItsAlbum_WithItsIsrcOnTheFullRecord()
    {
        var provider = Real();
        var results = await provider.SearchAsync(Track("Aerodynamic", "Discovery", "Daft Punk", 2));

        Assert.True(results[0].Score >= 90);
        var full = await provider.GetByIdAsync(results[0].Metadata.ExternalId);
        Assert.Equal("Aerodynamic", full.Title);
        Assert.False(string.IsNullOrEmpty(full.ExtendedData!.Value.GetProperty("isrc").GetString()));
        Assert.NotEmpty(full.Genres);
    }

    [LiveFact]
    public async Task TheCoverAtTheLargestSize_IsReallyServed()
    {
        var provider = Real();
        var album = await provider.GetByIdAsync("album:302127");

        Assert.Contains("/1900x1900-", album.PosterUrl);
        var bytes = await provider.GetImageAsync(album.PosterUrl!);
        Assert.True(bytes.Length > 100_000);
    }

    [LiveFact]
    public async Task AnIsrc_AndAUpc_ResolveToTheSameThingsAsTheirIds()
    {
        var provider = Real();
        Assert.Equal("Harder, Better, Faster, Stronger", (await provider.GetByIdAsync("isrc:GBDUW0000059")).Title);
        Assert.Equal("Discovery", (await provider.GetByIdAsync("upc:724384960650")).Title);
    }
}
