"""Integração com o SoundCloud.

O `client_id` é obtido sem criar aplicativo: extraímos o identificador que o
próprio site público usa no bundle JavaScript. Estratégia em cascata:

1. cache local (SQLite, TTL configurável)
2. scraping estático do bundle JS
3. scraping via navegador real (Selenium, só se o passo 2 falhar)
4. re-extração automática em 401/403

Se nada funcionar, a aplicação entra em modo degradado (embed oficial).
"""
from __future__ import annotations

import json
import re
import threading
import time
from typing import Any, Callable

import requests

from . import config
from .store import Store

API = "https://api-v2.soundcloud.com"
SITE = "https://soundcloud.com"

UA = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
    "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"
)

CLIENT_ID_RE = re.compile(r"""client_id\s*[:=]\s*["']([0-9a-zA-Z]{32})["']""")
CLIENT_ID_QS_RE = re.compile(r"""client_id=([0-9a-zA-Z]{32})""")

META_KEY = "sc_client_id"


class SoundCloudError(RuntimeError):
    """Falha genérica na integração."""


class DegradedError(SoundCloudError):
    """Não há client_id válido disponível."""


class NotFoundError(SoundCloudError):
    """Faixa/consulta inexistente."""


def normalize(raw: dict[str, Any]) -> dict[str, Any]:
    """Converte o formato do api-v2 no formato enxuto usado pela UI."""
    user = raw.get("user") or {}
    art = raw.get("artwork_url") or user.get("avatar_url")
    if art:
        art = art.replace("-large.", "-t500x500.").replace("-crop.", "-t500x500.")
    return {
        "id": str(raw.get("id")),
        "title": raw.get("title") or "Sem título",
        "artist": user.get("username") or "Desconhecido",
        "artist_url": user.get("permalink_url"),
        "artwork": art,
        "duration_ms": raw.get("duration") or 0,
        "permalink_url": raw.get("permalink_url"),
        "genre": raw.get("genre") or "",
        "playback_count": raw.get("playback_count") or 0,
        "created_at": raw.get("created_at"),
        "streamable": bool(raw.get("streamable", True)),
    }


class SoundCloud:
    def __init__(self, store: Store) -> None:
        self._store = store
        self._lock = threading.RLock()
        self._sess = requests.Session()
        self._sess.headers.update(
            {
                "User-Agent": UA,
                "Accept-Language": "pt-BR,pt;q=0.9,en;q=0.8",
                "Accept": "application/json, text/plain, */*",
                "Origin": SITE,
                "Referer": SITE + "/",
            }
        )
        self._client_id: str | None = None
        self._expires_at: float = 0.0
        self._source: str | None = None
        self._state: str = "unknown"
        self._last_error: str | None = None
        self._last_call: float = 0.0
        self._cache: dict[tuple[str, int], tuple[float, list[dict[str, Any]]]] = {}

    # -- status --------------------------------------------------------

    def status(self) -> dict[str, Any]:
        with self._lock:
            return {
                "state": self._state,
                "source": self._source,
                "expires_in": max(0, int(self._expires_at - time.time())),
                "error": self._last_error,
            }

    # -- client_id -----------------------------------------------------

    def _install(self, cid: str, source: str, ttl: float) -> None:
        self._client_id = cid
        self._expires_at = time.time() + ttl
        self._source = source
        self._state = "ok"
        self._last_error = None

    def _invalidate(self) -> None:
        with self._lock:
            self._client_id = None
            self._expires_at = 0.0
            self._store.delete_meta(META_KEY)

    def _fail(self, message: str) -> None:
        self._state = "degraded"
        self._last_error = message
        raise DegradedError(message)

    def _cid(self) -> str:
        with self._lock:
            now = time.time()
            if self._client_id and now < self._expires_at:
                return self._client_id

            cached = self._store.get_meta(META_KEY)
            if cached and cached["value"] and now < cached["expires_at"]:
                if self._validate(cached["value"]):
                    ttl = cached["expires_at"] - now
                    self._install(cached["value"], "cache", ttl)
                    return cached["value"]

            cid, source = self._extract()
            ttl = config.CLIENT_ID_TTL_HOURS * 3600
            self._install(cid, source, ttl)
            self._store.set_meta(META_KEY, cid, self._expires_at)
            print(f"[bakobtify] client_id obtido via {source} ({cid[:4]}…)")
            return cid

    def _extract(self) -> tuple[str, str]:
        errors: list[str] = []
        extractors: list[tuple[str, Callable[[], str | None]]] = [
            ("static", self._extract_static),
            ("selenium", self._extract_selenium),
        ]
        for name, fn in extractors:
            try:
                cid = fn()
            except Exception as exc:  # noqa: BLE001 - qualquer falha vira próximo passo
                errors.append(f"{name}: {type(exc).__name__}")
                continue
            if cid and self._validate(cid):
                return cid, name
            errors.append(f"{name}: id inválido")
        self._fail("Não foi possível obter um client_id (" + ", ".join(errors) + ")")
        raise AssertionError("inalcançável")  # pragma: no cover

    def _extract_static(self) -> str | None:
        resp = self._sess.get(SITE, timeout=20)
        resp.raise_for_status()
        html = resp.text

        inline = CLIENT_ID_RE.search(html)
        if inline:
            return inline.group(1)

        from bs4 import BeautifulSoup

        soup = BeautifulSoup(html, "html.parser")
        srcs = [s.get("src", "") for s in soup.find_all("script", src=True)]
        candidates = [
            ("https:" + s if s.startswith("//") else s)
            for s in srcs
            if "sndcdn.com/assets" in s and s.endswith(".js")
        ]
        for url in reversed(candidates[-8:]):
            try:
                js = self._sess.get(url, timeout=25).text
            except requests.RequestException:
                continue
            match = CLIENT_ID_RE.search(js) or CLIENT_ID_QS_RE.search(js)
            if match:
                return match.group(1)
        return None

    def _extract_selenium(self) -> str | None:
        try:
            from selenium import webdriver
            from selenium.webdriver.support.ui import WebDriverWait
        except ImportError as exc:  # pragma: no cover - selenium é opcional
            raise RuntimeError("selenium não instalado") from exc

        attempts = [
            (webdriver.Edge, getattr(webdriver, "EdgeOptions", None)),
            (webdriver.Chrome, getattr(webdriver, "ChromeOptions", None)),
        ]
        last: Exception | None = None
        for factory, options_cls in attempts:
            if options_cls is None:
                continue
            driver = None
            try:
                options = options_cls()
                options.add_argument("--headless=new")
                options.add_argument("--disable-gpu")
                options.add_argument("--no-first-run")
                options.add_argument("--no-default-browser-check")
                options.add_argument("--window-size=1280,900")
                options.add_argument("--log-level=3")
                options.set_capability("goog:loggingPrefs", {"performance": "ALL"})
                driver = factory(options=options)
                driver.get(SITE)
                WebDriverWait(driver, 25).until(lambda d: len(d.page_source) > 500)
                time.sleep(2)

                match = CLIENT_ID_RE.search(driver.page_source)
                if match:
                    return match.group(1)

                for entry in driver.get_log("performance"):
                    found = CLIENT_ID_QS_RE.search(entry.get("message", ""))
                    if found:
                        return found.group(1)
            except Exception as exc:  # noqa: BLE001
                last = exc
            finally:
                if driver is not None:
                    try:
                        driver.quit()
                    except Exception:  # noqa: BLE001
                        pass
        if last:
            raise last
        return None

    def _validate(self, cid: str) -> bool:
        try:
            resp = self._sess.get(
                f"{API}/search/tracks",
                params={"q": "a", "limit": 1, "client_id": cid, "app_locale": "en"},
                timeout=15,
            )
        except requests.RequestException:
            return False
        return resp.status_code == 200

    # -- HTTP ----------------------------------------------------------

    def _throttle(self) -> None:
        elapsed = time.monotonic() - self._last_call
        if elapsed < config.MIN_REQUEST_INTERVAL:
            time.sleep(config.MIN_REQUEST_INTERVAL - elapsed)
        self._last_call = time.monotonic()

    def _api(self, path: str, params: dict[str, Any] | None = None, retry: bool = True) -> Any:
        with self._lock:
            cid = self._cid()
            query = dict(params or {})
            query["client_id"] = cid
            self._throttle()
            resp = self._sess.get(f"{API}{path}", params=query, timeout=25)

        if resp.status_code in (401, 403):
            self._invalidate()
            if retry:
                return self._api(path, params, retry=False)
            self._fail("SoundCloud recusou as credenciais (401/403).")

        if resp.status_code == 404:
            raise NotFoundError("Conteúdo não encontrado no SoundCloud.")
        if resp.status_code == 429:
            raise SoundCloudError("Muitas requisições ao SoundCloud. Tente de novo em instantes.")
        if resp.status_code >= 400:
            raise SoundCloudError(f"SoundCloud respondeu {resp.status_code}.")

        try:
            return resp.json()
        except json.JSONDecodeError as exc:
            raise SoundCloudError("Resposta inválida do SoundCloud.") from exc

    # -- API pública ---------------------------------------------------

    def search(self, query: str, limit: int = 24) -> list[dict[str, Any]]:
        query = query.strip()
        if not query:
            return []
        limit = max(1, min(int(limit), 50))
        key = (query.lower(), limit)
        now = time.time()

        with self._lock:
            hit = self._cache.get(key)
            if hit and hit[0] > now:
                return hit[1]

        data = self._api(
            "/search/tracks",
            {"q": query, "limit": limit, "app_locale": "en", "linked_partitioning": 1},
        )
        tracks = [normalize(t) for t in data.get("collection", []) if t.get("kind") == "track"]

        with self._lock:
            if len(self._cache) > config.SEARCH_CACHE_MAX:
                self._cache.clear()
            self._cache[key] = (now + config.SEARCH_CACHE_SECONDS, tracks)
        return tracks

    def _track_raw(self, track_id: str) -> dict[str, Any]:
        if not str(track_id).isdigit():
            raise ValueError("track_id inválido")
        return self._api(f"/tracks/{track_id}")

    def track(self, track_id: str) -> dict[str, Any]:
        return normalize(self._track_raw(track_id))

    def stream(self, track_id: str) -> dict[str, Any]:
        raw = self._track_raw(track_id)
        permalink = raw.get("permalink_url")

        if not raw.get("streamable", True):
            return {"kind": "widget", "url": permalink, "reason": "not_streamable"}

        transcodings = (raw.get("media") or {}).get("transcodings") or []
        progressive = next(
            (
                t
                for t in transcodings
                if (t.get("format") or {}).get("protocol") == "progressive"
            ),
            None,
        )
        if progressive:
            with self._lock:
                cid = self._cid()
                self._throttle()
                resp = self._sess.get(
                    progressive["url"], params={"client_id": cid}, timeout=25
                )
            if resp.status_code == 200:
                try:
                    url = resp.json().get("url")
                except json.JSONDecodeError:
                    url = None
                if url:
                    return {
                        "kind": "progressive",
                        "url": url,
                        "mime": (progressive.get("format") or {}).get("mime_type"),
                    }

        # Só existe transcode HLS (ou nenhum): delega para o embed oficial.
        return {"kind": "widget", "url": permalink, "reason": "no_progressive"}
