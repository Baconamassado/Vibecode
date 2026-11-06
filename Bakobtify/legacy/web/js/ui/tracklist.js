/** Renderização de listas de faixas e estados vazios. */

import {
  artworkFallback,
  escapeHtml,
  formatDuration,
  formatPlays,
} from "../util.js";
import { state } from "../state.js";

export function trackRowHtml(track, index) {
  const isCurrent = state.track && String(state.track.id) === String(track.id);
  const classes = [
    "track",
    isCurrent ? "is-playing" : "",
    isCurrent && !state.playing ? "is-paused" : "",
  ]
    .filter(Boolean)
    .join(" ");

  const art = track.artwork
    ? `<img src="${escapeHtml(track.artwork)}" alt="" loading="lazy" decoding="async">`
    : "";

  const plays = formatPlays(track.playback_count);
  const genre = track.genre ? escapeHtml(track.genre) : "";

  return `
    <div class="${classes}" data-id="${escapeHtml(track.id)}" tabindex="0" role="button"
         aria-label="Tocar ${escapeHtml(track.title)}">
      <button class="track-play" tabindex="-1" aria-hidden="true">
        <span class="idx">${String(index + 1).padStart(2, "0")}</span>
        <span class="eq" aria-hidden="true"><span></span><span></span><span></span></span>
        <svg class="i-play"><use href="#i-play"/></svg>
        <svg class="i-pause"><use href="#i-pause"/></svg>
      </button>
      <div class="track-art" style="${track.artwork ? "" : `background:${artworkFallback(String(track.id))}`}">${art}</div>
      <div class="track-info">
        <div class="track-title">${escapeHtml(track.title)}</div>
        <div class="track-sub">
          <span class="artist">${escapeHtml(track.artist || "Desconhecido")}</span>
          ${plays ? `<span class="sep">·</span><span class="plays">${plays} plays</span>` : ""}
        </div>
      </div>
      <div class="track-genre">${genre}</div>
      <div class="track-dur">${formatDuration(track.duration_ms)}</div>
      <button class="track-more" tabindex="-1" aria-label="Mais opções de ${escapeHtml(track.title)}">
        <svg><use href="#i-more"/></svg>
      </button>
    </div>`;
}

export function viewHeadHtml({ eyebrow = "", title, sub = "", actions = "", hero = false }) {
  return `
    <div class="view-head${hero ? " is-hero" : ""}">
      <div class="view-head-text">
        ${eyebrow ? `<div class="view-eyebrow">${escapeHtml(eyebrow)}</div>` : ""}
        <h1 class="view-title">${escapeHtml(title)}</h1>
        ${sub ? `<p class="view-sub">${escapeHtml(sub)}</p>` : ""}
      </div>
      ${actions ? `<div class="view-actions">${actions}</div>` : ""}
    </div>`;
}

export function trackListHtml(tracks) {
  return `<div class="track-list">${tracks
    .map((track, index) => trackRowHtml(track, index))
    .join("")}</div>`;
}

export function skeletonHtml(count = 8) {
  return `<div class="track-list">${Array.from({ length: count })
    .map(
      () => `
      <div class="skeleton-row">
        <div class="sk" style="width:14px;height:10px"></div>
        <div class="sk art"></div>
        <div style="display:flex;flex-direction:column;gap:7px">
          <div class="sk w-60"></div>
          <div class="sk w-25" style="height:9px"></div>
        </div>
        <div class="sk" style="width:36px;justify-self:end"></div>
      </div>`
    )
    .join("")}</div>`;
}

export function emptyHtml({ icon = "i-note", title, text = "", extra = "" }) {
  return `
    <div class="empty">
      <div class="empty-ico"><svg><use href="#${icon}"/></svg></div>
      <div class="empty-title">${escapeHtml(title)}</div>
      ${text ? `<p class="empty-text">${escapeHtml(text)}</p>` : ""}
      ${extra}
    </div>`;
}
