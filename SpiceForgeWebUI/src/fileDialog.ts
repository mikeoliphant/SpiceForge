// The Open and Save As dialogs, on the server's file system (WebSocketApi.md, Files: a web page can't get native file
// paths, so it browses with fs.list). Folders and schematics, a path box, the recent files, and for saving, a name.
// Saving and choosing a folder can make a new folder (fs.mkdir); saving can delete the schematic its name box names,
// after asking (fs.delete).
//
// Enter is handled on keydown and its default prevented: finishing closes the dialog, focus returns to the button that
// opened it, and an Enter left to run its course would then press that button and open the dialog again.

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
  /** Whether a file is open in the editor, which can't be deleted from under it. */
  isOpen?: (path: string) => boolean;
}

/** Runs `action` on Enter, and stops the Enter there (see the top of the file). */
function onEnter(element: HTMLElement, action: (e: KeyboardEvent) => void) {
  element.addEventListener("keydown", (e) => {
    if (e.key !== "Enter") return;
    e.preventDefault();
    action(e);
  });
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
    const newFolder = h("button", { textContent: "New folder", title: "Make a folder here" });
    const remove = h("button", { textContent: "Delete", className: "left", disabled: true });
    const canMakeFolders = saving || choosingFolder;
    const dialog = h(
      "dialog",
      { className: "file-dialog" },
      h("h2", { textContent: saving ? "Save schematic as" : choosingFolder ? "Choose a folder" : "Open schematic" }),
      h("div", { className: "path-row" }, up, pathBox, ...(canMakeFolders ? [newFolder] : [])),
      h("div", { className: "panes" }, h("div", { className: "folder" }, list), h("div", { className: "side" }, h("h3", { textContent: "Recent" }), recent)),
      error,
      saving ? h("div", { className: "name-row" }, h("label", { textContent: "File name" }), nameBox) : "",
      h("div", { className: "buttons" }, ...(saving ? [remove, save, cancel] : choosingFolder ? [useFolder, cancel] : [cancel])),
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

    /** The schematic in this folder that the name box names, if there is one: what Delete deletes. */
    const named = () => {
      const name = nameBox.value.trim().toLowerCase();
      return listing?.entries.find((e) => e.kind === "file" && e.name.toLowerCase() === name) ?? null;
    };
    /** Marks the named schematic in the list, and offers to delete it. */
    const showNamed = () => {
      const entry = named();
      for (const item of list.querySelectorAll("li")) item.classList.toggle("chosen", item.dataset["name"] === entry?.name);
      remove.disabled = !entry;
      remove.title = entry ? `Delete ${entry.name}` : "Delete the schematic named below";
    };

    const deleteNamed = async () => {
      const entry = named();
      if (!entry || !listing) return;
      const path = join(listing.path, entry.name);
      if (options.isOpen?.(path)) {
        error.textContent = `${entry.name} is open; close it first.`;
        return;
      }
      if (!confirm(`Delete ${entry.name}? This can't be undone.`)) return;
      try {
        await client.request("fs.delete", { path });
      } catch (e) {
        error.textContent = (e as Error).message;
        return;
      }
      await show(listing.path);
      void showRecent();
    };

    /** A name box in the list for a new folder: Enter makes it and goes into it, Escape or leaving drops it. */
    const startNewFolder = () => {
      if (!listing || list.querySelector(".new-folder")) return;
      const input = h("input", { type: "text", className: "name", spellcheck: false, value: "New folder" });
      const item = h("li", { className: "dir new-folder" }, input);
      list.querySelector(".empty")?.remove();
      list.prepend(item);
      input.focus();
      input.select();
      let done = false;
      const drop = () => {
        done = true;
        item.remove();
      };
      onEnter(input, async () => {
        const name = input.value.trim();
        if (!name || !listing) return drop();
        if (/[\\/]/.test(name)) {
          error.textContent = "A folder name can't contain \\ or /.";
          return;
        }
        done = true;
        try {
          const made = await client.request<{ path: string }>("fs.mkdir", { path: join(listing.path, name) });
          await show(made.path);
          (saving ? nameBox : useFolder).focus();
        } catch (e) {
          error.textContent = (e as Error).message;
          done = false;
          input.focus();
        }
      });
      input.addEventListener("keydown", (e) => {
        if (e.key !== "Escape") return;
        // Only the new folder's box closes, not the dialog.
        e.preventDefault();
        e.stopPropagation();
        drop();
      });
      input.addEventListener("blur", () => !done && drop());
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
          item.dataset["name"] = entry.name;
          const target = join(listing!.path, entry.name);
          // Saving: a file click picks its name (double-click saves over it); opening: a click opens it.
          const choose = () => {
            if (entry.kind === "dir") return show(target);
            if (saving) {
              nameBox.value = entry.name;
              showNamed();
            } else finish(target);
          };
          item.addEventListener("click", () => entry.kind === "file" && choose());
          item.addEventListener("dblclick", () => (entry.kind === "dir" ? show(target) : saving ? saveTo() : finish(target)));
          onEnter(item, choose);
          return item;
        }),
      );
      if (listing.entries.length === 0)
        list.append(h("li", { className: "empty", textContent: choosingFolder ? "No folders here." : "No folders or schematics here." }));
      showNamed();
    }

    async function showRecent() {
      let files: string[];
      try {
        ({ files } = await client.request<{ files: string[] }>("fs.recent"));
      } catch {
        return;
      }
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
          onEnter(item, pick);
          return item;
        }),
      );
      if (files.length === 0) recent.append(h("li", { className: "empty", textContent: "None yet." }));
    }

    up.addEventListener("click", () => listing?.parent && show(listing.parent));
    onEnter(pathBox, () => {
      const path = pathBox.value.trim();
      if (isSchematic(path) && options.mode === "open") finish(path);
      else void show(path);
    });
    onEnter(nameBox, saveTo);
    nameBox.addEventListener("input", showNamed);
    save.addEventListener("click", saveTo);
    remove.addEventListener("click", () => void deleteNamed());
    newFolder.addEventListener("click", startNewFolder);
    useFolder.addEventListener("click", () => listing && finish(listing.path));
    cancel.addEventListener("click", () => finish(null));
    dialog.addEventListener("cancel", (e) => {
      e.preventDefault();
      finish(null);
    });

    void showRecent();
    dialog.showModal();
    void show(folder).then(() => saving && nameBox.focus());
  });
}
