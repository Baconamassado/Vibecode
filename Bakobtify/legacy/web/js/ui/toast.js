/** Toasts discretos. */

const root = () => document.getElementById("toasts");

export function toast(message, kind = "info", timeout = 4200) {
  const host = root();
  if (!host) return;

  const el = document.createElement("div");
  el.className = `toast ${kind}`;
  el.textContent = message;
  host.appendChild(el);

  setTimeout(() => {
    el.classList.add("out");
    el.addEventListener("animationend", () => el.remove(), { once: true });
  }, timeout);
}

toast.error = (message) => toast(message, "error", 5200);
toast.success = (message) => toast(message, "success", 3200);
