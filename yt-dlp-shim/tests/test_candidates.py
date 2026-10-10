"""spotify-import asks for a short list and ranks it itself.

/candidates repeats yt-dlp's ytsearchN order and does not sort. /search stays
the one object Octo already calls: with a duration hint that object is still
the closest non-live hit, not the first row yt-dlp printed.
"""
import json
import os
import sys

import pytest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

app_module = pytest.importorskip("app")

# Length-ranking would put "studio" first and the live take last. The printed
# order is the opposite, which is what /candidates has to keep.
_ROWS = [
    {"id": "live-first", "title": "Song (Live)", "duration": 400, "channel": "Venue"},
    {"id": "studio", "title": "Song", "duration": 201.5, "channel": "Band"},
    {"id": "unknown", "title": "Song (topic)", "duration": None, "channel": "Topic"},
]
_QUERY = "Artist - Song"


@pytest.fixture(autouse=True)
def _isolate(monkeypatch):
    with app_module._SEARCH_CACHE_LOCK:
        app_module._SEARCH_CACHE.clear()
    # A real enqueue would fetch audio. These tests never go to the network.
    monkeypatch.setattr(app_module, "_loudness_enqueue", lambda video_id: None)
    yield
    with app_module._SEARCH_CACHE_LOCK:
        app_module._SEARCH_CACHE.clear()


def _client():
    return app_module.app.test_client()


def _extraction(monkeypatch, rows):
    seen = []
    flat = "\n".join(json.dumps(row) for row in rows) + "\n"

    def fake_run(args, timeout=20, label="ytdlp", bg=False, capture=False):
        seen.append(list(args))
        if "-g" in args:
            return "https://example.invalid/audio\n"
        if "%(urls)s" in args:
            return json.dumps(rows[0]) + "\nhttps://example.invalid/audio\n"
        return flat

    monkeypatch.setattr(app_module, "_run", fake_run)
    return seen


def _refuse(monkeypatch):
    def fake_run(*args, **kwargs):
        raise AssertionError("yt-dlp ran for a rejected request")

    monkeypatch.setattr(app_module, "_run", fake_run)


def test_candidates_keep_yt_dlp_order(monkeypatch):
    seen = _extraction(monkeypatch, _ROWS)

    resp = _client().get("/candidates", query_string={"q": _QUERY})

    body = resp.get_json()
    assert resp.status_code == 200
    assert [row["video_id"] for row in body] == ["live-first", "studio", "unknown"]
    assert [row["title"] for row in body] == ["Song (Live)", "Song", "Song (topic)"]
    assert [row["channel"] for row in body] == ["Venue", "Band", "Topic"]
    assert body[0]["duration"] == 400
    assert body[1]["duration"] == 201
    assert isinstance(body[1]["duration"], int)
    assert body[2]["duration"] is None
    assert set(body[0]) == {"video_id", "title", "duration", "channel"}
    assert seen[0][0] == f"ytsearch5:{_QUERY}"


def test_omitted_limit_asks_for_five(monkeypatch):
    seen = _extraction(monkeypatch, _ROWS)

    resp = _client().get("/candidates", query_string={"q": _QUERY})

    assert resp.status_code == 200
    assert seen[0][0] == f"ytsearch5:{_QUERY}"


def test_limit_caps_at_ten(monkeypatch):
    seen = _extraction(monkeypatch, _ROWS)
    client = _client()

    for raw in ("10", "11", "100"):
        resp = client.get("/candidates", query_string={"q": _QUERY, "limit": raw})
        assert resp.status_code == 200
        assert [row["video_id"] for row in resp.get_json()] == ["live-first", "studio", "unknown"]

    assert [call[0] for call in seen] == [f"ytsearch10:{_QUERY}"] * 3


@pytest.mark.parametrize("params", [{}, {"q": ""}, {"q": "   "}])
def test_an_empty_query_is_rejected(monkeypatch, params):
    _refuse(monkeypatch)

    resp = _client().get("/candidates", query_string=params)

    assert resp.status_code == 400


@pytest.mark.parametrize("limit", ["0", "-1", "no", "1.5", "+5"])
def test_a_limit_that_is_not_a_positive_integer_is_rejected(monkeypatch, limit):
    _refuse(monkeypatch)

    resp = _client().get("/candidates", query_string={"q": _QUERY, "limit": limit})

    assert resp.status_code == 400


def test_no_hits_is_an_empty_array(monkeypatch):
    _extraction(monkeypatch, [])

    resp = _client().get("/candidates", query_string={"q": _QUERY, "limit": "5"})

    assert resp.status_code == 200
    assert resp.get_json() == []


def test_a_failed_extraction_is_a_bad_gateway(monkeypatch):
    monkeypatch.setattr(app_module, "_run", lambda *args, **kwargs: None)

    resp = _client().get("/candidates", query_string={"q": _QUERY})

    assert resp.status_code == 502
    assert resp.get_json() == {"error": "search_failed"}


def test_search_with_a_duration_still_returns_the_one_ranked_hit(monkeypatch):
    _extraction(monkeypatch, _ROWS)

    resp = _client().get("/search", query_string={"q": _QUERY, "duration": "200"})

    body = resp.get_json()
    assert resp.status_code == 200
    assert isinstance(body, dict)
    assert body["video_id"] == "studio"
    assert body["title"] == "Song"
    assert body["channel"] == "Band"
    # /search still passes yt-dlp's duration through. Only /candidates coerces it.
    assert body["duration"] == 201.5


def test_search_without_a_duration_still_returns_one_object(monkeypatch):
    _extraction(monkeypatch, _ROWS)

    resp = _client().get("/search", query_string={"q": _QUERY})

    body = resp.get_json()
    assert resp.status_code == 200
    assert isinstance(body, dict)
    assert body["video_id"] == "live-first"
    assert body["duration"] == 400


def test_search_still_rejects_an_empty_query(monkeypatch):
    _refuse(monkeypatch)

    resp = _client().get("/search", query_string={"q": "  "})

    assert resp.status_code == 400
