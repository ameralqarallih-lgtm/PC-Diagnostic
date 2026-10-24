import { decodeAnimatedFile, decodeVideoFile } from '../anim';
import { PACK_LIMITS, writePack, type CharacterPack, type PackClip } from './pack';
import { encodePng8, indexFrame, nearestCache, paletteOf, type RgbaFrame } from './png8';

/**
 * Importing a character: an animation for each thing it does (an animated GIF, WebP or PNG; a video, WebM with
 * transparency or a flat background to take out; PNG frames; a sprite sheet), made into the frames of one character
 * file. Each clip is cut to what its frames draw, its feet (the bottom of what it draws) put on one spot shared by every
 * clip, and scaled alike (the idle clip says how tall the character stands), so the clips play in turn without a jump.
 */

/** The tallest the character is kept, in pixels (big enough for its largest size on a high-DPI screen). */
export const IMPORT_TALL = 320;

/** The most seconds of a video taken. */
export const VIDEO_SECONDS = 12;

/** One clip as chosen: its frames decoded, at a rate, and how large it is drawn against the others. */
export interface SourceClip {
  action: string;
  fps: number;
  frames: ImageBitmap[];
  /** 50-200 %: this clip's size against the others' (a clip drawn larger or smaller than the rest). */
  scale: number;
}

/** A colour taken out of every frame (a flat background), with how far from it still counts (0-255). */
export interface KeyColour {
  r: number;
  g: number;
  b: number;
  tolerance: number;
}

/** A sprite sheet's grid: frames left to right, then top to bottom; `count` of them (the last row may be short). */
export interface SheetGrid {
  columns: number;
  rows: number;
  count: number;
}

/** Names in the order people read them (frame2 before frame10). */
export const naturalOrder = (a: string, b: string) => a.localeCompare(b, undefined, { numeric: true, sensitivity: 'base' });

const isVideo = (f: File) => f.type.startsWith('video/');

/**
 * The frames of what was chosen for a clip: one animated picture or video, several pictures (frames, by name), or one
 * picture (a sprite sheet when `grid` says how it is cut; otherwise a single still frame). `fps` applies to videos,
 * frames and sheets (an animated picture has its own).
 */
export async function decodeClip(files: readonly File[], options: { fps?: number; grid?: SheetGrid | null; maxSide?: number } = {}): Promise<{ fps: number; frames: ImageBitmap[] }> {
  const { fps = 12, grid = null, maxSide = 720 } = options;
  if (files.length === 0) throw new Error('Choose a file first.');
  if (files.length > 1) {
    const sorted = [...files].sort((a, b) => naturalOrder(a.name, b.name)).slice(0, PACK_LIMITS.frames);
    const frames = await Promise.all(sorted.map((f) => createImageBitmap(f)));
    return { fps, frames: await shrinkAll(frames, maxSide) };
  }
  const file = files[0]!;
  if (isVideo(file)) {
    const decoded = await decodeVideoFile(file, { maxSide, fps: Math.min(30, fps), maxSeconds: VIDEO_SECONDS });
    if (!decoded) throw new Error('This browser cannot play that video. WebM (VP9, with transparency) or MP4 (H.264) work.');
    return decoded;
  }
  const animated = await decodeAnimatedFile(file, maxSide).catch(() => null);
  if (animated) return animated;
  const still = await createImageBitmap(file);
  if (grid && grid.columns * grid.rows > 1) return { fps, frames: await shrinkAll(await splitSheet(still, grid), maxSide) };
  return { fps, frames: await shrinkAll([still], maxSide) };
}

/** A sprite sheet cut into its frames. */
export async function splitSheet(sheet: ImageBitmap, grid: SheetGrid): Promise<ImageBitmap[]> {
  const w = Math.floor(sheet.width / grid.columns);
  const h = Math.floor(sheet.height / grid.rows);
  const count = Math.min(grid.count || grid.columns * grid.rows, grid.columns * grid.rows, PACK_LIMITS.frames);
  const frames: ImageBitmap[] = [];
  for (let i = 0; i < count; i++) frames.push(await createImageBitmap(sheet, (i % grid.columns) * w, Math.floor(i / grid.columns) * h, w, h));
  return frames;
}

async function shrinkAll(frames: ImageBitmap[], maxSide: number): Promise<ImageBitmap[]> {
  const out: ImageBitmap[] = [];
  for (const f of frames) {
    const k = Math.min(1, maxSide / Math.max(f.width, f.height));
    if (k >= 1) out.push(f);
    else {
      out.push(await createImageBitmap(f, { resizeWidth: Math.max(1, Math.round(f.width * k)), resizeHeight: Math.max(1, Math.round(f.height * k)), resizeQuality: 'high' }));
      f.close();
    }
  }
  return out;
}

/**
 * The background colour a frame seems to have: its corners' colour when all four are opaque and alike; null when the
 * frame is see-through there already (or has no flat background).
 */
export function backgroundOf(frame: RgbaFrame): KeyColour | null {
  const { data, width, height } = frame;
  const at = (x: number, y: number) => (y * width + x) * 4;
  const corners = [at(0, 0), at(width - 1, 0), at(0, height - 1), at(width - 1, height - 1)].map((i) => [data[i]!, data[i + 1]!, data[i + 2]!, data[i + 3]!] as const);
  if (corners.some((c) => c[3] < 250)) return null;
  const [r, g, b] = corners[0]!;
  const alike = corners.every((c) => Math.abs(c[0] - r) + Math.abs(c[1] - g) + Math.abs(c[2] - b) < 30);
  return alike ? { r, g, b, tolerance: 48 } : null;
}

/** How far from a pixel taken out an edge reaches (pixels): its colour is part the key's. */
const EDGE = 2;

/**
 * Takes a colour out of a frame (in place), the way a green screen is taken out:
 * - pixels within `tolerance` of it become see-through;
 * - a shadow cast on it (the colour darkened, nothing else mixed in) becomes a see-through black shadow;
 * - pixels at the cut's edge are part the key colour: how much is told by how far they lean its way from grey, which
 *   is taken out of them and becomes see-through (no fringe). Pixels further in are left as they are, so a character
 *   with something of the key's colour on it keeps it.
 */
export function keyOut(frame: RgbaFrame, key: KeyColour) {
  const { data, width, height } = frame;
  const kk = key.r * key.r + key.g * key.g + key.b * key.b;
  const kg = (key.r + key.g + key.b) / 3;
  const chroma = [key.r - kg, key.g - kg, key.b - kg] as const;
  const cc = chroma[0] ** 2 + chroma[1] ** 2 + chroma[2] ** 2;
  const cut = new Uint8Array(width * height);
  for (let p = 0, i = 0; p < cut.length; p++, i += 4) {
    const r = data[i]!, g = data[i + 1]!, b = data[i + 2]!;
    if (Math.sqrt((r - key.r) ** 2 + (g - key.g) ** 2 + (b - key.b) ** 2) <= key.tolerance) {
      data[i + 3] = 0;
      cut[p] = 1;
      continue;
    }
    // A shadow: the key colour scaled down (s of it), so black at 1 - s over it.
    if (kk > 48 * 48) {
      const s = (r * key.r + g * key.g + b * key.b) / kk;
      const rest = Math.sqrt((r - s * key.r) ** 2 + (g - s * key.g) ** 2 + (b - s * key.b) ** 2);
      if (s > 0.05 && s < 1 && rest < 14 + key.tolerance * 0.15) {
        data[i] = data[i + 1] = data[i + 2] = 0;
        data[i + 3] = Math.round(data[i + 3]! * (1 - s));
        cut[p] = 1;
      }
    }
  }
  if (cc < 24 * 24) return; // A grey key has no way to lean.
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      const p = y * width + x;
      if (cut[p]) continue;
      let edge = false;
      for (let dy = -EDGE; dy <= EDGE && !edge; dy++) {
        for (let dx = -EDGE; dx <= EDGE && !edge; dx++) {
          const nx = x + dx, ny = y + dy;
          if (nx >= 0 && ny >= 0 && nx < width && ny < height && cut[ny * width + nx]) edge = true;
        }
      }
      if (!edge) continue;
      const i = 4 * p;
      const r = data[i]!, g = data[i + 1]!, b = data[i + 2]!;
      const grey = (r + g + b) / 3;
      const lean = Math.max(0, Math.min(1, ((r - grey) * chroma[0] + (g - grey) * chroma[1] + (b - grey) * chroma[2]) / cc));
      if (lean <= 0.04) continue;
      const a = Math.max(0.02, 1 - lean);
      data[i] = Math.max(0, Math.min(255, Math.round((r - (1 - a) * key.r) / a)));
      data[i + 1] = Math.max(0, Math.min(255, Math.round((g - (1 - a) * key.g) / a)));
      data[i + 2] = Math.max(0, Math.min(255, Math.round((b - (1 - a) * key.b) / a)));
      data[i + 3] = Math.round(data[i + 3]! * a);
    }
  }
}

/** The box of a frame's pixels that are not (nearly) see-through; null when it is empty. */
export function boundsOf(frame: RgbaFrame): { left: number; top: number; right: number; bottom: number } | null {
  const { data, width, height } = frame;
  let left = width;
  let top = height;
  let right = -1;
  let bottom = -1;
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      if (data[(y * width + x) * 4 + 3]! > 8) {
        if (x < left) left = x;
        if (x > right) right = x;
        if (y < top) top = y;
        if (y > bottom) bottom = y;
      }
    }
  }
  return right < 0 ? null : { left, top, right: right + 1, bottom: bottom + 1 };
}

/** Where each clip goes in the character's frames: its box in its own frames, and its scale and place in the new ones. */
export interface ClipPlacement {
  box: { left: number; top: number; right: number; bottom: number };
  scale: number;
  /** Where its box's top left goes in the character's frames. */
  x: number;
  y: number;
}

/**
 * The character's frames' size, its feet, how tall it stands, and each clip's placement: every clip's box (what its
 * frames draw) bottom-centred on the feet, scaled so the idle clip stands `tall` pixels at most.
 */
export function layoutClips(boxes: readonly { box: ClipPlacement['box']; scale: number }[], idle = 0, tall = IMPORT_TALL) {
  const reference = boxes[idle] ?? boxes[0]!;
  const standing = (reference.box.bottom - reference.box.top) * (reference.scale / 100);
  const k = Math.min(1, tall / standing, (PACK_LIMITS.side - 8) / Math.max(...boxes.map((b) => Math.max(b.box.right - b.box.left, b.box.bottom - b.box.top) * (b.scale / 100))));
  const sized = boxes.map((b) => {
    const s = k * (b.scale / 100);
    return { ...b, s, w: (b.box.right - b.box.left) * s, h: (b.box.bottom - b.box.top) * s };
  });
  const pad = 2;
  const half = Math.max(...sized.map((b) => b.w / 2));
  const up = Math.max(...sized.map((b) => b.h));
  const width = Math.ceil(2 * half) + 2 * pad;
  const height = Math.ceil(up) + 2 * pad;
  const anchor: [number, number] = [width / 2, height - pad];
  const placements: ClipPlacement[] = sized.map((b) => ({ box: b.box, scale: b.s, x: anchor[0] - b.w / 2, y: anchor[1] - b.h }));
  return { width, height, anchor, tall: Math.max(8, Math.min(height, standing * k)), placements };
}

/** The RGBA pixels of a decoded frame (keyed when asked). */
function pixelsOf(frame: ImageBitmap, key: KeyColour | null): RgbaFrame {
  const canvas = new OffscreenCanvas(frame.width, frame.height);
  const ctx = canvas.getContext('2d')!;
  ctx.drawImage(frame, 0, 0);
  const data = ctx.getImageData(0, 0, frame.width, frame.height);
  const out = { data: data.data, width: frame.width, height: frame.height };
  if (key) keyOut(out, key);
  return out;
}

/** How many of a clip's frames its palette is made from (spread through it). */
const PALETTE_FRAMES = 24;

/**
 * The character's file from its clips: keyed, cut, placed and scaled (see {@link layoutClips}), then written as PNG
 * frames (256 colours with a palette per clip, unless `fullColour`). A frame's pixels are made when needed and let go
 * (a long video would not fit in memory whole).
 */
export async function buildCharacter(
  name: string,
  clips: readonly SourceClip[],
  options: { faces: 1 | -1; key: KeyColour | null; fullColour?: boolean; tall?: number; onProgress?: (what: string, done: number) => void },
): Promise<{ pack: CharacterPack; bytes: Uint8Array }> {
  const { faces, key, fullColour = false, tall = IMPORT_TALL, onProgress } = options;
  // The frames kept: at most 300 a clip and 1,200 in all.
  let room = PACK_LIMITS.totalFrames;
  const kept = clips.map((clip) => {
    const frames = clip.frames.slice(0, Math.max(0, Math.min(PACK_LIMITS.frames, room)));
    room -= frames.length;
    return frames;
  });
  const total = kept.reduce((n, f) => n + f.length, 0);
  if (kept.some((f) => f.length === 0)) throw new Error('A character has at most 1,200 frames in all: use fewer or shorter animations.');

  // First, the box of what each clip's frames draw.
  const boxes: { box: ClipPlacement['box']; scale: number }[] = [];
  let done = 0;
  for (let c = 0; c < clips.length; c++) {
    let box: ClipPlacement['box'] | null = null;
    for (const f of kept[c]!) {
      const b = boundsOf(pixelsOf(f, key));
      if (b) box = box ? { left: Math.min(box.left, b.left), top: Math.min(box.top, b.top), right: Math.max(box.right, b.right), bottom: Math.max(box.bottom, b.bottom) } : b;
      onProgress?.('Reading the frames', ++done / total);
    }
    if (!box) throw new Error(`The ${clips[c]!.action} animation is empty${key ? ' once its background is taken out (lower how close a colour must be)' : ''}.`);
    boxes.push({ box, scale: clips[c]!.scale });
  }
  const idle = Math.max(0, clips.findIndex((c) => c.action === 'idle'));
  const layout = layoutClips(boxes, idle, tall);

  // Then each frame drawn at its place in the character's frames, and written.
  const canvas = new OffscreenCanvas(layout.width, layout.height);
  const ctx = canvas.getContext('2d', { willReadFrequently: true })!;
  ctx.imageSmoothingQuality = 'high';
  const placedFrame = (c: number, f: ImageBitmap): RgbaFrame => {
    const p = layout.placements[c]!;
    const pixels = pixelsOf(f, key);
    const source = new OffscreenCanvas(pixels.width, pixels.height);
    source.getContext('2d')!.putImageData(new ImageData(new Uint8ClampedArray(pixels.data), pixels.width, pixels.height), 0, 0);
    ctx.clearRect(0, 0, layout.width, layout.height);
    const w = p.box.right - p.box.left;
    const h = p.box.bottom - p.box.top;
    ctx.drawImage(source, p.box.left, p.box.top, w, h, p.x, p.y, w * p.scale, h * p.scale);
    return { data: ctx.getImageData(0, 0, layout.width, layout.height).data, width: layout.width, height: layout.height };
  };
  const out: PackClip[] = [];
  done = 0;
  for (let c = 0; c < clips.length; c++) {
    const frames: Uint8Array[] = [];
    const list = kept[c]!;
    if (fullColour) {
      for (const f of list) {
        const placed = placedFrame(c, f);
        const one = new OffscreenCanvas(placed.width, placed.height);
        one.getContext('2d')!.putImageData(new ImageData(new Uint8ClampedArray(placed.data), placed.width, placed.height), 0, 0);
        frames.push(new Uint8Array(await (await one.convertToBlob({ type: 'image/png' })).arrayBuffer()));
        onProgress?.('Making the frames', ++done / total);
      }
    } else {
      const step = Math.max(1, list.length / PALETTE_FRAMES);
      const samples: RgbaFrame[] = [];
      for (let i = 0; i < list.length; i += step) samples.push(placedFrame(c, list[Math.floor(i)]!));
      const palette = paletteOf(samples);
      const nearest = nearestCache();
      for (const f of list) {
        frames.push(await encodePng8(indexFrame(placedFrame(c, f), palette, nearest), layout.width, layout.height, palette));
        onProgress?.('Making the frames', ++done / total);
      }
    }
    out.push({ action: clips[c]!.action, fps: Math.max(1, Math.min(60, Math.round(clips[c]!.fps))), frames });
  }
  const pack: CharacterPack = { name: name.trim().slice(0, 60) || 'Character', width: layout.width, height: layout.height, anchor: layout.anchor, tall: Math.round(layout.tall * 100) / 100, faces, clips: out };
  const bytes = writePack(pack);
  if (bytes.length > PACK_LIMITS.bytes) throw new Error('That character is too large: use fewer or shorter animations, or 256 colours.');
  return { pack, bytes };
}
