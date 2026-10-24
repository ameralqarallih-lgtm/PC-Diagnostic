import { describe, expect, it } from 'vitest';
import {
  addWidget,
  columnWidth,
  duplicateWidget,
  flowLayout,
  layoutsFor,
  moveWidget,
  newWidget,
  normalize,
  removeWidget,
  reorderFromLayout,
  resizeWidget,
  spanFromColumns,
} from './layout';
import { defaultDashboard, PRESETS } from './presets';
import { COLUMNS, type DashboardConfig, type LayoutEntry } from './types';
import { WIDGETS } from './widgets';

const ID = /^[a-z0-9][a-z0-9-]{0,39}$/; // what the web host accepts

function at(layout: LayoutEntry[], id: string): LayoutEntry {
  return layout.find((e) => e.i === id)!;
}

describe('dashboard layout', () => {
  it.each(PRESETS.map((p) => [p.id, p]))('preset %s fits every screen size', (_id, preset) => {
    const config = preset.config();

    for (const widget of config.widgets) expect(widget.id).toMatch(ID);
    for (const width of [1900, 1200, 900, 600, 375]) {
      for (const [breakpoint, layout] of Object.entries(layoutsFor(config, width)) as [keyof typeof COLUMNS, LayoutEntry[]][]) {
        expect(layout.map((e) => e.i).sort()).toEqual(config.widgets.map((w) => w.id).sort());
        for (const entry of layout) expect(entry.x + entry.w).toBeLessThanOrEqual(COLUMNS[breakpoint]);
      }
    }
  });

  it('wraps widgets to new rows when the screen is too narrow for them side by side', () => {
    const config = defaultDashboard();
    const wide = flowLayout(config.widgets, config.layouts.lg!, 12, 1600);
    const narrow = flowLayout(config.widgets, config.layouts.lg!, 12, 1000);

    // Chart (8 columns) and recorder (4 columns) share a row on a wide screen.
    expect(at(wide, 'recorder').y).toBe(at(wide, 'chart').y);
    // At 1000 px a third (4 columns, ~316 px) is still wide enough for the recorder (300 px)...
    expect(at(narrow, 'recorder').y).toBe(at(narrow, 'chart').y);
    // ...but on a tablet grid the chart takes the full width and the recorder moves below it.
    const tablet = flowLayout(config.widgets, config.layouts.lg!, 8, 800);
    expect(at(tablet, 'chart').w).toBe(8);
    expect(at(tablet, 'recorder').y).toBeGreaterThan(at(tablet, 'chart').y);
  });

  it('never makes a widget narrower than it can show', () => {
    const config = resizeWidget(defaultDashboard(), 'chart', 3, null);
    const layout = flowLayout(config.widgets, config.layouts.lg!, 12, 1100);

    const pixels = at(layout, 'chart').w * columnWidth(1100, 12) + (at(layout, 'chart').w - 1) * 16;
    expect(pixels).toBeGreaterThanOrEqual(WIDGETS.chart.minWidthPx);
  });

  it('stacks everything in one column on phones, in order, with gauges tall enough for two per row', () => {
    const config = defaultDashboard();
    const phone = flowLayout(config.widgets, config.layouts.lg!, 1, 375);

    expect(phone.every((e) => e.x === 0 && e.w === 1)).toBe(true);
    expect([...phone].sort((a, b) => a.y - b.y).map((e) => e.i).slice(0, 3)).toEqual(['capture', 'gauges', 'chart']);
    expect(at(phone, 'gauges').h).toBeGreaterThanOrEqual(WIDGETS.gauges.narrowHeight);
  });

  it('sets quick widths and converts resizes on smaller grids into a share of 12 columns', () => {
    const config = resizeWidget(defaultDashboard(), 'events', 6, null);

    expect(at(config.layouts.lg!, 'events').w).toBe(6);
    expect(spanFromColumns(4, 8)).toBe(6);
    expect(spanFromColumns(2, 4)).toBe(6);
    expect(at(resizeWidget(config, 'events', 1, 2).layouts.lg!, 'events')).toMatchObject({ w: WIDGETS.events.size.minW, h: WIDGETS.events.size.minH });
  });

  it('adds widgets at the end and removes them', () => {
    const config = defaultDashboard();
    const widget = newWidget('metric', config.widgets.map((w) => w.id));

    const added = addWidget(config, widget);

    expect(widget.id).toMatch(ID);
    expect(added.widgets.at(-1)!.id).toBe(widget.id);
    expect(at(added.layouts.lg!, widget.id).w).toBe(WIDGETS.metric.size.w);
    const removed = removeWidget(added, widget.id);
    expect(removed.widgets).toEqual(config.widgets);
    expect(removed.layouts.lg!.some((e) => e.i === widget.id)).toBe(false);
  });

  it('duplicates a widget right after the original, with its settings and size', () => {
    const config = defaultDashboard();
    const copy = duplicateWidget(config, 'chart');

    const index = copy.widgets.findIndex((w) => w.id === 'chart');
    const duplicate = copy.widgets[index + 1]!;
    expect(duplicate.type).toBe('chart');
    expect(duplicate.id).not.toBe('chart');
    expect(duplicate.settings).toEqual(config.widgets[index]!.settings);
    expect(at(copy.layouts.lg!, duplicate.id).w).toBe(at(config.layouts.lg!, 'chart').w);
  });

  it('reorders by buttons and by the arrangement after a drag', () => {
    const config = defaultDashboard();
    expect(moveWidget(config, 'gauges', -1).widgets.map((w) => w.id).slice(0, 2)).toEqual(['gauges', 'capture']);

    const dragged = reorderFromLayout(config, [
      { i: 'chart', x: 0, y: 0 },
      ...config.widgets.filter((w) => w.id !== 'chart').map((w, n) => ({ i: w.id, x: 0, y: n + 1 })),
    ]);
    expect(dragged.widgets[0]!.id).toBe('chart');
  });

  it('repairs a stored dashboard: unknown widgets go, settings and sizes are filled in, order follows the stored layout', () => {
    const stored = {
      version: 1,
      widgets: [
        { id: 'gauges', type: 'gauges', settings: { metrics: ['cpu'] } },
        { id: 'old', type: 'retired-widget', settings: {} },
        { id: 'chart', type: 'chart' },
      ],
      layouts: { lg: [{ i: 'chart', x: 0, y: 0, w: 8, h: 10 }, { i: 'gauges', x: 0, y: 10, w: 99, h: 5 }, { i: 'old', x: 0, y: 5, w: 4, h: 4 }], xs: [] },
    };

    const config = normalize(stored, defaultDashboard());

    expect(config.widgets.map((w) => w.id)).toEqual(['chart', 'gauges']);
    expect(config.widgets[0]!.settings).toEqual(WIDGETS.chart.defaults);
    expect(at(config.layouts.lg!, 'gauges').w).toBe(12);
    expect(Object.keys(config.layouts)).toEqual(['lg']);
  });

  it('falls back to the default for anything that is not a version 1 dashboard', () => {
    const fallback: DashboardConfig = defaultDashboard();
    expect(normalize(null, fallback)).toBe(fallback);
    expect(normalize({ version: 2 }, fallback)).toBe(fallback);
  });
});
