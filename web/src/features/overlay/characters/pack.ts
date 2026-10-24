/**
 * Animated characters (".char"), as the recorder reads them (CharacterPack.cs): a clip of transparent PNG frames for
 * each thing the character does, every frame the same size, with the point its feet stand on and how tall it stands.
 *
 *  0 "PCDC" · 4 version 1 · 5 reserved · 6 the manifest's length (4 bytes) · 10 the manifest (UTF-8 JSON)
 *  then every clip's frames (PNG) one after another, in the manifest's order (all little-endian)
 *
 * The manifest: {name, width, height, anchor: [x, y], head?: [x, y], tall, faces: 1 | -1, clips: [{action, fps, frames:
 * [lengths]}]}; the anchor (its feet), its head (the top of it, where effects go) and how tall it stands are in the
 * frames' pixels, and `faces` is the way the drawing looks (1: right). The reactions' effects are such a file too: a
 * clip per effect, each saying what it is attached to (`at`: head or feet) and whether it turns with the character.
 *
 * No imports: the character tools (Node) use it as it is.
 */

export interface PackClip {
  action: string;
  fps: number;
  /** Each frame's PNG. */
  frames: Uint8Array[];
  /** An effect's: what it is attached to. */
  at?: 'head' | 'feet';
  /** An effect's: whether it turns with the character. */
  mirror?: boolean;
  /** The first frame repeated (absent: all): the frames before it play once (sitting down), then the rest repeats. */
  loop?: number;
  /** The last frame repeated (absent: the clip's last). */
  end?: number;
  /** The repeated frames play forwards then back. */
  bounce?: boolean;
}

export interface CharacterPack {
  name: string;
  width: number;
  height: number;
  anchor: [number, number];
  /** The top of its head (where effects go); when missing, the top of its height above its feet. */
  head?: [number, number];
  tall: number;
  faces: 1 | -1;
  clips: PackClip[];
}

/** What a character may do: the recorder's list (OverlayCharacters.Actions). */
export const PACK_ACTIONS = ['idle', 'walk', 'run', 'sit', 'sleep', 'jump', 'play', 'dance', 'held', 'land'] as const;

/** The recorder's limits. */
export const PACK_LIMITS = { bytes: 48 * 1024 * 1024, side: 1024, frames: 300, totalFrames: 1200 } as const;

const PNG = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

/** Packs a character into its file's bytes. */
export function writePack(pack: CharacterPack): Uint8Array {
  const manifest = new TextEncoder().encode(
    JSON.stringify({
      name: pack.name,
      width: pack.width,
      height: pack.height,
      anchor: pack.anchor,
      ...(pack.head ? { head: pack.head } : {}),
      tall: pack.tall,
      faces: pack.faces,
      clips: pack.clips.map((c) => ({
        action: c.action,
        fps: c.fps,
        frames: c.frames.map((f) => f.length),
        ...(c.at ? { at: c.at, mirror: c.mirror ?? true } : {}),
        ...(c.loop != null ? { loop: c.loop } : {}),
        ...(c.end != null ? { end: c.end } : {}),
        ...(c.bounce ? { bounce: true } : {}),
      })),
    }),
  );
  const frames = pack.clips.flatMap((c) => c.frames);
  const out = new Uint8Array(10 + manifest.length + frames.reduce((n, f) => n + f.length, 0));
  out.set([0x50, 0x43, 0x44, 0x43, 1, 0], 0);
  new DataView(out.buffer).setUint32(6, manifest.length, true);
  out.set(manifest, 10);
  let at = 10 + manifest.length;
  for (const f of frames) {
    out.set(f, at);
    at += f.length;
  }
  return out;
}

/** Reads a character's file (or, with `effects`, the effects' file); null when it is not a sound one (the recorder's rules). */
/** A new animation's name (made by the generator for every character): a few lowercase letters, digits and dashes. */
export const ANIMATION_ID = /^[a-z][a-z0-9-]{1,23}$/;

/**
 * Reads a character's file (null when it is not a sound one). `effects`: the effects' file (a clip per effect).
 * `extra`: a new animation's file for a character (its clips alone, no idle).
 */
export function readPack(data: ArrayBuffer | Uint8Array, options: { effects?: boolean; extra?: boolean } = {}): CharacterPack | null {
  // An effect's name is checked by its form (the app reads only its own effects' file); an action is one of the list,
  // or a new animation's name.
  const isAction = options.effects ? (a: string) => /^[a-z][a-z0-9-]{0,30}$/.test(a) : (a: string) => (PACK_ACTIONS as readonly string[]).includes(a) || ANIMATION_ID.test(a);
  const bytes = data instanceof Uint8Array ? data : new Uint8Array(data);
  if (bytes.length < 10 || bytes.length > PACK_LIMITS.bytes || String.fromCharCode(...bytes.subarray(0, 4)) !== 'PCDC' || bytes[4] !== 1) return null;
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const length = view.getUint32(6, true);
  if (length < 2 || 10 + length > bytes.length) return null;
  let m: { name?: unknown; width?: unknown; height?: unknown; anchor?: unknown; head?: unknown; tall?: unknown; faces?: unknown; clips?: unknown };
  try {
    m = JSON.parse(new TextDecoder().decode(bytes.subarray(10, 10 + length)));
  } catch {
    return null;
  }
  const num = (v: unknown): v is number => typeof v === 'number' && Number.isFinite(v);
  const { name, width, height, anchor, head, tall, faces, clips } = m;
  if (typeof name !== 'string' || name.trim().length < 1 || name.trim().length > 60) return null;
  if (!num(width) || !num(height) || width < 1 || height < 1 || width > PACK_LIMITS.side || height > PACK_LIMITS.side) return null;
  if (!Array.isArray(anchor) || anchor.length !== 2 || !num(anchor[0]) || !num(anchor[1]) || anchor[0] < 0 || anchor[0] > width || anchor[1] < 0 || anchor[1] > height) return null;
  if (!num(tall) || tall < 8 || tall > height || (faces !== 1 && faces !== -1)) return null;
  if (head !== undefined && (!Array.isArray(head) || head.length !== 2 || !num(head[0]) || !num(head[1]) || head[0] < 0 || head[0] > width || head[1] < 0 || head[1] > height)) return null;
  if (!Array.isArray(clips) || clips.length < 1 || clips.length > 32) return null;
  const seen = new Set<string>();
  let total = 0;
  let at = 10 + length;
  const out: PackClip[] = [];
  for (const c of clips as { action?: unknown; fps?: unknown; frames?: unknown; at?: unknown; mirror?: unknown; loop?: unknown; end?: unknown; bounce?: unknown }[]) {
    const { action, fps, frames, at: attached, mirror, loop, end, bounce } = c;
    if (typeof action !== 'string' || !isAction(action) || seen.has(action)) return null;
    if (attached !== undefined && attached !== 'head' && attached !== 'feet') return null;
    if (!num(fps) || fps < 1 || fps > 60 || !Array.isArray(frames) || frames.length < 1 || frames.length > PACK_LIMITS.frames) return null;
    seen.add(action);
    total += frames.length;
    const list: Uint8Array[] = [];
    for (const n of frames) {
      if (!num(n) || n < 33 || at + n > bytes.length) return null;
      const f = bytes.subarray(at, at + n);
      const fv = new DataView(f.buffer, f.byteOffset, f.byteLength);
      if (PNG.some((b, i) => f[i] !== b) || String.fromCharCode(...f.subarray(12, 16)) !== 'IHDR' || fv.getInt32(16) !== width || fv.getInt32(20) !== height) return null;
      list.push(f);
      at += n;
    }
    const count = list.length;
    if ((loop != null && (!Number.isInteger(loop) || (loop as number) < 0 || (loop as number) >= count)) || (end != null && (!Number.isInteger(end) || (end as number) < ((loop as number | undefined) ?? 0) || (end as number) >= count))) return null;
    out.push({
      action,
      fps,
      frames: list,
      ...(attached ? { at: attached as 'head' | 'feet', mirror: mirror !== false } : {}),
      ...(loop != null ? { loop: loop as number } : {}),
      ...(end != null ? { end: end as number } : {}),
      ...(bounce === true ? { bounce: true } : {}),
    });
  }
  if ((!options.effects && !options.extra && !seen.has('idle')) || total > PACK_LIMITS.totalFrames || at !== bytes.length) return null;
  return { name: name.trim(), width, height, anchor: [anchor[0], anchor[1]], ...(head ? { head: [head[0], head[1]] as [number, number] } : {}), tall, faces, clips: out };
}

/** The clip played for an action: that one, or the closest the character has (as the recorder picks it). */
export function clipFor(pack: Pick<CharacterPack, 'clips'>, action: string): PackClip {
  for (const a of closeTo(action)) {
    const clip = pack.clips.find((c) => c.action === a);
    if (clip) return clip;
  }
  return pack.clips.find((c) => c.action === 'idle') ?? pack.clips[0]!;
}

/** An action and what comes closest to it, in order (idle last): what a character without it does instead (as the recorder). */
export function closeTo(action: string): string[] {
  const order: Record<string, string[]> = {
    dance: ['dance', 'play', 'jump', 'idle'],
    play: ['play', 'dance', 'idle'],
    run: ['run', 'walk', 'idle'],
    walk: ['walk', 'run', 'idle'],
    sleep: ['sleep', 'sit', 'idle'],
    sit: ['sit', 'sleep', 'idle'],
    jump: ['jump', 'play', 'idle'],
    // The emotions (made by the generator) until a character has them: the closest it has (as the recorder).
    worried: ['worried', 'sit', 'idle'],
    panic: ['panic', 'jump', 'idle'],
    tired: ['tired', 'sit', 'sleep', 'idle'],
    cheer: ['cheer', 'dance', 'play', 'jump', 'idle'],
  };
  return order[action] ?? [action, 'idle'];
}

/** The frame shown `seconds` into a clip: once through its opening, then its repeated part (as the recorder). */
export const clipFrame = (clip: Pick<PackClip, 'fps' | 'frames' | 'loop' | 'end' | 'bounce'>, seconds: number) => {
  const n = clip.frames.length;
  if (n === 0) return 0;
  const i = Math.floor(seconds * clip.fps);
  if (clip.loop == null && !clip.bounce) return ((i % n) + n) % n;
  const start = Math.min(n - 1, Math.max(0, clip.loop ?? 0));
  const end = Math.min(n - 1, Math.max(start, clip.end ?? n - 1));
  if (i >= 0 && i < start) return i;
  const length = end - start + 1;
  const j = i - start;
  if (!clip.bounce || length < 2) return start + (((j % length) + length) % length);
  const period = 2 * length - 2;
  const p = ((j % period) + period) % period;
  return start + (p < length ? p : period - p);
};
