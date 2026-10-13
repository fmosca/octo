"""/download writes one mp3 and names that path in the response.

The destination arrives on the query string. It has to be able to land in
spotify-import-youtube, and in the directory Octo already uses, and it has
to be refused when the resolved path leaves DOWNLOAD_ROOT — including when
the caller walks out with ".." or a symlink.
"""
import os
import subprocess
import sys

import pytest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

app_module = pytest.importorskip("app")


def _under(tmp_path, monkeypatch):
    root = tmp_path / "downloads"
    root.mkdir()
    monkeypatch.setattr(app_module, "_DOWNLOAD_ROOT", os.path.realpath(str(root)))
    return root, app_module.app.test_client()


def _accepting_run(monkeypatch):
    def fake_run(args, timeout=20, label="ytdlp", bg=False, capture=False):
        template = args[args.index("-o") + 1]
        path = template.replace(".%(ext)s", ".mp3")
        with open(path, "wb") as fh:
            fh.write(b"ID3")
        return subprocess.CompletedProcess(args, 0, stdout="", stderr="")

    monkeypatch.setattr(app_module, "_run", fake_run)


def _refusing_run(monkeypatch):
    def fake_run(*args, **kwargs):
        raise AssertionError("yt-dlp ran for a destination outside the download root")

    monkeypatch.setattr(app_module, "_run", fake_run)


def test_a_destination_under_spotify_import_youtube_returns_its_mp3_path(tmp_path, monkeypatch):
    root, client = _under(tmp_path, monkeypatch)
    _accepting_run(monkeypatch)
    dest = root / "spotify-import-youtube" / "video-1"

    resp = client.get("/download", query_string={"id": "video-1", "dest": str(dest)})

    expected = os.path.realpath(str(dest)) + ".mp3"
    assert resp.status_code == 200
    assert resp.get_json() == {"path": expected}
    assert os.path.isfile(expected)


def test_a_destination_in_octos_directory_under_the_same_root_still_returns_its_path(tmp_path, monkeypatch):
    root, client = _under(tmp_path, monkeypatch)
    _accepting_run(monkeypatch)
    dest = root / "incoming" / "yt" / "video-2"

    resp = client.get("/download", query_string={"id": "video-2", "dest": str(dest)})

    expected = os.path.realpath(str(dest)) + ".mp3"
    assert resp.status_code == 200
    assert resp.get_json() == {"path": expected}
    assert os.path.isfile(expected)


def test_a_destination_that_walks_out_of_the_root_is_rejected(tmp_path, monkeypatch):
    root, client = _under(tmp_path, monkeypatch)
    _refusing_run(monkeypatch)
    dest = root / "spotify-import-youtube" / ".." / ".." / "escaped"

    resp = client.get("/download", query_string={"id": "video-3", "dest": str(dest)})

    assert resp.status_code == 400
    assert not os.path.exists(os.path.realpath(str(dest)) + ".mp3")


def test_a_sibling_of_the_root_is_rejected(tmp_path, monkeypatch):
    """'/downloads-extra' shares a prefix with '/downloads' and is still outside it."""
    root, client = _under(tmp_path, monkeypatch)
    _refusing_run(monkeypatch)
    sibling = tmp_path / "downloads-extra" / "spotify-import-youtube" / "video-4"

    resp = client.get("/download", query_string={"id": "video-4", "dest": str(sibling)})

    assert resp.status_code == 400
    assert not sibling.with_suffix(".mp3").exists()


def test_a_symlink_inside_the_root_cannot_point_the_write_outside(tmp_path, monkeypatch):
    root, client = _under(tmp_path, monkeypatch)
    _refusing_run(monkeypatch)
    outside = tmp_path / "outside"
    outside.mkdir()
    (root / "spotify-import-youtube").symlink_to(outside, target_is_directory=True)
    dest = root / "spotify-import-youtube" / "video-5"

    resp = client.get("/download", query_string={"id": "video-5", "dest": str(dest)})

    assert resp.status_code == 400
    assert not (outside / "video-5.mp3").exists()


def test_a_relative_destination_is_written_under_the_download_root(tmp_path, monkeypatch):
    """spotify-import sends dest=spotify-import-youtube/<id> from a process whose cwd is not the root."""
    root, client = _under(tmp_path, monkeypatch)
    _accepting_run(monkeypatch)
    elsewhere = tmp_path / "app"
    elsewhere.mkdir()
    monkeypatch.chdir(elsewhere)

    resp = client.get(
        "/download",
        query_string={"id": "video-rel", "dest": "spotify-import-youtube/video-rel"},
    )

    expected = os.path.realpath(str(root / "spotify-import-youtube" / "video-rel")) + ".mp3"
    assert resp.status_code == 200
    assert resp.get_json() == {"path": expected}
    assert os.path.isfile(expected)
    assert not (elsewhere / "spotify-import-youtube" / "video-rel.mp3").exists()


def test_a_relative_dotdot_is_rejected_even_from_inside_the_root(tmp_path, monkeypatch):
    """'../escaped' joined onto the root leaves it, even when the cwd would keep the walk inside."""
    root, client = _under(tmp_path, monkeypatch)
    _refusing_run(monkeypatch)
    inside = root / "spotify-import-youtube"
    inside.mkdir()
    monkeypatch.chdir(inside)

    resp = client.get("/download", query_string={"id": "video-esc", "dest": "../escaped"})

    assert resp.status_code == 400
    assert not (root / "escaped.mp3").exists()
    assert not (inside / "escaped.mp3").exists()
