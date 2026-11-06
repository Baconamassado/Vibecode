"""Configuração central do Bakobtify.

Tudo aqui é ajustável por variável de ambiente, sem precisar editar código.
"""
from __future__ import annotations

import os
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WEB_DIR = ROOT / "web"
DATA_DIR = ROOT.parent / "data"
DB_PATH = DATA_DIR / "bakobtify.db"

HOST = os.environ.get("BAKOBTIFY_HOST", "127.0.0.1")
PORT = int(os.environ.get("BAKOBTIFY_PORT", "8000"))

# Tempo de vida do client_id antes de re-extrair do site.
CLIENT_ID_TTL_HOURS = int(os.environ.get("BAKOBTIFY_CLIENT_TTL", "24"))

# Freio de mão: intervalo mínimo entre chamadas ao api-v2.
MIN_REQUEST_INTERVAL = 0.30

# Cache curto de resultados de busca (evita repetir a mesma query).
SEARCH_CACHE_SECONDS = 60
SEARCH_CACHE_MAX = 60

# Limite do corpo de requisições JSON.
MAX_BODY_BYTES = 1_000_000
