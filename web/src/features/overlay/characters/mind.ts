import type { OverlayReading, OverlaySource } from '@/lib/api/types';
import type { CharacterSenses, CharacterWeather } from './company';
import { CONFETTI_SECONDS } from './particles';
import { KINDS, type ReactionStatus, type ReadingKind } from './reactions';

/**
 * A character's mind, moment to moment (as the recorder's CharacterMind.cs, checked against it by mind-parity.json): it
 * watches the reading it reacts to and the user, and decides a bubble when the reading turns bad ("CPU at 100 %!") or
 * good again ("Phew, CPU back to 40 %", with a little dance), a jump with "!" when the reading climbs fast while still
 * normal, asleep after {@link AWAY_SECONDS} without input and a hello on return, and a shake while it is critical.
 * With its senses (company.ts) it also notices the mouse (a hop, then it watches it, and comes over when it rests near
 * it), enjoys being petted (the mouse rubbed back and forth over it) and answers clicks, meets another character on its
 * line now and then, says good morning once a day and yawns late at night, and says and shows the weather. An attention
 * budget keeps it from chattering: a bubble at most every {@link BUBBLE_GAP} seconds (a reading turning critical, a
 * hello, a click, a petting and a meeting excepted) and a fast-rise warning at most every {@link TREND_GAP} seconds.
 */

export const AWAY_SECONDS = 180;
export const BUBBLE_GAP = 8;
export const TREND_GAP = 15;
export const TREND_WINDOW = 6;
export const TREND_SHARE = 0.35;
export const NOTICE_REACH = 4;
export const NOTICE_RISE = 2.5;
export const REST_SECONDS = 1.2;
export const COME_GAP = 0.7;
export const HOP_GAP = 20;
export const PET_STROKES = 3;
export const PET_STROKE = 0.06;
export const PET_WINDOW = 1.5;
export const CLICK_WINDOW = 4;
export const MEET_REACH = 1.3;
export const MEET_SECONDS = 5;
export const MEET_GAP = 45;
export const SEEK_REACH = 5;
export const SEEK_EVERY = 60;
export const YAWN_EVERY = 1200;
export const WEATHER_EVERY = 300;
export const WEATHER_SPELL = 40;

const CLICK_LINES = ['Hi there!', 'Hehe!', 'That tickles!'];
const PET_LINES = ['Hehe, that tickles!', 'Aww, thank you!', 'More, please!'];

export type BubbleTone = 'critical' | 'high' | 'relief' | 'info';

/** What the mind adds at a moment over its reaction. */
export interface Thought {
  /** What it does in place of its reaction's action (null: the reaction's). */
  action: string | null;
  /** The effect in place of its reaction's (null: the reaction's). */
  effect: string | null;
  /** A prop in place of its reaction's (a trophy on relief, a toolbox for an incident; null: the reaction's). */
  prop?: string | null;
  /** What it says now (null: nothing). */
  bubble: string | null;
  tone: BubbleTone;
  /** How long the bubble has been shown, and how long it is shown, in seconds. */
  bubbleAge: number;
  bubbleLife: number;
  /** 1 while what it reacts to is critical, 0 otherwise. */
  shake: number;
  /** A burst of particles from it now (confetti on a happy moment; null: none), and how long it has been going. */
  burst: string | null;
  burstAge: number;
  /** Where along its line it goes or stays now (the mouse resting near it, a friend; null: its route). */
  toward: number | null;
  /** The way it faces when it is not walking (1 right, -1 left, 0: its own). */
  facing: number;
  /** The weather's effect and prop, under its reaction's (null: none). */
  weather: string | null;
  weatherProp: string | null;
}

export const NO_THOUGHT: Thought = { action: null, effect: null, bubble: null, tone: 'info', bubbleAge: 0, bubbleLife: 0, shake: 0, burst: null, burstAge: 0, toward: null, facing: 0, weather: null, weatherProp: null };

/** Something that happened that characters react to once (incident, recording-on, recording-off, game), numbered. */
export interface CharacterEvent {
  id: number;
  kind: 'incident' | 'recording-on' | 'recording-off' | 'game' | string;
  text?: string | null;
}

/** A name or title cut to fit a bubble (28 characters, with an ellipsis). */
const short = (text: string) => (text.length <= 28 ? text : `${text.slice(0, 27).trimEnd()}…`);

/** What it reacts to (readingFor's answer). */
export interface Judged {
  reading: Pick<OverlayReading, 'critical'> & Partial<OverlayReading>;
  source: OverlaySource;
  kind: ReadingKind;
  status: ReactionStatus;
}

/** Whether a status is the bad way for a kind: critical, or the warning side (high going up; low going down). */
export const isBad = (kind: ReadingKind, status: ReactionStatus) => status === 'critical' || status === (KINDS[kind]?.bad === 'down' ? 'low' : 'high');

const pick = (plays: readonly string[] | null | undefined, ...wanted: string[]) => wanted.find((a) => !plays || plays.includes(a)) ?? null;

/** How the weather is told apart for saying it: its kind (rain, snow, storm, fog), else hot (30 °C or more), cold (0 °C or less), clear or clouds. */
const weatherKey = (w: CharacterWeather) => (w.kind === 'rain' || w.kind === 'snow' || w.kind === 'storm' || w.kind === 'fog' ? w.kind : w.temperature >= 30 ? 'hot' : w.temperature <= 0 ? 'cold' : w.kind);

function weatherText(key: string, w: CharacterWeather) {
  // Rounded half away from zero, as the recorder.
  const t = String(Math.sign(w.temperature) * Math.round(Math.abs(w.temperature)) + 0);
  if (key === 'storm') return 'Thunderstorm outside!';
  if (key === 'rain') return `Raining outside: ${t} °C`;
  if (key === 'snow') return `Snowing outside: ${t} °C`;
  if (key === 'fog') return 'Foggy outside';
  if (key === 'hot') return `Hot outside: ${t} °C`;
  if (key === 'cold') return `Brr, ${t} °C outside`;
  if (key === 'clear') return w.day ? `Sunny outside: ${t} °C` : `Clear night: ${t} °C`;
  return `Cloudy outside: ${t} °C`;
}

const criticalText = (kind: ReadingKind, label: string, value: string) =>
  kind === 'temperature' ? `${label} ${value}: too hot!` : kind === 'load' ? `${label} at ${value}!` : kind === 'fps' ? `${label} down to ${value}!` : `${label} ${value}!`;

const warningText = (kind: ReadingKind, label: string, value: string) =>
  kind === 'temperature' ? `${label} ${value}, getting hot` : kind === 'load' || kind === 'bandwidth' ? `${label} busy: ${value}` : kind === 'fps' ? `${label} dropped to ${value}` : `${label} ${value}`;

/** A value as a bubble says it (short: whole degrees and percents, bytes in binary units), as the recorder says it. */
export function formatSpoken(source: Pick<OverlaySource, 'value' | 'unit' | 'text'>): string {
  const v = source.value;
  if (v == null || !Number.isFinite(v)) return source.text ?? '?';
  const whole = (x: number) => String(Math.round(x) + 0);
  const one = (x: number) => (Math.round(x * 10) / 10 + 0).toFixed(1);
  const scaled = (x: number, units: string[]) => {
    let i = 0;
    while (Math.abs(x) >= 1024 && i < units.length - 1) {
      x /= 1024;
      i++;
    }
    return `${i === 0 ? whole(x) : one(x)} ${units[i]}`;
  };
  if (source.unit === 'B') return scaled(v, ['B', 'KB', 'MB', 'GB', 'TB']);
  if (source.unit === 'B/s') return scaled(v, ['B/s', 'KB/s', 'MB/s', 'GB/s']);
  if (source.unit === 'ms') return `${one(v)} ms`;
  if (!source.unit) return whole(v);
  return `${whole(v)} ${source.unit}`;
}

export class Mind {
  private samples: { at: number; value: number }[] = [];
  private source: string | null = null;
  private status: ReactionStatus | null = null;
  private moment: { action: string | null; effect: string | null; until: number; prop?: string } | null = null;
  private said: { text: string; tone: BubbleTone; since: number; life: number } | null = null;
  private lastBubble = Number.NEGATIVE_INFINITY;
  private lastTrend = Number.NEGATIVE_INFINITY;
  private away = false;
  private seenEvent: number | null = null;
  private confetti = Number.NEGATIVE_INFINITY;
  private seenTouch: number | null = null;
  private clicks: number[] = [];
  private holdUntil = Number.NEGATIVE_INFINITY;
  private near = false;
  private watchUntil = Number.NEGATIVE_INFINITY;
  private lastHop = Number.NEGATIVE_INFINITY;
  private rest = { x: Number.NaN, y: Number.NaN, since: 0 };
  private strokeWay = 0;
  private anchor = Number.NaN;
  private strokes: number[] = [];
  private pets = 0;
  private petUntil = Number.NEGATIVE_INFINITY;
  private meeting: { friend: string; since: number; at: number; speaker: boolean; answered: boolean } | null = null;
  private readonly met = new Map<string, number>();
  private seeking: { friend: string; since: number } | null = null;
  private lastSeek = Number.NEGATIVE_INFINITY;
  private greeted: number | null = null;
  private lastYawn: number | null = null;
  private weatherSeen: number | null = null;
  private weatherSaid: string | null = null;
  private weatherSince = 0;
  /** It had nothing else to do at its last moment (another may meet it). */
  free = false;
  /** Whom it was meeting at its last moment (null: no one). */
  get meetingWith(): string | null {
    return this.meeting?.friend ?? null;
  }

  /**
   * What the mind adds at `now` (seconds): `judged` is what the character reacts to (null: nothing) and `status` its
   * status as held; `idle` how long the user has not touched the keyboard or mouse (null: not known); `plays` the
   * animations it uses (null: all).
   */
  think(now: number, judged: Judged | null, status: ReactionStatus | null, idle: number | null, plays: readonly string[] | null | undefined, happened: CharacterEvent | null = null, senses: CharacterSenses | null = null): Thought {
    const bad = !!judged && !!status && isBad(judged.kind, status);
    if (judged && status) this.watch(now, judged, status, plays);
    // A new event, once (those before its first moment, and while the user is away, are let go).
    if (happened && this.seenEvent !== null && happened.id > this.seenEvent && !this.away) this.react(now, happened, plays);
    if (happened) this.seenEvent = happened.id;
    else this.seenEvent ??= 0;
    if (idle != null) {
      if (!this.away && idle >= AWAY_SECONDS && !bad) {
        this.away = true;
        this.said = null;
      } else if (this.away && (idle < 5 || bad)) {
        this.away = false;
        if (!bad) {
          this.moment = { action: pick(plays, 'cheer', 'jump', 'play', 'dance'), effect: null, until: now + 2.5, prop: 'coffee' };
          this.confetti = now;
          // Back in the morning, not greeted yet today: a good morning in place of the hello.
          const morning = !!senses && senses.hour != null && senses.hour >= 5 && senses.hour < 11 && this.greeted !== senses.day;
          if (morning) this.greeted = senses!.day;
          this.say(now, morning ? 'Good morning!' : 'Welcome back!', 'info', true);
        }
      }
    }
    if (this.away) {
      this.free = false;
      this.meeting = null;
      return { ...NO_THOUGHT, action: pick(plays, 'sleep', 'sit', 'idle'), effect: 'zzz' };
    }
    if (this.moment && now >= this.moment.until) this.moment = null;
    const out = { toward: null as number | null, facing: 0 };
    let weather: [string | null, string | null] = [null, null];
    if (senses) {
      this.touch(now, senses, plays);
      const watching = this.watchCursor(now, senses, plays, bad, out);
      this.company(now, senses, plays, bad, watching, out);
      const free = !bad && !this.moment && !this.meeting && !watching;
      this.clock(now, senses, plays, free);
      weather = this.weatherNow(now, senses, free && !this.moment);
      // Clicked, petted or noticing: it stays where it is meanwhile.
      if (now < this.holdUntil && senses.line) out.toward ??= senses.x;
      this.free = !bad && !this.moment && !this.meeting && !watching;
    } else this.free = !bad && !this.moment;
    let action: string | null = null;
    let effect: string | null = null;
    let prop: string | null = null;
    if (this.moment) ({ action, effect, prop = null } = this.moment);
    else if (this.meeting) {
      // A hello (hearts), then a dance together (notes).
      const together = now - this.meeting.since >= 1.5;
      [action, effect] = together ? [pick(plays, 'dance', 'play', 'cheer', 'jump'), 'notes'] : [null, 'hearts'];
    }
    let thought: Thought = { ...NO_THOUGHT, action, effect, shake: status === 'critical' && judged ? 1 : 0, ...(prop ? { prop } : {}), toward: out.toward, facing: out.facing, weather: weather[0], weatherProp: weather[1] };
    if (now - this.confetti < CONFETTI_SECONDS) thought = { ...thought, burst: 'confetti', burstAge: now - this.confetti };
    if (this.said) {
      if (now - this.said.since < this.said.life) return { ...thought, bubble: this.said.text, tone: this.said.tone, bubbleAge: now - this.said.since, bubbleLife: this.said.life };
      this.said = null;
    }
    return thought;
  }

  /** A new click on it: a jump and a word (the first three within CLICK_WINDOW), dizzy after more. */
  private touch(now: number, s: CharacterSenses, plays: readonly string[] | null | undefined) {
    if (this.seenTouch !== null && s.touches > this.seenTouch) {
      this.clicks.push(now);
      while (this.clicks[0]! < now - CLICK_WINDOW) this.clicks.shift();
      if (this.clicks.length > CLICK_LINES.length) {
        this.moment = { action: pick(plays, 'sit', 'idle'), effect: 'stars', until: now + 3 };
        this.say(now, "Whoa, I'm dizzy…", 'high', true);
      } else {
        this.moment = { action: pick(plays, 'jump', 'play', 'cheer'), effect: null, until: now + 1.2 };
        this.say(now, CLICK_LINES[this.clicks.length - 1]!, 'info', true);
      }
      this.holdUntil = this.moment.until;
      this.meeting = null;
      this.seeking = null;
    }
    this.seenTouch = s.touches;
  }

  /**
   * The mouse: petted when it is rubbed back and forth over it (whatever its reading says); otherwise, unless its reading
   * is bad, a hop when the mouse comes near, then it faces it and stays, and comes over when it rests. True while it
   * watches the mouse.
   */
  private watchCursor(now: number, s: CharacterSenses, plays: readonly string[] | null | undefined, bad: boolean, out: { toward: number | null; facing: number }): boolean {
    if (!s.cursor) {
      this.near = false;
      this.strokeWay = 0;
      this.strokes = [];
      this.watchUntil = Number.NEGATIVE_INFINITY;
      return false;
    }
    const { x: cx, y: cy } = s.cursor;
    const h = Math.max(1, s.size);
    const dx = cx - s.x;
    const dy = cy - (s.y - h / 2);
    const way = Math.abs(dx) > 0.15 * h ? Math.sign(dx) : 0;
    // Petting: each turn of the mouse over it (a stroke one way, then back) counts.
    const over = Math.abs(dx) <= 0.45 * h && cy >= s.y - h && cy <= s.y + 0.1 * h;
    if (!over) {
      this.strokeWay = 0;
      this.anchor = cx;
    } else if (Number.isNaN(this.anchor)) this.anchor = cx;
    else if (this.strokeWay === 0) {
      if (Math.abs(cx - this.anchor) >= PET_STROKE * h) {
        this.strokeWay = Math.sign(cx - this.anchor);
        this.anchor = cx;
      }
    } else if (Math.sign(cx - this.anchor) === this.strokeWay) this.anchor = cx;
    else if (Math.abs(cx - this.anchor) >= PET_STROKE * h) {
      this.strokes.push(now);
      this.strokeWay = -this.strokeWay;
      this.anchor = cx;
    }
    while (this.strokes.length > 0 && this.strokes[0]! < now - PET_WINDOW) this.strokes.shift();
    if (this.strokes.length >= PET_STROKES) {
      if (now >= this.petUntil) {
        // A new petting (not the same one going on).
        this.say(now, PET_LINES[this.pets % PET_LINES.length]!, 'relief', true);
        this.pets++;
      }
      this.petUntil = now + 1.5;
      this.holdUntil = this.petUntil;
      this.moment = { action: pick(plays, 'cheer', 'dance', 'play', 'jump'), effect: 'hearts', until: this.petUntil };
      this.meeting = null;
      this.seeking = null;
    }
    const near = Math.abs(dx) <= NOTICE_REACH * h && Math.abs(dy) <= NOTICE_RISE * h;
    if (Math.abs(cx - this.rest.x) > 0.25 * h || Math.abs(cy - this.rest.y) > 0.25 * h || Number.isNaN(this.rest.x)) this.rest = { x: cx, y: cy, since: now };
    if (bad) {
      // Its reading comes first.
      this.near = near;
      this.watchUntil = Number.NEGATIVE_INFINITY;
      if (this.moment) out.facing = way;
      return false;
    }
    if (near) {
      if (!this.near && !this.moment && !this.meeting && now - this.lastHop >= HOP_GAP) {
        this.lastHop = now;
        this.moment = { action: pick(plays, 'jump', 'play'), effect: null, until: now + 1 };
        this.holdUntil = now + 1;
      }
      this.watchUntil = now + 2;
    }
    this.near = near;
    if (now >= this.watchUntil && !this.moment) return false;
    out.facing = way;
    if (s.line && now < this.watchUntil) {
      const comes = near && now - this.rest.since >= REST_SECONDS && Math.abs(dx) > COME_GAP * h + 2 && pick(plays, 'walk', 'run') !== null;
      out.toward = comes ? cx - Math.sign(dx) * COME_GAP * h : s.x;
    }
    return now < this.watchUntil;
  }

  /**
   * Another character on its line: two free ones this close meet (a hello each way, then a dance together); the first
   * of the two (by id) walks over to the other now and then.
   */
  private company(now: number, s: CharacterSenses, plays: readonly string[] | null | undefined, bad: boolean, watching: boolean, out: { toward: number | null; facing: number }) {
    const f = s.friend;
    const m = this.meeting;
    if (m) {
      if (bad || !f || f.id !== m.friend || now - m.since >= MEET_SECONDS || this.moment) {
        this.met.set(m.friend, now);
        this.meeting = null;
      } else {
        out.facing = f.x > s.x ? 1 : f.x < s.x ? -1 : out.facing;
        if (s.line) out.toward = m.at;
        if (!m.speaker && !m.answered && now - m.since >= 1.2) {
          this.say(now, `Hey ${f.name}!`, 'info', true);
          m.answered = true;
        }
        return;
      }
    }
    if (!f || bad || this.moment || watching || !s.line) {
      this.seeking = null;
      return;
    }
    const h = (s.size + f.size) / 2;
    const gap = Math.abs(f.x - s.x);
    const last = this.met.get(f.id);
    const rested = last === undefined || now - last >= MEET_GAP;
    // The first of the two by id (ordinal, as the recorder's string.CompareOrdinal).
    const speaker = s.self < f.id;
    if (gap <= MEET_REACH * h && rested && (f.free || f.meetingWith === s.self)) {
      this.meeting = { friend: f.id, since: now, at: s.x, speaker, answered: false };
      this.seeking = null;
      out.facing = f.x > s.x ? 1 : f.x < s.x ? -1 : out.facing;
      out.toward = s.x;
      if (speaker) this.say(now, `Hi ${f.name}!`, 'info', true);
      return;
    }
    if (this.seeking && (this.seeking.friend !== f.id || now - this.seeking.since >= 10 || !rested || !f.free)) this.seeking = null;
    if (!this.seeking && speaker && rested && f.free && gap <= SEEK_REACH * h && now - this.lastSeek >= SEEK_EVERY && pick(plays, 'walk', 'run') !== null) {
      this.seeking = { friend: f.id, since: now };
      this.lastSeek = now;
    }
    if (this.seeking) out.toward = f.x - (f.x > s.x ? 1 : -1) * h;
  }

  /** The time of day: a good morning once a day (5 to 11), a yawn every YAWN_EVERY seconds late at night (midnight to 5). */
  private clock(now: number, s: CharacterSenses, plays: readonly string[] | null | undefined, free: boolean) {
    const hour = s.hour;
    if (hour == null) return;
    this.lastYawn ??= now;
    if (!free) return;
    if (hour >= 5 && hour < 11 && this.greeted !== s.day) {
      this.greeted = s.day;
      this.moment = { action: pick(plays, 'cheer', 'jump', 'play', 'dance'), effect: null, until: now + 3, prop: 'coffee' };
      this.say(now, 'Good morning!', 'info', true);
    } else if (hour < 5 && now - this.lastYawn >= YAWN_EVERY) {
      this.lastYawn = now;
      this.moment = { action: pick(plays, 'tired', 'sleep', 'sit', 'idle'), effect: 'zzz', until: now + 3 };
      this.say(now, "It's late… bedtime?", 'info');
    }
  }

  /** The weather: said when it changes (while it is free), shown for WEATHER_SPELL seconds of every WEATHER_EVERY. */
  private weatherNow(now: number, s: CharacterSenses, free: boolean): [string | null, string | null] {
    const w = s.weather;
    if (!w) {
      this.weatherSaid = null;
      return [null, null];
    }
    const key = weatherKey(w);
    if (this.weatherSeen !== w.id) {
      this.weatherSeen = w.id;
      if (key !== this.weatherSaid) {
        this.weatherSaid = key;
        this.weatherSince = now;
        if (free) this.say(now, weatherText(key, w), 'info');
      }
    }
    if (!free || (now - this.weatherSince) % WEATHER_EVERY >= WEATHER_SPELL) return [null, null];
    if (key === 'rain' || key === 'storm') return ['rain', 'umbrella'];
    if (key === 'snow') return ['snow', null];
    if (key === 'hot') return ['sweat', 'desk-fan'];
    if (key === 'cold') return [null, 'coffee'];
    return w.wind >= 10 ? ['wind', null] : [null, null];
  }

  private watch(now: number, judged: Judged, status: ReactionStatus, plays: readonly string[] | null | undefined) {
    const value = judged.source.value ?? Number.NaN;
    if (this.source !== judged.source.id) {
      this.samples = [];
      this.source = judged.source.id;
    }
    if (Number.isFinite(value)) {
      this.samples.push({ at: now, value });
      while (this.samples.length > 0 && this.samples[0]!.at < now - TREND_WINDOW) this.samples.shift();
    }
    const label = judged.source.label;
    const shown = formatSpoken(judged.source);
    const was = this.status;
    if (was !== null && was !== status) {
      if (status === 'critical') this.say(now, criticalText(judged.kind, label, shown), 'critical', true);
      else if (isBad(judged.kind, status)) this.say(now, warningText(judged.kind, label, shown), 'high');
      else if (isBad(judged.kind, was)) {
        // Back from critical it is always said (the end of an alarm), whatever the budget.
        this.say(now, `Phew, ${label} back to ${shown}`, 'relief', was === 'critical');
        this.moment = { action: pick(plays, 'cheer', 'dance', 'jump', 'play'), effect: 'notes', until: now + 3, prop: 'trophy' };
        this.confetti = now;
      } else if (judged.kind === 'fps' && status === 'high') this.say(now, `${shown}! Smooth`, 'relief');
    }
    this.status = status;

    const rules = KINDS[judged.kind];
    if (status !== 'normal' || now - this.lastTrend < TREND_GAP || this.samples.length < 2 || !rules || rules.bad === 'down') return;
    const top = judged.reading.critical ?? rules.critical;
    if (top == null || rules.share) return;
    const span = top - (rules.low ?? 0);
    const lowest = Math.min(...this.samples.map((s) => s.value));
    if (span > 0 && value - lowest >= TREND_SHARE * span && now - this.samples[0]!.at >= 1) {
      this.lastTrend = now;
      this.moment = { action: pick(plays, 'worried', 'jump', 'run', 'play'), effect: 'exclaim', until: now + 2 };
      this.say(now, `${label} rising: ${shown}`, 'high');
    }
  }

  private react(now: number, e: CharacterEvent, plays: readonly string[] | null | undefined) {
    if (e.kind === 'incident') {
      this.moment = { action: pick(plays, 'panic', 'jump', 'run', 'play'), effect: 'exclaim', until: now + 3, prop: 'toolbox' };
      this.say(now, e.text ? `Incident saved: ${short(e.text)}` : 'Incident saved', 'critical', true);
    } else if (e.kind === 'recording-on') {
      this.moment = { action: pick(plays, 'play', 'jump', 'dance'), effect: null, until: now + 2 };
      this.say(now, 'Recording started', 'info', true);
    } else if (e.kind === 'recording-off') {
      this.say(now, 'Recording stopped', 'info', true);
    } else if (e.kind === 'game') {
      this.moment = { action: pick(plays, 'cheer', 'dance', 'jump', 'play'), effect: 'notes', until: now + 3, prop: 'popcorn' };
      this.confetti = now;
      this.say(now, e.text ? `${short(e.text)} started: have fun!` : 'Game on: have fun!', 'relief', true);
    }
  }

  private say(now: number, text: string, tone: BubbleTone, force = false) {
    if (!force && now - this.lastBubble < BUBBLE_GAP) return;
    this.said = { text, tone, since: now, life: tone === 'critical' ? 5 : 4 };
    this.lastBubble = now;
  }
}
