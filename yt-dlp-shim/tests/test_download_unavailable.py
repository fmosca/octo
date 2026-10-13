"""A video yt-dlp cannot fetch is 404. Every other failure stays 502.

The phrases are the ones _VIDEO_UNAVAILABLE_MESSAGES names. They are listed
again here so a wording change is a diff in this file, not only in the
constant.
"""
import os
import subprocess
import sys

import pytest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

app_module = pytest.importorskip("app")

# yt-dlp 2026.08.19, yt_dlp/extractor/youtube/_video.py (still the same on
# master 89734124, 2026-10-10). See the constant's comment for which line
# each phrase comes from.
_UNAVAILABLE = (
    "Video unavailable",
    "Private video",
    "This video has been removed",
    "This video is not available",
    "confirm your age",
    "members-only",
    "members only",
    "The uploader has not made this video available in your country",
    "blocked it in your country",
)


def _under(tmp_path, monkeypatch):
    root = tmp_path / "downloads"
    root.mkdir()
    monkeypatch.setattr(app_module, "_DOWNLOAD_ROOT", os.path.realpath(str(root)))
    return root, app_module.app.test_client()


def _dest(root):
    return str(root / "spotify-import-youtube" / "deadbeef")


def _failing_run(monkeypatch, stderr):
    def fake_run(args, timeout=20, label="ytdlp", bg=False, capture=False):
        assert capture is True
        return subprocess.CompletedProcess(args, 1, stdout="", stderr=stderr)

    monkeypatch.setattr(app_module, "_run", fake_run)


def test_the_route_recognizes_these_unavailable_phrases():
    assert app_module._VIDEO_UNAVAILABLE_MESSAGES == _UNAVAILABLE


@pytest.mark.parametrize("phrase", _UNAVAILABLE)
def test_an_unavailable_phrase_on_captured_stderr_is_404(tmp_path, monkeypatch, phrase):
    root, client = _under(tmp_path, monkeypatch)
    _failing_run(
        monkeypatch,
        f"ERROR: [youtube] deadbeef: {phrase}. Sign in to view this video.\n",
    )
    updates = []
    monkeypatch.setattr(
        app_module, "_maybe_self_update", lambda reason: updates.append(reason) or True,
    )

    resp = client.get("/download", query_string={"id": "deadbeef", "dest": _dest(root)})

    assert resp.status_code == 404
    body = resp.get_json()
    assert body["error"] == "video_unavailable"
    assert body["expected"] == os.path.realpath(_dest(root)) + ".mp3"
    assert updates == []


def test_another_yt_dlp_failure_stays_502(tmp_path, monkeypatch):
    root, client = _under(tmp_path, monkeypatch)
    _failing_run(
        monkeypatch,
        "ERROR: [youtube] deadbeef: Unable to download webpage: HTTP Error 503: Service Unavailable\n",
    )

    resp = client.get("/download", query_string={"id": "deadbeef", "dest": _dest(root)})

    assert resp.status_code == 502
    body = resp.get_json()
    assert body["error"] == "download_failed"
    assert body["expected"] == os.path.realpath(_dest(root)) + ".mp3"


def test_a_403_is_retried_before_the_failure_is_classified(tmp_path, monkeypatch):
    """The self-update rung stays ahead of unavailable-vs-failed."""
    root, client = _under(tmp_path, monkeypatch)
    calls = {"n": 0}

    def fake_run(args, timeout=20, label="ytdlp", bg=False, capture=False):
        assert capture is True
        calls["n"] += 1
        if calls["n"] == 1:
            return subprocess.CompletedProcess(
                args, 1, stdout="", stderr="ERROR: HTTP Error 403: Forbidden\nVideo unavailable\n",
            )
        template = args[args.index("-o") + 1]
        path = template.replace(".%(ext)s", ".mp3")
        with open(path, "wb") as fh:
            fh.write(b"ID3")
        return subprocess.CompletedProcess(args, 0, stdout="", stderr="")

    monkeypatch.setattr(app_module, "_run", fake_run)
    monkeypatch.setattr(app_module, "_maybe_self_update", lambda reason: True)

    resp = client.get("/download", query_string={"id": "deadbeef", "dest": _dest(root)})

    assert resp.status_code == 200
    assert calls["n"] == 2
    assert resp.get_json()["path"].endswith("deadbeef.mp3")
