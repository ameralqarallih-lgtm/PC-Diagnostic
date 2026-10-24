import { describe, expect, it } from 'vitest';
import {
  describeUnavailable,
  formatBitsPerSecond,
  formatBytes,
  formatBytesPerSecond,
  formatPercent,
  formatRemaining,
  formatTemperature,
  metricText,
  UNAVAILABLE,
} from './format';

describe('formatters', () => {
  it('formats bytes with binary units', () => {
    expect(formatBytes(0)).toBe('0 B');
    expect(formatBytes(1536)).toBe('1.5 KB');
    expect(formatBytes(16 * 1024 ** 3)).toBe('16.0 GB');
  });

  it('formats percentages and bit rates', () => {
    expect(formatPercent(42.4)).toBe('42%');
    expect(formatPercent(99.95, 1)).toBe('100.0%');
    expect(formatBitsPerSecond(12_000_000)).toBe('12.0 Mbps');
    expect(formatBitsPerSecond(900)).toBe('900 bps');
  });

  it('formats temperatures', () => {
    expect(formatTemperature(61.6)).toBe('62°C');
  });

  it.each([null, undefined, Number.NaN, Number.POSITIVE_INFINITY])('renders %s as Unavailable, never 0', (value) => {
    expect(formatBytes(value)).toBe(UNAVAILABLE);
    expect(formatPercent(value)).toBe(UNAVAILABLE);
    expect(formatBitsPerSecond(value)).toBe(UNAVAILABLE);
    expect(formatTemperature(value)).toBe(UNAVAILABLE);
  });

  it('rejects negative sizes and rates', () => {
    expect(formatBytes(-1)).toBe(UNAVAILABLE);
    expect(formatBitsPerSecond(-1)).toBe(UNAVAILABLE);
  });

  it('formats live metrics and explains missing ones', () => {
    expect(metricText({ value: 42.4, unavailable: null }, (v) => formatPercent(v))).toEqual({ text: '42%' });
    expect(metricText({ value: null, unavailable: 'DriverMissing' }, (v) => formatPercent(v))).toEqual({
      text: UNAVAILABLE,
      title: 'Needs the sensor driver (PawnIO)',
    });
    expect(metricText(undefined, (v) => formatPercent(v)).text).toBe(UNAVAILABLE);
    expect(describeUnavailable('SomethingNew')).toBe('SomethingNew');
  });

  it('formats rates and countdowns', () => {
    expect(formatBytesPerSecond(2048)).toBe('2.0 KB/s');
    expect(formatBytesPerSecond(null)).toBe(UNAVAILABLE);
    const now = Date.parse('2026-10-06T12:00:00Z');
    expect(formatRemaining('2026-10-06T12:01:05Z', now)).toBe('1:05');
    expect(formatRemaining('2026-10-06T11:59:00Z', now)).toBe('0:00');
  });
});
