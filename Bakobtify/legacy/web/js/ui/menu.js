/** Popover de ações de uma faixa. */

import {
  addToPlaylist,
  createPlaylist,
  removeFromPlaylist,
  toggleFavorite,
} from "../data.js";
import { isFavorite, state } from "../state.js";
import { playNow } from "../player/queue.js";
import { promptText } from "./dialog.js";
import { toast } from "./toast.js";

let currentMenu = null;
let removeOutside = null;

function closeMenu() {
  if (currentMenu) {
    currentMenu.remove();
    currentMenu = null;
  }
  if (removeOutside) {
    removeOutside();
    removeOutside = null;
  }
}

function positionMenu(menu, anchor) {
  const rect = anchor.getBoundingClientRect();
  const width = menu.offsetWidth;
  const height = menu.offsetHeight;
  let left = rect.right - width;
  let top = rect.bottom + 6;

  if (left < 8) left = 8;
  if (left + width > window.innerWidth - 8) left = window.innerWidth - width - 8;
  if (top + height > window.innerHeight - 8) top = rect.top - height - 6;
  if (top < 8) top = 8;

  menu.style.left = `${left}px`;
  menu.style.top = `${top}px`;
}

function buildMenu(items) {
  const menu = document.createElement("div");
  menu.className = "menu";
  menu.setAttribute("role", "menu");

  for (const item of items) {
    if (item.separator) {
      const sep = document.createElement("div");
      sep.className = "menu-sep";
      menu.appendChild(sep);
      continue;
    }
    if (item.label) {
      const label = document.createElement("div");
      label.className = "menu-label";
      label.textContent = item.label;
      menu.appendChild(label);
      continue;
    }
    const button = document.createElement("button");
    button.className = `menu-item${item.danger ? " danger" : ""}`;
    button.setAttribute("role", "menuitem");
    button.innerHTML = `${item.icon ? `<svg><use href="#${item.icon}"/></svg>` : ""}<span></span>`;
    button.querySelector("span").textContent = item.text;
    button.addEventListener("click", async () => {
      closeMenu();
      try {
        await item.action?.();
      } catch (err) {
        toast.error(err.message || "Ação falhou.");
      }
    });
    menu.appendChild(button);
  }
  return menu;
}

function showMenu(items, anchor) {
  closeMenu();
  const menu = buildMenu(items);
  document.body.appendChild(menu);
  positionMenu(menu, anchor);
  currentMenu = menu;

  const onPointerDown = (event) => {
    if (!menu.contains(event.target)) closeMenu();
  };
  const onKey = (event) => {
    if (event.key === "Escape") closeMenu();
  };
  const onScroll = () => closeMenu();

  setTimeout(() => {
    document.addEventListener("pointerdown", onPointerDown);
    window.addEventListener("keydown", onKey);
    window.addEventListener("resize", onScroll);
    window.addEventListener("scroll", onScroll, true);
  }, 0);

  removeOutside = () => {
    document.removeEventListener("pointerdown", onPointerDown);
    window.removeEventListener("keydown", onKey);
    window.removeEventListener("resize", onScroll);
    window.removeEventListener("scroll", onScroll, true);
  };
}

export function openTrackMenu(track, anchor, { playlistId = null } = {}) {
  if (!track) return;
  const favorite = isFavorite(track.id);

  const items = [
    { text: "Tocar agora", icon: "i-play", action: () => playNow(track) },
    {
      text: favorite ? "Remover das favoritas" : "Adicionar às favoritas",
      icon: "i-heart",
      action: () => toggleFavorite(track),
    },
    { separator: true },
    { label: "Adicionar a playlist" },
  ];

  for (const playlist of state.playlists) {
    items.push({
      text: playlist.name,
      icon: "i-plus",
      action: () => addToPlaylist(playlist.id, track),
    });
  }

  items.push({
    text: "Nova playlist…",
    icon: "i-plus",
    action: async () => {
      const name = await promptText({
        title: "Nova playlist",
        description: `Adicionar “${track.title}” a uma nova playlist.`,
        placeholder: "Nome da playlist",
        confirmLabel: "Criar e adicionar",
      });
      if (!name) return;
      const playlist = await createPlaylist(name);
      if (playlist) await addToPlaylist(playlist.id, track);
    },
  });

  items.push({ separator: true });

  if (track.permalink_url) {
    items.push({
      text: "Abrir no SoundCloud",
      icon: "i-link",
      action: () => window.open(track.permalink_url, "_blank", "noopener"),
    });
  }

  if (playlistId != null) {
    items.push({
      text: "Remover desta playlist",
      icon: "i-trash",
      danger: true,
      action: () => removeFromPlaylist(playlistId, track.id),
    });
  }

  showMenu(items, anchor);
}

export { closeMenu };
