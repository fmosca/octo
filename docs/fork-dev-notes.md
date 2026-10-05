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
and `09-docker-compose-run.yml`. Until 2026-10-05 the shim was *vendored* into
that repo at `files/opt/fmosca.dev/yt-dlp-shim/` and built on the deploy host;
the vendoring was retired when CI started publishing the shim image, because
the two copies had already drifted twice (the loudness worker existed only in
the vendored copy; the live-ranking fix only in the fork). The single source of
truth for shim code is now this repo's `yt-dlp-shim/`.

## Debugging against the running server

- Service state, ports, mounts, auth and settings: the `octo` entries in the
  deploy repo's `RUNBOOK.md` (§35 for octo + shim, §36 for the ReplayGain
  pipeline that crosses both repos).
- `docs/octo-slskd-move-race.md` — write-up of the slskd move race that drops
  album-heart tracks (fix in progress on `dev-fm`).
- Deploy a new build: run `09-docker-compose-run.yml` after CI publishes; the
  compose files pin `:main` tags, so a plain re-run picks the new image up.
- Server logs: `docker logs octo` / `docker logs octo-ytdlp-shim` on the host.