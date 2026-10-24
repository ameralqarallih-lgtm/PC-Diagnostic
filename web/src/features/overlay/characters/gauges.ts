import type { OverlayCharacter } from '@/lib/api/types';
import type { CharacterFrame } from './behaviour';

/**
 * A gauge as drawn (a ring, arc or speedometer), for a character to play on, in the same pixels as the character (as
 * the recorder's GaugeHit): its middle, the radius of its stroke's middle, its stroke, its kind, how far it is filled
 * (0-1), and where its fill starts and how far it can go, in degrees clockwise from the right.
 */
export interface GaugeSpot {
  cx: number;
  cy: number;
  radius: number;
  thickness: number;
  visual: string;
  fill: number;
  start: number;
  sweep: number;
  /** A bar's or a graph's box. */
  box?: { left: number; top: number; right: number; bottom: number };
  /** A graph's line, its points left to right. */
  line?: [number, number][];
}

/** Whether a gauge suits a place (as the recorder): a ring, arc or speedometer for a wheel or a needle, a bar for a bar, a line graph for a graph. */
export const fits = (place: string, visual: string) => (place === 'bar' ? visual === 'bar' : place === 'graph' ? visual === 'graph' : visual === 'ring' || visual === 'arc' || visual === 'speed');

/** The height of a graph's line at `x` (between its points; its ends beyond them), as the recorder's GaugeHit.LineAt. */
export function lineAt(gauge: GaugeSpot, x: number): number {
  const line = gauge.line;
  if (!line?.length) return gauge.box?.bottom ?? gauge.cy;
  if (x <= line[0]![0]) return line[0]![1];
  for (let i = 1; i < line.length; i++) {
    const [ax, ay] = line[i - 1]!;
    const [bx, by] = line[i]!;
    if (x <= bx) return bx - ax < 0.001 ? by : ay + ((by - ay) * (x - ax)) / (bx - ax);
  }
  return line[line.length - 1]![1];
}

/** A character playing on a gauge at a moment: on which, its kind, how tall it is drawn, and how far the wheel has turned. */
export interface GaugePlay {
  kind: 'wheel' | 'needle' | 'bar' | 'graph';
  gauge: GaugeSpot;
  size: number;
  spin: number;
}

/**
 * A reading's gauge on the editor's screen (marked data-gauge by PanelPreview), in the same pixels as `el` (a
 * character's canvas): null when it has none.
 */
export function gaugeOf(el: HTMLElement, source: string): GaugeSpot | null {
  const host = el.offsetParent as HTMLElement | null;
  if (!host) return null;
  const id = typeof CSS !== 'undefined' && CSS.escape ? CSS.escape(source) : source.replace(/["\\]/g, '\\$&');
  const svg = host.querySelector(`[data-gauge][data-source="${id}"]`) as HTMLElement | SVGElement | null;
  if (!svg) return null;
  const outer = host.getBoundingClientRect();
  const r = svg.getBoundingClientRect();
  const k = host.offsetWidth ? outer.width / host.offsetWidth : 1;
  const box = { left: (r.left - outer.left) / k, top: (r.top - outer.top) / k, right: (r.right - outer.left) / k, bottom: (r.bottom - outer.top) / k };
  const visual = svg.dataset.gauge ?? 'ring';
  if (visual === 'bar' || visual === 'graph') {
    // A graph's line from its drawing's units to the screen's pixels (it is drawn stretched to its box).
    const [uw, uh] = (svg.dataset.units ?? '1 1').split(' ').map(Number);
    const line = (svg.dataset.line ?? '')
      .split(' ')
      .filter(Boolean)
      .map((pair) => pair.split(',').map(Number) as [number, number])
      .map(([x, y]) => [box.left + (x / (uw || 1)) * (box.right - box.left), box.top + (y / (uh || 1)) * (box.bottom - box.top)] as [number, number]);
    return { cx: (box.left + box.right) / 2, cy: (box.top + box.bottom) / 2, radius: 0, thickness: box.bottom - box.top, visual, fill: Math.min(1, Math.max(0, Number(svg.dataset.fill) || 0)), start: 0, sweep: 0, box, line };
  }
  const side = Math.min(r.width, r.height) / k;
  const units = Number(svg.dataset.size) || side;
  // Its own units to the screen's pixels.
  const scale = units > 0 ? side / units : 1;
  const thickness = (Number(svg.dataset.thickness) || 0) * scale;
  return {
    cx: (r.left + r.width / 2 - outer.left) / k,
    cy: (r.top + r.height / 2 - outer.top) / k,
    radius: (side - thickness) / 2,
    thickness,
    visual: svg.dataset.gauge ?? 'ring',
    fill: Math.min(1, Math.max(0, Number(svg.dataset.fill) || 0)),
    start: Number(svg.dataset.start) || 0,
    sweep: Number(svg.dataset.sweep) || 360,
  };
}

/** The box a wheel's spokes take (the ring's inside), or null for a needle. */
export function wheelBox(play: GaugePlay | null | undefined) {
  if (!play || play.kind !== 'wheel') return null;
  const r = Math.max(4, play.gauge.radius - play.gauge.thickness / 2);
  return { left: play.gauge.cx - r, top: play.gauge.cy - r, right: play.gauge.cx + r, bottom: play.gauge.cy + r };
}

/** A hamster wheel inside a ring gauge, as the recorder draws it: eight faint spokes and a hub, turning back under the runner's feet. */
export function drawWheel(ctx: CanvasRenderingContext2D, play: GaugePlay | null | undefined, x: number, y: number, k: number) {
  const box = wheelBox(play);
  if (!box || !play) return;
  const r = ((box.right - box.left) / 2) * k;
  ctx.save();
  ctx.translate(x, y);
  ctx.lineCap = 'round';
  ctx.strokeStyle = 'rgba(255,255,255,0.27)';
  ctx.lineWidth = Math.max(1.2 * k, r * 0.03);
  const turn = -((play.spin % 360) * Math.PI) / 180;
  ctx.beginPath();
  for (let i = 0; i < 8; i++) {
    const a = turn + (i * Math.PI) / 4;
    ctx.moveTo(Math.cos(a) * r * 0.18, Math.sin(a) * r * 0.18);
    ctx.lineTo(Math.cos(a) * r * 0.94, Math.sin(a) * r * 0.94);
  }
  ctx.stroke();
  ctx.strokeStyle = 'rgba(255,255,255,0.35)';
  ctx.lineWidth = Math.max(1.5 * k, r * 0.04);
  ctx.beginPath();
  ctx.arc(0, 0, r * 0.16, 0, Math.PI * 2);
  ctx.stroke();
  ctx.restore();
}

const pick = (plays: readonly string[] | null | undefined, ...wanted: string[]) => wanted.find((a) => !plays || plays.includes(a)) ?? null;

/**
 * On a gauge (as the recorder's CharacterReacting.OnGauge): in a ring it runs in place at its bottom, faster as the
 * gauge fills (asleep near empty, walking when low), the wheel turning with it; on a needle it sits on the end of the
 * fill, facing out. `clock` keeps its wheel's turning between moments.
 */
export function onGauge(
  c: Pick<OverlayCharacter, 'place' | 'speed' | 'plays'>,
  gauge: GaugeSpot,
  critical: boolean,
  action: string,
  now: number,
  clock: { wheel: number; at: number; fill?: number },
  size = 48,
): { frame: CharacterFrame; play: GaugePlay } {
  const dt = Number.isNaN(clock.at) ? 0 : Math.min(0.1, Math.max(0, now - clock.at));
  clock.at = now;
  const fill = Math.min(1, Math.max(0, gauge.fill));
  let own = action === 'default' || action === 'walk' || action === 'run' || action === 'idle' ? null : action;
  if (c.place === 'wheel') {
    // In its wheel the running is the reading: worried or tired, it keeps running (as the recorder).
    if (own === 'worried' || own === 'tired') own = null;
    const inner = Math.max(4, gauge.radius - gauge.thickness / 2);
    clock.wheel += (dt * (0.3 + 2.2 * fill) * (c.speed ?? 100)) / 100;
    const stride = fill < 0.04 ? pick(c.plays, 'sleep', 'sit', 'idle') : fill < 0.3 ? pick(c.plays, 'walk', 'run') : pick(c.plays, 'run', 'walk');
    return { frame: { x: gauge.cx, y: gauge.cy + inner * 0.93, action: own ?? stride ?? 'idle', seconds: clock.wheel, facing: 1 }, play: { kind: 'wheel', gauge, size: inner * 1.15, spin: clock.wheel * 150 } };
  }
  if (c.place === 'bar' && gauge.box) {
    // On the bar, pushing the end of its fill: walking while it grows, as it reacts while it holds or shrinks.
    const growing = clock.fill !== undefined && fill - clock.fill > 0.0005;
    clock.fill = fill;
    const pushing = own ?? (growing ? pick(c.plays, 'walk', 'run') : critical ? pick(c.plays, 'panic', 'jump') : null) ?? 'idle';
    return { frame: { x: gauge.box.left + (gauge.box.right - gauge.box.left) * fill - size * 0.12, y: gauge.box.top, action: pushing, seconds: now, facing: 1 }, play: { kind: 'bar', gauge, size, spin: 0 } };
  }
  if (c.place === 'graph' && gauge.box) {
    // Surfing the line left to right (6 s across at its speed, round again), up and down with it.
    clock.wheel += (dt * (c.speed ?? 100)) / 100 / 6;
    const x = gauge.box.left + (clock.wheel % 1) * (gauge.box.right - gauge.box.left);
    return { frame: { x, y: lineAt(gauge, x), action: own ?? pick(c.plays, 'run', 'walk') ?? 'idle', seconds: now, facing: 1 }, play: { kind: 'graph', gauge, size, spin: 0 } };
  }
  const angle = ((gauge.start + gauge.sweep * fill) * Math.PI) / 180;
  const frame: CharacterFrame = {
    x: gauge.cx + Math.cos(angle) * gauge.radius,
    y: gauge.cy + Math.sin(angle) * gauge.radius,
    action: own ?? (critical ? pick(c.plays, 'jump', 'sit') : pick(c.plays, 'sit', 'idle')) ?? 'idle',
    seconds: now,
    facing: Math.cos(angle) >= 0 ? 1 : -1,
  };
  return { frame, play: { kind: 'needle', gauge, size: Math.max(12, gauge.radius * 0.55), spin: 0 } };
}
