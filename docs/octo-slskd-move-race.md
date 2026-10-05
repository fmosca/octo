# fix(soulseek): a finished transfer can report a pre-move path; track recorded as Completed and never placed

**Environment:** octo `2026.09.30.1`, slskd `0.26.0.0`, slskd's `incomplete` directory enabled. Octo runs in Docker; slskd's download tree is mounted read-write in octo.

Star an album and let octo pull several tracks back to back. Some tracks get placed in the library; others are silently lost.

When a transfer finishes, slskd moves the file out of its `incomplete/` directory into its download directory. Octo resolves the local path for the finished transfer from slskd's API, and if that read lands while slskd is relocating the file, octo gets the pre-move path under `incomplete/`. The file already isn't there, so the metadata write fails — the log shows `Failed to write metadata to: <stale path>` followed by a `System.IO.DirectoryNotFoundException` out of `TagLib.File.Create` inside `Octo.Services.Common.BaseDownloadService.WriteMetadataAsync` (`BaseDownloadService.cs:950`) — and yet the run carries on as if nothing happened: `Download completed` is logged for that same stale path, the track is marked `Completed`, and `downloads-history.json` records it with `sizeBytes: 0`. Nothing is retried and nothing surfaces to you — the star just yields a partial album: three of eight in one observed run.

Sequence for each lost track: transfer `Succeeded` → path resolved under `incomplete/` → metadata write fails at `BaseDownloadService.cs:950` → `Download completed` logged for that same path → `sizeBytes: 0` recorded → never retried.

The file itself isn't lost — slskd placed it one directory up, under its download directory, where it sits unreferenced.

The stale path was avoidable: octo already knows slskd's download directory at startup (it logs `slskd downloads dir: /downloads/slskd`). Check the resolved path exists before the metadata write; if it doesn't, re-resolve it against that directory, and treat a still-missing file as a failed download so the star retries instead of recording a 0-byte phantom.