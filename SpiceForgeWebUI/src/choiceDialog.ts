// A question with more answers than confirm()'s two: a modal dialog of buttons, styled as the editor's other dialogs.
// Escape, or the browser closing the dialog, answers null, as Cancel does.

function h<K extends keyof HTMLElementTagNameMap>(tag: K, props: Partial<HTMLElementTagNameMap[K]> = {}, ...children: (Node | string)[]) {
  const e = document.createElement(tag);
  Object.assign(e, props);
  e.append(...children);
  return e;
}

export interface Choice<T> {
  label: string;
  value: T;
  /** The answer Enter gives, marked as the editor's dialogs mark it. */
  primary?: boolean;
}

/** Asks, and resolves to the chosen value, or null for Cancel. */
export function choose<T>(title: string, message: string, choices: Choice<T>[]): Promise<T | null> {
  return new Promise((resolve) => {
    const buttons = choices.map((c) => {
      const button = h("button", { textContent: c.label, className: c.primary ? "primary" : "" });
      button.addEventListener("click", () => finish(c.value));
      return button;
    });
    const cancel = h("button", { textContent: "Cancel" });
    cancel.addEventListener("click", () => finish(null));
    const dialog = h(
      "dialog",
      { className: "file-dialog choice-dialog" },
      h("h2", { textContent: title }),
      h("p", { textContent: message }),
      h("div", { className: "buttons" }, ...buttons, cancel),
    );
    const finish = (value: T | null) => {
      dialog.close();
      dialog.remove();
      resolve(value);
    };
    dialog.addEventListener("cancel", (e) => {
      e.preventDefault();
      finish(null);
    });
    document.body.append(dialog);
    dialog.showModal();
    (buttons[choices.findIndex((c) => c.primary)] ?? cancel).focus();
  });
}
