"""Handlers da API local. Cada função recebe (ctx, params, body) e devolve
(status_http, payload_serializável)."""
from __future__ import annotations

import sqlite3
from typing import Any

from .soundcloud import DegradedError, NotFoundError, SoundCloudError

Params = dict[str, Any]
Body = dict[str, Any] | None


def _require_track(body: Body) -> dict[str, Any]:
    if not body or not isinstance(body.get("track"), dict):
        raise ValueError("Campo 'track' obrigatório.")
    track = body["track"]
    if not track.get("id") or not track.get("title"):
        raise ValueError("Faixa precisa de 'id' e 'title'.")
    return track


# -- sistema -----------------------------------------------------------


def health(ctx, params: Params, body: Body):
    from . import __version__

    return 200, {"version": __version__, "client": ctx.sc.status()}


# -- catálogo ----------------------------------------------------------


def search(ctx, params: Params, body: Body):
    query = (params.get("q") or "").strip()
    if not query:
        raise ValueError("Parâmetro 'q' obrigatório.")
    limit = int(params.get("limit") or 24)
    tracks = ctx.sc.search(query, limit)
    return 200, {"query": query, "tracks": tracks, "total": len(tracks)}


def track(ctx, params: Params, body: Body):
    return 200, {"track": ctx.sc.track(params["track_id"])}


def stream(ctx, params: Params, body: Body):
    return 200, ctx.sc.stream(params["track_id"])


# -- favoritos ---------------------------------------------------------


def favorites_list(ctx, params: Params, body: Body):
    return 200, {"tracks": ctx.store.list_favorites()}


def favorites_add(ctx, params: Params, body: Body):
    track = _require_track(body)
    ctx.store.add_favorite(track)
    return 201, {"ok": True}


def favorites_remove(ctx, params: Params, body: Body):
    ctx.store.remove_favorite(params["track_id"])
    return 200, {"ok": True}


# -- playlists ---------------------------------------------------------


def playlists_list(ctx, params: Params, body: Body):
    return 200, {"playlists": ctx.store.list_playlists()}


def playlists_create(ctx, params: Params, body: Body):
    name = (body or {}).get("name") or ""
    playlist = ctx.store.create_playlist(name)
    return 201, {"playlist": playlist}


def playlists_get(ctx, params: Params, body: Body):
    playlist_id = int(params["playlist_id"])
    if not ctx.store.playlist_exists(playlist_id):
        raise NotFoundError("Playlist não encontrada.")
    name = next(
        (p["name"] for p in ctx.store.list_playlists() if p["id"] == playlist_id), ""
    )
    return 200, {
        "playlist": {
            "id": playlist_id,
            "name": name,
            "tracks": ctx.store.playlist_tracks(playlist_id),
        }
    }


def playlists_delete(ctx, params: Params, body: Body):
    if not ctx.store.delete_playlist(int(params["playlist_id"])):
        raise NotFoundError("Playlist não encontrada.")
    return 200, {"ok": True}


def playlists_add_track(ctx, params: Params, body: Body):
    playlist_id = int(params["playlist_id"])
    if not ctx.store.playlist_exists(playlist_id):
        raise NotFoundError("Playlist não encontrada.")
    ctx.store.add_to_playlist(playlist_id, _require_track(body))
    return 201, {"ok": True}


def playlists_remove_track(ctx, params: Params, body: Body):
    ctx.store.remove_from_playlist(int(params["playlist_id"]), params["track_id"])
    return 200, {"ok": True}


# -- histórico ---------------------------------------------------------


def history_list(ctx, params: Params, body: Body):
    return 200, {"tracks": ctx.store.list_history()}


def history_add(ctx, params: Params, body: Body):
    ctx.store.add_history(_require_track(body))
    return 201, {"ok": True}


def history_clear(ctx, params: Params, body: Body):
    ctx.store.clear_history()
    return 200, {"ok": True}


# -- tradução de exceções ---------------------------------------------

EXCEPTION_MAP = (
    (DegradedError, 503, "soundcloud_degraded"),
    (NotFoundError, 404, "not_found"),
    (SoundCloudError, 502, "soundcloud_error"),
    (ValueError, 400, "bad_request"),
    (sqlite3.IntegrityError, 409, "conflict"),
)
