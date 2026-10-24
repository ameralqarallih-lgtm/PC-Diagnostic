import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { OverlayConfig, OverlayDisplay, OverlayPanel, OverlayResponse, OverlaySource, OverlayState, OverlayStyle, ScreenDriverInfo } from '@/lib/api/types';
import { bodyOf, confirmIt, mockApi, renderApp } from '@/test/app';
import { FakeEventSource, statusResponse } from '@/test/fixtures';
import { autoIcon } from './icons';
import { autoMax, screenCharactersOf, screenStage, withScreenCharacters } from './model';
import { DEFAULT_STYLE, arrange, arrangedInGrid, fitWithin, fittedToScreen, formatSource, gridFor, iconOf, inCell, level, maximized, newPanel, pack, panelsOf, placement, presetOf, presetsOf, readable, readableOn, restored, shareOf, shown, templateReadings, valueSourceOf, visualOf, widest, withPanels, withPortrait } from './model';
import { PanelPreview, onBand } from './PanelPreview';
import { ScreenEditor } from './ScreenCanvas';
import { ART_THEMES, artSignature, artSvg, artUrl, panelGeometry } from './art';
import { ART_PANEL_DESIGNS, ART_SCREEN_DESIGNS } from './artDesigns';
import { DESIGNS } from './designs';
import { SCREEN_DESIGNS, SCREEN_SIZES, fitScreenDesign, logicalSize, shapeDistance, sizeOf } from './screens';
import { BACKDROPS, PATTERNS, patternSvg, patternTile } from './model';
import { render } from '@testing-library/react';

const GB = 1024 ** 3;

/** Saves the editor's changes (nothing is saved, nor shown on the screens, until Save is pressed). */
async function saveChanges() {
  const bar = await screen.findByRole('region', { name: 'Changes not saved' });
  const button = within(bar).getByRole('button', { name: /^Save/ });
  await waitFor(() => expect(button).toBeEnabled(), { timeout: 3000 });
  await userEvent.click(button);
  await waitFor(() => expect(screen.queryByRole('region', { name: 'Changes not saved' })).toBeNull(), { timeout: 3000 });
}

/** Opens a part of the Screen editor (Editor, Screens, All panels, Options, Screen drivers). */
async function openPart(name: RegExp) {
  const parts = await screen.findByRole('navigation', { name: 'Screen editor' });
  await userEvent.click(within(parts).getByRole('button', { name }));
}

const sources: OverlaySource[] = [
  { id: 'cpu.usage', label: 'CPU', group: 'Processor', unit: '%', value: 52.4 },
  { id: 'cpu.temp', label: 'CPU temp', group: 'Processor', unit: '°C', value: 91 },
  { id: 'memory.usage', label: 'RAM', group: 'Memory', unit: '%', value: 70 },
  { id: 'gpu.g1.usage', label: 'GPU', group: 'RTX 4090', unit: '%', value: 12 },
  { id: 'gpu.g1.temp', label: 'GPU temp', group: 'RTX 4090', unit: '°C', value: 41 },
  { id: 'gpu.g1.vram', label: 'VRAM', group: 'RTX 4090', unit: 'B', value: 3.4 * GB },
  { id: 'net.n1.down', label: 'Download', group: 'Ethernet 3', unit: 'B/s', value: 19.5 * 1024 * 1024 },
  { id: 'net.n1.up', label: 'Upload', group: 'Ethernet 3', unit: 'B/s', value: 300 * 1024 },
  { id: 'time.clock', label: 'Time', group: 'This PC', unit: '', value: null, text: '01:53' },
  { id: 'sensor.lhm/lpc/nct6701d/0/voltage/8', label: 'CMOS Battery', group: 'Nuvoton NCT6701D', unit: 'V', value: 3.088 },
];

/** The recorder's looks (a few, as the API sends them). */
const presets: Record<string, OverlayStyle> = {
  aurora: { ...DEFAULT_STYLE, accent: '#22D3EE', background: '#1E1B4B', background2: '#0E7490', shadow: 60, sheen: true, caps: true, valueScale: 120, icons: true, cornerRadius: 14, padding: 12 },
  neon: { ...DEFAULT_STYLE, font: 'Consolas', labelColor: '#F472B6', valueColor: '#22D3EE', accent: '#22D3EE', border: '#22D3EE', glow: 70, barStyle: 'segments' },
  card: DEFAULT_STYLE,
};

const state: OverlayState = {
  running: true,
  visible: true,
  problem: null,
  displays: [
    { key: 'AUS32F4/1', name: 'PG32UCDP', number: 1, x: 0, y: 0, width: 3840, height: 2160, scale: 1.5, primary: true, refreshHz: 240 },
    { key: 'AUS2721/2', name: 'VG279QM', number: 2, x: 3840, y: 0, width: 1920, height: 1080, scale: 1, primary: false, refreshHz: 120 },
  ],
  sources,
};

describe('On-screen readings model', () => {
  it('formats values the way the recorder draws them', () => {
    const at = (id: string) => sources.find((s) => s.id === id);
    expect(formatSource(at('cpu.usage'))).toEqual({ value: '52', unit: '%' });
    expect(formatSource(at('gpu.g1.vram'))).toEqual({ value: '3.4', unit: 'GB' });
    expect(formatSource(at('net.n1.down'))).toEqual({ value: '19.5', unit: 'MB/s' });
    expect(formatSource(at('net.n1.up'))).toEqual({ value: '300', unit: 'KB/s' });
    expect(formatSource(at('sensor.lhm/lpc/nct6701d/0/voltage/8'))).toEqual({ value: '3.088', unit: 'V' });
    expect(formatSource(at('cpu.usage'), 1)).toEqual({ value: '52.4', unit: '%' });
    expect(formatSource(at('time.clock'))).toEqual({ value: '01:53', unit: '' });
    expect(formatSource(undefined)).toEqual({ value: '—', unit: '' });
    expect(level({ source: 'x', warning: 80, critical: 90 }, 91)).toBe(2);
    expect(level({ source: 'x', warning: 20, critical: 10, below: true }, 15)).toBe(1);
  });

  it('fills the screen, puts a panel in a grid cell, fits it and restores it; arranges all; keeps a layout for upright', () => {
    const base: OverlayPanel = { id: 'p', title: null, x: 1, y: 0, layout: 'rows', readings: [{ source: 'cpu.usage' }], style: DEFAULT_STYLE };
    const screen = { width: 2560, height: 1440 };

    const full = maximized(base, screen);
    expect([full.x, full.y, full.width, full.height]).toEqual([0, 0, 2560, 1440]);
    expect(full.restore).toEqual({ x: 1, y: 0, width: null, height: null, size: 14 });

    // The bottom-right cell of a 2×2 grid: half the screen, at its right and bottom edges; restore still goes back to the start.
    const cell = inCell(full, screen, { columns: 2, rows: 2 }, 1, 1);
    expect([cell.x, cell.y, cell.width, cell.height]).toEqual([1, 1, 1280, 720]);
    expect(cell.restore).toEqual(full.restore);
    const back = restored(cell);
    expect([back.x, back.y, back.width, back.height, back.style.size, back.restore]).toEqual([1, 0, null, null, 14, null]);

    // Readings larger than the screen at their own size: filling it (or a cell) makes the text smaller, never larger.
    expect(maximized(base, { width: 1920, height: 480 }, { width: 2800, height: 600 }).style.size).toBe(9.5);
    expect(maximized(base, { width: 1920, height: 480 }, { width: 400, height: 100 }).style.size).toBe(14);
    expect(inCell(base, { width: 1920, height: 480 }, { columns: 2, rows: 1 }, 0, 0, { width: 1440, height: 300 }).style.size).toBe(9);

    // Placed by its box: at the right edge, its box touches it (its 10 px shadow margin goes past).
    expect(placement({ x: 1, y: 0 }, { width: 1000, height: 500 }, { width: 220, height: 120 }, 10)).toEqual({ left: 790, top: -10 });

    // Fitted: a 400 × 100 panel (20 px of padding and margins each way) on a 1000 × 1000 screen grows by (1000-20)/(400-20).
    const fit = fittedToScreen(base, { width: 1000, height: 1000 }, { width: 400, height: 100 }, 20);
    expect(fit.style.size).toBe(36);
    expect(fittedToScreen({ ...base, width: 800, height: 200 }, { width: 400, height: 400 }, { width: 800, height: 200 }, 0)).toMatchObject({ width: 400, height: 100, style: { size: 7 } });
    expect(fitWithin({ ...base, width: 800, height: 200 }, { width: 400, height: 900 })).toMatchObject({ width: 400, height: 100, style: { size: 7 } });

    // Every shown panel into the cells in order (a hidden one stays); a grid for the count, wider on a wide screen.
    const three = [base, { ...base, id: 'q', hidden: true }, { ...base, id: 'r' }];
    const arranged = arrangedInGrid(three, screen, { columns: 2, rows: 1 });
    expect(arranged.map((p) => [p.x, p.width])).toEqual([
      [0, 1280],
      [1, undefined],
      [1, 1280],
    ]);
    expect(gridFor(4, screen)).toEqual({ columns: 2, rows: 2 });
    expect(gridFor(3, { width: 1920, height: 480 })).toEqual({ columns: 3, rows: 1 });
    expect(gridFor(3, { width: 480, height: 1920 })).toEqual({ columns: 1, rows: 3 });

    // Upright panels of their own: shown when upright, edited there, removed again.
    let config = withPanels({ version: 1, enabled: true, hotkey: '', intervalMs: 1000, hideFromCapture: false, screens: [] }, 'k', 'Name', [base]);
    expect(panelsOf(config, 'k', true)).toEqual([base]);
    config = withPortrait(config, 'k', 'Name', [{ ...base, id: 'tall' }]);
    expect(panelsOf(config, 'k', true).map((p) => p.id)).toEqual(['tall']);
    config = withPanels(config, 'k', 'Name', [{ ...base, id: 'tall', x: 0 }], true);
    expect(panelsOf(config, 'k', true)[0]!.x).toBe(0);
    expect(panelsOf(config, 'k', false)).toEqual([base]);
    expect(panelsOf(withPortrait(config, 'k', 'Name', null), 'k', true)).toEqual([base]);
  });

  it('places a panel by its share of the room it leaves, and fills templates with this PC’s values', () => {
    expect(placement({ x: 1, y: 0.5 }, { width: 1920, height: 1080 }, { width: 200, height: 100 })).toEqual({ left: 1720, top: 490 });
    expect(shareOf(860, 1720)).toBe(0.5);
    expect(shareOf(-5, 100)).toBe(0);
    expect(templateReadings('essentials', sources).map((r) => r.source)).toEqual(['cpu.usage', 'cpu.temp', 'gpu.g1.usage', 'gpu.g1.temp', 'memory.usage']);
    expect(templateReadings('network', sources).map((r) => r.source)).toEqual(['net.n1.down', 'net.n1.up']);
    const first = newPanel('essentials', sources, []);
    expect([first.x, first.y, first.title]).toEqual([1, 0, 'Essentials']);
    const second = newPanel('clock', sources, [first]);
    expect([second.id, second.x, second.y, second.layout]).toEqual(['panel-2', 0, 0, 'line']);
    const { rects } = arrange(state.displays, 600, 200, 0);
    expect(rects[1]!.left - rects[0]!.left).toBeCloseTo(rects[0]!.width, 5); // side by side, to scale
    expect(rects[1]!.width).toBeCloseTo(rects[0]!.width / 2, 5);
    expect(withPanels({ version: 1, enabled: true, hotkey: '', intervalMs: 1000, hideFromCapture: false, screens: [] }, 'k', 'Name', []).screens).toEqual([]);
  });
});

describe('On-screen readings redesign', () => {
  it('offers the recorder’s looks in the gallery’s order and knows an unchanged one', () => {
    const list = presetsOf(presets);
    expect(list.map((p) => p.id)).toEqual(['aurora', 'neon', 'card']);
    expect(presetOf({ ...presets.neon! }, list)).toBe('neon');
    expect(presetOf({ ...presets.neon!, glow: 10 }, list)).toBeNull();
    expect(presetsOf(undefined).map((p) => p.id)).toEqual(['card']);
  });

  it('chooses visuals and icons the way the recorder does', () => {
    expect(visualOf({ source: 'x', bar: true })).toBe('bar');
    expect(visualOf({ source: 'x', bar: true, visual: 'ring' })).toBe('ring');
    expect(autoIcon('gpu.g1.temp', '°C')).toBe('temp');
    expect(autoIcon('gpu.g1.vram', 'B')).toBe('memory');
    expect(autoIcon('net.n1.up', 'B/s')).toBe('up');
    expect(autoIcon('cpu.clock', 'GHz')).toBe('cpu');
    expect(iconOf({ source: 'cpu.usage', icon: 'none' }, undefined)).toBeNull();
    expect(iconOf({ source: 'cpu.usage', icon: 'fan' }, undefined)).toBe('fan');
    expect(onBand('#F97316', '#F97316')).toBe('#FFFFFF');
    expect(onBand('#FBBF24', '#FDE68A')).toBe('#0F172A');
    expect(onBand('#FFFFFF', '#1E1B4B')).toBe('#FFFFFF');
  });

  it('fills the gaming HUD with rings in tiles and the live graphs with graphs', () => {
    const hud = newPanel('hud', sources, [], presets.aurora);
    expect(hud).toMatchObject({ layout: 'tiles', columns: 3, title: 'Gaming HUD' });
    expect(hud.readings.map((r) => [r.source, r.visual])).toEqual([
      ['gpu.g1.usage', 'ring'],
      ['cpu.usage', 'ring'],
      ['memory.usage', 'ring'],
    ]);
    expect(templateReadings('graphs', sources).every((r) => r.visual === 'graph')).toBe(true);
  });

  it('keeps a panel’s size only at the scale it was measured at (zooming or turning the screen never sticks it larger)', () => {
    // A browser's layout in short: an element is at least its min-width.
    let natural = 400;
    const spy = vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockImplementation(function (this: HTMLElement) {
      return Math.max(natural, parseFloat(this.style.minWidth) || 0);
    });
    try {
      const panel: OverlayPanel = { id: 'line', title: null, x: 1, y: 1, layout: 'line', readings: [{ source: 'cpu.usage' }], style: DEFAULT_STYLE };
      const map = new Map(sources.map((s) => [s.id, s]));
      const { container, rerender } = render(<PanelPreview panel={panel} sources={map} scale={1} />);
      const box = () => container.querySelector('[data-panel] > div') as HTMLElement;
      expect(box().style.minWidth).toBe('400px');

      // Drawn smaller (the canvas zoomed out, or turned back): its own size there, not the one kept at the larger scale.
      natural = 200;
      rerender(<PanelPreview panel={panel} sources={map} scale={0.5} />);
      expect(box().style.minWidth).toBe('200px');
    } finally {
      spy.mockRestore();
    }
  });

  it('draws tiles with rings, graphs from the history, bars and icons', () => {
    const panel: OverlayPanel = {
      id: 'p',
      title: 'Performance',
      x: 0,
      y: 0,
      layout: 'tiles',
      columns: 2,
      style: presets.aurora!,
      readings: [{ source: 'gpu.g1.usage', visual: 'ring' }, { source: 'cpu.usage', visual: 'graph' }, { source: 'memory.usage', visual: 'bar' }, { source: 'cpu.temp', warning: 80, critical: 90 }],
    };
    const now = Date.now();
    const history = new Map([['cpu.usage', [0, 1, 2, 3].map((i) => [now - (3 - i) * 1000, 40 + i * 5] as const)]]);
    const { container } = render(<PanelPreview panel={panel} sources={new Map(sources.map((s) => [s.id, s]))} scale={1} history={history} />);
    expect(container.querySelectorAll('[data-ring]')).toHaveLength(1);
    // A graph fills from the right: the newest point is at its right edge.
    expect(container.querySelector('[data-graph] path[fill="none"]')?.getAttribute('d')).toMatch(/L100\.00 [\d.]+$/);
    expect(container.querySelectorAll('[data-bar]')).toHaveLength(1);
    expect(container.querySelectorAll('svg path[stroke-linecap="round"]').length).toBeGreaterThanOrEqual(4);
    // The critical temperature is drawn in the critical color.
    const temp = [...container.querySelectorAll('[data-value]')].find((v) => v.textContent?.startsWith('91'));
    expect((temp as HTMLElement).style.color).toBe('rgb(239, 68, 68)');
  });
});

describe('On-screen readings: the widget designs', () => {
  const now = Date.now();
  const rising = [0, 1, 2, 3].map((i) => [now - (3 - i) * 1000, 40 + i * 5] as const);
  const history = new Map([
    ['cpu.usage', rising],
    ['gpu.g1.usage', rising],
    ['memory.usage', rising],
  ]);
  const everyDesign: OverlayPanel['readings'] = [
    { source: 'gpu.g1.usage', visual: 'arc' },
    { source: 'cpu.usage', visual: 'speed' },
    { source: 'cpu.temp', visual: 'status', warning: 80, critical: 90 },
    { source: 'gpu.g1.temp', visual: 'thermo', max: 100 },
    { source: 'memory.usage', visual: 'line' },
    { source: 'gpu.g1.usage', visual: 'columns' },
    { source: 'cpu.usage', visual: 'stats' },
    { source: 'memory.usage', visual: 'trend' },
  ];

  it('draws dials, a status light, a thermometer, graphs and text in rows and tiles', () => {
    for (const layout of ['rows', 'tiles'] as const) {
      const panel: OverlayPanel = { id: 'p', title: 'All', x: 0, y: 0, layout, columns: 2, style: presets.aurora!, readings: everyDesign };
      const { container, unmount } = render(<PanelPreview panel={panel} sources={new Map(sources.map((s) => [s.id, s]))} scale={1} history={history} />);
      expect(container.querySelector('[data-dial="arc"]')).not.toBeNull();
      expect(container.querySelector('[data-dial="speed"]')).not.toBeNull();
      // 91 °C is past the critical limit: the light is red.
      expect((container.querySelector('[data-status]') as HTMLElement).style.background).toBe('rgb(239, 68, 68)');
      expect(container.querySelector('[data-thermo]')).not.toBeNull();
      // A line graph has no area under it; columns are drawn as rectangles.
      const graphs = container.querySelectorAll('[data-graph]');
      expect(graphs).toHaveLength(2);
      expect(graphs[0]!.querySelectorAll('path')).toHaveLength(1);
      expect(graphs[1]!.querySelectorAll('rect').length).toBeGreaterThan(0);
      const extras = [...container.querySelectorAll('[data-extra]')].map((e) => e.textContent);
      expect(extras[0]).toMatch(/^lo 40 · avg \d+ · hi 55$/);
      expect(extras[1]).toBe('▲ 15');
      unmount();
    }
  });

  it('draws a dial without a full scale against an automatic one, and falls back where a design cannot be drawn', () => {
    // A dial without a full scale of its own uses an automatic one; no history for a graph, and text for a light, fall back.
    const panel: OverlayPanel = {
      id: 'p',
      title: 'Plain',
      layout: 'rows',
      x: 0,
      y: 0,
      style: DEFAULT_STYLE,
      readings: [{ source: 'net.n1.down', visual: 'speed' }, { source: 'net.n1.up', visual: 'columns' }, { source: 'time.clock', visual: 'status' }],
    };
    const { container } = render(<PanelPreview panel={panel} sources={new Map(sources.map((s) => [s.id, s]))} scale={1} />);
    expect(container.querySelectorAll('[data-dial]')).toHaveLength(1);
    expect(container.querySelector('[data-status], [data-extra]')).toBeNull();
    expect(container.querySelector('[data-graph] rect')).toBeNull();
  });

  it('gives a reading without a full scale an automatic one: the next round number above the value', () => {
    const at = (value: number) => autoMax({ id: 'x', label: 'X', group: 'G', unit: 'B', value } as OverlaySource);
    expect(at(2 * GB)).toBe(2.5 * GB);
    expect(at(1700)).toBe(2.5 * 1024); // 1.7 KB: rounded in kilobytes
    expect(at(80)).toBe(100);
    expect(at(0)).toBeNull();
    expect(autoMax({ id: 'x', label: 'X', group: 'G', unit: '', value: null, text: 'On' } as OverlaySource)).toBeNull();
  });

  it('picks a design from the grouped list, as the widget designs are picked', async () => {
    let saved: OverlayConfig = {
      version: 1,
      enabled: true,
      hotkey: 'Ctrl+Alt+Shift+O',
      intervalMs: 1000,
      hideFromCapture: false,
      screens: [{ key: 'AUS32F4/1', name: 'PG32UCDP', panels: [{ id: 'hud', title: 'HUD', x: 1, y: 0, layout: 'rows', readings: [{ source: 'gpu.g1.usage', visual: 'ring' }], style: presets.aurora! }] }],
    };
    mockApi({
      'GET /api/v1/status': () => statusResponse(),
      'GET /api/v1/overlay': (): OverlayResponse => ({ config: saved, state, fileProblem: null, recorderProblem: null, presets }),
      'GET /api/v1/overlay/state': () => state,
      'PUT /api/v1/overlay': (_url, init) => {
        saved = JSON.parse(String(init!.body)) as OverlayConfig;
        return { config: saved, state: null, fileProblem: null, recorderProblem: null };
      },
    });
    renderApp('/on-screen');

    await userEvent.click(await screen.findByRole('button', { name: /^HUD \(1 reading\) on PG32UCDP/ }));
    await userEvent.click(await screen.findByRole('button', { name: /RTX 4090/, expanded: false }));
    const picker = screen.getByRole('radiogroup', { name: 'Shown with' });
    expect(within(picker).getAllByRole('radio')).toHaveLength(12);
    expect(within(picker).getByRole('radio', { name: 'Ring' })).toHaveAttribute('aria-checked', 'true');
    await userEvent.click(within(picker).getByRole('radio', { name: 'Speedometer' }));
    expect(screen.getByText('A scale with ticks and a needle pointing at the value.')).toBeInTheDocument();
    await saveChanges();
    await waitFor(() => expect(saved.screens![0]!.panels[0]!.readings[0]).toMatchObject({ visual: 'speed' }), { timeout: 3000 });
  });

  it('puts a panel made from a ready-made design back as the design makes it, in its place', async () => {
    const design = DESIGNS.find((d) => d.id === 'obsidian')!;
    const made = { ...design.build(sources), id: 'hud', x: 0.3, y: 0.2, design: design.id } as OverlayPanel;
    let saved: OverlayConfig = {
      version: 1,
      enabled: true,
      hotkey: 'Ctrl+Alt+Shift+O',
      intervalMs: 1000,
      hideFromCapture: false,
      screens: [{ key: 'AUS32F4/1', name: 'PG32UCDP', panels: [{ ...made, title: 'Mine now', readings: made.readings.slice(0, 1) }] }],
    };
    mockApi({
      'GET /api/v1/status': () => statusResponse(),
      'GET /api/v1/overlay': (): OverlayResponse => ({ config: saved, state, fileProblem: null, recorderProblem: null, presets }),
      'GET /api/v1/overlay/state': () => state,
      'PUT /api/v1/overlay': (_url, init) => {
        saved = JSON.parse(String(init!.body)) as OverlayConfig;
        return { config: saved, state, fileProblem: null, recorderProblem: null, presets };
      },
    });
    renderApp('/on-screen');

    await userEvent.click(await screen.findByRole('button', { name: /^Mine now/ }));
    await userEvent.click(await screen.findByRole('button', { name: `Reset to ${design.name}` }));
    await confirmIt();
    // As the design makes it again, and where it was; nothing left to reset.
    expect(screen.queryByRole('button', { name: `Reset to ${design.name}` })).toBeNull();
    await saveChanges();
    await waitFor(() => expect(saved.screens![0]!.panels[0]).toMatchObject({ id: 'hud', x: 0.3, y: 0.2, design: 'obsidian', title: made.title ?? null, readings: made.readings }), { timeout: 3000 });
  });

  it('gives a reading its own look apart from the panel’s, with what applies to its visual, and draws it', async () => {
    let saved: OverlayConfig = {
      version: 1,
      enabled: true,
      hotkey: 'Ctrl+Alt+Shift+O',
      intervalMs: 1000,
      hideFromCapture: false,
      screens: [{ key: 'AUS32F4/1', name: 'PG32UCDP', panels: [{ id: 'hud', title: 'HUD', x: 1, y: 0, layout: 'rows', readings: [{ source: 'gpu.g1.usage', visual: 'ring' }], style: presets.aurora! }] }],
    };
    mockApi({
      'GET /api/v1/status': () => statusResponse(),
      'GET /api/v1/overlay': (): OverlayResponse => ({ config: saved, state, fileProblem: null, recorderProblem: null, presets }),
      'GET /api/v1/overlay/state': () => state,
      'PUT /api/v1/overlay': (_url, init) => {
        saved = JSON.parse(String(init!.body)) as OverlayConfig;
        return { config: saved, state: null, fileProblem: null, recorderProblem: null };
      },
    });
    renderApp('/on-screen');

    await userEvent.click(await screen.findByRole('button', { name: /^HUD \(1 reading\) on PG32UCDP/ }));
    await userEvent.click(await screen.findByRole('button', { name: /RTX 4090/, expanded: false }));
    const look = screen.getByRole('region', { name: 'Its look' });
    // A ring: its size, thickness and track; not a bar's style or a graph's area.
    expect(within(look).getByRole('slider', { name: 'Dial size' })).toBeInTheDocument();
    expect(within(look).getByRole('slider', { name: 'Thickness' })).toBeInTheDocument();
    expect(within(look).getByRole('slider', { name: 'Track opacity' })).toBeInTheDocument();
    expect(within(look).queryByRole('radiogroup', { name: 'Its bar style' })).toBeNull();
    expect(within(look).queryByRole('slider', { name: 'Area under the line' })).toBeNull();
    fireEvent.change(within(look).getByRole('slider', { name: 'Value size' }), { target: { value: '200' } });
    await userEvent.selectOptions(within(look).getByRole('combobox', { name: 'Its font' }), 'Consolas');
    await userEvent.click(within(within(look).getByRole('radiogroup', { name: 'Unit' })).getByRole('radio', { name: 'Off' }));
    await saveChanges();
    await waitFor(() => expect(saved.screens![0]!.panels[0]!.readings[0]!.look).toEqual({ valueSize: 200, font: 'Consolas', showUnit: false }), { timeout: 3000 });
    // The panel's own look is unchanged.
    expect(saved.screens![0]!.panels[0]!.style).toEqual(presets.aurora);

    // Drawn in its font at twice the size, without its unit; another reading keeps the panel's.
    const panel: OverlayPanel = { ...saved.screens![0]!.panels[0]!, readings: [...saved.screens![0]!.panels[0]!.readings, { source: 'cpu.usage' }] };
    const { container } = render(<PanelPreview panel={panel} sources={new Map(sources.map((s) => [s.id, s]))} scale={1} />);
    const [own, other] = Array.from(container.querySelectorAll<HTMLElement>('[data-value]'));
    expect(parseFloat(own!.style.fontSize)).toBeCloseTo(parseFloat(other!.style.fontSize) * 2, 1);
    expect(own!.style.fontFamily).toContain('Consolas');
    expect(other!.style.fontFamily).toBe('');
    expect(own!.textContent).not.toContain('%');
  });

  it('saves only when asked, and asks before another panel is chosen with changes not saved', async () => {
    let saved: OverlayConfig = {
      version: 1,
      enabled: true,
      hotkey: 'Ctrl+Alt+Shift+O',
      intervalMs: 1000,
      hideFromCapture: false,
      screens: [
        {
          key: 'AUS32F4/1',
          name: 'PG32UCDP',
          panels: [
            { id: 'hud', title: 'HUD', x: 1, y: 0, layout: 'rows', readings: [{ source: 'gpu.g1.usage' }], style: presets.aurora! },
            { id: 'net', title: 'Net', x: 0, y: 1, layout: 'rows', readings: [{ source: 'net.n1.down' }], style: presets.aurora! },
          ],
        },
      ],
    };
    const fetchMock = mockApi({
      'GET /api/v1/status': () => statusResponse(),
      'GET /api/v1/overlay': (): OverlayResponse => ({ config: saved, state, fileProblem: null, recorderProblem: null, presets }),
      'GET /api/v1/overlay/state': () => state,
      'PUT /api/v1/overlay': (_url, init) => {
        saved = JSON.parse(String(init!.body)) as OverlayConfig;
        return { config: saved, state: null, fileProblem: null, recorderProblem: null };
      },
    });
    renderApp('/on-screen');
    const puts = () => fetchMock.mock.calls.filter(([u, i]) => String(u).endsWith('/api/v1/overlay') && i?.method === 'PUT').length;

    // Choosing panels, nothing changed: never asked to save (a click on a panel counted as a change).
    await userEvent.click(await screen.findByRole('button', { name: /^HUD \(1 reading\) on PG32UCDP/ }));
    await userEvent.click(screen.getByRole('button', { name: /^Net \(1 reading\) on PG32UCDP/ }));
    await userEvent.click(screen.getByRole('button', { name: /^HUD \(1 reading\) on PG32UCDP/ }));
    expect(screen.queryByRole('dialog', { name: 'Save your changes?' })).toBeNull();
    expect(screen.queryByText('Changes not saved')).toBeNull();

    await userEvent.click(await screen.findByRole('tab', { name: 'Look' }));
    await userEvent.click(within(screen.getByRole('group', { name: 'Ready-made looks' })).getByRole('button', { name: 'Neon' }));
    expect(await screen.findByRole('region', { name: 'Changes not saved' })).toBeInTheDocument();
    await new Promise((r) => setTimeout(r, 800));
    expect(puts()).toBe(0);

    // Another panel: asked first; keep editing stays on this one.
    await userEvent.click(screen.getByRole('button', { name: /^Net \(1 reading\) on PG32UCDP/ }));
    const ask = await screen.findByRole('dialog', { name: 'Save your changes?' });
    await userEvent.click(within(ask).getByRole('button', { name: 'Keep editing' }));
    expect(screen.queryByRole('dialog', { name: 'Save your changes?' })).toBeNull();
    // Asked again, and saved from the prompt.
    await userEvent.click(screen.getByRole('button', { name: /^Net \(1 reading\) on PG32UCDP/ }));
    await userEvent.click(within(await screen.findByRole('dialog', { name: 'Save your changes?' })).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(puts()).toBe(1));
    expect(saved.screens![0]!.panels[0]!.style.font).toBe('Consolas');
    await waitFor(() => expect(screen.queryByRole('region', { name: 'Changes not saved' })).toBeNull());

    // A change discarded: back to the layout as saved, nothing sent (the panel chosen now opens on its readings).
    await userEvent.click(await screen.findByRole('tab', { name: 'Look' }));
    await userEvent.click(within(await screen.findByRole('group', { name: 'Ready-made looks' })).getByRole('button', { name: 'Neon' }));
    await userEvent.click(within(await screen.findByRole('region', { name: 'Changes not saved' })).getByRole('button', { name: 'Discard' }));
    await waitFor(() => expect(screen.queryByRole('region', { name: 'Changes not saved' })).toBeNull());
    expect(puts()).toBe(1);
  });

  it('counts unsaved changes, undoes and redoes them, and keeps the other parts in tabs', async () => {
    const saved: OverlayConfig = {
      version: 1,
      enabled: true,
      hotkey: 'Ctrl+Alt+Shift+O',
      intervalMs: 1000,
      hideFromCapture: false,
      screens: [{ key: 'AUS32F4/1', name: 'PG32UCDP', panels: [{ id: 'hud', title: 'HUD', x: 1, y: 0, layout: 'rows', readings: [{ source: 'gpu.g1.usage' }], style: presets.aurora! }] }],
    };
    mockApi({
      'GET /api/v1/status': () => statusResponse(),
      'GET /api/v1/overlay': (): OverlayResponse => ({ config: saved, state, fileProblem: null, recorderProblem: null, presets }),
      'GET /api/v1/overlay/state': () => state,
    });
    {
      renderApp('/on-screen');
      await userEvent.click(await screen.findByRole('button', { name: /^HUD \(1 reading\) on PG32UCDP/ }));
      expect(screen.getByRole('navigation', { name: 'Screen editor' })).toBeInTheDocument();
      // The panel's settings slide in.
      await userEvent.click(await screen.findByRole('tab', { name: 'Look' }));
      await userEvent.click(within(screen.getByRole('group', { name: 'Ready-made looks' })).getByRole('button', { name: 'Neon' }));
      expect(await screen.findByText('1 unsaved change')).toBeInTheDocument();
      await userEvent.keyboard('{Control>}z{/Control}');
      await waitFor(() => expect(screen.queryByText('1 unsaved change')).toBeNull());
      await userEvent.keyboard('{Control>}y{/Control}');
      expect(await screen.findByText('1 unsaved change')).toBeInTheDocument();
      await userEvent.click(screen.getByRole('button', { name: /^All panels/ }));
      expect(await screen.findByRole('heading', { name: 'All panels' })).toBeInTheDocument();
    }
  });

  it('sits a tile’s value at the top, middle or bottom as chosen, for the panel or one reading', () => {
    const map = new Map(sources.map((x) => [x.id, x]));
    const draw = (valueVertical: 'top' | 'middle' | 'bottom' | null, readings: OverlayPanel['readings']) =>
      render(<PanelPreview panel={{ id: 'p', title: null, x: 0, y: 0, layout: 'tiles', columns: 2, readings, style: { ...DEFAULT_STYLE, valueVertical } }} sources={map} scale={1} />).container;
    const rows = (c: HTMLElement) => [...c.querySelectorAll<HTMLElement>('[data-tile]')].map((t) => t.querySelector('[data-value]')!.parentElement!.className);
    const both = [{ source: 'cpu.usage' }, { source: 'memory.usage', visual: 'bar' as const }];
    // Automatic: alone in the middle, over a bar at the top.
    const [alone, bar] = rows(draw(null, both));
    expect(alone).toContain('items-center');
    expect(bar).toContain('items-start');
    // The panel's choice for both; one reading's own over it.
    expect(rows(draw('bottom', both)).every((c) => c.includes('items-end') && c.includes('flex-1'))).toBe(true);
    expect(rows(draw('top', [{ source: 'cpu.usage', look: { vertical: 'middle' } }, { source: 'memory.usage', visual: 'bar' }]))).toEqual([expect.stringContaining('items-center'), expect.stringContaining('items-start')]);
  });

  it('keeps a screen’s own characters with its panels, and they react to all its readings', () => {
    const kitty = { id: 'char-1', character: 'mikan', place: 'screen' as const, movement: 'wander' as const, action: 'idle', x: 0.5, size: 48, speed: 100, flip: false, behind: false, opacity: 100, react: 'auto' };
    const p = (id: string): OverlayPanel => ({ id, title: null, x: 0, y: 0, layout: 'rows', readings: [], style: DEFAULT_STYLE });
    let config = withScreenCharacters({ version: 1, enabled: true, hotkey: 'X', intervalMs: 1000, hideFromCapture: false, screens: [] }, 'S', 'Screen', [kitty]);
    expect(screenCharactersOf(config, 'S')).toEqual([kitty]);
    config = withPanels(config, 'S', 'Screen', [p('S')]);
    expect(screenCharactersOf(config, 'S')).toEqual([kitty]);
    config = withPanels(config, 'S', 'Screen', []);
    expect(screenCharactersOf(config, 'S')).toEqual([kitty]);
    expect(screenCharactersOf(withScreenCharacters(config, 'S', 'Screen', []), 'S')).toEqual([]);
    const stage = screenStage([{ ...p('a'), readings: [{ source: 'cpu.usage' }] }, { ...p('b'), readings: [{ source: 'cpu.usage' }, { source: 'gpu.g1.temp' }] }, { ...p('c'), hidden: true, readings: [{ source: 'net.n1.up' }] }], [kitty]);
    expect(stage.readings.map((r) => r.source)).toEqual(['cpu.usage', 'gpu.g1.temp']);
  });

  it('does not move a panel that is only clicked (a wobble of a few pixels)', () => {
    const panel: OverlayPanel = { id: 'p', title: 'P', x: 0, y: 0, layout: 'rows', readings: [{ source: 'cpu.usage' }], style: DEFAULT_STYLE };
    const onMove = vi.fn();
    render(<ScreenEditor display={state.displays[0]!} panels={[panel]} sources={new Map(sources.map((x) => [x.id, x]))} selected={null} onSelect={() => {}} onMove={onMove} onRemove={() => {}} />);
    const box = screen.getByRole('button', { name: /^P \(1 reading\)/ });
    fireEvent.pointerDown(box, { clientX: 100, clientY: 100 });
    fireEvent.pointerMove(box, { clientX: 102, clientY: 101 });
    fireEvent.pointerUp(box, { clientX: 102, clientY: 101 });
    expect(onMove).not.toHaveBeenCalled();
    fireEvent.pointerDown(box, { clientX: 100, clientY: 100 });
    fireEvent.pointerMove(box, { clientX: 130, clientY: 100 });
    fireEvent.pointerUp(box, { clientX: 130, clientY: 100 });
    expect(onMove).toHaveBeenCalled();
  });

  it('moves and sizes a value from its menu, live, and opens its reading when it is double-clicked', () => {
    const panel: OverlayPanel = { id: 'p', title: null, x: 0, y: 0, layout: 'tiles', columns: 2, readings: [{ source: 'cpu.usage' }, { source: 'memory.usage', visual: 'bar' }], style: DEFAULT_STYLE };
    const onPanel = vi.fn();
    const onReading = vi.fn();
    const { container } = render(
      <ScreenEditor display={state.displays[0]!} panels={[panel]} sources={new Map(sources.map((x) => [x.id, x]))} selected="p" onSelect={() => {}} onMove={() => {}} onRemove={() => {}} onPanel={onPanel} onReading={onReading} />,
    );
    for (const tile of container.querySelectorAll<HTMLElement>('[data-tile]')) tile.getBoundingClientRect = () => ({ left: 0, top: 0, width: 300, height: 300, right: 300, bottom: 300, x: 0, y: 0, toJSON: () => ({}) });
    const [, ram] = Array.from(container.querySelectorAll<HTMLElement>('[data-value]'));
    // Pointed at: its menu.
    fireEvent.pointerMove(ram!, { clientX: 150, clientY: 20, buttons: 0 });
    const menu = screen.getByRole('toolbar', { name: 'memory.usage: its value' });
    // Move: it follows the mouse, sitting on the third it is over; a click sets it.
    fireEvent.click(within(menu).getByRole('button', { name: /Move/ }));
    fireEvent.pointerMove(ram!, { clientX: 280, clientY: 140, buttons: 0 });
    expect(onPanel).toHaveBeenLastCalledWith(expect.objectContaining({ readings: [{ source: 'cpu.usage' }, expect.objectContaining({ look: { align: 'right', vertical: 'middle' } })] }));
    fireEvent.pointerDown(ram!, { clientX: 280, clientY: 140 });
    expect(screen.getByRole('toolbar', { name: 'memory.usage: its value' })).not.toHaveTextContent('click to set');
    // Size: the mouse up makes it larger.
    fireEvent.click(within(screen.getByRole('toolbar', { name: 'memory.usage: its value' })).getByRole('button', { name: /Size/ }), { clientY: 200 });
    fireEvent.pointerMove(ram!, { clientX: 280, clientY: 150, buttons: 0 });
    expect(onPanel.mock.lastCall![0].readings[1].look.valueSize).toBe(150);
    fireEvent.pointerDown(ram!, { clientX: 280, clientY: 150 });
    // A click does not open its reading; a double click does.
    fireEvent.pointerDown(ram!, { clientX: 100, clientY: 100 });
    fireEvent.pointerUp(ram!, { clientX: 100, clientY: 100 });
    fireEvent.click(ram!);
    expect(onReading).not.toHaveBeenCalled();
    fireEvent.doubleClick(ram!);
    expect(onReading).toHaveBeenCalledWith('p', 1);
  });

  it('shows a value not read yet by its name with a loading mark, never its id', () => {
    const panel: OverlayPanel = { id: 'p', title: null, x: 0, y: 0, layout: 'line', readings: [{ source: 'sensor.lhm/gpu-nvidia/0/temperature/2' }], style: DEFAULT_STYLE };
    const loading: OverlaySource = { id: 'sensor.lhm/gpu-nvidia/0/temperature/2', label: 'GPU temperature', group: 'RTX 4090', unit: '°C', value: null, loading: true };
    const { container } = render(<PanelPreview panel={panel} sources={new Map([[loading.id, loading]])} scale={1} />);
    expect(container.textContent).toContain('GPU temperature');
    expect(container.textContent).not.toContain('sensor.lhm');
    expect(container.querySelector('[data-loading]')!.textContent).toContain('···');
  });
});

describe('On-screen readings: readable units', () => {
  it('keeps labels and units readable on the panel, and a percentage sign with its number, as the recorder does', () => {
    // The same answers as the recorder's Readable.
    expect(readableOn('#64748B', '#0F172A', '#F1F5F9')).toBe('#728196');
    expect(readableOn('#CBD5E1', '#0F172A', '#FFFFFF')).toBe('#CBD5E1');
    expect(readableOn('#94A3B8', '#1E1B4B', '#FFFFFF')).toBe('#94A3B8');
    expect(readableOn('#CBD5E1', '#FFFFFF', '#0F172A')).toBe('#6E7787');
    const dim: OverlayStyle = { ...presets.aurora!, background: '#0F172A', background2: null, backgroundOpacity: 72, valueColor: '#F1F5F9', unitColor: '#64748B', labelColor: '#64748B' };
    expect(readable('#64748B', dim)).toBe('#728196');
    expect(readable('#64748B', { ...dim, backgroundOpacity: 20 })).toBe('#64748B');

    const panel: OverlayPanel = { id: 'p', title: null, x: 0, y: 0, layout: 'line', style: dim, readings: [{ source: 'cpu.usage' }, { source: 'cpu.temp' }] };
    const { container } = render(<PanelPreview panel={panel} sources={new Map(sources.map((x) => [x.id, x]))} scale={1} />);
    const [cpu, temp] = [...container.querySelectorAll<HTMLElement>('[data-value] .value-text')];
    const unitOf = (value: HTMLElement) => value.children[1] as HTMLElement;
    // "52%": the sign in the value's color; "91 °C": the unit in the readable unit color.
    expect(unitOf(cpu!).textContent).toBe('%');
    expect(unitOf(cpu!).style.color).toBe('rgb(241, 245, 249)');
    expect(unitOf(temp!).style.color).toBe('rgb(114, 129, 150)');
    expect((container.querySelector('[data-label]') as HTMLElement).style.color).toBe('rgb(114, 129, 150)');
  });
});

describe('On-screen readings: its value or a percentage', () => {
  it('reads VRAM as its percentage or its amount, the limits following, and offers it for every number', async () => {
    const vramPct: OverlaySource = { id: 'gpu.g1.vram_pct', label: 'VRAM', group: 'RTX 4090', unit: '%', value: 14 };
    const withTotal = sources.map((s) => (s.id === 'gpu.g1.vram' ? { ...s, max: 24 * GB } : s));
    const live: OverlayState = { ...state, sources: [...withTotal, vramPct, { id: 'gpu.g1.power', label: 'GPU power', group: 'RTX 4090', unit: 'W', value: 74 }] };
    expect(valueSourceOf('gpu.g1.vram_pct')).toBe('gpu.g1.vram');
    expect(valueSourceOf('memory.usage')).toBe('memory.used');
    expect(valueSourceOf('cpu.usage')).toBeNull();
    let saved: OverlayConfig = {
      version: 1,
      enabled: true,
      hotkey: 'Ctrl+Alt+Shift+O',
      intervalMs: 1000,
      hideFromCapture: false,
      screens: [{ key: 'AUS32F4/1', name: 'PG32UCDP', panels: [{ id: 'hud', title: 'HUD', x: 1, y: 0, layout: 'rows', readings: [{ source: 'gpu.g1.vram_pct', warning: 80, critical: 95 }, { source: 'cpu.usage' }, { source: 'gpu.g1.power' }], style: presets.aurora! }] }],
    };
    mockApi({
      'GET /api/v1/status': () => statusResponse(),
      'GET /api/v1/overlay': (): OverlayResponse => ({ config: saved, state: live, fileProblem: null, recorderProblem: null, presets }),
      'GET /api/v1/overlay/state': () => live,
      'PUT /api/v1/overlay': (_url, init) => {
        saved = JSON.parse(String(init!.body)) as OverlayConfig;
        return { config: saved, state: null, fileProblem: null, recorderProblem: null };
      },
    });
    renderApp('/on-screen');

    await userEvent.click(await screen.findByRole('button', { name: /^HUD \(3 readings\) on PG32UCDP/ }));
    // VRAM's percentage: reads as a percentage now; as its value it reads its amount, 80 % and 95 % of 24 GB as limits.
    await userEvent.click(await screen.findByRole('button', { name: /^VRAM/, expanded: false }));
    const reads = screen.getByRole('radiogroup', { name: 'Reads as' });
    expect(within(reads).getByRole('radio', { name: 'Percent' })).toHaveAttribute('aria-checked', 'true');
    await userEvent.click(within(reads).getByRole('radio', { name: 'Value' }));
    await saveChanges();
    await waitFor(() => expect(saved.screens![0]!.panels[0]!.readings[0]).toMatchObject({ source: 'gpu.g1.vram', show: null, warning: Math.round(0.8 * 24 * GB), critical: Math.round(0.95 * 24 * GB) }), { timeout: 3000 });

    // Its settings stay open, now reading as its value; back to a percentage it is the percentage again.
    const again = screen.getAllByRole('radiogroup', { name: 'Reads as' })[0]!;
    expect(within(again).getByRole('radio', { name: 'Value' })).toHaveAttribute('aria-checked', 'true');

    // CPU load is a percentage only; GPU power has no total, so a percentage asks for its full scale.
    await userEvent.click(screen.getByRole('button', { name: /^CPU/, expanded: false }));
    expect(screen.getByText('A percentage (there is no other value to show for it).')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: /^GPU power/, expanded: false }));
    const power = screen.getAllByRole('radiogroup', { name: 'Reads as' }).at(-1)!;
    await userEvent.click(within(power).getByRole('radio', { name: 'Percent' }));
    expect(await screen.findByText(/needs its full scale: set “Full at” below/)).toBeInTheDocument();
  });
});

describe('On-screen readings: sizes, values and the grid', () => {
  it('reads a value as a percentage, both or of its total, as the recorder does', () => {
    const vram: OverlaySource = { id: 'gpu.g1.vram', label: 'VRAM', group: 'RTX 4090', unit: 'B', value: 22.1 * GB, max: 24 * GB };
    expect(shown(vram, { source: vram.id })).toEqual({ value: '22.1', unit: 'GB' });
    expect(shown(vram, { source: vram.id, show: 'percent' })).toEqual({ value: '92', unit: '%' });
    expect(shown(vram, { source: vram.id, show: 'both' })).toEqual({ value: '22.1', unit: 'GB · 92%' });
    expect(shown(vram, { source: vram.id, show: 'of' })).toEqual({ value: '22.1/24.0', unit: 'GB' });
    const temp: OverlaySource = { id: 'cpu.temp', label: 'CPU temp', group: 'Processor', unit: '°C', value: 61 };
    expect(shown(temp, { source: temp.id, show: 'percent' })).toEqual({ value: '61', unit: '°C' });
    expect(shown(temp, { source: temp.id, show: 'of', max: 100 })).toEqual({ value: '61/100', unit: '°C' });
  });

  it('keeps room for the widest a value can read, so a panel keeps its size, as the recorder does', () => {
    const of = (unit: string, max?: number) => widest({ id: 'x', label: 'X', group: 'G', unit, value: 5, max: max ?? null }, { source: 'x' });
    expect(of('%').values).toContain('000');
    expect(of('fps')).toEqual({ values: expect.arrayContaining(['000']), units: ['fps'] });
    expect(of('ms').values).toEqual(expect.arrayContaining(['00.0', '000']));
    expect(of('°C').values).toContain('000');
    expect(of('B/s').values).toEqual(expect.arrayContaining(['000', '000.0', '00.00']));
    expect(of('B/s').units.sort()).toEqual(['GB/s', 'KB/s', 'MB/s']);
    expect(of('B', 32 * GB).units.sort()).toEqual(['GB', 'KB', 'MB']);
    for (const unit of ['B', 'B/s', 'ms', 'fps']) expect(of(unit).values.every((v) => !v.includes('0000'))).toBe(true);
    expect(widest(sources.find((s) => s.id === 'time.clock'), { source: 'time.clock' }).values).toEqual([]);
    // The next unit from 1000 of one, and a tenth of a millisecond only under 100 as rounded.
    expect(formatSource({ id: 'x', label: 'X', group: 'G', unit: 'B/s', value: 999.7 * 1024 })).toEqual({ value: '1.0', unit: 'MB/s' });
    expect(formatSource({ id: 'x', label: 'X', group: 'G', unit: 'B/s', value: 1010 * 1024 ** 2 })).toEqual({ value: '0.99', unit: 'GB/s' });
    expect(formatSource({ id: 'x', label: 'X', group: 'G', unit: 'ms', value: 99.96 })).toEqual({ value: '100', unit: 'ms' });

    // The preview lays the number over its widest (hidden), so a ping of 8.0 and of 112 take the same room.
    const panel: OverlayPanel = { id: 'p', title: null, x: 0, y: 0, layout: 'line', style: presets.aurora!, readings: [{ source: 'net.ping.internet' }, { source: 'net.n1.down' }] };
    const rooms = (ping: number | null) => {
      const live = new Map(sources.map((s) => [s.id, s]));
      live.set('net.ping.internet', { id: 'net.ping.internet', label: 'Ping', group: 'Network', unit: 'ms', value: ping });
      const { container, unmount } = render(<PanelPreview panel={panel} sources={live} scale={1} />);
      const found = [...container.querySelectorAll('.value-ghost [data-room]')].map((e) => e.getAttribute('data-room'));
      unmount();
      return found;
    };
    expect(rooms(8)).toEqual(['00.0', 'ms', expect.stringMatching(/^(000\.0|00\.00)$/), expect.stringMatching(/^[KMG]B\/s$/)]);
    expect(rooms(112)).toEqual(rooms(8));
    expect(rooms(null)).toEqual(rooms(8));
  });

  it('gives every value a fixed space it sits in on the left, in the middle or on the right, and cuts a long name to fit', () => {
    const live = new Map(sources.map((s) => [s.id, s]));
    live.set('game.app', { id: 'game.app', label: 'Game', group: 'Gaming', unit: '', value: null, text: 'VeryLongProcessNameOfSomeLauncher', room: ['00000000000000'] });
    live.set('time.date', { id: 'time.date', label: 'Date', group: 'This PC', unit: '', value: null, text: 'Thu 9 Oct', room: ['Wed 00 May', 'Thu 00 Oct'] });
    const draw = (valueAlign?: 'left' | 'center' | 'right') => {
      const panel: OverlayPanel = {
        id: 'p',
        title: null,
        x: 0,
        y: 0,
        layout: 'rows',
        style: { ...presets.aurora!, ...(valueAlign ? { valueAlign } : {}) },
        readings: [{ source: 'cpu.usage' }, { source: 'game.app' }, { source: 'time.date' }, { source: 'time.clock' }],
      };
      return render(<PanelPreview panel={panel} sources={live} scale={1} />).container;
    };
    // In the middle unless chosen otherwise; a row's value fills the value column.
    const slots = [...draw().querySelectorAll<HTMLElement>('[data-value]')];
    expect(slots.map((v) => v.dataset.align)).toEqual(['center', 'center', 'center', 'center']);
    expect(slots.every((v) => v.classList.contains('value-slot-fill') && v.style.textAlign === 'center')).toBe(true);
    expect([...draw('right').querySelectorAll<HTMLElement>('[data-value]')].every((v) => v.style.textAlign === 'right')).toBe(true);
    // A name is cut to its space (the text is whole, the space shortens it), a date and the time keep their widest.
    const [, app, date] = slots;
    expect(app!.querySelector('.value-text')!.classList.contains('value-fit')).toBe(true);
    expect(app!.querySelector('.value-ghost [data-room]')!.getAttribute('data-room')).toBe('00000000000000');
    expect(date!.querySelector('.value-ghost [data-room]')!.getAttribute('data-room')).toBe('Wed 00 May');
    expect(app!.textContent).toBe('VeryLongProcessNameOfSomeLauncher');
  });

  it('packs tiles densely, as the recorder does', () => {
    expect(pack([{ source: 'a', colSpan: 2, rowSpan: 2 }, { source: 'b' }, { source: 'c' }, { source: 'd' }], 3)).toEqual([
      { row: 0, column: 0, columns: 2, rows: 2 },
      { row: 0, column: 2, columns: 1, rows: 1 },
      { row: 1, column: 2, columns: 1, rows: 1 },
      { row: 2, column: 0, columns: 1, rows: 1 },
    ]);
  });

  it('designs the grid: a tile made wider and taller, then fixed to a size', async () => {
    let saved: OverlayConfig = {
      version: 1,
      enabled: true,
      hotkey: 'Ctrl+Alt+Shift+O',
      intervalMs: 1000,
      hideFromCapture: false,
      screens: [{ key: 'AUS32F4/1', name: 'PG32UCDP', panels: [{ id: 'hud', title: 'HUD', x: 1, y: 0, layout: 'tiles', columns: 3, readings: [{ source: 'gpu.g1.usage', visual: 'ring' }, { source: 'cpu.usage' }, { source: 'memory.usage' }], style: presets.aurora! }] }],
    };
    mockApi({
      'GET /api/v1/status': () => statusResponse(),
      'GET /api/v1/overlay': (): OverlayResponse => ({ config: saved, state, fileProblem: null, recorderProblem: null, presets }),
      'GET /api/v1/overlay/state': () => state,
      'PUT /api/v1/overlay': (_url, init) => {
        saved = JSON.parse(String(init!.body)) as OverlayConfig;
        return { config: saved, state: null, fileProblem: null, recorderProblem: null };
      },
    });
    renderApp('/on-screen');

    const panel = await screen.findByRole('button', { name: /^HUD \(3 readings\) on PG32UCDP/ });
    await userEvent.click(panel);
    await userEvent.click(await screen.findByRole('tab', { name: 'Layout' }));
    const grid = screen.getByRole('list', { name: 'Tiles in the grid' });
    await userEvent.click(within(grid).getByRole('listitem', { name: /^GPU/ }));
    const gpu = screen.getByRole('toolbar', { name: 'Arrange GPU' });
    await userEvent.click(within(gpu).getByRole('button', { name: 'GPU wider' }));
    await userEvent.click(within(gpu).getByRole('button', { name: 'GPU taller' }));
    await userEvent.click(within(grid).getByRole('listitem', { name: /^RAM/ }));
    await userEvent.click(within(screen.getByRole('toolbar', { name: 'Arrange RAM' })).getByRole('button', { name: 'Move RAM earlier' }));
    await userEvent.click(within(screen.getByRole('radiogroup', { name: 'Size' })).getByRole('radio', { name: 'Fixed' }));
    await saveChanges();
    await waitFor(
      () =>
        expect(saved.screens![0]!.panels[0]).toMatchObject({
          width: 320,
          height: 180,
          readings: [{ source: 'gpu.g1.usage', colSpan: 2, rowSpan: 2 }, { source: 'memory.usage' }, { source: 'cpu.usage' }],
        }),
      { timeout: 3000 },
    );
  });
});

describe('On-screen readings: designs, pictures and patterns', () => {
  const rich: OverlaySource[] = [
    ...sources,
    { id: 'game.fps', label: 'FPS', group: 'Game', unit: 'fps', value: 144 },
    { id: 'game.low1', label: '1% low', group: 'Game', unit: 'fps', value: 98 },
    { id: 'game.frametime', label: 'Frame time', group: 'Game', unit: 'ms', value: 6.9 },
    { id: 'net.ping.router', label: 'Ping router', group: 'Network', unit: 'ms', value: 0.8 },
    { id: 'net.ping.internet', label: 'Ping internet', group: 'Network', unit: 'ms', value: 9 },
    { id: 'cpu.max_core', label: 'Busiest core', group: 'Processor', unit: '%', value: 71 },
    { id: 'gpu.g1.hotspot', label: 'Hot spot', group: 'RTX 4090', unit: '°C', value: 58 },
    { id: 'gpu.g1.vram_pct', label: 'VRAM', group: 'RTX 4090', unit: '%', value: 14 },
    { id: 'time.seconds', label: 'Time', group: 'This PC', unit: '', value: null, text: '01:53:20' },
    { id: 'time.date', label: 'Date', group: 'This PC', unit: '', value: null, text: 'Fri 9 Oct' },
  ];

  it('builds every design from this PC’s values and draws it', () => {
    expect(DESIGNS).toHaveLength(61);
    expect(new Set(DESIGNS.map((d) => d.id)).size).toBe(DESIGNS.length);
    for (const design of DESIGNS) {
      const built = design.build(rich);
      expect(built.readings.length, design.id).toBeGreaterThan(0);
      // Only values this PC has, spans within the grid, a look the recorder accepts.
      for (const r of built.readings) expect(rich.some((s) => s.id === r.source), `${design.id} ${r.source}`).toBe(true);
      if (built.layout === 'tiles') for (const r of built.readings) expect(r.colSpan ?? 1, design.id).toBeLessThanOrEqual(built.columns ?? 2);
      expect(built.columns ?? 2, design.id).toBeLessThanOrEqual(6);
      expect(BACKDROPS.some((b) => b.id === (built.style.backdrop ?? 'none')), design.id).toBe(true);
      expect(built.style.cornerRadius).toBeLessThanOrEqual(32);
      expect(built.style.valueScale).toBeLessThanOrEqual(300);
      const { container, unmount } = render(<PanelPreview panel={{ id: design.id, x: 0, y: 0, ...built }} sources={new Map(rich.map((s) => [s.id, s]))} scale={1} />);
      expect(container.querySelectorAll('[data-value]').length, design.id).toBe(built.readings.length);
      unmount();
    }

    // Without the readings a design shows, it has none (the gallery says so instead of adding an empty panel).
    expect(DESIGNS.find((d) => d.id === 'pulse')!.build([]).readings).toEqual([]);
  });

  it('draws a backdrop behind every tile, fitted to it, and a bezel around each dial', () => {
    // The tile's size as a browser measures it (jsdom lays nothing out).
    class Measured {
      constructor(private readonly done: ResizeObserverCallback) {}
      observe(target: Element) {
        this.done([{ contentRect: { width: 180, height: 120 }, target } as unknown as ResizeObserverEntry], this as unknown as ResizeObserver);
      }
      disconnect() {}
      unobserve() {}
    }
    vi.stubGlobal('ResizeObserver', Measured);
    try {
      const sources = new Map(rich.map((s) => [s.id, s]));
      const panel = (backdrop: OverlayStyle['backdrop']): OverlayPanel => ({
        id: 'p',
        title: null,
        x: 0,
        y: 0,
        layout: 'tiles',
        columns: 2,
        readings: [
          { source: 'cpu.usage', visual: 'ring', warning: 10 },
          { source: 'cpu.temp', visual: 'bar' },
        ],
        style: { ...DEFAULT_STYLE, backdrop },
      });
      expect(BACKDROPS.map((b) => b.id)).toEqual(['none', 'plate', 'hud', 'bezel', 'socket', 'chevron', 'glass', 'neon', 'blueprint']);

      const none = render(<PanelPreview panel={panel('none')} sources={sources} scale={1} />);
      expect(none.container.querySelectorAll('[data-backdrop]')).toHaveLength(0);
      none.unmount();

      for (const { id } of BACKDROPS.filter((b) => b.id !== 'none')) {
        const { container, unmount } = render(<PanelPreview panel={panel(id)} sources={sources} scale={1} />);
        const layers = container.querySelectorAll('[data-backdrop]');
        expect(layers, id).toHaveLength(2);
        // Under the readings, in the tile's own stacking context, drawn at the tile's size.
        expect((layers[0] as HTMLElement).style.zIndex, id).toBe('-1');
        expect((layers[0]!.parentElement as HTMLElement).style.isolation, id).toBe('isolate');
        expect(layers[1]!.querySelector('svg')?.getAttribute('width'), id).toBe('180');
        // Past its limit, the tile's outline is drawn over the backdrop.
        expect(layers[0]!.querySelector('[stroke^="rgba(245, 158, 11"]'), id).not.toBeNull();
        // A bezel only around the dial; a chevron tile takes its cut shape (the tile itself has no color).
        expect(container.querySelectorAll('[data-bezel]'), id).toHaveLength(id === 'bezel' ? 1 : 0);
        if (id === 'chevron') expect((layers[1]!.parentElement as HTMLElement).style.background, id).toBe('');
        unmount();
      }
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it('draws a picture for a design from where its readings are: a face behind each dial, a grid behind each graph', () => {
    // Every art draws for a panel of dials, a graph, bars, a value and a light.
    const panel: OverlayPanel = {
      id: 'p',
      title: null,
      x: 0,
      y: 0,
      layout: 'tiles',
      columns: 3,
      width: 900,
      height: 500,
      readings: [
        { source: 'cpu.usage', visual: 'arc', warning: 85 },
        { source: 'game.fps', visual: 'speed', max: 240 },
        { source: 'memory.usage', visual: 'ring' },
        { source: 'cpu.temp', visual: 'bar', max: 100, warning: 85 },
        { source: 'net.n1.down', visual: 'graph' },
        { source: 'gpu.g1.hotspot', visual: 'status', warning: 95 },
      ],
      style: { ...DEFAULT_STYLE, size: 18, padding: 16 },
    };
    const g = panelGeometry(panel)!;
    expect(g.tiles).toHaveLength(6);
    // Tiles side by side in the panel's padding, three to a row; each dial in its own tile, the arc's open side down.
    expect(g.tiles[0]!.x).toBeCloseTo(16, 5);
    expect(g.tiles[3]!.y).toBeGreaterThan(g.tiles[0]!.y + g.tiles[0]!.h);
    for (const tile of g.tiles.slice(0, 3)) {
      const dial = tile.dial!;
      expect(dial.cx - dial.d / 2, tile.reading.source).toBeGreaterThanOrEqual(tile.x);
      expect(dial.cx + dial.d / 2, tile.reading.source).toBeLessThanOrEqual(tile.x + tile.w + 0.01);
    }
    expect(g.tiles.map((t) => [t.dial?.start, t.dial?.sweep])).toEqual([[145, 250], [160, 220], [-90, 360], [undefined, undefined], [undefined, undefined], [undefined, undefined]]);
    expect(g.tiles[3]!.bar!.y + g.tiles[3]!.bar!.h).toBeLessThanOrEqual(g.tiles[3]!.y + g.tiles[3]!.h);
    expect(g.tiles[4]!.chart!.w).toBeCloseTo(g.tiles[4]!.body.w, 5);
    expect(g.tiles[5]!.light).not.toBeNull();

    for (const kind of Object.keys(ART_THEMES) as (keyof typeof ART_THEMES)[]) {
      const svg = artSvg(panel, kind)!;
      expect(svg, kind).toMatch(/^<svg xmlns="http:\/\/www.w3.org\/2000\/svg" width="900" height="500"/);
      // A face for each of the three dials, ticks along them, a scope behind the graph, a channel under the bar.
      expect(svg.match(/fill="url\(#face\)"/g), kind).toHaveLength(3);
      expect((svg.match(/<line /g) ?? []).length, kind).toBeGreaterThan(100);
      if (ART_THEMES[kind].numbers) expect(svg, kind).toContain('>240<');
      if (ART_THEMES[kind].redZone) expect(svg, kind).toContain('stroke="#EF4444"');
    }
    // Not tiles, or no fixed size: nothing to draw around.
    expect(artSvg({ ...panel, width: null }, 'cockpit')).toBeNull();
    expect(artSvg({ ...panel, layout: 'rows' }, 'cockpit')).toBeNull();
    // What it depends on: its size and readings, not its picture.
    expect(artSignature({ ...panel, style: { ...panel.style, image: '0123456789abcdef.png' } })).toBe(artSignature(panel));
    expect(artSignature({ ...panel, width: 901 })).not.toBe(artSignature(panel));

    // The designs with pictures: each a fixed size with its art and no picture yet (the editor draws and saves it).
    expect(ART_SCREEN_DESIGNS.length).toBeGreaterThanOrEqual(26);
    expect(ART_PANEL_DESIGNS).toHaveLength(8);
    for (const design of ART_SCREEN_DESIGNS) {
      for (const p of fitScreenDesign(design, rich, sizeOf(design))) {
        expect(p.style.art, design.id).toBeTruthy();
        expect(p.style.image ?? null, design.id).toBeNull();
        expect(artUrl(p), design.id).toMatch(/^data:image\/svg\+xml/);
      }
    }
    for (const design of ART_PANEL_DESIGNS) expect(artUrl({ ...design.build(rich) }), design.id).toMatch(/^data:image\/svg\+xml/);

    // The editor draws the art live, stretched over the panel.
    const built = { ...ART_PANEL_DESIGNS[0]!.build(rich), id: 'a', x: 0, y: 0 };
    const { container, unmount } = render(<PanelPreview panel={built} sources={new Map(rich.map((src) => [src.id, src]))} scale={1} />);
    const layer = container.querySelector<HTMLElement>('[data-art]')!;
    expect(layer.dataset.art).toBe('cockpit');
    expect((layer.firstElementChild as HTMLElement).style.backgroundSize).toBe('100% 100%');
    unmount();
  });

  it('builds every whole-screen design to fill its size, and fits it to other screens', () => {
    expect(SCREEN_DESIGNS).toHaveLength(145);
    expect(new Set(SCREEN_DESIGNS.map((d) => d.id)).size).toBe(145);
    // Every kind of screen has one, upright and wide, and every design is made for a listed size.
    for (const family of ['bar', 'square', 'small', 'tablet', 'portable', 'monitor']) expect(SCREEN_DESIGNS.some((d) => sizeOf(d).family === family), family).toBe(true);
    expect(SCREEN_DESIGNS.filter((d) => d.size === 'bar-88-portrait').length).toBeGreaterThanOrEqual(12);
    expect(SCREEN_SIZES.every((s) => SCREEN_DESIGNS.some((d) => d.size === s.id))).toBe(true);

    for (const design of SCREEN_DESIGNS) {
      const made = sizeOf(design);
      const parts = fitScreenDesign(design, rich, made);
      expect(parts.length, design.id).toBeGreaterThan(0);
      // Its panels cover the screen exactly (opaque, side by side), with values this PC has and spans within the grid.
      expect(parts.reduce((area, p) => area + p.width! * p.height!, 0), design.id).toBe(made.width * made.height);
      for (const p of parts) {
        expect(p.style.backgroundOpacity, design.id).toBe(100);
        expect([p.x, p.y].every((v) => v >= 0 && v <= 1), design.id).toBe(true);
        for (const r of p.readings) expect(rich.some((s) => s.id === r.source), `${design.id} ${r.source}`).toBe(true);
        if (p.layout === 'tiles') for (const r of p.readings) expect(r.colSpan ?? 1, design.id).toBeLessThanOrEqual(p.columns ?? 2);
        expect(p.columns ?? 2, design.id).toBeLessThanOrEqual(6);
        expect(BACKDROPS.some((b) => b.id === (p.style.backdrop ?? 'none')), design.id).toBe(true);
      }

      // On a portable monitor at 125 %: the whole screen again, text in proportion and within the recorder's limits.
      const fitted = fitScreenDesign(design, rich, { width: 1536, height: 864 });
      expect(fitted.reduce((area, p) => area + p.width! * p.height!, 0), design.id).toBeCloseTo(1536 * 864, -3);
      for (const p of fitted) expect(p.style.size >= 6 && p.style.size <= 72 && p.style.padding <= 40, design.id).toBe(true);
    }

    // Two panels: a header line at the top, the grid under it; half the size, half the text.
    const mission = SCREEN_DESIGNS.find((d) => d.id === 'mission-control')!;
    expect(fitScreenDesign(mission, rich, { width: 1920, height: 1080 }).map((p) => [p.layout, p.y, p.width, p.height])).toEqual([
      ['line', 0, 1920, 96],
      ['tiles', 1, 1920, 984],
    ]);
    expect(fitScreenDesign(mission, rich, { width: 960, height: 540 })[1]!.style.size).toBe(13);
    expect(logicalSize({ width: 3840, height: 2160, scale: 1.5 })).toEqual({ width: 2560, height: 1440 });
    const racing = SCREEN_DESIGNS.find((d) => d.id === 'racing-strip')!;
    expect(shapeDistance(racing, { width: 1920, height: 480 })).toBe(0);
    expect(shapeDistance(racing, { width: 480, height: 480 })).toBeGreaterThan(1);
  });

  it('draws a picture and a pattern behind the readings, as the recorder does', () => {
    for (const p of PATTERNS) {
      const tile = patternTile(p.id, 12);
      expect(tile.width, p.id).toBeGreaterThan(0);
      expect(patternSvg(p.id, 12, '#FFFFFF', 0.5), p.id).toMatch(/^url\("data:image\/svg\+xml,/);
    }

    const panel: OverlayPanel = {
      id: 'p',
      title: 'Pictured',
      x: 0,
      y: 0,
      layout: 'rows',
      readings: [{ source: 'cpu.usage' }],
      style: { ...DEFAULT_STYLE, image: '0123456789abcdef.jpg', imageBlur: 4, imageTint: '#000000', imageTintOpacity: 40, pattern: 'hexagons', patternOpacity: 20 },
    };
    const { container } = render(<PanelPreview panel={panel} sources={new Map(sources.map((s) => [s.id, s]))} scale={1} />);
    const picture = container.querySelector('[data-image] > span') as HTMLElement;
    expect(picture.style.backgroundImage).toContain('/api/v1/overlay/images/0123456789abcdef.jpg');
    expect(picture.style.filter).toContain('blur(4px)');
    expect(container.querySelector('[data-pattern="hexagons"]')).not.toBeNull();
  });
});

describe('On-screen readings page', () => {
  beforeEach(() => {
    FakeEventSource.instances = [];
    vi.stubGlobal('EventSource', FakeEventSource);
  });

  afterEach(() => vi.unstubAllGlobals());

  it('shows the screens, adds a panel to the chosen one, saves it and identifies the screens', async () => {
    let saved: OverlayConfig = {
      version: 1,
      enabled: true,
      hotkey: 'Ctrl+Alt+Shift+O',
      intervalMs: 1000,
      hideFromCapture: false,
      screens: [{ key: 'AUS32F4/1', name: 'PG32UCDP', panels: [{ id: 'panel-1', title: 'System', x: 1, y: 0, layout: 'rows', readings: [{ source: 'cpu.usage', bar: true }], style: DEFAULT_STYLE }] }],
    };
    const fetchMock = mockApi({
      'GET /api/v1/status': () => statusResponse(),
      'GET /api/v1/overlay': (): OverlayResponse => ({ config: saved, state, fileProblem: null, recorderProblem: null, presets }),
      'GET /api/v1/overlay/state': () => state,
      'PUT /api/v1/overlay': (_url, init) => {
        saved = JSON.parse(String(init!.body)) as OverlayConfig;
        return { config: saved, state: null, fileProblem: null, recorderProblem: null };
      },
      'POST /api/v1/overlay/identify': () => undefined,
    });
    renderApp('/on-screen');

    await openPart(/^Screens/);
    const map = await screen.findByRole('group', { name: 'Your screens' });
    expect(within(map).getByRole('button', { name: 'Screen 1: PG32UCDP, 1 panel' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByText('On 1 screen')).toBeInTheDocument();

    // The second screen has its own readings.
    await userEvent.click(within(map).getByRole('button', { name: 'Screen 2: VG279QM, 0 panels' }));
    expect(await screen.findByText('No readings on this screen yet: add a panel.')).toBeInTheDocument();
    await userEvent.click(await screen.findByRole('button', { name: 'Add a panel' }));
    await userEvent.click(screen.getByRole('menuitem', { name: /^Network/ }));
    const editor = screen.getByRole('group', { name: 'Screen 2: VG279QM' });
    expect(within(editor).getByText('19.5')).toBeInTheDocument();

    // Style it from the inspector's themes (each previews this panel); it is saved shortly after.
    await userEvent.click(await screen.findByRole('tab', { name: 'Look' }));
    const looks = screen.getByRole('group', { name: 'Ready-made looks' });
    expect(within(looks).getByRole('button', { name: 'Aurora' })).toHaveAttribute('aria-pressed', 'true');
    await userEvent.click(within(looks).getByRole('button', { name: 'Neon' }));
    await saveChanges();
    await waitFor(() => expect(saved.screens).toHaveLength(2), { timeout: 3000 });
    const second = saved.screens!.find((s) => s.key === 'AUS2721/2')!;
    expect(second.panels[0]).toMatchObject({ title: 'Network', layout: 'line', readings: [{ source: 'net.n1.down', visual: 'graph' }, { source: 'net.n1.up', visual: 'graph' }], style: { font: 'Consolas', glow: 70 } });

    // Every panel, on any screen, in one list that groups and sorts.
    await openPart(/^All panels/);
    const all = await screen.findByRole('table', { name: 'All panels' });
    expect(within(all).getAllByText('System').length).toBeGreaterThan(0);
    expect(within(all).getAllByText('Network').length).toBeGreaterThan(0);

    await userEvent.click(screen.getByRole('button', { name: 'Identify' }));
    await waitFor(() => expect(fetchMock.mock.calls.some(([url, init]) => String(url).endsWith('/overlay/identify') && init?.method === 'POST')).toBe(true));
    expect(bodyOf(fetchMock, 'PUT', '/api/v1/overlay')).toMatchObject({ enabled: true });
  });

  it('moves a panel with the arrow keys and adds a reading from the grouped list', async () => {
    let saved: OverlayConfig = {
      version: 1,
      enabled: false,
      hotkey: 'Ctrl+Alt+Shift+O',
      intervalMs: 1000,
      hideFromCapture: false,
      screens: [{ key: 'AUS32F4/1', name: 'PG32UCDP', panels: [{ id: 'panel-1', title: 'System', x: 0.5, y: 0.5, layout: 'rows', readings: [], style: DEFAULT_STYLE }] }],
    };
    mockApi({
      'GET /api/v1/status': () => statusResponse(),
      'GET /api/v1/overlay': (): OverlayResponse => ({ config: saved, state, fileProblem: null, recorderProblem: null, presets }),
      'GET /api/v1/overlay/state': () => state,
      'PUT /api/v1/overlay': (_url, init) => {
        saved = JSON.parse(String(init!.body)) as OverlayConfig;
        return { config: saved, state: null, fileProblem: null, recorderProblem: null };
      },
    });
    renderApp('/on-screen');

    const panel = await screen.findByRole('button', { name: /^System \(0 readings\) on PG32UCDP/ });
    panel.focus();
    await userEvent.keyboard('{Shift>}{ArrowRight}{/Shift}{ArrowUp}');
    await userEvent.click(await screen.findByRole('button', { name: 'Add a reading' }));
    await userEvent.click(within(screen.getByRole('region', { name: 'Add a reading' })).getByRole('button', { name: /CMOS Battery/ }));
    await saveChanges();
    await waitFor(() => expect(saved.screens![0]!.panels[0]!.readings).toEqual([{ source: 'sensor.lhm/lpc/nct6701d/0/voltage/8' }]), { timeout: 3000 });
    expect(saved.screens![0]!.panels[0]!.x).toBeCloseTo(0.6, 5);
    expect(saved.screens![0]!.panels[0]!.y).toBeCloseTo(0.49, 5);
    expect(screen.getByText('Off')).toBeInTheDocument();
  });

  it('shows a USB screen under the monitors, uses it when asked, and approves a screen driver', async () => {
    const usb: OverlayDisplay = { key: 'usb:1cbe:a088:34314314969ec304', name: 'Lian Li Universal Screen 8.8″', number: 2, x: 0, y: 0, width: 1920, height: 480, scale: 1, primary: false, refreshHz: 30, kind: 'device', status: 'busy', detail: 'L-Connect is using it.', model: 'Lian Li Universal Screen 8.8″' };
    const live: OverlayState = { ...state, displays: [...state.displays, usb] };
    let saved: OverlayConfig = { version: 1, enabled: true, hotkey: 'Ctrl+Alt+Shift+O', intervalMs: 1000, hideFromCapture: false, screens: [] };
    let drivers: ScreenDriverInfo[] = [
      { id: 'lianli-turzx', name: 'Lian Li screens', version: '1.0', publisher: 'PC Diagnostic', builtIn: true, sha256: null, approved: true, loaded: true, problem: null, models: ['Lian Li Universal Screen 8.8″'] },
      { id: 'my-screen', name: 'My screen', version: '1.0', publisher: 'Me', builtIn: false, sha256: 'a'.repeat(64), approved: false, loaded: false, problem: 'Waiting for your approval.', models: ['My 5″ screen'] },
    ];
    const approvals: unknown[] = [];
    mockApi({
      'GET /api/v1/status': () => statusResponse(),
      'GET /api/v1/overlay': (): OverlayResponse => ({ config: saved, state: live, fileProblem: null, recorderProblem: null, presets }),
      'GET /api/v1/overlay/state': () => live,
      'PUT /api/v1/overlay': (_url, init) => {
        saved = JSON.parse(String(init!.body)) as OverlayConfig;
        return { config: saved, state: null, fileProblem: null, recorderProblem: null };
      },
      'GET /api/v1/drivers': () => drivers,
      'POST /api/v1/drivers/my-screen/approve': (_url, init) => {
        approvals.push(JSON.parse(String(init!.body)));
        drivers = drivers.map((d) => (d.id === 'my-screen' ? { ...d, approved: true, loaded: true, problem: null } : d));
        return drivers;
      },
    });
    renderApp('/on-screen');

    // Under the monitors, at its own shape, with who holds it.
    await openPart(/^Screens/);
    const row = await screen.findByRole('group', { name: 'USB screens' });
    const card = within(row).getByRole('button', { name: /USB screen 2: Lian Li Universal Screen 8.8″, In use by another program/ });
    expect(within(row).getByText('L-Connect is using it.')).toBeInTheDocument();
    await userEvent.click(card);

    // Its settings over its editor; using it and turning it are saved with the layout.
    const settings = await screen.findByRole('group', { name: 'USB screen settings' });
    expect(screen.getByText(/USB screen 2: Lian Li Universal Screen 8.8″/, { selector: '[class*=font-semibold], h2, h3, div' })).toBeInTheDocument();
    await userEvent.click(within(settings).getByRole('switch', { name: 'Show my panels on this screen' }));
    await userEvent.click(within(within(settings).getByRole('radiogroup', { name: 'Turned' })).getByRole('radio', { name: '270°' }));
    await saveChanges();
    await waitFor(() => expect(saved.devices).toEqual([{ key: usb.key, use: true, rotation: 270, zoom: 100, brightness: 100, background: '#000000' }]), { timeout: 4000 });

    // A driver package waiting for approval is approved by its content.
    await openPart(/^Screen drivers/);
    const list = await screen.findByRole('list', { name: 'Driver packages' });
    expect(within(list).getByText('Built in')).toBeInTheDocument();
    expect(within(list).getByText('Waiting for your approval.')).toBeInTheDocument();
    await userEvent.click(within(list).getByRole('button', { name: 'Approve' }));
    // Approving asks first, naming who made it.
    expect(await screen.findByRole('dialog', { name: 'Approve My screen?' })).toHaveTextContent('From Me.');
    await confirmIt();
    await waitFor(() => expect(approvals).toEqual([{ id: 'my-screen', sha256: 'a'.repeat(64), approve: true }]));
    expect(await within(list).findByText('Approved')).toBeInTheDocument();
  });

  it('controls a panel from its control box, makes a layout for upright, arranges all and lets a panel be moved on the screen', async () => {
    let saved: OverlayConfig = {
      version: 1,
      enabled: true,
      hotkey: 'Ctrl+Alt+Shift+O',
      intervalMs: 1000,
      hideFromCapture: false,
      screens: [{ key: 'AUS2721/2', name: 'VG279QM', panels: [{ id: 'panel-1', title: 'System', x: 1, y: 0, layout: 'rows', readings: [{ source: 'cpu.usage' }], style: DEFAULT_STYLE }, { id: 'panel-2', title: 'Clock', x: 0, y: 0, layout: 'line', readings: [{ source: 'time.clock' }], style: DEFAULT_STYLE }] }],
    };
    mockApi({
      'GET /api/v1/status': () => statusResponse(),
      'GET /api/v1/overlay': (): OverlayResponse => ({ config: saved, state, fileProblem: null, recorderProblem: null, presets }),
      'GET /api/v1/overlay/state': () => state,
      'PUT /api/v1/overlay': (_url, init) => {
        saved = JSON.parse(String(init!.body)) as OverlayConfig;
        return { config: saved, state: null, fileProblem: null, recorderProblem: null };
      },
    });
    renderApp('/on-screen');

    await openPart(/^Screens/);
    await userEvent.click(within(await screen.findByRole('group', { name: 'Your screens' })).getByRole('button', { name: /^Screen 2: VG279QM/ }));
    await userEvent.click(await screen.findByRole('button', { name: /^System \(1 reading\) on VG279QM/ }));

    // The control box: fill the 1920 × 1080 screen, then a grid cell, then back.
    const box = screen.getByRole('toolbar', { name: 'Controls of System' });
    await userEvent.click(within(box).getByRole('button', { name: 'Fill the screen' }));
    await saveChanges();
    await waitFor(() => expect(saved.screens![0]!.panels[0]).toMatchObject({ x: 0, y: 0, width: 1920, height: 1080 }), { timeout: 3000 });
    await userEvent.click(within(screen.getByRole('toolbar', { name: 'Controls of System' })).getByRole('button', { name: 'Put in a grid cell' }));
    await userEvent.click(screen.getByRole('button', { name: 'Four: cell 2' }));
    await saveChanges();
    await waitFor(() => expect(saved.screens![0]!.panels[0]).toMatchObject({ x: 1, y: 0, width: 960, height: 540 }), { timeout: 3000 });
    await userEvent.click(within(screen.getByRole('toolbar', { name: 'Controls of System' })).getByRole('button', { name: 'Restore size and place' }));
    await saveChanges();
    await waitFor(() => expect(saved.screens![0]!.panels[0]).toMatchObject({ x: 1, y: 0, width: null, height: null, restore: null }), { timeout: 3000 });

    // Filled, then moved by hand: that is its place now, and Restore has nothing older to go back to.
    await userEvent.click(within(screen.getByRole('toolbar', { name: 'Controls of System' })).getByRole('button', { name: 'Fill the screen' }));
    await saveChanges();
    await waitFor(() => expect(saved.screens![0]!.panels[0]!.restore).not.toBeNull(), { timeout: 3000 });
    screen.getByRole('button', { name: /^System \(1 reading\) on VG279QM/ }).focus();
    await userEvent.keyboard('{ArrowLeft}');
    await saveChanges();
    await waitFor(() => expect(saved.screens![0]!.panels[0]!.restore).toBeNull(), { timeout: 3000 });
    expect(within(screen.getByRole('toolbar', { name: 'Controls of System' })).getByRole('button', { name: 'Restore size and place' })).toBeDisabled();
    await userEvent.click(within(screen.getByRole('toolbar', { name: 'Controls of System' })).getByRole('button', { name: 'Restore size and place' }));

    // Moving it on the screen itself is off until asked for.
    await userEvent.click(screen.getByRole('tab', { name: 'Layout' }));
    const onScreen = screen.getByRole('group', { name: 'On the screen itself' });
    expect(within(onScreen).getByRole('checkbox', { name: 'Move it with the mouse' })).not.toBeChecked();
    await userEvent.click(within(onScreen).getByRole('checkbox', { name: 'Move it with the mouse' }));
    await saveChanges();
    await waitFor(() => expect(saved.screens![0]!.panels[0]!.movable).toBe(true), { timeout: 3000 });

    // Arrange all: the two panels side by side.
    await userEvent.click(screen.getByRole('button', { name: 'Arrange all panels' }));
    await userEvent.click(screen.getByRole('menuitem', { name: /^Two side by side/ }));
    await saveChanges();
    await waitFor(() => expect(saved.screens![0]!.panels.map((p) => [p.x, p.width])).toEqual([[0, 960], [1, 960]]), { timeout: 3000 });

    // Upright: the same panels until a layout of its own is made (copies with new ids), shown as the upright one.
    const orientation = screen.getByRole('radiogroup', { name: 'Orientation' });
    expect(within(orientation).getByRole('radio', { name: /Wide/ })).toHaveAttribute('aria-checked', 'true');
    await userEvent.click(within(orientation).getByRole('radio', { name: /Upright/ }));
    expect(screen.getByText(/Upright, VG279QM shows the same panels as when it is wide/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Make a layout for upright' }));
    await saveChanges();
    await waitFor(() => expect(saved.screens![0]!.portrait?.map((p) => p.id)).toEqual(['panel-3', 'panel-4']), { timeout: 3000 });
    expect(screen.getByRole('group', { name: 'Screen 2: VG279QM' })).toBeInTheDocument();
  }, 20000);

  it('fills a USB screen with a whole-screen design made for its size, and undoes it', async () => {
    const usb: OverlayDisplay = { key: 'usb:1cbe:a088:34314314969ec304', name: 'Lian Li Universal Screen 8.8″', number: 3, x: 0, y: 0, width: 1920, height: 480, scale: 1, primary: false, refreshHz: 30, kind: 'device', status: 'on', detail: null, model: 'Lian Li Universal Screen 8.8″' };
    const live: OverlayState = { ...state, displays: [...state.displays, usb] };
    const old: OverlayPanel = { id: 'panel-1', title: 'Mine', x: 0, y: 0, layout: 'rows', readings: [{ source: 'cpu.usage' }], style: DEFAULT_STYLE };
    let saved: OverlayConfig = { version: 1, enabled: true, hotkey: 'Ctrl+Alt+Shift+O', intervalMs: 1000, hideFromCapture: false, screens: [{ key: usb.key, name: usb.name, panels: [old] }] };
    mockApi({
      'GET /api/v1/status': () => statusResponse(),
      'GET /api/v1/overlay': (): OverlayResponse => ({ config: saved, state: live, fileProblem: null, recorderProblem: null, presets }),
      'GET /api/v1/overlay/state': () => live,
      'PUT /api/v1/overlay': (_url, init) => {
        saved = JSON.parse(String(init!.body)) as OverlayConfig;
        return { config: saved, state: null, fileProblem: null, recorderProblem: null };
      },
      'GET /api/v1/drivers': () => [],
    });
    renderApp('/on-screen');

    await openPart(/^Screens/);
    await userEvent.click(within(await screen.findByRole('group', { name: 'USB screens' })).getByRole('button', { name: /USB screen 3/ }));
    await userEvent.click(await screen.findByRole('button', { name: 'Designs' }));

    // A USB screen opens on the whole-screen designs, those made for its shape first.
    const dialog = await screen.findByRole('dialog', { name: 'Designs' });
    expect(within(dialog).getByRole('tab', { name: /Whole screen/ })).toHaveAttribute('aria-selected', 'true');
    const list = within(dialog).getByRole('list', { name: 'Whole-screen designs' });
    expect(within(within(list).getAllByRole('listitem')[0]!).getByText(/Made for 8\.8″ bar, 1920 × 480$/)).toBeInTheDocument();
    expect(within(dialog).getByText(/replaces the 1 panel on it/)).toBeInTheDocument();
    const madeFor = within(dialog).getByRole('group', { name: 'Made for' });
    await userEvent.click(within(madeFor).getByRole('button', { name: '3.5″ portrait' }));
    expect(within(list).getAllByRole('listitem')).toHaveLength(3);
    expect(within(list).getAllByText(/another shape, stretched on this screen/)).toHaveLength(3);
    await userEvent.click(within(madeFor).getByRole('button', { name: 'Any size' }));

    await userEvent.click(within(list).getByRole('button', { name: 'Use Racing Strip on this screen' }));
    await saveChanges();
    await waitFor(() => expect(saved.screens![0]!.panels).toEqual([expect.objectContaining({ layout: 'tiles', columns: 6, x: 0, y: 0, width: 1920, height: 480 })]), { timeout: 3000 });

    // It can be undone: the screen's own panels come back.
    expect(screen.getByText((_, el) => el?.tagName === 'SPAN' && /^Racing Strip now fills Lian Li Universal Screen 8\.8″ in place of its 1 panel\.$/.test(el.textContent ?? ''))).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Undo' }));
    await saveChanges();
    await waitFor(() => expect(saved.screens![0]!.panels).toEqual([old]), { timeout: 3000 });
  });

  it('arranges the panels on the screens and shows what was changed there', async () => {
    let saved: OverlayConfig = {
      version: 1,
      enabled: true,
      hotkey: 'Ctrl+Alt+Shift+O',
      intervalMs: 1000,
      hideFromCapture: false,
      screens: [{ key: 'AUS32F4/1', name: 'PG32UCDP', panels: [{ id: 'hud', title: 'HUD', x: 1, y: 0, layout: 'rows', readings: [{ source: 'cpu.usage' }], style: presets.aurora! }] }],
    };
    let revision = 'r1';
    let arranging = false;
    const arranged: boolean[] = [];
    mockApi({
      'GET /api/v1/status': () => statusResponse(),
      'GET /api/v1/overlay': (): OverlayResponse => ({ config: saved, state, fileProblem: null, recorderProblem: null, presets, revision }),
      'GET /api/v1/overlay/state': () => ({ ...state, arranging, revision }),
      'POST /api/v1/overlay/arrange': (_url, init) => {
        arranging = (JSON.parse(String(init!.body)) as { on: boolean }).on;
        arranged.push(arranging);
        return { ...state, arranging, revision };
      },
    });
    renderApp('/on-screen');

    await userEvent.click(await screen.findByRole('button', { name: /Arrange on the screens/ }));
    await waitFor(() => expect(arranged).toEqual([true]));
    expect(await screen.findByText(/Arranging on the screens\./)).toBeInTheDocument();

    // Moved and retitled on the screens: the recorder saved it; the page loads it.
    saved = { ...saved, screens: [{ ...saved.screens![0]!, panels: [{ ...saved.screens![0]!.panels[0]!, title: 'Moved on screen', x: 0.25 }] }] };
    revision = 'r2';
    expect(await screen.findByRole('button', { name: /^Moved on screen \(1 reading\) on PG32UCDP/ }, { timeout: 4000 })).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: /Done arranging/ }));
    await waitFor(() => expect(arranged).toEqual([true, false]));
  });
});
