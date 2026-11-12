// The document's edits and their undo history. An edit is applied when it's made (a drag moves elements live), then
// pushed; undo and redo revert and re-apply it.

import { cos90, roundHalfEven, sin90 } from "./geometry";
import type { Element, Point, SchematicDocument } from "./protocol";

export interface Edit {
  /** Shown in the Undo and Redo tooltips, as LiveSPICE names them. */
  readonly label: string;
  apply(): void;
  revert(): void;
}

export class EditStack {
  private readonly done: Edit[] = [];
  private readonly undone: Edit[] = [];
  // The edit on top when the document was last saved (null: none), so undoing back to it is clean again.
  private saved: Edit | null = null;

  /** Called after any change to the history. */
  onChange: () => void = () => {};

  /** Records an edit that has already been applied. */
  push(edit: Edit) {
    this.done.push(edit);
    this.undone.length = 0;
    this.onChange();
  }

  undo(): Edit | null {
    const edit = this.done.pop();
    if (!edit) return null;
    edit.revert();
    this.undone.push(edit);
    this.onChange();
    return edit;
  }

  redo(): Edit | null {
    const edit = this.undone.pop();
    if (!edit) return null;
    edit.apply();
    this.done.push(edit);
    this.onChange();
    return edit;
  }

  get canUndo() {
    return this.done.length > 0;
  }

  get canRedo() {
    return this.undone.length > 0;
  }

  get undoLabel() {
    return this.done.at(-1)?.label ?? null;
  }

  get redoLabel() {
    return this.undone.at(-1)?.label ?? null;
  }

  get dirty() {
    return (this.done.at(-1) ?? null) !== this.saved;
  }

  markSaved() {
    this.saved = this.done.at(-1) ?? null;
    this.onChange();
  }

  clear() {
    this.done.length = 0;
    this.undone.length = 0;
    this.saved = null;
    this.onChange();
  }
}

/** Element.Move: symbols by their position, wires by both ends. */
export function moveElements(doc: SchematicDocument, ids: ReadonlySet<string>, [dx, dy]: Point) {
  for (const e of doc.elements) {
    if (!ids.has(e.id)) continue;
    if (e.kind === "symbol") e.position = [e.position[0] + dx, e.position[1] + dy];
    else if (e.kind === "wire") {
      e.a = [e.a[0] + dx, e.a[1] + dy];
      e.b = [e.b[0] + dx, e.b[1] + dy];
    }
  }
}

/** Element.RotateAround: quarter turns (+1 is 90° counter-clockwise on screen) about a point, rounding each result
 * half to even, as LiveSPICE's Point.Round does. A symbol's own rotation turns with it. */
export function rotateElements(doc: SchematicDocument, ids: ReadonlySet<string>, turns: number, about: Point) {
  const c = cos90(turns);
  const s = sin90(turns);
  const turn = ([x, y]: Point): Point => {
    const dx = x - about[0];
    const dy = y - about[1];
    return [roundHalfEven(dx * c + dy * s + about[0]), roundHalfEven(-dx * s + dy * c + about[1])];
  };
  for (const e of doc.elements) {
    if (!ids.has(e.id)) continue;
    if (e.kind === "symbol") {
      e.position = turn(e.position);
      e.rotation += turns;
    } else if (e.kind === "wire") {
      e.a = turn(e.a);
      e.b = turn(e.b);
    }
  }
}

/** Element.FlipOver: mirrors over the horizontal line y = `y`. A symbol's own flip toggles with it. */
export function flipElements(doc: SchematicDocument, ids: ReadonlySet<string>, y: number) {
  const mirror = (py: number) => py + roundHalfEven(2 * (y - py));
  for (const e of doc.elements) {
    if (!ids.has(e.id)) continue;
    if (e.kind === "symbol") {
      e.position = [e.position[0], mirror(e.position[1])];
      e.flip = !e.flip;
    } else if (e.kind === "wire") {
      e.a = [e.a[0], mirror(e.a[1])];
      e.b = [e.b[0], mirror(e.b[1])];
    }
  }
}

/** SchematicEdits.RotateElements. Exact both ways: quarter turns of integer offsets about a grid point stay integral. */
export class RotateEdit implements Edit {
  readonly label = "Rotate";

  constructor(
    private readonly doc: SchematicDocument,
    private readonly ids: ReadonlySet<string>,
    private readonly turns: number,
    private readonly about: Point,
  ) {}

  apply() {
    rotateElements(this.doc, this.ids, this.turns, this.about);
  }

  revert() {
    rotateElements(this.doc, this.ids, -this.turns, this.about);
  }
}

/** SchematicEdits.FlipElements: its own inverse. */
export class FlipEdit implements Edit {
  readonly label = "Flip";

  constructor(
    private readonly doc: SchematicDocument,
    private readonly ids: ReadonlySet<string>,
    private readonly y: number,
  ) {}

  apply() {
    flipElements(this.doc, this.ids, this.y);
  }

  revert() {
    flipElements(this.doc, this.ids, this.y);
  }
}

/**
 * Elements removed and added, as SchematicEdits.AddElements and RemoveElements do it: removed elements remember their
 * place in the document, so undo puts them back where they were (the file's order is kept), and added elements go
 * at the end.
 */
export class ChangeEdit implements Edit {
  private readonly removed: { element: Element; index: number }[];

  constructor(
    readonly label: string,
    private readonly doc: SchematicDocument,
    removed: Element[],
    private readonly added: Element[],
  ) {
    this.removed = removed
      .map((element) => ({ element, index: doc.elements.indexOf(element) }))
      .filter((r) => r.index >= 0)
      .sort((a, b) => a.index - b.index);
  }

  get removedIds(): string[] {
    return this.removed.map((r) => r.element.id);
  }

  apply() {
    const gone = new Set(this.removed.map((r) => r.element));
    this.doc.elements = this.doc.elements.filter((e) => !gone.has(e));
    this.doc.elements.push(...this.added);
  }

  revert() {
    const added = new Set(this.added);
    this.doc.elements = this.doc.elements.filter((e) => !added.has(e));
    for (const r of this.removed) this.doc.elements.splice(r.index, 0, r.element);
  }
}

/** Several edits as one undo step: applied in order, reverted in reverse (SchematicEditor's edit groups). */
export class CompositeEdit implements Edit {
  constructor(
    readonly label: string,
    private readonly edits: Edit[],
  ) {}

  apply() {
    for (const e of this.edits) e.apply();
  }

  revert() {
    for (const e of [...this.edits].reverse()) e.revert();
  }
}

/** A property set on several components (or on the schematic's circuit), each remembering its old value: Edit's
 * PropertyEdit, for a multi-selection. A missing old value means the property wasn't stored. */
export class PropertyEdit implements Edit {
  readonly label: string;
  private readonly old: (string | undefined)[];

  constructor(
    private readonly targets: Record<string, string>[],
    private readonly property: string,
    private readonly value: string,
  ) {
    this.label = `Set ${property}`;
    this.old = targets.map((t) => t[property]);
  }

  apply() {
    for (const t of this.targets) t[this.property] = this.value;
  }

  revert() {
    this.targets.forEach((t, i) => {
      const old = this.old[i];
      if (old === undefined) delete t[this.property];
      else t[this.property] = old;
    });
  }
}

/** Component.UniqueName: a name already used becomes its letters plus the first free number from 1. */
export function uniqueName(used: ReadonlySet<string>, name: string): string {
  if (!used.has(name)) return name;
  const prefix = name.match(/^\D*/)![0];
  for (let i = 1; ; i++) if (!used.has(prefix + i)) return prefix + i;
}

/** Every component name in the document. */
export function componentNames(doc: SchematicDocument): Set<string> {
  const names = new Set<string>();
  for (const e of doc.elements) {
    const name = e.kind === "symbol" ? e.component.properties["Name"] : undefined;
    if (name !== undefined) names.add(name);
  }
  return names;
}

/** Gives each added symbol a name no other component has, as LiveSPICE's ComponentCollection does when they're added. */
export function nameAdded(doc: SchematicDocument, added: Element[]) {
  const used = componentNames(doc);
  for (const e of added) {
    if (e.kind !== "symbol") continue;
    const name = e.component.properties["Name"];
    if (name === undefined) continue;
    const unique = uniqueName(used, name);
    e.component.properties["Name"] = unique;
    used.add(unique);
  }
}

/** A move of some elements by a whole number of grid steps: SchematicEdits.MoveElements. */
export class MoveEdit implements Edit {
  readonly label = "Move";

  constructor(
    private readonly doc: SchematicDocument,
    private readonly ids: ReadonlySet<string>,
    private readonly by: Point,
  ) {}

  apply() {
    moveElements(this.doc, this.ids, this.by);
  }

  revert() {
    moveElements(this.doc, this.ids, [-this.by[0], -this.by[1]]);
  }
}
