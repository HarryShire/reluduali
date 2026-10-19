/**
 * The gun in 3D, wearing the edit as it is made, and painted on directly (2026-10-17). Mycelium: "Make sure we have a 3D
 * Version of the Model. I wish photoshop still had this feature but it doesnt so we'll just build it here." Then: "the
 * Inbuilt Console Viewer should be paintable itself and render at a pretty decent quality, rotate, zoom, pan, etc".
 *
 * WebGL 2, no library. A model's triangles as models.py sends them (by image: position, normal, uv, three corners a
 * triangle), each part with its own colour image and, when the dump has them, its material's normal, specular (gloss
 * in alpha) and occlusion maps. The edited image's parts draw from a canvas the page composites on every change.
 *
 * LOOK: linear light (sRGB in, ACES out), a key, a fill and a rim light that ride with the camera, a studio
 * environment for reflections (bright top, a softbox, a dark floor) weighted by Fresnel and gloss, normal maps from
 * the screen-space derivatives (no tangents in the file), and occlusion on the ambient. Brighter than the game: it is
 * for seeing the paint.
 *
 * PAINTING ON IT (`project`): the page draws the brush in SCREEN space (any brush: round, spray, scattered stamps), and
 * this projects it onto the image: every triangle that wears the image is drawn into a texture-sized target at its UV,
 * and each texel looks up where it lands on screen, whether it is the surface the camera sees there (a depth map of
 * the scene, from the camera) and how squarely it faces the camera; the brush's colour there, times that, is the
 * texel's paint. The edges are drawn again as lines so a texel half inside a triangle is not left out (no seams).
 * Read back, the page lays it into a layer like any 2D stroke. Projection painting, as Blender's.
 *
 * The game's UVs run down the picture, as its rows do, so V is used as it is (check/uv-orientation.py).
 * Controls (the page decides which button does what): orbit, pan, zoom (`orbitBy`, `panBy`, `zoomBy`), `frame`,
 * `viewFrom` (front, back, left, right, top). Looks: `lighting` (studio, daylight, game), `background`, `wireframe`.
 * `setHighlight` tints the part under the pointer (the page's Fill and Tape tools say what they will take).
 * MIRROR (`project(..., { mirror: true })`): the brush also lands on the other side of the gun, reflected across the
 * left-right plane the parts that wear the image are most nearly symmetric about (`symmetryPlane`: y = 0 for the guns).
 */

const FOVY = 35 * Math.PI / 180;

const VS = `#version 300 es
in vec3 aPos; in vec3 aNrm; in vec2 aUv;
uniform mat4 uMvp;
out vec3 vNrm; out vec2 vUv; out vec3 vPos;
void main() { vNrm = aNrm; vUv = aUv; vPos = aPos; gl_Position = uMvp * vec4(aPos, 1.0); }`;

const FS = `#version 300 es
precision highp float;
in vec3 vNrm; in vec2 vUv; in vec3 vPos;
uniform sampler2D uCol, uNrm, uSpc, uAo, uTape, uHi;
uniform float uHasCol, uHasNrm, uHasSpc, uHasAo, uTapeOn, uHiOn, uDim, uExposure;
uniform vec3 uEye, uKey, uFill, uRim, uKeyC, uFillC, uRimC, uSky, uGround;
out vec4 o;
vec3 lin(vec3 c) { return pow(c, vec3(2.2)); }
vec3 aces(vec3 x) { return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0); }
vec3 studio(vec3 r) {                       // Z up: a bright ceiling, a softbox over the key side, a dark floor
  float up = r.z;
  vec3 c = mix(uGround, uSky, smoothstep(-0.15, 0.6, up));
  float box = smoothstep(0.86, 0.97, dot(normalize(r), normalize(uKey)));
  return c + vec3(2.2) * box + vec3(0.25) * smoothstep(-0.05, 0.02, up) * (1.0 - smoothstep(0.02, 0.12, up));
}
void main() {
  vec3 n = normalize(vNrm);
  vec3 v = normalize(uEye - vPos);
  if (dot(n, v) < 0.0) n = -n;              // both sides: a view model is not closed everywhere
  if (uHasNrm > 0.5) {
    vec3 dp1 = dFdx(vPos), dp2 = dFdy(vPos);
    vec2 du1 = dFdx(vUv), du2 = dFdy(vUv);
    vec3 p2 = cross(dp2, n), p1 = cross(n, dp1);
    vec3 T = p2 * du1.x + p1 * du2.x, B = p2 * du1.y + p1 * du2.y;
    float k = inversesqrt(max(max(dot(T, T), dot(B, B)), 1e-20));
    vec2 xy = texture(uNrm, vUv).rg * 2.0 - 1.0;
    n = normalize(mat3(T * k, B * k, n) * vec3(xy, sqrt(max(0.0, 1.0 - dot(xy, xy)))));
  }
  vec3 base = uHasCol > 0.5 ? lin(texture(uCol, vUv).rgb) : vec3(0.18);
  vec4 sg = uHasSpc > 0.5 ? texture(uSpc, vUv) : vec4(0.2, 0.2, 0.2, 0.45);
  vec3 spc = lin(sg.rgb);
  float gloss = sg.a;
  float ao = uHasAo > 0.5 ? texture(uAo, vUv).r : 1.0;
  float p = exp2(gloss * 11.0) + 2.0;
  vec3 c = vec3(0.0);
  vec3 L[3] = vec3[3](uKey, uFill, uRim);
  vec3 C[3] = vec3[3](uKeyC, uFillC, uRimC);
  for (int i = 0; i < 3; i++) {
    vec3 l = normalize(L[i]);
    float ndl = max(dot(n, l), 0.0);
    vec3 h = normalize(l + v);
    c += C[i] * ndl * (base + spc * (p + 8.0) / 8.0 * pow(max(dot(n, h), 0.0), p));
  }
  float ndv = max(dot(n, v), 0.0);
  vec3 F = spc + (max(vec3(gloss), spc) - spc) * pow(1.0 - ndv, 5.0);
  vec3 amb = mix(uGround * 1.2, uSky * 0.42, 0.5 + 0.5 * n.z);
  c += ao * (base * amb + studio(reflect(-v, n)) * F * mix(0.2, 1.0, gloss));
  vec3 outc = pow(aces(c * uExposure), vec3(1.0 / 2.2));
  if (uTapeOn > 0.5) outc = mix(outc, vec3(0.36, 0.85, 0.32), 0.55 * texture(uTape, vUv).a);
  if (uHiOn > 0.5) outc = mix(outc, vec3(1.0, 0.86, 0.35), 0.45 * texture(uHi, vUv).a);
  if (uDim > 0.5) outc = mix(outc, vec3(0.22), 0.5);
  o = vec4(outc, 1.0);
}`;

// The paint pass: each texel of the edited image, from the triangles that wear it, gets the brush where it lands.
const PVS = `#version 300 es
in vec3 aPos; in vec3 aNrm; in vec2 aUvT;
uniform mat4 uMvp; uniform float uMirror, uMirrorY;
out vec4 vClip; out vec3 vPos; out vec3 vNrm;
void main() {
  vec3 p = aPos, n = aNrm;
  if (uMirror > 0.5) { p.y = 2.0 * uMirrorY - p.y; n.y = -n.y; }    // the texel's twin on the other side
  vClip = uMvp * vec4(p, 1.0); vPos = p; vNrm = n;
  gl_Position = vec4(aUvT.x * 2.0 - 1.0, aUvT.y * 2.0 - 1.0, 0.0, 1.0);   // read back, row 0 is the picture's top
}`;
const PFS = `#version 300 es
precision highp float;
in vec4 vClip; in vec3 vPos; in vec3 vNrm;
uniform highp sampler2D uDepth; uniform sampler2D uBrush;
uniform vec3 uEye; uniform float uBias;
out vec4 o;
void main() {
  if (vClip.w <= 0.0) discard;
  vec3 ndc = vClip.xyz / vClip.w;
  vec2 s = ndc.xy * 0.5 + 0.5;
  if (s.x < 0.0 || s.y < 0.0 || s.x > 1.0 || s.y > 1.0) discard;
  if (ndc.z * 0.5 + 0.5 > texture(uDepth, s).r + uBias) discard;          // behind what the camera sees
  float f = abs(dot(normalize(vNrm), normalize(uEye - vPos)));
  vec4 b = texture(uBrush, vec2(s.x, 1.0 - s.y));
  o = vec4(b.rgb, b.a * smoothstep(0.04, 0.22, f));                       // fades off grazing faces
}`;
const DVS = `#version 300 es
in vec3 aPos; uniform mat4 uMvp;
void main() { gl_Position = uMvp * vec4(aPos, 1.0); }`;
const DFS = `#version 300 es
precision mediump float; uniform vec4 uColor; out vec4 o; void main() { o = uColor; }`;

// the looks: light colours (key, fill, rim), the environment's sky and ground, the background, the exposure
const LOOKS = {
  studio: { key: [2.6, 2.6, 2.6], fill: [0.7, 0.75, 0.9], rim: [1.4, 1.4, 1.4], sky: [0.75, 0.74, 0.8], ground: [0.05, 0.045, 0.06] },
  daylight: { key: [3.0, 2.8, 2.4], fill: [0.45, 0.6, 0.9], rim: [0.8, 0.85, 1.0], sky: [0.55, 0.7, 1.0], ground: [0.18, 0.15, 0.11] },
  game: { key: [1.7, 1.6, 1.45], fill: [0.35, 0.38, 0.45], rim: [0.5, 0.5, 0.55], sky: [0.38, 0.38, 0.42], ground: [0.06, 0.055, 0.05] },
};
const BACKGROUNDS = { dark: [0.075, 0.055, 0.08], grey: [0.32, 0.32, 0.34], light: [0.82, 0.82, 0.84] };

// ---- 4 x 4 matrices, column-major as WebGL takes them ---------------------------------------------
function mul(a, b) {
  const o = new Float32Array(16);
  for (let c = 0; c < 4; c++) for (let r = 0; r < 4; r++) {
    let s = 0;
    for (let k = 0; k < 4; k++) s += a[k * 4 + r] * b[c * 4 + k];
    o[c * 4 + r] = s;
  }
  return o;
}
function invert(m) {
  const a = m, o = new Float32Array(16);
  const b00 = a[0] * a[5] - a[1] * a[4], b01 = a[0] * a[6] - a[2] * a[4], b02 = a[0] * a[7] - a[3] * a[4];
  const b03 = a[1] * a[6] - a[2] * a[5], b04 = a[1] * a[7] - a[3] * a[5], b05 = a[2] * a[7] - a[3] * a[6];
  const b06 = a[8] * a[13] - a[9] * a[12], b07 = a[8] * a[14] - a[10] * a[12], b08 = a[8] * a[15] - a[11] * a[12];
  const b09 = a[9] * a[14] - a[10] * a[13], b10 = a[9] * a[15] - a[11] * a[13], b11 = a[10] * a[15] - a[11] * a[14];
  const det = b00 * b11 - b01 * b10 + b02 * b09 + b03 * b08 - b04 * b07 + b05 * b06;
  if (!det) return null;
  const d = 1 / det;
  o[0] = (a[5] * b11 - a[6] * b10 + a[7] * b09) * d; o[1] = (a[2] * b10 - a[1] * b11 - a[3] * b09) * d;
  o[2] = (a[13] * b05 - a[14] * b04 + a[15] * b03) * d; o[3] = (a[10] * b04 - a[9] * b05 - a[11] * b03) * d;
  o[4] = (a[6] * b08 - a[4] * b11 - a[7] * b07) * d; o[5] = (a[0] * b11 - a[2] * b08 + a[3] * b07) * d;
  o[6] = (a[14] * b02 - a[12] * b05 - a[15] * b01) * d; o[7] = (a[8] * b05 - a[10] * b02 + a[11] * b01) * d;
  o[8] = (a[4] * b10 - a[5] * b08 + a[7] * b06) * d; o[9] = (a[1] * b08 - a[0] * b10 - a[3] * b06) * d;
  o[10] = (a[12] * b04 - a[13] * b02 + a[15] * b00) * d; o[11] = (a[9] * b02 - a[8] * b04 - a[11] * b00) * d;
  o[12] = (a[5] * b07 - a[4] * b09 - a[6] * b06) * d; o[13] = (a[0] * b09 - a[1] * b07 + a[2] * b06) * d;
  o[14] = (a[13] * b01 - a[12] * b03 - a[14] * b00) * d; o[15] = (a[8] * b03 - a[9] * b01 + a[10] * b00) * d;
  return o;
}
function perspective(fovy, aspect, near, far) {
  const f = 1 / Math.tan(fovy / 2), o = new Float32Array(16);
  o[0] = f / aspect; o[5] = f; o[10] = (far + near) / (near - far); o[11] = -1; o[14] = (2 * far * near) / (near - far);
  return o;
}
const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
const norm = (a) => { const l = Math.hypot(a[0], a[1], a[2]) || 1; return [a[0] / l, a[1] / l, a[2] / l]; };
const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
function lookAt(eye, at, up) {
  const z = norm(sub(eye, at)), x = norm(cross(up, z)), y = cross(z, x);
  return new Float32Array([x[0], y[0], z[0], 0, x[1], y[1], z[1], 0, x[2], y[2], z[2], 0, -dot(x, eye), -dot(y, eye), -dot(z, eye), 1]);
}

/**
 * Of these planes y = c, the one the image's parts are most nearly mirrored about: for a sample of corners, how many
 * have a corner at their reflection (within 0.05 in). The DSR-50's view model: 69% about y = 0, 4.5% about the middle
 * of its box (the bolt handle is on one side), so the box's middle is not the answer.
 */
function symmetryPlane(mesh, image, candidates) {
  const pts = [];
  for (const P of mesh.parts) if (P.image.toLowerCase() === image) for (let i = 0; i < P.position.length; i += 3) pts.push(P.position[i], P.position[i + 1], P.position[i + 2]);
  if (!pts.length) return candidates[0];
  const cell = 0.05, key = (x, y, z) => `${Math.round(x / cell)},${Math.round(y / cell)},${Math.round(z / cell)}`;
  const grid = new Set();
  for (let i = 0; i < pts.length; i += 3) grid.add(key(pts[i], pts[i + 1], pts[i + 2]));
  const n = pts.length / 3, step = Math.max(1, Math.floor(n / 600));
  let best = candidates[0], bestScore = -1;
  for (const c of candidates) {
    let hit = 0, tried = 0;
    for (let k = 0; k < n; k += step) {
      tried++;
      const x = pts[k * 3], y = 2 * c - pts[k * 3 + 1], z = pts[k * 3 + 2];
      let found = false;
      for (let dx = -1; dx <= 1 && !found; dx++) for (let dy = -1; dy <= 1 && !found; dy++) for (let dz = -1; dz <= 1 && !found; dz++) {
        if (grid.has(key(x + dx * cell, y + dy * cell, z + dz * cell))) found = true;
      }
      if (found) hit++;
    }
    if (hit / tried > bestScore) { bestScore = hit / tried; best = c; }
  }
  return best;
}

function program(gl, vs, fs) {
  const p = gl.createProgram();
  for (const [type, src] of [[gl.VERTEX_SHADER, vs], [gl.FRAGMENT_SHADER, fs]]) {
    const s = gl.createShader(type);
    gl.shaderSource(s, src);
    gl.compileShader(s);
    if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s));
    gl.attachShader(p, s);
  }
  // one vertex layout for every program, so one vertex array per part serves them all
  ['aPos', 'aNrm', 'aUv', 'aUvT'].forEach((n, i) => gl.bindAttribLocation(p, i, n));
  gl.linkProgram(p);
  if (!gl.getProgramParameter(p, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(p));
  const loc = (n) => gl.getUniformLocation(p, n);
  return { p, loc };
}

export function createView3d(canvas) {
  const gl = canvas.getContext('webgl2', { antialias: true, preserveDrawingBuffer: true });
  if (!gl) throw new Error('this browser has no WebGL 2');
  const shade = program(gl, VS, FS);
  const paint = program(gl, PVS, PFS);
  const depthOnly = program(gl, DVS, DFS);
  const U = Object.fromEntries(['uMvp', 'uCol', 'uNrm', 'uSpc', 'uAo', 'uTape', 'uHi', 'uHasCol', 'uHasNrm', 'uHasSpc', 'uHasAo',
    'uTapeOn', 'uHiOn', 'uDim', 'uExposure', 'uEye', 'uKey', 'uFill', 'uRim', 'uKeyC', 'uFillC', 'uRimC', 'uSky', 'uGround'].map((n) => [n, shade.loc(n)]));
  let look = LOOKS.studio, bg = BACKGROUNDS.dark, wire = false, hi = null, hiOn = false;
  const aniso = gl.getExtension('EXT_texture_filter_anisotropic');

  let parts = [];              // { image, vao, count, edgeCount, maps: {col, normal, spec, ao}, mine }
  let owned = [];              // every buffer and texture of the mesh, freed with it
  let live = null, liveImage = '', tape = null, showTape = true;
  let box = null;
  const cam = { yaw: -0.6, pitch: 0.25, dist: 50, target: [0, 0, 0] };
  let exposure = 1.0;
  let queued = false;
  let depth = null;            // { fb, tex, w, h, key }
  let paintTarget = null;      // { fb, tex, w, h }
  let last = null;             // the matrices of the last frame drawn: { mvp, eye, w, h }
  let forced = null;           // a size of our own for a snapshot: { w, h }
  let mirrorY = 0;             // the plane (y = mirrorY) the gun is most nearly symmetric about

  function texture(src, w, h) {
    const t = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, t);
    gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
    if (src instanceof Uint8Array) gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, w, h, 0, gl.RGBA, gl.UNSIGNED_BYTE, src);
    else gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, src);
    gl.generateMipmap(gl.TEXTURE_2D);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.REPEAT);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.REPEAT);
    if (aniso) gl.texParameterf(gl.TEXTURE_2D, aniso.TEXTURE_MAX_ANISOTROPY_EXT, 8);
    return t;
  }

  /**
   * A model from models.py. `images` maps a lower-case image name or a map's file path to { rgba, w, h }: every image
   * the parts draw that the page could read (the edited one comes from updateLive).
   */
  function setMesh(mesh, images = new Map(), edited = '') {
    for (const p of parts) gl.deleteVertexArray(p.vao);
    for (const o of owned) (o instanceof WebGLTexture ? gl.deleteTexture(o) : gl.deleteBuffer(o));
    parts = [];
    owned = [];
    liveImage = String(edited || '').toLowerCase();
    const lo = [Infinity, Infinity, Infinity], hi = [-Infinity, -Infinity, -Infinity];
    const mlo = [Infinity, Infinity, Infinity], mhi = [-Infinity, -Infinity, -Infinity];   // the edited parts only
    const cache = new Map();
    const tex = (key) => {
      if (!key) return null;
      if (cache.has(key)) return cache.get(key);
      const img = images.get(key);
      const t = img ? texture(img.rgba, img.w, img.h) : null;
      if (t) owned.push(t);
      cache.set(key, t);
      return t;
    };
    for (const P of mesh.parts) {
      const n = P.position.length / 3;
      const data = new Float32Array(n * 10);           // position 3, normal 3, uv 2, uv wrapped per triangle 2
      const isMine = P.image.toLowerCase() === liveImage;
      for (let t = 0; t < n / 3; t++) {
        const cu = (P.uv[t * 6] + P.uv[t * 6 + 2] + P.uv[t * 6 + 4]) / 3, cv = (P.uv[t * 6 + 1] + P.uv[t * 6 + 3] + P.uv[t * 6 + 5]) / 3;
        const du = Math.floor(cu), dv = Math.floor(cv);
        for (let k = 0; k < 3; k++) {
          const i = t * 3 + k;
          for (let c = 0; c < 3; c++) {
            const x = P.position[i * 3 + c];
            data[i * 10 + c] = x;
            if (x < lo[c]) lo[c] = x;
            if (x > hi[c]) hi[c] = x;
            if (isMine && x < mlo[c]) mlo[c] = x;
            if (isMine && x > mhi[c]) mhi[c] = x;
            data[i * 10 + 3 + c] = P.normal[i * 3 + c];
          }
          data[i * 10 + 6] = P.uv[i * 2];
          data[i * 10 + 7] = P.uv[i * 2 + 1];
          data[i * 10 + 8] = P.uv[i * 2] - du;
          data[i * 10 + 9] = P.uv[i * 2 + 1] - dv;
        }
      }
      const buf = gl.createBuffer();
      gl.bindBuffer(gl.ARRAY_BUFFER, buf);
      gl.bufferData(gl.ARRAY_BUFFER, data, gl.STATIC_DRAW);
      owned.push(buf);
      const vao = gl.createVertexArray();
      gl.bindVertexArray(vao);
      [[0, 3, 0], [1, 3, 12], [2, 2, 24], [3, 2, 32]].forEach(([loc, size, off]) => {
        gl.enableVertexAttribArray(loc);
        gl.vertexAttribPointer(loc, size, gl.FLOAT, false, 40, off);
      });
      const edges = new Uint32Array(n * 2);
      for (let t = 0; t < n / 3; t++) for (let k = 0; k < 3; k++) { edges[t * 6 + k * 2] = t * 3 + k; edges[t * 6 + k * 2 + 1] = t * 3 + (k + 1) % 3; }
      const ebuf = gl.createBuffer();
      gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, ebuf);
      gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, edges, gl.STATIC_DRAW);
      owned.push(ebuf);
      gl.bindVertexArray(null);
      const image = P.image.toLowerCase();
      const maps = { col: tex(image), normal: tex(P.maps?.normal), spec: tex(P.maps?.spec), ao: tex(P.maps?.ao) };
      parts.push({ image, vao, count: n, edgeCount: edges.length, maps, mine: image === liveImage });
    }
    // framed on the parts that wear the edited image (a view model also carries far-off bits: its emblem, its tag)
    const [a, b] = mlo[0] <= mhi[0] ? [mlo, mhi] : [lo, hi];
    box = { centre: [0, 1, 2].map((k) => (a[k] + b[k]) / 2), radius: Math.max(1, Math.hypot(b[0] - a[0], b[1] - a[1], b[2] - a[2]) / 2) };
    mirrorY = symmetryPlane(mesh, liveImage, [0, box.centre[1]]);
    depth = null;
    frame();
  }

  /** The composite of the edited image, again (a canvas the image's size). */
  function updateLive(source) {
    if (!live) live = texture(source);
    else {
      gl.bindTexture(gl.TEXTURE_2D, live);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, source);
      gl.generateMipmap(gl.TEXTURE_2D);
    }
    draw();
  }

  /** The tape (a canvas the image's size; its alpha is where it is), drawn green on the gun. */
  function setTape(source) {
    if (!source) { tape = null; draw(); return; }
    if (!tape) tape = texture(source);
    else {
      gl.bindTexture(gl.TEXTURE_2D, tape);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, source);
      gl.generateMipmap(gl.TEXTURE_2D);
    }
    draw();
  }

  /** A canvas the image's size whose alpha is the part to tint (the page's hover), or null. */
  function setHighlight(source) {
    hiOn = !!source;
    if (source) {
      if (!hi) hi = texture(source);
      else {
        gl.bindTexture(gl.TEXTURE_2D, hi);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, source);
        gl.generateMipmap(gl.TEXTURE_2D);
      }
    }
    draw();
  }

  /** The camera to a named side: front (down the barrel), back, left, right, top. */
  function viewFrom(side) {
    const at = { front: [0, 0.05], back: [Math.PI, 0.05], left: [Math.PI / 2, 0.05], right: [-Math.PI / 2, 0.05], top: [-Math.PI / 2, 1.45] }[side];
    if (!at) return;
    frame();
    [cam.yaw, cam.pitch] = at;
    draw();
  }

  function frame() {
    if (!box) return;
    cam.target = box.centre.slice();
    const aspect = forced ? forced.w / forced.h : Math.max(0.2, canvas.clientWidth / Math.max(1, canvas.clientHeight));
    const half = Math.min(FOVY / 2, Math.atan(Math.tan(FOVY / 2) * aspect));
    cam.dist = box.radius / Math.sin(half) * 0.75;     // a gun is long and thin: its bounding sphere is mostly air
    draw();
  }

  function eye() {
    const cp = Math.cos(cam.pitch);
    return [cam.target[0] + cam.dist * cp * Math.cos(cam.yaw), cam.target[1] + cam.dist * cp * Math.sin(cam.yaw), cam.target[2] + cam.dist * Math.sin(cam.pitch)];
  }

  function matrices() {
    const dpr = window.devicePixelRatio || 1;
    const w = forced ? forced.w : Math.max(1, Math.round(canvas.clientWidth * dpr));
    const h = forced ? forced.h : Math.max(1, Math.round(canvas.clientHeight * dpr));
    const e = eye();
    const r = box ? box.radius : 10;
    const proj = perspective(FOVY, w / h, Math.max(0.05, cam.dist - r * 2) * 0.5, cam.dist + r * 4);
    return { mvp: mul(proj, lookAt(e, cam.target, [0, 0, 1])), eye: e, w, h };
  }

  function draw() {
    if (queued) return;
    queued = true;
    requestAnimationFrame(() => { queued = false; render(); });
  }

  function render() {
    const m = matrices();
    if (canvas.width !== m.w || canvas.height !== m.h) { canvas.width = m.w; canvas.height = m.h; }
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    gl.viewport(0, 0, m.w, m.h);
    gl.clearColor(bg[0], bg[1], bg[2], 1);
    gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
    last = m;
    if (!parts.length || !box) return;
    gl.enable(gl.DEPTH_TEST);
    gl.disable(gl.CULL_FACE);
    gl.useProgram(shade.p);
    gl.uniformMatrix4fv(U.uMvp, false, m.mvp);
    gl.uniform3fv(U.uEye, m.eye);
    const fwd = norm(sub(cam.target, m.eye)), right = norm(cross(fwd, [0, 0, 1])), up = cross(right, fwd);
    const comb = (a, b, c) => norm([0, 1, 2].map((i) => -fwd[i] * a + right[i] * b + up[i] * c));
    gl.uniform3fv(U.uKey, comb(0.8, -0.7, 0.9));       // up and to the left of the camera
    gl.uniform3fv(U.uFill, comb(0.6, 0.9, -0.1));      // low, from the right
    gl.uniform3fv(U.uRim, comb(-1.0, 0.3, 0.6));       // from behind
    gl.uniform1f(U.uExposure, exposure);
    gl.uniform3fv(U.uKeyC, look.key); gl.uniform3fv(U.uFillC, look.fill); gl.uniform3fv(U.uRimC, look.rim);
    gl.uniform3fv(U.uSky, look.sky); gl.uniform3fv(U.uGround, look.ground);
    const bind = (unit, t, has) => {
      gl.activeTexture(gl.TEXTURE0 + unit);
      gl.bindTexture(gl.TEXTURE_2D, t);
      gl.uniform1f(has, t ? 1 : 0);
    };
    gl.uniform1i(U.uCol, 0); gl.uniform1i(U.uNrm, 1); gl.uniform1i(U.uSpc, 2); gl.uniform1i(U.uAo, 3); gl.uniform1i(U.uTape, 4); gl.uniform1i(U.uHi, 5);
    if (wire) { gl.enable(gl.POLYGON_OFFSET_FILL); gl.polygonOffset(1, 1); }
    for (const p of parts) {
      bind(0, p.mine && live ? live : p.maps.col, U.uHasCol);
      bind(1, p.maps.normal, U.uHasNrm);
      bind(2, p.maps.spec, U.uHasSpc);
      bind(3, p.maps.ao, U.uHasAo);
      gl.activeTexture(gl.TEXTURE4);
      gl.bindTexture(gl.TEXTURE_2D, p.mine && tape ? tape : null);
      gl.uniform1f(U.uTapeOn, p.mine && tape && showTape ? 1 : 0);
      gl.activeTexture(gl.TEXTURE5);
      gl.bindTexture(gl.TEXTURE_2D, p.mine && hi ? hi : null);
      gl.uniform1f(U.uHiOn, p.mine && hi && hiOn ? 1 : 0);
      gl.uniform1f(U.uDim, dimOthers && liveImage && !p.mine ? 1 : 0);
      gl.bindVertexArray(p.vao);
      gl.drawArrays(gl.TRIANGLES, 0, p.count);
    }
    gl.disable(gl.POLYGON_OFFSET_FILL);
    if (wire) {                                        // the triangles' edges over the shading
      gl.useProgram(depthOnly.p);
      gl.uniformMatrix4fv(depthOnly.loc('uMvp'), false, m.mvp);
      gl.enable(gl.BLEND);
      gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);
      for (const p of parts) {
        gl.uniform4fv(depthOnly.loc('uColor'), p.mine ? [0.37, 0.87, 0.63, 0.55] : [0.6, 0.6, 0.65, 0.25]);
        gl.bindVertexArray(p.vao);
        gl.drawElements(gl.LINES, p.edgeCount, gl.UNSIGNED_INT, 0);
      }
      gl.disable(gl.BLEND);
    }
    gl.bindVertexArray(null);
  }
  let dimOthers = false;

  // ---- painting: the brush, drawn in screen space by the page, onto the image's texels ----------------

  function ensureDepth(m) {
    const key = `${m.w}x${m.h}:${Array.from(m.mvp).join(',')}`;
    if (depth && depth.key === key) return;
    if (!depth || depth.w !== m.w || depth.h !== m.h) {
      if (depth) { gl.deleteFramebuffer(depth.fb); gl.deleteTexture(depth.tex); }
      const tex = gl.createTexture();
      gl.bindTexture(gl.TEXTURE_2D, tex);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.DEPTH_COMPONENT24, m.w, m.h, 0, gl.DEPTH_COMPONENT, gl.UNSIGNED_INT, null);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
      const fb = gl.createFramebuffer();
      gl.bindFramebuffer(gl.FRAMEBUFFER, fb);
      gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.DEPTH_ATTACHMENT, gl.TEXTURE_2D, tex, 0);
      gl.drawBuffers([gl.NONE]);
      depth = { fb, tex, w: m.w, h: m.h, key: '' };
    }
    gl.bindFramebuffer(gl.FRAMEBUFFER, depth.fb);
    gl.viewport(0, 0, m.w, m.h);
    gl.clear(gl.DEPTH_BUFFER_BIT);
    gl.enable(gl.DEPTH_TEST);
    gl.useProgram(depthOnly.p);
    gl.uniformMatrix4fv(depthOnly.loc('uMvp'), false, m.mvp);
    for (const p of parts) { gl.bindVertexArray(p.vao); gl.drawArrays(gl.TRIANGLES, 0, p.count); }
    gl.bindVertexArray(null);
    depth.key = key;
  }

  /**
   * The brush (a canvas the size of this view's drawing buffer, drawn in its pixels, top row first) projected onto the
   * edited image: RGBA, w x h, row 0 the picture's top. Alpha is the brush's times "the camera sees this texel here".
   */
  function project(brush, w, h, { mirror = false } = {}) {
    if (!parts.some((p) => p.mine) || !box) return null;
    const m = matrices();
    ensureDepth(m);
    if (!paintTarget || paintTarget.w !== w || paintTarget.h !== h) {
      if (paintTarget) { gl.deleteFramebuffer(paintTarget.fb); gl.deleteTexture(paintTarget.tex); }
      const tex = gl.createTexture();
      gl.bindTexture(gl.TEXTURE_2D, tex);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, w, h, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
      const fb = gl.createFramebuffer();
      gl.bindFramebuffer(gl.FRAMEBUFFER, fb);
      gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, tex, 0);
      paintTarget = { fb, tex, w, h, brush: gl.createTexture(), out: new Uint8Array(w * h * 4) };
    }
    gl.bindTexture(gl.TEXTURE_2D, paintTarget.brush);
    gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, brush);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.bindFramebuffer(gl.FRAMEBUFFER, paintTarget.fb);
    gl.viewport(0, 0, w, h);
    gl.clearColor(0, 0, 0, 0);
    gl.clear(gl.COLOR_BUFFER_BIT);
    gl.disable(gl.DEPTH_TEST);
    gl.enable(gl.BLEND);
    gl.blendEquation(gl.MAX);                          // where two texels' twins both land, the stronger paint wins
    gl.useProgram(paint.p);
    gl.uniformMatrix4fv(paint.loc('uMvp'), false, m.mvp);
    gl.uniform3fv(paint.loc('uEye'), m.eye);
    gl.uniform1f(paint.loc('uBias'), 0.0008);
    gl.activeTexture(gl.TEXTURE0); gl.bindTexture(gl.TEXTURE_2D, depth.tex); gl.uniform1i(paint.loc('uDepth'), 0);
    gl.activeTexture(gl.TEXTURE1); gl.bindTexture(gl.TEXTURE_2D, paintTarget.brush); gl.uniform1i(paint.loc('uBrush'), 1);
    gl.uniform1f(paint.loc('uMirrorY'), mirrorY);
    for (const twin of mirror ? [0, 1] : [0]) {
      gl.uniform1f(paint.loc('uMirror'), twin);
      for (const p of parts) {
        if (!p.mine) continue;
        gl.bindVertexArray(p.vao);
        gl.drawElements(gl.LINES, p.edgeCount, gl.UNSIGNED_INT, 0);   // the edges too, so no texel at a seam is left out
        gl.drawArrays(gl.TRIANGLES, 0, p.count);
      }
    }
    gl.bindVertexArray(null);
    gl.blendEquation(gl.FUNC_ADD);
    gl.disable(gl.BLEND);
    gl.readPixels(0, 0, w, h, gl.RGBA, gl.UNSIGNED_BYTE, paintTarget.out);
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    return paintTarget.out;
  }

  /** The ray under a point of the canvas (CSS pixels from its top-left), in the model's space: { origin, dir }. */
  function rayAt(x, y) {
    const m = last ?? matrices();
    const inv = invert(m.mvp);
    if (!inv) return null;
    const nx = (x / canvas.clientWidth) * 2 - 1, ny = 1 - (y / canvas.clientHeight) * 2;
    const un = (z) => {
      const v = [nx, ny, z, 1], o = [0, 0, 0, 0];
      for (let r = 0; r < 4; r++) for (let c = 0; c < 4; c++) o[r] += inv[c * 4 + r] * v[c];
      return [o[0] / o[3], o[1] / o[3], o[2] / o[3]];
    };
    const a = un(-1), b = un(1);
    return { origin: a, dir: norm(sub(b, a)) };
  }

  // ---- the camera, as the page drives it ----
  function orbitBy(dx, dy) { cam.yaw -= dx * 0.008; cam.pitch = Math.max(-1.5, Math.min(1.5, cam.pitch + dy * 0.008)); draw(); }
  function panBy(dx, dy) {
    const e = eye(), fwd = norm(sub(cam.target, e)), right = norm(cross(fwd, [0, 0, 1])), up = cross(right, fwd);
    const k = cam.dist * 0.0015;
    cam.target = [0, 1, 2].map((i) => cam.target[i] - right[i] * dx * k + up[i] * dy * k);
    draw();
  }
  function zoomBy(deltaY) { cam.dist *= Math.pow(1.0015, deltaY); draw(); }
  new ResizeObserver(draw).observe(canvas);

  /** A picture of the gun from yaw / pitch (radians), w x h, framed; the camera put back after: a design's pictures. */
  function snapshot(yaw, pitch, w = 1280, h = 720) {
    const keep = { ...cam, target: cam.target.slice() };
    forced = { w, h };
    frame();
    cam.yaw = yaw; cam.pitch = pitch;
    render();
    const url = canvas.toDataURL('image/png');
    forced = null;
    Object.assign(cam, keep);
    render();
    return url;
  }

  return {
    setMesh, updateLive, setTape, setHighlight, viewFrom, frame, draw, project, rayAt, orbitBy, panBy, zoomBy, snapshot,
    set showTape(v) { showTape = !!v; draw(); },
    set dimOthers(v) { dimOthers = !!v; draw(); },
    set exposure(v) { exposure = v; draw(); },
    set lighting(name) { look = LOOKS[name] ?? LOOKS.studio; draw(); },
    set background(name) { bg = BACKGROUNDS[name] ?? BACKGROUNDS.dark; draw(); },
    set wireframe(v) { wire = !!v; draw(); },
    get mirrorPlane() { return mirrorY; },
    get exposure() { return exposure; },
    get size() { const m = matrices(); return { w: m.w, h: m.h }; },
    get hasMesh() { return parts.length > 0; },
  };
}
