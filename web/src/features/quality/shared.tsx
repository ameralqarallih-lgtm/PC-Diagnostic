import { Badge } from '@/components/ui/badge';
import type { HistoryResult, QualityFinding, SeriesInfo } from '@/lib/api/types';

/** One line of a quality chart: what it reads from each row. */
export interface ChartLine<T> {
  id: string;
  label: string;
  unit: string;
  value: (row: T) => number | null | undefined;
  /** The lowest and highest in the row's span, for the band (defaults to the value). */
  low?: (row: T) => number | null | undefined;
  high?: (row: T) => number | null | undefined;
}

/** Rows as the history chart takes them: one bucket per row, [time, min, average, max]. */
export function toChart<T>(rows: T[], at: (row: T) => string, lines: ChartLine<T>[], group: string): { result: HistoryResult; catalog: SeriesInfo[] } {
  const times = rows.map((r) => Date.parse(at(r)));
  const from = times.length > 0 ? Math.min(...times) : Date.now() - 60_000;
  const to = times.length > 0 ? Math.max(...times) : Date.now();
  const bucketMs = times.length > 1 ? Math.max(1000, Math.round((to - from) / (times.length - 1))) : 60_000;
  const series = lines.map((line) => ({
    id: line.id,
    points: rows.flatMap((row, i): [number, number, number, number][] => {
      const value = line.value(row);
      const time = times[i];
      if (value == null || !Number.isFinite(value) || time === undefined) return [];
      return [[time, line.low?.(row) ?? value, value, line.high?.(row) ?? value]];
    }),
  }));
  const catalog = lines.map((line) => ({ id: line.id, label: line.label, unit: line.unit, group, device: null, longTerm: false }));
  return { result: { from, to, bucketMs, series }, catalog };
}

const TONES = ['neutral', 'warning', 'warning', 'danger'] as const;
const WORDS = ['Note', 'Worth a look', 'Problem', 'Serious'];

/** Findings, most serious first, each with its weight. */
export function Findings({ findings, empty }: { findings: QualityFinding[]; empty?: string }) {
  if (findings.length === 0) return empty ? <p className="text-sm text-muted">{empty}</p> : null;
  return (
    <ul className="flex flex-col gap-2">
      {findings.map((f, i) => (
        <li key={i} className="flex items-start gap-2 text-sm">
          <Badge tone={TONES[Math.min(3, Math.max(0, f.severity))]} className="mt-0.5 shrink-0">
            {WORDS[Math.min(3, Math.max(0, f.severity))]}
          </Badge>
          <span>{f.text}</span>
        </li>
      ))}
    </ul>
  );
}

/** A figure with its label, for the summary rows. */
export function Stat({ label, value, hint }: { label: string; value: string; hint?: string }) {
  return (
    <div className="flex min-w-[7rem] flex-col rounded-lg border border-border bg-surface px-3 py-2">
      <span className="text-xs text-muted">{label}</span>
      <span className="text-lg font-semibold tabular-nums">{value}</span>
      {hint && <span className="text-xs text-muted">{hint}</span>}
    </div>
  );
}

/** "12 ms", "0.4 ms", "–". */
export function ms(value: number | null | undefined): string {
  if (value == null) return '–';
  return `${value < 10 && value !== Math.round(value) ? value.toFixed(1) : Math.round(value)} ms`;
}

/** "3 %", "0.5 %", "–". */
export function pct(value: number | null | undefined): string {
  if (value == null) return '–';
  return `${value < 10 && value !== Math.round(value) ? value.toFixed(1) : Math.round(value)} %`;
}

/** "1 h 12 min", "45 min", "30 s". */
export function duration(seconds: number): string {
  if (seconds < 60) return `${Math.round(seconds)} s`;
  const minutes = Math.round(seconds / 60);
  return minutes < 60 ? `${minutes} min` : `${Math.floor(minutes / 60)} h ${minutes % 60} min`;
}
