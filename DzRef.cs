using System.Text.RegularExpressions;

namespace Chronicle.Plugin.Deezer;

internal enum DzKind { Artist, Album, Track, Isrc, Upc }

/// <summary>What a pasted or stored identifier refers to.</summary>
internal sealed record DzRef(DzKind Kind, string Value)
{
    private static readonly Regex Prefixed = new(@"^(artist|album|track)\s*:\s*(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Url = new(@"deezer\.(?:com|page\.link)/(?:[a-z]{2}(?:-[a-z]{2})?/)?(artist|album|track)/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IsrcCode = new(@"^[A-Z]{2}[A-Z0-9]{3}\d{7}$", RegexOptions.Compiled);

    /// <summary>
    /// Understands: a kind-prefixed id (<c>artist:27</c>, <c>album:302127</c>, <c>track:3135556</c>), the same with a "deezer:"
    /// prefix, a deezer.com or api.deezer.com URL (with or without a language segment), "isrc:" plus an ISRC (or a bare ISRC),
    /// and "upc:" plus an album's UPC or EAN. A bare number is refused: Deezer's ids overlap across kinds, so 27 could be an
    /// artist, an album or a track.
    /// </summary>
    public static DzRef? Parse(string? text)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0) return null;

        if (s.StartsWith("deezer:", StringComparison.OrdinalIgnoreCase)) s = s["deezer:".Length..].Trim();

        if (s.StartsWith("isrc:", StringComparison.OrdinalIgnoreCase)) return IsrcRef(s["isrc:".Length..]);
        if (s.StartsWith("upc:", StringComparison.OrdinalIgnoreCase)) return UpcRef(s["upc:".Length..]);

        if (Url.Match(s) is { Success: true } url) return FromKind(url.Groups[1].Value, url.Groups[2].Value);
        if (Prefixed.Match(s) is { Success: true } prefixed) return FromKind(prefixed.Groups[1].Value, prefixed.Groups[2].Value);
        return IsrcRef(s);   // a bare ISRC is unambiguous (two letters, three characters, seven digits)
    }

    private static DzRef? FromKind(string kind, string id)
    {
        if (!long.TryParse(id, out var n) || n <= 0) return null;
        return kind.ToLowerInvariant() switch
        {
            "artist" => new DzRef(DzKind.Artist, id),
            "album" => new DzRef(DzKind.Album, id),
            _ => new DzRef(DzKind.Track, id),
        };
    }

    private static DzRef? IsrcRef(string text)
    {
        var code = text.Replace("-", "").Replace(" ", "").ToUpperInvariant();
        return IsrcCode.IsMatch(code) ? new DzRef(DzKind.Isrc, code) : null;
    }

    private static DzRef? UpcRef(string text)
    {
        var digits = text.Replace("-", "").Replace(" ", "");
        return digits.Length is >= 8 and <= 14 && digits.All(char.IsDigit) ? new DzRef(DzKind.Upc, digits) : null;
    }

    /// <summary>The id as Chronicle stores it: kind and number.</summary>
    public string Stored => Kind switch
    {
        DzKind.Artist => "artist:" + Value,
        DzKind.Album => "album:" + Value,
        DzKind.Track => "track:" + Value,
        DzKind.Isrc => "isrc:" + Value,
        _ => "upc:" + Value,
    };
}
