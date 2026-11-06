/** Sidebar de playlists + view de uma playlist. */

import { createPlaylist, openPlaylist } from "../data.js";
import { state } from "../state.js";
import { escapeHtml } from "../util.js";
import { promptText } from "./dialog.js";
import { emptyHtml, trackListHtml, viewHeadHtml } from "./tracklist.js";

export function initPlaylistUi() {
  document.getElementById("new-playlist")?.addEventListener("click", async () => {
    const name = await promptText({
      title: "Nova playlist",
      description: "Dê um nome para a playlist.",
      placeholder: "Nome da playlist",
      confirmLabel: "Criar",
    });
    if (name) await createPlaylist(name);
  });

  document.getElementById("playlist-list")?.addEventListener("click", (event) => {
    const button = event.target.closest("[data-playlist-id]");
    if (button) openPlaylist(Number(button.dataset.playlistId));
  });
}

export function renderPlaylistList() {
  const list = document.getElementById("playlist-list");
  if (!list) return;

  if (!state.playlists.length) {
    list.innerHTML = `<li class="playlist-empty">Nenhuma ainda</li>`;
    return;
  }

  list.innerHTML = state.playlists
    .map(
      (playlist) => `
      <li>
        <button class="playlist-item${
          state.activePlaylist?.id === playlist.id ? " is-active" : ""
        }" data-playlist-id="${playlist.id}">
          <span>${escapeHtml(playlist.name)}</span>
          <small>${playlist.track_count || 0}</small>
        </button>
      </li>`
    )
    .join("");
}

export function renderPlaylistView(container) {
  const playlist = state.activePlaylist;

  if (!playlist) {
    state.viewTracks = [];
    container.innerHTML = emptyHtml({
      icon: "i-note",
      title: "Playlist não encontrada",
      text: "Ela pode ter sido removida. Escolha outra na barra lateral.",
    });
    return;
  }

  const tracks = playlist.tracks || [];
  state.viewTracks = tracks;

  const head = viewHeadHtml({
    eyebrow: "Playlist",
    title: playlist.name,
    sub: `${tracks.length} ${tracks.length === 1 ? "faixa" : "faixas"}`,
    actions: `
      ${tracks.length ? `<button class="btn ghost" data-action="play-all">Tocar tudo</button>` : ""}
      <button class="btn ghost danger" data-action="delete-playlist">Excluir</button>`,
  });

  if (!tracks.length) {
    container.innerHTML =
      head +
      emptyHtml({
        icon: "i-note",
        title: "Playlist vazia",
        text: "Abra o menu de qualquer faixa e escolha esta playlist para adicioná-la.",
      });
    return;
  }

  container.innerHTML = head + trackListHtml(tracks);
}
