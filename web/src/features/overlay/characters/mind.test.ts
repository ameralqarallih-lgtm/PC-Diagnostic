import { describe, expect, it } from 'vitest';
import type { OverlaySource } from '@/lib/api/types';
import parity from './mind-parity.json';
import type { CharacterFriend, CharacterSenses, CharacterWeather } from './company';
import { AWAY_SECONDS, BUBBLE_GAP, MEET_GAP, YAWN_EVERY, Mind, formatSpoken, type CharacterEvent, type Judged, type Thought } from './mind';
import { Reacting, kindOf, statusOf, type ReactionStatus } from './reactions';

interface Step {
  now: number;
  source: { id: string; label: string; unit: string; value: number | null } | null;
  critical: number | null;
  status: ReactionStatus | null;
  idle: number | null;
  plays: string[] | null;
  event?: CharacterEvent | null;
  senses?: CharacterSenses | null;
}

const judge = (step: Step): Judged | null => {
  if (!step.source || !step.status) return null;
  const source: OverlaySource = { group: '', ...step.source };
  return { reading: { source: step.source.id, critical: step.critical }, source, kind: kindOf(source.id, source.unit), status: step.status };
};

/** A day in a character's life: what it reacts to, second by second, and the user coming and going. */
function scenario(): Step[] {
  const steps: Step[] = [];
  const cpu = (now: number, value: number, idle: number | null = 1, plays: string[] | null = null) => {
    const status = statusOf('load', undefined, value);
    steps.push({ now, source: { id: 'cpu.usage', label: 'CPU', unit: '%', value }, critical: null, status, idle, plays });
  };
  let t = 0;
  for (; t < 5; t++) cpu(t, 40);
  // A fast rise while still normal (40 → 70 % in 3 s), then high, then critical, then back to normal.
  cpu(t++, 50);
  cpu(t++, 60);
  cpu(t++, 70);
  for (; t < 20; t++) cpu(t, 80);
  for (; t < 30; t++) cpu(t, 96);
  for (; t < 45; t++) cpu(t, 30);
  // The user away for longer than AWAY_SECONDS, then back.
  for (let idle = 0; idle <= AWAY_SECONDS + 20; idle += 20, t += 20) cpu(t, 30, idle);
  for (let i = 0; i < 6; i++, t++) cpu(t, 30, 0.5);
  // A character without a dance (the relief picks the next it has), and a temperature with its own critical limit.
  for (let i = 0; i < 12; i++, t++) cpu(t, i < 6 ? 95 : 20, 1, ['idle', 'walk', 'jump']);
  for (const value of [60, 70, 82, 91, 91, 91, 50]) {
    steps.push({ now: t++, source: { id: 'gpu.temp', label: 'GPU temp', unit: '°C', value }, critical: 90, status: statusOf('temperature', { critical: 90 }, value), idle: null, plays: null });
  }
  steps.push({ now: t++, source: null, critical: null, status: null, idle: null, plays: null });
  // Events: each reacted to once (the same one again is not), an incident's long title cut short.
  const event = (e: CharacterEvent | null, n = 1) => {
    for (let i = 0; i < n; i++) steps.push({ now: t++, source: null, critical: null, status: null, idle: null, plays: null, event: e });
  };
  event({ id: 1, kind: 'recording-on' }, 3);
  event({ id: 2, kind: 'incident', text: 'Application crash: GameLauncher.exe stopped responding' }, 6);
  event({ id: 3, kind: 'game', text: 'Cyberpunk 2077' }, 4);
  event({ id: 4, kind: 'recording-off' }, 2);
  steps.push(...senseSteps(t + 10));
  return steps;
}

/**
 * What it senses, from `t`: the mouse coming near, resting, rubbed over it; clicks; other characters (one it meets,
 * one that meets it, one it walks over to); the morning and late at night; the weather changing. In tenths of seconds.
 */
function senseSteps(t: number): Step[] {
  const steps: Step[] = [];
  const base: CharacterSenses = { self: 'p/m', x: 500, y: 400, size: 60, cursor: null, touches: 0, friend: null, weather: null, hour: null, day: 100, line: true };
  let now = t;
  const at = (s: Partial<CharacterSenses>, seconds = 0.1) => {
    steps.push({ now: Math.round(now * 1000) / 1000, source: null, critical: null, status: null, idle: null, plays: null, senses: { ...base, ...s } });
    now += seconds;
  };
  const many = (n: number, s: Partial<CharacterSenses>, seconds = 0.1) => {
    for (let i = 0; i < n; i++) at(s, seconds);
  };
  // Far, then near (a hop, then it watches), resting there (it comes over), moving about near it (it stays).
  many(3, { cursor: { x: 1000, y: 380 } });
  many(25, { cursor: { x: 640, y: 380 } });
  for (let i = 0; i < 6; i++) at({ cursor: { x: 640 + (i % 2) * 40, y: 380 } });
  // Petted: rubbed back and forth over it, then the mouse gone.
  for (let i = 0; i < 12; i++) at({ cursor: { x: 495 + (i % 2) * 12, y: 370 } });
  many(40, { cursor: null });
  // Clicks: a word for each of the first three, dizzy at the fourth.
  for (let i = 1; i <= 4; i++) at({ touches: i }, 0.5);
  many(8, { touches: 4 }, 0.5);
  // Another one beside it, free: they meet (it speaks first: its id comes first), then not again for a while.
  const biscuit: CharacterFriend = { id: 'p/x', name: 'Biscuit', x: 560, size: 60, free: true, meetingWith: null };
  many(60, { touches: 4, friend: biscuit });
  many(10, { touches: 4, friend: { ...biscuit, free: false } });
  // One whose id comes first meets it: it answers.
  many(60, { touches: 4, friend: { id: 'p/a', name: 'Mochi', x: 450, size: 60, free: false, meetingWith: 'p/m' } });
  // One further off: it walks over (the first of the two), and stops when the other is busy.
  many(10, { touches: 4, friend: { id: 'p/z', name: 'Kuma', x: 760, size: 60, free: true, meetingWith: null } });
  many(5, { touches: 4, friend: { id: 'p/z', name: 'Kuma', x: 760, size: 60, free: false, meetingWith: null } });
  // The morning (once a day), late at night (a yawn now and then).
  many(40, { touches: 4, hour: 7.5 });
  many(5, { touches: 4, hour: 7.6 });
  many(3, { touches: 4, hour: 1 });
  now += YAWN_EVERY;
  many(40, { touches: 4, hour: 1.2 });
  // The weather: rain (said, shown a while, then not, then again), cold, hot, windy clouds.
  const weather = (id: number, kind: string, temperature: number, wind = 2, day = true): CharacterWeather => ({ id, kind, temperature, wind, day });
  // (After the bubble gap: the yawn's word was just said.)
  now += 10;
  many(20, { touches: 4, weather: weather(1, 'rain', 12.4) }, 1);
  many(3, { touches: 4, weather: weather(1, 'rain', 12.4) }, 100);
  many(12, { touches: 4, weather: weather(2, 'clear', -2.5) }, 1);
  many(12, { touches: 4, weather: weather(3, 'clear', 31) }, 1);
  many(12, { touches: 4, weather: weather(4, 'clouds', 14, 12) }, 1);
  many(12, { touches: 4, weather: weather(5, 'clouds', 14.2, 12) }, 1);
  return steps;
}

function run(steps: Step[]): Thought[] {
  const mind = new Mind();
  return steps.map((s) => mind.think(s.now, judge(s), s.status, s.idle, s.plays, s.event ?? null, s.senses ?? null));
}

describe('A character’s mind', () => {
  it('says when a reading turns bad or good again, warns of a fast rise, sleeps while the user is away and keeps quiet between', () => {
    const steps = scenario();
    const thoughts = run(steps);
    const at = (now: number) => thoughts[steps.findIndex((s) => s.now === now)]!;
    expect(at(7)).toMatchObject({ action: 'worried', effect: 'exclaim', bubble: 'CPU rising: 70 %', tone: 'high' });
    // High (75 %) right after the rise warning: within the bubble gap, so the rise's bubble stays.
    expect(thoughts.filter((t) => t.bubble === 'CPU busy: 80 %')).toHaveLength(0);
    expect(at(20)).toMatchObject({ bubble: 'CPU at 96 %!', tone: 'critical', shake: 1 });
    expect(at(30)).toMatchObject({ action: 'cheer', effect: 'notes', bubble: 'Phew, CPU back to 30 %', tone: 'relief', shake: 0 });
    expect(at(30)).toMatchObject({ burst: 'confetti', burstAge: 0 });
    expect(at(32)).toMatchObject({ burst: null });
    const asleep = thoughts.find((_, i) => (steps[i]!.idle ?? 0) >= AWAY_SECONDS);
    expect(asleep).toMatchObject({ action: 'sleep', effect: 'zzz', bubble: null });
    const back = thoughts.find((th) => th.bubble === 'Welcome back!');
    expect(back).toMatchObject({ action: 'cheer', tone: 'info' });
    expect(thoughts.some((th) => th.bubble === 'Phew, CPU back to 20 %' && th.action === 'jump')).toBe(true);
    expect(thoughts.some((th) => th.bubble === 'GPU temp 91 °C: too hot!')).toBe(true);
    expect(thoughts.some((th) => th.bubble === 'Phew, GPU temp back to 50 °C')).toBe(true);
    expect(thoughts.some((th) => th.bubble === 'Recording started')).toBe(true);
    expect(thoughts.find((th) => th.bubble?.startsWith('Incident saved'))).toMatchObject({ bubble: 'Incident saved: Application crash: GameLaun…', tone: 'critical', action: 'panic', effect: 'exclaim' });
    expect(thoughts.find((th) => th.bubble?.endsWith('have fun!'))).toMatchObject({ bubble: 'Cyberpunk 2077 started: have fun!', action: 'cheer' });
    expect(thoughts.filter((th) => th.bubble === 'Recording stopped' && th.bubbleAge === 0)).toHaveLength(1);
    // The gap between bubbles (critical excepted).
    const said = thoughts.map((th, i) => ({ th, at: steps[i]!.now, event: !!steps[i]!.event })).filter((x, i, all) => !steps[i]!.senses && x.th.bubble && x.th.bubbleAge === 0 && (i === 0 || all[i - 1]!.th.bubble !== x.th.bubble));
    for (let i = 1; i < said.length; i++) if (!said[i]!.event && said[i]!.th.tone !== 'critical' && said[i]!.th.bubble !== 'Welcome back!' && said[i - 1]!.th.tone !== 'critical') expect(said[i]!.at - said[i - 1]!.at).toBeGreaterThanOrEqual(BUBBLE_GAP);
  });

  it('goes to the tile of a reading that turns bad, reacts there, and walks back into its route', () => {
    const state = new Reacting();
    const c = { place: 'screen', speed: 100, plays: null } as const;
    const box = { left: 0, top: 0, right: 1000, bottom: 100 };
    const route = { x: 100, y: 100, action: 'idle', seconds: 0, facing: 1 as const };
    const tile = { left: 600, top: 0, right: 700, bottom: 80 };
    let f = state.goTo(route, c, tile, true, 'jump', null, box, box, 64, 0);
    for (let t = 0.05; t < 1.2; t += 0.05) f = state.goTo(route, c, tile, true, 'jump', null, box, box, 64, t);
    expect(f.action).toBe('run');
    expect(f.facing).toBe(1);
    for (let t = 1.2; t < 4; t += 0.05) f = state.goTo(route, c, tile, true, 'jump', null, box, box, 64, t);
    expect(f).toMatchObject({ x: 650, action: 'jump' });
    // Good again: its relief first, where it is, then back along the line (walking) to its route.
    expect(state.goTo(route, c, null, false, 'default', 'dance', box, box, 64, 4)).toMatchObject({ x: 650, action: 'dance' });
    f = state.goTo(route, c, null, false, 'default', null, box, box, 64, 4.05);
    expect(f).toMatchObject({ action: 'walk', facing: -1 });
    for (let t = 4.1; t < 12; t += 0.05) f = state.goTo(route, c, null, false, 'default', null, box, box, 64, t);
    expect(f).toBe(route);
    // Beside its panel it keeps to its route.
    expect(new Reacting().goTo(route, { ...c, place: 'left' }, tile, true, 'jump', null, box, box, 64, 0)).toBe(route);
  });

  it('watches the mouse, enjoys a petting, answers clicks, meets the others, greets the morning and tells the weather', () => {
    const steps = scenario();
    const thoughts = run(steps);
    const sensing = steps.map((s, i) => ({ s, th: thoughts[i]! })).filter((x) => x.s.senses);
    const first = (test: (th: Thought) => boolean) => sensing.find((x) => test(x.th));
    // The mouse near: a hop, it faces it and stays; resting there, it comes over (to 0.7 of its height from it).
    expect(first((th) => th.facing === 1)?.th).toMatchObject({ action: 'jump', toward: 500 });
    expect(first((th) => th.toward === 640 - 0.7 * 60)).toBeTruthy();
    // Petted: hearts, a dance, a word; it stays where it is.
    expect(first((th) => th.effect === 'hearts' && th.action === 'cheer')?.th).toMatchObject({ bubble: 'Hehe, that tickles!', tone: 'relief', toward: 500 });
    // Clicks.
    for (const word of ['Hi there!', 'Hehe!', 'That tickles!']) expect(first((th) => th.bubble === word && th.bubbleAge === 0)?.th).toMatchObject({ action: 'jump' });
    expect(first((th) => th.bubble === "Whoa, I'm dizzy…")?.th).toMatchObject({ effect: 'stars', action: 'sit' });
    // Meeting Biscuit: a hello with hearts, facing it, then a dance with notes; once only within MEET_GAP.
    const hello = first((th) => th.bubble === 'Hi Biscuit!')!;
    expect(hello.th).toMatchObject({ effect: 'hearts', facing: 1, toward: 500 });
    expect(sensing.some((x) => x.s.senses!.friend?.id === 'p/x' && x.th.action === 'dance' && x.th.effect === 'notes')).toBe(true);
    expect(sensing.filter((x) => x.th.bubble === 'Hi Biscuit!' && x.th.bubbleAge === 0)).toHaveLength(1);
    expect(MEET_GAP).toBeGreaterThan(6);
    // Mochi meets it: it answers, facing left.
    expect(first((th) => th.bubble === 'Hey Mochi!')?.th).toMatchObject({ facing: -1 });
    // Kuma further off: it walks over (to a height from it), and stops when Kuma is busy.
    const seek = sensing.filter((x) => x.s.senses!.friend?.id === 'p/z');
    expect(seek[0]!.th.toward).toBe(760 - 60);
    expect(seek.at(-1)!.th.toward).toBeNull();
    // The morning once, a yawn late at night.
    expect(sensing.filter((x) => x.th.bubble === 'Good morning!' && x.th.bubbleAge === 0)).toHaveLength(1);
    expect(first((th) => th.bubble === 'Good morning!')?.th).toMatchObject({ prop: 'coffee' });
    expect(first((th) => th.bubble === "It's late… bedtime?")?.th).toMatchObject({ effect: 'zzz' });
    // The weather: said when it changes, shown now and then.
    expect(first((th) => th.bubble === 'Raining outside: 12 °C')?.th).toMatchObject({ weather: 'rain', weatherProp: 'umbrella' });
    const rain = sensing.filter((x) => x.s.senses!.weather?.id === 1);
    expect(rain.some((x) => x.th.weather === null)).toBe(true);
    expect(first((th) => th.bubble === 'Brr, -3 °C outside')?.th).toMatchObject({ weatherProp: 'coffee' });
    expect(first((th) => th.bubble === 'Hot outside: 31 °C')?.th).toMatchObject({ weather: 'sweat', weatherProp: 'desk-fan' });
    expect(first((th) => th.bubble === 'Cloudy outside: 14 °C')?.th).toMatchObject({ weather: 'wind' });
    // The same weather again (a new report) is not said again.
    expect(sensing.filter((x) => x.th.bubble?.startsWith('Cloudy') && x.th.bubbleAge === 0)).toHaveLength(1);
  });

  it('says values short, as the recorder does', () => {
    expect(formatSpoken({ value: 96.4, unit: '%', text: null })).toBe('96 %');
    expect(formatSpoken({ value: 38_000_000_000, unit: 'B', text: null })).toBe('35.4 GB');
    expect(formatSpoken({ value: 1_500_000, unit: 'B/s', text: null })).toBe('1.4 MB/s');
    expect(formatSpoken({ value: 16.66, unit: 'ms', text: null })).toBe('16.7 ms');
    expect(formatSpoken({ value: -0.2, unit: '', text: null })).toBe('0');
    expect(formatSpoken({ value: null, unit: '%', text: 'n/a' })).toBe('n/a');
  });

  it('thinks as the recorder thinks (mind-parity.json, which the recorder’s tests replay)', async () => {
    const steps = scenario();
    const cases = { steps, thoughts: run(steps).map((t) => ({ ...t, bubbleAge: Math.round(t.bubbleAge * 1000) / 1000, burstAge: Math.round(t.burstAge * 1000) / 1000 })) };
    // UPDATE_CHARACTER_PARITY=1 writes it again (after a change to the rules).
    const node = (globalThis as unknown as { process?: { env: Record<string, string | undefined>; cwd: () => string } }).process;
    if (node?.env.UPDATE_CHARACTER_PARITY) {
      const fs = (await import(/* @vite-ignore */ ['node', 'fs'].join(':'))) as { writeFileSync: (file: string, text: string) => void };
      fs.writeFileSync(`${node.cwd()}/src/features/overlay/characters/mind-parity.json`, JSON.stringify(cases));
      return;
    }
    expect(parity).toEqual(cases);
  });
});
