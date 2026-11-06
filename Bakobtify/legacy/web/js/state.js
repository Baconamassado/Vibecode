/** Estado global mínimo, com assinatura por patch. */

export const state = {
  view: "search",
  loading: false,
  results: [],
  resultsQuery: "",
  favorites: [],
  playlists: [],
  history: [],
  activePlaylist: null,
  health: null,

  /** Faixas visíveis na view atual (não dispara re-render). */
  viewTracks: [],

  queue: [],
  queueIndex: -1,
  track: null,
  playing: false,
  shuffle: false,
  repeat: "off",
  volume: 0.8,
  muted: false,
  widgetMode: false,
};

const listeners = new Set();

/** Chaves cuja mudança exige redesenhar a view principal. */
export const VIEW_KEYS = new Set([
  "view",
  "loading",
  "results",
  "resultsQuery",
  "favorites",
  "playlists",
  "history",
  "activePlaylist",
]);

export function setState(patch) {
  let changed = false;
  for (const [key, value] of Object.entries(patch)) {
    if (state[key] !== value) {
      state[key] = value;
      changed = true;
    }
  }
  if (changed) emit(patch);
}

export function subscribe(fn) {
  listeners.add(fn);
  return () => listeners.delete(fn);
}

function emit(patch) {
  for (const fn of listeners) {
    try {
      fn(state, patch);
    } catch (err) {
      console.error("[bakobtify] erro em listener:", err);
    }
  }
}

export function isFavorite(trackId) {
  return state.favorites.some((t) => String(t.id) === String(trackId));
}

export function findTrack(trackId) {
  const id = String(trackId);
  const pools = [state.results, state.favorites, state.history, state.queue];
  for (const pool of pools) {
    const hit = pool.find((t) => String(t.id) === id);
    if (hit) return hit;
  }
  return null;
}
