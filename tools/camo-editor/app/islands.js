/**
 * The parts of a gun as its image sees them: UV islands, and which one is under a point (2026-10-17). No DOM.
 *
 * Mycelium: "isolating sections to avoid painting in them (Green Scotch Tape Icon - Temp)". A section is a UV island:
 * triangles that share UV corners, one connected piece of the picture (the stock, the bolt, a ring of the scope).
 * Tape a part, and every texel of its island is taped; Fill a part, and every texel of it is filled.
 *
 * `tris` is the page's list of the edited image's triangles in picture pixels, six numbers each (x0 y0 x1 y1 x2 y2),
 * each triangle already moved by whole tiles to sit over the picture.
 */

/** { of: Int32Array (triangle -> island), islands: [[triangle, ...], ...] }: triangles sharing a UV corner are one. */
export function buildIslands(tris) {
  const n = tris.length / 6;
  const parent = new Int32Array(n).map((_, i) => i);
  const find = (i) => { while (parent[i] !== i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; };
  const seen = new Map();
  for (let t = 0; t < n; t++) {
    for (let k = 0; k < 3; k++) {
      const key = `${Math.round(tris[t * 6 + k * 2] * 32)},${Math.round(tris[t * 6 + k * 2 + 1] * 32)}`;
      const other = seen.get(key);
      if (other === undefined) seen.set(key, t);
      else { const a = find(t), b = find(other); if (a !== b) parent[a] = b; }
    }
  }
  const index = new Map(), islands = [], of = new Int32Array(n);
  for (let t = 0; t < n; t++) {
    const r = find(t);
    if (!index.has(r)) { index.set(r, islands.length); islands.push([]); }
    of[t] = index.get(r);
    islands[of[t]].push(t);
  }
  return { of, islands };
}

/** Every triangle under a point of the picture (mirrored parts share texels, so there can be more than one). */
export function trianglesAt(tris, x, y) {
  const out = [];
  for (let t = 0; t < tris.length / 6; t++) {
    const ax = tris[t * 6], ay = tris[t * 6 + 1], bx = tris[t * 6 + 2], by = tris[t * 6 + 3], cx = tris[t * 6 + 4], cy = tris[t * 6 + 5];
    const d = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
    if (!d) continue;
    const l1 = ((by - cy) * (x - cx) + (cx - bx) * (y - cy)) / d;
    const l2 = ((cy - ay) * (x - cx) + (ax - cx) * (y - cy)) / d;
    if (l1 >= -1e-6 && l2 >= -1e-6 && l1 + l2 <= 1 + 1e-6) out.push(t);
  }
  return out;
}

/**
 * The first triangle a ray meets (Möller-Trumbore), with where on it: { t (triangle), u, v (barycentric of corners
 * 1 and 2), dist }, or null. `pos` holds the triangles' corners in the model's space, nine numbers each.
 */
export function raycast(pos, origin, dir) {
  let best = null;
  const [ox, oy, oz] = origin, [dx, dy, dz] = dir;
  for (let t = 0; t < pos.length / 9; t++) {
    const i = t * 9;
    const e1x = pos[i + 3] - pos[i], e1y = pos[i + 4] - pos[i + 1], e1z = pos[i + 5] - pos[i + 2];
    const e2x = pos[i + 6] - pos[i], e2y = pos[i + 7] - pos[i + 1], e2z = pos[i + 8] - pos[i + 2];
    const px = dy * e2z - dz * e2y, py = dz * e2x - dx * e2z, pz = dx * e2y - dy * e2x;
    const det = e1x * px + e1y * py + e1z * pz;
    if (Math.abs(det) < 1e-12) continue;
    const inv = 1 / det;
    const sx = ox - pos[i], sy = oy - pos[i + 1], sz = oz - pos[i + 2];
    const u = (sx * px + sy * py + sz * pz) * inv;
    if (u < 0 || u > 1) continue;
    const qx = sy * e1z - sz * e1y, qy = sz * e1x - sx * e1z, qz = sx * e1y - sy * e1x;
    const v = (dx * qx + dy * qy + dz * qz) * inv;
    if (v < 0 || u + v > 1) continue;
    const dist = (e2x * qx + e2y * qy + e2z * qz) * inv;
    if (dist > 1e-6 && (!best || dist < best.dist)) best = { t, u, v, dist };
  }
  return best;
}

/** Where on the picture a hit lands (its triangle's corners blended by the hit's barycentrics). */
export function pictureAt(tris, hit) {
  const i = hit.t * 6, w = 1 - hit.u - hit.v;
  return { x: tris[i] * w + tris[i + 2] * hit.u + tris[i + 4] * hit.v, y: tris[i + 1] * w + tris[i + 3] * hit.u + tris[i + 5] * hit.v };
}

/** Draw these triangles filled into a 2D context (picture pixels), a hair wider so neighbours leave no gap. */
export function fillTriangles(g, tris, list) {
  g.beginPath();
  for (const t of list) {
    const i = t * 6;
    g.moveTo(tris[i], tris[i + 1]);
    g.lineTo(tris[i + 2], tris[i + 3]);
    g.lineTo(tris[i + 4], tris[i + 5]);
    g.closePath();
  }
  g.fill();
  g.lineWidth = 1.5;
  g.lineJoin = 'round';
  g.strokeStyle = g.fillStyle;
  g.stroke();
}
