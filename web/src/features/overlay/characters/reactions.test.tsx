import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { OverlayCharacter, OverlayPanel, OverlayReading, OverlaySource } from '@/lib/api/types';
import { mockApi } from '@/test/app';
import { DEFAULT_STYLE } from '../model';
import { CharactersTab, newCharacter } from './CharactersTab';
import parity from './reaction-parity.json';
import { EFFECTS, KINDS, Reacting, STATUS_HOLD, kindOf, paceOf, ordered, propFor, reactingFrame, reactionFor, reactionsFor, readingFor, statusOf, type ReadingKind } from './reactions';
import table from './reactions.json';

const panelBox = { left: 100, top: 200, right: 500, bottom: 320 };
const screenBox = { left: 0, top: 0, right: 1920, bottom: 1080 };
const source = (id: string, unit: string, value: number | null, max: number | null = null): OverlaySource => ({ id, label: id, group: 'g', unit, value, max });

/** What the recorder must judge alike (CharacterPlayerTests.cs reads the same file): kinds, statuses and reactions. */
function parityCases() {
  const cases: { source: string; unit: string; reading?: Partial<OverlayReading>; value: number; max?: number | null }[] = [
    { source: 'cpu.usage', unit: '%', value: 5 },
    { source: 'cpu.usage', unit: '%', value: 50 },
    { source: 'cpu.usage', unit: '%', value: 80 },
    { source: 'cpu.usage', unit: '%', value: 95 },
    { source: 'cpu.usage', unit: '%', reading: { warning: 60, critical: 70 }, value: 65 },
    { source: 'cpu.temp', unit: '°C', value: 35 },
    { source: 'cpu.temp', unit: '°C', value: 60 },
    { source: 'gpu.g1.hotspot', unit: '°C', value: 80 },
    { source: 'gpu.g1.hotspot', unit: '°C', value: 91 },
    { source: 'net.n1.down', unit: 'B/s', value: 2_000 },
    { source: 'net.n1.down', unit: 'B/s', value: 6_000_000 },
    { source: 'net.n1.down', unit: 'B/s', value: 60_000_000 },
    { source: 'net.n1.down', unit: 'B/s', value: 70_000_000, max: 100_000_000 },
    { source: 'disk.d1.read', unit: 'B/s', value: 95_000_000, max: 100_000_000 },
    { source: 'game.fps', unit: 'fps', value: 25 },
    { source: 'game.fps', unit: 'fps', value: 45 },
    { source: 'game.fps', unit: 'fps', value: 90 },
    { source: 'game.fps', unit: 'fps', value: 144 },
    { source: 'game.fps', unit: 'fps', reading: { warning: 100, critical: 60, below: true }, value: 80 },
    { source: 'game.frametime', unit: 'ms', value: 6.9 },
    { source: 'game.frametime', unit: 'ms', value: 40 },
    { source: 'net.ping.internet', unit: 'ms', value: 9 },
    { source: 'net.ping.internet', unit: 'ms', value: 120 },
    { source: 'net.ping.internet', unit: 'ms', value: 250 },
    { source: 'gpu.g1.fan', unit: 'RPM', value: 0 },
    { source: 'gpu.g1.fan', unit: 'RPM', value: 2000 },
    { source: 'cpu.power', unit: 'W', value: 10 },
    { source: 'cpu.power', unit: 'W', value: 320 },
    { source: 'gpu.g1.power', unit: 'W', value: 400, max: 450 },
    { source: 'memory.used', unit: 'B', reading: { show: 'percent' }, value: 30e9, max: 32e9 },
    { source: 'cpu.clock', unit: 'GHz', value: 5.2 },
    { source: 'devices.missing', unit: '', reading: { warning: 1, critical: 3 }, value: 2 },
  ];
  return cases.map((c) => {
    const kind = kindOf(c.source, c.unit);
    const status = statusOf(kind, c.reading, c.value, c.max ?? null);
    return { ...c, kind, status, reaction: reactionFor(kind, status), prop: propFor(kind, status) };
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Characters reacting to readings', () => {
  it('knows each reading’s kind, judges its value, and reacts the kind’s way', () => {
    const cases = parityCases();
    const at = (source: string, value: number) => cases.find((c) => c.source === source && c.value === value)!;
    expect(at('cpu.usage', 5)).toMatchObject({ kind: 'load', status: 'low', reaction: { action: 'sleep', effect: 'zzz' } });
    expect(at('cpu.usage', 80)).toMatchObject({ status: 'high', reaction: { action: 'worried', effect: 'sweat' } });
    expect(at('cpu.usage', 65)).toMatchObject({ status: 'high' });
    expect(at('gpu.g1.hotspot', 91)).toMatchObject({ kind: 'temperature', status: 'critical', reaction: { action: 'panic', effect: 'flame' } });
    expect(at('cpu.temp', 35)).toMatchObject({ status: 'low', reaction: { effect: 'snow' } });
    expect(at('net.n1.down', 6_000_000)).toMatchObject({ kind: 'bandwidth', status: 'high', reaction: { action: 'run', effect: 'data' } });
    expect(at('net.n1.down', 70_000_000)).toMatchObject({ status: 'high' });
    expect(at('disk.d1.read', 95_000_000)).toMatchObject({ status: 'critical', reaction: { effect: 'sparks' } });
    // A low frame rate is poor only with the reading's own limits: the desktop draws few frames while nothing moves,
    // and the alarm showed with everything well.
    expect(at('game.fps', 25)).toMatchObject({ kind: 'fps', status: 'normal' });
    expect(at('game.fps', 45)).toMatchObject({ status: 'normal' });
    expect(at('game.fps', 144)).toMatchObject({ status: 'high', reaction: { action: 'cheer', effect: 'notes' } });
    expect(at('game.fps', 80)).toMatchObject({ status: 'low' });
    expect(at('game.frametime', 40)).toMatchObject({ kind: 'frametime', status: 'normal' });
    expect(at('net.ping.internet', 9)).toMatchObject({ kind: 'latency', status: 'low', reaction: { effect: 'hearts' } });
    expect(at('gpu.g1.fan', 2000)).toMatchObject({ kind: 'fan', status: 'high', reaction: { effect: 'wind' } });
    expect(at('gpu.g1.power', 400)).toMatchObject({ kind: 'power', status: 'high' });
    expect(at('memory.used', 30e9)).toMatchObject({ kind: 'other', status: 'normal' });
    expect(at('devices.missing', 2)).toMatchObject({ kind: 'other', status: 'high', reaction: { effect: 'exclaim' } });
    // Every kind has a reaction for every status, with known actions and effects.
    for (const kind of Object.keys(KINDS) as ReadingKind[]) {
      for (const status of table.statuses) {
        const [action, effect] = KINDS[kind].reactions[status as 'low'];
        expect(['default', 'idle', 'walk', 'run', 'sit', 'sleep', 'jump', 'play', 'dance', 'worried', 'panic', 'tired', 'cheer']).toContain(action);
        if (effect) expect(EFFECTS.map(([id]) => id)).toContain(effect);
      }
    }
  });

  it('keeps the file the recorder checks its judging against up to date', async () => {
    const cases = JSON.parse(JSON.stringify(parityCases()));
    // UPDATE_CHARACTER_PARITY=1 writes it again (after a change to the rules or the table).
    const node = (globalThis as unknown as { process?: { env: Record<string, string | undefined>; cwd: () => string } }).process;
    if (node?.env.UPDATE_CHARACTER_PARITY) {
      const fs = (await import(/* @vite-ignore */ ['node', 'fs'].join(':'))) as { writeFileSync: (file: string, text: string) => void };
      fs.writeFileSync(`${node.cwd()}/src/features/overlay/characters/reaction-parity.json`, JSON.stringify(cases));
      return;
    }
    expect(parity).toEqual(cases);
  });

  it('takes a character’s own reactions, and the panel’s most urgent reading for “auto”', () => {
    const own = { high: { action: 'dance', effect: 'none' }, low: { effect: 'hearts' } };
    expect(reactionFor('load', 'high', own)).toEqual({ action: 'dance', effect: null });
    expect(reactionFor('load', 'low', own)).toEqual({ action: 'sleep', effect: 'hearts' });
    const panel = { readings: [{ source: 'cpu.usage' }, { source: 'cpu.temp' }, { source: 'net.n1.down' }] };
    const sources = new Map([source('cpu.usage', '%', 40), source('cpu.temp', '°C', 90), source('net.n1.down', 'B/s', 1000)].map((s) => [s.id, s]));
    expect(readingFor({ react: 'auto' }, panel, sources)).toMatchObject({ kind: 'temperature', status: 'critical' });
    expect(readingFor({ react: 'net.n1.down' }, panel, sources)).toMatchObject({ kind: 'bandwidth', status: 'low' });
    expect(readingFor({ react: null }, panel, sources)).toBeNull();
    expect(readingFor({ react: 'gpu.nothing' }, panel, sources)).toBeNull();
    // The most urgent wins over the order: an idle GPU put first does not drown out a CPU at full load (16 Oct 2026).
    const busy = { readings: [{ source: 'gpu.usage' }, { source: 'cpu.usage' }] };
    const loads = new Map([source('gpu.usage', '%', 5), source('cpu.usage', '%', 100)].map((s) => [s.id, s]));
    expect(readingFor({ react: 'auto', priority: ['gpu.usage', 'cpu.usage'] }, busy, loads)).toMatchObject({ source: { id: 'cpu.usage' }, status: 'critical' });
    expect(readingFor({ react: 'auto', priority: ['gpu.usage'] }, busy, new Map([source('gpu.usage', '%', 5), source('cpu.usage', '%', 50)].map((s) => [s.id, s])))).toMatchObject({ source: { id: 'gpu.usage' }, status: 'low' });
    // No critical or high one: the first reading, whatever it shows (a low one later does not come first).
    expect(readingFor({ react: 'auto', priority: ['cpu.usage'] }, busy, new Map([source('gpu.usage', '%', 5), source('cpu.usage', '%', 50)].map((s) => [s.id, s])))).toMatchObject({ source: { id: 'cpu.usage' }, status: 'normal' });

    // One reading's own reactions over its general ones: when the CPU's temperature is what it reacts to.
    const kitten: OverlayCharacter = { ...newCharacter('mochi', []), react: 'auto', reactions: { critical: { action: 'sit' } }, reactionsBy: { 'cpu.temp': { critical: { effect: 'steam' } } } };
    expect(reactionsFor(kitten, 'cpu.temp')).toEqual({ critical: { action: 'sit', effect: 'steam', prop: null } });
    expect(reactionsFor(kitten, 'cpu.usage')).toEqual({ critical: { action: 'sit' } });
    const hot = reactingFrame(kitten, panel, sources, new Reacting(), panelBox, screenBox, 64, 0);
    expect(hot).toMatchObject({ status: 'critical', effect: 'steam' });
  });

  it('holds a status until a new one lasts, and carries on along its route instead of jumping', () => {
    const state = new Reacting();
    expect(state.hold(0, 'normal')).toBe('normal');
    expect(state.hold(1, 'high')).toBe('normal');
    expect(state.hold(1.5, 'normal')).toBe('normal');
    expect(state.hold(2, 'high')).toBe('normal');
    expect(state.hold(2 + STATUS_HOLD, 'high')).toBe('high');
    // The route's clock: real time at first, then at the pace (stopped, faster).
    const clock = new Reacting();
    expect(clock.advance(10, 1)).toBe(10);
    expect(clock.advance(11, 0)).toBe(10);
    expect(clock.advance(12, paceOf('run'))).toBeCloseTo(11.8, 6);
    expect(clock.advance(20, 1)).toBeCloseTo(12.8, 6);

    // A wandering kitten on a quiet PC goes to sleep where it is, and stays there.
    const kitten: OverlayCharacter = { ...newCharacter('mochi', []), react: 'cpu.usage' };
    const quiet = new Map([['cpu.usage', source('cpu.usage', '%', 3)]]);
    const live = new Reacting();
    const first = reactingFrame(kitten, { readings: [{ source: 'cpu.usage' }] }, quiet, live, panelBox, screenBox, 64, 100);
    const later = reactingFrame(kitten, { readings: [{ source: 'cpu.usage' }] }, quiet, live, panelBox, screenBox, 64, 101);
    expect(first).toMatchObject({ status: 'low', effect: 'zzz', frame: { action: 'sleep' } });
    expect(later.frame.x).toBe(first.frame.x);
    expect(later.frame.seconds).toBe(101);

    // A high frame rate: it dances when it pauses, and still wanders between (as chosen), music notes all along.
    const dancer: OverlayCharacter = { ...newCharacter('mochi', []), react: 'game.fps' };
    const fast = new Map([['game.fps', source('game.fps', 'fps', 240)]]);
    const going = new Reacting();
    const frames = Array.from({ length: 400 }, (_, i) => reactingFrame(dancer, { readings: [{ source: 'game.fps' }] }, fast, going, panelBox, screenBox, 64, 200 + i * 0.25));
    expect(new Set(frames.map((f) => f.frame.action))).toEqual(new Set(['walk', 'cheer']));
    expect(new Set(frames.map((f) => Math.round(f.frame.x))).size).toBeGreaterThan(20);
    expect(frames.every((f) => f.effect === 'notes')).toBe(true);
  });

  it('sets what a character reacts to and its own reactions in the Characters tab', async () => {
    mockApi({ 'GET /api/v1/overlay/characters': () => [{ id: 'mochi', name: 'Mochi', shipped: true, actions: ['idle', 'walk', 'run', 'jump', 'play', 'sleep'], used: true, here: true, added: null }] });
    const panel: OverlayPanel = { id: 'p1', title: null, x: 0, y: 0, layout: 'rows', readings: [{ source: 'cpu.usage' }, { source: 'cpu.temp' }], style: DEFAULT_STYLE, characters: [{ ...newCharacter('mochi', []) }] };
    const sources = new Map([source('cpu.usage', '%', 96), source('cpu.temp', '°C', 50)].map((s) => [s.id, { ...s, label: s.id === 'cpu.usage' ? 'CPU' : 'CPU temp' }]));
    const onChange = vi.fn();
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={client}>
        <CharactersTab panel={panel} sources={sources} onChange={onChange} />
      </QueryClientProvider>,
    );
    const section = screen.getByRole('region', { name: 'Reacts to readings' });
    expect(within(section).getByRole('combobox', { name: 'Reacts to' })).toHaveValue('auto');
    expect(within(section).getByRole('status')).toHaveTextContent('Now: CPU 96 %: critical, so it panics with alarm (!) and a siren light.');
    const user = userEvent.setup();
    await user.selectOptions(within(section).getByRole('combobox', { name: 'Reacts to' }), 'cpu.temp');
    expect(onChange).toHaveBeenLastCalledWith([expect.objectContaining({ react: 'cpu.temp' })]);
    await user.selectOptions(within(section).getByRole('combobox', { name: 'High: what it does' }), 'dance');
    expect(onChange).toHaveBeenLastCalledWith([expect.objectContaining({ reactions: { high: { action: 'dance' } } })]);
    await user.selectOptions(within(section).getByRole('combobox', { name: 'Critical: its effect' }), 'none');
    expect(onChange).toHaveBeenLastCalledWith([expect.objectContaining({ reactions: { critical: { effect: 'none' } } })]);
  });

  it('reacts to the most urgent reading, its order breaking ties, and gives each reading its own reactions in its item', async () => {
    const panel = { readings: [{ source: 'cpu.usage' }, { source: 'cpu.temp' }] };
    const sources = new Map([source('cpu.usage', '%', 80), source('cpu.temp', '°C', 90)].map((s) => [s.id, s]));
    // High and critical: the critical one, whatever the order.
    expect(readingFor({ react: 'auto' }, panel, sources)).toMatchObject({ source: { id: 'cpu.temp' }, status: 'critical' });
    expect(readingFor({ react: 'auto', priority: ['cpu.temp', 'cpu.usage'] }, panel, sources)).toMatchObject({ source: { id: 'cpu.temp' }, status: 'critical' });
    // Both high: the first in its order.
    const both = new Map([source('cpu.usage', '%', 80), source('cpu.temp', '°C', 80)].map((s) => [s.id, s]));
    expect(readingFor({ react: 'auto' }, panel, both)).toMatchObject({ source: { id: 'cpu.usage' }, status: 'high' });
    expect(readingFor({ react: 'auto', priority: ['cpu.temp'] }, panel, both)).toMatchObject({ source: { id: 'cpu.temp' }, status: 'high' });
    expect(ordered(panel.readings, ['cpu.temp']).map((r) => r.source)).toEqual(['cpu.temp', 'cpu.usage']);
    // A reading left out is not considered.
    expect(readingFor({ react: 'auto', ignore: ['cpu.usage'] }, panel, sources)).toMatchObject({ source: { id: 'cpu.temp' } });
    expect(readingFor({ react: 'auto', ignore: ['cpu.usage', 'cpu.temp'] }, panel, sources)).toBeNull();

    mockApi({ 'GET /api/v1/overlay/characters': () => [{ id: 'mochi', name: 'Mochi', shipped: true, actions: ['idle', 'walk', 'run', 'jump', 'play', 'sleep'], used: true, here: true, added: null }] });
    const full: OverlayPanel = { id: 'p1', title: null, x: 0, y: 0, layout: 'rows', readings: panel.readings, style: DEFAULT_STYLE, characters: [{ ...newCharacter('mochi', []) }] };
    const labelled = new Map([...sources].map(([id, s]) => [id, { ...s, label: id === 'cpu.usage' ? 'CPU' : 'CPU temp' }]));
    const onChange = vi.fn();
    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <CharactersTab panel={full} sources={labelled} onChange={onChange} />
      </QueryClientProvider>,
    );
    const user = userEvent.setup();
    const list = screen.getByRole('list', { name: 'Its reactions' });
    // Put the temperature first.
    await user.click(within(list).getByRole('button', { name: 'CPU temp first' }));
    expect(onChange).toHaveBeenLastCalledWith([expect.objectContaining({ priority: ['cpu.temp', 'cpu.usage'] })]);
    await user.click(within(list).getByRole('checkbox', { name: 'Reacts to CPU' }));
    expect(onChange).toHaveBeenLastCalledWith([expect.objectContaining({ ignore: ['cpu.usage'] })]);
    // One item open at a time: the temperature's own reactions.
    const temp = within(list).getByRole('listitem', { name: 'CPU temp' });
    await user.click(within(temp).getByRole('button', { expanded: false }));
    expect(within(list).getAllByRole('table')).toHaveLength(1);
    await user.selectOptions(within(temp).getByRole('combobox', { name: 'Critical: its effect' }), 'steam');
    expect(onChange).toHaveBeenLastCalledWith([expect.objectContaining({ reactionsBy: { 'cpu.temp': { critical: { effect: 'steam' } } } })]);
  });
});
