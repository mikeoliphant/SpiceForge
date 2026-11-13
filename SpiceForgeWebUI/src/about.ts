// The About dialog, opened from the SpiceForge button at the toolbar's left: the version and commit, copyright and
// license, the source repository, and the third-party components the server ships, each with its license text (served
// by the server from licenses/, beside it) shown in place. It all comes from hello's about (SpiceForgeServer/About.cs).

import type { About } from "./protocol";

function h<K extends keyof HTMLElementTagNameMap>(tag: K, props: Partial<HTMLElementTagNameMap[K]> = {}, ...children: (Node | string)[]) {
  const e = document.createElement(tag);
  Object.assign(e, props);
  e.append(...children);
  return e;
}

/** A link that opens outside the editor's window. */
function link(href: string, text: string): HTMLAnchorElement {
  return h("a", { href, textContent: text, target: "_blank", rel: "noopener noreferrer" });
}

/** "SpiceForge v0.0.1", as the status line names the server (the brand button shows the same, the version muted). */
export function versionLabel(about: About): string {
  return `${about.name} v${about.version}`;
}

/** A license text, fetched the first time its section is opened. */
function licenseText(summary: string, file: string): HTMLDetailsElement {
  const text = h("pre", { className: "license-text", textContent: "Loading…" });
  const details = h("details", { className: "license" }, h("summary", { textContent: summary }), text);
  let loaded = false;
  details.addEventListener("toggle", async () => {
    if (!details.open || loaded) return;
    loaded = true;
    try {
      const response = await fetch(`licenses/${encodeURIComponent(file)}`);
      if (!response.ok) throw new Error(`${response.status} ${response.statusText}`);
      text.textContent = await response.text();
    } catch (e) {
      loaded = false;
      text.textContent = `Couldn't load the license (${(e as Error).message}). It's in the licenses folder beside the server.`;
    }
  });
  return details;
}

/** Shows the dialog until it's closed. */
export function showAbout(about: About): void {
  const commit = about.commit ? about.commit.slice(0, 7) : "";
  const version = h(
    "p",
    { className: "about-version" },
    `Version ${about.version}`,
    ...(commit ? [", commit ", link(`${about.repository}/commit/${about.commit}`, commit)] : []),
  );
  const copy = h("button", { textContent: "Copy version info", title: "For a bug report: the version, commit, browser and system" });
  copy.addEventListener("click", async () => {
    const info = [`${about.name} ${about.version}${commit ? ` (${about.commit})` : ""}`, navigator.userAgent].join("\n");
    try {
      await navigator.clipboard.writeText(info);
      copy.textContent = "Copied";
    } catch {
      copy.textContent = "Couldn't copy";
    }
    setTimeout(() => (copy.textContent = "Copy version info"), 1500);
  });
  const close = h("button", { textContent: "Close", className: "primary" });

  const components = h(
    "ul",
    { className: "about-components" },
    ...about.components.map((c) =>
      h(
        "li",
        {},
        h("div", {}, link(c.url, c.name), ` by ${c.by}, ${c.license}`),
        licenseText("License", c.file),
        ...(c.notices ? [h("div", { className: "about-notices" }, link(c.notices, "Third-party notices"))] : []),
      ),
    ),
  );

  const dialog = h(
    "dialog",
    { className: "file-dialog about-dialog" },
    h("div", { className: "about-head" }, h("img", { src: "icon-256.png", alt: "", width: 56, height: 56 }), h("div", {}, h("h2", { textContent: about.name }), version)),
    h("p", { textContent: "A circuit simulator and schematic editor for audio, built on LiveSPICE." }),
    h("p", {}, `${about.copyright}. `, link(about.repository, "Source on GitHub"), "."),
    licenseText(about.license, about.licenseFile),
    h("h3", { textContent: "Third-party components" }),
    components,
    h("div", { className: "buttons" }, copy, close),
  );
  const finish = () => {
    dialog.close();
    dialog.remove();
  };
  close.addEventListener("click", finish);
  dialog.addEventListener("cancel", (e) => {
    e.preventDefault();
    finish();
  });
  document.body.append(dialog);
  dialog.showModal();
  close.focus();
}
