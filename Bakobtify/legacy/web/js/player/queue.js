/** Fila de reprodução e ações de transporte. */

import { api } from "../api.js";
import { loadHistory } from "../data.js";
import { setState, state } from "../state.js";
import { toast } from "../ui/toast.js";
import * as engine from "./engine.js";

let requestToken = 0;

export function initQueue() {
  engine.on("state", ({ playing }) => setState({ playing }));
  engine.on("mode", ({ mode }) => setState({ widgetMode: mode === "widget" }));
  engine.on("ended", () => next(true));
  engine.on("blocked", () =>
    toast("O navegador bloqueou o início automático. Toque no play.", "info", 6000)
  );
  engine.on("error", ({ message }) => {
    if (message) toast.error(message);
  });
}

export async function playList(tracks, index = 0) {
  const list = tracks.filter(Boolean);
  if (!list.length) return;
  const safeIndex = Math.min(Math.max(0, index), list.length - 1);
  setState({ queue: list, queueIndex: safeIndex });
  await playAt(safeIndex);
}

export async function playNow(track) {
  if (!track) return;
  const existing = state.queue.findIndex((t) => String(t.id) === String(track.id));
  if (existing >= 0) {
    await playAt(existing);
    return;
  }
  const head = state.queue.slice(0, state.queueIndex + 1);
  const queue = [...head, track];
  setState({ queue, queueIndex: queue.length - 1 });
  await playAt(queue.length - 1);
}

export async function playAt(index, { autoplay = true } = {}) {
  const track = state.queue[index];
  if (!track) return;

  const mine = ++requestToken;
  setState({ queueIndex: index, track });

  try {
    const stream = await api.stream(track.id);
    if (mine !== requestToken) return;

    await engine.load(track, stream, { autoplay });
    if (mine !== requestToken) return;
    window.dispatchEvent(new Event("bakobtify:health"));

    if (autoplay) {
      api.logPlay(track).catch(() => {});
      if (state.view === "history") loadHistory({ silent: true });
    }
  } catch (err) {
    if (mine !== requestToken) return;
    setState({ playing: false });
    window.dispatchEvent(new Event("bakobtify:health"));
    toast.error(err.message || "Não foi possível tocar esta faixa.");
  }
}

export async function toggle() {
  if (!state.track) return;
  if (state.playing) engine.pause();
  else await engine.play();
}

export async function next(auto = false) {
  const queue = state.queue;
  if (!queue.length) return;

  if (auto && state.repeat === "one") {
    engine.seek(0);
    await engine.play();
    return;
  }

  if (state.shuffle && queue.length > 1) {
    let index = state.queueIndex;
    while (index === state.queueIndex) {
      index = Math.floor(Math.random() * queue.length);
    }
    await playAt(index);
    return;
  }

  if (state.queueIndex + 1 < queue.length) {
    await playAt(state.queueIndex + 1);
    return;
  }

  if (state.repeat === "all") {
    await playAt(0);
    return;
  }

  if (auto) engine.pause();
}

export async function prev() {
  if (!state.track) return;
  const position = engine.time();
  if (position != null && position > 3) {
    engine.seek(0);
    return;
  }
  if (state.queueIndex > 0) {
    await playAt(state.queueIndex - 1);
    return;
  }
  engine.seek(0);
}

export function setShuffle(value) {
  setState({ shuffle: Boolean(value) });
}

export function cycleRepeat() {
  const order = ["off", "all", "one"];
  const nextValue = order[(order.indexOf(state.repeat) + 1) % order.length];
  setState({ repeat: nextValue });
  return nextValue;
}
