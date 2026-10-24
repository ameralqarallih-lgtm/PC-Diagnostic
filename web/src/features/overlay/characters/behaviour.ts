import type { OverlayCharacter } from '@/lib/api/types';

/**
 * Where a character is and what it does at a moment: from its settings, the panel's box and its screen's, and the time.
 * Nothing is remembered between moments, so the editor and the recorder (CharacterMotion.cs, the same rules and the
 * same integer hash) agree, and a character resumes where it would be.
 *
 * Places: on the panel's top edge, inside it along its bottom, beside it (left or right), along the bottom of its
 * screen, or all around it (along the top, hopping down beside it and back up). Movements: staying in place doing its
 * action, pacing from end to end with a pause at each, or wandering to spots along its place with a pause at each.
 */

export interface Box {
  left: number;
  top: number;
  right: number;
  bottom: number;
}

/** The character's anchor (its feet) in the area's pixels, what it plays, how far into it, and which way it faces. */
export interface CharacterFrame {
  x: number;
  y: number;
  action: string;
  seconds: number;
  facing: 1 | -1;
}

/** FNV-1a of a text (UTF-16 code units). */
export function seedOf(text: string): number {
  let h = 2166136261;
  for (let i = 0; i < text.length; i++) {
    h ^= text.charCodeAt(i);
    h = Math.imul(h, 16777619) >>> 0;
  }
  return h >>> 0;
}

/** A number in [0, 1) for a seed and a step (xorshift). */
export function random(seed: number, k: number): number {
  let x = (seed ^ Math.imul(k + 1, 0x9e3779b1)) >>> 0;
  x = (x ^ (x << 13)) >>> 0;
  x = (x ^ (x >>> 17)) >>> 0;
  x = (x ^ (x << 5)) >>> 0;
  return x / 4294967296;
}

/** The actions done while pausing (and staying), and those that are ways of moving. */
const MOVING = new Set(['walk', 'run']);

/** The line a place stands on: from x0 to x1 at y (pixels); x0 = x1 for a spot. */
export function lineOf(place: OverlayCharacter['place'], panel: Box, screen: Box, size: number) {
  switch (place) {
    case 'inside':
      return { x0: panel.left + size * 0.4, x1: panel.right - size * 0.4, y: panel.bottom - size * 0.06 };
    case 'left':
      return { x0: panel.left - size * 0.5, x1: panel.left - size * 0.5, y: panel.bottom };
    case 'right':
      return { x0: panel.right + size * 0.5, x1: panel.right + size * 0.5, y: panel.bottom };
    case 'screen':
      return { x0: screen.left + size * 0.5, x1: screen.right - size * 0.5, y: screen.bottom };
    default:
      return { x0: panel.left + size * 0.35, x1: panel.right - size * 0.35, y: panel.top };
  }
}

const smooth = (u: number) => u * u * (3 - 2 * u);

/** Where a character is and what it does `t` seconds on (any steady clock), its size in pixels. */
/**
 * Where a character is and what it does `t` seconds on, kept on its screen (on a panel at the screen's top edge it stands
 * just inside the screen rather than above it, out of sight), as the recorder.
 */
export function frameOf(c: OverlayCharacter, panel: Box, screen: Box, size: number, t: number): CharacterFrame {
  const f = route(c, panel, screen, size, t);
  return { ...f, x: Math.max(screen.left + size * 0.4, Math.min(screen.right - size * 0.4, f.x)), y: Math.max(screen.top + size, Math.min(screen.bottom, f.y)) };
}

function route(c: OverlayCharacter, panel: Box, screen: Box, size: number, t: number): CharacterFrame {
  const s = Math.min(3, Math.max(0.25, (c.speed ?? 100) / 100));
  // How fast its animations play (as fast as it moves unless set), and how long it stops.
  const a = c.tempo != null ? Math.min(3, Math.max(0.25, c.tempo / 100)) : s;
  const hold = Math.min(4, Math.max(0.25, (c.pause ?? 100) / 100));
  const seed = seedOf(c.id);
  const may = (action: string) => c.plays == null || c.plays.includes(action);
  let rest = MOVING.has(c.action) ? 'idle' : c.action;
  if (!may(rest)) rest = RESTING.find(may) ?? 'idle';
  let gait = c.action === 'run' ? 'run' : 'walk';
  if (!may(gait)) gait = gait === 'run' ? 'walk' : 'run';
  const moves = may(gait);
  const hop = may('jump') ? 'jump' : gait;
  const v = size * (gait === 'run' ? 1.3 : 0.55) * s;
  const pause = ((rest === 'sleep' ? 6 : 2.6) / Math.sqrt(s)) * hold;

  if (c.place === 'around' && moves) {
    const L = panel.left + size * 0.35;
    const R = Math.max(L, panel.right - size * 0.35);
    const xr = panel.right + size * 0.55;
    const xl = panel.left - size * 0.55;
    const walk = (R - L) / v;
    const J = 1 / s;
    const P = (1.8 / Math.sqrt(s)) * hold;
    const legs: { d: number; at: (u: number) => CharacterFrame }[] = [
      { d: walk, at: (u) => ({ x: L + v * u, y: panel.top, action: gait, seconds: u * a, facing: 1 }) },
      { d: J, at: (u) => ({ x: R + (xr - R) * smooth(u / J), y: panel.top + (panel.bottom - panel.top) * smooth(u / J), action: hop, seconds: u * a, facing: 1 }) },
      { d: P, at: (u) => ({ x: xr, y: panel.bottom, action: rest, seconds: u * a, facing: -1 }) },
      { d: J, at: (u) => ({ x: xr + (R - xr) * smooth(u / J), y: panel.bottom + (panel.top - panel.bottom) * smooth(u / J), action: hop, seconds: u * a, facing: -1 }) },
      { d: walk, at: (u) => ({ x: R - v * u, y: panel.top, action: gait, seconds: u * a, facing: -1 }) },
      { d: J, at: (u) => ({ x: L + (xl - L) * smooth(u / J), y: panel.top + (panel.bottom - panel.top) * smooth(u / J), action: hop, seconds: u * a, facing: -1 }) },
      { d: P, at: (u) => ({ x: xl, y: panel.bottom, action: rest, seconds: u * a, facing: 1 }) },
      { d: J, at: (u) => ({ x: xl + (L - xl) * smooth(u / J), y: panel.bottom + (panel.top - panel.bottom) * smooth(u / J), action: hop, seconds: u * a, facing: 1 }) },
    ];
    return along(legs, t + random(seed, 0) * 30);
  }

  // Dropped on its screen: across it, at its height there.
  const line =
    c.place === 'spot'
      ? { x0: screen.left + size * 0.5, x1: screen.right - size * 0.5, y: screen.top + Math.min(1, Math.max(0, c.y ?? 1)) * (screen.bottom - screen.top) }
      : lineOf(c.place === 'around' ? 'top' : c.place, panel, screen, size);
  let { x0, x1 } = line;
  const { y } = line;
  if (c.movement !== 'stay' && ((c.from ?? 0) > 0 || (c.to ?? 1) < 1)) {
    // Kept to its area along the line.
    const whole = Math.max(0, x1 - x0);
    [x0, x1] = [x0 + whole * Math.min(1, Math.max(0, c.from ?? 0)), x0 + whole * Math.min(1, Math.max(0, c.to ?? 1))];
  }
  const span = Math.max(0, x1 - x0);
  const still = (x: number): CharacterFrame => ({ x, y, action: moves || !MOVING.has(c.action) ? c.action : rest, seconds: t * a, facing: c.flip ? -1 : 1 });
  if (c.movement === 'stay' || span < size * 0.2 || !moves) return still(x0 + span * Math.min(1, Math.max(0, c.x ?? 0.5)));

  if (c.movement === 'pace') {
    const walk = span / v;
    return along(
      [
        { d: walk, at: (u) => ({ x: x0 + v * u, y, action: gait, seconds: u * a, facing: 1 }) },
        { d: pause, at: (u) => ({ x: x1, y, action: rest, seconds: u * a, facing: 1 }) },
        { d: walk, at: (u) => ({ x: x1 - v * u, y, action: gait, seconds: u * a, facing: -1 }) },
        { d: pause, at: (u) => ({ x: x0, y, action: rest, seconds: u * a, facing: -1 }) },
      ],
      t + random(seed, 0) * (2 * walk + 2 * pause),
    );
  }

  // Wandering: to six spots in turn (the last back to the first), a pause doing something at each.
  const pool = c.plays == null ? [rest, 'idle', 'sit', 'play', 'jump'] : [...new Set([rest, ...c.plays.filter((p) => RESTING.includes(p))])];
  const spots = Array.from({ length: 6 }, (_, k) => x0 + span * random(seed, k + 1));
  const legs: { d: number; at: (u: number) => CharacterFrame }[] = [];
  for (let k = 0; k < 6; k++) {
    const from = spots[k]!;
    const to = spots[(k + 1) % 6]!;
    const dir: 1 | -1 = to >= from ? 1 : -1;
    const d = Math.abs(to - from) / v;
    const doing = pool[Math.floor(random(seed, k + 21) * pool.length)]!;
    const wait = ((doing === 'sleep' ? 6 : 1.5 + 3.5 * random(seed, k + 11)) / Math.sqrt(s)) * hold;
    legs.push({ d, at: (u) => ({ x: from + dir * v * u, y, action: gait, seconds: u * a, facing: dir }) });
    legs.push({ d: wait, at: (u) => ({ x: to, y, action: doing, seconds: u * a, facing: dir }) });
  }
  return along(legs, t);
}

/** What it may do between walks, in the order one is chosen when its own is not among its animations. */
const RESTING = ['idle', 'sit', 'play', 'dance', 'sleep', 'jump'];

/** The frame `t` seconds into a route of legs, looping. */
function along(legs: { d: number; at: (u: number) => CharacterFrame }[], t: number): CharacterFrame {
  const total = legs.reduce((sum, l) => sum + l.d, 0);
  let u = total > 0 ? ((t % total) + total) % total : 0;
  for (const leg of legs) {
    if (u < leg.d || leg === legs[legs.length - 1]) return leg.at(Math.min(u, leg.d));
    u -= leg.d;
  }
  return legs[0]!.at(0);
}
