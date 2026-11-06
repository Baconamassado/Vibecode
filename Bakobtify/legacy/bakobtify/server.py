"""Servidor HTTP local: estáticos + API. Só biblioteca padrão."""
from __future__ import annotations

import json
import mimetypes
import re
import sys
import traceback
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any, Callable, Pattern
from urllib.parse import parse_qs, urlparse

from . import api, config
from .soundcloud import SoundCloud
from .store import Store

Handler = Callable[[Any, dict[str, Any], dict[str, Any] | None], tuple[int, Any]]


class Ctx:
    def __init__(self) -> None:
        self.store = Store()
        self.sc = SoundCloud(self.store)


ROUTES: list[tuple[str, Pattern[str], Handler]] = [
    ("GET", re.compile(r"/api/health"), api.health),
    ("GET", re.compile(r"/api/search"), api.search),
    ("GET", re.compile(r"/api/tracks/(?P<track_id>\d+)"), api.track),
    ("GET", re.compile(r"/api/tracks/(?P<track_id>\d+)/stream"), api.stream),
    ("GET", re.compile(r"/api/favorites"), api.favorites_list),
    ("POST", re.compile(r"/api/favorites"), api.favorites_add),
    ("DELETE", re.compile(r"/api/favorites/(?P<track_id>\d+)"), api.favorites_remove),
    ("GET", re.compile(r"/api/playlists"), api.playlists_list),
    ("POST", re.compile(r"/api/playlists"), api.playlists_create),
    ("GET", re.compile(r"/api/playlists/(?P<playlist_id>\d+)"), api.playlists_get),
    ("DELETE", re.compile(r"/api/playlists/(?P<playlist_id>\d+)"), api.playlists_delete),
    (
        "POST",
        re.compile(r"/api/playlists/(?P<playlist_id>\d+)/tracks"),
        api.playlists_add_track,
    ),
    (
        "DELETE",
        re.compile(r"/api/playlists/(?P<playlist_id>\d+)/tracks/(?P<track_id>\d+)"),
        api.playlists_remove_track,
    ),
    ("GET", re.compile(r"/api/history"), api.history_list),
    ("POST", re.compile(r"/api/history"), api.history_add),
    ("DELETE", re.compile(r"/api/history"), api.history_clear),
]

mimetypes.add_type("text/javascript", ".js")
mimetypes.add_type("text/css", ".css")
mimetypes.add_type("image/svg+xml", ".svg")

API_PREFIX = "/api/"


class BakobtifyHandler(BaseHTTPRequestHandler):
    server_version = "Bakobtify/1.0"
    protocol_version = "HTTP/1.1"

    ctx: Ctx  # injetado na subclasse criada em serve()

    def log_message(self, fmt: str, *args: Any) -> None:  # silencia o ruído padrão
        if getattr(self, "_debug", False):
            super().log_message(fmt, *args)

    # -- helpers -------------------------------------------------------

    def _send_json(self, status: int, payload: Any) -> None:
        raw = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(raw)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(raw)

    def _read_body(self) -> dict[str, Any] | None:
        length = int(self.headers.get("Content-Length") or 0)
        if length <= 0:
            return None
        if length > config.MAX_BODY_BYTES:
            raise ValueError("Corpo da requisição grande demais.")
        raw = self.rfile.read(length)
        if not raw:
            return None
        try:
            data = json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise ValueError("JSON inválido.") from exc
        if not isinstance(data, dict):
            raise ValueError("O corpo deve ser um objeto JSON.")
        return data

    def _dispatch(self, method: str) -> None:
        parsed = urlparse(self.path)
        path = parsed.path

        if not path.startswith(API_PREFIX):
            if method == "GET":
                self._serve_static(path)
            else:
                self._send_json(405, {"error": "method_not_allowed"})
            return

        try:
            body = self._read_body() if method in ("POST", "PUT", "PATCH") else None
        except ValueError as exc:
            self._send_json(400, {"error": "bad_request", "message": str(exc)})
            return

        for route_method, pattern, handler in ROUTES:
            match = pattern.fullmatch(path)
            if not match or route_method != method:
                continue
            params: dict[str, Any] = {k: v for k, v in parse_qs(parsed.query).items()}
            params = {k: (v[0] if isinstance(v, list) and len(v) == 1 else v) for k, v in params.items()}
            params.update(match.groupdict())
            try:
                status, payload = handler(self.ctx, params, body)
            except Exception as exc:  # noqa: BLE001 - mapeado abaixo
                status, payload = self._error_payload(exc)
            self._send_json(status, payload)
            return

        self._send_json(404, {"error": "not_found", "message": "Rota inexistente."})

    def _error_payload(self, exc: Exception) -> tuple[int, Any]:
        for exc_type, status, code in api.EXCEPTION_MAP:
            if isinstance(exc, exc_type):
                return status, {"error": code, "message": str(exc)}
        traceback.print_exc()
        return 500, {"error": "internal_error", "message": "Erro interno no servidor."}

    # -- estáticos -----------------------------------------------------

    def _serve_static(self, path: str) -> None:
        rel = path.lstrip("/") or "index.html"
        if rel == "favicon.ico":
            self.send_response(204)
            self.send_header("Content-Length", "0")
            self.end_headers()
            return

        web_root = config.WEB_DIR.resolve()
        try:
            target = (web_root / rel).resolve()
        except (OSError, ValueError):
            self._send_json(400, {"error": "bad_path"})
            return

        if not target.is_relative_to(web_root) or any(part.startswith(".") for part in target.parts):
            self._send_json(404, {"error": "not_found"})
            return
        if target.is_dir():
            target = target / "index.html"
        if not target.is_file():
            self._send_json(404, {"error": "not_found"})
            return

        data = target.read_bytes()
        ctype, _ = mimetypes.guess_type(str(target))
        self.send_response(200)
        self.send_header("Content-Type", (ctype or "application/octet-stream") + (
            "; charset=utf-8" if ctype and ctype.startswith(("text/", "application/javascript")) else ""
        ))
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-cache")
        self.end_headers()
        self.wfile.write(data)

    # -- verbos --------------------------------------------------------

    def do_GET(self) -> None:  # noqa: N802
        self._dispatch("GET")

    def do_POST(self) -> None:  # noqa: N802
        self._dispatch("POST")

    def do_DELETE(self) -> None:  # noqa: N802
        self._dispatch("DELETE")


class BakobtifyServer(ThreadingHTTPServer):
    """Servidor que ignora quedas de conexão do navegador."""

    daemon_threads = True
    allow_reuse_address = True

    def handle_error(self, request, client_address) -> None:
        exc = sys.exc_info()[1]
        if isinstance(exc, (ConnectionResetError, ConnectionAbortedError, BrokenPipeError)):
            return
        super().handle_error(request, client_address)


def make_server(port: int = config.PORT, host: str = config.HOST) -> ThreadingHTTPServer:
    handler_cls = type("BoundHandler", (BakobtifyHandler,), {"ctx": Ctx()})
    httpd = BakobtifyServer((host, port), handler_cls)
    return httpd


def serve(port: int = config.PORT, host: str = config.HOST) -> None:
    httpd = make_server(port, host)
    url = f"http://{host}:{port}"
    print()
    print("  Bakobtify")
    print(f"  rodando em {url}")
    print("  Ctrl+C para encerrar")
    print()
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        print("\n  encerrando…")
    finally:
        httpd.server_close()
