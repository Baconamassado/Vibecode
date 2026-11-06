/** Carregamento e mutação dos dados da biblioteca. */

import { api } from "./api.js";
import { setState, state, isFavorite } from "./state.js";
import { toast } from "./ui/toast.js";

export async function loadFavorites({ silent = false } = {}) {
  try {
    const data = await api.favorites();
    setState({ favorites: data.tracks });
  } catch (err) {
    if (!silent) toast.error(err.message || "Não foi possível carregar as favoritas.");
  }
}

export async function loadPlaylists({ silent = false } = {}) {
  try {
    const data = await api.playlists();
    setState({ playlists: data.playlists });
  } catch (err) {
    if (!silent) toast.error(err.message || "Não foi possível carregar as playlists.");
  }
}

export async function loadHistory({ silent = false } = {}) {
  try {
    const data = await api.history();
    setState({ history: data.tracks });
  } catch (err) {
    if (!silent) toast.error(err.message || "Não foi possível carregar o histórico.");
  }
}

export async function toggleFavorite(track) {
  const id = String(track.id);
  const wasFavorite = isFavorite(id);
  try {
    if (wasFavorite) {
      await api.removeFavorite(id);
    } else {
      await api.addFavorite(track);
    }
    const next = wasFavorite
      ? state.favorites.filter((t) => String(t.id) !== id)
      : [track, ...state.favorites.filter((t) => String(t.id) !== id)];
    setState({ favorites: next });
    toast(wasFavorite ? "Removida das favoritas" : "Adicionada às favoritas", "success");
    return !wasFavorite;
  } catch (err) {
    toast.error(err.message || "Não foi possível atualizar as favoritas.");
    return wasFavorite;
  }
}

export async function createPlaylist(name) {
  try {
    const data = await api.createPlaylist(name);
    await loadPlaylists({ silent: true });
    toast(`Playlist “${data.playlist.name}” criada`, "success");
    return data.playlist;
  } catch (err) {
    toast.error(err.message || "Não foi possível criar a playlist.");
    return null;
  }
}

export async function deletePlaylist(playlistId) {
  try {
    await api.deletePlaylist(playlistId);
    await loadPlaylists({ silent: true });
    if (state.activePlaylist?.id === playlistId) {
      setState({ view: "search", activePlaylist: null });
    }
    toast("Playlist removida", "success");
    return true;
  } catch (err) {
    toast.error(err.message || "Não foi possível remover a playlist.");
    return false;
  }
}

export async function addToPlaylist(playlistId, track) {
  try {
    await api.addToPlaylist(playlistId, track);
    await loadPlaylists({ silent: true });
    if (state.activePlaylist?.id === playlistId) {
      await openPlaylist(playlistId);
    }
    const name = state.playlists.find((p) => p.id === playlistId)?.name || "playlist";
    toast(`Adicionada a “${name}”`, "success");
    return true;
  } catch (err) {
    toast.error(err.message || "Não foi possível adicionar à playlist.");
    return false;
  }
}

export async function removeFromPlaylist(playlistId, trackId) {
  try {
    await api.removeFromPlaylist(playlistId, trackId);
    await loadPlaylists({ silent: true });
    await openPlaylist(playlistId);
    toast("Removida da playlist", "success");
    return true;
  } catch (err) {
    toast.error(err.message || "Não foi possível remover da playlist.");
    return false;
  }
}

export async function openPlaylist(playlistId) {
  try {
    const data = await api.playlist(playlistId);
    setState({
      view: "playlist",
      activePlaylist: {
        id: data.playlist.id,
        name: data.playlist.name,
        tracks: data.playlist.tracks,
      },
    });
  } catch (err) {
    toast.error(err.message || "Não foi possível abrir a playlist.");
  }
}

export async function clearHistory() {
  try {
    await api.clearHistory();
    setState({ history: [] });
    toast("Histórico limpo", "success");
  } catch (err) {
    toast.error(err.message || "Não foi possível limpar o histórico.");
  }
}
