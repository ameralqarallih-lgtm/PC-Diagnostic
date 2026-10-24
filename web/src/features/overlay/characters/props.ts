import { random, type Particle } from './particles';
import table from './reactions.json';

/**
 * A character's props drawn in code (as the recorder's CharacterProps.cs, the same numbers, checked by props.test.ts
 * against prop-parity.json which the recorder's tests read): where each goes (on its head, over it, beside it on the
 * ground the way it faces, just before it), how it arrives (popping up on the character, dropping in and bouncing on
 * the ground), how it moves (bobbing, swaying, wobbling) and what it gives off (drips, steam, wind, sparkles, a
 * flashing glow). Each is a function of time alone, so the editor and the recorder draw the same. Their frames are the
 * props' file (props.char).
 */

/** A prop, from reactions.json (its frame is in props.char). */
export interface PropInfo {
  id: string;
  name: string;
  /** Where it goes: head (on top of its head), above (over its head), hand (held before it, halfway up), beside (on the ground, the way it faces), front (on the ground just before it). */
  at: 'head' | 'above' | 'hand' | 'beside' | 'front';
  /** How tall it is against the character. */
  size: number;
  /** How far up it is held, a share of its own height (0: it stands on its point). */
  grip: number;
  motion: 'none' | 'bob' | 'sway' | 'wobble';
  emit: 'drip' | 'steam' | 'wind' | 'sparkle' | 'glow' | 'foam' | 'water' | null;
  /** Drawn behind the character. */
  behind?: boolean;
  /** Turned with the character. */
  mirror?: boolean;
  /** How wide it is against the character (it stands that far clear of it). */
  width?: number;
  /** Where what it sprays comes out, from its point, against the character: along its drawing (turned with it), and up. */
  nozzle?: [number, number];
}

/** The props, in the table's order. */
export const PROPS = table.props as PropInfo[];
const BY_ID = new Map(PROPS.map((p) => [p.id, p]));

/** A prop by id (undefined: no such prop). */
export const propInfo = (id: string) => BY_ID.get(id);

export const ARRIVE_SECONDS = 0.45;
export const LEAVE_SECONDS = 0.3;

/** Where a prop is drawn: its point (where its bottom's middle goes, from the feet), how big, its turn (radians) and opacity. */
export interface PropPose {
  x: number;
  y: number;
  scale: number;
  angle: number;
  alpha: number;
}

/** Whether a prop stands on the ground: left behind while the character walks or runs. */
export const onGround = (prop: PropInfo) => prop.at === 'beside' || prop.at === 'front';

/** Whether what it gives off is drawn behind it (a glow) rather than in front. */
export const emitsBehind = (prop: PropInfo) => prop.emit === 'glow';

const back = (t: number) => {
  const c1 = 1.70158;
  const c3 = c1 + 1;
  return 1 + c3 * (t - 1) ** 3 + c1 * (t - 1) ** 2;
};

function bounce(t: number) {
  const n = 7.5625;
  const d = 2.75;
  if (t < 1 / d) return n * t * t;
  if (t < 2 / d) return n * (t -= 1.5 / d) * t + 0.75;
  if (t < 2.5 / d) return n * (t -= 2.25 / d) * t + 0.9375;
  return n * (t -= 2.625 / d) * t + 0.984375;
}

const fraction = (x: number) => x - Math.floor(x);

/** Where what a prop sprays comes out, from its point (turned with it when it turns with the character). */
const nozzleOf = (prop: PropInfo, facing: number, size: number): [number, number] => [(prop.mirror ? facing : 1) * (prop.nozzle?.[0] ?? 0) * size, -(prop.nozzle?.[1] ?? 0) * size];
const f32 = Math.fround;

/**
 * A prop's pose `age` seconds after it came (`gone`: seconds since it went, or negative while it stays), at `now`, with
 * a character `size` pixels tall facing a way, the top of its head at (`headX`, `headY`) from its feet and its body
 * reaching `reach` pixels from its feet the way it faces; on the ground it is not shown while the character is `moving`.
 */
export function poseAt(prop: PropInfo, age: number, gone: number, now: number, facing: number, size: number, headX: number, headY: number, reach: number, moving: boolean): PropPose {
  size = f32(size);
  const h = prop.size * size;
  let [x, y] =
    prop.at === 'head'
      ? [headX, headY + 0.05 * size]
      : prop.at === 'above'
        ? [headX + facing * 0.06 * size, headY - 0.04 * size]
        : // Held before it, at its front edge halfway up.
          prop.at === 'hand'
          ? [facing * reach * 0.75, -0.3 * size]
          : // Clear of its body, a little way off; just before it, its front edge.
          prop.at === 'beside'
          ? [facing * (reach + 0.04 * size + ((prop.width ?? 0) * size) / 2), 0]
          : [facing * reach * 0.85, 0];
  y += prop.grip * h;
  const t = Math.min(1, Math.max(0, age / ARRIVE_SECONDS));
  let scale = 1;
  let alpha: number;
  if (onGround(prop)) {
    y -= (1 - bounce(t)) * size * 0.6;
    alpha = Math.min(1, Math.max(0, age / 0.12));
  } else {
    scale = back(t);
    alpha = Math.min(1, Math.max(0, age / 0.15));
  }
  let angle = 0;
  if (prop.motion === 'bob') y += Math.sin((2 * Math.PI * now) / 1.6) * 0.015 * size;
  else if (prop.motion === 'sway') angle = Math.sin((2 * Math.PI * now) / 2.2) * 0.08;
  else if (prop.motion === 'wobble') angle = Math.sin((2 * Math.PI * now) / 0.3) * 0.03;
  if (gone >= 0) {
    const out = Math.min(1, Math.max(0, gone / LEAVE_SECONDS));
    alpha *= 1 - out;
    scale *= 1 - 0.2 * out;
  }
  if (moving && onGround(prop)) alpha = 0;
  return { x, y, scale, angle: angle * (prop.mirror ? facing : 1), alpha };
}

/**
 * What a prop gives off at `now`, from its point, with a character `size` pixels tall facing a way: drips falling from
 * an ice pack, steam rising from a cup, wind blowing from a fan back at the character, sparkles about a trophy, a
 * siren's glow flashing red and blue.
 */
export function emitAt(prop: PropInfo, now: number, facing: number, size: number): Particle[] {
  size = f32(size);
  const h = prop.size * size;
  const list: Particle[] = [];
  if (prop.emit === 'drip') {
    for (let k = 0; k < 3; k++) {
      const a = fraction(now / 1.0 + k / 3);
      list.push({ x: (k - 1) * 0.08 * size, y: -0.02 * size + 0.5 * size * 0.9 * a * a, r: size * 0.022, color: 0xff7dd3fc, alpha: 0.9 * (1 - a), angle: 0 });
    }
  } else if (prop.emit === 'steam') {
    for (let k = 0; k < 3; k++) {
      const a = fraction(now / 1.8 + k / 3);
      list.push({ x: Math.sin(a * 5 + k * 2) * 0.04 * size, y: -h - a * 0.35 * size, r: size * (0.03 + 0.05 * a), color: 0xfff1f5f9, alpha: 0.5 * (1 - a) * Math.min(1, a * 5), angle: 0 });
    }
  } else if (prop.emit === 'wind') {
    for (let k = 0; k < 6; k++) {
      const a = fraction(now / 0.9 + k / 6);
      list.push({ x: -facing * (0.05 * size + a * 0.45 * size), y: -h * 0.6 + ((k % 3) - 1) * 0.06 * size, r: size * 0.018, color: 0xffe0f2fe, alpha: 0.6 * (1 - a), angle: 0 });
    }
  } else if (prop.emit === 'sparkle') {
    for (let k = 0; k < 4; k++) {
      const twinkle = Math.max(0, Math.sin(2 * Math.PI * (now / 1.2 + random(k, 9))));
      if (twinkle <= 0) continue;
      list.push({ x: (random(k, 7) - 0.5) * 0.5 * size, y: -h * (0.3 + 0.8 * random(k, 8)), r: 0.03 * size * twinkle, color: 0xfffde68a, alpha: twinkle, angle: Math.PI / 4 });
    }
  } else if (prop.emit === 'foam') {
    // Thick white foam bursting from its nozzle, the way it faces, spreading and falling.
    const [fx, fy] = nozzleOf(prop, facing, size);
    for (let k = 0; k < 14; k++) {
      const a = fraction(now / 0.7 + k / 14);
      list.push({ x: fx + facing * a * 0.6 * size, y: fy + (random(k, 11) - 0.5) * a * 0.34 * size + a * a * 0.16 * size, r: size * (0.02 + 0.07 * a * (0.6 + 0.8 * random(k, 12))), color: 0xffffffff, alpha: 0.9 * (1 - a * a), angle: 0 });
    }
  } else if (prop.emit === 'water') {
    // A stream of drops arcing out of its nozzle, the way it faces.
    const [wx, wy] = nozzleOf(prop, facing, size);
    for (let k = 0; k < 10; k++) {
      const a = fraction(now / 0.6 + k / 10);
      list.push({ x: wx + facing * a * 0.6 * size, y: wy - a * 0.12 * size + a * a * 0.35 * size, r: size * 0.026, color: 0xff38bdf8, alpha: 1 - 0.7 * a, angle: 0 });
    }
  } else if (prop.emit === 'glow') {
    // Red, then blue, in turn: three soft rings each, faint outside, bright inside.
    const p = ((Math.sin((2 * Math.PI * now) / 0.8) + 1) / 2) ** 2;
    for (const [color, on] of [
      [0xffef4444, p],
      [0xff60a5fa, 1 - p],
    ] as const) {
      list.push({ x: 0, y: -h * 0.72, r: size * 0.3, color, alpha: 0.1 * on, angle: 0 });
      list.push({ x: 0, y: -h * 0.72, r: size * 0.2, color, alpha: 0.16 * on, angle: 0 });
      list.push({ x: 0, y: -h * 0.72, r: size * 0.11, color, alpha: 0.3 * on, angle: 0 });
    }
  }
  return list;
}

/** What a track gives at a moment: the prop shown (null: none) and its age, and the one going (null: none), its age and how long since it went (-1: none). */
export interface PropShown {
  id: string | null;
  age: number;
  going: string | null;
  goingAge: number;
  gone: number;
}

/** A character's prop as it changes (as the recorder's PropTrack): the one shown and since when, and the one before it, going, until it has gone. */
export class PropTrack {
  private id: string | null = null;
  private since = 0;
  private old: string | null = null;
  private oldSince = 0;
  private goneAt = 0;

  at(now: number, prop: string | null): PropShown {
    if (prop !== this.id) {
      if (this.id !== null) {
        this.old = this.id;
        this.oldSince = this.since;
        this.goneAt = now;
      }
      this.id = prop;
      this.since = now;
    }
    if (this.old !== null && (now - this.goneAt >= LEAVE_SECONDS || this.old === this.id)) this.old = null;
    return { id: this.id, age: now - this.since, going: this.old, goingAge: this.old === null ? 0 : now - this.oldSince, gone: this.old === null ? -1 : now - this.goneAt };
  }
}
