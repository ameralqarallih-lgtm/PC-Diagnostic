/**
 * PNG frames of 256 colours at most (a palette with transparency), for characters: about a third the size of
 * full-colour frames and alike to the eye. One palette serves all of a character's frames, so a colour never changes
 * from one frame to the next, and pixels take their nearest colour without dithering (dither noise would shimmer as the
 * frames play). The colours are chosen by median cut over the frames' pixels, weighted by how often each appears.
 *
 * Plain TypeScript with no imports: the editor uses it to import characters, and web/characters/bake-characters.mjs
 * (Node, which runs it as is) to bake the shipped ones.
 */

/** Frames' pixels as RGBA, each `width` × `height`. */
export interface RgbaFrame {
  data: Uint8ClampedArray | Uint8Array;
  width: number;
  height: number;
}

/** Bits kept of each colour channel and of alpha when colours are counted and matched. */
const RGB_BITS = 6;
const ALPHA_BITS = 4;
const KEYS = 1 << (3 * RGB_BITS + ALPHA_BITS);

/** A pixel this transparent is left out (it is index 0, fully transparent). */
const CLEAR = 8;

const keyOf = (r: number, g: number, b: number, a: number) =>
  ((((r >> (8 - RGB_BITS)) << RGB_BITS) | (g >> (8 - RGB_BITS))) << (RGB_BITS + ALPHA_BITS)) | ((b >> (8 - RGB_BITS)) << ALPHA_BITS) | (a >> (8 - ALPHA_BITS));

interface Box {
  keys: number[];
  /** Its pixels. */
  weight: number;
  /** Its widest channel, and how wide. */
  channel: number;
  widest: number;
}

/**
 * A palette of at most `colours` RGBA colours (entry 0 is transparent) for frames, by median cut: every box of colours
 * is split at the weighted median of its widest channel until there are enough, and each box's colour is its pixels'
 * average.
 */
export function paletteOf(frames: readonly RgbaFrame[], colours = 256): Uint8Array {
  // Each colour's count and its channels' sums (for the average), by key.
  const count = new Map<number, number>();
  const sums = new Map<number, [number, number, number, number]>();
  for (const f of frames) {
    const d = f.data;
    // At most about two million pixels looked at: enough for a palette.
    const step = Math.max(1, Math.floor((frames.length * f.width * f.height) / 2_000_000));
    for (let i = 0; i < d.length; i += 4 * step) {
      const a = d[i + 3]!;
      if (a < CLEAR) continue;
      const k = keyOf(d[i]!, d[i + 1]!, d[i + 2]!, a);
      count.set(k, (count.get(k) ?? 0) + 1);
      const s = sums.get(k);
      if (s) {
        s[0] += d[i]!;
        s[1] += d[i + 1]!;
        s[2] += d[i + 2]!;
        s[3] += a;
      } else sums.set(k, [d[i]!, d[i + 1]!, d[i + 2]!, a]);
    }
  }
  const channel = (k: number, c: number) => (c === 3 ? (k & ((1 << ALPHA_BITS) - 1)) << (8 - ALPHA_BITS) : ((k >> (ALPHA_BITS + (2 - c) * RGB_BITS)) & ((1 << RGB_BITS) - 1)) << (8 - RGB_BITS));
  const boxOf = (keys: number[]): Box => {
    let weight = 0;
    for (const k of keys) weight += count.get(k)!;
    let best = 0;
    let widest = -1;
    for (let c = 0; c < 4; c++) {
      let lo = 255;
      let hi = 0;
      for (const k of keys) {
        const v = channel(k, c);
        if (v < lo) lo = v;
        if (v > hi) hi = v;
      }
      if (hi - lo > widest) {
        widest = hi - lo;
        best = c;
      }
    }
    return { keys, weight, channel: best, widest };
  };
  const boxes: Box[] = count.size ? [boxOf([...count.keys()])] : [];
  while (boxes.length < colours - 1) {
    // The box to split: the one with the most pixels times its widest range.
    let pick = -1;
    let score = 0;
    for (let i = 0; i < boxes.length; i++) {
      const b = boxes[i]!;
      const s = b.keys.length < 2 ? 0 : b.weight * (b.widest + 1);
      if (s > score) {
        score = s;
        pick = i;
      }
    }
    if (pick < 0) break;
    const box = boxes[pick]!;
    const c = box.channel;
    box.keys.sort((x, y) => channel(x, c) - channel(y, c));
    const half = box.weight / 2;
    let seen = 0;
    let cut = 1;
    for (; cut < box.keys.length - 1; cut++) {
      seen += count.get(box.keys[cut - 1]!)!;
      if (seen >= half) break;
    }
    boxes.splice(pick, 1, boxOf(box.keys.slice(0, cut)), boxOf(box.keys.slice(cut)));
  }
  const palette = new Uint8Array(4 * (boxes.length + 1));
  boxes.forEach((b, i) => {
    let n = 0;
    const t = [0, 0, 0, 0];
    for (const k of b.keys) {
      n += count.get(k)!;
      const s = sums.get(k)!;
      for (let c = 0; c < 4; c++) t[c]! += s[c]!;
    }
    for (let c = 0; c < 4; c++) palette[4 * (i + 1) + c] = Math.round(t[c]! / n);
  });
  return palette;
}

/** Each pixel of a frame as its nearest palette entry (a cache shared across frames by `nearest`). */
export function indexFrame(frame: RgbaFrame, palette: Uint8Array, nearest: Int16Array = new Int16Array(KEYS).fill(-1)): Uint8Array {
  const d = frame.data;
  const out = new Uint8Array(frame.width * frame.height);
  const entries = palette.length / 4;
  for (let p = 0, i = 0; p < out.length; p++, i += 4) {
    const a = d[i + 3]!;
    if (a < CLEAR) continue;
    const k = keyOf(d[i]!, d[i + 1]!, d[i + 2]!, a);
    let best = nearest[k]!;
    if (best < 0) {
      // Colours compared as they show: a faint pixel's colour counts for less than its opacity.
      const r = d[i]!, g = d[i + 1]!, b = d[i + 2]!;
      let bestDistance = Infinity;
      for (let e = 1; e < entries; e++) {
        const pa = palette[4 * e + 3]!;
        const w = (a + pa) / 510;
        const dr = (r - palette[4 * e]!) * w, dg = (g - palette[4 * e + 1]!) * w, db = (b - palette[4 * e + 2]!) * w, da = (a - pa) * 1.5;
        const distance = dr * dr + dg * dg + db * db + da * da;
        if (distance < bestDistance) {
          bestDistance = distance;
          best = e;
        }
      }
      nearest[k] = best;
    }
    out[p] = best;
  }
  return out;
}

/** A cache for {@link indexFrame} (one per palette). */
export const nearestCache = () => new Int16Array(KEYS).fill(-1);

const CRC = (() => {
  const table = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    table[n] = c >>> 0;
  }
  return table;
})();

function crc32(bytes: Uint8Array): number {
  let c = 0xffffffff;
  for (let i = 0; i < bytes.length; i++) c = CRC[(c ^ bytes[i]!) & 0xff]! ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

function chunk(type: string, data: Uint8Array): Uint8Array {
  const out = new Uint8Array(12 + data.length);
  const view = new DataView(out.buffer);
  view.setUint32(0, data.length);
  for (let i = 0; i < 4; i++) out[4 + i] = type.charCodeAt(i);
  out.set(data, 8);
  view.setUint32(8 + data.length, crc32(out.subarray(4, 8 + data.length)));
  return out;
}

async function deflate(bytes: Uint8Array): Promise<Uint8Array> {
  const source = new ReadableStream<Uint8Array>({
    start(controller) {
      controller.enqueue(bytes);
      controller.close();
    },
  });
  const stream = source.pipeThrough(new CompressionStream('deflate') as unknown as TransformStream<Uint8Array, Uint8Array>);
  return new Uint8Array(await new Response(stream).arrayBuffer());
}

/** A frame's palette indices as a PNG (colour type 3, with its palette's transparency). */
export async function encodePng8(indices: Uint8Array, width: number, height: number, palette: Uint8Array): Promise<Uint8Array> {
  const entries = palette.length / 4;
  const header = new Uint8Array(13);
  const view = new DataView(header.buffer);
  view.setUint32(0, width);
  view.setUint32(4, height);
  header.set([8, 3, 0, 0, 0], 8);
  const plte = new Uint8Array(3 * entries);
  const trns = new Uint8Array(entries);
  for (let e = 0; e < entries; e++) {
    plte.set(palette.subarray(4 * e, 4 * e + 3), 3 * e);
    trns[e] = palette[4 * e + 3]!;
  }
  // Each row: no filter (best for a palette), then its indices.
  const raw = new Uint8Array(height * (width + 1));
  for (let y = 0; y < height; y++) raw.set(indices.subarray(y * width, (y + 1) * width), y * (width + 1) + 1);
  const parts = [new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), chunk('IHDR', header), chunk('PLTE', plte), chunk('tRNS', trns), chunk('IDAT', await deflate(raw)), chunk('IEND', new Uint8Array(0))];
  const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let at = 0;
  for (const p of parts) {
    out.set(p, at);
    at += p.length;
  }
  return out;
}

/** Frames as 256-colour PNGs sharing one palette. */
export async function encodeFrames8(frames: readonly RgbaFrame[], onProgress?: (done: number) => void): Promise<Uint8Array[]> {
  const palette = paletteOf(frames);
  const nearest = nearestCache();
  const out: Uint8Array[] = [];
  for (let i = 0; i < frames.length; i++) {
    const f = frames[i]!;
    out.push(await encodePng8(indexFrame(f, palette, nearest), f.width, f.height, palette));
    onProgress?.((i + 1) / frames.length);
  }
  return out;
}
