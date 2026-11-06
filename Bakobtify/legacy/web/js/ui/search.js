/** View de busca. */

import { api } from "../api.js";
import { setState, state } from "../state.js";
import { escapeHtml } from "../util.js";
import {
  emptyHtml,
  skeletonHtml,
  trackListHtml,
  viewHeadHtml,
} from "./tracklist.js";
import { toast } from "./toast.js";

const SUGGESTIONS = ["lo-fi", "jazz", "synthwave", "hip hop", "eletrônica", "piano"];

let timer = null;

export function initSearch() {
  const input = document.getElementById("search-input");
  if (!input) return;

  input.addEventListener("input", () => {
    clearTimeout(timer);
    const value = input.value.trim();
    if (!value) {
      setState({ results: [], resultsQuery: "", loading: false });
      return;
    }
    timer = setTimeout(() => runSearch(value), 380);
  });

  input.addEventListener("keydown", (event) => {
    if (event.key === "Enter") {
      clearTimeout(timer);
      runSearch(input.value.trim());
    }
    if (event.key === "Escape") input.blur();
  });
}

export function focusSearch() {
  const input = document.getElementById("search-input");
  if (!input) return;
  input.focus();
  input.select();
}

export async function runSearch(query) {
  const value = (query || "").trim();
  if (!value) return;

  setState({ view: "search", query: value, resultsQuery: value, loading: true });
  try {
    const data = await api.search(value);
    if (state.resultsQuery !== value) return;
    setState({ results: data.tracks, loading: false });
    window.dispatchEvent(new Event("bakobtify:health"));
  } catch (err) {
    setState({ results: [], loading: false });
    window.dispatchEvent(new Event("bakobtify:health"));
    toast.error(err.message || "A busca falhou.");
  }
}

export function renderSearchView(container) {
  if (state.loading) {
    state.viewTracks = [];
    container.innerHTML =
      viewHeadHtml({ eyebrow: "Buscando", title: state.resultsQuery || "…" }) +
      skeletonHtml(8);
    return;
  }

  if (!state.resultsQuery) {
    state.viewTracks = [];
    container.innerHTML = `
      ${viewHeadHtml({
        eyebrow: "Bakobtify",
        title: "O que você quer ouvir?",
        sub: "Busque faixas no SoundCloud e monte sua biblioteca.",
        hero: true,
      })}
      <div class="chips">
        ${SUGGESTIONS.map(
          (item) => `<button class="chip" data-query="${escapeHtml(item)}">${escapeHtml(item)}</button>`
        ).join("")}
      </div>`;
    return;
  }

  if (!state.results.length) {
    state.viewTracks = [];
    container.innerHTML =
      viewHeadHtml({ eyebrow: "Resultados", title: state.resultsQuery }) +
      emptyHtml({
        icon: "i-search",
        title: "Nada encontrado",
        text: `Nenhuma faixa apareceu para “${state.resultsQuery}”. Tente outras palavras.`,
      });
    return;
  }

  state.viewTracks = state.results;
  container.innerHTML =
    viewHeadHtml({
      eyebrow: "Resultados",
      title: state.resultsQuery,
      sub: `${state.results.length} faixas encontradas`,
      actions: `<button class="btn ghost" data-action="play-all">Tocar tudo</button>`,
    }) + trackListHtml(state.results);
}
