"""A live video of a song is usually within seconds of the studio length, so the closest
length alone picked live takes as often as not. Live takes now sort last unless asked for."""
import os
import sys

import pytest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

app_module = pytest.importorskip("app")


def ids(ranked):
    return [r["id"] for r in ranked]


def test_a_live_take_never_beats_the_studio_song():
    candidates = [
        {"id": "live", "title": "Silverstein - Smile in Your Sleep (Live at the El Mocambo)", "duration": 209},
        {"id": "studio", "title": "Silverstein - Smile in Your Sleep", "duration": 203},
    ]
    assert ids(app_module._rank_by_length(candidates, "Silverstein - Smile in Your Sleep", 209))[0] == "studio"


def test_a_query_that_asks_for_live_ranks_by_length_alone():
    candidates = [
        {"id": "studio", "title": "Nirvana - About a Girl", "duration": 168},
        {"id": "unplugged", "title": "Nirvana - About a Girl (MTV Unplugged)", "duration": 217},
    ]
    assert ids(app_module._rank_by_length(candidates, "Nirvana - About a Girl (MTV Unplugged)", 217))[0] == "unplugged"


def test_when_every_candidate_is_live_the_closest_still_plays():
    candidates = [
        {"id": "far", "title": "Song (Live)", "duration": 400},
        {"id": "near", "title": "Song (Live in Berlin)", "duration": 201},
    ]
    assert ids(app_module._rank_by_length(candidates, "Artist - Song", 200))[0] == "near"


def test_a_candidate_without_a_length_sorts_last():
    candidates = [
        {"id": "unknown", "title": "Song", "duration": None},
        {"id": "known", "title": "Song", "duration": 250},
    ]
    assert ids(app_module._rank_by_length(candidates, "Artist - Song", 200)) == ["known", "unknown"]
