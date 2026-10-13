using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace Chronicle.Plugin.Deezer;

/// <summary>
/// Chronicle metadata provider for Deezer (music: Artist, Album, Track). No API key is needed.
///
/// A supplement to MusicBrainz (identity) and Last.fm (text): Deezer adds covers up to 1900x1900, artist pictures, genres, labels,
/// release dates, ISRC and UPC codes, and is strong exactly where MusicBrainz is thin: singles, remixes, recent releases.
///
///   level 0  Artist  a plain-text artist search; the name must be equal, popularity separates namesakes
///   level 1  Album   "artist title", then the title alone; edition chosen by the tracks the library holds
///   level 2  Track   found INSIDE the track list of its album (one album request serves every track on it, remembered for ten
///                    minutes); only a track whose album cannot be found costs a search of its own
///
/// External ids (source "deezer") carry their kind because Deezer's numbers overlap: artist:27, album:302127, track:3135556.
/// Fix match also takes a deezer.com URL, "isrc:" plus an ISRC, or "upc:" plus an album's UPC.
///
/// Per call the plugin makes at most four requests (Chronicle allows 25 seconds); see <see cref="DeezerClient"/> for pacing and
/// the rate-limit contract, <see cref="Matchers"/> for how results are scored.
/// </summary>
public sealed class DeezerMetadataProvider : IMetadataProvider, IDisposable
{
    public string PluginId => "chronicle.plugin.deezer";
    public string Name     => "Deezer";
    public string Version  => "1.0.0";
    public string Author   => "Chronicle Contributors";

    public const string KeyCoverSize = "cover_size";
    public const string KeyDurationTolerance = "duration_tolerance_seconds";
    public const string KeyJunkTerms = "junk_terms";
    public const string KeyArtistWords = "artist_folder_words";
    public const string KeyTrackDetails = "fetch_track_details";
    public const string DefaultCoverSize = "max";

    private static readonly ILogger Log = Serilog.Log.ForContext<DeezerMetadataProvider>();

    private DeezerClient? _client;
    private HttpClient? _ownedHttp;
    private bool _clientSupplied;
    private MatchSettings _match = MatchSettings.Default;
    private int _imageSize = 1900;
    private bool _fetchTrackDetails = true;

    /// <summary>What the last <see cref="Configure"/> chose (read by the tests).</summary>
    internal MatchSettings Match => _match;
    internal int ImageSize => _imageSize;
    internal bool FetchTrackDetails => _fetchTrackDetails;
    internal string? ConfiguredUserAgent { get; private set; }

    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);
    private readonly TtlCache<AlbumDoc> _albums = new(CacheFor, 2000);
    private readonly TtlCache<TrackDoc> _tracks = new(CacheFor, 2000);
    private readonly TtlCache<ArtistDoc> _artists = new(CacheFor, 2000);
    private readonly TtlCache<AlbumResolution> _resolved = new(CacheFor, 2000);
    // Tracks as an album's own list gave them (title, length, rank: no ISRC, no contributors), kept for "fetch_track_details" off.
    private readonly TtlCache<TrackDoc> _listed = new(CacheFor, 5000);

    private const int SearchLimit = 10;
    private const int WideSearchLimit = 25;
    private const int MaxCandidates = 5;
    private const int ConfidentScore = 70;
    private const int CloseScores = 12;       // candidates within this of the best are worth telling apart by their tracks
    private const int MaxRequestsPerCall = 4; // Chronicle allows a plugin 25 seconds per call (4 during a file scan)
    private const int Gate = 50;              // Chronicle's confidence gate

    public DeezerMetadataProvider() { }

    internal DeezerMetadataProvider(DeezerClient client, MatchSettings? match = null, int imageSize = 1900)
    {
        _client = client;
        _clientSupplied = true;
        _match = match ?? MatchSettings.Default;
        _imageSize = imageSize;
    }

    // ── capabilities ──────────────────────────────────────────────────────────

    private static readonly MediaTypeSupport[] SupportedTypes =
    [
        // Declared as MusicBrainz and Last.fm declare it, so the type is created the same way whichever plugin is installed first.
        new MediaTypeSupport
        {
            MediaTypeName   = "music",
            DisplayName     = "Music",
            HierarchyLevels = 3,
            HierarchyLabels = ["Artist", "Album", "Track"],
            InteractionVerb = "listened",
            // MusicBrainz (10) keeps identity and Last.fm (20) keeps text; Deezer fills what they lack. Metadata Assignment can
            // move it first per field (covers are where it is better).
            DefaultPriority = 30,
            SupportedFields = ["poster_url"],
            LevelFields = new Dictionary<int, List<string>>
            {
                [1] = ["title", "year", "poster_url", "genres", "cast"],
                [2] = ["title", "year", "runtime_minutes", "poster_url", "genres", "cast"],
            },
        },
    ];

    public MediaTypeSupport[] GetSupportedMediaTypes() => SupportedTypes;

    public IReadOnlyList<string> GetAcceptedCrossRefPrefixes() => ["deezer:"];

    internal static readonly (string Value, string Label, int Size)[] CoverSizes =
    [
        ("max", "Largest Deezer has (1900x1900)", 1900),
        ("xl", "Extra large (1000x1000)", 1000),
        ("big", "Large (500x500)", 500),
        ("medium", "Medium (250x250)", 250),
    ];

    public PluginSettingsSchema GetSettingsSchema() => new()
    {
        Settings =
        [
            new SettingDefinition
            {
                Key = KeyCoverSize, Label = "Cover and picture size", Type = SettingType.Dropdown, DefaultValue = DefaultCoverSize,
                Description = "How large the album covers and artist pictures are. Deezer serves any size up to 1900x1900; the largest is the default.",
                Options = CoverSizes.Select(c => new SelectOption { Value = c.Value, Label = c.Label }).ToList(),
            },
            new SettingDefinition
            {
                Key = KeyDurationTolerance, Label = "Duration tolerance (seconds)", Type = SettingType.Number, DefaultValue = "5",
                Description = "How far two track lengths may differ and still count as the same recording when the library's tracks are " +
                              "compared with an album's, to choose between its editions. 1 to 60.",
            },
            new SettingDefinition
            {
                Key = KeyTrackDetails, Label = "Fetch each track in full", Type = SettingType.Boolean, DefaultValue = "true",
                Description = "A track found on its album costs no request to find, but its ISRC code, contributors, BPM and release date come " +
                              "from one more request per track (an album's own list carries only title, length and popularity). On by " +
                              "default; turn it off to roughly halve the time of a first pass over a large library, at the cost of those fields.",
            },
            new SettingDefinition
            {
                Key = KeyJunkTerms, Label = "Junk release words", Type = SettingType.TextArea,
                DefaultValue = string.Join("\n", MatchSettings.DefaultJunkTerms),
                Description = "One per line. A result whose title or artist contains one of these words or phrases (karaoke, tribute, " +
                              "nightcore ...) is a cover or bulk-uploaded version, not the release, and ranks far lower unless your own " +
                              "library item says so too. Leave empty to use the default list.",
            },
            new SettingDefinition
            {
                Key = KeyArtistWords, Label = "Artist folder words", Type = SettingType.TextArea,
                DefaultValue = string.Join("\n", MatchSettings.DefaultArtistNoise),
                Description = "One per line. Words that trail an artist's folder name in the library without being part of the name " +
                              "(\"Pink Floyd discography\") and are ignored when matching an artist. Album titles keep these words. " +
                              "Leave empty to use the default list.",
            },
        ],
    };

    public void Configure(IReadOnlyDictionary<string, string> settings)
    {
        string? Get(string key) => settings.TryGetValue(key, out var v) ? v?.Trim() : null;

        var size = Get(KeyCoverSize)?.ToLowerInvariant();
        _imageSize = CoverSizes.FirstOrDefault(c => c.Value == size) is { Size: > 0 } found ? found.Size : CoverSizes[0].Size;

        var tolerance = int.TryParse(Get(KeyDurationTolerance), out var t) ? Math.Clamp(t, 1, 60) : 5;
        _fetchTrackDetails = !bool.TryParse(Get(KeyTrackDetails), out var details) || details;
        var junk = TextRules.ParseList(Get(KeyJunkTerms));
        var words = TextRules.ParseList(Get(KeyArtistWords));
        _match = new MatchSettings(
            junk.Count > 0 ? junk : MatchSettings.DefaultJunkTerms,
            words.Count > 0 ? words : MatchSettings.DefaultArtistNoise,
            tolerance);

        if (_clientSupplied) return;   // a client was supplied directly (tests)

        _client?.Dispose();
        _ownedHttp?.Dispose();
        _ownedHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        ConfiguredUserAgent = DeezerClient.UserAgent(Version);
        _ownedHttp.DefaultRequestHeaders.UserAgent.TryParseAdd(ConfiguredUserAgent);
        _client = new DeezerClient(_ownedHttp, DeezerClient.Interval, Log);
        Log.Information("Deezer configured (images {Size}px, duration tolerance {Tolerance}s, {Junk} junk terms)",
            _imageSize, tolerance, _match.JunkTerms.Count);
    }

    // ── search ────────────────────────────────────────────────────────────────

    /// <summary>An album looked for and, when found, its full record (with the track list) for choosing between editions.</summary>
    private sealed record AlbumResolution(AlbumDoc? Detail, int Score);

    public async Task<IReadOnlyList<ScoredCandidate>> SearchAsync(MediaSearchContext context, CancellationToken ct = default)
    {
        EnsureConfigured();
        if (context.MediaTypeName is not (null or "music")) return [];

        var titles = SearchTitles(context);
        if (titles.Count == 0) return [];
        var start = _client!.Requests;

        // An id the item already holds is the surest match.
        if (await KnownIdentifierAsync(context, ct).ConfigureAwait(false) is { } known) return [known];

        // Free text (Add Media): the user typed something that may be an artist, an album or a track.
        var isFreeSearch = context.HierarchyLevel == 0 && !context.IsRealHierarchyPosition && context.ParentName is null;
        if (isFreeSearch) return await FreeSearchAsync(titles[0], ct).ConfigureAwait(false);

        return context.HierarchyLevel switch
        {
            0 => await SearchArtistsAsync(titles[0], context, start, ct).ConfigureAwait(false),
            1 => await SearchAlbumsAsync(titles, context, start, ct).ConfigureAwait(false),
            _ => await SearchTracksAsync(titles, context, start, ct).ConfigureAwait(false),
        };
    }

    /// <summary>The names an item is searched under: its precise name, then its other forms, each without a "(YYYY)" suffix.</summary>
    private static List<string> SearchTitles(MediaSearchContext context)
    {
        var all = new List<string>();
        if (!string.IsNullOrWhiteSpace(context.PreciseName)) all.Add(context.PreciseName);
        all.AddRange(context.AltTitles ?? []);
        all.Add(context.Name);
        return all.Select(TextRules.StripYearSuffix).Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
    }

    /// <summary>
    /// Text fit to send as a query. A word with a broken character in it ("B?la", U+FFFD) is left out, because the plain-text
    /// search needs every word to match and no spelling of the damaged one would; a "?" that is real punctuation ("Why?") is dropped
    /// from its word.
    /// </summary>
    internal static string QueryText(string? text)
    {
        var words = new List<string>();
        foreach (var raw in (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var broken = raw.Contains('�');
            for (var i = 1; !broken && i + 1 < raw.Length; i++)
                if (raw[i] == '?' && char.IsLetterOrDigit(raw[i - 1]) && char.IsLetterOrDigit(raw[i + 1])) broken = true;
            if (broken) continue;
            var word = raw.Replace("?", "");
            if (word.Length > 0) words.Add(word);
        }
        return string.Join(' ', words);
    }

    private string ArtistQuery(string? library) => QueryText(TextRules.CleanArtistName(library, _match.ArtistNoise));

    // ── artists ───────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<ScoredCandidate>> SearchArtistsAsync(string name, MediaSearchContext context, int start, CancellationToken ct)
    {
        var docs = await _client!.SearchArtistsAsync(ArtistQuery(name), SearchLimit, ct).ConfigureAwait(false);
        var scored = docs.Select(d => (Doc: d, Score: Matchers.ScoreArtist(d, name, _match))).Where(s => s.Score > 0 && s.Doc.Id > 0)
            .OrderByDescending(s => s.Score).ToList();
        if (scored.Count == 0) return [];

        // Several artists share the name and the library's own albums can tell them apart: ask the catalogue of each close
        // candidate (at most two requests).
        var libraryAlbums = context.ChildNames ?? [];
        if (scored.Count > 1 && libraryAlbums.Count > 0 && scored[0].Score - scored[1].Score <= 10)
        {
            var close = scored.Where(s => scored[0].Score - s.Score <= 10).Take(2).ToList();
            var confirmed = new List<(ArtistDoc Doc, int Score)>();
            foreach (var c in close)
            {
                if (_client.Requests - start >= MaxRequestsPerCall) { confirmed.Add(c); continue; }
                var catalogue = await _client.ArtistAlbumsAsync(c.Doc.Id, 100, ct).ConfigureAwait(false);
                confirmed.Add((c.Doc, Math.Min(99, c.Score + CatalogueBonus(libraryAlbums, catalogue))));
            }
            scored = confirmed.Concat(scored.Skip(close.Count)).OrderByDescending(s => s.Score).ToList();
        }

        foreach (var (doc, _) in scored) _artists.Set(doc.IdText, doc);
        return scored.Take(MaxCandidates)
            .Select(s => new ScoredCandidate(WithAlias(MetadataMapper.Artist(s.Doc, _imageSize), name, s.Score >= Gate), s.Score, "artist name search"))
            .ToList();
    }

    /// <summary>Up to +10 for the share of the library's albums that appear in the candidate artist's catalogue.</summary>
    private static int CatalogueBonus(IReadOnlyList<string> libraryAlbums, List<AlbumDoc> catalogue)
    {
        if (libraryAlbums.Count == 0 || catalogue.Count == 0) return 0;
        var catalogueTitles = catalogue.Select(a => TextRules.Analyse(a.Title)).ToList();
        var found = libraryAlbums.Select(a => TextRules.Analyse(a, libraryText: true))
            .Count(a => catalogueTitles.Any(c => TextRules.KeyEquals(a.Core, c.Core) || TextRules.KeyEquals(a.Full, c.Full)));
        return (int)Math.Round(10.0 * found / libraryAlbums.Count);
    }

    // ── albums ────────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<ScoredCandidate>> SearchAlbumsAsync(List<string> titles, MediaSearchContext context, int start, CancellationToken ct)
    {
        var query = AlbumQueryOf(titles[0], context.ParentName, context.Year, context.ChildCount, context.ChildNames, context.SubItemMetadata);
        var found = await FindAlbumsAsync(titles, query, start, ct).ConfigureAwait(false);
        return found.Take(MaxCandidates)
            .Select(f => new ScoredCandidate(WithAlias(f.Metadata, TextRules.StripYearSuffix(titles[0]), f.Score >= Gate), f.Score, "album search"))
            .ToList();
    }

    private static AlbumQuery AlbumQueryOf(string title, string? artist, int? year, int? childCount,
        IReadOnlyList<string>? childNames, IReadOnlyList<SiblingInfo>? subItems) =>
        new(title, artist, year, childCount, childNames,
            subItems?.Where(s => s.DurationSeconds is > 0).Select(s => s.DurationSeconds!.Value).ToList());

    private sealed record FoundAlbum(AlbumDoc Doc, AlbumDoc? Detail, int Score, MediaMetadata Metadata);

    /// <summary>
    /// Candidate albums for a library album, best first. "artist title" is tried first and the title alone only when that finds
    /// nothing; the close candidates are then fetched in full (their track lists settle which edition it is).
    /// </summary>
    private async Task<List<FoundAlbum>> FindAlbumsAsync(List<string> titles, AlbumQuery query, int start, CancellationToken ct)
    {
        var scored = new Dictionary<long, (AlbumDoc Doc, int Raw, int Cap)>();

        void Take(IEnumerable<AlbumDoc> docs, AlbumQuery q)
        {
            foreach (var doc in docs)
            {
                var (raw, cap) = Matchers.ScoreAlbumParts(doc, q, _match);
                if (raw <= 0) continue;
                if (!scored.TryGetValue(doc.Id, out var have) || Math.Min(raw, cap) > Math.Min(have.Raw, have.Cap)) scored[doc.Id] = (doc, raw, cap);
            }
        }

        int Best() => scored.Values.Select(s => Math.Min(s.Raw, s.Cap)).DefaultIfEmpty(0).Max();

        foreach (var title in titles)
        {
            var q = query with { Title = title };
            var artistText = ArtistQuery(q.Artist);
            var plain = QueryText(TextRules.StripYearSuffix(title));

            if (!string.IsNullOrEmpty(artistText))
                Take(await _client!.SearchAlbumsAsync($"{artistText} {plain}".Trim(), SearchLimit, ct).ConfigureAwait(false), q);

            // Free text is an AND of every word, so a long query finds nothing: the title alone, judged by artist afterwards.
            if (Best() < Gate && _client!.Requests - start < MaxRequestsPerCall - 1)
                Take(await _client.SearchAlbumsAsync(plain, WideSearchLimit, ct).ConfigureAwait(false), q);

            if (Best() >= ConfidentScore || _client!.Requests - start >= MaxRequestsPerCall - 1) break;
        }
        if (scored.Count == 0) return [];

        // Fetch the close candidates in full: the track list settles the edition, and the full record is what is asked for next.
        var ordered = scored.Values.OrderByDescending(s => Math.Min(s.Raw, s.Cap)).ToList();
        var top = Math.Min(ordered[0].Raw, ordered[0].Cap);
        var results = new List<FoundAlbum>();
        var fetched = 0;
        foreach (var (doc, raw, cap) in ordered)
        {
            AlbumDoc? detail = null;
            var score = Math.Min(raw, cap);
            var close = top - score <= CloseScores && score >= Gate - 10;
            if (close && fetched < 3 && _client!.Requests - start < MaxRequestsPerCall)
            {
                detail = await AlbumAsync(doc.Id, ct).ConfigureAwait(false);
                fetched++;
            }
            // What the track list says can raise a score, never past the caps the title and artist set.
            var final = detail is null || score < Gate - 10
                ? score
                : Math.Clamp(Math.Min(raw + Matchers.EditionBonus(detail, query, _match), cap), 0, 99);
            results.Add(new FoundAlbum(doc, detail, final,
                detail is null ? MetadataMapper.AlbumFromSearch(doc, _imageSize) : MetadataMapper.Album(detail, _imageSize)));
        }
        return results.OrderByDescending(r => r.Score).ThenByDescending(r => r.Doc.NbTracks ?? 0).ToList();
    }

    private async Task<AlbumDoc?> AlbumAsync(long id, CancellationToken ct)
    {
        var key = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (_albums.TryGet(key, out var cached)) return cached;
        var album = await _client!.GetAlbumAsync(id, ct).ConfigureAwait(false);
        if (album is not null) _albums.Set(key, album);
        return album;
    }

    // ── tracks ────────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<ScoredCandidate>> SearchTracksAsync(List<string> titles, MediaSearchContext context, int start, CancellationToken ct)
    {
        var artist = context.GrandparentName;
        var albumName = context.ParentName;
        var query = new TrackQuery(titles[0], artist, albumName, context.ItemNumber);
        var wording = TextRules.StripYearSuffix(titles[0]);

        // 1. Inside the album this track sits on: one album lookup serves every track on it, and costs nothing for the rest.
        if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(albumName))
        {
            var album = await ResolveAlbumAsync(artist!, albumName!, context, start, ct).ConfigureAwait(false);
            if (album.Detail is not null)
            {
                foreach (var title in titles)
                {
                    var (track, score) = Matchers.FindTrackInAlbum(album.Detail, query with { Title = title });
                    if (track is not null && score >= Gate)
                    {
                        _listed.Set(track.IdText, WithAlbum(track, album.Detail));
                        var meta = MetadataMapper.TrackFromSearch(WithAlbum(track, album.Detail), _imageSize);
                        return [new ScoredCandidate(WithAlias(meta, wording, true), score, "track in its album")];
                    }
                }
            }
        }

        // 2. A search of its own: "artist title", then the title alone.
        var found = new Dictionary<long, (TrackDoc Doc, int Score)>();
        void Take(IEnumerable<TrackDoc> docs, TrackQuery q)
        {
            foreach (var doc in docs)
            {
                var score = Matchers.ScoreTrack(doc, q, _match);
                if (score <= 0) continue;
                if (!found.TryGetValue(doc.Id, out var have) || score > have.Score) found[doc.Id] = (doc, score);
            }
        }

        foreach (var title in titles)
        {
            if (_client!.Requests - start >= MaxRequestsPerCall) break;
            var q = query with { Title = title };
            var plain = QueryText(TextRules.StripYearSuffix(title));
            var artistText = ArtistQuery(artist);
            if (!string.IsNullOrEmpty(artistText))
                Take(await _client.SearchTracksAsync($"{artistText} {plain}".Trim(), SearchLimit, ct).ConfigureAwait(false), q);
            if (!found.Values.Any(s => s.Score >= Gate) && _client.Requests - start < MaxRequestsPerCall)
                Take(await _client.SearchTracksAsync(plain, WideSearchLimit, ct).ConfigureAwait(false), q);
            if (found.Values.Any(s => s.Score >= ConfidentScore)) break;
        }

        return found.Values.OrderByDescending(s => s.Score).ThenByDescending(s => s.Doc.Rank ?? 0).Take(MaxCandidates)
            .Select(s => new ScoredCandidate(WithAlias(MetadataMapper.TrackFromSearch(s.Doc, _imageSize), wording, s.Score >= Gate), s.Score, "track search"))
            .ToList();
    }

    /// <summary>The album a track sits on, found once and remembered: every other track on it finds the answer ready (or ready to
    /// say there is none, so a missing album is not searched for again for each of its tracks).</summary>
    private async Task<AlbumResolution> ResolveAlbumAsync(string artist, string albumName, MediaSearchContext context, int start, CancellationToken ct)
    {
        var key = TextRules.Normalise(TextRules.CleanArtistName(artist, _match.ArtistNoise), true) + "|" + TextRules.Normalise(albumName, true);
        if (_resolved.TryGet(key, out var cached)) return cached;

        var siblings = context.SiblingNames ?? [];
        var query = new AlbumQuery(albumName, artist, context.Year, siblings.Count > 0 ? siblings.Count + 1 : null, siblings,
            context.SubItemMetadata?.Where(s => s.DurationSeconds is > 0).Select(s => s.DurationSeconds!.Value).ToList());

        var found = await FindAlbumsAsync([TextRules.StripYearSuffix(albumName)], query, start, ct).ConfigureAwait(false);
        var best = found.FirstOrDefault();
        AlbumDoc? detail = null;
        if (best is { Score: >= Gate })
            detail = best.Detail ?? await AlbumAsync(best.Doc.Id, ct).ConfigureAwait(false);

        var resolution = new AlbumResolution(detail, best?.Score ?? 0);
        _resolved.Set(key, resolution);
        return resolution;
    }

    /// <summary>A track from an album's list carries no album of its own: say which one, so its cover and album can be mapped.</summary>
    private static TrackDoc WithAlbum(TrackDoc track, AlbumDoc album)
    {
        track.Album ??= new AlbumRef { Id = album.Id, Title = album.Title, CoverXl = album.CoverXl, Md5Image = album.Md5Image, ReleaseDate = album.ReleaseDate };
        track.Artist ??= album.Artist;
        return track;
    }

    // ── free text, known ids ──────────────────────────────────────────────────

    private async Task<IReadOnlyList<ScoredCandidate>> FreeSearchAsync(string text, CancellationToken ct)
    {
        var q = QueryText(TextRules.StripYearSuffix(text));
        var typed = TextRules.Analyse(text);
        var all = new List<ScoredCandidate>();

        foreach (var artist in await _client!.SearchArtistsAsync(q, 5, ct).ConfigureAwait(false))
        {
            var score = Matchers.ScoreArtist(artist, text, _match);
            if (score > 0) all.Add(new ScoredCandidate(MetadataMapper.Artist(artist, _imageSize), score, "artist name search"));
        }
        foreach (var album in await _client.SearchAlbumsAsync(q, 5, ct).ConfigureAwait(false))
        {
            var title = Matchers.TitleScore(typed, TextRules.Analyse(album.Title), out _);
            if (title > 0) all.Add(new ScoredCandidate(MetadataMapper.AlbumFromSearch(album, _imageSize), title, "album search"));
        }
        foreach (var track in await _client.SearchTracksAsync(q, 5, ct).ConfigureAwait(false))
        {
            var title = Matchers.TitleScore(typed, TextRules.Analyse(track.Title), out _);
            if (title > 0) all.Add(new ScoredCandidate(MetadataMapper.TrackFromSearch(track, _imageSize), title, "track search"));
        }
        // The user is choosing from a list, so nothing is capped; the order is the score.
        return all.OrderByDescending(c => c.Score).Take(MaxCandidates * 2).ToList();
    }

    /// <summary>An id the item already holds, used only for the kind of item it is (an artist id for an artist, and so on).</summary>
    private async Task<ScoredCandidate?> KnownIdentifierAsync(MediaSearchContext context, CancellationToken ct)
    {
        var ids = context.KnownExternalIds;
        if (ids is null || !ids.TryGetValue("deezer", out var own) || DzRef.Parse(own) is not { } reference) return null;
        var rightKind = context.HierarchyLevel switch
        {
            0 => reference.Kind == DzKind.Artist,
            1 => reference.Kind is DzKind.Album or DzKind.Upc,
            _ => reference.Kind is DzKind.Track or DzKind.Isrc,
        };
        if (!rightKind) return null;
        var metadata = await GetByIdAsync(own, ct).ConfigureAwait(false);
        return string.IsNullOrEmpty(metadata.Title) ? null
            : new ScoredCandidate(metadata, 100, "known Deezer id") { IdentifierMatch = true };
    }

    // ── fetch by id ───────────────────────────────────────────────────────────

    public async Task<MediaMetadata> GetByIdAsync(string externalId, CancellationToken ct = default)
    {
        EnsureConfigured();
        var reference = DzRef.Parse(externalId);
        if (reference is null) return Empty(externalId);

        switch (reference.Kind)
        {
            case DzKind.Artist:
            {
                var id = long.Parse(reference.Value);
                if (!_artists.TryGet(reference.Value, out var artist))
                {
                    artist = await _client!.GetArtistAsync(id, ct).ConfigureAwait(false);
                    if (artist is not null) _artists.Set(reference.Value, artist);
                }
                return artist is null || artist.Id <= 0 ? Empty(externalId) : MetadataMapper.Artist(artist, _imageSize);
            }

            case DzKind.Album:
            {
                var album = await AlbumAsync(long.Parse(reference.Value), ct).ConfigureAwait(false);
                return album is null || album.Id <= 0 ? Empty(externalId) : MetadataMapper.Album(album, _imageSize);
            }

            case DzKind.Upc:
            {
                var album = await _client!.GetAlbumByUpcAsync(reference.Value, ct).ConfigureAwait(false);
                if (album is null || album.Id <= 0) return Empty(externalId);
                _albums.Set(album.IdText, album);
                return MetadataMapper.Album(album, _imageSize);
            }

            case DzKind.Isrc:
            {
                var track = await _client!.GetTrackByIsrcAsync(reference.Value, ct).ConfigureAwait(false);
                return track is null || track.Id <= 0 ? Empty(externalId) : await TrackWithAlbumAsync(track, ct).ConfigureAwait(false);
            }

            default:
            {
                var id = long.Parse(reference.Value);
                if (!_tracks.TryGet(reference.Value, out var track))
                {
                    // With "fetch each track in full" off, a track the album's own list just gave is used as it is.
                    if (!_fetchTrackDetails && _listed.TryGet(reference.Value, out var listed)) track = listed;
                    else track = await _client!.GetTrackAsync(id, ct).ConfigureAwait(false);
                    if (track is not null) _tracks.Set(reference.Value, track);
                }
                return track is null || track.Id <= 0 ? Empty(externalId) : await TrackWithAlbumAsync(track, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>A track has no genres of its own on Deezer: it carries its album's (one request, remembered).</summary>
    private async Task<MediaMetadata> TrackWithAlbumAsync(TrackDoc track, CancellationToken ct)
    {
        _tracks.Set(track.IdText, track);
        AlbumDoc? album = null;
        if (track.Album is { Id: > 0 } albumRef)
        {
            try { album = await AlbumAsync(albumRef.Id, ct).ConfigureAwait(false); }
            catch (HttpRequestException ex) when (ex.StatusCode is not System.Net.HttpStatusCode.TooManyRequests)
            {
                Log.Warning(ex, "Deezer: album {Album} of track {Track} could not be read; the track is returned without genres", albumRef.Id, track.Id);
            }
        }
        return MetadataMapper.Track(track, album, _imageSize);
    }

    private static MediaMetadata Empty(string externalId) => new() { ExternalId = externalId, Source = MetadataMapper.Source };

    /// <summary>The wording the library uses for a matched title or artist is another name for it: Chronicle's name check accepts a
    /// candidate under any of its alternate names, so "Simon &amp; Garfunkel discography" is not turned away.</summary>
    private static MediaMetadata WithAlias(MediaMetadata metadata, string wording, bool matched)
    {
        if (matched && !string.IsNullOrWhiteSpace(wording)
            && !string.Equals(wording, metadata.Title, StringComparison.OrdinalIgnoreCase)
            && !metadata.AlternateNames.Contains(wording, StringComparer.OrdinalIgnoreCase))
            metadata.AlternateNames.Add(wording);
        return metadata;
    }

    // ── images and health ─────────────────────────────────────────────────────

    public Task<byte[]> GetImageAsync(string url, CancellationToken ct = default)
    {
        EnsureConfigured();
        // Only Deezer's own image hosts: this plugin is asked for pictures it handed out, nothing else.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !(uri.Host.EndsWith(".dzcdn.net", StringComparison.OrdinalIgnoreCase) || uri.Host == "dzcdn.net"
                 || uri.Host.EndsWith(".deezer.com", StringComparison.OrdinalIgnoreCase) || uri.Host == "deezer.com"))
            throw new ArgumentException("Deezer only serves images from dzcdn.net and deezer.com.", nameof(url));
        return _client!.GetImageBytesAsync(url, ct);
    }

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        if (_client is null)
        {
            Log.Warning("Deezer health check skipped: plugin not configured");
            return false;
        }
        return await _client.HealthCheckAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (!_clientSupplied) _client?.Dispose();
        _ownedHttp?.Dispose();
        _client = null;
        _ownedHttp = null;
    }

    private void EnsureConfigured()
    {
        if (_client is null)
            throw new InvalidOperationException("Deezer plugin is not configured. Call Configure() first.");
    }
}
