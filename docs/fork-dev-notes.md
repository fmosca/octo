# Octo fork — development notes for `fmosca/octo`

This repo is our fork of [`winters27/octo`](https://github.com/winters27/octo)
(GPL-3.0). It is where all octo and yt-dlp-shim code work happens. Deployment
truth lives separately in the `ansible-homeserver` repo — this doc maps the two
so a fresh session knows where each thing belongs.

## Branches, CI, images

- Branches: `main` (published, what CI builds) and `dev-fm` (working branch).
  Work lands on `dev-fm`, and `main` is fast-forwarded to it when it is ready to
  ship; keep the two in lockstep after a push (`git checkout dev-fm && git merge
  --ff-only main` and the reverse). `upstream` remote is kept for merges —
  sync with `git fetch upstream && git merge upstream/main`.
- CI: GitHub Actions (not our Woodpecker — the fork lives on GitHub where it
  forked from). `.github/workflows/docker.yml` builds and pushes on every push
  to `main` and on dated-release tags:
  - `octo` → `ghcr.io/fmosca/octo:{branch, sha, latest}`, release tags verbatim
    (e.g. `2026.10.04`).
  - `octo-ytdlp-shim` → `ghcr.io/fmosca/octo-ytdlp-shim:{main, sha, latest}`,
    built from `yt-dlp-shim/` (added 2026-10-05). Upstream never publishes a
    shim image, which is why the deploy side used to vendor the build context;
    see below.
- GHCR is the personal-namespaced registry that comes with GitHub — nothing to
  manage, images are public, the workflow authenticates with the built-in
  `GITHUB_TOKEN` (`packages: write`). No forge-side registry involved.
- The dated-release convention follows upstream: tag `2026.10.04`, but first
  bump `InformationalVersion` in `octo/octo.csproj` (the tag/version check in
  CI enforces it), then trigger a rebuild if needed:
  `gh api -X POST repos/fmosca/octo/actions/workflows/docker.yml/dispatches -f ref=<tag>`.
  There is no need to tag for ordinary fixes on `main`: the branch tag
  `ghcr.io/fmosca/octo:main` follows every push.

## Deploy side (ansible-homeserver)

The octo service lives in `files/opt/fmosca.dev/docker-compose.yml` (services
`octo` and `octo-ytdlp-shim`, both `ghcr.io/fmosca/...` images, no host port,
Authelia via Caddy in front), deployed by playbooks `06-docker-compose-file.yml`
and `09-docker-compose-run.yml`. The compose service carries `mem_limit: 1g`
(since 2026-10-06) — .NET Server GC plus a day of cache growth reached ~450 MiB
on a host that otherwise runs with a full swap, where unbounded growth turns any
leak into machine-wide pressure; the VictoriaMetrics rule `OctoMemoryTrend`
(alerts.yaml) warns when octo's working set would cross 95% of that cap within
6 hours. Until 2026-10-05 the shim was *vendored* into
that repo at `files/opt/fmosca.dev/yt-dlp-shim/` and built on the deploy host;
the vendoring was retired when CI started publishing the shim image, because
the two copies had already drifted twice (the loudness worker existed only in
the vendored copy; the live-ranking fix only in the fork). The single source of
truth for shim code is now this repo's `yt-dlp-shim/`.

## Test suite and host resources (2026-10-06)

`octo.Tests` runs 3,460 tests, and the boot-flavoured ones each used to build
and dispose a full `WebApplicationFactory<Program>` host — a real ASP.NET Core
container with the DI tree of the production app. Three measured consequences:

- **CPU:** the dominant cost is JIT-compiling a fresh host per test; xUnit's
  default parallelism (all collections at once) turned that into a sustained
  multi-core burn on the 4-core homelab box. `octo.Tests/xunit.runner.json`
  (new, copied to output by the csproj) caps `maxParallelThreads` at 2.
- **Memory:** concurrent host boots peaked at ~1.2–1.5 GiB extra RSS and drove
  the saturated host into swap. Run capped slices when developing locally:
  `systemd-run --user --scope -p CPUQuota=100% -p MemoryMax=1G -- nice -n 19
  dotnet test ...`, or run a filtered subset. CI runners are unaffected.
- **inotify:** every boot creates watchers that are never fully released
  (upstream dotnet/runtime#115557 — `HostBuilder`'s `SetBasePath`-created
  `PhysicalFileProvider` is not disposed; fix merged for a later 9.0.x, and
  `WebApplicationFactory` inherits the path). Against the 128-instance
  per-user cap, a full run exhausted instances mid-suite (ENOSPC) and failed
  unrelated tests. Two app-side mitigations:
  - `octo/Program.cs` gates the settings-file `reloadOnChange` watcher on
    `IsProduction()` — a production process builds the host once, so nothing
    changes there; test and dev hosts read `/app/config/settings.json` once.
  - Boot-heavy test classes that proved stateless share one host via
    `IClassFixture<>` (`AdminContractTests`: 22 tests on one boot;
    `LastFmRadioNativeApiTests`: 2) instead of one boot per test.
  - Parameterized factory families (per-test settings variants, counter or
    request-log asserts — `Navidrome.Pings`, `Only("/rest/...")`,
    `Metadata.Verify(Times.Once)`) deliberately keep per-test boots: their
    asserts assume virgin state, and sharing would trade reliability for
    speed. xUnit v2 fixture activation requires a true parameterless
    constructor — default arguments on an optioned one do not count
    (`RadioWebFactory` carries a `public` parameterless delegating to the
    `internal` optioned one for this reason).
- `Program` is now `public partial class Program { }` at the foot of
  `octo/Program.cs`: fixture classes must be `public`, and `public sealed class
  X : WebApplicationFactory<Program>` requires a public base type. CI's
  `DOTNET_HOST_FACTORY_RESOLVER_DEFAULT_TIMEOUT_IN_SECONDS` stays.
- `AdminWebFactory` and friends write settings into their own temp directories,
  not `/app/config`; the gating above does not affect what they assert.

## Debugging against the running server

- Service state, ports, mounts, auth and settings: the `octo` entries in the
  deploy repo's `RUNBOOK.md` (§35 for octo + shim, §36 for the ReplayGain
  pipeline that crosses both repos).
- `docs/octo-slskd-move-race.md` — write-up of the slskd move race that drops
  album-heart tracks (fix in progress on `dev-fm`).
- Deploy a new build: run `09-docker-compose-run.yml` after CI publishes; the
  compose files pin `:main` tags, so a plain re-run picks the new image up.
- Server logs: `docker logs octo` / `docker logs octo-ytdlp-shim` on the host.

## Search-result dedup and the artist page (2026-10-05)

The outside-catalog album rows come from Deezer, which lists every edition of a
record as its own row — Coltrane's page was 52 (search) / 98 (artist) rows where
about 25 records exist. Two measures, both built on `SongIdentity`'s keys plus
the Deezer service:

1. **Title fold, everywhere, always** — `SongIdentity.AlbumCoreKey(artist, title)`
   strips the edition vocabulary (deluxe/expanded/remaster/year/mono…), keeps
   live versions, credited remixes, guests and volume parts (different records),
   and includes the artist (editions credited to "John Coltrane Quartet" vs
   "John Coltrane" stay apart, same as Navidrome's own album identity).
   `DeezerMetadataService.SearchAlbumsAsync` and `GetArtistAlbumsAsync` fold on
   it at parse time; the artist page keeps the better-ranked record type
   (`ReleaseRank`).
2. **Release-group fold, warm, second visit on** — a barcode (UPC, on the album
   detail) names a pressing everywhere; `MusicBrainzClient
   .FindReleaseGroupByBarcodeAsync` turns one into the music database's
   release-group id. After an artist page renders, `WarmReleaseGroups` asks for
   at most 8 albums per visit at the music database's 1 req/s pace; the next
   listing folds rows whose ids tie (a renamed edition, different spelling the
   title rules cannot hear). Rows with no answer stay as they are. The fold
   consult also runs over `SearchAlbumsAsync` results and over cached artist
   listings, so a warmed id trims the page on later visits without refetching.
3. **Singles are not an artist page's business** — a single is one song put out
   on its own, and the catalog lists every one, so an album's own singles
   ("Tonight", "Tonight (Remixes)", "Tonight (8-Bit Button Masher Remix)")
   repeated its title down the page under releases that are the same record
   under the identity rules and so cannot be folded. `SubSonicController
   .ArtistPageAlbums` keeps albums, EPs and compilations and drops rows whose
   release type is only `single`, on the outside artist page (Subsonic
   `getArtist`) and on the native one (`/api/artist`, `/api/album?artist_id=`),
   where the counts beside the page come from the same list and so agree with
   it. An artist whose catalog is singles keeps them, so a page cannot come back
   empty, and a row the catalog gave no type for is kept — it may be an album.
   The singles stay reachable through search and through their own album pages.

Search latency: the phone saw 4–8 s on a cold "coltrane" search. That was the
respond-time build waiting for two full Deezer enrich waves and the YouTube
duration resolve. `Subsonic__WaitForSearchDurations=false` (deploy side, set
2026-10-05) answers with Deezer lengths immediately (cold 0.8 s, warm 0.2 s,
measured through the Caddy-adjacent fast path); video-accurate lengths arrive
off-path via `getSong`. `SearchEnrichLimit` is 6 (one Deezer wave); rows past it
enrich from cache inline and are warmed for the next search by `WarmLengths` off
the critical path.

Artist biography: `getArtistInfo2` for an outside-catalog artist reads last.fm's
`artist.getinfo` for the artist's name and answers `biography` (plus a
`biographySummary`); empty means the key is unset, the catalog has no text for
that name, or the page last.fm answered names a different act (`artist.name` in
the body is checked with `SongIdentity.SameArtistName`, so a fuzzy match for a
near-miss spelling cannot ship someone else's text). Library artists still relay
Navidrome's own biography (needs Last.fm keys in Navidrome). The same biography
rides the outside `getArtist` response, and the Navidrome-native artist object
carries `biography` and a compact `similarArtists` list too. Similar-artist rows:
outside `getArtist`/`getArtistInfo2` list catalog related artists
(`RelatedArtistsAsync`; a row named like the page artist is skipped, because two
acts of one name hash to one registry id and the row would re-bind the page's
settled catalog id), and `getTopSongs` answers an outside artist's most-played
songs as play-registered songs — by registry id as Arpeggi sends it, or by the
spec's `artist` name. The per-page budget: `RelatedArtistsAsync` is cached and
shared like the other artist lookups (first page visit after a deploy or cache
expiry pays one `/artist/{id}/related` call; 12 rows; about 190 ms measured), and
`getTopSongs` one `/artist/{id}/top` call (5 rows, about 240 ms). Both run on the
interactive lane: page-render data on the background lane waits behind cache
warming and times out, per the `AlbumTrackCountAsync` comment. Last.fm's API terms
require attributing their data — the summaries and names come from Last.fm; our
own iOS app shows them inside octo-served pages, and the sources are named here
for any future surface.

## getCoverArt caches (2026-10-06)

The memory-incident review left octo serving every getCoverArt with fresh work:
a registry id re-decoded and re-badged the source art (full RGBA32 decode plus
re-encode per repeat), and a library id re-relayed the whole body from Navidrome.
The bursts behind it — an app refreshing a queue or re-fetching after dropping
the connection — now hit `CoverResponseCache` (Services/CoverArt/, singleton):

- **Badged external art** (registry branch): keyed by id plus plain/badged, not
  by size — the source art is fetched at a fixed upstream resolution and the
  response is the same picture regardless of the `size` the client asked at.
- **Relayed library art** (the local-id branch): keyed by id plus size, because
  Navidrome sizes the bytes to the request; the answer is caller-independent, so
  it is cached only after `RefuseUnlessSignedInAsync` has accepted the caller on
  that very request. A cache hit never skips the sign-in check — Navidrome's
  verdict is itself memoized for ten minutes (`CredentialCheck`), so a repeat
  costs no upstream ping either.
- Bounds: the budget is bytes, and the cache owns the accounting the way the
  aggregator's does — 128 MiB `SizeLimit`, each entry charged its length,
  least-recent entries evicted by MemoryCache when an addition would exceed it
  (probed on net9.0: compaction is synchronous at `Set`, and may overshoot the
  trim, dropping more than needed). Empty bodies and bodies larger than the
  whole budget never enter.
- Expiry: a hit slides the entry's one-hour expiry back, but never past a
  six-hour absolute floor, so a cover whose art changes underneath is corrected
  at worst six hours late however hot the entry is. The relayed library entry
  also carries the content type Navidrome answered under (it serves PNG covers
  as well as JPEG), so a cached PNG is still labelled PNG.
- The credential gate on the library branch refuses only wrong credentials.
  An unreachable Navidrome is NOT an error there: the fetch below fails into
  the existing unbranded placeholder, exactly as before the cache — an error
  envelope is what makes a client drop the row, and a placeholder keeps it.

Tests: `CoverResponseCacheTests` (fast: keying, empty and oversized
rejection, a budget that holds realistic 50 KB covers, trim at the bound)
and `CoverCacheEndpointTests` (boot, a Navidrome fake that counts
getCoverArt calls: a repeat relays once, a different size is a different
response, wrong credentials after a warm cache still get the refusal and no
second relay, a PNG answer keeps its content type on the cached repeat, and
an unreachable Navidrome serves the placeholder rather than an error). The
boot tests assert against the response bytes and content types the fake
serves per size, which is what would catch a keying slip.

## Cover fonts load only when a name needs them (2026-10-07)

`CoverFonts.For` sets every line of a drawn cover and used to attach the whole
installed fallback list (`Fallbacks`: the four Noto CJK faces, DejaVu Sans and
Symbola in this image) to the `RichTextOptions` of every measured line.
SixLabors.Fonts parses a family in full the first time a line that needs it is
measured — and it parses every *attached* family even when the text is Latin.
A probe against SixLabors.Fonts 2.1.3 inside the octo image measured both
ends: a Latin line with nothing attached costs 3.6 MB (the primary's own
parse), the same line with six families attached costs +108.6 MB, and the
line measured afterwards costs +0.0 MB because everything was already parsed.
So octo's first cover — the boot `Warm()` draw, whose name is Latin — parsed
all nine faces and held ~120 MB of glyph tables for the life of the process
(the finding behind the 2026-10-07 GC work), on a library where every name
that reaches a cover is Inter-covered: the stations are `Your Mix`,
`Discovery Mix`, `<artist> Radio` and `<tag> Radio`, mixes are named from
genre and decade tags, and none of the 2,068 artists or the 188 genres that
clear the mix threshold needs a fallback font.

`For` now returns only the families the line actually needs:

- The leading family is still the installed one covering most of the letters
  Inter lacks, but the scan stops at the first family that covers **all** of
  them, so a Japanese name parses one face, not four.
- `Behind` collects the characters of the text the leading family cannot draw
  and attaches one family for each, in the design's order; a character that is
  not a letter asks Symbola and DejaVu Sans first (the fonts installed for
  symbols and emoji), and a character an already attached family draws
  attaches nothing. The question parses the font, so the order is the cost: a
  Latin name asks no installed family at all, and a name nothing installed can
  draw asks each once, as it did before.
- When a non-Inter family leads, Inter stays first behind it, so the Latin in
  a Japanese name still sets in Inter.

The `Choices` cache holds the per-(text, weight) verdicts, so the asking
happens once per distinct name. `Warm()` still draws its one cover at boot;
that cover now parses Inter and nothing else, and what stays resident is the
design's own three Inter cuts rather than Inter plus six installed faces.

Tests: `NamesInterDraws_AttachNoOtherFont` (a Latin name attaches nothing and
is set in Inter; an emoji attaches at most one family, and that family holds
the emoji) and `UnicodeNames_DrawTheirLetters`, which now asserts the
*returned* chain draws every character of the name, not that some installed
font somewhere would.

Known bound, left as-is: the fallback list still leads with the CJK faces, so
the first Hebrew or Arabic name parses the four CJK faces before DejaVu
answers — once per process, and only on an image with no Noto Hebrew or
Arabic. Reordering by the missing script is the follow-up if that ever
matters.