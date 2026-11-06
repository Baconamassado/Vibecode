/** Views de favoritas e histórico. */

import { state } from "../state.js";
import { emptyHtml, trackListHtml, viewHeadHtml } from "./tracklist.js";

export function renderFavoritesView(container) {
  const tracks = state.favorites;
  state.viewTracks = tracks;

  if (!tracks.length) {
    container.innerHTML =
      viewHeadHtml({
        eyebrow: "Biblioteca",
        title: "Favoritas",
        sub: "As faixas que você marcar com o coração ficam aqui.",
      }) +
      emptyHtml({
        icon: "i-heart",
        title: "Nenhuma favorita ainda",
        text: "Passe o mouse em uma faixa, abra o menu e escolha “Adicionar às favoritas”.",
      });
    return;
  }

  container.innerHTML =
    viewHeadHtml({
      eyebrow: "Biblioteca",
      title: "Favoritas",
      sub: `${tracks.length} ${tracks.length === 1 ? "faixa" : "faixas"}`,
      actions: `<button class="btn ghost" data-action="play-all">Tocar tudo</button>`,
    }) + trackListHtml(tracks);
}

export function renderHistoryView(container) {
  const tracks = state.history;
  state.viewTracks = tracks;

  if (!tracks.length) {
    container.innerHTML =
      viewHeadHtml({
        eyebrow: "Biblioteca",
        title: "Histórico",
        sub: "Tudo o que você ouvir aparece por aqui.",
      }) +
      emptyHtml({
        icon: "i-history",
        title: "Nada por aqui",
        text: "Ainda não há reproduções registradas.",
      });
    return;
  }

  container.innerHTML =
    viewHeadHtml({
      eyebrow: "Biblioteca",
      title: "Histórico",
      sub: `${tracks.length} ${tracks.length === 1 ? "reprodução" : "reproduções"}`,
      actions: `
        <button class="btn ghost" data-action="play-all">Tocar tudo</button>
        <button class="btn ghost danger" data-action="clear-history">Limpar</button>`,
    }) + trackListHtml(tracks);
}
