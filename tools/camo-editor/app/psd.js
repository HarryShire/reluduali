/**
 * Photoshop files, written and read, with no DOM: the editor's layered save.
 *
 * Mycelium, 2026-10-10: *"I can drag in other images, see the layers. Its a
 * Photoviewer until I attempt to edit and then its a ImageEditor."* And: *"it
 * be cool to open PSDs as previews and then see their layers if I choose to
 * edit them."* An edit is saved as a new file beside the original, *"Always
 * writes a new file and never overwrites unless user specifies"*, as layers
 * kept editable with a flat PNG beside them.
 *
 * WHY PSD AND NOT A FORMAT OF OUR OWN. The layers have to live in some file.
 * One only this Client could read would be a file in his folders that nothing
 * else on his machine can open; a PSD opens in Photoshop with its layers, and
 * it is the format he asked to see layers of anyway. So the save and the
 * reading of his own PSDs are the same code, one format both ways.
 *
 * WHAT IS WRITTEN: 8-bit RGB, one raster layer per layer, PackBits (RLE)
 * compressed, which is what Photoshop itself writes and what every reader
 * reads. Each layer keeps its name (also as Unicode, the `luni` block), its
 * opacity, whether it is hidden and its blend mode. A layer that was scaled is
 * stored at the size it was scaled to: a PSD raster layer has pixels, not a
 * transform. The merged image (the composite, used by anything that does not
 * read layers: ffmpeg, Explorer's thumbnails, the viewer) is stored as well.
 *
 * WHAT IS READ: the same, plus raw (uncompressed) channels. Anything outside
 * that (16 or 32 bits, CMYK, greyscale, ZIP-compressed layers, the large-file
 * PSB) is refused with the reason, and the editor falls back to the composite
 * as one layer and says why. Group folders are read flat: their dividers are
 * dropped and the layers inside them kept.
 *
 * The format: Adobe's "Photoshop File Formats Specification". Checked against
 * readers this project did not write: Pillow reads the layers back
 * (`test/psd.test.mjs`) and ffmpeg reads the composite.
 */

/** Photoshop's limit for a PSD, a side. Larger needs PSB. */
export const MAX_SIDE = 30000;
/** A PSD's offsets are 32-bit signed: 2 GB is the file's limit. */
export const MAX_BYTES = 0x7fffffff;

/** PSD blend keys, and the names the page draws them with (CSS and canvas agree). */
export const BLEND = Object.freeze({
  'norm': 'normal', 'mul ': 'multiply', 'scrn': 'screen', 'over': 'overlay',
  'dark': 'darken', 'lite': 'lighten', 'colD': 'color-dodge', 'colB': 'color-burn',
  'hLit': 'hard-light', 'sLit': 'soft-light', 'diff': 'difference', 'smud': 'exclusion',
  'hue ': 'hue', 'sat ': 'saturation', 'colr': 'color', 'lum ': 'luminosity',
});

// ---- PackBits ------------------------------------------------------------------

/**
 * One row, PackBits-encoded into `out` at `o`. Returns the new end.
 * A run of three or more is a repeat packet; anything else is literal.
 * `out` needs room for len + ceil(len / 128) + 1 bytes.
 *
 * `split` writes the first literal of two or more bytes as two literals, one
 * byte longer, which is how a channel is made an even length (rleChannel).
 */
export function packRow(src, off, len, out, o, split = false) {
  let i = 0;
  while (i < len) {
    const v = src[off + i];
    let run = 1;
    while (run < 128 && i + run < len && src[off + i + run] === v) run++;
    if (run >= 3) {
      out[o++] = (257 - run) & 0xff;
      out[o++] = v;
      i += run;
      continue;
    }
    // Literal until a run of three begins, or 128 bytes. At i itself no run
    // of three begins (that was just tested), so this takes at least one.
    let j = i;
    while (j < len && j - i < 128) {
      if (j + 2 < len && src[off + j] === src[off + j + 1] && src[off + j] === src[off + j + 2]) break;
      j++;
    }
    if (split && j - i >= 2) {
      out[o++] = 0;
      out[o++] = src[off + i];
      i++;
      split = false;
    }
    out[o++] = j - i - 1;
    for (let k = i; k < j; k++) out[o++] = src[off + k];
    i = j;
  }
  return o;
}

/** Decode PackBits from `src[o..end)` into `dst[d..d+len)`. Returns the bytes produced. */
export function unpackRow(src, o, end, dst, d, len) {
  let n = 0;
  while (n < len && o < end) {
    const h = src[o++];
    if (h < 128) {
      const c = Math.min(h + 1, len - n, end - o);
      for (let k = 0; k < c; k++) dst[d + n + k] = src[o + k];
      o += h + 1;
      n += c;
    } else if (h > 128) {
      const c = Math.min(257 - h, len - n);
      const v = src[o++];
      for (let k = 0; k < c; k++) dst[d + n + k] = v;
      n += c;
    }
    // 128 is a no-op, by the specification.
  }
  return n;
}

/**
 * One channel of an RGBA buffer, RLE: the per-row byte counts and the data.
 *
 * ALWAYS AN EVEN NUMBER OF BYTES. The record says how long each channel is,
 * and Photoshop reads by that; Pillow ignores it and steps over a padding byte
 * after any channel that ends on an odd offset, then reads every later layer a
 * byte out. Even lengths read the same both ways. An odd total means some row
 * holds a literal of two bytes or more (repeat packets are two bytes each), so
 * that row is written again with one literal split in two: one byte longer.
 */
function rleChannel(rgba, w, h, c) {
  const row = new Uint8Array(w);
  const out = new Uint8Array(h * (w + Math.ceil(w / 128)) + 1);
  const counts = new Uint8Array(h * 2);
  const rowAt = (y) => { for (let x = 0, p = y * w * 4 + c; x < w; x++, p += 4) row[x] = rgba[p]; };
  let o = 0;
  let odd = -1, oddAt = 0;
  for (let y = 0; y < h; y++) {
    rowAt(y);
    const start = o;
    o = packRow(row, 0, w, out, o);
    const n = o - start;
    if (odd < 0 && n & 1) { odd = y; oddAt = start; }
    counts[y * 2] = n >>> 8;
    counts[y * 2 + 1] = n & 0xff;
  }
  if (o & 1) {
    const was = (counts[odd * 2] << 8) | counts[odd * 2 + 1];
    out.copyWithin(oddAt + was + 1, oddAt + was, o);
    rowAt(odd);
    const tmp = new Uint8Array(w + Math.ceil(w / 128) + 1);
    const n = packRow(row, 0, w, tmp, 0, true);
    if (n !== was + 1) throw new Error('psd: a row could not be evened');
    out.set(tmp.subarray(0, n), oddAt);
    counts[odd * 2] = n >>> 8;
    counts[odd * 2 + 1] = n & 0xff;
    o++;
  }
  return { counts, data: out.subarray(0, o) };
}

// ---- writing --------------------------------------------------------------------

const enc = new TextEncoder();

/** A layer name as Photoshop's legacy Pascal string: printable ASCII only, padded to 4. */
function pascalName(name) {
  const ascii = String(name).replace(/[^\x20-\x7e]/g, '_').slice(0, 255);
  const len = ascii.length;
  const total = Math.ceil((len + 1) / 4) * 4;
  const b = new Uint8Array(total);
  b[0] = len;
  b.set(enc.encode(ascii), 1);
  return b;
}

/** The `luni` block: the name as UTF-16, so a name in any script survives. */
function luniBlock(name) {
  const s = String(name).slice(0, 255);
  const dataLen = Math.ceil((4 + s.length * 2) / 4) * 4;
  const b = new Uint8Array(12 + dataLen);
  const v = new DataView(b.buffer);
  b.set(enc.encode('8BIMluni'), 0);
  v.setUint32(8, dataLen);
  v.setUint32(12, s.length);
  for (let i = 0; i < s.length; i++) v.setUint16(16 + i * 2, s.charCodeAt(i));
  return b;
}

/**
 * A layered PSD.
 *
 * `layers` bottom first, each `{ name, left, top, width, height, rgba,
 * opacity (0..1), visible, blend? }`, with `rgba` width*height*4 bytes.
 * `composite` is the flattened picture, width*height*4. Returns a Uint8Array.
 * Throws, with the reason, for what a PSD cannot hold.
 */
export function writePsd({ width, height, layers = [], composite }) {
  const side = (n, what) => {
    if (!Number.isInteger(n) || n < 1 || n > MAX_SIDE) {
      throw new Error(`${what} is ${n} px; a PSD holds 1 to ${MAX_SIDE.toLocaleString('en-US')} px a side`);
    }
  };
  side(width, 'the picture\'s width');
  side(height, 'the picture\'s height');
  if (!composite || composite.length !== width * height * 4) throw new Error('the composite is not width × height RGBA');
  if (layers.length > 8000) throw new Error('a PSD holds at most 8,000 layers');

  // Every channel first, because the layer records carry their lengths.
  // R, G, B, then transparency. Photoshop reads channels by their ids, in any
  // order; Pillow reads a layer's channels as R, G, B, A whatever the ids say.
  const CH = [[0, 0], [1, 1], [2, 2], [-1, 3]];
  const encoded = layers.map((L, i) => {
    side(L.width, `layer ${i + 1}'s width`);
    side(L.height, `layer ${i + 1}'s height`);
    if (!L.rgba || L.rgba.length !== L.width * L.height * 4) throw new Error(`layer ${i + 1}'s pixels are not width × height RGBA`);
    return CH.map(([, c]) => rleChannel(L.rgba, L.width, L.height, c));
  });
  const records = layers.map((L, i) => {
    const name = pascalName(L.name ?? `Layer ${i + 1}`);
    const luni = luniBlock(L.name ?? `Layer ${i + 1}`);
    const extra = 4 + 4 + name.length + luni.length;
    const b = new Uint8Array(16 + 2 + CH.length * 6 + 12 + 4 + extra);
    const v = new DataView(b.buffer);
    let o = 0;
    v.setInt32(o, L.top); v.setInt32(o + 4, L.left);
    v.setInt32(o + 8, L.top + L.height); v.setInt32(o + 12, L.left + L.width); o += 16;
    v.setUint16(o, CH.length); o += 2;
    CH.forEach(([id], k) => {
      const e = encoded[i][k];
      v.setInt16(o, id);
      v.setUint32(o + 2, 2 + e.counts.length + e.data.length);
      o += 6;
    });
    const key = Object.keys(BLEND).find((k) => k === L.blend || BLEND[k] === L.blend) ?? 'norm';
    b.set(enc.encode('8BIM' + key), o); o += 8;
    b[o++] = Math.round(Math.min(1, Math.max(0, L.opacity ?? 1)) * 255);
    b[o++] = 0;                                   // clipping: base
    b[o++] = 0x08 | (L.visible === false ? 0x02 : 0); // bit 1 set means HIDDEN
    b[o++] = 0;                                   // filler
    v.setUint32(o, extra); o += 4;
    v.setUint32(o, 0); o += 4;                    // no layer mask
    v.setUint32(o, 0); o += 4;                    // no blending ranges
    b.set(name, o); o += name.length;
    b.set(luni, o); o += luni.length;
    return b;
  });

  let layerInfo = 2;
  for (const r of records) layerInfo += r.length;
  for (const chans of encoded) for (const e of chans) layerInfo += 2 + e.counts.length + e.data.length;
  const layerInfoPadded = Math.ceil(layerInfo / 4) * 4;
  const layerMask = 4 + layerInfoPadded + 4;

  const merged = [0, 1, 2, 3].map((c) => rleChannel(composite, width, height, c));
  let mergedLen = 2;
  for (const e of merged) mergedLen += e.counts.length + e.data.length;

  const total = 26 + 4 + 4 + 4 + layerMask + mergedLen;
  if (total > MAX_BYTES) throw new Error('this would be larger than 2 GB, which is a PSD\'s limit');

  const out = new Uint8Array(total);
  const v = new DataView(out.buffer);
  let o = 0;
  out.set(enc.encode('8BPS'), 0); o = 4;
  v.setUint16(o, 1); o += 2;                      // version 1: PSD, not PSB
  o += 6;                                         // reserved
  v.setUint16(o, 4); o += 2;                      // R, G, B and the transparency
  v.setUint32(o, height); o += 4;
  v.setUint32(o, width); o += 4;
  v.setUint16(o, 8); o += 2;                      // bits per channel
  v.setUint16(o, 3); o += 2;                      // RGB
  v.setUint32(o, 0); o += 4;                      // colour mode data: none
  v.setUint32(o, 0); o += 4;                      // image resources: none
  v.setUint32(o, layerMask); o += 4;
  v.setUint32(o, layerInfoPadded); o += 4;
  // Negative: the merged image's first alpha channel is its transparency.
  v.setInt16(o, -layers.length); o += 2;
  for (const r of records) { out.set(r, o); o += r.length; }
  for (const chans of encoded) {
    for (const e of chans) {
      v.setUint16(o, 1); o += 2;                  // RLE
      out.set(e.counts, o); o += e.counts.length;
      out.set(e.data, o); o += e.data.length;
    }
  }
  o += layerInfoPadded - layerInfo;               // padding, already zero
  v.setUint32(o, 0); o += 4;                      // global layer mask: none
  v.setUint16(o, 1); o += 2;                      // the merged image, RLE
  for (const e of merged) { out.set(e.counts, o); o += e.counts.length; }
  for (const e of merged) { out.set(e.data, o); o += e.data.length; }
  if (o !== total) throw new Error(`psd: wrote ${o} of ${total} bytes`);
  return out;
}

// ---- reading --------------------------------------------------------------------

/**
 * A PSD's layers, or `{ ok: false, why }`.
 *
 * `{ ok: true, width, height, layers, flat }`, layers bottom first, each
 * `{ name, left, top, width, height, rgba, opacity, visible, blend }`.
 * `flat` is true when the file had group folders, now read flat.
 * `composite: true` in the options also returns the merged image.
 */
export function readPsd(bytes, opts = {}) {
  try {
    return readPsdOrThrow(bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes), opts);
  } catch (e) {
    return { ok: false, why: String(e.message ?? e) };
  }
}

function readPsdOrThrow(b, { composite: wantComposite = false } = {}) {
  const v = new DataView(b.buffer, b.byteOffset, b.byteLength);
  const dec = new TextDecoder('latin1');
  const need = (o, n) => { if (o + n > b.length) throw new Error('the file ends early'); };
  if (dec.decode(b.subarray(0, 4)) !== '8BPS') throw new Error('not a Photoshop file');
  need(0, 26);
  const version = v.getUint16(4);
  if (version === 2) throw new Error('a large-document PSB, not read yet');
  if (version !== 1) throw new Error(`Photoshop version ${version}, not read`);
  const channels = v.getUint16(12);
  const height = v.getUint32(14);
  const width = v.getUint32(18);
  const depth = v.getUint16(22);
  const mode = v.getUint16(24);
  if (depth !== 8) throw new Error(`${depth} bits a channel; only 8 is read yet`);
  if (mode !== 3) throw new Error(`${({ 0: 'bitmap', 1: 'greyscale', 2: 'indexed', 4: 'CMYK', 7: 'multichannel', 8: 'duotone', 9: 'Lab' })[mode] ?? `mode ${mode}`} colour; only RGB is read yet`);

  let o = 26;
  need(o, 4); o += 4 + v.getUint32(o);            // colour mode data
  need(o, 4); o += 4 + v.getUint32(o);            // image resources
  need(o, 4);
  const lmLen = v.getUint32(o); o += 4;
  const lmEnd = o + lmLen;
  const layers = [];
  let flat = false;
  if (lmLen > 0) {
    need(o, 4);
    const liLen = v.getUint32(o); o += 4;
    const liEnd = o + liLen;
    if (liLen > 0) {
      need(o, 2);
      const count = Math.abs(v.getInt16(o)); o += 2;
      const recs = [];
      for (let i = 0; i < count; i++) {
        need(o, 18);
        const top = v.getInt32(o), left = v.getInt32(o + 4), bottom = v.getInt32(o + 8), right = v.getInt32(o + 12);
        const nch = v.getUint16(o + 16); o += 18;
        const chans = [];
        for (let k = 0; k < nch; k++) { need(o, 6); chans.push({ id: v.getInt16(o), len: v.getUint32(o + 2) }); o += 6; }
        need(o, 16);
        if (dec.decode(b.subarray(o, o + 4)) !== '8BIM') throw new Error('a layer record is damaged');
        const blendKey = dec.decode(b.subarray(o + 4, o + 8));
        const opacity = b[o + 8] / 255;
        const flags = b[o + 10];
        const extraLen = v.getUint32(o + 12); o += 16;
        const extraEnd = o + extraLen;
        need(o, extraLen);
        let e = o;
        e += 4 + v.getUint32(e);                  // layer mask
        e += 4 + v.getUint32(e);                  // blending ranges
        const nlen = b[e];
        let name = dec.decode(b.subarray(e + 1, e + 1 + nlen));
        e += Math.ceil((nlen + 1) / 4) * 4;
        let divider = 0;
        while (e + 12 <= extraEnd) {
          const sig = dec.decode(b.subarray(e, e + 4));
          if (sig !== '8BIM' && sig !== '8B64') break;
          const key = dec.decode(b.subarray(e + 4, e + 8));
          const len = v.getUint32(e + 8);
          const data = e + 12;
          if (key === 'luni' && len >= 4) {
            const n = v.getUint32(data);
            let s = '';
            for (let k = 0; k < n && data + 4 + k * 2 + 1 < data + len; k++) s += String.fromCharCode(v.getUint16(data + 4 + k * 2));
            name = s;
          }
          if ((key === 'lsct' || key === 'lsdk') && len >= 4) divider = v.getUint32(data);
          e = data + len;
        }
        o = extraEnd;
        recs.push({ top, left, bottom, right, chans, blendKey, opacity, flags, name, divider });
      }
      for (const r of recs) {
        const w = Math.max(0, r.right - r.left), h = Math.max(0, r.bottom - r.top);
        const rgba = w && h ? new Uint8ClampedArray(w * h * 4) : null;
        if (rgba) for (let p = 3; p < rgba.length; p += 4) rgba[p] = 255;
        for (const c of r.chans) {
          const start = o;
          need(o, c.len);
          o += c.len;
          const slot = c.id === -1 ? 3 : c.id >= 0 && c.id <= 2 ? c.id : -1;
          if (!rgba || slot < 0 || c.len < 2) continue;   // a mask, or nothing to fill
          readChannel(b, v, start, start + c.len, rgba, w, h, slot);
        }
        // A group's opening and closing markers carry no pixels: the layers
        // between them are kept, read flat.
        if (r.divider === 1 || r.divider === 2 || r.divider === 3) { flat = true; continue; }
        if (!rgba) continue;
        layers.push({
          name: r.name, left: r.left, top: r.top, width: w, height: h, rgba,
          opacity: r.opacity, visible: !(r.flags & 0x02), blend: BLEND[r.blendKey] ?? 'normal',
        });
      }
      o = liEnd;
    }
  }
  const out = { ok: true, width, height, layers, flat };
  if (wantComposite) {
    o = lmEnd;
    need(o, 2);
    const comp = v.getUint16(o); o += 2;
    const rgba = new Uint8ClampedArray(width * height * 4);
    for (let p = 3; p < rgba.length; p += 4) rgba[p] = 255;
    const nc = Math.min(channels, 4);
    if (comp === 1) {
      const counts = o; o += channels * height * 2;
      need(counts, channels * height * 2);
      const row = new Uint8Array(width);
      for (let c = 0; c < nc; c++) {
        for (let y = 0; y < height; y++) {
          const n = v.getUint16(counts + (c * height + y) * 2);
          need(o, n);
          row.fill(0);
          unpackRow(b, o, o + n, row, 0, width);
          for (let x = 0; x < width; x++) rgba[(y * width + x) * 4 + c] = row[x];
          o += n;
        }
      }
    } else if (comp === 0) {
      need(o, nc * width * height);
      for (let c = 0; c < nc; c++) for (let i = 0; i < width * height; i++) rgba[i * 4 + c] = b[o++];
    } else {
      throw new Error('the composite is ZIP-compressed, not read yet');
    }
    out.composite = rgba;
  }
  return out;
}

/** One layer channel at `o`, compression first, into its slot of `rgba`. */
function readChannel(b, v, o, end, rgba, w, h, slot) {
  const comp = v.getUint16(o); o += 2;
  if (comp === 0) {
    for (let i = 0; i < w * h && o < end; i++) rgba[i * 4 + slot] = b[o++];
  } else if (comp === 1) {
    const counts = o; o += h * 2;
    const row = new Uint8Array(w);
    for (let y = 0; y < h; y++) {
      const n = v.getUint16(counts + y * 2);
      row.fill(0);
      unpackRow(b, o, Math.min(o + n, end), row, 0, w);
      for (let x = 0; x < w; x++) rgba[(y * w + x) * 4 + slot] = row[x];
      o += n;
    }
  } else {
    throw new Error('a layer is ZIP-compressed, which is not read yet');
  }
}
