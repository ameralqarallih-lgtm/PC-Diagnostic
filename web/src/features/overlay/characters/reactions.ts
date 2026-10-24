import type { OverlayCharacter, OverlayPanel, OverlayReading, OverlaySource } from '@/lib/api/types';
import { frameOf, type Box, type CharacterFrame } from './behaviour';
import type { CharacterSenses } from './company';
import { fits, onGauge, type GaugePlay, type GaugeSpot } from './gauges';
import { Mind, isBad, type Thought } from './mind';
import { PropTrack } from './props';
import table from './reactions.json';

/**
 * Characters reacting to readings (reactions.json, which the recorder embeds too: OverlayReactions.cs): each reading is
 * of a kind (performance, temperature, bandwidth, frame rate, frame time, latency, fan speed, power, other) by its
 * source and unit, its value has a status (low, normal, high, critical: critical is beyond the critical limit the bad
 * way, low the quiet end), and each kind's status brings a reaction: something to do and an effect around the
 * character (zZ, sweat, steam, a flame, sparks, rain, music notes…). A character's own settings can change any of them.
 */

export type ReactionStatus = 'low' | 'normal' | 'high' | 'critical';
export type ReadingKind = keyof typeof table.kinds;

export interface Reaction {
  /** What it does: 'default' (its own behaviour), a way of moving (walk, run), or something done in place. */
  action: string;
  /** The effect shown with it, or null. */
  effect: string | null;
}

interface KindTable {
  name: string;
  bad: 'up' | 'down';
  low: number | null;
  high: number | null;
  critical: number | null;
  share?: { low: number; high: number; critical: number };
  reactions: Record<ReactionStatus, [string, string | null]>;
  /** The prop shown with each status (none when missing). */
  props?: Partial<Record<ReactionStatus, string>>;
}

export const KINDS = table.kinds as unknown as Record<ReadingKind, KindTable>;
export const STATUSES = table.statuses as ReactionStatus[];
export const EFFECTS = table.effects as [string, string][];

/** The kind of a reading, by its source and unit. */
export function kindOf(source: string, unit: string): ReadingKind {
  const u = unit.trim();
  if (u.endsWith('°C') || u === 'C') return 'temperature';
  if (u === 'fps') return 'fps';
  if (u === 'ms') return source.includes('frametime') ? 'frametime' : 'latency';
  if (u === 'B/s') return 'bandwidth';
  if (u.toUpperCase() === 'RPM') return 'fan';
  if (u === 'W') return 'power';
  if (u === '%') return 'load';
  return 'other';
}

/**
 * A reading's status: its own warning and critical limits when it has them (crossed going down when it says so),
 * otherwise its kind's; bandwidth and power judged against the reading's full scale when it has one.
 */
export function statusOf(kind: ReadingKind, reading: Partial<Pick<OverlayReading, 'warning' | 'critical' | 'below' | 'show' | 'max'>> | undefined, value: number | null | undefined, total?: number | null): ReactionStatus {
  if (value == null || !Number.isFinite(value)) return 'normal';
  const k = KINDS[kind];
  const v = reading?.show === 'percent' && total != null && total > 0 ? (value / total) * 100 : value;
  // Its own limits are crossed going down when it says so; its kind's, the kind's bad way.
  const own = reading?.warning != null || reading?.critical != null;
  const down = own ? !!reading?.below : k.bad === 'down';
  const scale = reading?.max ?? (reading?.show === 'percent' ? null : total);
  const relative = k.share && scale != null && scale > 0 ? k.share : null;
  const limit = (key: 'low' | 'high' | 'critical') => (relative ? relative[key] * scale! : k[key]);
  // The limit that warns is the bad way's: the high one going up, the low one going down. A reading's own limits stand
  // alone; the quiet end is its kind's when the reading goes the kind's way.
  const warning = own ? (reading?.warning ?? null) : down ? limit('low') : limit('high');
  const critical = own ? (reading?.critical ?? null) : limit('critical');
  const quiet = down === (k.bad === 'down') ? (down ? limit('high') : limit('low')) : null;
  if (down) {
    if (critical != null && v <= critical) return 'critical';
    if (warning != null && v <= warning) return 'low';
    if (quiet != null && v >= quiet) return 'high';
    return 'normal';
  }
  if (critical != null && v >= critical) return 'critical';
  if (warning != null && v >= warning) return 'high';
  if (quiet != null && v <= quiet) return 'low';
  return 'normal';
}

/** A character's own reactions when `source` is what it reacts to: that reading's over its general ones (as the recorder). */
export function reactionsFor(character: Pick<OverlayCharacter, 'reactions' | 'reactionsBy'>, source: string): OverlayCharacter['reactions'] {
  const by = character.reactionsBy?.[source];
  if (!by || !Object.keys(by).length) return character.reactions;
  const merged: NonNullable<OverlayCharacter['reactions']> = { ...(character.reactions ?? {}) };
  for (const [status, r] of Object.entries(by) as [ReactionStatus, { action?: string | null; effect?: string | null; prop?: string | null }][]) {
    const under = merged[status];
    merged[status] = { action: r.action ?? under?.action ?? null, effect: r.effect ?? under?.effect ?? null, prop: r.prop ?? under?.prop ?? null };
  }
  return merged;
}

/** The reaction to a status: the character's own choice when it made one, otherwise its kind's. */
export function reactionFor(kind: ReadingKind, status: ReactionStatus, own?: OverlayCharacter['reactions']): Reaction {
  const [action, effect] = KINDS[kind].reactions[status];
  const mine = own?.[status];
  return {
    action: mine?.action ?? action,
    effect: mine?.effect === undefined || mine.effect === null ? effect : mine.effect === 'none' ? null : mine.effect,
  };
}


/** The prop shown with a status (null: none): the character's own choice when it made one, otherwise its kind's. */
export function propFor(kind: ReadingKind, status: ReactionStatus, own?: OverlayCharacter['reactions']): string | null {
  const mine = own?.[status]?.prop;
  if (mine === 'none') return null;
  return mine ?? KINDS[kind].props?.[status] ?? null;
}

/** What a character reacts to now: its reading (or the panel's most urgent one, for 'auto'), its kind and status. */
export function readingFor(character: Pick<OverlayCharacter, 'react' | 'priority' | 'ignore'>, panel: Pick<OverlayPanel, 'readings'>, sources: ReadonlyMap<string, OverlaySource>) {
  const react = character.react;
  if (!react) return null;
  const judge = (reading: OverlayReading) => {
    const source = sources.get(reading.source);
    if (!source || source.value == null) return null;
    const kind = kindOf(source.id, source.unit);
    return { reading, source, kind, status: statusOf(kind, reading, source.value, source.max) };
  };
  if (react !== 'auto') {
    const reading = panel.readings.find((r) => r.source === react) ?? { source: react };
    return judge(reading);
  }
  // In its order (the readings it puts first, then the rest as on the panel): the first critical one; else the first high
  // one; else the first reading, whatever it shows (low or normal).
  let best: ReturnType<typeof judge> = null;
  for (const reading of ordered(panel.readings, character.priority)) {
    if (character.ignore?.includes(reading.source)) continue;
    const j = judge(reading);
    if (j && (!best || URGENCY[j.status] > URGENCY[best.status])) best = j;
  }
  return best;
}

/** How urgent each status is, for choosing what an "auto" character reacts to. */
const URGENCY: Record<ReactionStatus, number> = { normal: 0, low: 0, high: 1, critical: 2 };

/**
 * A wheel or needle character's gauge (as the recorder's CharacterReacting.GaugeOf): the one of its reading; otherwise,
 * of its panel's readings shown with a gauge, in its order, the first critical one, else the first high one, else the
 * first (as it chooses what to react to); null: none drawn.
 */
export function gaugeFor(character: OverlayCharacter, panel: Pick<OverlayPanel, 'readings'>, sources: ReadonlyMap<string, OverlaySource>, gauges: (source: string) => GaugeSpot | null): GaugeSpot | null {
  const suits = (g: GaugeSpot | null) => (g && fits(character.place, g.visual) ? g : null);
  if (character.gauge) return suits(gauges(character.gauge));
  const shown = panel.readings.filter((r) => suits(gauges(r.source)));
  if (shown.length === 0) return null;
  const kept = ordered(shown, character.priority).filter((r) => !character.ignore?.includes(r.source));
  const chosen = readingFor({ ...character, react: 'auto' }, { readings: shown }, sources)?.reading.source ?? (kept[0] ?? shown[0])!.source;
  return suits(gauges(chosen));
}

/** A panel's readings in a character's order: those it puts first, first, then the rest as they are (as the recorder). */
export function ordered<T extends { source: string }>(readings: readonly T[], priority: readonly string[] | null | undefined): T[] {
  if (!priority?.length) return [...readings];
  const rank = new Map<string, number>();
  priority.forEach((source, i) => !rank.has(source) && rank.set(source, i));
  return readings
    .map((r, i) => ({ r, at: rank.get(r.source) ?? priority.length + i }))
    .sort((a, b) => a.at - b.at)
    .map((x) => x.r);
}

/** How long a new status must last before the character reacts to it (values hovering at a limit do not flicker). */
export const STATUS_HOLD = 2;

/** How fast a character goes along its route for each way of reacting (0: it stops where it is). */
export function paceOf(action: string): number {
  if (action === 'default' || action === 'walk') return 1;
  if (action === 'run') return 1.8;
  return 0;
}

/**
 * A character's reacting, moment to moment: its status held until a new one lasts {@link STATUS_HOLD} seconds, and
 * its own clock along its route, which runs faster while it runs and stops while it stays to do something, so it
 * carries on from where it was rather than jumping.
 */
export class Reacting {
  status: ReactionStatus | null = null;
  /** Its mind: bubbles, relief, fast-rise warnings, away and back (mind.ts). */
  readonly mind = new Mind();
  /** The prop it shows at the moment decided last (null: none): its mind's, else its reaction's. */
  prop: string | null = null;
  /** Its props as they change: the one shown and the one going. */
  readonly props = new PropTrack();
  private pending: { status: ReactionStatus; since: number } | null = null;
  private clock: number | null = null;
  private last = 0;

  /** The status held at `now` (seconds), given the status now. */
  hold(now: number, status: ReactionStatus): ReactionStatus {
    if (this.status === null) this.status = status;
    if (status === this.status) this.pending = null;
    else if (!this.pending || this.pending.status !== status) this.pending = { status, since: now };
    else if (now - this.pending.since >= STATUS_HOLD) {
      this.status = status;
      this.pending = null;
    }
    return this.status;
  }

  /** Its wheel's turning, when it runs in a ring gauge. */
  readonly wheel: { wheel: number; at: number; fill?: number } = { wheel: 0, at: Number.NaN };
  private atX: number | null = null;
  private facing: 1 | -1 = 1;
  private goalAt = Number.NaN;

  /**
   * The frame on its way to (or at) `tile`'s middle along its line, running when it is critical, doing `action` there;
   * without a tile, back to its route (a moment of its mind, a relief dance, done where it is first). Only along a line
   * (on top of or inside its panel, its screen's bottom, a spot), as the recorder.
   */
  goTo(frame: CharacterFrame, c: Pick<OverlayCharacter, 'place' | 'speed' | 'plays'>, tile: Box | null, critical: boolean, action: string, moment: string | null, box: Box, screen: Box, size: number, now: number): CharacterFrame {
    const dt = Number.isNaN(this.goalAt) ? 0 : Math.min(0.1, Math.max(0, now - this.goalAt));
    this.goalAt = now;
    if (c.place !== 'top' && c.place !== 'inside' && c.place !== 'screen' && c.place !== 'spot') {
      this.atX = null;
      return frame;
    }
    const line = c.place === 'top' || c.place === 'inside' ? box : screen;
    const low = line.left + size * 0.25;
    const goal = tile ? Math.min(Math.max(low, line.right - size * 0.25), Math.max(low, (tile.left + tile.right) / 2)) : null;
    let at = this.atX;
    if (at === null) {
      if (goal === null) return frame;
      at = frame.x;
    }
    if (goal === null && moment) {
      this.atX = at;
      return { ...frame, x: at, action: moment, facing: this.facing, seconds: now };
    }
    const target = goal ?? frame.x;
    const dx = target - at;
    const speed = (size * (critical ? 3 : 1.6) * (c.speed ?? 100)) / 100;
    at += Math.sign(dx) * Math.min(Math.abs(dx), speed * dt);
    if (goal === null && Math.abs(target - at) < 1.5) {
      this.atX = null;
      return frame;
    }
    this.atX = at;
    const has = (a: string) => !c.plays || c.plays.includes(a);
    if (Math.abs(target - at) >= 1.5) {
      this.facing = dx < 0 ? -1 : 1;
      const way = (critical ? ['run', 'walk'] : ['walk', 'run']).find(has) ?? frame.action;
      return { ...frame, x: at, action: way, facing: this.facing, seconds: now };
    }
    return { ...frame, x: at, action: moment ?? (action === 'default' || action === 'walk' || action === 'run' ? 'idle' : action), facing: this.facing, seconds: now };
  }

  /** The route's clock at `now`, having gone at `pace` since the last moment. */
  advance(now: number, pace: number): number {
    if (this.clock === null) this.clock = now;
    else this.clock += Math.max(0, Math.min(1, now - this.last)) * pace;
    this.last = now;
    return this.clock;
  }
}

/**
 * Where a reacting character is and what it does at `now` (seconds): its motion frame along its route at its own
 * clock, the reaction's action in place of its own (a way of moving while it moves; what it does in place when it
 * stops), and the effect to show. Without reactions, its plain motion frame.
 */
export function reactingFrame(
  character: OverlayCharacter,
  panel: Pick<OverlayPanel, 'readings'>,
  sources: ReadonlyMap<string, OverlaySource>,
  state: Reacting,
  box: Box,
  screen: Box,
  size: number,
  now: number,
  idle: number | null = null,
  spots: ((source: string) => Box | null) | null = null,
  gauges: ((source: string) => GaugeSpot | null) | null = null,
  senses: CharacterSenses | null = null,
): { frame: CharacterFrame; effect: string | null; status: ReactionStatus | null; kind: ReadingKind | null; thought: Thought; play: GaugePlay | null } {
  const judged = readingFor(character, panel, sources);
  const status = judged ? state.hold(now, judged.status) : null;
  const thought = state.mind.think(now, judged, status, idle, character.plays, null, senses);
  state.prop = thought.prop ?? (judged ? propFor(judged.kind, status!, reactionsFor(character, judged.source.id)) : null) ?? thought.weatherProp;
  if (character.place === 'wheel' || character.place === 'needle' || character.place === 'bar' || character.place === 'graph') {
    // On its gauge (running in the ring, riding the needle); without one on the panel, on its top edge.
    const gauge = gauges ? gaugeFor(character, panel, sources, gauges) : null;
    if (gauge) {
      const own = judged ? reactionFor(judged.kind, status!, reactionsFor(character, judged.source.id)) : { action: 'default', effect: null };
      const { frame, play } = onGauge(character, gauge, status === 'critical', thought.action ?? own.action, now, state.wheel, size);
      return { frame, effect: thought.effect ?? own.effect ?? thought.weather, status, kind: judged?.kind ?? null, thought, play };
    }
    character = { ...character, place: 'top' };
  }
  // Where its mind takes it (the mouse resting near it, a friend), as a tile's middle.
  const toward = thought.toward != null ? { left: thought.toward, top: 0, right: thought.toward, bottom: 0 } : null;
  if (!judged && !thought.action) {
    const own = state.goTo(frameOf(character, box, screen, size, state.advance(now, 1)), character, toward, false, 'default', null, box, screen, size, now);
    return { frame: faced(own, thought), effect: thought.effect ?? thought.weather, status: null, kind: null, thought, play: null };
  }
  const own = judged ? reactionFor(judged.kind, status!, reactionsFor(character, judged.source.id)) : { action: 'default', effect: null };
  // What its mind adds (a relief dance, a jump at a fast rise, asleep while the user is away) comes over the reaction.
  const reaction = { action: thought.action ?? own.action, effect: thought.effect ?? own.effect };
  const pace = paceOf(reaction.action);
  // Something done in place (dancing, sitting) is done when it pauses: it keeps its route between (wandering or pacing
  // as chosen); a way of moving (running) changes its pace.
  // Asleep it stays where it is.
  const asleep = reaction.action === 'sleep';
  const frame = frameOf(character, box, screen, size, state.advance(now, pace === 0 && !asleep ? 1 : pace));
  if (reaction.action !== 'default') {
    if (asleep) {
      frame.action = reaction.action;
      frame.seconds = now;
    } else if (pace === 0) {
      if (frame.action !== 'walk' && frame.action !== 'run') frame.action = reaction.action;
    } else if (frame.action === 'walk' || frame.action === 'run') {
      frame.action = reaction.action;
    } else if (character.movement === 'stay' || character.place === 'left' || character.place === 'right') {
      frame.action = reaction.action;
    }
  }
  // A reading turned bad: it goes to its tile and reacts there (back to its route when it is good again).
  const bad = !!judged && !!status && isBad(judged.kind, status);
  const tile = bad && spots ? spots(judged!.source.id) : null;
  const going = state.goTo(frame, character, tile ?? toward, status === 'critical' && !!tile, reaction.action, thought.action, box, screen, size, now);
  return { frame: faced(going, thought), effect: reaction.effect ?? thought.weather, status, kind: judged?.kind ?? null, thought, play: null };
}

/** The frame facing the way its mind says (the mouse, a friend), unless it is walking or running (as the recorder). */
const faced = (frame: CharacterFrame, thought: Thought): CharacterFrame => (thought.facing !== 0 && frame.action !== 'walk' && frame.action !== 'run' ? { ...frame, facing: thought.facing as 1 | -1 } : frame);
