// Symbol layouts from the server (catalog.layout), cached by everything a layout depends on: the component's type,
// properties and body (WebSocketApi.md, Layout).

import type { ApiClient } from "./client";
import type { ComponentData, Layout } from "./protocol";

export function layoutKey(c: ComponentData): string {
  return JSON.stringify([c.type, c.properties, c.body ?? null]);
}

export class LayoutCache {
  private readonly layouts = new Map<string, Layout>();

  constructor(private readonly client: ApiClient) {}

  get(component: ComponentData): Layout | undefined {
    return this.layouts.get(layoutKey(component));
  }

  /** Fetches, in one request, the layouts of whichever of these components aren't cached yet. */
  async fetch(components: ComponentData[]): Promise<void> {
    const missing = new Map<string, ComponentData>();
    for (const c of components) {
      const key = layoutKey(c);
      if (!this.layouts.has(key) && !missing.has(key)) missing.set(key, c);
    }
    if (missing.size === 0) return;
    const wanted = [...missing.values()].map((c) => ({ type: c.type, properties: c.properties, body: c.body }));
    const result = await this.client.request<{ layouts: Layout[] }>("catalog.layout", { components: wanted });
    [...missing.keys()].forEach((key, i) => this.layouts.set(key, result.layouts[i]!));
  }
}
