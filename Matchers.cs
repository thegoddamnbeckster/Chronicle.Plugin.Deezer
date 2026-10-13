namespace Chronicle.Plugin.Deezer;

/// <summary>What is configurable about matching (see the plugin's settings).</summary>
/// <param name="JunkTerms">Words that mark a karaoke, tribute or cover release.</param>
/// <param name="ArtistNoise">Folder words trailing an artist name in the library ("discography").</param>
/// <param name="DurationToleranceSeconds">How far two durations may differ and still be the same recording.</param>
internal sealed record MatchSettings(IReadOnlyList<string> JunkTerms, IReadOnlyList<string> ArtistNoise, int DurationToleranceSeconds)
{
    public static readonly string[] DefaultJunkTerms =
    [
        "karaoke", "tribute", "in the style of", "made famous by", "originally performed", "as made famous", "8d audio", "8d",
        "nightcore", "sped up", "slowed", "reverb", "lullaby", "lullabies", "ringtone", "bardcore", "cover version", "backing track",
        "music box", "workout", "party tribute", "performed by", "originally by", "instrumental versions of",
    ];

    public static readonly string[] DefaultArtistNoise =
    [
        "complete discography", "discography", "discografia", "complete collection", "collection", "greatest hits", "complete albums",
        "the albums", "anthology", "best of",
    ];

    public static MatchSettings Default => new(DefaultJunkTerms, DefaultArtistNoise, 5);
}

/// <summary>An album being looked for: its title, its artist, and what the library already holds under it.</summary>
internal sealed record AlbumQuery(string Title, string? Artist, int? Year, int? ChildCount = null,
    IReadOnlyList<string>? ChildNames = null, IReadOnlyList<int>? ChildDurations = null);

/// <summary>A track being looked for.</summary>
internal sealed record TrackQuery(string Title, string? Artist, string? Album, int? ItemNumber);

/// <summary>
/// Scoring of Deezer results against a library item. Scores run 0 to 99 (100 is reserved for a match by identifier); Chronicle
/// accepts a match at 50 or above. The numbers come from running these rules over real library items (see
/// docs/plans/2026-10-13-deezer-plugin-design.md in the Chronicle repository).
/// </summary>
internal static class Matchers
{
    /// <summary>The most a result can score when its artist is not the library's: under Chronicle's gate of 50.</summary>
    public const int ArtistMismatchCap = 35;

    /// <summary>The most a result can score when it is a different recording of the same title ("TKO" and "TKO (Radio Edit)").</summary>
    public const int RecordingMismatchCap = 45;

    /// <summary>The most a result can score when the library item has no artist to check it against.</summary>
    public const int NoArtistCap = 45;

    private const int JunkPenalty = 40;
    private const int CompilationPenalty = 15;

    public static bool IsVariousArtists(string? name) => TextRules.Normalise(name) == "variousartists";

    // ── titles ────────────────────────────────────────────────────────────────

    /// <summary>
    /// How well a Deezer title matches a library title: 60 for the same title, 55 when only edition or credit qualifiers differ
    /// ("Doubt" and "Doubt (Deluxe Edition)"), 40 when one continues the other after a separator ("Jumper" and
    /// "Jumper / Graduate"; not "Get Born" and "Get Born Live", which is another release), 0 otherwise. A title that merely
    /// appears inside another is not a match. <paramref name="recordingMismatch"/> is true when the two name different recordings.
    /// </summary>
    public static int TitleScore(TitleParts library, TitleParts deezer, out bool recordingMismatch)
    {
        recordingMismatch = library.Recording != deezer.Recording;
        if (TextRules.KeyEquals(library.Full, deezer.Full)) { recordingMismatch = false; return 60; }
        if (TextRules.KeyEquals(library.Core, deezer.Core)) return 55;
        if (Math.Min(library.Core.Length, deezer.Core.Length) >= 6
            && ((TextRules.ProviderContinues(library.Core, deezer.Core) && TextRules.ContinuesAtBoundary(deezer.Plain, library.Core, libraryLonger: false))
                || (TextRules.LibraryContinues(library.Core, deezer.Core) && TextRules.ContinuesAtBoundary(library.Plain, deezer.Core, libraryLonger: true))))
            return 40;
        return 0;
    }

    // ── artists ───────────────────────────────────────────────────────────────

    /// <summary>
    /// How well a Deezer artist matches a library artist. The names must be equal after folder noise, accents and "&amp;"; a
    /// similar name is never accepted. Popularity separates artists that share a name (663,757 fans against 23).
    /// </summary>
    public static int ScoreArtist(ArtistDoc doc, string name, MatchSettings settings)
    {
        if (!TextRules.SameArtist(name, doc.Name, settings.ArtistNoise)) return 0;
        var popularity = (int)Math.Min(20, Math.Log2(1 + Math.Max(0, doc.NbFan ?? 0)));
        return Math.Clamp(60 + popularity, 0, 99);
    }

    // ── albums ────────────────────────────────────────────────────────────────

    /// <summary>How well a search result matches a library album. 0 means "not this album".</summary>
    public static int ScoreAlbum(AlbumDoc doc, AlbumQuery q, MatchSettings settings)
    {
        var (raw, cap) = ScoreAlbumParts(doc, q, settings);
        return Math.Clamp(Math.Min(raw, cap), 0, 99);
    }

    /// <summary>
    /// The score before its caps, and the cap that applies (99 when none). Kept apart so that what is learned later from the
    /// album's track list (<see cref="EditionBonus"/>) can raise a score but never lift it past a cap: a remix of the right
    /// title, or the right title by another artist, stays below the gate whatever its tracks look like.
    /// </summary>
    public static (int Raw, int Cap) ScoreAlbumParts(AlbumDoc doc, AlbumQuery q, MatchSettings settings)
    {
        var library = TextRules.Analyse(q.Title, libraryText: true);
        var score = TitleScore(library, TextRules.Analyse(doc.Title), out var recordingMismatch);
        if (score == 0 || doc.Id <= 0) return (0, 0);

        var artistKnown = !string.IsNullOrWhiteSpace(q.Artist);
        var cap = 99;
        if (artistKnown)
        {
            if (TextRules.SameArtist(q.Artist, doc.Artist?.Name, settings.ArtistNoise)) score += 30;
            else cap = Math.Min(cap, ArtistMismatchCap);
        }

        if (IsJunk(doc.Title, doc.Artist?.Name, q.Title, q.Artist, settings)) score -= JunkPenalty;

        if (string.Equals(doc.RecordType, "compile", StringComparison.OrdinalIgnoreCase) && !IsVariousArtists(q.Artist))
            score -= CompilationPenalty;

        if (q.ChildCount is > 0 && doc.NbTracks is > 0)
        {
            var diff = Math.Abs(doc.NbTracks.Value - q.ChildCount.Value);
            score += diff == 0 ? 15 : diff <= 2 ? 6 : 0;
        }

        if (recordingMismatch) cap = Math.Min(cap, RecordingMismatchCap);
        if (!artistKnown) cap = Math.Min(cap, NoArtistCap);
        return (score, cap);
    }

    /// <summary>
    /// What the library holds under an album, set against the tracks Deezer lists for it: the same number of tracks, the same
    /// titles, the same durations. Tells apart the 11-track and the 21-track "Doubt", and rejects an album whose title matches
    /// but whose tracks do not. Between -25 and +45.
    /// </summary>
    public static int EditionBonus(AlbumDoc detail, AlbumQuery q, MatchSettings settings)
    {
        var tracks = detail.Tracks?.Data ?? [];
        if (tracks.Count == 0) return 0;
        var bonus = 0;

        if (q.ChildCount is > 0)
        {
            var diff = Math.Abs(tracks.Count - q.ChildCount.Value);
            bonus += diff == 0 ? 15 : diff <= 2 ? 5 : -10;
        }

        if (q.ChildNames is { Count: > 0 })
        {
            var deezerTitles = tracks.Select(t => TextRules.Analyse(t.Title)).ToList();
            var matched = q.ChildNames
                .Select(n => TextRules.Analyse(n, libraryText: true))
                .Count(n => deezerTitles.Any(d => TextRules.KeyEquals(n.Core, d.Core) || TextRules.KeyEquals(n.Full, d.Full)));
            var ratio = (double)matched / q.ChildNames.Count;
            bonus += (int)Math.Round(20 * ratio);
            if (ratio < 0.3 && q.ChildNames.Count >= 3) bonus -= 15;   // the title matches but the tracks do not: probably another album
        }
        else if (q.ChildDurations is { Count: > 0 })
        {
            var available = tracks.Where(t => t.Duration is > 0).Select(t => t.Duration!.Value).ToList();
            var matched = 0;
            foreach (var d in q.ChildDurations)
            {
                var hit = available.FindIndex(a => Math.Abs(a - d) <= settings.DurationToleranceSeconds);
                if (hit < 0) continue;
                matched++;
                available.RemoveAt(hit);
            }
            bonus += (int)Math.Round(10.0 * matched / q.ChildDurations.Count);
        }

        return Math.Clamp(bonus, -25, 45);
    }

    // ── tracks ────────────────────────────────────────────────────────────────

    /// <summary>How well a track-search result matches a library track. 0 means "not this track".</summary>
    public static int ScoreTrack(TrackDoc doc, TrackQuery q, MatchSettings settings)
    {
        var library = TextRules.Analyse(q.Title, libraryText: true);
        var score = TitleScore(library, TextRules.Analyse(doc.Title), out var recordingMismatch);
        if (score == 0 || doc.Id <= 0) return 0;

        var artistKnown = !string.IsNullOrWhiteSpace(q.Artist);
        var artistMismatch = false;
        if (artistKnown)
        {
            if (TextRules.SameArtist(q.Artist, doc.Artist?.Name, settings.ArtistNoise)
                || (doc.Contributors?.Any(c => TextRules.SameArtist(q.Artist, c.Name, settings.ArtistNoise)) ?? false)) score += 30;
            else artistMismatch = true;
        }

        if (IsJunk(doc.Title, doc.Artist?.Name, q.Title, q.Artist, settings)
            || TextRules.ContainsJunk(doc.Album?.Title, settings.JunkTerms) && !TextRules.ContainsJunk(q.Album, settings.JunkTerms))
            score -= JunkPenalty;

        if (!string.IsNullOrWhiteSpace(q.Album) && doc.Album is not null)
        {
            var album = TextRules.Analyse(q.Album, libraryText: true);
            if (TitleScore(album, TextRules.Analyse(doc.Album.Title), out _) >= 55) score += 15;

            // A track of a live album is a different recording from the studio track of the same name...
            if (TextRules.IsLiveRelease(q.Album) != TextRules.IsLiveRelease(doc.Album.Title)) recordingMismatch = true;
        }

        // ...and a live album's track is the live recording even when its own title does not say "(Live)".
        if (recordingMismatch && TextRules.IsLiveRelease(q.Album) && library.Recording.Length == 0
            && TextRules.Analyse(doc.Title).Recording.Contains("live", StringComparison.Ordinal)
            && TextRules.IsLiveRelease(doc.Album?.Title) == true) recordingMismatch = false;

        if (recordingMismatch) score = Math.Min(score, RecordingMismatchCap);
        if (artistMismatch) score = Math.Min(score, ArtistMismatchCap);
        if (!artistKnown) score = Math.Min(score, NoArtistCap);
        return Math.Clamp(score, 0, 99);
    }

    /// <summary>
    /// The library's track inside the track list of an album already matched. The artist is the album's, so it is not checked
    /// again; the title decides, and the position breaks a tie between two tracks of the same name on one release.
    /// </summary>
    public static (TrackDoc? Track, int Score) FindTrackInAlbum(AlbumDoc album, TrackQuery q)
    {
        var library = TextRules.Analyse(q.Title, libraryText: true);
        TrackDoc? best = null;
        var bestScore = 0;
        var list = album.Tracks?.Data ?? [];
        for (var i = 0; i < list.Count; i++)
        {
            var track = list[i];
            var score = TitleScore(library, TextRules.Analyse(track.Title), out var recordingMismatch);
            if (score == 0) continue;
            score += 30;   // it is on the album already matched to this artist
            // An album's own track list carries no position field: its order is the order of the release.
            if (q.ItemNumber is > 0 && (track.TrackPosition ?? i + 1) == q.ItemNumber) score += 10;
            if (recordingMismatch) score = Math.Min(score, RecordingMismatchCap);
            score = Math.Clamp(score, 0, 99);
            if (score > bestScore) { best = track; bestScore = score; }
        }
        return (best, bestScore);
    }

    // ── junk ──────────────────────────────────────────────────────────────────

    /// <summary>A result that looks like a karaoke, tribute or cover release, unless the library item says so itself.</summary>
    private static bool IsJunk(string? docTitle, string? docArtist, string libraryTitle, string? libraryArtist, MatchSettings settings)
    {
        var docJunk = TextRules.ContainsJunk(docTitle, settings.JunkTerms) || TextRules.ContainsJunk(docArtist, settings.JunkTerms);
        if (!docJunk) return false;
        return !(TextRules.ContainsJunk(libraryTitle, settings.JunkTerms) || TextRules.ContainsJunk(libraryArtist, settings.JunkTerms));
    }
}
