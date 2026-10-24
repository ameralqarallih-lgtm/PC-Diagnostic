import { WIDGETS } from './widgets';
import { COLUMNS, isWidgetType, type Breakpoint, type DashboardConfig, type LayoutEntry, type WidgetInstance, type WidgetType } from './types';

/**
 * The layout model: widgets are kept in reading order, each with a share of the 12-column grid (its span) and a
 * height in rows. Positions are never stored for small screens: they are computed for the space available, so
 * widgets wrap to the next row (and grow taller where their content needs it) when the window narrows.
 * `layouts.lg` holds the spans and heights (with the wide-screen positions for reference).
 */

/** Gap between widgets and the grid's row height, in pixels (must match the grid). */
export const GRID_MARGIN = 16;
export const GRID_ROW_HEIGHT = 30;

/** Quick widths: a quarter, a third, a half or the full width of the 12-column grid. */
export const QUICK_SPANS = [
  { span: 3, label: '¼', name: 'quarter width' },
  { span: 4, label: '⅓', name: 'third width' },
  { span: 6, label: '½', name: 'half width' },
  { span: 12, label: 'Full', name: 'full width' },
] as const;

/** A new widget id: the type plus a short random suffix. */
export function newWidgetId(type: WidgetType, taken: Iterable<string> = []): string {
  const used = new Set(taken);
  for (;;) {
    const id = `${type}-${Math.random().toString(36).slice(2, 7)}`;
    if (!used.has(id)) return id;
  }
}

export function newWidget(type: WidgetType, taken: Iterable<string> = []): WidgetInstance {
  return { id: newWidgetId(type, taken), type, settings: structuredClone(WIDGETS[type].defaults) };
}

/** Width of one column for a grid this wide. */
export function columnWidth(gridWidth: number, columns: number): number {
  return Math.max(1, (gridWidth - GRID_MARGIN * (columns - 1)) / columns);
}

/** Pixel width of a widget spanning <paramref name="w"/> columns. */
function pixelWidth(w: number, colWidth: number): number {
  return w * colWidth + (w - 1) * GRID_MARGIN;
}

/** Rows needed for a given pixel height. */
function rowsFor(px: number): number {
  return Math.ceil((px + GRID_MARGIN) / (GRID_ROW_HEIGHT + GRID_MARGIN));
}

/** The height a widget needs at a given width (some widgets wrap their content when narrow). */
function heightFor(widget: WidgetInstance, h: number, widthPx: number, columns: number): number {
  const definition = WIDGETS[widget.type];
  if (columns === 1) return Math.max(h, definition.narrowHeight);
  if (widget.type === 'gauges') {
    // Matches the gauge grid's container queries: 8 per row from 64rem, 4 from 28rem, else 2.
    const metrics = Array.isArray(widget.settings.metrics) ? widget.settings.metrics.length : 8;
    const perRow = widthPx >= 1024 ? 8 : widthPx >= 448 ? 4 : 2;
    const rows = Math.max(1, Math.ceil(metrics / perRow));
    return Math.max(h, rowsFor(rows * 150 + (rows - 1) * 12));
  }
  if (widget.type === 'capture') {
    // The quick-type buttons sit beside the main button from about 1100 px; narrower, they wrap below it.
    const rows = widthPx < 420 ? 11 : widthPx < 720 ? 9 : widthPx < 1100 ? 7 : 0;
    return Math.max(h, rows);
  }
  return h;
}

/**
 * Positions for a grid with <paramref name="columns"/> columns, <paramref name="gridWidth"/> pixels wide: widgets in
 * order, each with its share of the width (never narrower than it can show), wrapping when a row is full.
 */
export function flowLayout(widgets: WidgetInstance[], wide: LayoutEntry[], columns: number, gridWidth: number): LayoutEntry[] {
  const sizes = new Map(wide.map((e) => [e.i, e]));
  const colWidth = columnWidth(gridWidth, columns);
  const result: LayoutEntry[] = [];
  let x = 0;
  let y = 0;
  let rowHeight = 0;
  for (const widget of widgets) {
    const definition = WIDGETS[widget.type];
    const size = sizes.get(widget.id) ?? { w: definition.size.w, h: definition.size.h };
    const share = columns === 12 ? size.w : Math.round((size.w * columns) / 12);
    const minimum = Math.ceil((definition.minWidthPx + GRID_MARGIN) / (colWidth + GRID_MARGIN));
    const w = Math.min(columns, Math.max(1, share, minimum));
    const h = heightFor(widget, size.h, pixelWidth(w, colWidth), columns);
    if (x + w > columns) {
      x = 0;
      y += rowHeight;
      rowHeight = 0;
    }
    result.push({ i: widget.id, x, y, w, h });
    x += w;
    rowHeight = Math.max(rowHeight, h);
  }

  // On narrower grids, a row left part-empty by wrapping is filled by its last widget (no ragged gaps).
  // On the full 12-column grid the widths stay exactly as the user chose them.
  if (columns < 12) {
    const rows = new Map<number, LayoutEntry[]>();
    for (const entry of result) rows.set(entry.y, [...(rows.get(entry.y) ?? []), entry]);
    for (const row of rows.values()) {
      const last = row[row.length - 1]!;
      last.w = columns - last.x;
    }
  }
  return result;
}

/** Wide-screen reference width used for the stored positions. */
const WIDE_WIDTH = 1280;

/** Recomputes the stored wide-screen positions after a change of order or size. */
function reflow(config: DashboardConfig): DashboardConfig {
  const wide = config.layouts.lg ?? [];
  const sizes = new Map(wide.map((e) => [e.i, e]));
  const entries = config.widgets.map((w) => {
    const size = sizes.get(w.id) ?? { w: WIDGETS[w.type].size.w, h: WIDGETS[w.type].size.h };
    return { i: w.id, x: 0, y: 0, w: size.w, h: size.h };
  });
  const positions = flowLayout(config.widgets, entries, 12, WIDE_WIDTH);
  return { ...config, layouts: { lg: entries.map((e, index) => ({ ...e, x: positions[index]!.x, y: positions[index]!.y })) } };
}

/** Adds a widget at the end. */
export function addWidget(config: DashboardConfig, widget: WidgetInstance): DashboardConfig {
  const size = WIDGETS[widget.type].size;
  return reflow({
    ...config,
    widgets: [...config.widgets, widget],
    layouts: { lg: [...(config.layouts.lg ?? []), { i: widget.id, x: 0, y: 0, w: size.w, h: size.h }] },
  });
}

export function removeWidget(config: DashboardConfig, id: string): DashboardConfig {
  return reflow({ ...config, widgets: config.widgets.filter((w) => w.id !== id), layouts: { lg: (config.layouts.lg ?? []).filter((e) => e.i !== id) } });
}

/** A copy of a widget (same settings and size) right after it. */
export function duplicateWidget(config: DashboardConfig, id: string): DashboardConfig {
  const index = config.widgets.findIndex((w) => w.id === id);
  const source = config.widgets[index];
  if (!source) return config;
  const copy: WidgetInstance = { ...structuredClone(source), id: newWidgetId(source.type, config.widgets.map((w) => w.id)) };
  const size = (config.layouts.lg ?? []).find((e) => e.i === id) ?? { w: WIDGETS[source.type].size.w, h: WIDGETS[source.type].size.h };
  const widgets = [...config.widgets.slice(0, index + 1), copy, ...config.widgets.slice(index + 1)];
  return reflow({ ...config, widgets, layouts: { lg: [...(config.layouts.lg ?? []), { i: copy.id, x: 0, y: 0, w: size.w, h: size.h }] } });
}

/** Moves a widget one place earlier or later (keyboard alternative to dragging). */
export function moveWidget(config: DashboardConfig, id: string, direction: -1 | 1): DashboardConfig {
  const index = config.widgets.findIndex((w) => w.id === id);
  const target = index + direction;
  if (index < 0 || target < 0 || target >= config.widgets.length) return config;
  const widgets = [...config.widgets];
  [widgets[index], widgets[target]] = [widgets[target]!, widgets[index]!];
  return reflow({ ...config, widgets });
}

/** Puts widgets in the reading order of an arranged grid (after a drag). */
export function reorderFromLayout(config: DashboardConfig, arranged: readonly { i: string; x: number; y: number }[]): DashboardConfig {
  const order = [...arranged].sort((a, b) => a.y - b.y || a.x - b.x).map((e) => e.i);
  const rank = new Map(order.map((id, index) => [id, index]));
  const widgets = [...config.widgets].sort((a, b) => (rank.get(a.id) ?? 1e6) - (rank.get(b.id) ?? 1e6));
  return reflow({ ...config, widgets });
}

/** Sets a widget's share of the 12 columns and its height (null keeps the height). */
export function resizeWidget(config: DashboardConfig, id: string, span: number, h: number | null): DashboardConfig {
  const widget = config.widgets.find((w) => w.id === id);
  if (!widget) return config;
  const definition = WIDGETS[widget.type];
  const w = Math.min(12, Math.max(definition.size.minW, Math.round(span)));
  const lg = (config.layouts.lg ?? []).map((e) => (e.i === id ? { ...e, w, h: h == null ? e.h : Math.max(definition.size.minH, Math.round(h)) } : e));
  return reflow({ ...config, layouts: { lg } });
}

/** Converts a width on a narrower grid into a share of 12 columns. */
export function spanFromColumns(w: number, columns: number): number {
  return columns >= 12 ? w : Math.round((w * 12) / columns);
}

/** The layouts the grid shows for each screen size at the current width. */
export function layoutsFor(config: DashboardConfig, gridWidth: number): Record<Breakpoint, LayoutEntry[]> {
  const wide = config.layouts.lg ?? [];
  return {
    lg: flowLayout(config.widgets, wide, COLUMNS.lg, gridWidth),
    md: flowLayout(config.widgets, wide, COLUMNS.md, gridWidth),
    sm: flowLayout(config.widgets, wide, COLUMNS.sm, gridWidth),
    xs: flowLayout(config.widgets, wide, COLUMNS.xs, gridWidth),
  };
}

/**
 * Makes a stored (or default) dashboard safe to render: unknown widgets are dropped, settings get defaults for
 * anything missing, widgets are put in the stored reading order, and every widget has a size.
 */
export function normalize(input: unknown, fallback: DashboardConfig): DashboardConfig {
  if (!input || typeof input !== 'object' || (input as { version?: unknown }).version !== 1) return fallback;
  const raw = input as { widgets?: unknown; layouts?: unknown };
  const seen = new Set<string>();
  const widgets: WidgetInstance[] = [];
  for (const item of Array.isArray(raw.widgets) ? raw.widgets : []) {
    if (!item || typeof item !== 'object') continue;
    const { id, type, title, settings } = item as Record<string, unknown>;
    if (typeof id !== 'string' || seen.has(id) || !isWidgetType(type)) continue;
    seen.add(id);
    widgets.push({
      id,
      type,
      title: typeof title === 'string' && title.trim() ? title : null,
      settings: { ...structuredClone(WIDGETS[type].defaults), ...(settings && typeof settings === 'object' ? (settings as WidgetInstance['settings']) : {}) },
    });
  }

  const rawLayouts = raw.layouts && typeof raw.layouts === 'object' ? (raw.layouts as Record<string, unknown>) : {};
  const stored = (Array.isArray(rawLayouts.lg) ? (rawLayouts.lg as LayoutEntry[]) : []).filter(
    (e) => e && seen.has(e.i) && [e.x, e.y, e.w, e.h].every((n) => Number.isInteger(n) && n >= 0),
  );
  const position = new Map(stored.map((e) => [e.i, e]));
  widgets.sort((a, b) => {
    const pa = position.get(a.id);
    const pb = position.get(b.id);
    return (pa?.y ?? 1e6) - (pb?.y ?? 1e6) || (pa?.x ?? 0) - (pb?.x ?? 0);
  });
  const lg = widgets.map((w) => {
    const entry = position.get(w.id);
    const size = WIDGETS[w.type].size;
    return { i: w.id, x: 0, y: 0, w: Math.min(12, Math.max(1, entry?.w ?? size.w)), h: Math.max(1, entry?.h ?? size.h) };
  });
  const library: WidgetInstance[] = [];
  const libraryIds = new Set<string>();
  for (const item of Array.isArray((raw as { library?: unknown }).library) ? ((raw as { library: unknown[] }).library) : []) {
    if (!item || typeof item !== 'object') continue;
    const { id, type, title, settings } = item as Record<string, unknown>;
    if (typeof id !== 'string' || libraryIds.has(id) || !isWidgetType(type)) continue;
    libraryIds.add(id);
    library.push({
      id,
      type,
      title: typeof title === 'string' && title.trim() ? title : null,
      settings: { ...structuredClone(WIDGETS[type].defaults), ...(settings && typeof settings === 'object' ? (settings as WidgetInstance['settings']) : {}) },
    });
  }
  return reflow({ version: 1, widgets, layouts: { lg }, ...(library.length ? { library } : {}) });
}

/** A dashboard from widgets in reading order and their sizes. */
export function fromWidgets(widgets: WidgetInstance[], sizes: { i: string; w: number; h: number }[]): DashboardConfig {
  return reflow({ version: 1, widgets, layouts: { lg: sizes.map((s) => ({ ...s, x: 0, y: 0 })) } });
}
