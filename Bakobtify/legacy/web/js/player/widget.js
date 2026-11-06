/** Fallback de reprodução: embed oficial do SoundCloud + Widget API. */

let apiPromise = null;
let frame = null;
let widget = null;
let poll = null;
let duration = 0;
let handlers = {};
let currentVolume = 0.8;

function loadWidgetApi() {
  if (window.SC?.Widget) return Promise.resolve(window.SC);
  if (!apiPromise) {
    apiPromise = new Promise((resolve, reject) => {
      const script = document.createElement("script");
      script.src = "https://w.soundcloud.com/player/api.js";
      script.async = true;
      script.onload = () =>
        window.SC?.Widget ? resolve(window.SC) : reject(new Error("SC ausente"));
      script.onerror = () => reject(new Error("Falha ao carregar a API do widget"));
      document.head.appendChild(script);
    }).catch((err) => {
      apiPromise = null;
      throw err;
    });
  }
  return apiPromise;
}

export function isActive() {
  return Boolean(widget);
}

export async function load(track, permalink, nextHandlers, autoplay = true) {
  teardown();
  handlers = nextHandlers || {};

  if (!permalink) {
    handlers.onError?.("Esta faixa não está disponível para reprodução.");
    return;
  }

  const dock = document.getElementById("widget-dock");
  if (!dock) return;
  dock.innerHTML = "";

  frame = document.createElement("iframe");
  frame.title = `SoundCloud — ${track.title}`;
  // encrypted-media é obrigatório: sem essa delegação o embed não toca áudio
  frame.setAttribute(
    "allow",
    "autoplay; encrypted-media; fullscreen; picture-in-picture"
  );
  frame.setAttribute("scrolling", "no");
  frame.setAttribute("tabindex", "-1");
  frame.setAttribute("aria-hidden", "true");
  frame.height = "2";
  frame.style.width = "2px";
  frame.style.height = "2px";
  frame.style.border = "0";
  frame.style.opacity = "0.01";
  frame.style.pointerEvents = "none";
  frame.style.display = "block";

  const params = new URLSearchParams({
    url: permalink,
    color: "#8b5cf6",
    auto_play: autoplay ? "true" : "false",
    hide_related: "true",
    show_comments: "false",
    show_user: "false",
    show_reposts: "false",
    show_teaser: "false",
    visual: "false",
    enable_api: "true",
  });
  frame.src = `https://w.soundcloud.com/player/?${params}`;
  dock.appendChild(frame);

  try {
    const SC = await loadWidgetApi();
    widget = SC.Widget(frame);

    await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error("Widget demorou demais")), 12000);
      widget.bind(SC.Widget.Events.READY, () => {
        clearTimeout(timer);
        resolve();
      });
    });

    const E = SC.Widget.Events;
    widget.bind(E.PLAY, () => handlers.onState?.({ playing: true }));
    widget.bind(E.PAUSE, () => handlers.onState?.({ playing: false }));
    widget.bind(E.FINISH, () => handlers.onEnded?.());
    try {
      widget.bind(E.ERROR, () => handlers.onError?.("O SoundCloud recusou esta faixa."));
    } catch {
      /* evento pode não existir em versões antigas */
    }

    widget.getDuration((ms) => {
      duration = (ms || 0) / 1000;
    });
    widget.setVolume(Math.round(currentVolume * 100));

    // O embed pode iniciar sozinho (auto_play) antes de ligarmos os eventos,
    // então forçamos o play e sincronizamos o estado real depois.
    if (autoplay) widget.play();

    setTimeout(() => {
      if (!widget) return;
      try {
        widget.isPaused((paused) => {
          handlers.onState?.({ playing: !paused });
          if (paused && autoplay) {
            handlers.onBlocked?.();
            // o próximo clique em qualquer lugar serve de gesto para liberar o áudio
            document.addEventListener(
              "pointerdown",
              () => {
                if (widget) widget.play();
              },
              { once: true, capture: true }
            );
          }
        });
      } catch {
        /* widget pode não suportar a consulta */
      }
    }, 1800);

    poll = setInterval(() => {
      if (!widget) return;
      widget.getPosition((ms) => {
        if (!widget) return;
        if (!duration) {
          widget.getDuration((total) => {
            duration = (total || 0) / 1000;
          });
        }
        handlers.onProgress?.({ current: (ms || 0) / 1000, duration });
      });
    }, 500);
  } catch (err) {
    // Sem a API do widget ainda dá para ouvir: revela o player oficial.
    revealDock();
    handlers.onError?.(err.message || "Não foi possível controlar o embed.");
  }
}

/** Mostra o embed visível quando a Widget API não responde. */
function revealDock() {
  const dock = document.getElementById("widget-dock");
  if (!dock || !frame) return;
  dock.classList.add("is-visible");
  frame.style.width = "100%";
  frame.style.height = "166px";
  frame.style.opacity = "1";
  frame.style.pointerEvents = "auto";
}

export function teardown() {
  if (poll) {
    clearInterval(poll);
    poll = null;
  }
  widget = null;
  duration = 0;
  if (frame) {
    frame.remove();
    frame = null;
  }
  const dock = document.getElementById("widget-dock");
  if (dock) {
    dock.classList.remove("is-visible");
    dock.innerHTML = "";
  }
}

export function play() {
  if (!widget) return;
  widget.play();
  // Se nem o play manual sair do lugar, o controle está quebrado:
  // mostra o player oficial para não deixar o usuário sem áudio.
  setTimeout(() => {
    if (!widget) return;
    widget.isPaused((paused) => {
      if (!paused) return;
      widget.getPosition((pos) => {
        if (!widget || pos > 0) return;
        revealDock();
        handlers.onError?.(
          "O embed não iniciou pelo controle. Use o player do SoundCloud no canto."
        );
      });
    });
  }, 2400);
}

export function pause() {
  widget?.pause();
}

export function seek(seconds) {
  if (!widget) return;
  widget.seekTo(Math.max(0, seconds * 1000));
  // o widget pode demorar a refletir o novo ponto; força uma leitura
  setTimeout(() => {
    if (!widget) return;
    widget.getPosition((ms) => {
      if (!widget) return;
      handlers.onProgress?.({ current: (ms || 0) / 1000, duration });
    });
  }, 350);
}

/** O widget só informa a duração por callback; o seek precisa dela antes. */
export function refreshDuration() {
  return new Promise((resolve) => {
    if (!widget) {
      resolve(duration);
      return;
    }
    try {
      widget.getDuration((ms) => {
        if (ms > 0) duration = ms / 1000;
        resolve(duration);
      });
    } catch {
      resolve(duration);
    }
  });
}

export function setVolume(value) {
  currentVolume = Math.min(1, Math.max(0, Number(value) || 0));
  if (widget) widget.setVolume(Math.round(currentVolume * 100));
}

export function currentTime() {
  return null; // o widget reporta via polling
}

export function totalDuration() {
  return duration;
}
