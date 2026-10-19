/**
 * Brushes (2026-10-17). Mycelium: "Add additional tools the photo editor is lacking like blending modes, spray paint,
 * texture paint randomizer brushes ...".
 *
 * A dab is drawn into a 2D context at a point, in that context's pixels: the picture's in the flat view, the screen's
 * in the 3D view (view3d.js projects the screen onto the gun). The same brush, so a stroke looks the same either way.
 *
 *   round    a soft or hard disc: `hardness` 0 is all falloff, 1 a hard edge; `flow` its opacity per dab
 *   spray    paint as a can lays it: `density` dots per dab over the disc, each `dot` px, thicker at the middle
 *   scatter  the randomizer: `count` stamps per dab, each a shape (`stamp`: dots, splats, stars, hexes, leaves, streaks,
 *            mixed, or a picture), thrown up to `scatter` x the size away, its size, turn, hue, lightness and opacity
 *            each jittered by its own amount. Seeded (`seed`), so "Reroll" gives a new throw and undo gives it back
 *
 * Which layer it lands on, the eraser (the same dab taken away), and the tape are the page's.
 */

/** A small fast seeded random (mulberry32): the same seed, the same throw. */
export function random(seed) {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/**
 * The dabs along a segment, `spacing` px apart, carrying what is left over to the next segment so a slow stroke and a
 * fast one put their dabs the same distance apart. Returns { points, carry }.
 */
export function along(from, to, spacing, carry = 0) {
  const dx = to.x - from.x, dy = to.y - from.y, len = Math.hypot(dx, dy);
  const points = [];
  let d = spacing - carry;
  while (d <= len) {
    points.push({ x: from.x + (dx * d) / len, y: from.y + (dy * d) / len });
    d += spacing;
  }
  return { points, carry: len - (d - spacing) };
}

// ---- colour --------------------------------------------------------------------------------------
export function hexToRgb(hex) {
  const m = /^#?([0-9a-f]{6})$/i.exec(hex) ?? [0, '000000'];
  const n = parseInt(m[1], 16);
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}
export function rgbToHex([r, g, b]) {
  return `#${[r, g, b].map((v) => Math.max(0, Math.min(255, Math.round(v))).toString(16).padStart(2, '0')).join('')}`;
}
function rgbToHsl([r, g, b]) {
  r /= 255; g /= 255; b /= 255;
  const max = Math.max(r, g, b), min = Math.min(r, g, b), l = (max + min) / 2;
  if (max === min) return [0, 0, l];
  const d = max - min, s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
  const h = max === r ? (g - b) / d + (g < b ? 6 : 0) : max === g ? (b - r) / d + 2 : (r - g) / d + 4;
  return [h * 60, s, l];
}
function hslCss(h, s, l, a) {
  return `hsla(${((h % 360) + 360) % 360}, ${Math.round(s * 100)}%, ${Math.round(Math.max(0, Math.min(1, l)) * 100)}%, ${a})`;
}

// ---- stamps: unit shapes, radius 1 about the origin ------------------------------------------------
export const STAMPS = ['dots', 'splats', 'stars', 'hexes', 'leaves', 'streaks', 'mixed', 'picture'];
const SHAPES = ['dots', 'splats', 'stars', 'hexes', 'leaves', 'streaks'];

function shapePath(kind, rnd) {
  const p = new Path2D();
  if (kind === 'dots') p.arc(0, 0, 1, 0, Math.PI * 2);
  else if (kind === 'stars') {
    for (let i = 0; i < 10; i++) {
      const r = i % 2 ? 0.42 : 1, a = (i / 10) * Math.PI * 2 - Math.PI / 2;
      i ? p.lineTo(Math.cos(a) * r, Math.sin(a) * r) : p.moveTo(Math.cos(a) * r, Math.sin(a) * r);
    }
    p.closePath();
  } else if (kind === 'hexes') {
    for (let i = 0; i < 6; i++) { const a = (i / 6) * Math.PI * 2; i ? p.lineTo(Math.cos(a), Math.sin(a)) : p.moveTo(Math.cos(a), Math.sin(a)); }
    p.closePath();
  } else if (kind === 'leaves') {
    p.moveTo(-1, 0);
    p.quadraticCurveTo(0, -0.75, 1, 0);
    p.quadraticCurveTo(0, 0.75, -1, 0);
  } else if (kind === 'streaks') {
    p.roundRect(-1, -0.18, 2, 0.36, 0.18);
  } else {                                    // splats: a lumpy blob and a few droplets
    const n = 9 + Math.floor(rnd() * 6);
    for (let i = 0; i < n; i++) {
      const a = (i / n) * Math.PI * 2, r = 0.55 + rnd() * 0.45;
      i ? p.lineTo(Math.cos(a) * r, Math.sin(a) * r) : p.moveTo(Math.cos(a) * r, Math.sin(a) * r);
    }
    p.closePath();
    for (let i = 0; i < 3; i++) {
      const a = rnd() * Math.PI * 2, r = 1.05 + rnd() * 0.5;
      p.moveTo(Math.cos(a) * r + 0.12, Math.sin(a) * r);
      p.arc(Math.cos(a) * r, Math.sin(a) * r, 0.06 + rnd() * 0.1, 0, Math.PI * 2);
    }
  }
  return p;
}

/**
 * One dab of brush `b` at (x, y) in `g`'s pixels; `rnd` a random() for this stroke. `b` is
 * { kind, size, hardness, flow, colour, density, dot, stamp, count, scatter, sizeJitter, rotJitter, hueJitter,
 *   valJitter, alphaJitter, picture } (sizes in the context's pixels, fractions 0..1, colour "#rrggbb").
 */
export function dab(g, x, y, b, rnd) {
  const r = Math.max(0.5, b.size / 2);
  const [cr, cg, cb] = hexToRgb(b.colour);
  g.save();
  if (b.kind === 'round') {
    const grad = g.createRadialGradient(x, y, r * Math.min(0.999, b.hardness), x, y, r);
    grad.addColorStop(0, `rgba(${cr},${cg},${cb},${b.flow})`);
    grad.addColorStop(1, `rgba(${cr},${cg},${cb},0)`);
    g.fillStyle = grad;
    g.beginPath();
    g.arc(x, y, r, 0, Math.PI * 2);
    g.fill();
  } else if (b.kind === 'spray') {
    const n = Math.max(1, Math.round(b.density * (r / 10) ** 2 * 3 + b.density * 0.5));
    g.fillStyle = `rgb(${cr},${cg},${cb})`;
    for (let i = 0; i < n; i++) {
      const a = rnd() * Math.PI * 2, d = r * Math.min(1, Math.abs(rnd() + rnd() - 1) * 1.25);   // more at the middle
      g.globalAlpha = b.flow * (0.45 + 0.55 * rnd());
      g.beginPath();
      g.arc(x + Math.cos(a) * d, y + Math.sin(a) * d, Math.max(0.35, (b.dot / 2) * (0.5 + rnd() * 0.5)), 0, Math.PI * 2);
      g.fill();
    }
  } else {                                    // scatter: the randomizer
    const [h, s, l] = rgbToHsl([cr, cg, cb]);
    for (let i = 0; i < Math.max(1, b.count); i++) {
      const a = rnd() * Math.PI * 2, d = rnd() * b.scatter * r * 2;
      const sx = x + Math.cos(a) * d, sy = y + Math.sin(a) * d;
      const size = r * Math.max(0.05, 1 + b.sizeJitter * (rnd() * 2 - 1));
      const turn = b.rotJitter * (rnd() * 2 - 1) * Math.PI;
      const alpha = b.flow * (1 - b.alphaJitter * rnd());
      const kind = b.stamp === 'mixed' ? SHAPES[Math.floor(rnd() * SHAPES.length)] : b.stamp;
      g.save();
      g.translate(sx, sy);
      g.rotate(turn);
      g.globalAlpha = alpha;
      if (kind === 'picture' && b.picture) {
        const k = (size * 2) / Math.max(b.picture.width, b.picture.height);
        if (b.hueJitter) g.filter = `hue-rotate(${Math.round(b.hueJitter * (rnd() * 2 - 1) * 180)}deg)`;
        g.drawImage(b.picture, (-b.picture.width * k) / 2, (-b.picture.height * k) / 2, b.picture.width * k, b.picture.height * k);
      } else {
        g.scale(size, size);
        g.fillStyle = hslCss(h + b.hueJitter * (rnd() * 2 - 1) * 180, s, l + b.valJitter * (rnd() * 2 - 1) * 0.4, 1);
        g.fill(shapePath(kind === 'picture' ? 'dots' : kind, rnd));
      }
      g.restore();
    }
  }
  g.restore();
}

/** How far apart a brush's dabs go along a stroke, in its pixels. */
export function spacingOf(b) {
  const r = Math.max(0.5, b.size / 2);
  if (b.kind === 'round') return Math.max(1, r * 2 * b.spacing);
  if (b.kind === 'spray') return Math.max(1, r * 0.5);
  return Math.max(2, r * 2 * Math.max(0.25, b.spacing * 3));
}
