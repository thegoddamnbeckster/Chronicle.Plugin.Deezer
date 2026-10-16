# Chronicle.Plugin.Deezer

[![Latest Release](https://img.shields.io/github/v/release/thegoddamnbeckster/Chronicle.Plugin.Deezer?label=Chronicle.Plugin.Deezer&color=a238ff)](https://github.com/thegoddamnbeckster/Chronicle.Plugin.Deezer/releases/latest)

Music metadata plugin for [Chronicle](https://github.com/thegoddamnbeckster/Chronicle).

Fetches artist pictures, album covers (up to 1900x1900), genres, labels, release dates, track lists and ISRC / UPC codes from [Deezer](https://www.deezer.com/). A third source beside MusicBrainz (identity) and Last.fm (text), with no API key and no account. It is strongest where MusicBrainz is thin: singles, remixes, live tracks and recent releases.

**Plugin ID:** `chronicle.plugin.deezer`
**Version:** 1.0.1
**Auth:** None (no API key or account required)

---

## Supported Media Types

| Media Type | Hierarchy | Fields |
|------------|-----------|--------|
| `music` | Artist / Album / Track | artist: poster_url; album: title, year, poster_url, genres, cast; track: title, year, runtime_minutes, poster_url, genres, cast |

Deezer has no biographies, ratings or tags, so none are made up: those stay with Last.fm and MusicBrainz. Popularity (`fans`, `rank`) is not a score and is kept in the extended data. Priority is 30, so **MusicBrainz (10) and Last.fm (20) win by default** and Deezer fills what they do not have. Reorder it per field in Metadata Assignment (covers are where it is better).

---

## External ID Format

Stored under source `deezer`. The id carries its kind, because Deezer's numbers overlap (artist 27 and album 27 are different things):

| ID | Meaning |
|----|---------|
| `artist:27` | An artist |
| `album:302127` | An album, single or EP |
| `track:3135556` | A track |

**Fix match** accepts any of: a kind-prefixed id, a deezer.com artist / album / track URL (with or without a language segment), `isrc:` followed by a track's ISRC (a bare ISRC works too), or `upc:` followed by an album's UPC. A bare number is refused, because it would be ambiguous.

Codes published for other plugins (`ids`): `isrc` on tracks and `upc` on albums.

---

## Features

### How an album is found

Deezer's own search syntax (`artist:"X" track:"Y"`) returned nothing for every track tried, so the plugin searches **plain text** and does all the ranking itself. "artist title" is tried first; Deezer needs every word to match, so if that finds nothing the title alone is searched and judged by artist afterwards. Results are scored 0 to 99:

* the title matches (same title 60; only edition or credit qualifiers differ 55: "Doubt" and "Doubt (Deluxe Edition)"; one continues the other after a separator 40). A title that merely **appears inside** another is not a match ("Doubt" is not "No Doubt"; "Get Born Live" is not "Get Born");
* the artist matches (+30). **A known artist that does not match caps the score under Chronicle's gate of 50**: this removes covers, karaoke and tribute versions in one rule;
* **a different recording is never the same title**: a remix, live take, demo, acoustic or instrumental version differs from the plain title in either direction ("Don't" is not "Don't (Don Diablo Remix)"). Usual cuts are the same song ("TKO" is "TKO (Radio Edit)");
* junk (karaoke, tribute, nightcore, 8D, sped up, lullaby, ringtone ...) ranks far lower unless your own library item says so; the list is editable (Settings);
* compilations rank lower unless the library artist is Various Artists;
* **which edition**: among releases of one title, the one with the same track count as the library's, with the library's track names, or with the library's track lengths wins (the 11-track and the 21-track "Doubt");
* what the track list says can raise a score, never past a cap.

The library's own wording is kept as an alternate name, so "Simon & Garfunkel discography" is not turned away by Chronicle's name check.

### Names the library has damaged

* A trailing folder word on an **artist** ("discography", "greatest hits" ...) is ignored when matching the artist. An **album** that has one of these words in its own title keeps it.
* A broken character (`?` inside a word, or U+FFFD, where the library lost an accent or an apostrophe: "B?la Fleck", "Don?t", "blink?182") matches any one character, and the damaged word is left out of the search query.

### How an artist is found

An artist search by name: the name must be **equal** (a similar name is never accepted: "Dream Theater" is not "Dream Home Theater"). Popularity separates namesakes (663,757 fans against 23), and when two candidates are close the library's own albums are checked against each one's catalogue.

### How a track is found

**Inside the album it sits on.** The album is looked up once (ten minutes in memory) and its track list, which Deezer returns with it, serves every other track of that album: **no further request per track**. A track is matched there by title, with its position as a tie-break. A track whose album is not on Deezer is searched for on its own ("artist title", then the title alone). A track of a *live* album is not matched to the studio track of the same name.

### Rate limiting

Deezer documents 50 requests per 5 seconds. Requests are paced at least 150 ms apart (measured throughput is about 3 a second, bound by Deezer's own response time). Chronicle gives a plugin 25 seconds per call, so a quota answer is never waited out beyond one 5 second retry: it is thrown as an HTTP 429, and Chronicle then leaves the item **Pending** for the next run instead of marking it not found. Deezer reports quota, "busy" and "no data" as HTTP 200 with an error in the body; these are read, not mistaken for an empty answer. A call never makes more than 4 requests.

A first pass over a large library is slow by design: for 247 artists, 7,600 albums and 47,500 tracks it is about **27,000 requests to find everything, plus one per track to fetch it in full (ISRC, contributors, BPM): roughly 75,000 requests, about 6 hours**. Turn off `fetch_track_details` to drop the per-track request and finish in about 2.5 hours. Nothing is scraped, and images are only downloaded when something asks for one. Preview links are **not stored**: they are signed and expire within hours.

---

## Setup

No account or API key is needed.

1. In Chronicle → **Plugins**, install this plugin from the catalog.
2. Optionally open its settings (see Configuration).
3. Enable it. Use **Health check** to confirm Chronicle can reach Deezer.

---

## Configuration

| Setting | Required | Default | Description |
|---------|----------|---------|-------------|
| `cover_size` | | `max` | Size of covers and artist pictures: `max` (1900x1900, the largest Deezer serves), `xl` (1000), `big` (500) or `medium` (250). |
| `duration_tolerance_seconds` | | `5` | How far two track lengths may differ and still count as the same recording when the library's tracks are compared with an album's to choose between its editions. 1 to 60. |
| `fetch_track_details` | | `true` | A track found on its album costs no request to find, but its ISRC, contributors, BPM and release date need one more request per track. Turn off to roughly halve a first pass, at the cost of those fields. |
| `junk_terms` | | karaoke, tribute, nightcore ... | One per line. Words or phrases that mark a cover or bulk-uploaded release. Leave empty to use the default list. |
| `artist_folder_words` | | discography, greatest hits ... | One per line. Words that trail an artist's folder name without being part of it. Leave empty to use the default list. |

---

## Background Tasks

| Task | Default schedule | Description |
|------|------------------|-------------|
| Fetch Missing Music Metadata | Daily 04:50, on | Looks up Deezer metadata for artists, albums and tracks not yet enriched by Deezer |
| Re-sync All Music Metadata | Weekly Monday 04:20, off | Re-fetches everything to pick up new covers and corrected genres. Slow on a large library |

---

## Repository Structure

```
Chronicle.Plugin.Deezer/
├── Chronicle.Plugin.Deezer.csproj
├── manifest.json
├── DeezerMetadataProvider.cs   # IMetadataProvider: routing by hierarchy level, search, fetch by id, settings
├── Matchers.cs                 # scoring of artists, albums, tracks; edition choice
├── TextRules.cs                # normalising, broken characters, edition vs recording qualifiers, folder noise, junk, image sizes
├── MetadataMapper.cs           # Deezer records -> Chronicle metadata
├── DeezerClient.cs             # paced HTTP client, error bodies, rate-limit contract
├── DzRef.cs                    # understands ids, URLs, ISRC and UPC codes
├── TtlCache.cs                 # ten-minute memory of fetched records
├── DeezerModels.cs             # JSON models
└── tests/Chronicle.Plugin.Deezer.Tests/   # real recorded responses in Fixtures/
```

---

## Building

```powershell
dotnet build -c Release
```

Copy output to your Chronicle plugin directory:

```powershell
$pluginDir = "..\Chronicle\src\Chronicle.API\plugins\chronicle.plugin.deezer"
New-Item -ItemType Directory -Force $pluginDir
dotnet build -c Release
Copy-Item "bin\Release\net9.0\Chronicle.Plugin.Deezer.dll"       $pluginDir
Copy-Item "bin\Release\net9.0\Chronicle.Plugin.Deezer.deps.json" $pluginDir
Copy-Item "manifest.json"                                        $pluginDir
```

> **Important:** `Chronicle.Plugins.dll` must **not** be in the plugin directory — Chronicle provides it, and Serilog too
> (referenced at the exact version the host loads, without bundling).

Tests:

```powershell
dotnet test tests/Chronicle.Plugin.Deezer.Tests                                  # recorded responses, no network
$env:DEEZER_LIVE = "1"; dotnet test tests/Chronicle.Plugin.Deezer.Tests          # also the real Deezer
dotnet test tests/Chronicle.Plugin.Deezer.Tests --collect:"XPlat Code Coverage"  # coverage
```

---

## Development

The plugin references `Chronicle.Plugins` via a local project reference:

```xml
<ProjectReference Include="..\Chronicle\src\Chronicle.Plugins\Chronicle.Plugins.csproj"
                  Private="false" ExcludeAssets="runtime" />
```

Both repositories must be cloned as siblings for this to resolve:

```
<base>\
  Chronicle\
  Chronicle.Plugin.Deezer\
```

The design, with the measurements it rests on, is in the Chronicle repository: `docs/plans/2026-10-13-deezer-plugin-design.md`.

---

## License

MIT. Deezer's data is used through its public API only, for a personal library; see [developers.deezer.com](https://developers.deezer.com/).
