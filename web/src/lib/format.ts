/** Formatting helpers. Missing values render as "Unavailable", never as 0. */

import type { LiveMetric } from '@/lib/api/types';

export const UNAVAILABLE = 'Unavailable';

const BYTE_UNITS = ['B', 'KB', 'MB', 'GB', 'TB'] as const;
const BIT_RATE_UNITS = ['bps', 'Kbps', 'Mbps', 'Gbps'] as const;

function isUsable(value: number | null | undefined): value is number {
  return value != null && Number.isFinite(value);
}

export function formatBytes(bytes: number | null | undefined, fractionDigits = 1): string {
  if (!isUsable(bytes) || bytes < 0) return UNAVAILABLE;
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < BYTE_UNITS.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value.toFixed(unit === 0 ? 0 : fractionDigits)} ${BYTE_UNITS[unit]}`;
}

export function formatBytesPerSecond(bytesPerSecond: number | null | undefined): string {
  const text = formatBytes(bytesPerSecond);
  return text === UNAVAILABLE ? text : `${text}/s`;
}

export function formatPercent(value: number | null | undefined, fractionDigits = 0): string {
  if (!isUsable(value)) return UNAVAILABLE;
  return `${value.toFixed(fractionDigits)}%`;
}

export function formatBitsPerSecond(bitsPerSecond: number | null | undefined): string {
  if (!isUsable(bitsPerSecond) || bitsPerSecond < 0) return UNAVAILABLE;
  let value = bitsPerSecond;
  let unit = 0;
  while (value >= 1000 && unit < BIT_RATE_UNITS.length - 1) {
    value /= 1000;
    unit++;
  }
  return `${value.toFixed(unit === 0 ? 0 : 1)} ${BIT_RATE_UNITS[unit]}`;
}

export function formatTemperature(celsius: number | null | undefined): string {
  if (!isUsable(celsius)) return UNAVAILABLE;
  return `${Math.round(celsius)}°C`;
}

export function formatNumber(value: number | null | undefined, unit: string, fractionDigits = 0): string {
  if (!isUsable(value)) return UNAVAILABLE;
  return `${value.toFixed(fractionDigits)} ${unit}`;
}

/** Formats a value in a series unit ("%", "°C", "W", "MHz", "B", "B/s", "ms", "RPM", "/s" or ""). */
export function formatUnit(value: number | null | undefined, unit: string): string {
  if (value == null || !Number.isFinite(value)) return UNAVAILABLE;
  switch (unit) {
    case '%':
      return `${value.toFixed(value < 10 ? 1 : 0)}%`;
    case '°C':
      return `${Math.round(value)}°C`;
    case 'B':
      return formatBytes(value);
    case 'B/s':
      return formatBytesPerSecond(value);
    case 'MHz':
      return value >= 1000 ? `${(value / 1000).toFixed(2)} GHz` : `${Math.round(value)} MHz`;
    case '':
      return value >= 100 ? Math.round(value).toLocaleString() : value.toFixed(1);
    default:
      return `${value >= 100 ? Math.round(value).toLocaleString() : value.toFixed(1)} ${unit}`;
  }
}

/** Why a metric is missing, in words (keys are PcDiag.Core.Telemetry.Availability names). */
const AVAILABILITY_TEXT: Record<string, string> = {
  Unsupported: 'Not exposed by this hardware, driver or Windows version',
  RequiresElevation: 'Needs the recorder to run as administrator',
  NotPresent: 'The device is not present',
  Error: 'Reading failed; see the Agent log',
  Pending: 'Waiting for the next sample',
  Disabled: 'Turned off in settings',
  PermissionDenied: 'Blocked by a Windows privacy setting',
  DriverMissing: 'Needs the sensor driver (PawnIO)',
  NotSelected: 'Not recorded: left out of the custom recording',
};

export function describeUnavailable(reason: string | null | undefined): string {
  if (!reason) return 'No value';
  return AVAILABILITY_TEXT[reason] ?? reason;
}

/** Formats a live metric with a formatter; the tooltip explains a missing value. */
export function metricText(metric: LiveMetric | null | undefined, format: (value: number) => string): { text: string; title?: string } {
  if (metric?.value != null && Number.isFinite(metric.value)) return { text: format(metric.value) };
  return { text: UNAVAILABLE, title: describeUnavailable(metric?.unavailable) };
}

export function formatTime(iso: string | null | undefined): string {
  if (!iso) return UNAVAILABLE;
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? UNAVAILABLE : date.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' });
}

export function formatDateTime(iso: string | null | undefined): string {
  if (!iso) return UNAVAILABLE;
  const date = new Date(iso);
  return Number.isNaN(date.getTime())
    ? UNAVAILABLE
    : date.toLocaleString(undefined, { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
}

/** "1:05" (m:ss) until a moment; "0:00" once it has passed. */
export function formatRemaining(untilIso: string | null | undefined, now: number = Date.now()): string {
  if (!untilIso) return UNAVAILABLE;
  const ms = Math.max(0, new Date(untilIso).getTime() - now);
  const totalSeconds = Math.round(ms / 1000);
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return `${minutes}:${seconds.toString().padStart(2, '0')}`;
}

/** "8 Oct 2026" (the date only). */
export function formatDate(iso: string | number | null | undefined): string {
  if (iso == null || iso === '') return UNAVAILABLE;
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? UNAVAILABLE : date.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
}

/** "just now", "5 min ago", "3 h ago", "4 days ago", else the date. */
export function formatAgo(at: string | number | null | undefined, now: number = Date.now()): string {
  if (at == null || at === '') return UNAVAILABLE;
  const time = typeof at === 'number' ? at : Date.parse(at);
  if (!Number.isFinite(time)) return UNAVAILABLE;
  const seconds = Math.max(0, (now - time) / 1000);
  if (seconds < 60) return 'just now';
  if (seconds < 3600) return `${Math.round(seconds / 60)} min ago`;
  if (seconds < 86_400) return `${Math.round(seconds / 3600)} h ago`;
  if (seconds < 30 * 86_400) return `${Math.round(seconds / 86_400)} day${Math.round(seconds / 86_400) === 1 ? '' : 's'} ago`;
  return formatDate(time);
}
