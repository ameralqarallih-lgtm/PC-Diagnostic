import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiGet, apiRequest } from '@/lib/api/client';
import type { AnimationsAnswer, CharacterAnimation } from '@/lib/api/types';
import type { CharacterInfo } from '@/lib/api/types';
import { clipFor, clipFrame, closeTo, readPack, type CharacterPack, type PackClip } from './pack';
import { emitAt, emitsBehind, poseAt, onGround, propInfo, type PropInfo, type PropPose, type PropShown } from './props';
import { drawParticles, type Particle } from './particles';
import { bodyOf, type Body } from './tops';

/**
 * The characters the panels may have: those shipped with PC Diagnostic and those the user imported (files of the
 * picture library), and their frames as the editor plays them.
 */
export function useCharacters() {
  return useQuery({ queryKey: ['overlay-characters'], queryFn: ({ signal }) => apiGet<CharacterInfo[]>('/overlay/characters', signal), staleTime: 30_000 });
}

/** Removes an imported character (here and in every storage location); refused while a panel has it. */
export function useDeleteCharacter() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => apiRequest('DELETE', `/overlay/characters/${encodeURIComponent(id)}`),
    onSuccess: () => client.invalidateQueries({ queryKey: ['overlay-characters'] }),
  });
}

/** The heights frames are decoded at (each drawn scaled from the nearest one at least as tall): a size change rarely decodes again. */
const STEPS = [48, 96, 160, 256, 384];

/** The decoded height for a shown height: the step at or above it, at most the frames' own. */
const stepFor = (pixelHeight: number, own: number) => Math.min(own, STEPS.find((h) => h >= pixelHeight) ?? own);

/**
 * How long a decoded height no longer drawn is kept, in ms. Only heights not drawn for that long are let go: the same
 * character is often shown at three (its list preview, the screen, the picker), and keeping two let go of one still
 * drawn, decoding it again and again (the characters flickered) and drawing let-go frames as nothing.
 */
const HEIGHT_KEPT_MS = 10_000;

/**
 * Every character's frames are decoded through one queue, a few at a time, so the page keeps answering (eight
 * characters shown at once in the picker hung it while each decoded all its frames together). A clip being shown goes
 * first; the rest of a character's clips follow in the background.
 */
const DECODING_AT_ONCE = 3;

/**
 * Decoding is done by background workers, off the page's thread (adding a character hung the page while its frames
 * were decoded on it); where there are none (tests), by the page.
 */
const WORKERS = 2;
const workers: Worker[] = [];
interface Decoded {
  bitmap: ImageBitmap | null;
  /** The body in it (null: not measured). */
  body: Body | null;
}
const answers = new Map<number, (decoded: Decoded) => void>();
let asks = 0;

/** A frame decoded at a size, and the body in it when asked with a head's width (`wide`, pixels). */
function decode(png: Uint8Array, width: number, height: number, wide?: number): Promise<Decoded> {
  if (typeof Worker === 'undefined') {
    if (typeof createImageBitmap === 'undefined') return Promise.resolve({ bitmap: null, body: null });
    return createImageBitmap(new Blob([png as BlobPart], { type: 'image/png' }), { resizeWidth: width, resizeHeight: height, resizeQuality: 'high' })
      .then((bitmap) => ({ bitmap, body: wide ? bodyOf(bitmap, wide) : null }))
      .catch(() => ({ bitmap: null, body: null }));
  }
  if (!workers.length) {
    for (let i = 0; i < WORKERS; i++) {
      const worker = new Worker(new URL('./decoder.worker.ts', import.meta.url), { type: 'module' });
      worker.onmessage = (e: MessageEvent<{ id: number; bitmap: ImageBitmap | null; body?: Body | null }>) => {
        answers.get(e.data.id)?.({ bitmap: e.data.bitmap, body: e.data.body ?? null });
        answers.delete(e.data.id);
      };
      workers.push(worker);
    }
  }
  const id = ++asks;
  // A copy of the PNG's bytes, handed over (the pack keeps its own).
  const bytes = png.slice().buffer;
  return new Promise((resolve) => {
    answers.set(id, resolve);
    workers[id % workers.length]!.postMessage({ id, png: bytes, width, height, wide }, [bytes]);
  });
}
const queue: (() => Promise<void>)[] = [];
let running = 0;

function pump() {
  while (running < DECODING_AT_ONCE && queue.length) {
    const job = queue.shift()!;
    running++;
    void job().finally(() => {
      running--;
      // Between jobs, the page draws.
      setTimeout(pump, 0);
    });
  }
}

function enqueue(job: () => Promise<void>, first: boolean) {
  if (first) queue.unshift(job);
  else queue.push(job);
  pump();
}

/**
 * A character's frames as the editor plays them: decoded in the background when a height is first needed (the clip
 * shown first, then every other one, so even its first turn plays smoothly), at a few steps of height and drawn scaled
 * from them (so resizing it rarely decodes again). Until a frame is ready, the nearest one ready is drawn instead.
 */
export class CharacterFrames {
  private readonly kept = new Map<number, Map<string, ImageBitmap | null>>();
  /** The clips asked for at each height (`action`), and the whole character (`*`). */
  private readonly asked = new Map<number, Set<string>>();
  private readonly waiting = new Set<() => void>();
  private readonly whole = new Map<number, Promise<void>>();
  /** When each height was last drawn. */
  private readonly used = new Map<number, number>();

  /** Its new animations' clips (made by the generator: a file each, at its own height), by action: played for an action its own file does not have. */
  readonly extras = new Map<string, CharacterFrames>();

  /** Its body in each frame decoded (`action/index`), in its file's pixels (top -1: none found). */
  private readonly bodies = new Map<string, Body>();

  constructor(readonly pack: CharacterPack) {}

  /** The frames playing an action: a new animation's, when its own file does not have it. */
  private extraFor(action: string): CharacterFrames | null {
    return this.pack.clips.some((c) => c.action === action) ? null : (this.extras.get(action) ?? null);
  }

  /** Whether it has a clip of its own for an action (in its file, or a new animation's). */
  has(action: string): boolean {
    return this.pack.clips.some((c) => c.action === action) || this.extras.has(action);
  }

  /** What it plays for an action: that one, or the closest it has, its new animations' clips counted (idle when nothing is closer). */
  plays(action: string): string {
    // As the recorder: the closest it has, its new animations counted.
    return closeTo(action).find((a) => this.has(a)) ?? 'idle';
  }

  /** The size of a frame shown `size` pixels tall (the character's standing height), and where its feet are. */
  layout(size: number) {
    const k = size / this.pack.tall;
    return { width: this.pack.width * k, height: this.pack.height * k, anchorX: this.pack.anchor[0] * k, anchorY: this.pack.anchor[1] * k };
  }

  private framesAt(step: number) {
    let frames = this.kept.get(step);
    if (!frames) {
      frames = new Map();
      this.kept.set(step, frames);
      this.asked.set(step, new Set());
      // Heights not drawn for a while let go.
      const now = performance.now();
      for (const [other, old] of [...this.kept]) {
        if (other === step || now - (this.used.get(other) ?? 0) < HEIGHT_KEPT_MS) continue;
        for (const bitmap of old.values()) bitmap?.close();
        this.kept.delete(other);
        this.asked.delete(other);
        this.whole.delete(other);
        this.used.delete(other);
      }
    }
    return frames;
  }

  /**
   * Calls those waiting for frames, once, on the next animation frame: the ones waiting now only (a redraw that waits
   * again is called next time; waking each as it re-added itself hung the page).
   */
  private wake() {
    if (this.waking) return;
    this.waking = true;
    const later = typeof requestAnimationFrame !== 'undefined' ? requestAnimationFrame : (f: () => void) => setTimeout(f, 0);
    later(() => {
      this.waking = false;
      const waiting = [...this.waiting];
      this.waiting.clear();
      for (const ready of waiting) ready();
    });
  }

  private waking = false;

  /** Queues a clip's frames at a height (`first`: before what waits), once. */
  private want(step: number, clip: PackClip, first: boolean): Promise<void> {
    const frames = this.framesAt(step);
    const asked = this.asked.get(step)!;
    if (asked.has(clip.action) || (typeof createImageBitmap === 'undefined' && typeof Worker === 'undefined')) return Promise.resolve();
    asked.add(clip.action);
    const width = Math.max(1, Math.round((this.pack.width * step) / this.pack.height));
    // A head is at least a quarter of its height across (as the recorder measures it).
    const wide = Math.max(2, Math.trunc((0.24 * this.pack.tall * step) / this.pack.height));
    const k = this.pack.height / step;
    const jobs = clip.frames.map(
      (png, index) => () =>
        decode(png, width, step, wide).then(({ bitmap, body }) => {
          const key = `${clip.action}/${index}`;
          if (body && !this.bodies.has(key)) this.bodies.set(key, { top: body.top < 0 ? -1 : body.top * k, middle: body.middle * k, left: body.left * k, right: body.right * k });
          // Let go meanwhile (another height): not kept.
          if (this.kept.get(step) !== frames) bitmap?.close();
          else frames.set(key, bitmap);
        }),
    );
    let done = 0;
    return new Promise<void>((resolve) => {
      const all = jobs.map((job) => async () => {
        await job();
        if (++done === jobs.length) {
          this.wake();
          resolve();
        }
      });
      // In order, the first frames first.
      if (first) for (const job of [...all].reverse()) enqueue(job, true);
      else for (const job of all) enqueue(job, false);
    });
  }

  /** Decodes every frame at the height used for `pixelHeight` (once); resolves when they are all ready. */
  prepare(pixelHeight: number, first?: string): Promise<void> {
    const step = stepFor(Math.max(1, Math.round(pixelHeight)), this.pack.height);
    const shown = first ? clipFor(this.pack, first) : null;
    // Asked every frame drawn: nothing to do once all are asked for.
    const whole = this.whole.get(step);
    if (whole && this.kept.has(step) && (!shown || this.asked.get(step)?.has(shown.action))) return whole;
    const all = Promise.all([...(shown ? [this.want(step, shown, true)] : []), ...this.pack.clips.filter((c) => c !== shown).map((c) => this.want(step, c, false))]).then(() => undefined);
    this.whole.set(step, whole ? Promise.all([whole, all]).then(() => undefined) : all);
    return this.whole.get(step)!;
  }

  /**
   * The frame for an action `seconds` into it, `pixelHeight` device pixels high; null while nothing of it is decoded yet
   * (`ready` is called as frames are).
   */
  frame(action: string, seconds: number, pixelHeight: number, ready?: () => void): ImageBitmap | null {
    action = this.plays(action);
    const extra = this.extraFor(action);
    if (extra) return extra.frame(action, seconds, pixelHeight, ready);
    return this.frameOf(clipFor(this.pack, action), seconds, pixelHeight, ready);
  }

  private frameOf(clip: PackClip, seconds: number, pixelHeight: number, ready?: () => void): ImageBitmap | null {
    const index = clipFrame(clip, seconds);
    const step = stepFor(Math.max(1, Math.round(pixelHeight)), this.pack.height);
    const key = `${clip.action}/${index}`;
    const here = this.kept.get(step);
    this.used.set(step, performance.now());
    void this.prepare(pixelHeight, clip.action);
    const found = here?.get(key);
    if (found) return found;
    if (ready) this.waiting.add(ready);
    // Not ready yet: the same frame at another height, else the nearest frame of the clip ready at this one.
    for (const other of this.kept.values()) {
      const frame = other.get(key);
      if (frame) return frame;
    }
    const frames = this.kept.get(step);
    for (let d = 1; frames && d < clip.frames.length; d++) {
      const near = frames.get(`${clip.action}/${(index - d + clip.frames.length) % clip.frames.length}`);
      if (near) return near;
    }
    return null;
  }

  /**
   * Where the top of its head is from its feet in the frame shown for an action `seconds` into it (as decoded: it sits,
   * jumps and bends, and what is on its head goes with it), `size` pixels tall facing a way; its usual head until that
   * frame is decoded.
   */
  headAt(action: string, seconds: number, facing: 1 | -1, size: number): [number, number] {
    action = this.plays(action);
    const extra = this.extraFor(action);
    if (extra) return extra.headAt(action, seconds, facing, size);
    const clip = clipFor(this.pack, action);
    const body = this.bodies.get(`${clip.action}/${clipFrame(clip, seconds)}`);
    if (!body || body.top < 0) return this.headFromFeet(size, facing);
    const k = size / this.pack.tall;
    // Its frames are drawn turned when it faces the other way.
    const dx = (body.middle - this.pack.anchor[0]) * k;
    return [facing !== this.pack.faces ? -dx : dx, (body.top - this.pack.anchor[1]) * k];
  }

  /** How far its body reaches from its feet the way it faces, `size` pixels tall (as it stands: its first idle frame, once decoded; a third of its size until then). */
  reachAt(facing: 1 | -1, size: number): number {
    const body = this.bodies.get(`${clipFor(this.pack, 'idle').action}/0`);
    if (!body || body.top < 0) return size * 0.3;
    const k = size / this.pack.tall;
    // The way it faces is its drawing's right when it is drawn as it is facing right (or turned facing left).
    const right = (facing === 1) === (facing === this.pack.faces) ? body.right - this.pack.anchor[0] : this.pack.anchor[0] - body.left;
    return right * k;
  }

  /**
   * A prop's part drawn behind the character (`behind`: an umbrella, a siren's glow) or in front of it: its frame
   * posed (`pose`, from the feet at (x, y) on the canvas, `k` canvas pixels to a pixel), and what it gives off.
   */
  drawProp(ctx: CanvasRenderingContext2D, prop: PropInfo, pose: PropPose, particles: readonly Particle[], facing: 1 | -1, size: number, x: number, y: number, k: number, behind: boolean, ready?: () => void) {
    const ox = x + pose.x * k;
    const oy = y + pose.y * k;
    ctx.save();
    ctx.globalAlpha *= pose.alpha;
    if (emitsBehind(prop) === behind) drawParticles(ctx, particles, ox, oy, k);
    const clip = this.pack.clips.find((c) => c.action === prop.id);
    if (clip && !!prop.behind === behind) {
      ctx.translate(ox, oy);
      ctx.rotate(pose.angle);
      ctx.scale(pose.scale, pose.scale);
      this.drawClip(ctx, clip, 0, clip.mirror !== false && facing !== this.pack.faces, size * k, 0, 0, ready);
    }
    ctx.restore();
  }

  /** Where the top of its head is from its feet, `size` pixels tall, facing a way. */
  headFromFeet(size: number, facing: 1 | -1): [number, number] {
    const k = size / this.pack.tall;
    const [hx, hy] = this.pack.head ?? [this.pack.anchor[0], Math.max(0, this.pack.anchor[1] - this.pack.tall)];
    const dx = (hx - this.pack.anchor[0]) * k;
    return [facing !== this.pack.faces ? -dx : dx, (hy - this.pack.anchor[1]) * k];
  }

  /** Draws the character with its feet at (x, y) in `ctx` (device pixels), `size` device pixels tall, facing a way. */
  draw(ctx: CanvasRenderingContext2D, action: string, seconds: number, facing: 1 | -1, size: number, x: number, y: number, ready?: () => void): boolean {
    action = this.plays(action);
    const extra = this.extraFor(action);
    if (extra) return extra.draw(ctx, action, seconds, facing, size, x, y, ready);
    return this.drawClip(ctx, clipFor(this.pack, action), seconds, facing !== this.pack.faces, size, x, y, ready);
  }

  /**
   * Draws an effect of the effects' file for a character (`body`) whose feet are at (x, y): at the top of its head or
   * at its feet, turned with it when the effect turns.
   */
  drawEffect(ctx: CanvasRenderingContext2D, effect: string, body: CharacterFrames, seconds: number, facing: 1 | -1, size: number, x: number, y: number, ready?: () => void): boolean {
    const clip = this.pack.clips.find((c) => c.action === effect);
    if (!clip) return false;
    const [hx, hy] = clip.at === 'head' ? body.headFromFeet(size, facing) : [0, 0];
    return this.drawClip(ctx, clip, seconds, clip.mirror !== false && facing !== this.pack.faces, size, x + hx, y + hy, ready);
  }

  private drawClip(ctx: CanvasRenderingContext2D, clip: PackClip, seconds: number, mirrored: boolean, size: number, x: number, y: number, ready?: () => void): boolean {
    const { width, height, anchorX, anchorY } = this.layout(size);
    const bitmap = this.frameOf(clip, seconds, height, ready);
    if (!bitmap) return false;
    ctx.save();
    ctx.translate(x, y);
    if (mirrored) ctx.scale(-1, 1);
    try {
      ctx.drawImage(bitmap, -anchorX, -anchorY, width, height);
    } catch {
      // A frame let go meanwhile: the next one is drawn.
    }
    ctx.restore();
    return true;
  }
}

const loaded = new Map<string, Promise<CharacterFrames | null>>();

/** The new animations as last read (null: not read yet), and their files already attached to a character. */
let animations: Promise<CharacterAnimation[]> | null = null;
const attached = new Map<string, Set<string>>();

/** The new animations (read once; again after {@link refreshCharacterAnimations}). */
function animationList(): Promise<CharacterAnimation[]> {
  animations ??= apiGet<AnimationsAnswer>('/overlay/animations')
    .then((a) => a.animations)
    .catch(() => []);
  return animations;
}

/** A character's new animations made so far, attached to its frames (each file once). */
async function attachAnimations(id: string, frames: CharacterFrames) {
  const done = attached.get(id) ?? new Set<string>();
  attached.set(id, done);
  for (const a of await animationList()) {
    const clip = a.clips[id];
    // Played: the animation and this character's clip both on, a file made (a clip made again plays its last one meanwhile).
    if (a.active === false || clip?.active === false || !clip?.file || done.has(clip.file)) continue;
    done.add(clip.file);
    try {
      const r = await fetch(`/api/v1/overlay/animations/${encodeURIComponent(a.id)}/${encodeURIComponent(id)}`, { credentials: 'same-origin' });
      const pack = r.ok ? readPack(await r.arrayBuffer(), { extra: true }) : null;
      if (pack?.clips.some((c) => c.action === a.id)) frames.extras.set(a.id, new CharacterFrames(pack));
      else done.delete(clip.file);
    } catch {
      done.delete(clip.file);
    }
  }
  // Removed (or turned off) let go, and a clip made again replaced.
  const playing = new Map((await animationList()).filter((a) => a.active !== false && a.clips[id]?.active !== false && a.clips[id]?.file).map((a) => [a.id, a.clips[id]!.file!]));
  for (const action of [...frames.extras.keys()]) if (!playing.has(action)) frames.extras.delete(action);
  for (const file of [...done]) if (![...playing.values()].includes(file)) done.delete(file);
}

/** The new animations changed (one made, one removed): every character loaded takes them again. */
export function refreshCharacterAnimations() {
  animations = null;
  for (const [id, found] of loaded) void found.then((f) => f && attachAnimations(id, f));
}

/** A character's frames by id (read once), with its new animations. */
export function loadCharacter(id: string): Promise<CharacterFrames | null> {
  let found = loaded.get(id);
  if (!found) {
    found = fetch(`/api/v1/overlay/characters/${encodeURIComponent(id)}`, { credentials: 'same-origin' })
      .then(async (r) => (r.ok ? readPack(await r.arrayBuffer()) : null))
      .then((pack) => (pack ? new CharacterFrames(pack) : null))
      .then(async (frames) => {
        if (frames) await attachAnimations(id, frames).catch(() => undefined);
        return frames;
      })
      .catch(() => null);
    found.then((f) => {
      // Not there (yet): asked again next time.
      if (!f) loaded.delete(id);
    });
    loaded.set(id, found);
  }
  return found;
}

let effects: Promise<CharacterFrames | null> | null = null;

/** The reactions' effects (read once). */
let props: Promise<CharacterFrames | null> | null = null;

/** The props' frames (read once; a clip per prop). */
export function loadProps(): Promise<CharacterFrames | null> {
  props ??= fetch('/api/v1/overlay/props', { credentials: 'same-origin' })
    .then(async (r) => (r.ok ? readPack(await r.arrayBuffer(), { effects: true }) : null))
    .then((pack) => (pack ? new CharacterFrames(pack) : null))
    .catch(() => null)
    .then((f) => {
      if (!f) props = null;
      return f;
    });
  return props;
}

/** A character's props to draw at a moment (the one shown and the one going), each posed for this frame, with what it gives off. */
export function propsOf(body: CharacterFrames, shown: PropShown | null, action: string, seconds: number, facing: 1 | -1, size: number, now: number, moving: boolean) {
  if (!shown || (!shown.id && !shown.going)) return [];
  const [hx, hy] = body.headAt(action, seconds, facing, size);
  const reach = body.reachAt(facing, size);
  const list: { prop: PropInfo; pose: PropPose; particles: Particle[] }[] = [];
  for (const [id, age, gone] of [
    [shown.going, shown.goingAge, shown.gone],
    [shown.id, shown.age, -1],
  ] as const) {
    const prop = id ? propInfo(id) : undefined;
    if (!prop) continue;
    const pose = poseAt(prop, age, gone, now, facing, size, hx, hy, reach, moving && onGround(prop));
    if (pose.alpha > 0.01) list.push({ prop, pose, particles: emitAt(prop, now, facing, size) });
  }
  return list;
}

export function loadEffects(): Promise<CharacterFrames | null> {
  effects ??= fetch('/api/v1/overlay/effects', { credentials: 'same-origin' })
    .then(async (r) => (r.ok ? readPack(await r.arrayBuffer(), { effects: true }) : null))
    .then((pack) => (pack ? new CharacterFrames(pack) : null))
    .catch(() => null)
    .then((f) => {
      if (!f) effects = null;
      return f;
    });
  return effects;
}

/** Puts a character's frames in place without fetching them (one just imported; tests). */
export function keepCharacter(id: string, pack: CharacterPack) {
  loaded.set(id, Promise.resolve(new CharacterFrames(pack)));
}
