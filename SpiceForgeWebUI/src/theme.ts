// The colour theme: <html data-theme> picks one of themes.css's token blocks. "system" follows the operating system's
// light or dark setting, and changes with it. The choice is kept in localStorage, so the page can apply it before its
// first paint (index.html), and in the server's per-user client settings, so it survives clearing the browser.

export const Themes = [
  { id: "system", name: "System" },
  { id: "light", name: "Light" },
  { id: "dark", name: "Dark" },
  { id: "dim", name: "Dim" },
  { id: "paper", name: "Paper" },
  { id: "contrast", name: "High contrast" },
] as const;

export type ThemeId = (typeof Themes)[number]["id"];

const StorageKey = "spiceforge.theme";
const dark = window.matchMedia("(prefers-color-scheme: dark)");
let chosen: ThemeId = "system";

export function isTheme(value: unknown): value is ThemeId {
  return Themes.some((t) => t.id === value);
}

/** The theme the user chose (possibly "system"). */
export function currentTheme(): ThemeId {
  return chosen;
}

/** Applies a theme, and remembers it in this browser. */
export function applyTheme(theme: ThemeId) {
  chosen = theme;
  document.documentElement.dataset["theme"] = theme === "system" ? (dark.matches ? "dark" : "light") : theme;
  try {
    localStorage.setItem(StorageKey, theme);
  } catch {
    // Storage may be unavailable; the server's copy still has it.
  }
}

/** Starts from the stored choice, and follows the system while "system" is chosen. */
export function initTheme() {
  let stored: string | null = null;
  try {
    stored = localStorage.getItem(StorageKey);
  } catch {
    // As above.
  }
  applyTheme(isTheme(stored) ? stored : "system");
  dark.addEventListener("change", () => chosen === "system" && applyTheme("system"));
}
