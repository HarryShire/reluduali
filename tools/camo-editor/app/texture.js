/**
 * Game textures, read and written, with no DOM: Black Ops II's .iwi and DirectDraw's .dds.
 *
 * Mycelium, 2026-10-17: *"Live camo swap works. - So essentially we can fork/steal an isolated version of Myejos
 * photo editor and make a camo editor within the console itself?"* A camo build is a folder of .iwi files named
 * after the game's images, and RipperBridge's live swap (0.11.0-dev28+, `img swap <image>|<file.dds>`) reads a
 * .dds. So an edit opens either and saves both: the .dds for the running game, the .iwi for a build.
 *
 * WHAT IS READ
 *   .iwi  version 27 (Black Ops II), format 1 (RGBA, 8 bits a channel): one picture and its mips. Every one of
 *         Mycelium's builds is that (805 files in v1..v6, measured 2026-10-17). Anything else is refused with the
 *         reason; OpenAssetTools' ImageConverter makes a .dds of it, and that is read below.
 *   .dds  32-bit RGBA or BGRA, BC1 / BC2 / BC3 (DXT1 / 3 / 5), BC4 and BC5, with or without the DX10 header:
 *         what ImageConverter writes, and what OpenAssetTools' Unlinker exports of the game's own images.
 *
 * WHAT IS WRITTEN
 *   .dds  32-bit RGBA with every mip, its header byte for byte the one ImageConverter writes, so RipperBridge
 *         reads it as it reads the GUNS tab's Try live files (check/real.mjs compares the two).
 *   .iwi  version 27, format 1, every mip, with the header of the image it replaces carried over: its flags
 *         (byte 5), gamma (12-15) and the 16 bytes after it (nonzero only on spec / gloss images). ImageConverter's
 *         own .iwi drops those: its round trip of the DSR-50's diffuse changed exactly bytes 5 and 15.
 *
 * THE .iwi LAYOUT, as measured on Mycelium's builds and checked against ImageConverter both ways:
 *    0 'IWi'   3 version (27)   4 format (1 = RGBA)   5 flags (0x10 on about half of them)
 *    6 width (u16)   8 height (u16)   10 depth (u16, 1)   12 gamma (f32: 0 or 2.0)
 *   16 16 bytes kept as they are (per-mip gloss on spec images)
 *   32 8 x u32: [i] is the file's size less every mip larger than mip i, so [0] is the file's size; past the
 *      smallest mip the entries repeat the smallest's (a 32 x 32 has 6 mips: [5], [6] and [7] are all 68)
 *   64 the mips, SMALLEST FIRST, each width x height x 4 bytes in the order R G B A
 *
 * A mip chain halves to 1 x 1. Mips made here are a 2 x 2 box average, in the picture's own values (as the game's
 * tools and Photoshop's DDS plug-in do by default); the game's own may differ by a level of rounding.
 */

export const IWI_VERSION = 27;
export const IWI_RGBA = 1;
export const IWI_HEADER = 64;
const DDS_MAGIC = 0x20534444; // 'DDS '

const no = (why) => ({ ok: false, why });
const u16 = (b, o) => b[o] | (b[o + 1] << 8);
const u32 = (b, o) => (b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)) >>> 0;
function put16(b, o, v) { b[o] = v & 255; b[o + 1] = (v >>> 8) & 255; }
function put32(b, o, v) { b[o] = v & 255; b[o + 1] = (v >>> 8) & 255; b[o + 2] = (v >>> 16) & 255; b[o + 3] = (v >>> 24) & 255; }

/** Each mip's [width, height], largest first, down to 1 x 1. */
export function mipDims(w, h) {
  const out = [];
  for (;;) {
    out.push([w, h]);
    if (w === 1 && h === 1) return out;
    w = Math.max(1, w >> 1);
    h = Math.max(1, h >> 1);
  }
}

/** Which kind of file these bytes are, by their first bytes: 'iwi', 'dds', or null. */
export function kindOf(b) {
  if (b.length >= 4 && b[0] === 0x49 && b[1] === 0x57 && b[2] === 0x69) return 'iwi';
  if (b.length >= 4 && u32(b, 0) === DDS_MAGIC) return 'dds';
  return null;
}

/** An .iwi or a .dds, whichever these bytes are. */
export function readTexture(b) {
  const k = kindOf(b);
  if (k === 'iwi') return readIwi(b);
  if (k === 'dds') return readDds(b);
  return no('neither an .iwi nor a .dds file');
}

// ---- .iwi -----------------------------------------------------------------------------------------

/**
 * `{ ok, kind: 'iwi', width, height, mips: [Uint8Array RGBA, largest first], header }`, or `{ ok: false, why }`.
 * `header` is the file's first 64 bytes, kept so a save can carry its flags, gamma and gloss.
 */
export function readIwi(b) {
  if (kindOf(b) !== 'iwi' || b.length < IWI_HEADER) return no('not an .iwi file');
  if (b[3] !== IWI_VERSION) return no(`an .iwi of another game (version ${b[3]}; Black Ops II's is ${IWI_VERSION})`);
  if (b[4] !== IWI_RGBA) return readIwiPacked(b);
  const width = u16(b, 6), height = u16(b, 8), depth = u16(b, 10);
  if (!width || !height) return no('an .iwi with no size');
  if (depth !== 1) return no('a volume .iwi, not one picture');
  // As many mips as the file holds, from the largest down: the sizes must add up to the file exactly.
  const dims = mipDims(width, height);
  const data = b.length - IWI_HEADER;
  let n = 0, sum = 0;
  while (n < dims.length && sum < data) { sum += dims[n][0] * dims[n][1] * 4; n++; }
  if (sum !== data && width === height && data === 6 * width * height * 4) {
    // a cube map, written RGBA (a painted sky): its six faces, no mips
    const pics = [];
    for (let f = 0; f < 6; f++) pics.push(b.slice(IWI_HEADER + f * width * height * 4, IWI_HEADER + (f + 1) * width * height * 4));
    return { ok: true, kind: 'iwi', width: width * 4, height: height * 3, mips: [crossOf(pics, width)], header: b.slice(0, IWI_HEADER), cube: width };
  }
  if (sum !== data) return no(`an .iwi whose pictures are not whole mips (${data} bytes for ${width} x ${height}): cut off, or a kind this editor does not read`);
  const mips = [];
  let end = b.length;                         // the largest is last
  for (let k = 0; k < n; k++) {
    const size = dims[k][0] * dims[k][1] * 4;
    mips.push(b.slice(end - size, end));
    end -= size;
  }
  return { ok: true, kind: 'iwi', width, height, mips, header: b.slice(0, IWI_HEADER) };
}

/**
 * An .iwi of these mips (largest first, RGBA). `header`, the replaced image's first 64 bytes, gives the flags,
 * gamma and gloss; without it they are ImageConverter's (all zero).
 */
export function writeIwi({ width, height, mips, header = null }) {
  checkMips(width, height, mips);
  if (width > 0xffff || height > 0xffff) throw new Error('an .iwi side is at most 65535');
  const total = IWI_HEADER + mips.reduce((n, m) => n + m.length, 0);
  const out = new Uint8Array(total);
  if (header) out.set(header.subarray(0, 32), 0);
  out[0] = 0x49; out[1] = 0x57; out[2] = 0x69;
  out[3] = IWI_VERSION;
  out[4] = IWI_RGBA;
  put16(out, 6, width);
  put16(out, 8, height);
  put16(out, 10, 1);
  let rest = total;
  for (let i = 0; i < 8; i++) {
    put32(out, 32 + 4 * i, rest);
    if (i < mips.length - 1) rest -= mips[i].length;   // past the smallest it stays at the smallest's (a 32 x 32's [5..7])
  }
  let at = IWI_HEADER;
  for (let k = mips.length - 1; k >= 0; k--) { out.set(mips[k], at); at += mips[k].length; }
  return out;
}

/**
 * The game's other .iwi formats (2026-10-18, a sky: "Why can't I just paint on the skybox as well?"): DXT1 (11), DXT3
 * (12), DXT5 (13), one picture or a CUBE MAP. A map's sky is a cube map, six square faces in one file (measured on
 * mp_frostbite_skybox_ft.iwi: format 13, flags 6, 512 x 512, 64 + 6 x 262144 bytes, no mips). A cube opens UNFOLDED, the
 * six faces on one canvas as a cross 4 faces wide and 3 high (cubeCells), and goes back to six faces on save.
 * Mips, smallest first as RGBA ones are; a cube's faces in D3D's order (+X -X +Y -Y +Z -Z) within each.
 */
const IWI_PACKED = { 11: 'bc1', 12: 'bc2', 13: 'bc3' };
function readIwiPacked(b) {
  const format = IWI_PACKED[b[4]];
  if (!format) return no(`an .iwi in format ${b[4]}, which this editor does not read: make a .dds of it with ImageConverter and open that`);
  const width = u16(b, 6), height = u16(b, 8), depth = u16(b, 10);
  if (!width || !height) return no('an .iwi with no size');
  if (depth !== 1) return no('a volume .iwi, not one picture');
  const dims = mipDims(width, height), bytes = ([w, h]) => Math.max(1, Math.ceil(w / 4)) * Math.max(1, Math.ceil(h / 4)) * BLOCK[format];
  const data = b.length - IWI_HEADER;
  for (const faces of [1, 6]) {
    let n = 0, sum = 0;
    while (n < dims.length && sum < data) { sum += faces * bytes(dims[n]); n++; }
    if (sum !== data) continue;
    if (faces === 6 && width !== height) continue;
    // the largest mip is last; within it, the faces in order
    const top = faces * bytes(dims[0]), start = b.length - top;
    const pics = [];
    for (let f = 0; f < faces; f++) pics.push(decode(format, b.subarray(start + f * bytes(dims[0]), start + (f + 1) * bytes(dims[0])), width, height));
    if (faces === 1) return { ok: true, kind: 'iwi', width, height, mips: [pics[0]], header: b.slice(0, IWI_HEADER), format, mipCount: n };
    return { ok: true, kind: 'iwi', width: width * 4, height: height * 3, mips: [crossOf(pics, width)], header: b.slice(0, IWI_HEADER), format, mipCount: n, cube: width };
  }
  return no(`an .iwi (${format}) whose size does not add up to whole mips (${data} bytes for ${width} x ${height})`);
}

/** Where each cube face sits on the unfolded canvas, in faces (column, row): +X -X +Y -Y +Z -Z. */
export const cubeCells = [[2, 1], [0, 1], [1, 0], [1, 2], [1, 1], [3, 1]];

/** Six square RGBA faces unfolded into one 4 x 3 canvas (the cells no face covers: transparent). */
export function crossOf(faces, fw) {
  const W = fw * 4, out = new Uint8Array(W * fw * 3 * 4);
  faces.forEach((f, i) => {
    const [cx, cy] = cubeCells[i];
    for (let y = 0; y < fw; y++) out.set(f.subarray(y * fw * 4, (y + 1) * fw * 4), ((cy * fw + y) * W + cx * fw) * 4);
  });
  return out;
}

/** The six faces back out of an unfolded canvas. */
export function facesOf(cross, fw) {
  const W = fw * 4;
  return cubeCells.map(([cx, cy]) => {
    const f = new Uint8Array(fw * fw * 4);
    for (let y = 0; y < fw; y++) f.set(cross.subarray(((cy * fw + y) * W + cx * fw) * 4, ((cy * fw + y) * W + cx * fw + fw) * 4), y * fw * 4);
    return f;
  });
}

/** A cube map's .iwi: six RGBA faces (the canvas folded back), no mips, the replaced sky's header (its flags) carried. */
export function writeIwiCube({ cross, face, header = null }) {
  const faces = facesOf(cross, face);
  const total = IWI_HEADER + faces.reduce((n, f) => n + f.length, 0);
  const out = new Uint8Array(total);
  if (header) out.set(header.subarray(0, 32), 0);
  out[0] = 0x49; out[1] = 0x57; out[2] = 0x69;
  out[3] = IWI_VERSION;
  out[4] = IWI_RGBA;
  if (!header) out[5] = 6;                       // the flags the game's own skies carry
  put16(out, 6, face); put16(out, 8, face); put16(out, 10, 1);
  // the size table as the game's one-mip cube has it: the file's size, then the size less the header
  for (let i = 0; i < 8; i++) put32(out, 32 + 4 * i, i === 0 ? total : total - IWI_HEADER);
  let at = IWI_HEADER;
  for (const f of faces) { out.set(f, at); at += f.length; }
  return out;
}

/** A cube map's .dds (RGBA, every mip of every face, face after face): what RipperBridge's img swap takes for a sky. */
export function writeDdsCube({ cross, face }) {
  const chains = facesOf(cross, face).map((f) => mipChain(f, face, face));
  const total = 128 + chains.reduce((n, c) => n + c.reduce((m, x) => m + x.length, 0), 0);
  const out = new Uint8Array(total);
  put32(out, 0, DDS_MAGIC);
  put32(out, 4, 124);
  put32(out, 8, 0x100f | 0x20000);
  put32(out, 12, face);
  put32(out, 16, face);
  put32(out, 20, face * 4);
  put32(out, 24, 1);
  put32(out, 28, chains[0].length);
  put32(out, 76, 32);
  put32(out, 80, 0x41);
  put32(out, 88, 32);
  put32(out, 92, 0x000000ff);
  put32(out, 96, 0x0000ff00);
  put32(out, 100, 0x00ff0000);
  put32(out, 104, 0xff000000);
  put32(out, 108, 0x1000 | 0x400008);            // texture, complex, mipmap
  put32(out, 112, 0x200 | 0xfc00);               // a cube map, all six faces
  let at = 128;
  for (const c of chains) for (const m of c) { out.set(m, at); at += m.length; }
  return out;
}

// ---- .dds -----------------------------------------------------------------------------------------

const FOURCC = {
  DXT1: 'bc1', DXT2: 'bc2', DXT3: 'bc2', DXT4: 'bc3', DXT5: 'bc3',
  ATI1: 'bc4', BC4U: 'bc4', ATI2: 'bc5', BC5U: 'bc5',
};
const DXGI = {
  28: 'rgba', 29: 'rgba', 87: 'bgra', 91: 'bgra', 88: 'bgrx', 93: 'bgrx',
  71: 'bc1', 72: 'bc1', 74: 'bc2', 75: 'bc2', 77: 'bc3', 78: 'bc3', 80: 'bc4', 83: 'bc5',
};
const BLOCK = { bc1: 8, bc4: 8, bc2: 16, bc3: 16, bc5: 16 };

/** `{ ok, kind: 'dds', width, height, mips: [Uint8Array RGBA, largest first], format }`, or `{ ok: false, why }`. */
export function readDds(b) {
  if (kindOf(b) !== 'dds' || b.length < 128) return no('not a .dds file');
  const height = u32(b, 12), width = u32(b, 16);
  let count = Math.max(1, u32(b, 28));
  const pf = u32(b, 80), four = String.fromCharCode(b[84], b[85], b[86], b[87]);
  const bits = u32(b, 88), rm = u32(b, 92), gm = u32(b, 96), bm = u32(b, 100), am = u32(b, 104);
  const caps2 = u32(b, 112);
  if (!width || !height) return no('a .dds with no size');
  if (caps2 & 0x200) return no('a cube map .dds, not one picture');
  if (caps2 & 0x200000) return no('a volume .dds, not one picture');
  let at = 128, format = null;
  if (pf & 4) {
    if (four === 'DX10') {
      if (b.length < 148) return no('a .dds cut off in its DX10 header');
      const dxgi = u32(b, 128);
      if (u32(b, 132) !== 3) return no('a .dds that is not a 2D texture');
      if (u32(b, 140) > 1) return no('a .dds holding an array of textures');
      format = DXGI[dxgi] ?? null;
      if (!format) return no(`a .dds in a format this editor does not read (DXGI ${dxgi})`);
      at = 148;
    } else {
      format = FOURCC[four] ?? null;
      if (!format) return no(`a .dds in a format this editor does not read (${JSON.stringify(four)})`);
    }
  } else if (bits === 32 && (pf & 0x40)) {
    const alpha = (pf & 1) && am === 0xff000000;
    if (rm === 0xff && gm === 0xff00 && bm === 0xff0000) format = alpha ? 'rgba' : 'rgbx';
    else if (rm === 0xff0000 && gm === 0xff00 && bm === 0xff) format = alpha ? 'bgra' : 'bgrx';
  }
  if (!format) return no(`a .dds in a format this editor does not read (${bits} bits, masks ${rm.toString(16)} ${gm.toString(16)} ${bm.toString(16)})`);
  const dims = mipDims(width, height);
  count = Math.min(count, dims.length);
  const mips = [];
  for (let k = 0; k < count; k++) {
    const [w, h] = dims[k];
    const size = BLOCK[format] ? Math.max(1, Math.ceil(w / 4)) * Math.max(1, Math.ceil(h / 4)) * BLOCK[format] : w * h * 4;
    if (at + size > b.length) {
      if (k === 0) return no('a .dds cut off inside its picture');
      break;                                   // a short chain: keep the mips that are whole
    }
    mips.push(decode(format, b.subarray(at, at + size), w, h));
    at += size;
  }
  return { ok: true, kind: 'dds', width, height, mips, format };
}

/** A .dds of these mips (largest first, RGBA): 32-bit RGBA, the header ImageConverter writes. */
export function writeDds({ width, height, mips }) {
  checkMips(width, height, mips);
  const many = mips.length > 1;
  const total = 128 + mips.reduce((n, m) => n + m.length, 0);
  const out = new Uint8Array(total);
  put32(out, 0, DDS_MAGIC);
  put32(out, 4, 124);
  put32(out, 8, 0x100f | (many ? 0x20000 : 0));  // caps, height, width, pitch, pixel format (+ mip count)
  put32(out, 12, height);
  put32(out, 16, width);
  put32(out, 20, width * 4);                      // pitch
  put32(out, 24, 1);                              // depth: ImageConverter writes 1
  put32(out, 28, mips.length);
  put32(out, 76, 32);                             // the pixel format: 32 bits, RGB with alpha, R in the low byte
  put32(out, 80, 0x41);
  put32(out, 88, 32);
  put32(out, 92, 0x000000ff);
  put32(out, 96, 0x0000ff00);
  put32(out, 100, 0x00ff0000);
  put32(out, 104, 0xff000000);
  put32(out, 108, 0x1000 | (many ? 0x400008 : 0)); // texture (+ complex, mipmap)
  let at = 128;
  for (const m of mips) { out.set(m, at); at += m.length; }
  return out;
}

// ---- mips -----------------------------------------------------------------------------------------

/** Every mip of a picture, itself first: each a 2 x 2 box average of the one before, down to 1 x 1. */
export function mipChain(rgba, w, h) {
  if (rgba.length !== w * h * 4) throw new Error(`a ${w} x ${h} picture is ${w * h * 4} bytes, not ${rgba.length}`);
  const out = [rgba instanceof Uint8Array ? rgba : new Uint8Array(rgba.buffer, rgba.byteOffset, rgba.length)];
  let src = out[0], sw = w, sh = h;
  while (sw > 1 || sh > 1) {
    const dw = Math.max(1, sw >> 1), dh = Math.max(1, sh >> 1);
    const dst = new Uint8Array(dw * dh * 4);
    for (let y = 0; y < dh; y++) {
      const y0 = Math.min(sh - 1, y * 2), y1 = Math.min(sh - 1, y * 2 + 1);
      for (let x = 0; x < dw; x++) {
        const x0 = Math.min(sw - 1, x * 2), x1 = Math.min(sw - 1, x * 2 + 1);
        const a = (y0 * sw + x0) * 4, b = (y0 * sw + x1) * 4, c = (y1 * sw + x0) * 4, d = (y1 * sw + x1) * 4;
        const o = (y * dw + x) * 4;
        for (let ch = 0; ch < 4; ch++) dst[o + ch] = (src[a + ch] + src[b + ch] + src[c + ch] + src[d + ch] + 2) >> 2;
      }
    }
    out.push(dst);
    src = dst; sw = dw; sh = dh;
  }
  return out;
}

function checkMips(width, height, mips) {
  if (!mips?.length) throw new Error('no picture to write');
  const dims = mipDims(width, height);
  if (mips.length > dims.length) throw new Error(`a ${width} x ${height} picture has at most ${dims.length} mips`);
  mips.forEach((m, k) => {
    const want = dims[k][0] * dims[k][1] * 4;
    if (m.length !== want) throw new Error(`mip ${k} of ${width} x ${height} should be ${want} bytes, not ${m.length}`);
  });
}

// ---- decoding -------------------------------------------------------------------------------------

function decode(format, src, w, h) {
  const out = new Uint8Array(w * h * 4);
  if (format === 'rgba' || format === 'rgbx') {
    out.set(src);
    if (format === 'rgbx') for (let i = 3; i < out.length; i += 4) out[i] = 255;
    return out;
  }
  if (format === 'bgra' || format === 'bgrx') {
    for (let i = 0; i < out.length; i += 4) {
      out[i] = src[i + 2]; out[i + 1] = src[i + 1]; out[i + 2] = src[i];
      out[i + 3] = format === 'bgrx' ? 255 : src[i + 3];
    }
    return out;
  }
  const bw = Math.max(1, Math.ceil(w / 4)), bh = Math.max(1, Math.ceil(h / 4));
  const px = new Uint8Array(64);               // one block's 16 pixels, RGBA
  for (let by = 0; by < bh; by++) {
    for (let bx = 0; bx < bw; bx++) {
      const o = (by * bw + bx) * BLOCK[format];
      if (format === 'bc1') colourBlock(src, o, px, true);
      else if (format === 'bc2') { colourBlock(src, o + 8, px, false); explicitAlpha(src, o, px); }
      else if (format === 'bc3') { colourBlock(src, o + 8, px, false); rampBlock(src, o, px, 3); }
      else if (format === 'bc4') { rampBlock(src, o, px, 0); for (let i = 0; i < 16; i++) { px[i * 4 + 1] = px[i * 4 + 2] = px[i * 4]; px[i * 4 + 3] = 255; } }
      else if (format === 'bc5') { rampBlock(src, o, px, 0); rampBlock(src, o + 8, px, 1); normalZ(px); }
      for (let y = 0; y < 4; y++) {
        const iy = by * 4 + y;
        if (iy >= h) break;
        for (let x = 0; x < 4; x++) {
          const ix = bx * 4 + x;
          if (ix >= w) break;
          out.set(px.subarray((y * 4 + x) * 4, (y * 4 + x) * 4 + 4), (iy * w + ix) * 4);
        }
      }
    }
  }
  return out;
}

const c565 = (c) => [((c >> 11) & 31) * 255 / 31, ((c >> 5) & 63) * 255 / 63, (c & 31) * 255 / 31];

/** A BC1 colour block into `px` (alpha 255, or 0 for BC1's transparent index when `oneBit`). */
function colourBlock(s, o, px, oneBit) {
  const a = u16(s, o), b = u16(s, o + 2);
  const p0 = c565(a), p1 = c565(b);
  const four = !oneBit || a > b;
  const pal = [p0, p1,
    four ? p0.map((v, i) => (2 * v + p1[i]) / 3) : p0.map((v, i) => (v + p1[i]) / 2),
    four ? p0.map((v, i) => (v + 2 * p1[i]) / 3) : [0, 0, 0]];
  const bits = u32(s, o + 4);
  for (let i = 0; i < 16; i++) {
    const idx = (bits >>> (i * 2)) & 3;
    const c = pal[idx];
    px[i * 4] = Math.round(c[0]); px[i * 4 + 1] = Math.round(c[1]); px[i * 4 + 2] = Math.round(c[2]);
    px[i * 4 + 3] = !four && idx === 3 ? 0 : 255;
  }
}

/** BC2's alpha: 4 bits a pixel, as they are. */
function explicitAlpha(s, o, px) {
  for (let i = 0; i < 16; i++) px[i * 4 + 3] = ((s[o + (i >> 1)] >> ((i & 1) * 4)) & 15) * 17;
}

/** A BC3 alpha / BC4 block (two ends and 3-bit indices) into channel `ch` of `px`. */
function rampBlock(s, o, px, ch) {
  const a0 = s[o], a1 = s[o + 1];
  const pal = [a0, a1];
  if (a0 > a1) for (let i = 1; i < 7; i++) pal.push(((7 - i) * a0 + i * a1) / 7);
  else { for (let i = 1; i < 5; i++) pal.push(((5 - i) * a0 + i * a1) / 5); pal.push(0, 255); }
  // 48 bits of indices, low first: two 24-bit halves keep it within 32-bit arithmetic
  const lo = s[o + 2] | (s[o + 3] << 8) | (s[o + 4] << 16), hi = s[o + 5] | (s[o + 6] << 8) | (s[o + 7] << 16);
  for (let i = 0; i < 16; i++) {
    const idx = i < 8 ? (lo >> (i * 3)) & 7 : (hi >> ((i - 8) * 3)) & 7;
    px[i * 4 + ch] = Math.round(pal[idx]);
  }
}

/** BC5 holds a normal's X and Y: Z is what makes it unit length. */
function normalZ(px) {
  for (let i = 0; i < 16; i++) {
    const x = px[i * 4] / 127.5 - 1, y = px[i * 4 + 1] / 127.5 - 1;
    px[i * 4 + 2] = Math.round((Math.sqrt(Math.max(0, 1 - x * x - y * y)) + 1) * 127.5);
    px[i * 4 + 3] = 255;
  }
}
