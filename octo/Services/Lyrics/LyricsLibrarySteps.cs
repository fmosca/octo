namespace Octo.Services.Lyrics;

/// <summary>
/// The lyrics page's steps (see <see cref="LyricsLibraryMode"/>): scan, preview, save, undo. A
/// scan reads tags only, so it is quick and changes nothing; a preview looks the picked songs up
/// one at a time with the same pause as a walk; Save writes only what the preview found, and only
/// where Octo may; Undo puts back every file Save wrote over, newest first.
/// </summary>
public sealed partial class LyricsLibraryWorker
{
    private async Task RunStepAsync(LyricsLibraryRequest request, LyricsLibraryMode mode, CancellationToken ct)
    {
        _cancelRequested = false;
        var current = _store.Current;
        if (request.Resume && current.CanResume)
        {
            _store.Update(run => { run.Status = LyricsLibraryStatus.Running; run.Reason = null; });
            _logger.LogInformation("Lyrics page {Mode}, resuming at {Cursor}/{Total}", mode, current.Cursor, current.Queue.Count);
        }
        else if (!await StartStepAsync(request, mode, current))
        {
            return;
        }

        switch (mode)
        {
            case LyricsLibraryMode.Scan: await ScanAsync(ct); break;
            case LyricsLibraryMode.Preview: await PreviewAsync(ct); break;
            case LyricsLibraryMode.Save: await SaveAsync(ct); break;
            case LyricsLibraryMode.Undo: Undo(); break;
        }
    }

    /// <summary>A new run for the step, keeping the list the earlier steps made. False when there
    /// is nothing to do, which is reported on the run.</summary>
    private async Task<bool> StartStepAsync(LyricsLibraryRequest request, LyricsLibraryMode mode, LyricsLibraryRun current)
    {
        var run = new LyricsLibraryRun
        {
            RunId = Guid.NewGuid().ToString("N")[..12],
            Status = LyricsLibraryStatus.Running,
            Mode = mode,
            Scope = current.Scope,
            StartedUtc = DateTime.UtcNow,
            Rows = current.Rows,
            WordAlready = current.WordAlready,
            Review = current.Review,
        };
        switch (mode)
        {
            case LyricsLibraryMode.Scan:
                run.Scope = string.Equals(request.Scope, "WholeLibrary", StringComparison.OrdinalIgnoreCase) ? "WholeLibrary" : "OctoDownloads";
                run.Rows = [];
                run.WordAlready = 0;
                _store.Replace(run);
                run.Queue = (await EnumerateAsync(run.Scope == "WholeLibrary")).ToList();
                break;
            case LyricsLibraryMode.Preview or LyricsLibraryMode.Save:
                var wanted = (request.Picked ?? []).ToHashSet(StringComparer.Ordinal);
                run.Picked = current.Rows
                    .Where(row => wanted.Contains(row.Id) && (mode == LyricsLibraryMode.Preview || row.Result == "found"))
                    .Select(row => row.Id)
                    .ToList();
                run.Queue = run.Picked;
                break;
            case LyricsLibraryMode.Undo:
                run.Queue = [];
                break;
        }
        run.Total = mode == LyricsLibraryMode.Undo ? _journal.ReadAll().Count : run.Queue.Count;
        _store.Replace(run);
        _logger.LogInformation("Lyrics page {Mode}: {Count} item(s), {Scope}", mode, run.Total, run.Scope);
        if (run.Total > 0 || mode == LyricsLibraryMode.Scan) return true;
        Finish(mode == LyricsLibraryMode.Undo ? "There was nothing to put back." : "Nothing was picked.");
        return false;
    }

    private void Finish(string? reason = null) => _store.Update(run =>
    {
        run.Status = LyricsLibraryStatus.Completed;
        run.FinishedUtc = DateTime.UtcNow;
        run.Reason = reason;
    });

    /// <summary>Stops the loop when asked to, saying so on the run. True when it should stop.</summary>
    private bool Stopping(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            _store.Update(run => run.Status = LyricsLibraryStatus.Interrupted);
            return true;
        }
        if (!_cancelRequested) return false;
        _store.Update(run =>
        {
            run.Status = LyricsLibraryStatus.Cancelled;
            run.FinishedUtc = DateTime.UtcNow;
            run.Reason = "Stopped from the dashboard.";
        });
        return true;
    }

    private static string HasName(LyricsTiming timing) => timing switch
    {
        LyricsTiming.Plain => "plain",
        LyricsTiming.Line => "line",
        LyricsTiming.Word => "word",
        _ => "none",
    };

    // ---- Scan ---------------------------------------------------------------------------------

    private Task ScanAsync(CancellationToken ct)
    {
        var queue = _store.Current.Queue;
        for (var index = _store.Current.Cursor; index < queue.Count; index++)
        {
            if (Stopping(ct)) return Task.CompletedTask;
            var path = queue[index];
            LyricsLibraryRow? row = null;
            var word = false;
            string? error = null;
            try
            {
                (row, word) = ScanSong(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex.Message;
            }
            _store.Update(run =>
            {
                run.Cursor = index + 1;
                run.Processed++;
                run.LastPath = path;
                if (error is not null) { run.Failed++; run.Errors.Add($"{path}: {error}"); }
                else if (row is not null) run.Rows.Add(row);
                else if (word) run.WordAlready++;
                else run.Skipped++;
            });
        }
        Finish();
        return Task.CompletedTask;
    }

    /// <summary>One song as a scan sees it: a row when its lyrics are missing, plain or timed by
    /// line; word true when it already has word timing. Neither for a song without an artist and a
    /// title to look it up by, or with a lyrics file Octo does not read.</summary>
    internal static (LyricsLibraryRow? Row, bool Word) ScanSong(string path)
    {
        string? artist, title, album, tagLyrics;
        try
        {
            using var file = TagLib.File.Create(path, TagLib.ReadStyle.None);
            artist = file.Tag.FirstPerformer ?? file.Tag.FirstAlbumArtist;
            title = file.Tag.Title;
            album = file.Tag.Album;
            tagLyrics = file.Tag.Lyrics;
        }
        catch (Exception ex) when (ex is not IOException and not UnauthorizedAccessException)
        {
            return (null, false);
        }
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title)) return (null, false);
        var has = SongLyrics.Of(path, tagLyrics);
        if (has.Unknown) return (null, false);
        if (has.Timing == LyricsTiming.Word) return (null, true);
        return (new LyricsLibraryRow
        {
            Id = LyricsLibraryRow.IdOf(path),
            Path = path,
            Artist = artist.Trim(),
            Title = title.Trim(),
            Album = string.IsNullOrWhiteSpace(album) ? null : album.Trim(),
            Has = HasName(has.Timing),
        }, false);
    }

    // ---- Preview ------------------------------------------------------------------------------

    private async Task PreviewAsync(CancellationToken ct)
    {
        var queue = _store.Current.Queue;
        var rows = _store.Current.Rows.ToDictionary(row => row.Id, StringComparer.Ordinal);
        var busyInARow = 0;
        for (var index = _store.Current.Cursor; index < queue.Count; index++)
        {
            if (Stopping(ct)) return;
            if (!rows.TryGetValue(queue[index], out var row)) continue;

            var job = ReadTags(row.Path)
                ?? new LyricsJob(row.Path, row.Artist, LyricsText.QueryTitle(row.Title, row.Artist), row.Album, null);
            var has = File.Exists(row.Path) ? SongLyrics.Of(row.Path) : SongLyrics.Nothing;
            LyricsLookup? lookup = null;
            string? error = null;
            try
            {
                lookup = File.Exists(row.Path) ? await _writer.LookUpAsync(job, has.Timing, ct) : null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _store.Update(run => run.Status = LyricsLibraryStatus.Interrupted);
                return;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            var busy = lookup?.Transient == true;
            busyInARow = busy ? busyInARow + 1 : 0;
            if (busyInARow >= BusyInARowLimit)
            {
                var from = index - BusyInARowLimit + 1;
                _store.Update(run =>
                {
                    run.Status = LyricsLibraryStatus.Interrupted;
                    run.Cursor = from;
                    run.Reason = "The lyrics services stopped answering. Resume later to carry on from here.";
                });
                return;
            }

            var found = lookup?.Result is { IsSongsOwn: false, Instrumental: false } better
                && (better.HasSynced || better.HasPlain) && better.Timing > has.Timing ? better : null;
            _store.Update(run =>
            {
                run.Cursor = index + 1;
                run.Processed++;
                run.LastPath = row.Path;
                row.Has = HasName(has.Timing);
                if (error is not null || lookup is null)
                {
                    row.Result = "failed";
                    run.Failed++;
                    run.Errors.Add($"{row.Path}: {error ?? "the file is gone"}");
                }
                else if (busy)
                {
                    row.Result = "busy";
                    run.Busy++;
                }
                else if (found is not null)
                {
                    row.Result = "found";
                    row.Source = found.Source;
                    row.Kind = LyricsChoiceService.KindOf(found);
                    row.CandidateId = found.CandidateId;
                    row.Doubt = found.Doubt;
                    row.Preview = LyricsText.Preview(found).ToList();
                    row.FoundSynced = found.Synced;
                    row.FoundPlain = found.Plain;
                    run.Upgraded++;
                }
                else
                {
                    row.Result = "none";
                    row.Kind = lookup.Result?.Instrumental == true ? "instrumental" : null;
                    row.FoundSynced = row.FoundPlain = null;
                    run.NotFound++;
                }
            });
            if (Gap > TimeSpan.Zero) await Task.Delay(Gap, ct);
        }
        Finish();
    }

    // ---- Save ---------------------------------------------------------------------------------

    private async Task SaveAsync(CancellationToken ct)
    {
        var queue = _store.Current.Queue;
        var runId = _store.Current.RunId;
        var rows = _store.Current.Rows.ToDictionary(row => row.Id, StringComparer.Ordinal);
        for (var index = _store.Current.Cursor; index < queue.Count; index++)
        {
            if (Stopping(ct)) return;
            if (!rows.TryGetValue(queue[index], out var row) || row.Found is not { } found) continue;

            string result;
            string? error = null;
            try
            {
                var has = File.Exists(row.Path) ? SongLyrics.Of(row.Path) : null;
                if (has is null) { result = "failed"; error = "the file is gone"; }
                else if (has.Where != SongLyricsPlace.None && has.Timing >= found.Timing) result = "kept";
                else result = await _writer.SaveChosenAsync(row.Path, found,
                    (path, kind, before) => _journal.Record(path, kind, before, runId), ct) ? "saved" : "blocked";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = "failed";
                error = ex.Message;
            }
            _store.Update(run =>
            {
                run.Cursor = index + 1;
                run.Processed++;
                run.LastPath = row.Path;
                row.Result = result;
                switch (result)
                {
                    case "saved": run.Written++; break;
                    case "kept": run.AlreadyHad++; break;
                    case "blocked": run.Skipped++; break;
                    default: run.Failed++; run.Errors.Add($"{row.Path}: {error}"); break;
                }
            });
        }
        Finish();
    }

    // ---- Undo ---------------------------------------------------------------------------------

    private void Undo()
    {
        var entries = _journal.ReadAll();
        var restored = 0;
        var left = 0;
        foreach (var entry in entries.Reverse())
        {
            if (_writer.Restore(entry)) restored++;
            else left++;
        }
        _journal.Clear();
        _store.Update(run =>
        {
            run.Processed = entries.Count;
            run.Written = restored;
            run.Skipped = left;
            // Saved again with one press, if wanted: the found lyrics are still on the rows.
            foreach (var row in run.Rows.Where(row => row.Result == "saved")) row.Result = "found";
        });
        _logger.LogInformation("Lyrics page undo: {Restored} put back, {Left} changed since and left alone", restored, left);
        Finish(left > 0 ? $"{left} changed since they were saved, so they were left alone." : null);
    }
}
