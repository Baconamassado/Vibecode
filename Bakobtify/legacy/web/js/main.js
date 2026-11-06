/** Bootstrap da interface: navegação, views, atalhos, status. */

import { api } from "./api.js";
import {
  clearHistory,
  deletePlaylist,
  loadFavorites,
  loadHistory,
  loadPlaylists,
} from "./data.js";
import * as engine from "./player/engine.js";
import { initPlayer, setVolumeValue, syncRows, toggleMute } from "./player/player-ui.js";
import { initQueue, playList, toggle } from "./player/queue.js";
import { findTrack, setState, state, subscribe, VIEW_KEYS } from "./state.js";
import { openTrackMenu } from "./ui/menu.js";
import { confirmAction } from "./ui/dialog.js";
import {
  renderFavoritesView,
  renderHistoryView,
} from "./ui/library.js";
import {
  initPlaylistUi,
  renderPlaylistList,
  renderPlaylistView,
} from "./ui/playlist.js";
import {
  focusSearch,
  initSearch,
  renderSearchView,
  runSearch,
} from "./ui/search.js";
import { toast } from "./ui/toast.js";
import { escapeHtml } from "./util.js";

const $ = (id) => document.getElementById(id);

function currentPlaylistId() {
  return state.view === "playlist" ? state.activePlaylist?.id ?? null : null;
}

/* ---------------- views ---------------- */

function renderView() {
  const view = $("view");
  if (!view) return;

  if (state.view === "favorites") renderFavoritesView(view);
  else if (state.view === "history") renderHistoryView(view);
  else if (state.view === "playlist") renderPlaylistView(view);
  else renderSearchView(view);

  syncRows();
}

function updateNav() {
  for (const item of document.querySelectorAll(".nav-item")) {
    item.classList.toggle("is-active", item.dataset.view === state.view);
  }
  const count = $("fav-count");
  if (count) {
    count.hidden = !state.favorites.length;
    count.textContent = String(state.favorites.length);
  }
}

/* ---------------- status ---------------- */

function updateStatus() {
  const pill = $("status-pill");
  const label = $("status-label");
  if (!pill || !label) return;

  const health = state.health;
  if (!health || health.state === "unknown") {
    pill.dataset.state = "idle";
    label.textContent = "em espera";
    pill.title = "O SoundCloud será consultado na primeira busca.";
    return;
  }

  if (health.state === "ok") {
    const cached = health.source === "cache";
    pill.dataset.state = cached ? "cache" : "ok";
    label.textContent = cached ? "conectado · cache" : "conectado";
    pill.title =
      health.expires_in > 0
        ? `client_id válido por ${Math.round(health.expires_in / 3600)}h`
        : "Conectado ao SoundCloud";
  } else {
    pill.dataset.state = "degraded";
    label.textContent = "modo degradado";
    pill.title = health.error || "Sem conexão com a API do SoundCloud";
  }
}

async function checkHealth() {
  try {
    const data = await api.health();
    setState({ health: data.client });
  } catch {
    setState({ health: { state: "degraded", error: "Servidor local indisponível." } });
  }
}

/* ---------------- fila ---------------- */

function renderQueuePanel() {
  const list = $("queue-list");
  if (!list) return;

  if (!state.queue.length) {
    list.innerHTML = `<li class="playlist-empty" style="padding:14px 10px">A fila está vazia</li>`;
    return;
  }

  list.innerHTML = state.queue
    .map((track, index) => {
      const active = index === state.queueIndex;
      return `
        <li>
          <button class="queue-item${active ? " is-active" : ""}" data-index="${index}">
            <span class="queue-index">${
              active
                ? `<span class="eq"><span></span><span></span><span></span></span>`
                : String(index + 1).padStart(2, "0")
            }</span>
            <span class="queue-meta">
              <span class="queue-title">${escapeHtml(track.title)}</span>
              <span class="queue-artist">${escapeHtml(track.artist || "")}</span>
            </span>
          </button>
        </li>`;
    })
    .join("");
}

function bindQueue() {
  $("queue-toggle")?.addEventListener("click", () => {
    const panel = $("queue-panel");
    if (!panel) return;
    panel.hidden = !panel.hidden;
    if (!panel.hidden) renderQueuePanel();
  });

  $("queue-close")?.addEventListener("click", () => {
    const panel = $("queue-panel");
    if (panel) panel.hidden = true;
  });

  $("queue-list")?.addEventListener("click", (event) => {
    const button = event.target.closest("[data-index]");
    if (!button) return;
    import("./player/queue.js").then((mod) => mod.playAt(Number(button.dataset.index)));
  });
}

/* ---------------- ações ---------------- */

async function playRow(id) {
  if (state.track && String(state.track.id) === String(id)) {
    await toggle();
    return;
  }
  const list = state.viewTracks;
  const index = list.findIndex((track) => String(track.id) === String(id));
  if (index < 0) return;
  await playList(list, index);
}

async function handleAction(action) {
  switch (action) {
    case "play-all":
      if (state.viewTracks.length) await playList(state.viewTracks, 0);
      break;
    case "clear-history": {
      const ok = await confirmAction({
        title: "Limpar histórico?",
        description: "Essa ação não pode ser desfeita.",
        confirmLabel: "Limpar",
      });
      if (ok) await clearHistory();
      break;
    }
    case "delete-playlist": {
      if (!state.activePlaylist) break;
      const ok = await confirmAction({
        title: `Excluir “${state.activePlaylist.name}”?`,
        description: "A playlist será removida junto com suas faixas. Sua biblioteca continua intacta.",
        confirmLabel: "Excluir",
      });
      if (ok) await deletePlaylist(state.activePlaylist.id);
      break;
    }
    default:
      break;
  }
}

/* ---------------- bindings ---------------- */

function bindNav() {
  $("nav")?.addEventListener("click", (event) => {
    const item = event.target.closest(".nav-item");
    if (!item) return;
    const view = item.dataset.view;
    setState({ view, activePlaylist: null });
    if (view === "search") focusSearch();
  });
}

function bindView() {
  const view = $("view");
  if (!view) return;

  view.addEventListener("click", (event) => {
    const chip = event.target.closest(".chip");
    if (chip) {
      const query = chip.dataset.query || "";
      const input = $("search-input");
      if (input) input.value = query;
      runSearch(query);
      return;
    }

    const action = event.target.closest("[data-action]");
    if (action) {
      handleAction(action.dataset.action);
      return;
    }

    const more = event.target.closest(".track-more");
    if (more) {
      const row = more.closest(".track");
      openTrackMenu(findTrack(row.dataset.id), more, { playlistId: currentPlaylistId() });
      return;
    }

    const row = event.target.closest(".track");
    if (row) playRow(row.dataset.id);
  });

  view.addEventListener("keydown", (event) => {
    if (event.key !== "Enter" && event.key !== " ") return;
    if (event.target.closest("button")) return;
    const row = event.target.closest(".track");
    if (!row) return;
    event.preventDefault();
    playRow(row.dataset.id);
  });
}

function bindKeyboard() {
  window.addEventListener("keydown", (event) => {
    const tag = event.target.tagName;
    const typing =
      tag === "INPUT" || tag === "TEXTAREA" || event.target.isContentEditable;

    if (typing) return;

    // sliders tratam as próprias setas (evita seek/volume em dobro)
    const onSlider = event.target.closest?.('[role="slider"]');
    if (onSlider && (event.key.startsWith("Arrow") || event.key === "Home" || event.key === "End")) {
      return;
    }

    switch (event.key) {
      case "/":
        event.preventDefault();
        focusSearch();
        break;
      case " ":
        event.preventDefault();
        import("./player/queue.js").then((mod) => mod.toggle());
        break;
      case "ArrowRight": {
        const current = engine.time();
        if (current != null) {
          event.preventDefault();
          engine.seek(current + 5);
        }
        break;
      }
      case "ArrowLeft": {
        const current = engine.time();
        if (current != null) {
          event.preventDefault();
          engine.seek(Math.max(0, current - 5));
        }
        break;
      }
      case "ArrowUp":
        event.preventDefault();
        setVolumeValue(state.volume + 0.05);
        break;
      case "ArrowDown":
        event.preventDefault();
        setVolumeValue(state.volume - 0.05);
        break;
      case "m":
      case "M":
        toggleMute();
        break;
      default:
        break;
    }
  });
}

function bindStatus() {
  $("status-pill")?.addEventListener("click", async () => {
    await checkHealth();
    const health = state.health;
    if (!health) return;
    if (health.state === "ok") {
      toast(
        health.source === "cache"
          ? "SoundCloud conectado (client_id em cache)."
          : "SoundCloud conectado."
      );
    } else if (health.state === "unknown") {
      toast("Ainda não consultamos o SoundCloud. Faça uma busca.");
    } else {
      toast.error(health.error || "Falha na conexão com o SoundCloud.");
    }
  });
}

/* ---------------- init ---------------- */

function boot() {
  initQueue();
  initSearch();
  initPlaylistUi();
  initPlayer();
  bindNav();
  bindView();
  bindQueue();
  bindStatus();
  bindKeyboard();

  updateNav();
  updateStatus();
  renderPlaylistList();
  renderView();
  renderQueuePanel();

  subscribe((_, patch) => {
    const keys = Object.keys(patch);
    const has = (...names) => names.some((name) => keys.includes(name));

    if (has(...VIEW_KEYS)) renderView();
    if (has("view", "favorites")) updateNav();
    if (has("playlists", "activePlaylist")) renderPlaylistList();
    if (has("queue", "queueIndex", "track", "playing")) renderQueuePanel();
    if (has("health")) updateStatus();
  });

  loadFavorites({ silent: true });
  loadPlaylists({ silent: true });
  loadHistory({ silent: true });
  checkHealth();
  setInterval(checkHealth, 60000);
  window.addEventListener("bakobtify:health", checkHealth);

  window.addEventListener("beforeunload", () => engine.pause());
}

if (document.readyState === "loading") {
  document.addEventListener("DOMContentLoaded", boot, { once: true });
} else {
  boot();
}
