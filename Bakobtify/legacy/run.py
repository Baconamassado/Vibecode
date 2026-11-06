#!/usr/bin/env python3
"""Ponto de entrada do Bakobtify.

    python run.py               # http://127.0.0.1:8000
    python run.py --open        # abre o navegador
    python run.py --port 9000   # outra porta
"""
from __future__ import annotations

import argparse
import threading
import webbrowser

from bakobtify import config
from bakobtify.server import serve


def main() -> None:
    parser = argparse.ArgumentParser(description="Bakobtify - player de música minimalista")
    parser.add_argument("--port", type=int, default=config.PORT, help="porta HTTP (padrão: 8000)")
    parser.add_argument("--host", default=config.HOST, help="host de escuta (padrão: 127.0.0.1)")
    parser.add_argument("--open", action="store_true", help="abre o navegador automaticamente")
    args = parser.parse_args()

    if args.open:
        url = f"http://{args.host}:{args.port}"
        threading.Timer(0.8, lambda: webbrowser.open(url)).start()

    serve(port=args.port, host=args.host)


if __name__ == "__main__":
    main()
