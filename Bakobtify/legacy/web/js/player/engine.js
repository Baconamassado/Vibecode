/** Motor de áudio unificado: <audio> nativo ou widget oficial. */

import * as widget from "./widget.js";

const audio = new Audio();
audio.preload = "auto";

const bus = new EventTarget();
const emit = (type, detail) => bus.dispatchEvent(new CustomEvent(type, { detail }));

let mode = "audio";
let volume = 0.8;
let mute = false;

export function on(type, fn) {
  const handler = (event) => fn(event.detail);
  bus.addEventListener(type, handler);
  return () => bus.removeEventListener(type, handler);
}

audio.addEventListener("timeupdate", () => {
  if (mode !== "audio") return;
  emit("time", { current: audio.currentTime, duration: audio.duration || 0 });
});

audio.addEventListener("loadedmetadata", () => {
  if (mode !== "audio") return;
  emit("time", { current: 0, duration: audio.duration || 0 });
});

audio.addEventListener("durationchange", () => {
  if (mode !== "audio") return;
  emit("time", { current: audio.currentTime || 0, duration: audio.duration || 0 });
});

audio.addEventListener("play", () => emit("state", { playing: true }));
audio.addEventListener("pause", () => emit("state", { playing: false }));
audio.addEventListener("ended", () => {
  if (mode === "audio") emit("ended", {});
});
audio.addEventListener("error", () => {
  if (mode === "audio" && audio.src) {
    emit("error", { message: "Não foi possível carregar o áudio desta faixa." });
  }
});

export async function load(track, stream, { autoplay = true } = {}) {
  if (stream.kind === "widget") {
    mode = "widget";
    audio.pause();
    audio.removeAttribute("src");
    emit("mode", { mode });
    await widget.load(
      track,
      stream.url,
      {
        onProgress: (payload) => emit("time", payload),
        onState: (payload) => emit("state", payload),
        onEnded: () => emit("ended", {}),
        onError: (message) => emit("error", { message }),
        onBlocked: () => emit("blocked", {}),
      },
      autoplay
    );
    widget.setVolume(mute ? 0 : volume);
    return;
  }

  mode = "audio";
  widget.teardown();
  emit("mode", { mode });
  audio.src = stream.url;
  audio.volume = mute ? 0 : volume;
  audio.load();
  if (autoplay) await play();
}

export async function play() {
  if (mode === "widget") {
    widget.play();
    return;
  }
  try {
    await audio.play();
  } catch (err) {
    if (err?.name !== "AbortError") {
      emit("error", { message: "O navegador bloqueou a reprodução. Clique no play." });
    }
  }
}

export function pause() {
  if (mode === "widget") {
    widget.pause();
    return;
  }
  audio.pause();
}

export function seek(seconds) {
  if (mode === "widget") {
    widget.seek(seconds);
    return;
  }
  if (Number.isFinite(seconds)) {
    try {
      audio.currentTime = Math.max(0, seconds);
    } catch {
      /* ignora antes do metadata */
    }
  }
}

export function setVolume(value) {
  volume = Math.min(1, Math.max(0, Number(value) || 0));
  audio.volume = mute ? 0 : volume;
  if (mode === "widget") widget.setVolume(mute ? 0 : volume);
}

export function setMuted(value) {
  mute = Boolean(value);
  audio.volume = mute ? 0 : volume;
  if (mode === "widget") widget.setVolume(mute ? 0 : volume);
}

export function getVolume() {
  return volume;
}

export function isMuted() {
  return mute;
}

export function time() {
  if (mode === "widget") return null;
  return audio.currentTime || 0;
}

export function duration() {
  if (mode === "widget") return widget.totalDuration();
  return audio.duration || 0;
}

/** Garante uma duração antes de confirmar um seek (widget demora a responder). */
export async function ensureDuration() {
  if (mode === "widget") return widget.refreshDuration();
  if (Number.isFinite(audio.duration) && audio.duration > 0) return audio.duration;

  return new Promise((resolve) => {
    let timer = null;
    const done = () => {
      cleanup();
      resolve(Number.isFinite(audio.duration) ? audio.duration : 0);
    };
    const cleanup = () => {
      clearTimeout(timer);
      audio.removeEventListener("loadedmetadata", done);
      audio.removeEventListener("durationchange", done);
    };
    timer = setTimeout(() => {
      cleanup();
      resolve(Number.isFinite(audio.duration) ? audio.duration : 0);
    }, 3000);
    audio.addEventListener("loadedmetadata", done);
    audio.addEventListener("durationchange", done);
  });
}

export function isWidgetMode() {
  return mode === "widget";
}

export function reset() {
  mode = "audio";
  audio.pause();
  audio.removeAttribute("src");
  widget.teardown();
}
