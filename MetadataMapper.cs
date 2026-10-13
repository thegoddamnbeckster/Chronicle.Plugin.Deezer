using System.Text.Json;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.Deezer;

/// <summary>
/// Turns Deezer records into Chronicle's metadata shape. Nothing is invented: Deezer has no overview, rating or tags, so none is
/// made up ("fans" and "rank" are popularity, not a score, and stay in ExtendedData). Everything Deezer supplied that has no
/// first-class field is kept in ExtendedData. Not kept: the preview link (signed, expires within hours) and the track token.
/// </summary>
internal static class MetadataMapper
{
    public const string Source = "deezer";

    // ── artists ───────────────────────────────────────────────────────────────

    public static MediaMetadata Artist(ArtistDoc doc, int imageSize) => new()
    {
        ExternalId = "artist:" + doc.IdText,
        Source = Source,
        Title = doc.Name,
        PosterUrl = TextRules.ImageAtSize(doc.PictureXl ?? doc.PictureBig, imageSize),
        ExtendedData = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["deezer_id"] = doc.Id,
            ["nb_fan"] = doc.NbFan,
            ["nb_album"] = doc.NbAlbum,
            ["link"] = doc.Link,
        }),
    };

    // ── albums ────────────────────────────────────────────────────────────────

    /// <summary>A search result: what the search itself returned (no genres, label or release date).</summary>
    public static MediaMetadata AlbumFromSearch(AlbumDoc doc, int imageSize) => new()
    {
        ExternalId = "album:" + doc.IdText,
        Source = Source,
        Title = doc.Title,
        PosterUrl = CoverOf(doc.CoverXl ?? doc.CoverBig, doc.Md5Image, imageSize),
        Cast = doc.Artist is null ? [] : [new CastMember(doc.Artist.Name, "Main", "artist:" + doc.Artist.IdText, TextRules.ImageAtSize(doc.Artist.PictureXl, imageSize))],
        AlternateNames = AlternateTitles(doc.Title),
        ExtendedData = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["deezer_id"] = doc.Id,
            ["nb_tracks"] = doc.NbTracks,
            ["record_type"] = doc.RecordType,
            ["explicit_lyrics"] = doc.ExplicitLyrics,
        }),
    };

    /// <summary>The full record of an album, with its track list.</summary>
    public static MediaMetadata Album(AlbumDoc doc, int imageSize)
    {
        var tracks = (doc.Tracks?.Data ?? []).Select(t => new Dictionary<string, object?>
        {
            ["id"] = t.Id,
            ["title"] = t.Title,
            ["position"] = t.TrackPosition,
            ["disk_number"] = t.DiskNumber,
            ["duration_seconds"] = t.Duration,
            ["rank"] = t.Rank,
        }).ToList();

        var ids = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(doc.Upc)) ids["upc"] = doc.Upc!;

        return new MediaMetadata
        {
            ExternalId = "album:" + doc.IdText,
            Source = Source,
            Title = doc.Title,
            Year = TextRules.YearOf(doc.ReleaseDate),
            PosterUrl = CoverOf(doc.CoverXl ?? doc.CoverBig, doc.Md5Image, imageSize),
            Genres = GenreNames(doc),
            Cast = CastOf(doc.Contributors, doc.Artist, imageSize),
            AlternateNames = AlternateTitles(doc.Title),
            ExtendedData = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["ids"] = ids,
                ["external_ids"] = ids,
                ["deezer_id"] = doc.Id,
                ["upc"] = doc.Upc,
                ["label"] = doc.Label,
                ["record_type"] = doc.RecordType,
                ["nb_tracks"] = doc.NbTracks,
                ["duration_seconds"] = doc.Duration,
                ["fans"] = doc.Fans,
                ["release_date"] = doc.ReleaseDate,
                ["explicit_lyrics"] = doc.ExplicitLyrics,
                ["explicit_content_lyrics"] = doc.ExplicitContentLyrics,
                ["available"] = doc.Available,
                ["link"] = doc.Link,
                ["tracks"] = tracks,
            }),
        };
    }

    // ── tracks ────────────────────────────────────────────────────────────────

    /// <summary>A search result: what the search itself returned (no ISRC, no release date, no contributors).</summary>
    public static MediaMetadata TrackFromSearch(TrackDoc doc, int imageSize) => new()
    {
        ExternalId = "track:" + doc.IdText,
        Source = Source,
        Title = doc.Title,
        RuntimeMinutes = MinutesOf(doc.Duration),
        PosterUrl = CoverOf(doc.Album?.CoverXl, doc.Album?.Md5Image ?? doc.Md5Image, imageSize),
        Cast = doc.Artist is null ? [] : [new CastMember(doc.Artist.Name, "Main", "artist:" + doc.Artist.IdText, TextRules.ImageAtSize(doc.Artist.PictureXl, imageSize))],
        AlternateNames = AlternateTitles(doc.Title, doc.TitleShort),
        ExtendedData = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["deezer_id"] = doc.Id,
            ["duration_seconds"] = doc.Duration,
            ["rank"] = doc.Rank,
            ["album"] = doc.Album is null ? null : new Dictionary<string, object?> { ["id"] = doc.Album.Id, ["title"] = doc.Album.Title },
        }),
    };

    /// <summary>The full record of a track. <paramref name="album"/> is the album it sits on (when fetched): a track has no genres
    /// of its own on Deezer, so it carries its album's.</summary>
    public static MediaMetadata Track(TrackDoc doc, AlbumDoc? album, int imageSize)
    {
        var ids = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(doc.Isrc)) ids["isrc"] = doc.Isrc!;

        var albumRef = doc.Album;
        return new MediaMetadata
        {
            ExternalId = "track:" + doc.IdText,
            Source = Source,
            Title = doc.Title,
            Year = TextRules.YearOf(doc.ReleaseDate) ?? TextRules.YearOf(albumRef?.ReleaseDate) ?? TextRules.YearOf(album?.ReleaseDate),
            RuntimeMinutes = MinutesOf(doc.Duration),
            PosterUrl = CoverOf(albumRef?.CoverXl ?? album?.CoverXl, albumRef?.Md5Image ?? album?.Md5Image ?? doc.Md5Image, imageSize),
            Genres = album is null ? [] : GenreNames(album),
            Cast = CastOf(doc.Contributors, doc.Artist, imageSize),
            AlternateNames = AlternateTitles(doc.Title, doc.TitleShort),
            ExtendedData = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["ids"] = ids,
                ["external_ids"] = ids,
                ["deezer_id"] = doc.Id,
                ["isrc"] = doc.Isrc,
                ["title_short"] = doc.TitleShort,
                ["title_version"] = doc.TitleVersion,
                ["duration_seconds"] = doc.Duration,
                ["track_position"] = doc.TrackPosition,
                ["disk_number"] = doc.DiskNumber,
                ["rank"] = doc.Rank,
                ["bpm"] = doc.Bpm,
                ["gain"] = doc.Gain,
                ["explicit_lyrics"] = doc.ExplicitLyrics,
                ["explicit_content_lyrics"] = doc.ExplicitContentLyrics,
                ["release_date"] = doc.ReleaseDate,
                ["available_countries_count"] = doc.AvailableCountries?.Count,
                ["link"] = doc.Link,
                ["album"] = albumRef is null ? null : new Dictionary<string, object?> { ["id"] = albumRef.Id, ["title"] = albumRef.Title },
            }),
        };
    }

    // ── pieces ────────────────────────────────────────────────────────────────

    private static string? CoverOf(string? url, string? md5, int size) =>
        TextRules.ImageAtSize(url, size) ?? TextRules.ImageFromHash(md5, "cover", size);

    /// <summary>A track length in whole minutes (Chronicle's unit), never 0 for a track that has a length. The exact seconds stay in ExtendedData.</summary>
    internal static int? MinutesOf(int? seconds) => seconds is > 0 ? Math.Max(1, (int)Math.Round(seconds.Value / 60.0)) : null;

    private static List<string> GenreNames(AlbumDoc album) =>
        (album.Genres?.Data ?? []).Select(g => g.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Who made it: Deezer's contributors with their role (Main, Featured), or the credited artist when there are none.</summary>
    private static List<CastMember> CastOf(List<ArtistDoc>? contributors, ArtistDoc? artist, int size)
    {
        var list = (contributors is { Count: > 0 } ? contributors : artist is null ? [] : [artist])
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .Select(c => new CastMember(c.Name, string.IsNullOrWhiteSpace(c.Role) ? "Main" : c.Role, "artist:" + c.IdText,
                TextRules.ImageAtSize(c.PictureXl ?? c.PictureBig, size)))
            .ToList();
        return list;
    }

    /// <summary>The other ways to write the title: without its edition qualifiers, and Deezer's own short form.</summary>
    internal static List<string> AlternateTitles(string title, string? titleShort = null)
    {
        var result = new List<string>();
        void Add(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            text = text.Trim();
            if (string.Equals(text, title, StringComparison.OrdinalIgnoreCase)) return;
            if (!result.Contains(text, StringComparer.OrdinalIgnoreCase)) result.Add(text);
        }
        Add(TextRules.Analyse(title).Plain);
        Add(titleShort);
        return result;
    }
}
