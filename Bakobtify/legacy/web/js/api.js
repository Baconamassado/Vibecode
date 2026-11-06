/** Camada fina sobre a API local. */

async function request(path, { method = "GET", body } = {}) {
  let res;
  try {
    res = await fetch(path, {
      method,
      headers: body ? { "Content-Type": "application/json" } : undefined,
      body: body ? JSON.stringify(body) : undefined,
    });
  } catch {
    const err = new Error("Servidor local fora do ar.");
    err.code = "network";
    throw err;
  }

  if (res.status === 204) return null;

  let data = null;
  try {
    data = await res.json();
  } catch {
    data = null;
  }

  if (!res.ok) {
    const err = new Error(data?.message || `Falha na requisição (${res.status}).`);
    err.code = data?.error || "http_error";
    err.status = res.status;
    throw err;
  }
  return data;
}

export const api = {
  health: () => request("/api/health"),

  search: (q, limit = 24) =>
    request(`/api/search?q=${encodeURIComponent(q)}&limit=${limit}`),

  stream: (id) => request(`/api/tracks/${encodeURIComponent(id)}/stream`),

  favorites: () => request("/api/favorites"),
  addFavorite: (track) => request("/api/favorites", { method: "POST", body: { track } }),
  removeFavorite: (id) =>
    request(`/api/favorites/${encodeURIComponent(id)}`, { method: "DELETE" }),

  playlists: () => request("/api/playlists"),
  playlist: (id) => request(`/api/playlists/${id}`),
  createPlaylist: (name) =>
    request("/api/playlists", { method: "POST", body: { name } }),
  deletePlaylist: (id) =>
    request(`/api/playlists/${id}`, { method: "DELETE" }),
  addToPlaylist: (id, track) =>
    request(`/api/playlists/${id}/tracks`, { method: "POST", body: { track } }),
  removeFromPlaylist: (id, trackId) =>
    request(`/api/playlists/${id}/tracks/${encodeURIComponent(trackId)}`, {
      method: "DELETE",
    }),

  history: () => request("/api/history"),
  logPlay: (track) => request("/api/history", { method: "POST", body: { track } }),
  clearHistory: () => request("/api/history", { method: "DELETE" }),
};
