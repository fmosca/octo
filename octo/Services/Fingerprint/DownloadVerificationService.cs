using Microsoft.Extensions.Options;
using Octo.Models.Domain;
using Octo.Models.Settings;
using Octo.Services.Common;

namespace Octo.Services.Fingerprint;

public enum VerificationVerdict
{
    /// <summary>AcoustID identified the file as the track that was asked for, or the ISRC that
    /// was asked for is on the file's tags or on the recording AcoustID named.</summary>
    Confirmed,

    /// <summary>
    /// Nothing could be established: verification off, no key, no fpcalc, AcoustID down or
    /// rate limited, no result above the score threshold, or no AcoustID entry at all. The
    /// file is kept and nothing is remembered.
    ///
    /// The last of those is the one that matters. A legitimately obscure track, which is the
    /// music Soulseek is best at and the reason Octo uses it, has no AcoustID entry. Treating
    /// absence as evidence would make this feature worst exactly where the library is rarest.
    /// </summary>
    Inconclusive,

    /// <summary>
    /// The file was identified with confidence and it is a different recording, or it holds
    /// no decodable audio at all. The only verdict that discards a file or writes a denial.
    /// </summary>
    Mismatch,
}

/// <summary>
/// Why a verdict was Inconclusive. Only the last three are questions a person can settle by
/// listening, so only those reach the Review playlist (#47). The others are Octo not asking.
/// </summary>
public enum InconclusiveReason
{
    None,
    Disabled,
    NotFingerprinted,
    LookupFailed,
    NoEntry,
    BelowThreshold,
    SourceDisagreed,
}

public sealed record VerificationResult
{
    public VerificationVerdict Verdict { get; init; } = VerificationVerdict.Inconclusive;
    public double Score { get; init; }
    public string? MatchedTitle { get; init; }
    public string? MatchedArtist { get; init; }
    public string? MatchedAlbum { get; init; }
    public int? MatchedYear { get; init; }
    public string? RecordingId { get; init; }
    public string DenyReason { get; init; } = "";
    public bool TagsAuthoritative { get; init; }

    public InconclusiveReason Reason { get; init; }

    /// <summary>The recording that agreed with the request. Set only when Confirmed.</summary>
    public AcoustIdRecording? Match { get; init; }

    /// <summary>Kept so a person's confirmation can be sent back to AcoustID (#47).</summary>
    public string? Fingerprint { get; init; }
    public int DurationSeconds { get; init; }

    /// <summary>The whole answer, every result and release, so the chooser can weigh them all.
    /// Set whenever the service answered, even below the threshold.</summary>
    public AcoustIdLookup? Lookup { get; init; }

    /// <summary>The service's id for the fingerprint that confirmed the recording.</summary>
    public string? AcoustId { get; init; }

    /// <summary>
    /// The one recording AcoustID proposed below the threshold that agrees with the request on
    /// title, artist and length. The first MusicBrainz id a person's Keep may submit, and null
    /// when there were none or more than one.
    /// </summary>
    public string? CandidateRecordingId { get; init; }

    /// <summary>
    /// What confirmed or kept the file when it was not the fingerprint's title and artist alone,
    /// in words for the log: an ISRC in the file's tags, or one on the MusicBrainz recording the
    /// fingerprint named. Null otherwise.
    /// </summary>
    public string? Evidence { get; init; }

    public bool NeedsReview => Verdict == VerificationVerdict.Inconclusive
        && Reason is InconclusiveReason.NoEntry or InconclusiveReason.BelowThreshold
            or InconclusiveReason.SourceDisagreed;

    public static readonly VerificationResult Inconclusive = new() { Reason = InconclusiveReason.Disabled };

    /// <summary>
    /// Whether a song's album IS the MusicBrainz release its fingerprint matched, so the ids and
    /// artwork that belong to that release describe the album the tags name. A download tagged
    /// with a compilation's name must not get the original album's cover or group id.
    /// </summary>
    public static bool AlbumIsFromRelease(Song song) =>
        !string.IsNullOrWhiteSpace(song.MusicBrainzAlbumTitle)
        && SongIdentity.Key(song.Album) == SongIdentity.Key(song.MusicBrainzAlbumTitle);

    public string Describe() => string.IsNullOrEmpty(MatchedArtist) && string.IsNullOrEmpty(MatchedTitle)
        ? "a different recording"
        : $"'{MatchedArtist} - {MatchedTitle}'";

    /// <summary>
    /// Record what a confirmed match proved, and overwrite the song's name from it when
    /// tagging from MusicBrainz is on.
    ///
    /// The ids are written whenever the match is confirmed. An id says what the file IS and
    /// changes nothing a person reads, and it is what lets every later pass skip identifying
    /// the same file again (#48). The release id stays in memory for the cover lookup and is
    /// never written: Navidrome groups albums by MUSICBRAINZ_ALBUMID before the album name, so
    /// one track carrying it beside another without it would split an album in two.
    ///
    /// The name overwrite is unconditional where the Deezer fill is conditional. That one fills
    /// only what is missing, because a peer's own tags beat nothing. A confirmed fingerprint
    /// match beats the peer, which is the entire point of the setting.
    /// </summary>
    public void ApplyTagsTo(Song song)
    {
        if (Verdict != VerificationVerdict.Confirmed) return;

        if (!string.IsNullOrEmpty(RecordingId)) song.MusicBrainzRecordingId = RecordingId;
        if (Match is { } match)
        {
            if (match.Credits.Count > 1) song.Artists = match.Credits.Select(credit => credit.Name).ToList();
            if (match.Credits.Count == 1 && match.Credits[0].ArtistId is { Length: > 0 } artistId)
                song.MusicBrainzArtistIds = [artistId];
            if (match.PrimaryArtist is { Length: > 0 } primary) song.PrimaryArtist = primary;
            song.MusicBrainzReleaseId = match.Release?.ReleaseId;
            song.MusicBrainzReleaseGroupId = match.Release?.ReleaseGroupId;
            song.MusicBrainzAlbumTitle = match.AlbumTitle;
        }

        if (!TagsAuthoritative) return;
        if (!string.IsNullOrEmpty(MatchedTitle)) song.Title = MatchedTitle;
        if (!string.IsNullOrEmpty(MatchedArtist)) song.Artist = MatchedArtist;
        if (!string.IsNullOrEmpty(MatchedAlbum)) song.Album = MatchedAlbum;
        if (MatchedYear is > 0) song.Year = MatchedYear;

        if (Match?.Release is { } release && !string.IsNullOrEmpty(MatchedAlbum))
        {
            if (release.TrackNumber is > 0) song.Track = release.TrackNumber;
            if (release.TrackCount is > 0) song.TotalTracks = release.TrackCount;
            if (release.DiscNumber is > 0) song.DiscNumber = release.DiscNumber;
            if (!string.IsNullOrEmpty(release.AlbumArtist)) song.AlbumArtist = release.AlbumArtist;
            song.IsCompilation = release.IsCompilation;
        }
    }
}

/// <summary>
/// Asks what a finished download actually contains, rather than what its name and advertised
/// length claim.
///
/// Every failure path accepts the file. That is the correct trade and also the dominant risk:
/// a broken key, a missing binary, a mishandled gzip and a parse error all look exactly like
/// "everything is fine", which is why the pieces below log refusals at Warning.
/// </summary>
public sealed class DownloadVerificationService
{
    /// <summary>How many of the recordings a fingerprint named are asked for their ISRCs. Each is
    /// a MusicBrainz call a second apart, spent only when the request carried an ISRC and the
    /// recordings' names disagreed with it.</summary>
    internal const int MaxIsrcLookups = 3;

    private readonly AudioFingerprinter _fingerprinter;
    private readonly AcoustIdClient _client;
    private readonly IOptionsMonitor<SoulseekSettings> _options;
    private readonly ILogger<DownloadVerificationService> _logger;
    private readonly MusicBrainzClient? _musicBrainz;
    private readonly SpectrumAnalyzer? _spectrum;

    public DownloadVerificationService(AudioFingerprinter fingerprinter, AcoustIdClient client,
        IOptionsMonitor<SoulseekSettings> options, ILogger<DownloadVerificationService> logger,
        MusicBrainzClient? musicBrainz = null, SpectrumAnalyzer? spectrum = null)
    {
        _fingerprinter = fingerprinter;
        _client = client;
        _options = options;
        _logger = logger;
        _musicBrainz = musicBrainz;
        _spectrum = spectrum;
    }

    /// <summary>
    /// Whether a file that claims to be lossless really is, by its spectrum. Unknown, and no
    /// work at all, when the check is off, the file does not claim to be lossless, or ffmpeg
    /// cannot say. Separate from <see cref="VerifyAsync"/> because the answer never rejects a
    /// file: it only decides which of two right songs is kept.
    /// </summary>
    public async Task<SpectrumReport> CheckLosslessAsync(string path, string? requestedArtist, string? requestedTitle)
    {
        var settings = _options.CurrentValue;
        if (!settings.DetectTranscodes || _spectrum is null || !SpectrumAnalyzer.ClaimsLossless(path))
            return SpectrumReport.Unknown("not checked");

        var report = await _spectrum.AnalyzeAsync(path, settings.EffectiveTranscodeCheckTimeoutSeconds);
        if (report.IsLikelyLossy)
            _logger.LogWarning("the {Format} for '{Artist} - {Title}' is {Spectrum}",
                Path.GetExtension(path).TrimStart('.').ToUpperInvariant(), requestedArtist, requestedTitle, report.Describe());
        else
            // Logged when it passes too, for the same reason a confirmation is: silence on
            // success looks exactly like a check that never ran.
            _logger.LogInformation("spectrum of '{Artist} - {Title}': {Spectrum}", requestedArtist, requestedTitle, report.Describe());
        return report;
    }

    /// <summary>AcoustID can answer at all.</summary>
    public bool HasApiKey => !string.IsNullOrWhiteSpace(_options.CurrentValue.AcoustIdApiKey);

    /// <summary>
    /// The user asked Octo to police what peers deliver. Governs the deny-list on its own,
    /// because the duration check needs no API key and its verdicts are just as durable.
    /// </summary>
    public bool RemembersRejections => _options.CurrentValue.VerifyDownloads;

    /// <summary>
    /// A lookup can actually happen. Split from the above deliberately, in the shape
    /// LastFmService uses for HasApiKey/IsRadioEnabled: collapsing them would mean a user who
    /// switches verification on without a key gets no bad-peer memory either, and the
    /// duration check quietly keeps re-downloading the same wrong file.
    /// </summary>
    public bool IsFingerprintingEnabled => RemembersRejections && HasApiKey;

    /// <summary>
    /// No CancellationToken parameter, deliberately. There must be no way for a caller who
    /// has already given up to skip verification on a file that is about to enter the library.
    /// </summary>
    public async Task<VerificationResult> VerifyAsync(string path, string? requestedArtist, string? requestedTitle,
        string? requestedIsrc = null)
    {
        if (!RemembersRejections) return VerificationResult.Inconclusive;

        // What the file's own tags say it is, when the request named an ISRC to hold them to.
        // A header read, and it needs no API key: with no key it is the only question asked.
        var isrc = SongIdentity.NormalizeIsrc(requestedIsrc);
        var tagged = isrc is null ? [] : ReadIsrcs(path);
        var taggedMatch = isrc is not null && tagged.Contains(isrc);

        var verdict = HasApiKey
            ? await IdentifyAsync(path, requestedArtist, requestedTitle, isrc, taggedMatch)
            : VerificationResult.Inconclusive;

        verdict = WithTaggedIsrc(verdict, isrc, taggedMatch);
        if (verdict.Verdict == VerificationVerdict.Confirmed && verdict.Evidence is { } evidence)
            _logger.LogInformation("confirmed '{Artist} - {Title}' by ISRC {Isrc}: {Evidence}",
                requestedArtist, requestedTitle, isrc, evidence);
        else if (isrc is not null && tagged.Count > 0 && !taggedMatch)
            // Not a rejection: a re-release or a remaster is often given a new code.
            _logger.LogInformation("the file for '{Artist} - {Title}' is tagged ISRC {Tagged}, not the {Isrc} asked for; "
                + "that alone decides nothing", requestedArtist, requestedTitle, string.Join(", ", tagged), isrc);
        return verdict;
    }

    /// <summary>
    /// A file whose own tags carry the ISRC that was asked for is that recording, when nothing
    /// better could be established: AcoustID off, down, without an entry or below the
    /// threshold. A confident fingerprint of something else is not overruled by a tag, since
    /// tags are copied and audio is not, and neither is a fingerprint that named a recording
    /// whose ISRCs were not found (that one goes to a person).
    /// </summary>
    internal static VerificationResult WithTaggedIsrc(VerificationResult verdict, string? isrc, bool taggedMatch)
    {
        if (!taggedMatch || verdict.Verdict != VerificationVerdict.Inconclusive
            || verdict.Reason == InconclusiveReason.SourceDisagreed) return verdict;
        return verdict with
        {
            Verdict = VerificationVerdict.Confirmed,
            Reason = InconclusiveReason.None,
            Evidence = $"the file's own tags carry the requested ISRC {isrc}",
        };
    }

    /// <summary>
    /// The fingerprint and the AcoustID lookup, and the ISRC check of the recordings it named.
    /// The question VerifyAsync asked alone before ISRCs were evidence.
    /// </summary>
    private async Task<VerificationResult> IdentifyAsync(string path, string? requestedArtist, string? requestedTitle,
        string? isrc, bool taggedMatch)
    {
        var settings = _options.CurrentValue;
        var fingerprint = await _fingerprinter.FingerprintAsync(path,
            settings.EffectiveFingerprintSeconds, settings.EffectiveFingerprintTimeoutSeconds);

        if (fingerprint.Outcome == FingerprintOutcome.Undecodable)
            return new VerificationResult
            {
                Verdict = VerificationVerdict.Mismatch,
                DenyReason = "delivered a file with no decodable audio",
            };

        if (fingerprint.Outcome != FingerprintOutcome.Ok || string.IsNullOrEmpty(fingerprint.Fingerprint))
            return new VerificationResult { Reason = InconclusiveReason.NotFingerprinted };

        // TagLib's duration, not fpcalc's. -length pins how much audio is fingerprinted, and
        // staking a rejection on whether that also truncates the reported duration would
        // reject every track over two minutes if it does.
        var seconds = ReadDurationSeconds(path);
        if (seconds <= 0) seconds = fingerprint.DecodedSeconds;
        if (seconds <= 0) return new VerificationResult { Reason = InconclusiveReason.NotFingerprinted };

        var lookup = await _client.LookupAsync(settings.AcoustIdApiKey, fingerprint.Fingerprint,
            seconds, settings.EffectiveAcoustIdTimeoutSeconds);
        if (lookup is null) return new VerificationResult { Reason = InconclusiveReason.LookupFailed };
        if (!lookup.IsOk)
        {
            _logger.LogWarning("acoustid refused the lookup for {Path}: {Error}", path, lookup.Error);
            return new VerificationResult { Reason = InconclusiveReason.LookupFailed };
        }
        if (lookup.Results.Count == 0)
        {
            _logger.LogInformation(
                "acoustid has no entry for '{Artist} - {Title}'; keeping the file. Obscure music is "
                + "exactly what Soulseek is for, so an absent match is never treated as a mismatch.",
                requestedArtist, requestedTitle);
            // Kept with the fingerprint: this is the one case a person listening can settle, and
            // the one where their answer is worth sending back to AcoustID.
            return new VerificationResult
            {
                Reason = InconclusiveReason.NoEntry,
                Fingerprint = fingerprint.Fingerprint,
                DurationSeconds = seconds,
                Lookup = lookup,
            };
        }

        // NameFromMatch implies authoritative tags: a path from MusicBrainz beside tags from the
        // source is exactly the split it exists to remove (#48).
        var verdict = Decide(lookup, requestedArtist, requestedTitle,
            settings.EffectiveMinScoreFraction, settings.TagFromMusicBrainz || settings.NameFromMatch,
            seconds) with
        {
            Fingerprint = fingerprint.Fingerprint,
            DurationSeconds = seconds,
        };

        // A recording whose name reads differently from the request may still be it: a title in
        // its own script, or translated. Its ISRCs settle that when the request carried one.
        if (verdict.Verdict == VerificationVerdict.Mismatch && isrc is not null && _musicBrainz is not null)
        {
            var recordingIsrcs = await RecordingIsrcsAsync(lookup, settings.EffectiveMinScoreFraction,
                settings.EffectiveAcoustIdTimeoutSeconds);
            verdict = SettleByIsrc(verdict, lookup, settings.EffectiveMinScoreFraction,
                settings.TagFromMusicBrainz || settings.NameFromMatch, isrc, recordingIsrcs, taggedMatch);
        }

        // A confirmation is logged too, not just a refusal. The dominant risk in this feature is
        // that a broken key, a missing binary or a mangled request makes it accept everything
        // while looking healthy, and silence on success is indistinguishable from never running.
        if (verdict.Verdict == VerificationVerdict.Confirmed && verdict.Evidence is null)
            _logger.LogInformation(
                "acoustid confirmed '{Artist} - {Title}' at {Score:P0}{Album}",
                requestedArtist, requestedTitle, verdict.Score,
                string.IsNullOrEmpty(verdict.MatchedAlbum) ? "" : $" from '{verdict.MatchedAlbum}'");

        if (verdict.Verdict == VerificationVerdict.Inconclusive && verdict.Reason == InconclusiveReason.SourceDisagreed)
            _logger.LogInformation(
                "acoustid names {Actual} for '{Artist} - {Title}', but {Evidence}; keeping the file and asking about it",
                verdict.Describe(), requestedArtist, requestedTitle, verdict.Evidence);
        else if (verdict.Verdict == VerificationVerdict.Inconclusive)
            _logger.LogInformation(
                "acoustid's best match for '{Artist} - {Title}' scored {Best:P0} against a {Threshold:P0} "
                + "threshold, so it decides nothing and the file is kept",
                requestedArtist, requestedTitle,
                lookup.Results.Max(result => result.Score), settings.EffectiveMinScoreFraction);

        return verdict;
    }

    /// <summary>
    /// The ISRCs MusicBrainz lists for the first few recordings of the best qualifying result, by
    /// recording id. A recording that could not be asked is left out; one asked that lists none
    /// is present with an empty list. Bounded by one timeout for all of them.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> RecordingIsrcsAsync(
        AcoustIdLookup lookup, double threshold, int timeoutSeconds)
    {
        var found = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (BestQualifying(lookup, threshold) is not { } best) return found;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds + 2 * MaxIsrcLookups));
        try
        {
            foreach (var recording in best.Recordings.Where(recording => recording.RecordingId.Length > 0)
                         .DistinctBy(recording => recording.RecordingId).Take(MaxIsrcLookups))
                if (await _musicBrainz!.FetchIsrcsAsync(recording.RecordingId, cts.Token) is { } isrcs)
                    found[recording.RecordingId] = isrcs;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("musicbrainz took too long to list ISRCs; deciding on what it answered");
        }
        return found;
    }

    private static AcoustIdResult? BestQualifying(AcoustIdLookup lookup, double threshold) =>
        lookup.Results
            .Where(result => result.Score >= threshold && result.Recordings.Count > 0)
            .OrderByDescending(result => result.Score)
            .FirstOrDefault();

    /// <summary>
    /// A fingerprint named recordings whose titles and artists read differently from the
    /// request, and the request carried an ISRC. One of those recordings listing that ISRC makes
    /// it the recording asked for, whatever its name. None of them listing any ISRC at all,
    /// while the file's own tags carry the requested one, is a disagreement between two
    /// sources a person can settle, so the file is kept and asked about rather than deleted.
    /// Recordings that list other ISRCs change nothing: the verdict stays as it was.
    /// </summary>
    internal static VerificationResult SettleByIsrc(VerificationResult verdict, AcoustIdLookup lookup,
        double threshold, bool tagsAuthoritative, string isrc,
        IReadOnlyDictionary<string, IReadOnlyList<string>> recordingIsrcs, bool taggedMatch)
    {
        if (verdict.Verdict != VerificationVerdict.Mismatch || BestQualifying(lookup, threshold) is not { } best)
            return verdict;

        var agreed = best.Recordings.FirstOrDefault(recording =>
            recordingIsrcs.TryGetValue(recording.RecordingId, out var isrcs) && isrcs.Contains(isrc));
        if (agreed is not null)
            return Confirm(best, agreed, tagsAuthoritative) with
            {
                Fingerprint = verdict.Fingerprint,
                DurationSeconds = verdict.DurationSeconds,
                Lookup = lookup,
                Evidence = $"MusicBrainz lists the requested ISRC {isrc} on '{agreed.ArtistCredit} - {agreed.Title}'",
            };

        if (taggedMatch && recordingIsrcs.Count > 0 && recordingIsrcs.Values.All(isrcs => isrcs.Count == 0))
            return verdict with
            {
                Verdict = VerificationVerdict.Inconclusive,
                Reason = InconclusiveReason.SourceDisagreed,
                DenyReason = "",
                Evidence = $"the file's own tags carry the requested ISRC {isrc} and MusicBrainz lists none to contradict it",
            };

        return verdict;
    }

    /// <summary>
    /// The whole decision, separated from the I/O so it can be driven directly. Every branch
    /// here either keeps a file or deletes one, and the sealed fingerprinter and HTTP client
    /// above make the orchestration awkward to mock for no benefit.
    /// </summary>
    internal static VerificationResult Decide(AcoustIdLookup lookup, string? requestedArtist,
        string? requestedTitle, double threshold, bool tagsAuthoritative, int durationSeconds = 0) =>
        DecideCore(lookup, requestedArtist, requestedTitle, threshold, tagsAuthoritative, durationSeconds)
            with { Lookup = lookup };

    private static VerificationResult DecideCore(AcoustIdLookup lookup, string? requestedArtist,
        string? requestedTitle, double threshold, bool tagsAuthoritative, int durationSeconds)
    {
        var qualifying = lookup.Results
            .Where(result => result.Score >= threshold && result.Recordings.Count > 0)
            .OrderByDescending(result => result.Score)
            .ToList();

        // Below the threshold an answer is ignored, never acted on. That is why raising
        // MinMatchScore makes Octo MORE permissive rather than less.
        if (qualifying.Count == 0)
        {
            // A result above the threshold with no recordings is a fingerprint AcoustID knows and
            // MusicBrainz does not: exactly the gap a person's confirmation can fill.
            var known = lookup.Results.Any(result => result.Score >= threshold);
            return new VerificationResult
            {
                Reason = known ? InconclusiveReason.NoEntry : InconclusiveReason.BelowThreshold,
                CandidateRecordingId = AgreeingCandidate(lookup, requestedArtist, requestedTitle, durationSeconds),
            };
        }

        var best = qualifying[0];

        // Any, not first: one AcoustID id maps to several MusicBrainz recordings when the
        // same audio ships on an album and a compilation, and demanding the first would
        // reject correct files.
        var agreed = best.Recordings.FirstOrDefault(recording =>
            TrackMatchComparer.TitleMatches(requestedTitle, recording.Title)
            && TrackMatchComparer.ArtistMatches(requestedArtist, recording.ArtistCredit, recording.Artists));

        if (agreed is not null) return Confirm(best, agreed, tagsAuthoritative);

        var actual = best.Recordings[0];
        return new VerificationResult
        {
            Verdict = VerificationVerdict.Mismatch,
            Score = best.Score,
            MatchedTitle = actual.Title,
            MatchedArtist = actual.ArtistCredit,
            MatchedAlbum = actual.AlbumTitle,
            MatchedYear = actual.Year,
            RecordingId = actual.RecordingId,
            DenyReason = $"is '{actual.ArtistCredit} - {actual.Title}'",
        };
    }

    private static VerificationResult Confirm(AcoustIdResult result, AcoustIdRecording recording, bool tagsAuthoritative) => new()
    {
        Verdict = VerificationVerdict.Confirmed,
        Score = result.Score,
        AcoustId = result.Id,
        MatchedTitle = recording.Title,
        MatchedArtist = recording.ArtistCredit,
        MatchedAlbum = recording.AlbumTitle,
        MatchedYear = recording.Year,
        RecordingId = recording.RecordingId,
        TagsAuthoritative = tagsAuthoritative,
        Match = recording,
    };

    /// <summary>
    /// Every valid ISRC the file's tags carry: ID3's TSRC, a Vorbis comment's ISRC and the MP4
    /// iTunes ISRC atom all come through one TagLib property. ffmpeg, and whatever converted a
    /// file with it, writes an MP3's ISRC as a user text frame named ISRC instead, so that is
    /// read too. A tag that holds several joins them, so it is split. Empty when there is none
    /// or the tags cannot be read.
    /// </summary>
    internal static IReadOnlyList<string> ReadIsrcs(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            var raw = new List<string?> { file.Tag.ISRC };
            if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3
                && TagLib.Id3v2.UserTextInformationFrame.Get(id3, "ISRC", false) is { } frame)
                raw.AddRange(frame.Text);
            return raw.Where(value => !string.IsNullOrWhiteSpace(value))
                .SelectMany(value => value!.Split([';', ',', '/', '\0'], StringSplitOptions.RemoveEmptyEntries))
                .Select(SongIdentity.NormalizeIsrc).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// The single recording, at any score, that agrees on title, artist and length (7 seconds
    /// either way). Two or more is ambiguity, and ambiguity submits nothing.
    /// </summary>
    internal static string? AgreeingCandidate(AcoustIdLookup lookup, string? requestedArtist,
        string? requestedTitle, int durationSeconds)
    {
        var ids = lookup.Results
            .SelectMany(result => result.Recordings)
            .Where(recording => !string.IsNullOrEmpty(recording.RecordingId)
                && TrackMatchComparer.TitleMatches(requestedTitle, recording.Title)
                && TrackMatchComparer.ArtistMatches(requestedArtist, recording.ArtistCredit, recording.Artists)
                && (durationSeconds <= 0 || recording.DurationSeconds is null
                    || Math.Abs(recording.DurationSeconds.Value - durationSeconds) <= 7))
            .Select(recording => recording.RecordingId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return ids.Count == 1 ? ids[0] : null;
    }

    private int ReadDurationSeconds(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            return (int)Math.Round(file.Properties.Duration.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("could not read a duration from {Path}: {M}", path, ex.Message);
            return 0;
        }
    }
}
