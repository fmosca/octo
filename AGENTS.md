# Octo fork (`fmosca/octo`) — agent notes

Fork of `winters27/octo` (GPL-3.0). Octo server (+ yt-dlp-shim sidecar) is a
Subsonic proxy for Navidrome deployed on the baikonur homeserver.

**Read `docs/fork-dev-notes.md` first** for the branch model, CI/image
publishing (including `ghcr.io/fmosca/octo-ytdlp-shim`), the deploy repo split,
and how to debug against the running service.

Key conventions:

- Branches: do work on `dev-fm`, fast-forward `main` when shipping, keep the
  two in lockstep; merge upstream (`git fetch upstream && git merge
  upstream/main`) after checking the upstream diff first.
- Release tags follow upstream's dated convention (`2026.10.04`) and require
  `InformationalVersion` in `octo/octo.csproj` to match — CI enforces it.
- Infra truth (compose, Caddy, Authelia, playbooks, secrets) lives in
  `~/projects/ansible-homeserver`, not here. That repo's `RUNBOOK.md` §35–36
  documents the running service; this repo owns the code.
- Song-identity rules are shared with the Android app through
  `docs/song-identity-cases.json`: change a rule in the .NET server, the app,
  and that file together. Every case note says why it expects what it does.
- Tests (this machine hosts production services — run them so they stay quiet):
  the `Host=Boot` trait marks every test class that boots a
  `WebApplicationFactory` host. Develop against the fast slice
  (`dotnet test --filter "Host!=Boot"`), and run the full suite only before
  pushing, under a resource cap and low priority:
  `systemd-run --user --scope -p CPUQuota=100% -p MemoryMax=2G -- nice -n 19
  dotnet test -c Release` (wall ~7 min; uncapped it is ~2.5 min but the burn is
  multi-core). Note `systemd-run` does not inherit the shell's cwd unless
  `--scope` is used — pass an absolute project path or use the script wrapper.
  See `docs/fork-dev-notes.md` §"Test suite and host resources" for why the
  suite needs this treatment.