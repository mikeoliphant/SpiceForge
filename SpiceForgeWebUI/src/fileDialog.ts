// The Open and Save As dialogs, on the server's file system (WebSocketApi.md, Files: a web page can't get native file
// paths, so it browses with fs.list). Folders and schematics, a path box, the recent files, and for saving, a name.

import type { ApiClient } from "./client";
import type { FolderListing } from "./protocol";

function h<K extends keyof HTMLElementTagNameMap>(tag: K, props: Partial<HTMLElementTagNameMap[K]> = {}, ...children: (Node | string)[]) {
  const e = document.createElement(tag);
  Object.assign(e, props);
  e.append(...children);
  return e;
}

const Extensions = [".schx", ".xml"];

export interface FileDialogOptions {
  /** Open a schematic, save one, or choose a folder (folders only are listed). */
  mode: "open" | "save" | "folder";
  /** The file name to start with, when saving. */
  name?: string;
}

const isSchematic = (path: string) => Extensions.some((e) => path.toLowerCase().endsWith(e));
const separator = (dir: string) => (dir.includes("\\") ? "\\" : "/");
const join = (dir: string, name: string) => (dir.endsWith("\\") || dir.endsWith("/") ? dir + name : `${dir}${separator(dir)}${name}`);

/** Shows the dialog, starting in `folder`; resolves with the chosen path, or null if cancelled. Saving adds ".schx" to
 * a name without an extension; the caller confirms overwriting. */
export function chooseFile(client: ApiClient, folder: string, options: FileDialogOptions = { mode: "open" }): Promise<string | null> {
  const saving = options.mode === "save";
  const choosingFolder = options.mode === "folder";
  return new Promise((resolve) => {
    const pathBox = h("input", { type: "text", className: "path", spellcheck: false });
    const up = h("button", { textContent: "Up", title: "The parent folder" });
    const list = h("ul", { className: "entries" });
    const recent = h("ul", { className: "recent" });
    const error = h("div", { className: "error" });
    const cancel = h("button", { textContent: "Cancel" });
    const nameBox = h("input", { type: "text", className: "name", spellcheck: false, value: options.name ?? "" });
    const save = h("button", { textContent: "Save", className: "primary" });
    const useFolder = h("button", { textContent: "Use this folder", className: "primary" });
    const dialog = h(
      "dialog",
      { className: "file-dialog" },
      h("h2", { textContent: saving ? "Save schematic as" : choosingFolder ? "Choose a folder" : "Open schematic" }),
      h("div", { className: "path-row" }, up, pathBox),
      h("div", { className: "panes" }, h("div", { className: "folder" }, list), h("div", { className: "side" }, h("h3", { textContent: "Recent" }), recent)),
      error,
      saving ? h("div", { className: "name-row" }, h("label", { textContent: "File name" }), nameBox) : "",
      h("div", { className: "buttons" }, ...(saving ? [save, cancel] : choosingFolder ? [useFolder, cancel] : [cancel])),
    );
    document.body.append(dialog);

    let listing: FolderListing | null = null;
    const finish = (path: string | null) => {
      dialog.close();
      dialog.remove();
      resolve(path);
    };
    const saveTo = () => {
      let name = nameBox.value.trim();
      if (!name || !listing) {
        error.textContent = "Enter a file name.";
        return;
      }
      if (/[\\/]/.test(name)) return finish(isSchematic(name) ? name : `${name}.schx`);
      if (!/\.[^.]+$/.test(name)) name += ".schx";
      finish(join(listing.path, name));
    };

    async function show(path: string) {
      error.textContent = "";
      try {
        listing = await client.request<FolderListing>("fs.list", { path, extensions: Extensions });
        if (choosingFolder) listing = { ...listing, entries: listing.entries.filter((e) => e.kind === "dir") };
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
          // Saving: a file click picks its name (double-click saves over it); opening: a click opens it.
          const choose = () => {
            if (entry.kind === "dir") return show(target);
            if (saving) nameBox.value = entry.name;
            else finish(target);
          };
          item.addEventListener("click", () => entry.kind === "file" && choose());
          item.addEventListener("dblclick", () => (entry.kind === "dir" ? show(target) : saving ? saveTo() : finish(target)));
          item.addEventListener("keydown", (e) => e.key === "Enter" && choose());
          return item;
        }),
      );
      if (listing.entries.length === 0)
        list.append(h("li", { className: "empty", textContent: choosingFolder ? "No folders here." : "No folders or schematics here." }));
    }

    up.addEventListener("click", () => listing?.parent && show(listing.parent));
    pathBox.addEventListener("keydown", (e) => {
      if (e.key !== "Enter") return;
      const path = pathBox.value.trim();
      if (isSchematic(path) && options.mode === "open") finish(path);
      else show(path);
    });
    nameBox.addEventListener("keydown", (e) => e.key === "Enter" && saveTo());
    save.addEventListener("click", saveTo);
    useFolder.addEventListener("click", () => listing && finish(listing.path));
    cancel.addEventListener("click", () => finish(null));
    dialog.addEventListener("cancel", (e) => {
      e.preventDefault();
      finish(null);
    });

    client.request<{ files: string[] }>("fs.recent").then(({ files }) => {
      recent.replaceChildren(
        ...files.map((file) => {
          const item = h("li", { textContent: file.split(/[\\/]/).pop() ?? file, title: file, tabIndex: 0 });
          const pick = () => {
            if (!saving && !choosingFolder) return finish(file);
            // Saving: go to the recent file's folder, with its name.
            nameBox.value = file.split(/[\\/]/).pop() ?? "";
            void show(file.replace(/[\\/][^\\/]*$/, ""));
          };
          item.addEventListener("click", pick);
          item.addEventListener("keydown", (e) => e.key === "Enter" && pick());
          return item;
        }),
      );
      if (files.length === 0) recent.append(h("li", { className: "empty", textContent: "None yet." }));
    });

    dialog.showModal();
    void show(folder).then(() => saving && nameBox.focus());
  });
}
