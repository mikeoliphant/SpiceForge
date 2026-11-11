// The document's edits and their undo history. An edit is applied when it's made (a drag moves elements live), then
// pushed; undo and redo revert and re-apply it.

import { cos90, roundHalfEven, sin90 } from "./geometry";
import type { Point, SchematicDocument } from "./protocol";

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
