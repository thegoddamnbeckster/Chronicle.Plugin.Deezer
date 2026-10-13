using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Chronicle.Plugin.Deezer;

/// <summary>What a title says once its qualifiers are separated out (see <see cref="TextRules.Analyse"/>).</summary>
/// <param name="Full">The whole title normalised, qualifiers included.</param>
/// <param name="Core">The title without edition and credit qualifiers: "Doubt (Deluxe Edition)" is "doubt".</param>
/// <param name="Recording">The qualifiers that change which recording it is ("Don Diablo Remix", "Live", "Demo"), normalised and
/// sorted. Two titles with different recording qualifiers are different recordings however alike the rest is.</param>
/// <param name="Plain">The title as text without its edition and credit qualifiers ("Doubt"), for display and alternate names.</param>
internal sealed record TitleParts(string Full, string Core, string Recording, string Plain = "");

/// <summary>
/// Everything that turns Deezer's text and the library's text into something comparable: name and title normalising (including
/// the library's broken characters), edition versus recording qualifiers, folder noise on artist names, and junk detection. Pure
/// functions, no network, so every rule is tested on its own.
/// </summary>
internal static partial class TextRules
{
    /// <summary>Stands for a character the library lost (a "?" inside a word, or U+FFFD): it matches any one character or none.</summary>
    private const char Wildcard = '';

    // ── comparing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Folds accents, lower-cases, and keeps letters and digits (any script); "&amp;" reads as "and". With
    /// <paramref name="libraryText"/> a "?" between two letters or digits, and U+FFFD, become <see cref="Wildcard"/>: this
    /// library holds those where an accent or an apostrophe was ("B?la Fleck", "Don?t", "blink?182"). Text from Deezer is never
    /// read that way, because a real "?" there ("Who? Where? Why?") is punctuation.
    /// </summary>
    private static string Fold(string? s, bool libraryText)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace("&", " and ");
        var decomposed = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        for (var i = 0; i < decomposed.Length; i++)
        {
            var ch = decomposed[i];
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (libraryText && ch == '�') sb.Append(Wildcard);
            else if (libraryText && ch == '?' && i > 0 && i + 1 < decomposed.Length
                     && char.IsLetterOrDigit(decomposed[i - 1]) && char.IsLetterOrDigit(decomposed[i + 1])) sb.Append(Wildcard);
            else if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (ch == ' ') sb.Append(' ');
            else if (ch is not ('\'' or '’' or '.')) sb.Append(' ');   // apostrophes and dots join, everything else separates
        }
        return WhitespaceRun().Replace(sb.ToString(), " ").Trim();
    }

    /// <summary>A title or name reduced for comparison: folded, a leading article dropped, spaces removed.</summary>
    public static string Normalise(string? text, bool libraryText = false)
    {
        var folded = Fold(text, libraryText);
        foreach (var article in new[] { "the ", "a ", "an " })
            if (folded.StartsWith(article, StringComparison.Ordinal) && folded.Length > article.Length)
            { folded = folded[article.Length..]; break; }
        return folded.Replace(" ", "");
    }

    /// <summary>True when two normalised keys are the same; a wildcard in <paramref name="libraryKey"/> matches one character or none.</summary>
    public static bool KeyEquals(string libraryKey, string providerKey)
    {
        if (libraryKey.Length == 0 || providerKey.Length == 0) return false;
        return libraryKey.IndexOf(Wildcard) < 0
            ? string.Equals(libraryKey, providerKey, StringComparison.Ordinal)
            : WildcardRegex(libraryKey, "$").IsMatch(providerKey);
    }

    /// <summary>True when the provider's key continues the library's: the library key is a proper beginning of it.</summary>
    public static bool ProviderContinues(string libraryKey, string providerKey)
    {
        if (libraryKey.Length == 0 || providerKey.Length <= libraryKey.Length) return false;
        return libraryKey.IndexOf(Wildcard) < 0
            ? providerKey.StartsWith(libraryKey, StringComparison.Ordinal)
            : WildcardRegex(libraryKey, ".+$").IsMatch(providerKey);
    }

    /// <summary>True when the library's key continues the provider's. A wildcard is dropped here: this direction is rare enough
    /// that a lost character is not worth a second matcher.</summary>
    public static bool LibraryContinues(string libraryKey, string providerKey)
    {
        var key = libraryKey.Replace(Wildcard.ToString(), "");
        return providerKey.Length > 0 && key.Length > providerKey.Length && key.StartsWith(providerKey, StringComparison.Ordinal);
    }

    private static Regex WildcardRegex(string libraryKey, string tail)
    {
        var sb = new StringBuilder("^");
        foreach (var ch in libraryKey) sb.Append(ch == Wildcard ? "." : "").Append(ch == Wildcard ? "?" : Regex.Escape(ch.ToString()));
        return new Regex(sb.Append(tail).ToString(), RegexOptions.CultureInvariant);
    }

    // ── titles ────────────────────────────────────────────────────────────────

    [GeneratedRegex(@"\s*[\(\[](1\d{3}|20\d{2})[\)\]]\s*$")]
    private static partial Regex YearSuffix();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    /// <summary>The title with a trailing "(YYYY)" removed. The library carries them: "Rush (1991)".</summary>
    public static string StripYearSuffix(string? title) => YearSuffix().Replace((title ?? "").Trim(), "").Trim();

    // A group is a bracketed phrase or a trailing " - phrase". Open brackets inside a phrase are not nested in practice.
    [GeneratedRegex(@"\(([^()]*)\)|\[([^\[\]]*)\]")]
    private static partial Regex BracketGroup();

    [GeneratedRegex(@"\s+[-–—]\s+([^-–—]+)$")]
    private static partial Regex DashSuffix();

    // What changes WHICH RECORDING it is.
    [GeneratedRegex(@"\b(remix(?:es|ed)?|mix(?:es)?|live|demo|acoustic|instrumental|radio|edit|extended|cover|session|unplugged|reprise|a\s?cappella|karaoke|sped|slowed|vip|rework(?:ed)?|bootleg|dub|orchestral|piano|stripped|single\s+version|club|dance|techno|version)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RecordingWords();

    // What only says WHICH RELEASE of the same recordings it is.
    [GeneratedRegex(@"\b(deluxe|expanded|remaster(?:ed)?|anniversary|edition|bonus|re-?issue|collector'?s?|platinum|explicit|clean|mono|stereo|standard|single|ep|digital|int'?l|international|japan(?:ese)?|tour|super|original|soundtrack|motion\s+picture|version)\b", RegexOptions.IgnoreCase)]
    private static partial Regex EditionWords();

    // A credit, not a different recording.
    [GeneratedRegex(@"^\s*(feat\.?|featuring|ft\.?|with|prod\.?|produced)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CreditPhrase();

    [GeneratedRegex(@"^\s*(1\d{3}|20\d{2})\s*$")]
    private static partial Regex YearOnly();

    // Only words that can never be part of a title's own wording on their own at its end.
    [GeneratedRegex(@"\s+(ep|single|deluxe(\s+edition|\s+version)?|remaster(ed)?(\s+\d{4})?|expanded(\s+edition)?|special\s+edition|anniversary\s+edition)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingEdition();

    private enum GroupKind { Edition, Recording, Credit, Unknown }

    // Qualifiers that name the same recording in its usual cut: "TKO" and "TKO (Radio Edit)" are one song, and a library single named
    // "TKO" is the radio edit. A remix, a live take or a demo is a different recording and is not in this list.
    [GeneratedRegex(@"^\s*(radio\s+(edit|mix|version)|single\s+(edit|version)|album\s+(edit|version|mix)|original\s+(mix|version|edit)|edit|clean\s+version|explicit\s+version)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex UsualCut();

    private static GroupKind Classify(string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase)) return GroupKind.Edition;
        if (CreditPhrase().IsMatch(phrase)) return GroupKind.Credit;
        if (YearOnly().IsMatch(phrase)) return GroupKind.Edition;
        if (UsualCut().IsMatch(phrase)) return GroupKind.Edition;
        // "Remastered Version", "Deluxe Version" are editions; the bare word "version" alone is not enough to be a recording
        // ("Orchestral Version" is, through "orchestral").
        var recording = RecordingWords().Matches(phrase).Select(m => m.Value.ToLowerInvariant()).Where(w => w != "version").Any();
        if (recording) return GroupKind.Recording;
        if (EditionWords().IsMatch(phrase)) return GroupKind.Edition;
        return GroupKind.Unknown;
    }

    /// <summary>Separates a title into the part that names the work and the qualifiers that follow it (see <see cref="TitleParts"/>).
    /// A group that is neither an edition, a credit nor a recording word stays part of the name ("(Part Two)", "(Pt. 2)").</summary>
    public static TitleParts Analyse(string? title, bool libraryText = false)
    {
        var text = StripYearSuffix(title);
        var recording = new List<string>();

        string Strip(Match m)
        {
            var phrase = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            switch (Classify(phrase))
            {
                case GroupKind.Recording: recording.Add(Normalise(phrase, libraryText)); return " ";
                case GroupKind.Edition or GroupKind.Credit: return " ";
                default: return m.Value;
            }
        }

        var core = BracketGroup().Replace(text, Strip);

        // " - Remastered 2015", " - Radio Edit", " - Live at Wembley": a trailing dash phrase is a qualifier when it classifies as one.
        if (DashSuffix().Match(core) is { Success: true } dash)
        {
            var phrase = dash.Groups[1].Value;
            var kind = Classify(phrase);
            if (kind is GroupKind.Recording) { recording.Add(Normalise(phrase, libraryText)); core = core[..dash.Index]; }
            else if (kind is GroupKind.Edition or GroupKind.Credit) core = core[..dash.Index];
        }

        // "Volcano Girls EP", "Heligoland Deluxe": a trailing edition word with no brackets is an edition too.
        while (TrailingEdition().Match(core) is { Success: true, Index: > 0 } trailing && Normalise(core[..trailing.Index]).Length >= 4) core = core[..trailing.Index];

        recording.RemoveAll(string.IsNullOrEmpty);
        recording.Sort(StringComparer.Ordinal);
        return new TitleParts(Normalise(text, libraryText), Normalise(core, libraryText), string.Join("|", recording), WhitespaceRun().Replace(core, " ").Trim());
    }

    /// <summary>
    /// True when <paramref name="longerPlain"/> continues the shorter key after a separator ("Jumper" in "Jumper / Graduate"),
    /// and not by running on in the same breath ("Get Born" in "Get Born Live"). The longer text is judged as written, so its
    /// punctuation is still there.
    /// </summary>
    public static bool ContinuesAtBoundary(string longerPlain, string shorterKey, bool libraryLonger)
    {
        if (string.IsNullOrEmpty(longerPlain) || shorterKey.Length == 0) return false;
        for (var i = 1; i < longerPlain.Length; i++)
        {
            if (longerPlain[i] is not (':' or '-' or '–' or '—' or '(' or '[' or ',' or '/' or ';' or '+' or '&')) continue;
            var head = Normalise(longerPlain[..i], libraryLonger).Replace(Wildcard.ToString(), "");
            if (head == shorterKey) return true;
            if (head.Length > shorterKey.Length) break;
        }
        return false;
    }

    [GeneratedRegex(@"\b(live|unplugged|acoustic|in concert|bootleg)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LiveWords();

    /// <summary>True for a release named as a live, unplugged or acoustic one: its tracks are not the studio recordings.</summary>
    public static bool IsLiveRelease(string? title) => !string.IsNullOrWhiteSpace(title) && LiveWords().IsMatch(title);

    // ── artist names ──────────────────────────────────────────────────────────

    /// <summary>
    /// An artist name as the library holds it, without the folder noise: "Simon &amp; Garfunkel discography" is "Simon and Garfunkel".
    /// Only artist names are cleaned; an album that has one of these words in its own name keeps it.
    /// </summary>
    public static string CleanArtistName(string? name, IReadOnlyList<string> noiseWords)
    {
        var text = StripYearSuffix(name);
        foreach (var word in noiseWords.OrderByDescending(w => w.Length))
        {
            if (string.IsNullOrWhiteSpace(word)) continue;
            var pattern = @"(?:\s*[-–—:]\s*|\s+|\s*[\(\[]\s*)(?:the\s+)?" + Regex.Escape(word.Trim()) + @"\s*[\)\]]?\s*$";
            var cleaned = Regex.Replace(text, pattern, "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
            if (cleaned.Length > 0 && cleaned.Length < text.Length) { text = cleaned; break; }
        }
        return text.Trim();
    }

    /// <summary>True when a library artist and a Deezer artist are the same name (after folder noise, accents, "&amp;" and the like).</summary>
    public static bool SameArtist(string? libraryName, string? deezerName, IReadOnlyList<string> noiseWords)
    {
        var lib = Normalise(CleanArtistName(libraryName, noiseWords), libraryText: true);
        var prov = Normalise(deezerName);
        return KeyEquals(lib, prov);
    }

    // ── junk ──────────────────────────────────────────────────────────────────

    /// <summary>True when the text contains any of the junk terms as whole words or phrases (karaoke, tribute, nightcore, ...).</summary>
    public static bool ContainsJunk(string? text, IReadOnlyList<string> terms)
    {
        if (string.IsNullOrWhiteSpace(text) || terms.Count == 0) return false;
        var padded = " " + Fold(text, libraryText: false) + " ";
        foreach (var term in terms)
        {
            var t = Fold(term, libraryText: false);
            if (t.Length > 0 && padded.Contains(" " + t + " ", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>One term per line (or comma separated); blanks dropped.</summary>
    public static List<string> ParseList(string? text) =>
        (text ?? "").Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    // ── images ────────────────────────────────────────────────────────────────

    [GeneratedRegex(@"/\d{2,4}x\d{2,4}-")]
    private static partial Regex SizeSegment();

    /// <summary>
    /// An image URL at the requested size. Deezer's images are addressed by a size segment ("/1000x1000-000000-80-0-0.jpg"); the
    /// service serves any size up to 1900x1900. Anything that does not look like one of its image addresses is returned as it was.
    /// </summary>
    public static string? ImageAtSize(string? url, int size) =>
        string.IsNullOrWhiteSpace(url) ? null : SizeSegment().Replace(url, $"/{size}x{size}-", 1);

    /// <summary>An image built from the hash Deezer gives with an album or artist, for answers that carry no URL.</summary>
    public static string? ImageFromHash(string? md5, string kind, int size) =>
        string.IsNullOrWhiteSpace(md5) ? null : $"https://cdn-images.dzcdn.net/images/{kind}/{md5}/{size}x{size}-000000-80-0-0.jpg";

    /// <summary>The year at the start of a Deezer date ("2001-03-07"), or null.</summary>
    public static int? YearOf(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y is > 1000 and < 3000 ? y : null;
}
