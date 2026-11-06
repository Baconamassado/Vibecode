/** Dialog nativo reaproveitado para entradas de texto. */

let resolveCurrent = null;

function refs() {
  return {
    dialog: document.getElementById("dialog"),
    form: document.getElementById("dialog-form"),
    title: document.getElementById("dialog-title"),
    desc: document.getElementById("dialog-desc"),
    input: document.getElementById("dialog-input"),
    cancel: document.getElementById("dialog-cancel"),
    ok: document.getElementById("dialog-ok"),
  };
}

function settle(value) {
  const resolve = resolveCurrent;
  resolveCurrent = null;
  const { dialog } = refs();
  if (dialog.open) dialog.close();
  if (resolve) resolve(value);
}

export function promptText({
  title = "Digite um nome",
  description = "",
  placeholder = "",
  confirmLabel = "Confirmar",
} = {}) {
  const { dialog, form, title: titleEl, desc, input, cancel, ok } = refs();
  if (!dialog || !form) return Promise.resolve(null);

  if (dialog.open) settle(null);

  titleEl.textContent = title;
  desc.textContent = description;
  desc.hidden = !description;
  input.hidden = false;
  input.value = "";
  input.placeholder = placeholder;
  ok.textContent = confirmLabel;

  form.onsubmit = (event) => {
    event.preventDefault();
    const value = input.value.trim();
    if (!value) {
      input.focus();
      return;
    }
    settle(value);
  };
  cancel.onclick = () => settle(null);
  dialog.oncancel = (event) => {
    event.preventDefault();
    settle(null);
  };

  return new Promise((resolve) => {
    resolveCurrent = resolve;
    dialog.showModal();
    requestAnimationFrame(() => input.focus());
  });
}

export function confirmAction({
  title = "Tem certeza?",
  description = "",
  confirmLabel = "Confirmar",
} = {}) {
  const { dialog, form, title: titleEl, desc, input, cancel, ok } = refs();
  if (!dialog || !form) return Promise.resolve(false);

  if (dialog.open) settle(null);

  titleEl.textContent = title;
  desc.textContent = description;
  desc.hidden = !description;
  input.hidden = true;
  input.value = "";
  ok.textContent = confirmLabel;

  form.onsubmit = (event) => {
    event.preventDefault();
    settle(true);
  };
  cancel.onclick = () => settle(false);
  dialog.oncancel = (event) => {
    event.preventDefault();
    settle(false);
  };

  return new Promise((resolve) => {
    resolveCurrent = (value) => resolve(Boolean(value));
    dialog.showModal();
  });
}
