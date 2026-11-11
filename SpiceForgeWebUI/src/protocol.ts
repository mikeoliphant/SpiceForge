// The shapes of DesignDocs/WebSocketApi.md, as TypeScript types.

export type Point = [number, number];

// ---- The document (WebSocketApi.md, The document) ----

export interface ComponentData {
  type: string;
  properties: Record<string, string>;
  body?: string;
  unresolved?: { message: string };
}

export interface SymbolElement {
  id: string;
  kind: "symbol";
  position: Point;
  rotation: number;
  flip: boolean;
  component: ComponentData;
}

export interface WireElement {
  id: string;
  kind: "wire";
  a: Point;
  b: Point;
}

export interface RawElement {
  id: string;
  kind: "raw";
  xml: string;
}

export type Element = SymbolElement | WireElement | RawElement;

export interface SchematicDocument {
  circuit: Record<string, string>;
  elements: Element[];
}

// ---- Symbols (WebSocketApi.md, Symbols) ----

export type Edge = "wire" | "black" | "gray" | "red" | "green" | "blue" | "yellow" | "cyan" | "magenta" | "orange";
export type Alignment = "near" | "center" | "far";

export interface Layout {
  bounds: { min: Point; max: Point };
  terminals: { name: string; at: Point }[];
  lines: { edge: Edge; a: Point; b: Point }[];
  rectangles: { edge: Edge; a: Point; b: Point; fill: boolean }[];
  ellipses: { edge: Edge; a: Point; b: Point; fill: boolean }[];
  curves: { edge: Edge; points: Point[] }[];
  arcs: { edge: Edge; center: Point; radius: number; start: number; end: number; direction: "clockwise" | "counterclockwise" }[];
  texts: { text: string; at: Point; h: Alignment; v: Alignment; size: "small" | "normal" | "large" }[];
  /** Set on a placeholder, for a component the server couldn't lay out. */
  message?: string;
}

// ---- Operations used so far ----

export interface Hello {
  protocol: number;
  server: { name: string; version: string };
  capabilities: Record<string, unknown>;
  folders: { user: string; components: string[] };
}

export interface FileEntry {
  name: string;
  kind: "dir" | "file";
  modified: string;
}

export interface FolderListing {
  path: string;
  parent: string | null;
  entries: FileEntry[];
}

export interface FileRead {
  path: string;
  document: SchematicDocument;
  modified: string;
  messages: { level: string; message: string }[];
}

export interface ApiError {
  code: string;
  message: string;
  details?: unknown;
}

export interface LogEntry {
  time: string;
  level: "error" | "warning" | "info" | "verbose";
  source: string;
  message: string;
}
