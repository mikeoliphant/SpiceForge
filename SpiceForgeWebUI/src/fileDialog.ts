// An Open dialog on the server's file system (WebSocketApi.md, Files: a web page can't get native file paths, so it
// browses with fs.list). Folders and schematics, a path box, and the recent files.

import type { ApiClient } from "./client";
import type { FolderListing } from "./protocol";

function h<K extends keyof HTMLElementTagNameMap>(tag: K, props: Partial<HTMLElementTagNameMap[K]> = {}, ...children: (Node | string)[]) {
  const e = document.createElement(tag);
  Object.assign(e, props);
  e.append(...children);
  return e;
}

const Extensions = [".schx", ".xml"];

/** Shows the dialog, starting in `folder`; resolves with the chosen file's path, or null if cancelled. */
export function chooseFile(client: ApiClient, folder: string): Promise<string | null> {
  return new Promise((resolve) => {
    const pathBox = h("input", { type: "text", className: "path", spellcheck: false });
    const up = h("button", { textContent: "Up", title: "The parent folder" });
    const list = h("ul", { className: "entries" });
    const recent = h("ul", { className: "recent" });
    const error = h("div", { className: "error" });
    const cancel = h("button", { textContent: "Cancel" });
    const dialog = h(
      "dialog",
      { className: "file-dialog" },
      h("h2", { textContent: "Open schematic" }),
      h("div", { className: "path-row" }, up, pathBox),
      h("div", { className: "panes" }, h("div", { className: "folder" }, list), h("div", { className: "side" }, h("h3", { textContent: "Recent" }), recent)),
      error,
      h("div", { className: "buttons" }, cancel),
    );
    document.body.append(dialog);

    let listing: FolderListing | null = null;
    const finish = (path: string | null) => {
      dialog.close();
      dialog.remove();
      resolve(path);
    };
    const isSchematic = (path: string) => Extensions.some((e) => path.toLowerCase().endsWith(e));
    const join = (dir: string, name: string) => (dir.endsWith("\\") || dir.endsWith("/") ? dir + name : `${dir}${dir.includes("\\") ? "\\" : "/"}${name}`);

    async function show(path: string) {
      error.textContent = "";
      try {
        listing = await client.request<FolderListing>("fs.list", { path, extensions: Extensions });
      } catch (e) {
        error.textContent = (e as Error).message;
        return;
      }
      pathBox.value = listing.path;
      up.disabled = !listing.parent;
      list.replaceChildren(
        ...listing.entries.map((entry) => {
          const item = h("li", { className: entry.kind, textContent: entry.name, tabIndex: 0 });
          const target = join(listing!.path, entry.name);
          const activate = () => (entry.kind === "dir" ? show(target) : finish(target));
          item.addEventListener("dblclick", activate);
          item.addEventListener("click", () => entry.kind === "file" && finish(target));
          item.addEventListener("keydown", (e) => e.key === "Enter" && activate());
          return item;
        }),
      );
      if (listing.entries.length === 0) list.append(h("li", { className: "empty", textContent: "No folders or schematics here." }));
    }

    up.addEventListener("click", () => listing?.parent && show(listing.parent));
    pathBox.addEventListener("keydown", (e) => {
      if (e.key !== "Enter") return;
      const path = pathBox.value.trim();
      if (isSchematic(path)) finish(path);
      else show(path);
    });
    cancel.addEventListener("click", () => finish(null));
    dialog.addEventListener("cancel", (e) => {
      e.preventDefault();
      finish(null);
    });

    client.request<{ files: string[] }>("fs.recent").then(({ files }) => {
      recent.replaceChildren(
        ...files.map((file) => {
          const item = h("li", { textContent: file.split(/[\\/]/).pop() ?? file, title: file, tabIndex: 0 });
          item.addEventListener("click", () => finish(file));
          item.addEventListener("keydown", (e) => e.key === "Enter" && finish(file));
          return item;
        }),
      );
      if (files.length === 0) recent.append(h("li", { className: "empty", textContent: "None yet." }));
    });

    dialog.showModal();
    void show(folder);
  });
}
