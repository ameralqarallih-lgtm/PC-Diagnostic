import { useEffect, useRef, useState } from 'react';
import type { OverlayCharacter, OverlayReading, OverlaySource } from '@/lib/api/types';
import { frameOf, type Box } from './behaviour';
import { clockNow, isLine, type CharacterSenses, type Company } from './company';
import { loadCharacter, loadEffects, loadProps, propsOf, type CharacterFrames } from './library';
import { drawWheel, gaugeOf, wheelBox, type GaugePlay } from './gauges';
import { breathAt, drawParticles, particlesOf } from './particles';
import type { BubbleTone, Thought } from './mind';
import { Reacting, reactingFrame } from './reactions';

/**
 * Characters drawn in the editor: each on a canvas redrawn by one shared clock (at most 30 times a second, none when
 * the system asks for less motion: then each shows one still moment), placed by its motion rules (behaviour.ts) around
 * its panel's box on the editor's screen, playing its character's frames as the recorder does. A character that reacts
 * to a reading does what its status says, with the status's effect and prop drawn with it (reactions.ts, props.ts).
 */

type Draw = (now: number) => void;
const drawers = new Set<{ draw: Draw; last: number; moving: () => boolean }>();
let frame = 0;
function tick(now: number) {
  for (const d of drawers) {
    if (d.moving() && now - d.last >= 33) {
      d.last = now;
      d.draw(now);
    }
  }
  frame = drawers.size ? requestAnimationFrame(tick) : 0;
}

/** Draws on the shared clock until the returned function is called. */
export function onEveryFrame(draw: Draw, moving: () => boolean = () => true): () => void {
  const entry = { draw, last: 0, moving };
  drawers.add(entry);
  if (!frame && typeof requestAnimationFrame !== 'undefined') frame = requestAnimationFrame(tick);
  return () => {
    drawers.delete(entry);
  };
}

export const reducedMotion = () => typeof matchMedia !== 'undefined' && matchMedia('(prefers-reduced-motion: reduce)').matches;

/** A character's frames once read (null while reading, or when it is not there). */
export function useCharacterFrames(id: string): CharacterFrames | null {
  const [frames, setFrames] = useState<{ id: string; frames: CharacterFrames | null } | null>(null);
  useEffect(() => {
    let live = true;
    void loadCharacter(id).then((f) => {
      if (live) setFrames({ id, frames: f });
    });
    return () => {
      live = false;
    };
  }, [id]);
  return frames?.id === id ? frames.frames : null;
}

const ratio = () => (typeof devicePixelRatio === 'number' && devicePixelRatio > 0 ? devicePixelRatio : 1);

/** The reactions' effects (or, with `load`, another such file: the props) once read (null while reading). */
function useEffectFrames(wanted: boolean, load: () => Promise<CharacterFrames | null> = loadEffects): CharacterFrames | null {
  const [frames, setFrames] = useState<CharacterFrames | null>(null);
  useEffect(() => {
    if (!wanted) return undefined;
    let live = true;
    void load().then((f) => {
      if (live) setFrames(f);
    });
    return () => {
      live = false;
    };
  }, [wanted, load]);
  return wanted ? frames : null;
}

/** Room around a character's feet for its frames and, when it reacts, its effects (either way it faces). */
function roomFor(frames: CharacterFrames, effects: CharacterFrames | null, size: number) {
  const body = frames.layout(size);
  let half = Math.max(body.anchorX, body.width - body.anchorX);
  let up = body.anchorY;
  let down = body.height - body.anchorY;
  if (effects) {
    const fx = effects.layout(size);
    const [hx, hy] = frames.headFromFeet(size, 1);
    for (const clip of effects.pack.clips) {
      const [x, y] = clip.at === 'head' ? [Math.abs(hx), hy] : [0, 0];
      half = Math.max(half, x + Math.max(fx.anchorX, fx.width - fx.anchorX));
      up = Math.max(up, fx.anchorY - y);
      down = Math.max(down, fx.height - fx.anchorY + y);
    }
  }
  // Its shadow under its feet, and room to shake.
  return { half: half + size * 0.03, up, down: Math.max(down, size * 0.05) };
}

/** A bubble tone's edge color: critical red, high amber, relief green, info blue (as the recorder draws it). */
export const TONE_COLOR: Record<BubbleTone, string> = { critical: '#EF4444', high: '#F59E0B', relief: '#34D399', info: '#60A5FA' };

/** A soft shadow under its feet (darkest in the middle), at (`x`, `y`) on the canvas. */
function drawShadow(ctx: CanvasRenderingContext2D, x: number, y: number, size: number) {
  const w = size * 0.21;
  const h = size * 0.04;
  ctx.save();
  ctx.translate(x, y - size * 0.005);
  ctx.scale(1, h / w);
  const shade = ctx.createRadialGradient(0, 0, 0, 0, 0, w);
  shade.addColorStop(0, 'rgba(0,0,0,0.35)');
  shade.addColorStop(1, 'rgba(0,0,0,0)');
  ctx.fillStyle = shade;
  ctx.beginPath();
  ctx.arc(0, 0, w, 0, Math.PI * 2);
  ctx.fill();
  ctx.restore();
}

/** How far it shakes at a moment (seconds), as the recorder: side to side, a fortieth of its height at most. */
export const shakeAt = (thought: Thought | null | undefined, size: number, now: number) => (thought?.shake ? Math.sin(now * 38) * size * 0.025 * thought.shake : 0);

/**
 * One character of a panel on the editor's screen (`pixels`: canvas pixels to a panel pixel at 100 %), reacting to its
 * panel's `readings` with their values in `sources` when it is set to. With its screen's `company` (and who it is
 * there, `self`) it also senses the mouse over the screen, clicks, the others and the weather, as on the screens.
 */
export function CharacterSprite({ character, panel, screen, pixels, readings = [], sources, onClick, company, self = character.id }: { character: OverlayCharacter; panel: Box; screen: Box; pixels: number; readings?: OverlayReading[]; sources?: ReadonlyMap<string, OverlaySource>; onClick?: () => void; company?: Company; self?: string }) {
  const ref = useRef<HTMLCanvasElement>(null);
  const frames = useCharacterFrames(character.character);
  const onGaugePlace = character.place === 'wheel' || character.place === 'needle' || character.place === 'bar' || character.place === 'graph';
  // A wheel or needle character plays on its gauge with or without reacting (its readings' values move it); with a
  // company it always thinks (the mouse, the others, the weather).
  const reacts = (!!character.react || onGaugePlace || !!company) && !!sources;
  const touches = useRef(0);
  const feet = useRef({ x: Number.NaN, y: Number.NaN, size: 0 });
  const effects = useEffectFrames(reacts);
  const props = useEffectFrames(reacts, loadProps);
  const size = Math.max(4, character.size * pixels);
  const state = useRef(new Reacting());
  const bubble = useRef<HTMLDivElement>(null);
  // The values, read at each moment (they change without the character changing).
  const live = useRef({ readings, sources });
  live.current = { readings, sources };
  useEffect(() => {
    const el = ref.current;
    if (!frames || !el) return undefined;
    const dpr = ratio();
    // The canvas, made again when the size it is drawn at changes (on a gauge it fits the gauge) or a wheel needs room.
    let room = { half: 0, up: 0, down: 0, key: '' };
    const fit = (drawn: number, play: GaugePlay | null, sparks: 'none' | 'dust' | 'confetti', propped: boolean) => {
      const wheel = wheelBox(play);
      const inner = wheel ? (wheel.right - wheel.left) / 2 : 0;
      const key = `${drawn.toFixed(1)}|${inner.toFixed(1)}|${sparks}|${propped}`;
      if (key === room.key) return room;
      const r = roomFor(frames, effects, drawn);
      // Room for its particles: confetti flies about its head, dust trails behind its feet.
      const spread = sparks === 'confetti' ? drawn * 1.7 : sparks === 'dust' ? drawn * 0.45 : 0;
      const rise = sparks === 'confetti' ? drawn * 1.5 : 0;
      // And for a prop: beside it on the ground, dropping in from above, over its head.
      const [aside, over] = propped ? [drawn * 0.95, drawn * 1.9] : [0, 0];
      room = { half: Math.max(r.half, inner + 2, spread, aside), up: Math.max(r.up, inner * 1.93 + 2, rise, over), down: Math.max(r.down, sparks === 'confetti' ? drawn * 0.8 : 0, propped ? drawn * 0.12 : 0), key };
      el.width = Math.ceil(2 * room.half * dpr);
      el.height = Math.ceil((room.up + room.down) * dpr);
      el.style.width = `${2 * room.half}px`;
      el.style.height = `${room.up + room.down}px`;
      return room;
    };
    const ctx = el.getContext('2d');
    let last = 0;
    const draw = (now: number) => {
      last = now;
      const { readings: r, sources: s } = live.current;
      const at = feet.current;
      const senses: CharacterSenses | null = company && !Number.isNaN(at.x)
        ? { self, x: at.x, y: at.y, size: at.size, cursor: character.mouse === false ? null : company.cursor, touches: touches.current, friend: company.friendOf(self), weather: company.weather, ...clockNow(), line: isLine(character.place) }
        : null;
      const reacted = reacts && s ? reactingFrame(character, { readings: r }, s, state.current, panel, screen, size, now / 1000, null, (source) => tileOf(el, source), (source) => gaugeOf(el, source), senses) : null;
      const plain = onGaugePlace ? { ...character, place: 'top' as const } : character;
      const f = reacted?.frame ?? frameOf(plain, panel, screen, size, now / 1000);
      const play = reacted?.play ?? null;
      const drawn = play?.size ?? size;
      const thought = reacted?.thought;
      // Where it was drawn, for the others of its screen (and its own senses next time).
      feet.current = { x: f.x, y: f.y, size: drawn };
      company?.set({ id: self, name: frames.pack.name, x: f.x, y: f.y, size: drawn, free: state.current.mind.free, meetingWith: state.current.mind.meetingWith, line: isLine(character.place) && !play });
      const particles = particlesOf(thought?.burst ?? null, thought?.burstAge ?? 0, f.action, f.facing, drawn, now / 1000);
      // Its props (the one shown and the one going): on the ground they are left while it walks or runs.
      const shown = reacted && props ? propsOf(frames, state.current.props.at(now / 1000, state.current.prop), f.action, f.seconds, f.facing, drawn, now / 1000, f.action === 'walk' || f.action === 'run' || !!play) : [];
      const { half, up } = fit(drawn, play, thought?.burst === 'confetti' ? 'confetti' : f.action === 'run' ? 'dust' : 'none', shown.length > 0);
      const shake = shakeAt(reacted?.thought, drawn, now / 1000);
      el.style.left = `${f.x + shake - half}px`;
      el.style.top = `${f.y - up}px`;
      const [hx, hy] = frames.headFromFeet(drawn, f.facing);
      showBubble(bubble.current, reacted?.thought ?? null, f.x + shake + hx, f.y + hy, drawn);
      el.dataset.status = reacted?.status ?? '';
      el.dataset.effect = reacted?.effect ?? '';
      el.dataset.prop = shown.map((p) => p.prop.id).join(' ');
      el.dataset.play = play?.kind ?? '';
      if (!ctx) return;
      ctx.clearRect(0, 0, el.width, el.height);
      // The wheel's middle relative to its feet (the canvas follows the feet).
      if (play?.kind === 'wheel') drawWheel(ctx, play, (half + play.gauge.cx - f.x - shake) * dpr, (up + play.gauge.cy - f.y) * dpr, dpr);
      if (play?.kind !== 'needle') drawShadow(ctx, half * dpr, up * dpr, drawn * dpr);
      for (const p of shown) props!.drawProp(ctx, p.prop, p.pose, p.particles, f.facing, drawn, half * dpr, up * dpr, dpr, true, () => draw(last));
      // Breathing while it stands, sits or sleeps: a little taller and shorter, its feet where they are.
      const breath = breathAt(f.action, now / 1000);
      ctx.save();
      ctx.translate(0, up * dpr * (1 - breath));
      ctx.scale(1, breath);
      frames.draw(ctx, f.action, f.seconds, f.facing, drawn * dpr, half * dpr, up * dpr, () => draw(last));
      ctx.restore();
      for (const p of shown) props!.drawProp(ctx, p.prop, p.pose, p.particles, f.facing, drawn, half * dpr, up * dpr, dpr, false, () => draw(last));
      drawParticles(ctx, particles, half * dpr, up * dpr, dpr);
      if (reacted?.effect && effects) effects.drawEffect(ctx, reacted.effect, frames, f.seconds, f.facing, drawn * dpr, half * dpr, up * dpr, () => draw(last));
    };
    draw(reducedMotion() ? 1200 : performance.now());
    const stop = reducedMotion() ? undefined : onEveryFrame(draw);
    return () => {
      stop?.();
      company?.remove(self);
    };
  }, [frames, effects, props, reacts, onGaugePlace, character, panel.left, panel.top, panel.right, panel.bottom, screen.left, screen.top, screen.right, screen.bottom, size, company, self]);
  if (!frames) return null;
  return (
    <>
      <div ref={bubble} role="status" aria-live="polite" data-bubble="" style={{ position: 'absolute', display: 'none', pointerEvents: 'none', whiteSpace: 'nowrap', zIndex: 2 }} />
      <canvas
      ref={ref}
      aria-hidden
      data-character={character.character}
      onClick={
        onClick &&
        (() => {
          // A click it answers on the screens (it takes the mouse there) pokes it here too.
          if (character.clickable || character.draggable) touches.current++;
          onClick();
        })
      }
      title={onClick ? 'Edit this character' : undefined}
      style={{ position: 'absolute', pointerEvents: onClick ? 'auto' : 'none', cursor: onClick ? 'pointer' : undefined, opacity: Math.min(100, Math.max(20, character.opacity ?? 100)) / 100 }}
      />
    </>
  );
}

/**
 * Where a reading's tile is on the editor's screen (its tile, or its value in a panel without tiles), in the same pixels
 * as the character's: it goes there when the reading turns bad. Null when the reading is not shown.
 */
function tileOf(el: HTMLElement, source: string): Box | null {
  const host = el.offsetParent as HTMLElement | null;
  if (!host) return null;
  const id = typeof CSS !== 'undefined' && CSS.escape ? CSS.escape(source) : source.replace(/["\\]/g, '\\$&');
  const tile = host.querySelector(`[data-tile][data-source="${id}"]`) ?? host.querySelector(`[data-value][data-source="${id}"]`);
  if (!tile) return null;
  const outer = host.getBoundingClientRect();
  const r = tile.getBoundingClientRect();
  // The screen may be drawn zoomed: back to its own pixels.
  const k = host.offsetWidth ? outer.width / host.offsetWidth : 1;
  return { left: (r.left - outer.left) / k, top: (r.top - outer.top) / k, right: (r.right - outer.left) / k, bottom: (r.bottom - outer.top) / k };
}

/**
 * Its speech bubble over its head (`x`, `y`: the top of its head), as the recorder draws it: dark, edged in its tone,
 * rising a little and fading in, fading out at the end; hidden without one.
 */
function showBubble(el: HTMLDivElement | null, thought: Thought | null, x: number, y: number, size: number) {
  if (!el) return;
  if (!thought?.bubble) {
    if (el.style.display !== 'none') el.style.display = 'none';
    return;
  }
  const px = Math.min(22, Math.max(11, size * 0.17));
  const fade = Math.max(0, Math.min(1, thought.bubbleAge / 0.25, (thought.bubbleLife - thought.bubbleAge) / 0.4));
  const rise = Math.max(0, 1 - thought.bubbleAge / 0.25) * px * 0.4;
  if (el.textContent !== thought.bubble) el.textContent = thought.bubble;
  el.dataset.tone = thought.tone;
  Object.assign(el.style, {
    display: 'block',
    left: `${x}px`,
    top: `${y - px * 0.3 - px * 0.5 + rise}px`,
    transform: 'translate(-50%, -100%)',
    font: `600 ${px}px "Segoe UI", system-ui, sans-serif`,
    color: '#F8FAFC',
    background: 'rgba(15, 23, 42, 0.92)',
    border: `${Math.max(1.5, px * 0.13)}px solid ${TONE_COLOR[thought.tone]}`,
    borderRadius: `${px * 0.7}px`,
    padding: `${px * 0.36}px ${px * 0.6}px`,
    opacity: String(fade),
  });
}

/** A character playing an action in a small square (pickers and lists): moving while `moving` says so. */
export function CharacterThumb({ id, action = 'idle', size = 64, moving = true }: { id: string; action?: string; size?: number; moving?: boolean | (() => boolean) }) {
  const ref = useRef<HTMLCanvasElement>(null);
  const frames = useCharacterFrames(id);
  useEffect(() => {
    const el = ref.current;
    if (!el || !frames) return undefined;
    const dpr = ratio();
    el.width = Math.round(size * dpr);
    el.height = Math.round(size * dpr);
    const ctx = el.getContext('2d');
    if (!ctx) return undefined;
    // The whole frame fitted in the square, its feet near the bottom.
    const { pack } = frames;
    const k = Math.min(1, (0.92 * size) / Math.max(pack.width, pack.height) / (size / pack.tall));
    const tall = size * k * dpr;
    const box = frames.layout(tall);
    let last = 0;
    const draw = (now: number) => {
      last = now;
      ctx.clearRect(0, 0, el.width, el.height);
      frames.draw(ctx, action, now / 1000, 1, tall, (el.width - box.width) / 2 + box.anchorX, (el.height - box.height) / 2 + box.anchorY, () => draw(last));
    };
    draw(1200);
    if (reducedMotion() || moving === false) return undefined;
    return onEveryFrame(draw, typeof moving === 'function' ? moving : () => true);
  }, [frames, action, moving, size]);
  return <canvas ref={ref} aria-hidden data-character={id} style={{ width: size, height: size }} />;
}
