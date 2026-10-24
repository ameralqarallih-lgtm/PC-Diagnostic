import { describe, expect, it } from 'vitest';
import type { OverlayCharacter, OverlaySource } from '@/lib/api/types';
import { fits, lineAt, onGauge, wheelBox, type GaugeSpot } from './gauges';
import { gaugeFor } from './reactions';

// The recorder's case (CharacterPlayerTests.On_a_gauge_it_runs_in_the_ring_or_rides_the_needle): the same numbers.
const ring: GaugeSpot = { cx: 200, cy: 200, radius: 90, thickness: 20, visual: 'ring', fill: 0.8, start: -90, sweep: 360 };

describe('Characters on a gauge', () => {
  it('runs in its ring, faster as it fills, the wheel turning with it, and asleep near empty', () => {
    const clock = { wheel: 0, at: Number.NaN };
    const c = { place: 'wheel', speed: 100, plays: null } as const;
    const first = onGauge(c, ring, false, 'default', 0, clock);
    const next = onGauge(c, ring, false, 'default', 0.1, clock);
    expect(next.frame).toMatchObject({ x: 200, action: 'run', facing: 1 });
    expect(next.frame.y).toBeCloseTo(200 + 80 * 0.93);
    expect(next.frame.seconds).toBeGreaterThan(first.frame.seconds);
    expect(next.play.size).toBeCloseTo(80 * 1.15);
    expect(next.play.spin).toBeGreaterThan(0);
    expect(wheelBox(next.play)).toEqual({ left: 120, top: 120, right: 280, bottom: 280 });
    expect(onGauge(c, { ...ring, fill: 0.01 }, false, 'default', 0.2, clock).frame.action).toBe('sleep');
    expect(onGauge(c, { ...ring, fill: 0.2 }, false, 'default', 0.3, clock).frame.action).toBe('walk');
  });

  it('rides the end of the fill on a needle, facing out', () => {
    const c = { place: 'needle', speed: 100, plays: null } as const;
    const { frame, play } = onGauge(c, { ...ring, fill: 0.25 }, false, 'default', 0, { wheel: 0, at: Number.NaN });
    expect(frame.x).toBeCloseTo(290);
    expect(frame.y).toBeCloseTo(200);
    expect(frame).toMatchObject({ facing: 1, action: 'sit' });
    expect(play.kind).toBe('needle');
    expect(wheelBox(play)).toBeNull();
    expect(onGauge(c, { ...ring, fill: 0.75 }, true, 'default', 0, { wheel: 0, at: Number.NaN }).frame).toMatchObject({ facing: -1, action: 'jump' });
  });

  it('pushes the end of its bar and surfs its graph (as the recorder)', () => {
    const bar: GaugeSpot = { cx: 200, cy: 305, radius: 0, thickness: 10, visual: 'bar', fill: 0.5, start: 0, sweep: 0, box: { left: 100, top: 300, right: 300, bottom: 310 } };
    const clock = { wheel: 0, at: Number.NaN } as { wheel: number; at: number; fill?: number };
    const c = { place: 'bar', speed: 100, plays: null } as const;
    expect(onGauge(c, bar, false, 'default', 0, clock, 64).frame).toMatchObject({ x: 200 - 64 * 0.12, y: 300, action: 'idle' });
    expect(onGauge(c, { ...bar, fill: 0.6 }, false, 'default', 0.1, clock, 64).frame.action).toBe('walk');
    expect(onGauge(c, { ...bar, fill: 0.6 }, true, 'default', 0.2, clock, 64).frame.action).toBe('panic');
    expect(fits('bar', 'bar')).toBe(true);
    expect(fits('wheel', 'bar')).toBe(false);

    const graph: GaugeSpot = { cx: 250, cy: 230, radius: 0, thickness: 2, visual: 'graph', fill: 0.5, start: 0, sweep: 0, box: { left: 100, top: 200, right: 400, bottom: 260 }, line: [[100, 260], [250, 200], [400, 230]] };
    expect(lineAt(graph, 175)).toBeCloseTo(230);
    expect(lineAt(graph, 50)).toBe(260);
    expect(lineAt(graph, 500)).toBe(230);
    const surf = { wheel: 0, at: Number.NaN };
    const g = { place: 'graph', speed: 100, plays: null } as const;
    onGauge(g, graph, false, 'default', 0, surf, 64);
    const { frame, play } = onGauge(g, graph, false, 'default', 0.1, surf, 64);
    expect(frame.x).toBeGreaterThan(100);
    expect(frame.x).toBeLessThan(110);
    expect(frame.y).toBeCloseTo(lineAt(graph, frame.x));
    expect([frame.action, play.kind, play.size]).toEqual(['run', 'graph', 64]);
  });

  it('takes the most urgent gauge of its panel, in its order (as the recorder)', () => {
    const panel = { readings: [{ source: 'cpu.usage' }, { source: 'gpu.usage' }, { source: 'cpu.temp' }, { source: 'ram' }] };
    const at = ['cpu.usage', 'gpu.usage', 'cpu.temp'];
    const spot = (source: string): GaugeSpot | null => (at.includes(source) ? { ...ring, cx: at.indexOf(source) } : null);
    const values = (cpu: number, gpu: number, temp: number) =>
      new Map<string, OverlaySource>([
        ['cpu.usage', { id: 'cpu.usage', label: 'CPU', group: '', unit: '%', value: cpu }],
        ['gpu.usage', { id: 'gpu.usage', label: 'GPU', group: '', unit: '%', value: gpu }],
        ['cpu.temp', { id: 'cpu.temp', label: 'CPU temp', group: '', unit: '°C', value: temp }],
        ['ram', { id: 'ram', label: 'RAM', group: '', unit: '%', value: 99 }],
      ]);
    const c: OverlayCharacter = { id: 'c', character: 'ren', place: 'wheel', movement: 'stay', action: 'idle', x: 0.5, size: 48, speed: 100, flip: false, behind: false, opacity: 100, priority: ['gpu.usage'] };
    expect(gaugeFor(c, panel, values(80, 40, 95), spot)?.cx).toBe(at.indexOf('cpu.temp'));
    expect(gaugeFor(c, panel, values(80, 40, 50), spot)?.cx).toBe(at.indexOf('cpu.usage'));
    expect(gaugeFor(c, panel, values(40, 40, 50), spot)?.cx).toBe(at.indexOf('gpu.usage'));
    expect(gaugeFor({ ...c, gauge: 'cpu.usage' }, panel, values(40, 40, 95), spot)?.cx).toBe(at.indexOf('cpu.usage'));
  });
});
