import { motion, useReducedMotion } from 'motion/react';
import { useId } from 'react';
import { heatColor, heatGradient, levelOf, READING_PALETTES, THERMOMETER_MIN, type ReadingKind } from '@/lib/charts/readings';
import { cn } from '@/lib/utils';

const LEVEL_COLORS = { warning: '#f59e0b', critical: '#ef4444' } as const;

function clamp01(v: number) {
  return Math.min(1, Math.max(0, v));
}

export interface ThermometerProps {
  value: number | null;
  /** Top of the scale (°C). */
  max: number;
  warning: number;
  critical: number;
  className?: string;
}

/**
 * A horizontal thermometer: the bulb takes the heat color of the current temperature, the column fills along the
 * sensor's heat scale, and marks show the warning and critical limits.
 */
export function Thermometer({ value, max, warning, critical, className }: ThermometerProps) {
  const reduced = useReducedMotion() ?? false;
  const id = useId().replace(/:/g, '');
  const min = THERMOMETER_MIN;
  const tubeStart = 24;
  const tubeEnd = 156;
  const length = tubeEnd - tubeStart;
  const fill = value == null ? 0 : clamp01((value - min) / (max - min)) * length;
  const bulb = value == null ? 'var(--unavailable)' : heatColor(value, warning, critical);
  const at = (c: number) => tubeStart + clamp01((c - min) / (max - min)) * length;
  const critical_ = levelOf(value, warning, critical) === 'critical';

  return (
    <svg viewBox="0 0 160 40" className={cn('w-full', className)} aria-hidden>
      <defs>
        <linearGradient id={`${id}-heat`} gradientUnits="userSpaceOnUse" x1={tubeStart} x2={tubeEnd} y1="0" y2="0">
          {heatGradient(min, max, warning, critical).map((s) => (
            <stop key={s.offset} offset={s.offset} stopColor={s.color} />
          ))}
        </linearGradient>
        <clipPath id={`${id}-level`}>
          <motion.rect
            x={tubeStart - 6}
            y="11"
            height="20"
            initial={false}
            animate={{ width: fill + 6 }}
            transition={reduced ? { duration: 0 } : { duration: 0.9, ease: 'easeOut' }}
          />
        </clipPath>
      </defs>
      {/* The tube with the whole heat scale faintly visible, then the column up to the temperature. */}
      <rect x={tubeStart - 4} y="13" width={length + 4} height="15" rx="7.5" style={{ fill: 'var(--border)' }} />
      <rect x={tubeStart - 4} y="13" width={length + 4} height="15" rx="7.5" fill={`url(#${id}-heat)`} opacity="0.16" />
      <rect x={tubeStart - 4} y="13" width={length + 4} height="15" rx="7.5" fill={`url(#${id}-heat)`} clipPath={`url(#${id}-level)`} />
      {/* Limits. */}
      <line x1={at(warning)} x2={at(warning)} y1="5" y2="11" stroke={LEVEL_COLORS.warning} strokeWidth="1.5" strokeLinecap="round" />
      <line x1={at(critical)} x2={at(critical)} y1="5" y2="11" stroke={LEVEL_COLORS.critical} strokeWidth="1.5" strokeLinecap="round" />
      {/* The bulb. */}
      <circle cx="15" cy="20.5" r="14" style={{ fill: 'var(--surface)' }} />
      <motion.circle
        cx="15"
        cy="20.5"
        r="11.5"
        initial={false}
        animate={{ fill: bulb }}
        transition={reduced ? { duration: 0 } : { duration: 0.6 }}
        className={cn(critical_ && !reduced && 'thermo-critical')}
      />
      <circle cx="11" cy="16.5" r="3" fill="#ffffff" opacity="0.35" />
    </svg>
  );
}

export interface MeterBarProps {
  kind: ReadingKind;
  value: number | null;
  max: number;
  warning: number;
  critical: number;
  /** Accessible name. */
  label: string;
  className?: string;
}

/**
 * A slim bar in the style of its reading: usage and capacity fill with their palette (amber or red past the
 * limits), temperatures fill along the sensor's heat scale.
 */
export function MeterBar({ kind, value, max, warning, critical, label, className }: MeterBarProps) {
  const level = levelOf(value, warning, critical);
  const min = kind === 'temperature' ? THERMOMETER_MIN : 0;
  const share = value == null ? 0 : clamp01((value - min) / (max - min));
  let fill: string;
  if (kind === 'temperature') {
    const stops = heatGradient(min, max, warning, critical)
      .map((s) => `${s.color} ${Math.round(s.offset * 100)}%`)
      .join(', ');
    fill = `linear-gradient(90deg, ${stops})`;
  } else {
    const palette = READING_PALETTES[kind];
    const end = level === 'critical' ? LEVEL_COLORS.critical : level === 'warning' ? LEVEL_COLORS.warning : palette.to;
    fill = `linear-gradient(90deg, ${palette.from}, ${end})`;
  }

  return (
    <div
      role="meter"
      aria-label={label}
      aria-valuemin={min}
      aria-valuemax={max}
      aria-valuenow={value ?? undefined}
      className={cn('relative h-1.5 w-full overflow-hidden rounded-full bg-border/70', kind === 'capacity' && 'h-2', className)}
    >
      {kind === 'temperature' ? (
        // The full heat scale, revealed up to the temperature, so the end of the bar has the temperature's color.
        <div
          className="absolute inset-0 rounded-full transition-[clip-path] duration-700 ease-out motion-reduce:transition-none"
          style={{ background: fill, clipPath: `inset(0 ${100 - share * 100}% 0 0 round 9999px)` }}
        />
      ) : (
        <div
          className={cn(
            'absolute inset-y-0 left-0 rounded-full transition-[width] duration-700 ease-out motion-reduce:transition-none',
            kind === 'load' && 'meter-segments',
          )}
          style={{ width: `${share * 100}%`, background: fill }}
        />
      )}
    </div>
  );
}
