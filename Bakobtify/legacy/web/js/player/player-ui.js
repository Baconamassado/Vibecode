/** Barra do player: liga DOM ao motor de áudio. */

import { toggleFavorite } from "../data.js";
import { isFavorite, setState, state, subscribe } from "../state.js";
import { clamp, escapeHtml, formatTime } from "../util.js";
import * as engine from "./engine.js";
import * as queue from "./queue.js";

const VOLUME_KEY = "bakobtify:volume";

let lastDuration = 0;
let lastCurrent = 0;
let previewing = false;
let dragRatio = null;
let seekHoldUntil = 0;

const $ = (id) => document.getElementById(id);

function ratioOf(bar, event) {
  const rect = bar.getBoundingClientRect();
  return clamp((event.clientX - rect.left) / rect.width, 0, 1);
}

/**
 * Arrasto robusto para as barras.
 *
 * O estado do gesto nunca depende de receber `pointerup` no próprio elemento:
 * durante o arrasto os eventos ficam no `window`, e `lostpointercapture`,
 * `blur` e `visibilitychange` encerram tudo. Era isso que deixava o slider
 * preso depois de arrastar por alguns segundos.
 */
function attachBarDrag(bar, { onStart, onMove, onEnd, onCancel } = {}) {
  let pointerId = null;

  const move = (event) => {
    if (pointerId === null || event.pointerId !== pointerId) return;
    onMove?.(ratioOf(bar, event), event);
  };

  const up = (event) => reset(event, false);
  const cancel = () => reset(null, true);

  function reset(event, cancelled) {
    if (pointerId === null) return;
    if (event && event.pointerId !== undefined && event.pointerId !== pointerId) return;

    const id = pointerId;
    pointerId = null;

    window.removeEventListener("pointermove", move, true);
    window.removeEventListener("pointerup", up, true);
    window.removeEventListener("pointercancel", cancel, true);
    bar.removeEventListener("lostpointercapture", onLostCapture);
    window.removeEventListener("blur", cancel);
    document.removeEventListener("visibilitychange", cancel);

    try {
      if (bar.hasPointerCapture?.(id)) bar.releasePointerCapture(id);
    } catch {
      /* captura já liberada */
    }

    bar.classList.remove("is-dragging");
    if (cancelled) onCancel?.();
    else onEnd?.(id);
  }

  function onLostCapture(event) {
    reset(event, true);
  }

  bar.addEventListener("pointerdown", (event) => {
    if (pointerId !== null) return;
    if (event.pointerType === "mouse" && event.button !== 0) return;

    // evita seleção de texto / gesto nativo enquanto arrasta
    event.preventDefault();
    pointerId = event.pointerId;
    bar.classList.add("is-dragging");

    try {
      bar.setPointerCapture(event.pointerId);
    } catch {
      /* sem captura: os listeners do window cobrem o gesto inteiro */
    }

    window.addEventListener("pointermove", move, true);
    window.addEventListener("pointerup", up, true);
    window.addEventListener("pointercancel", cancel, true);
    bar.addEventListener("lostpointercapture", onLostCapture);
    window.addEventListener("blur", cancel);
    document.addEventListener("visibilitychange", cancel);

    onStart?.(ratioOf(bar, event), event);
  });

  return { isDragging: () => pointerId !== null };
}

function paintProgress(current, duration) {
  lastDuration = duration || lastDuration;
  const ratio = lastDuration > 0 ? clamp(current / lastDuration, 0, 1) : 0;

  $("seek-fill").style.width = `${ratio * 100}%`;
  $("seek-knob").style.left = `${ratio * 100}%`;
  $("time-current").textContent = formatTime(current);
  $("time-total").textContent = formatTime(lastDuration);
  $("seek-bar").setAttribute("aria-valuenow", String(Math.round(ratio * 100)));
}

function paintSeekRatio(ratio) {
  $("seek-fill").style.width = `${ratio * 100}%`;
  $("seek-knob").style.left = `${ratio * 100}%`;
  $("time-current").textContent = formatTime(ratio * lastDuration);
}

function paintVolume(volume, muted) {
  const value = muted ? 0 : volume;
  $("vol-fill").style.width = `${value * 100}%`;
  $("vol-knob").style.left = `${value * 100}%`;
  $("vol-bar").setAttribute("aria-valuenow", String(Math.round(value * 100)));

  const icon = $("btn-mute").querySelector("use");
  icon.setAttribute("href", muted || value === 0 ? "#i-mute" : "#i-volume");
}

function syncRows() {
  const id = state.track ? String(state.track.id) : null;
  for (const row of document.querySelectorAll(".track")) {
    const match = id && row.dataset.id === id;
    row.classList.toggle("is-playing", Boolean(match));
    row.classList.toggle("is-paused", Boolean(match) && !state.playing);
  }
}

export { syncRows };

export function setVolumeValue(value) {
  const volume = clamp(Number(value) || 0, 0, 1);
  const muted = volume === 0;
  setState({ volume, muted });
  engine.setVolume(volume);
  engine.setMuted(muted);
  localStorage.setItem(VOLUME_KEY, String(volume));
  paintVolume(volume, muted);
}

export function toggleMute() {
  const muted = !state.muted;
  setState({ muted });
  engine.setMuted(muted);
  paintVolume(state.volume, muted);
}

function syncFromState() {
  const track = state.track;

  $("btn-play").disabled = !track;
  $("btn-play").classList.toggle("is-playing", state.playing);
  $("btn-play").setAttribute("aria-label", state.playing ? "Pausar" : "Tocar");

  const nowTitle = $("now-title");
  const nowArtist = $("now-artist");
  const nowArt = $("now-art");
  const favBtn = $("now-fav");

  if (track) {
    nowTitle.textContent = track.title;
    nowArtist.textContent = track.artist || "Desconhecido";
    nowArt.innerHTML = track.artwork
      ? `<img src="${escapeHtml(track.artwork)}" alt="">`
      : `<svg><use href="#i-note"/></svg>`;
    favBtn.disabled = false;
    favBtn.classList.toggle("is-on", isFavorite(track.id));
    document.title = `${track.title} · ${track.artist || "Bakobtify"}`;
  } else {
    nowTitle.textContent = "Nada tocando";
    nowArtist.textContent = "Busque algo para começar";
    nowArt.innerHTML = `<svg><use href="#i-note"/></svg>`;
    favBtn.disabled = true;
    favBtn.classList.remove("is-on");
    document.title = "Bakobtify";
  }

  $("btn-shuffle").classList.toggle("is-on", state.shuffle);

  const repeatBtn = $("btn-repeat");
  repeatBtn.classList.toggle("is-on", state.repeat !== "off");
  repeatBtn.querySelector("use").setAttribute(
    "href",
    state.repeat === "one" ? "#i-repeat-one" : "#i-repeat"
  );
  repeatBtn.setAttribute(
    "aria-label",
    state.repeat === "one" ? "Repetir faixa" : state.repeat === "all" ? "Repetir fila" : "Repetir desligado"
  );

  paintVolume(state.volume, state.muted);
  syncRows();

  const badge = $("source-badge");
  if (badge) badge.hidden = !state.widgetMode;
}

export function initPlayer() {
  const stored = localStorage.getItem(VOLUME_KEY);
  const parsed = stored === null ? Number.NaN : Number(stored);
  const volume = Number.isFinite(parsed) && parsed >= 0 && parsed <= 1 ? parsed : 0.8;
  setState({ volume });
  engine.setVolume(volume);
  engine.setMuted(state.muted);

  $("btn-play").addEventListener("click", () => queue.toggle());
  $("btn-next").addEventListener("click", () => queue.next(false));
  $("btn-prev").addEventListener("click", () => queue.prev());
  $("btn-shuffle").addEventListener("click", () => queue.setShuffle(!state.shuffle));
  $("btn-repeat").addEventListener("click", () => queue.cycleRepeat());

  $("btn-mute").addEventListener("click", () => toggleMute());

  $("now-fav").addEventListener("click", () => {
    if (state.track) toggleFavorite(state.track);
  });

  const seekBar = $("seek-bar");
  const seekDuration = async () => {
    const known = lastDuration || engine.duration() || 0;
    if (known > 0) return known;
    const fetched = await engine.ensureDuration();
    if (fetched > 0) lastDuration = fetched;
    return fetched || 0;
  };

  const commitSeek = async (ratio) => {
    if (!state.track || ratio == null) return;
    const duration = await seekDuration();
    if (!(duration > 0)) return;
    const target = clamp(ratio, 0, 1) * duration;
    lastDuration = duration;
    seekHoldUntil = performance.now() + 800;
    paintProgress(target, duration);
    engine.seek(target);
  };

  attachBarDrag(seekBar, {
    onStart: (ratio) => {
      if (!state.track) return;
      previewing = true;
      dragRatio = ratio;
      paintSeekRatio(ratio);
      if (lastDuration <= 0) {
        seekDuration().then(() => {
          if (previewing && dragRatio != null) paintSeekRatio(dragRatio);
        });
      }
    },
    onMove: (ratio) => {
      if (!state.track) return;
      dragRatio = ratio;
      paintSeekRatio(ratio);
    },
    onEnd: () => {
      const ratio = dragRatio;
      dragRatio = null;
      previewing = false;
      commitSeek(ratio);
    },
    onCancel: () => {
      previewing = false;
      dragRatio = null;
    },
  });

  const seekStep = (deltaSeconds) => {
    if (!state.track) return;
    const duration = lastDuration || engine.duration() || 0;
    if (!(duration > 0)) return;
    const base = previewing ? dragRatio * duration : lastCurrent;
    const target = clamp(base + deltaSeconds, 0, duration);
    seekHoldUntil = performance.now() + 500;
    engine.seek(target);
    if (!previewing) paintProgress(target, duration);
  };

  seekBar.addEventListener("keydown", (event) => {
    const step = event.shiftKey ? 30 : 5;
    switch (event.key) {
      case "ArrowRight":
        event.preventDefault();
        seekStep(step);
        break;
      case "ArrowLeft":
        event.preventDefault();
        seekStep(-step);
        break;
      case "Home":
        event.preventDefault();
        seekStep(-1e9);
        break;
      case "End":
        event.preventDefault();
        seekStep(1e9);
        break;
      default:
        break;
    }
  });

  // volume
  const volBar = $("vol-bar");
  const applyVolume = (ratio) => {
    const value = clamp(ratio, 0, 1);
    const muted = value === 0;
    setState({ volume: value, muted });
    engine.setVolume(value);
    engine.setMuted(muted);
    paintVolume(value, muted);
  };

  attachBarDrag(volBar, {
    onStart: (ratio) => applyVolume(ratio),
    onMove: (ratio) => applyVolume(ratio),
    onEnd: () => localStorage.setItem(VOLUME_KEY, String(state.volume)),
    onCancel: () => paintVolume(state.volume, state.muted),
  });

  volBar.addEventListener("keydown", (event) => {
    const step = event.shiftKey ? 0.1 : 0.05;
    if (event.key === "ArrowRight" || event.key === "ArrowUp") {
      event.preventDefault();
      setVolumeValue(state.volume + step);
    } else if (event.key === "ArrowLeft" || event.key === "ArrowDown") {
      event.preventDefault();
      setVolumeValue(state.volume - step);
    }
  });

  engine.on("time", ({ current, duration }) => {
    if (Number.isFinite(current)) lastCurrent = current;
    if (previewing) return;
    if (performance.now() < seekHoldUntil) return;
    paintProgress(current, duration);
  });

  engine.on("mode", ({ mode }) => {
    previewing = false;
    dragRatio = null;
    lastCurrent = 0;
    seekHoldUntil = 0;
    if (mode === "widget") {
      lastDuration = 0;
      paintProgress(0, 0);
    }
  });

  subscribe(syncFromState);
  syncFromState();
}
