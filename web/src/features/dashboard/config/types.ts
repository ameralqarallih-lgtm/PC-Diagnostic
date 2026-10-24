/** The user's dashboard: which widgets, their settings, and where they sit on each screen size. */

export const WIDGET_TYPES = [
  'capture',
  'gauges',
  'chart',
  'metric',
  'recorder',
  'processes',
  'incidents',
  'events',
  'anomalies',
  'sensors',
  'gpu',
  'displays',
  'network',
  'custom',
] as const;

export type WidgetType = (typeof WIDGET_TYPES)[number];

export type SettingValue = string | number | boolean | string[];

export type WidgetSettings = Record<string, SettingValue>;

export interface WidgetInstance {
  /** Lowercase letters, digits and dashes (validated by the server). */
  id: string;
  type: WidgetType;
  /** Overrides the widget's default title. */
  title?: string | null;
  settings: WidgetSettings;
}

export const BREAKPOINTS = { lg: 1100, md: 760, sm: 480, xs: 0 } as const;
export type Breakpoint = keyof typeof BREAKPOINTS;
export const COLUMNS: Record<Breakpoint, number> = { lg: 12, md: 8, sm: 4, xs: 1 };

/** The breakpoint for a grid width. */
export function breakpointFor(width: number): Breakpoint {
  return (Object.entries(BREAKPOINTS) as [Breakpoint, number][]).sort((a, b) => b[1] - a[1]).find(([, min]) => width >= min)?.[0] ?? 'xs';
}

export interface LayoutEntry {
  i: string;
  x: number;
  y: number;
  w: number;
  h: number;
}

export interface DashboardConfig {
  version: 1;
  widgets: WidgetInstance[];
  layouts: Partial<Record<Breakpoint, LayoutEntry[]>>;
  /** Widgets the user built and kept ("My widgets"), to add again. */
  library?: WidgetInstance[];
}

export function isWidgetType(value: unknown): value is WidgetType {
  return typeof value === 'string' && (WIDGET_TYPES as readonly string[]).includes(value);
}
