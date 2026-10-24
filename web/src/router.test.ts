import { describe, expect, it } from 'vitest';
import { parseIncidentSearch } from '@/features/incidents/IncidentPage';
import { parseIncidentsSearch } from '@/features/incidents/IncidentsPage';
import { historyFor, parseDiagnosticsSearch } from '@/features/diagnostics/search';
import { DEFAULT_SERIES } from '@/features/history/ranges';
import { parseHistorySearch } from './router';

describe('history search params', () => {
  it('accepts supported ranges, series and an end time', () => {
    expect(parseHistorySearch({ range: '24h', series: 'cpu,gpu.3.temp', end: 1_780_000_000_000 })).toEqual({ range: '24h', series: 'cpu,gpu.3.temp', end: 1_780_000_000_000 });
    expect(parseHistorySearch({ range: '30d' }).range).toBe('30d');
  });

  it('falls back for missing or unsupported values', () => {
    expect(parseHistorySearch({})).toEqual({ range: '1h', series: DEFAULT_SERIES.join(',') });
    expect(parseHistorySearch({ range: '2y' }).range).toBe('1h');
    expect(parseHistorySearch({ range: 42 }).range).toBe('1h');
    expect(parseHistorySearch({ series: 'cpu,<script>,gpu' }).series).toBe('cpu,gpu');
    expect(parseHistorySearch({ end: -5 }).end).toBeUndefined();
  });
});

describe('incident search params', () => {
  it('keeps only known filters', () => {
    expect(parseIncidentsSearch({ type: 'ScreenFlicker', source: 'Hotkey', period: '7d', q: '  flicker ' })).toEqual({ type: 'ScreenFlicker', source: 'Hotkey', period: '7d', q: 'flicker' });
    expect(parseIncidentsSearch({ type: 'Explosion', source: 'Mars', period: 'all', q: ' ' })).toEqual({});
  });

  it('keeps a valid moment', () => {
    expect(parseIncidentSearch({ t: 1_780_000_000_123 })).toEqual({ t: 1_780_000_000_123 });
    expect(parseIncidentSearch({ t: 'soon' })).toEqual({});
  });
});

describe('period diagnostic search params', () => {
  it('keeps a valid period of at most 31 days', () => {
    expect(parseDiagnosticsSearch({ from: 1_780_000_000_000, to: 1_780_003_600_000 })).toEqual({ from: 1_780_000_000_000, to: 1_780_003_600_000 });
    expect(parseDiagnosticsSearch({ from: 1_780_003_600_000, to: 1_780_000_000_000 })).toEqual({});
    expect(parseDiagnosticsSearch({ from: 1_780_000_000_000, to: 1_780_000_000_000 + 32 * 86_400_000 })).toEqual({});
    expect(parseDiagnosticsSearch({ from: 'yesterday', to: 1_780_000_000_000 })).toEqual({});
  });

  it('opens History on the smallest window around a span, centered', () => {
    const now = 1_790_000_000_000;
    // 20 minutes: the 1 h window, centered on the span, with the span's series and moment.
    expect(historyFor(1_780_000_000_000, 1_780_001_200_000, ['memory.commit'], 1_780_000_000_000, now)).toEqual({
      range: '1h',
      series: 'memory.commit',
      end: 1_780_001_200_000 + 1_200_000,
      at: 1_780_000_000_000,
    });
    // A span ending now stays live; without series the default ones.
    expect(historyFor(now - 600_000, now, null, undefined, now)).toEqual({ range: '15m', series: DEFAULT_SERIES.join(',') });
  });
});
