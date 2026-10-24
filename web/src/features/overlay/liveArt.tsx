import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import type { OverlayPanel } from '@/lib/api/types';
import { frameAt, jpegOf, packAnimation, readAnimation } from './anim';
import { artPicture, panelGeometry } from './art';
import { GENERATIVE, GENERATIVE_KINDS, GenerativeRenderer, type GenerativeKind, type GlassCard, type GlassDial } from './generative';
import { imageUrl } from './model';

/**
 * Generated and glass art, and animated pictures, in the editor: drawn live (one shared GPU renderer for every
 * preview, each preview at most 20 times a second, none off screen, a gallery's only while the mouse is on it), and
 * baked into the picture saved for the recorder (an animation of {@link LOOP_SECONDS} at {@link BAKE_FPS}, or a still).
 */

/** How long a generated scene's loop lasts, and the frames a second it is baked at for the recorder. */
export const LOOP_SECONDS = 8;
export const BAKE_FPS = 12;

export const isGenerative = (art: string | null | undefined): art is GenerativeKind => !!art && (GENERATIVE_KINDS as readonly string[]).includes(art);
export const isAnimationId = (id: string | null | undefined) => !!id && id.endsWith('.anim');

/** Whether a panel's background is drawn live here (a generated scene, glass over a picture, or an animation). */
export const drawsLive = (panel: Pick<OverlayPanel, 'style'>) => isGenerative(panel.style.art) || panel.style.art === 'glass' || (!panel.style.art && isAnimationId(panel.style.image));

/** A panel's cards and dials (its tiles), in canvas pixels at `k` canvas pixels to a panel pixel. */
export function glassLayout(panel: Pick<OverlayPanel, 'layout' | 'columns' | 'readings' | 'style' | 'title' | 'width' | 'height'>, k: number): { cards: GlassCard[]; dials: GlassDial[] } {
  const g = panelGeometry(panel);
  if (!g) return { cards: [], dials: [] };
  return {
    cards: g.tiles.map((t) => ({ x: t.x * k, y: t.y * k, w: t.w * k, h: t.h * k })),
    dials: g.tiles.filter((t) => t.dial).map((t) => ({ cx: t.dial!.cx * k, cy: t.dial!.cy * k, r: (t.dial!.d / 2) * k, track: t.dial!.track * k, start: t.dial!.start, sweep: t.dial!.sweep })),
  };
}

let shared: GenerativeRenderer | null | undefined;
/**
 * The renderer every preview and bake shares (a page may hold only a few GPU contexts); null without WebGL2. Made
 * again when the GPU takes its context back (a driver reset).
 */
export function sharedRenderer(): GenerativeRenderer | null {
  if (shared === undefined) {
    try {
      shared = typeof document !== 'undefined' && GenerativeRenderer.supported() ? new GenerativeRenderer() : null;
      shared?.canvas.addEventListener('webglcontextlost', () => (shared = undefined), { once: true });
    } catch {
      shared = null;
    }
  }
  return shared;
}

/** A library picture ready to draw: a still (one frame) or an animation's frames. */
export interface PictureSource {
  width: number;
  height: number;
  fps: number;
  frames: ImageBitmap[];
}

const loaded = new Map<string, Promise<PictureSource | null>>();
/** Loads a library picture to draw (once per id). */
export function loadPicture(id: string): Promise<PictureSource | null> {
  let source = loaded.get(id);
  if (!source) {
    source = (async () => {
      try {
        const response = await fetch(imageUrl(id), { credentials: 'same-origin' });
        if (!response.ok) return null;
        if (isAnimationId(id)) {
          const animation = readAnimation(await response.arrayBuffer());
          if (!animation) return null;
          return { width: animation.width, height: animation.height, fps: animation.fps, frames: await Promise.all(animation.frames.map((f) => createImageBitmap(f))) };
        }
        const bitmap = await createImageBitmap(await response.blob());
        return { width: bitmap.width, height: bitmap.height, fps: 1, frames: [bitmap] };
      } catch {
        return null;
      }
    })();
    loaded.set(id, source);
  }
  return source;
}

const yieldToPage = () => new Promise((resolve) => setTimeout(resolve, 0));

export interface BakeOptions {
  /** How far it is, 0–1 (an animation takes a few seconds). */
  onProgress?: (done: number) => void;
  /** Asked between frames: true stops it (the panel changed again), and nothing is made. */
  cancelled?: () => boolean;
}

/**
 * The picture saved for the recorder from a panel's art: the drawn arts as before (art.ts); a generated scene as an
 * animation; glass over a library picture as a still, or an animation when the picture is one. Null when it cannot
 * be made (no fixed size, no picture, no WebGL2) or was cancelled.
 */
export async function bakeArt(panel: Pick<OverlayPanel, 'layout' | 'columns' | 'readings' | 'style' | 'title' | 'width' | 'height'>, options: BakeOptions = {}): Promise<Blob | null> {
  const art = panel.style.art;
  if (!art) return null;
  if (!isGenerative(art) && art !== 'glass') return artPicture(panel);
  if (panel.width == null || panel.height == null || !sharedRenderer()) return null;
  // At up to twice the panel's size (a scaled monitor draws it larger: 150 % stretches a 1× bake soft), at most
  // 1920 on its long side.
  const k = Math.min(2, 1920 / Math.max(panel.width, panel.height));
  const w = Math.max(16, Math.round(panel.width * k));
  const h = Math.max(16, Math.round(panel.height * k));
  const { cards, dials } = glassLayout(panel, k);
  const canvas = document.createElement('canvas');
  canvas.width = w;
  canvas.height = h;
  const ctx = canvas.getContext('2d')!;
  const glass = { radius: 18, unit: k, accent: panel.style.accent };
  // A large animation a little more compressed (a whole monitor's stays well under the 48 MB the recorder takes).
  const quality = w * h > 1_200_000 ? 0.78 : 0.86;
  // Each frame drawn and copied at once: the shared renderer draws previews between frames.
  const frame = (draw: (r: GenerativeRenderer) => void) => {
    const r = sharedRenderer();
    if (!r) return false;
    draw(r);
    ctx.drawImage(r.canvas, 0, 0);
    return true;
  };
  const animate = async (count: number, fps: number, draw: (r: GenerativeRenderer, i: number) => void) => {
    const frames: Blob[] = [];
    for (let i = 0; i < count; i++) {
      if (options.cancelled?.() || !frame((r) => draw(r, i))) return null;
      frames.push(await jpegOf(canvas, quality));
      options.onProgress?.((i + 1) / count);
      if (i % 3 === 2) await yieldToPage();
    }
    return options.cancelled?.() ? null : packAnimation(fps, w, h, frames);
  };
  if (isGenerative(art)) {
    const count = LOOP_SECONDS * BAKE_FPS;
    return animate(count, BAKE_FPS, (r, i) => r.render(art, w, h, i / count, cards, dials, glass));
  }
  const source = panel.style.artSource ? await loadPicture(panel.style.artSource) : null;
  if (!source || options.cancelled?.()) return null;
  if (source.frames.length === 1) {
    if (!frame((r) => r.renderPicture(source.frames[0]!, source.width, source.height, w, h, 0.12, cards, dials, glass))) return null;
    return jpegOf(canvas, 0.9);
  }
  const count = source.frames.length;
  return animate(count, source.fps, (r, i) => r.renderPicture(source.frames[i]!, source.width, source.height, w, h, i / count, cards, dials, glass));
}

// ---- Drawing live -----------------------------------------------------------------------------------------------

interface Live {
  draw: (now: number) => void;
  moving: () => boolean;
  last: number;
}
const live = new Set<Live>();
let frame = 0;
function tick(now: number) {
  for (const item of live) {
    if (item.moving() && now - item.last >= 50) {
      item.last = now;
      item.draw(now);
    }
  }
  frame = live.size ? requestAnimationFrame(tick) : 0;
}
function register(item: Live) {
  live.add(item);
  if (!frame && typeof requestAnimationFrame !== 'undefined') frame = requestAnimationFrame(tick);
  return () => live.delete(item);
}

const reducedMotion = () => typeof matchMedia !== 'undefined' && matchMedia('(prefers-reduced-motion: reduce)').matches;

/** Draws a picture into a canvas as the panel places it: cover, contain, stretch, or centred at its size. */
function place(ctx: CanvasRenderingContext2D, image: CanvasImageSource, iw: number, ih: number, w: number, h: number, fit: string) {
  if (fit === 'stretch') return ctx.drawImage(image, 0, 0, w, h);
  const k = fit === 'cover' ? Math.max(w / iw, h / ih) : fit === 'contain' ? Math.min(w / iw, h / ih) : 1;
  ctx.drawImage(image, (w - iw * k) / 2, (h - ih * k) / 2, iw * k, ih * k);
}

/**
 * A panel's live background: a generated scene with glass cards over its tiles, glass over a library picture, or a
 * plain animated picture. It fills the box it is in (the panel's background layer) and follows its size.
 */
export function LiveArt({ panel }: { panel: OverlayPanel }) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const [size, setSize] = useState<{ w: number; h: number } | null>(null);
  const [source, setSource] = useState<PictureSource | null>(null);
  const style = panel.style;
  const pictureId = style.art === 'glass' ? style.artSource : !style.art ? style.image : null;

  // Its size as shown (a shrunk preview is scaled down: its canvas is drawn at the size it shows, not its own).
  useLayoutEffect(() => {
    const element = canvas.current?.parentElement;
    if (!element) return undefined;
    const set = () => {
      const { width, height } = element.getBoundingClientRect();
      if (width < 1 || height < 1) return;
      setSize((old) => (old && Math.abs(old.w - width) < 1 && Math.abs(old.h - height) < 1 ? old : { w: width, h: height }));
    };
    set();
    // Once more when the preview around it has measured itself and shrunk.
    const again = typeof requestAnimationFrame !== 'undefined' ? requestAnimationFrame(set) : 0;
    const observer = typeof ResizeObserver !== 'undefined' ? new ResizeObserver(set) : null;
    observer?.observe(element);
    return () => {
      if (again) cancelAnimationFrame(again);
      observer?.disconnect();
    };
  }, []);

  useEffect(() => {
    let current = true;
    setSource(null);
    if (pictureId) void loadPicture(pictureId).then((s) => current && setSource(s));
    return () => {
      current = false;
    };
  }, [pictureId]);

  useEffect(() => {
    const element = canvas.current;
    if (!element || !size || size.w < 2 || size.h < 2) return undefined;
    // Sharp on a high-density screen, but never more than a large panel needs.
    const density = Math.min(typeof devicePixelRatio === 'number' ? devicePixelRatio : 1, 1600 / Math.max(size.w, size.h));
    const cw = Math.max(1, Math.round(size.w * density));
    const ch = Math.max(1, Math.round(size.h * density));
    element.width = cw;
    element.height = ch;
    const ctx = element.getContext('2d');
    if (!ctx) return undefined;
    const k = panel.width ? cw / panel.width : 1;
    const layout = panel.width && panel.height ? glassLayout(panel, k) : { cards: [], dials: [] };
    const options = { radius: 18, unit: k, accent: style.accent };
    const draw = (now: number) => {
      const seconds = now / 1000;
      const phase = (seconds / LOOP_SECONDS) % 1;
      if (isGenerative(style.art)) {
        const r = sharedRenderer();
        if (!r) return;
        r.render(style.art, cw, ch, phase, layout.cards, layout.dials, options);
        ctx.drawImage(r.canvas, 0, 0);
      } else if (source) {
        const image = source.frames[frameAt(source, seconds)]!;
        if (style.art === 'glass') {
          const r = sharedRenderer();
          if (!r) return place(ctx, image, source.width, source.height, cw, ch, 'cover');
          r.renderPicture(image, source.width, source.height, cw, ch, phase, layout.cards, layout.dials, options);
          ctx.drawImage(r.canvas, 0, 0);
        } else {
          ctx.clearRect(0, 0, cw, ch);
          place(ctx, image, source.width, source.height, cw, ch, style.imageFit ?? 'cover');
        }
      }
    };
    // The first picture: a still a moment into the loop (what a gallery shows until the mouse is on it).
    draw(1200);

    // Moving: unless asked for less motion, while on screen, and in a gallery only while the mouse is on its card.
    const gallery = element.closest('.motion-hover');
    let hovered = false;
    let visible = true;
    const enter = () => (hovered = true);
    const leave = () => (hovered = false);
    gallery?.addEventListener('pointerenter', enter);
    gallery?.addEventListener('pointerleave', leave);
    const observer = typeof IntersectionObserver !== 'undefined' ? new IntersectionObserver((entries) => (visible = entries[0]?.isIntersecting ?? true)) : null;
    observer?.observe(element);
    const animated = isGenerative(style.art) || (source?.frames.length ?? 0) > 1 || style.art === 'glass';
    const stop = animated && !reducedMotion() ? register({ draw, moving: () => visible && (!gallery || hovered), last: 0 }) : () => undefined;
    return () => {
      stop();
      observer?.disconnect();
      gallery?.removeEventListener('pointerenter', enter);
      gallery?.removeEventListener('pointerleave', leave);
    };
  }, [size, source, panel, style.art, style.accent, style.imageFit]);

  // Without WebGL2 a generated scene shows its colours as a gradient.
  const fallback = isGenerative(style.art) && !sharedRenderer() ? `linear-gradient(135deg, ${GENERATIVE[style.art].palette[1]}, ${GENERATIVE[style.art].palette[3]})` : undefined;
  return <canvas ref={canvas} aria-hidden data-live-art={style.art ?? 'animation'} className="absolute inset-0 size-full" style={{ background: fallback }} />;
}
