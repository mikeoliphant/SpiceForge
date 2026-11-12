// The component folders dialog: the server's own libraries, listed, and the user's, which can be added and removed
// (WebSocketApi.md, Session and settings: componentFolders). The server reloads the catalogue when they change, and
// every client's library follows its catalog.changed.

import type { ApiClient } from "./client";
import { chooseFile } from "./fileDialog";

function h<K extends keyof HTMLElementTagNameMap>(tag: K, props: Partial<HTMLElementTagNameMap[K]> = {}, ...children: (Node | string)[]) {
  const e = document.createElement(tag);
  Object.assign(e, props);
  e.append(...children);
  return e;
}

/** Shows the dialog. `serverFolders` are hello's folders.components; `start` is where Add begins browsing. */
export async function manageComponentFolders(client: ApiClient, serverFolders: string[], start: string): Promise<void> {
  let mine = (await client.request<{ componentFolders?: string[] }>("settings.get")).componentFolders ?? [];
  const builtIn = serverFolders.filter((f) => !mine.some((m) => m.toLowerCase() === f.toLowerCase()));

  const list = h("ul", { className: "entries" });
  const error = h("div", { className: "error" });
  const add = h("button", { textContent: "Add folder…" });
  const close = h("button", { textContent: "Close", className: "primary" });
  const dialog = h(
    "dialog",
    { className: "file-dialog folders-dialog" },
    h("h2", { textContent: "Component folders" }),
    h("p", { className: "muted", textContent: "LiveSPICE component libraries (.xml) in these folders appear in the library." }),
    h("h3", { textContent: "Yours" }),
    list,
    error,
    h("h3", { textContent: "The server's" }),
    h("ul", { className: "entries" }, ...builtIn.map((f) => h("li", { className: "dir", textContent: f, title: f }))),
    h("div", { className: "buttons" }, add, close),
  );
  document.body.append(dialog);

  const render = () => {
    list.replaceChildren(
      ...mine.map((folder) => {
        const remove = h("button", { textContent: "Remove", title: `Stop loading components from ${folder}` });
        remove.addEventListener("click", () => void save(mine.filter((f) => f !== folder)));
        return h("li", { className: "dir folder-row", title: folder }, h("span", { textContent: folder }), remove);
      }),
    );
    if (mine.length === 0) list.append(h("li", { className: "empty", textContent: "None yet." }));
  };

  const save = async (folders: string[]) => {
    error.textContent = "";
    add.disabled = true;
    try {
      const settings = await client.request<{ componentFolders?: string[] }>("settings.set", { componentFolders: folders });
      mine = settings.componentFolders ?? folders;
      render();
    } catch (e) {
      error.textContent = (e as Error).message.replace(/^settings\.set: /, "");
    } finally {
      add.disabled = false;
    }
  };

  add.addEventListener("click", async () => {
    dialog.close();
    const folder = await chooseFile(client, mine.at(-1) ?? start, { mode: "folder" });
    dialog.showModal();
    if (folder) await save([...mine, folder]);
  });

  return new Promise((resolve) => {
    const finish = () => {
      dialog.close();
      dialog.remove();
      resolve();
    };
    close.addEventListener("click", finish);
    dialog.addEventListener("cancel", (e) => {
      e.preventDefault();
      finish();
    });
    render();
    dialog.showModal();
  });
}
