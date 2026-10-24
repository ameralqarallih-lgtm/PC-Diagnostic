/**
 * How each kind of reading is drawn, so a glance tells what a value is: usage is a segmented load meter in cool
 * blues, capacity a violet fill ring, throughput a teal-to-indigo speedometer, and temperature a thermometer on a
 * heat scale from cool blue to red.
 */
export type ReadingKind = 'load' | 'capacity' | 'throughput' | 'temperature';

export type ReadingLevel = 'normal' | 'warning' | 'critical' | 'unavailable';

/** Gradient (start → end) for the non-temperature kinds. */
export const READING_PALETTES: Record<Exclude<ReadingKind, 'temperature'>, { from: string; to: string }> = {
  load: { from: '#22d3ee', to: '#3b82f6' },
  capacity: { from: '#e879f9', to: '#7c3aed' },
  throughput: { from: '#2dd4bf', to: '#6366f1' },
};

/** Lowest temperature on thermometer scales. */
export const THERMOMETER_MIN = 20;

interface HeatStop {
  at: number;
  color: string;
}

/**
 * The heat scale for a sensor, anchored on its own limits: comfortably below the warning limit is blue and green,
 * the warning limit is yellow, between the limits orange, and the critical limit red.
 */
export function heatStops(warning: number, critical: number): HeatStop[] {
  return [
    { at: warning - 40, color: '#38bdf8' },
    { at: warning - 20, color: '#22c55e' },
    { at: warning, color: '#eab308' },
    { at: (warning + critical) / 2, color: '#f97316' },
    { at: critical, color: '#ef4444' },
  ];
}

function hex(color: string): [number, number, number] {
  const n = Number.parseInt(color.slice(1), 16);
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}

function mix(a: string, b: string, t: number): string {
  const [ar, ag, ab] = hex(a);
  const [br, bg, bb] = hex(b);
  const c = (x: number, y: number) => Math.round(x + (y - x) * t).toString(16).padStart(2, '0');
  return `#${c(ar, br)}${c(ag, bg)}${c(ab, bb)}`;
}

/** The heat color of a temperature on a sensor's scale. */
export function heatColor(celsius: number, warning: number, critical: number): string {
  const stops = heatStops(warning, critical);
  const first = stops[0]!;
  const last = stops[stops.length - 1]!;
  if (celsius <= first.at) return first.color;
  if (celsius >= last.at) return last.color;
  for (let i = 1; i < stops.length; i++) {
    const lo = stops[i - 1]!;
    const hi = stops[i]!;
    if (celsius <= hi.at) return mix(lo.color, hi.color, (celsius - lo.at) / (hi.at - lo.at));
  }
  return last.color;
}

/** Gradient stops (offset 0–1) of a sensor's heat scale across [min, max]. */
export function heatGradient(min: number, max: number, warning: number, critical: number): { offset: number; color: string }[] {
  return heatStops(warning, critical)
    .map((s) => ({ offset: (s.at - min) / (max - min), color: s.color }))
    .filter((s) => s.offset > -0.25 && s.offset < 1.25)
    .map((s) => ({ offset: Math.min(1, Math.max(0, s.offset)), color: s.color }));
}

/** The level of a value against its limits. */
export function levelOf(value: number | null, warning: number, critical: number): ReadingLevel {
  if (value == null) return 'unavailable';
  if (value >= critical) return 'critical';
  if (value >= warning) return 'warning';
  return 'normal';
}

/** Typical limits for temperature sensors (°C). */
export const TEMPERATURE_LIMITS = {
  cpu: { warning: 80, critical: 92 },
  gpu: { warning: 80, critical: 88 },
  hotspot: { warning: 90, critical: 100 },
  disk: { warning: 55, critical: 65 },
} as const;
