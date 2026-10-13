using System.Globalization;
using System.Text.Json.Serialization;

namespace Chronicle.Plugin.Deezer;

// JSON shapes of the Deezer answers this plugin reads. Only what is used is declared; a field Deezer adds later is ignored.

internal sealed class ErrorBody
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("code")] public int Code { get; set; }
}

internal sealed class ErrorEnvelope
{
    [JsonPropertyName("error")] public ErrorBody? Error { get; set; }
}

internal sealed class ListResponse<T>
{
    [JsonPropertyName("data")] public List<T> Data { get; set; } = [];
    [JsonPropertyName("total")] public int? Total { get; set; }
    [JsonPropertyName("next")] public string? Next { get; set; }
}

internal sealed class ArtistDoc
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("link")] public string? Link { get; set; }
    [JsonPropertyName("picture_xl")] public string? PictureXl { get; set; }
    [JsonPropertyName("picture_big")] public string? PictureBig { get; set; }
    [JsonPropertyName("nb_album")] public int? NbAlbum { get; set; }
    [JsonPropertyName("nb_fan")] public long? NbFan { get; set; }
    /// <summary>Only on contributors: "Main", "Featured", ...</summary>
    [JsonPropertyName("role")] public string? Role { get; set; }

    public string IdText => Id.ToString(CultureInfo.InvariantCulture);
}

internal sealed class GenreDoc
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

internal sealed class GenreList
{
    [JsonPropertyName("data")] public List<GenreDoc> Data { get; set; } = [];
}

internal sealed class TrackList
{
    [JsonPropertyName("data")] public List<TrackDoc> Data { get; set; } = [];
    [JsonPropertyName("next")] public string? Next { get; set; }
}

internal sealed class AlbumDoc
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("upc")] public string? Upc { get; set; }
    [JsonPropertyName("link")] public string? Link { get; set; }
    [JsonPropertyName("cover_xl")] public string? CoverXl { get; set; }
    [JsonPropertyName("cover_big")] public string? CoverBig { get; set; }
    [JsonPropertyName("md5_image")] public string? Md5Image { get; set; }
    [JsonPropertyName("genre_id")] public long? GenreId { get; set; }
    [JsonPropertyName("genres")] public GenreList? Genres { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("nb_tracks")] public int? NbTracks { get; set; }
    [JsonPropertyName("duration")] public int? Duration { get; set; }
    [JsonPropertyName("fans")] public long? Fans { get; set; }
    [JsonPropertyName("release_date")] public string? ReleaseDate { get; set; }
    [JsonPropertyName("record_type")] public string? RecordType { get; set; }
    [JsonPropertyName("available")] public bool? Available { get; set; }
    [JsonPropertyName("explicit_lyrics")] public bool? ExplicitLyrics { get; set; }
    [JsonPropertyName("explicit_content_lyrics")] public int? ExplicitContentLyrics { get; set; }
    [JsonPropertyName("artist")] public ArtistDoc? Artist { get; set; }
    [JsonPropertyName("contributors")] public List<ArtistDoc>? Contributors { get; set; }
    [JsonPropertyName("tracks")] public ListResponse<TrackDoc>? Tracks { get; set; }

    public string IdText => Id.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The album a track answer names (a few fields only).</summary>
internal sealed class AlbumRef
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("cover_xl")] public string? CoverXl { get; set; }
    [JsonPropertyName("md5_image")] public string? Md5Image { get; set; }
    [JsonPropertyName("release_date")] public string? ReleaseDate { get; set; }
}

internal sealed class TrackDoc
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("title_short")] public string? TitleShort { get; set; }
    [JsonPropertyName("title_version")] public string? TitleVersion { get; set; }
    [JsonPropertyName("isrc")] public string? Isrc { get; set; }
    [JsonPropertyName("link")] public string? Link { get; set; }
    [JsonPropertyName("duration")] public int? Duration { get; set; }
    [JsonPropertyName("track_position")] public int? TrackPosition { get; set; }
    [JsonPropertyName("disk_number")] public int? DiskNumber { get; set; }
    [JsonPropertyName("rank")] public long? Rank { get; set; }
    [JsonPropertyName("release_date")] public string? ReleaseDate { get; set; }
    [JsonPropertyName("explicit_lyrics")] public bool? ExplicitLyrics { get; set; }
    [JsonPropertyName("explicit_content_lyrics")] public int? ExplicitContentLyrics { get; set; }
    [JsonPropertyName("bpm")] public double? Bpm { get; set; }
    [JsonPropertyName("gain")] public double? Gain { get; set; }
    [JsonPropertyName("md5_image")] public string? Md5Image { get; set; }
    [JsonPropertyName("available_countries")] public List<string>? AvailableCountries { get; set; }
    [JsonPropertyName("contributors")] public List<ArtistDoc>? Contributors { get; set; }
    [JsonPropertyName("artist")] public ArtistDoc? Artist { get; set; }
    [JsonPropertyName("album")] public AlbumRef? Album { get; set; }

    // Deliberately NOT read: "preview" (a link signed with an expiry of a few hours) and "track_token" (a session token). Storing
    // either would store something already dead by the next run.

    public string IdText => Id.ToString(CultureInfo.InvariantCulture);
}
