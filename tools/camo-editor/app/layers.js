/**
 * The editor's document: layers and what can be done to them, with no DOM.
 *
 * Mycelium, 2026-10-10: *"I can drag in other images, see the layers. Its a
 * Photoviewer until I attempt to edit and then its a ImageEditor. Very
 * Barebones. Nothing Crazy."* So: layers that can be added, moved, scaled,
 * hidden, faded, reordered and removed, and undone. No brushes, no filters.
 *
 * A layer never changes its pixels here. It is a picture and where it sits:
 * `x`, `y` (its top-left, in the document's pixels), one scale `s` (uniform,
 * so a picture keeps its shape), `opacity`, `visible`, `locked`, `blend`. The
 * picture itself is the page's (an <img> or a <canvas>, by `id`), which is why
 * undo is cheap: it is a list of small objects, never pixels.
 *
 * The document's size is its first picture's. A layer may hang off the edge;
 * what is outside the canvas is kept, as Photoshop keeps it.
 *
 * Kept apart from files.html for the reason tabs.js is: the rules are tested
 * without a browser (test/layers.test.mjs), and the page only draws them.
 */

/** Undo remembers this many steps. */
export const UNDO_DEPTH = 200;

/** The fields that make a layer what it is. Everything undo restores. */
const FIELDS = ['id', 'name', 'w', 'h', 'x', 'y', 's', 'opacity', 'visible', 'locked', 'blend'];

export function newDoc(width, height) {
  return { width, height, layers: [], sel: null, seq: 0, undo: [], redo: [], dirty: false };
}

/**
 * A layer on top of the others, selected. `w` and `h` are the picture's own
 * pixels; the rest default to where it was put, whole, visible and normal.
 */
export function addLayer(doc, props) {
  const L = {
    id: `L${++doc.seq}`,
    name: String(props.name ?? `Layer ${doc.seq}`),
    w: props.w, h: props.h,
    x: props.x ?? 0, y: props.y ?? 0, s: props.s ?? 1,
    opacity: props.opacity ?? 1,
    visible: props.visible ?? true,
    locked: props.locked ?? false,
    blend: props.blend ?? 'normal',
  };
  doc.layers.push(L);
  doc.sel = L.id;
  return L;
}

/**
 * Where a picture dropped in goes: centred on the drop, and scaled down to fit
 * the canvas if it is bigger (never up), as Photoshop places one.
 */
export function placeDropped(doc, w, h, at = null) {
  const s = Math.min(1, doc.width / w, doc.height / h);
  const cx = at?.x ?? doc.width / 2;
  const cy = at?.y ?? doc.height / 2;
  return { s, x: Math.round(cx - (w * s) / 2), y: Math.round(cy - (h * s) / 2) };
}

export const layerOf = (doc, id) => doc.layers.find((L) => L.id === id) ?? null;

/** A layer's box in document pixels. */
export const boxOf = (L) => ({ x: L.x, y: L.y, w: L.w * L.s, h: L.h * L.s });

const inside = (L, x, y) => {
  const b = boxOf(L);
  return x >= b.x && y >= b.y && x < b.x + b.w && y < b.y + b.h;
};

/**
 * Which layer a press at (x, y) takes hold of, or null for "none: pan".
 *
 * The selected one first, if the press is on it: what he chose stays chosen
 * even under another. Otherwise the topmost he can see and has not locked. A
 * locked layer is never taken hold of on the canvas, which is what lets the
 * picture he opened sit still under the pictures he dragged onto it, and a
 * press on it pan instead, as on a picture in the viewer.
 */
export function hitTest(doc, x, y) {
  const usable = (L) => L && L.visible && !L.locked && inside(L, x, y);
  const sel = layerOf(doc, doc.sel);
  if (usable(sel)) return sel.id;
  for (let i = doc.layers.length - 1; i >= 0; i--) if (usable(doc.layers[i])) return doc.layers[i].id;
  return null;
}

/**
 * Scale from a corner: the opposite corner stays where it is, and the scale
 * follows the pointer along the box's own diagonal, so the shape is kept.
 * `start` is the layer as it was when the drag began. Returns `{ x, y, s }`.
 */
export function scaleFromCorner(start, corner, px, py) {
  const b = boxOf(start);
  const east = corner === 'ne' || corner === 'se';
  const south = corner === 'sw' || corner === 'se';
  const ax = east ? b.x : b.x + b.w;            // the corner that stays
  const ay = south ? b.y : b.y + b.h;
  const dx = east ? b.w : -b.w;                 // from it to the corner held
  const dy = south ? b.h : -b.h;
  const along = ((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy);
  const min = 1 / Math.min(start.w, start.h);   // never smaller than a pixel
  const max = 30000 / Math.max(start.w, start.h); // never larger than a PSD can hold
  const s = Math.min(max, Math.max(min, start.s * along));
  const w = start.w * s, h = start.h * s;
  return { s, x: east ? ax : ax - w, y: south ? ay : ay - h };
}

/** The document as undo keeps it: every layer's fields, in order, and the selection. */
export function snapshot(doc) {
  return { sel: doc.sel, layers: doc.layers.map((L) => Object.fromEntries(FIELDS.map((k) => [k, L[k]]))) };
}

const same = (a, b) => JSON.stringify(a.layers) === JSON.stringify(b.layers);

/**
 * Close a step: if the document changed since `before`, it can be undone and
 * is unsaved. A press that moved nothing is not a step.
 */
export function commit(doc, before) {
  if (same(before, snapshot(doc))) return false;
  doc.undo.push(before);
  if (doc.undo.length > UNDO_DEPTH) doc.undo.shift();
  doc.redo = [];
  doc.dirty = true;
  return true;
}

function restore(doc, snap) {
  doc.layers = snap.layers.map((L) => ({ ...L }));
  doc.sel = snap.layers.some((L) => L.id === snap.sel) ? snap.sel : null;
}

export function undo(doc) {
  const snap = doc.undo.pop();
  if (!snap) return false;
  doc.redo.push(snapshot(doc));
  restore(doc, snap);
  doc.dirty = true;
  return true;
}

export function redo(doc) {
  const snap = doc.redo.pop();
  if (!snap) return false;
  doc.undo.push(snapshot(doc));
  restore(doc, snap);
  doc.dirty = true;
  return true;
}

/** Each of these is one step: call between snapshot() and commit(). */
export function moveBy(doc, id, dx, dy) {
  const L = layerOf(doc, id);
  if (!L || L.locked) return false;
  L.x += dx;
  L.y += dy;
  return true;
}

export function setLayer(doc, id, fields) {
  const L = layerOf(doc, id);
  if (!L) return false;
  for (const k of ['x', 'y', 's', 'visible', 'locked', 'name', 'blend']) if (k in fields) L[k] = fields[k];
  if ('opacity' in fields) L.opacity = Math.min(1, Math.max(0, Number(fields.opacity) || 0));
  return true;
}

/** Move a layer to `to`, counted from the bottom, as the layers are stored. */
export function reorder(doc, id, to) {
  const from = doc.layers.findIndex((L) => L.id === id);
  if (from < 0) return false;
  const [L] = doc.layers.splice(from, 1);
  doc.layers.splice(Math.max(0, Math.min(doc.layers.length, to)), 0, L);
  return from !== doc.layers.indexOf(L);
}

export function removeLayer(doc, id) {
  const i = doc.layers.findIndex((L) => L.id === id);
  if (i < 0) return false;
  doc.layers.splice(i, 1);
  if (doc.sel === id) doc.sel = doc.layers[Math.min(i, doc.layers.length - 1)]?.id ?? null;
  return true;
}
