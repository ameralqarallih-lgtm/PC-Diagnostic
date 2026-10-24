import { useId, useLayoutEffect, useRef, useState } from 'react';
import type { OverlayBackdrop } from '@/lib/api/types';
import { rgba } from './model';

const WHITE = '#FFFFFF';
const BLACK = '#000000';

/** The chevron's cut corners, as the recorder cuts them. */
export const chevronCut = (w: number, h: number, s: number) => Math.min(10 * s, Math.min(w, h) / 3);

const chevron = (w: number, h: number, inset: number, cut: number) => {
  const [x0, y0, x1, y1] = [inset, inset, w - inset, h - inset];
  return `${x0},${y0} ${x1 - cut},${y0} ${x1},${y0 + cut} ${x1},${y1} ${x0 + cut},${y1} ${x0},${y1 - cut}`;
};

/** The size of what it is in (a tile), followed as it changes. */
function useSize() {
  const ref = useRef<HTMLSpanElement>(null);
  const [size, setSize] = useState<{ w: number; h: number } | null>(null);
  useLayoutEffect(() => {
    const element = ref.current;
    if (!element) return;
    const set = (w: number, h: number) => setSize((old) => (old && Math.abs(old.w - w) < 0.25 && Math.abs(old.h - h) < 0.25 ? old : w > 0 && h > 0 ? { w, h } : null));
    set(element.offsetWidth, element.offsetHeight);
    if (typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver((entries) => {
      const box = entries[0]?.contentRect;
      if (box) set(box.width, box.height);
    });
    observer.observe(element);
    return () => observer.disconnect();
  }, []);
  return [ref, size] as const;
}

/**
 * What is drawn behind a tile (the style's backdrop), fitted to it wherever it is and whatever its size: the recorder's
 * DrawBackdrop (OverlayBackdrops.cs) with the same shapes at the same sizes, in units of the panel's scale. It sits
 * under the tile's readings (the tile is its own stacking context). For chevron it also draws the tile's color and its
 * outline past a limit, in the tile's cut shape.
 */
export function TileBackdrop({
  kind,
  s,
  radius,
  pad,
  accent,
  underline,
  dial,
  level,
  fill,
}: {
  kind: OverlayBackdrop;
  /** The panel's scale. */
  s: number;
  /** The tile's corner radius. */
  radius: number;
  /** The tile's padding. */
  pad: number;
  accent: string;
  /** Where the line under the label goes, from the tile's top (none without a label). */
  underline: number | null;
  /** The tile holds a dial (its bezel is drawn around the dial instead). */
  dial: boolean;
  /** The warning or critical color past a limit (the tile's outline is drawn over the backdrop). */
  level: string | null;
  /** The tile's color (chevron only: the tile itself has none). */
  fill?: string;
}) {
  const [ref, size] = useSize();
  const id = useId().replace(/[^a-zA-Z0-9]/g, '');
  const body = size ? shapes(kind, size.w, size.h) : null;

  // Past a limit, the tile's outline in the warning or critical color, over the backdrop (as the recorder draws it).
  function outline(w: number, h: number) {
    if (!level) return null;
    const width = Math.max(1, 1.5 * s);
    if (kind === 'chevron') return <polygon points={chevron(w, h, 0, chevronCut(w, h, s))} fill="none" stroke={rgba(level, 78)} strokeWidth={width} />;
    const inset = width / 2;
    const rw = Math.max(0, w - width);
    const rh = Math.max(0, h - width);
    return <rect x={inset} y={inset} width={rw} height={rh} rx={Math.max(0, Math.min(radius - inset, Math.min(rw, rh) / 2))} fill="none" stroke={rgba(level, 80)} strokeWidth={width} />;
  }

  function shapes(k: OverlayBackdrop, w: number, h: number) {
    const rect = (inset: number) => {
      const width = Math.max(0, w - 2 * inset);
      const height = Math.max(0, h - 2 * inset);
      return { x: inset, y: inset, width, height, rx: Math.max(0, Math.min(radius - inset, Math.min(width, height) / 2)) };
    };
    switch (k) {
      case 'plate': {
        const screws: [number, number][] = [
          [6 * s, 6 * s],
          [w - 6 * s, 6 * s],
          [w - 6 * s, h - 6 * s],
          [6 * s, h - 6 * s],
        ];
        return (
          <>
            <line x1={radius} y1={0.5 * s} x2={w - radius} y2={0.5 * s} stroke={rgba(WHITE, 22)} strokeWidth={s} />
            <line x1={radius} y1={h - 0.75 * s} x2={w - radius} y2={h - 0.75 * s} stroke={rgba(BLACK, 35)} strokeWidth={1.5 * s} />
            {w > 30 * s &&
              h > 24 * s &&
              screws.map(([cx, cy]) => (
                <g key={`${cx}-${cy}`}>
                  <circle cx={cx} cy={cy} r={1.9 * s} fill={rgba(WHITE, 18)} stroke={rgba(BLACK, 40)} strokeWidth={0.8 * s} />
                  <line x1={cx - 0.85 * s} y1={cy + 0.85 * s} x2={cx + 0.85 * s} y2={cy - 0.85 * s} stroke={rgba(BLACK, 45)} strokeWidth={0.7 * s} />
                </g>
              ))}
          </>
        );
      }
      case 'hud': {
        const arm = Math.min(9 * s, Math.min(w, h) / 3);
        const t = 1.6 * s;
        const half = t / 2;
        const corners = [
          [[half, arm], [half, half], [arm, half]],
          [[w - arm, half], [w - half, half], [w - half, arm]],
          [[w - half, h - arm], [w - half, h - half], [w - arm, h - half]],
          [[arm, h - half], [half, h - half], [half, h - arm]],
        ];
        return (
          <>
            {corners.map((points, i) => (
              <polyline key={i} points={points.map((p) => p.join(',')).join(' ')} fill="none" stroke={rgba(accent, 85)} strokeWidth={t} strokeLinejoin="miter" strokeLinecap="butt" />
            ))}
            {underline != null && <line x1={pad} y1={underline} x2={pad + (w - 2 * pad) * 0.6} y2={underline} stroke={rgba(accent, 35)} strokeWidth={s} />}
          </>
        );
      }
      case 'bezel':
        return dial ? null : (
          <>
            <rect {...rect(0.5 * s)} fill="none" stroke={rgba(WHITE, 14)} strokeWidth={s} />
            <rect {...rect(3.5 * s)} fill="none" stroke={rgba(BLACK, 35)} strokeWidth={s} />
          </>
        );
      case 'socket':
        return (
          <>
            <defs>
              <linearGradient id={`${id}e`} x1="0" y1="0" x2="0" y2="1">
                <stop offset="0" stopColor={BLACK} stopOpacity={0.55} />
                <stop offset="1" stopColor={WHITE} stopOpacity={0.14} />
              </linearGradient>
            </defs>
            <rect {...rect(0)} fill={rgba(BLACK, 16)} />
            <rect {...rect(0.75 * s)} fill="none" stroke={`url(#${id}e)`} strokeWidth={1.5 * s} />
          </>
        );
      case 'chevron': {
        const cut = chevronCut(w, h, s);
        return (
          <>
            {fill && <polygon points={chevron(w, h, 0, cut)} fill={fill} />}
            <polygon points={chevron(w, h, 0.6 * s, Math.max(0, cut - 0.25 * s))} fill="none" stroke={rgba(accent, 55)} strokeWidth={1.2 * s} />
            {cut > 3 * s && <polygon points={`${w - cut + 2.5 * s},0 ${w},0 ${w},${cut - 2.5 * s}`} fill={rgba(accent, 90)} />}
          </>
        );
      }
      case 'glass':
        return (
          <>
            <defs>
              <linearGradient id={`${id}f`} x1="0" y1="0" x2="0" y2="1">
                <stop offset="0" stopColor={WHITE} stopOpacity={0.14} />
                <stop offset="1" stopColor={WHITE} stopOpacity={0.02} />
              </linearGradient>
            </defs>
            <rect {...rect(0)} fill={`url(#${id}f)`} />
            <rect {...rect(0.5 * s)} fill="none" stroke={rgba(WHITE, 22)} strokeWidth={s} />
            <line x1={radius} y1={1.5 * s} x2={w - radius} y2={1.5 * s} stroke={rgba(WHITE, 45)} strokeWidth={s} />
          </>
        );
      case 'neon': {
        const color = level ?? accent;
        return (
          <>
            {(
              [
                [6, 10],
                [3.5, 22],
                [1.4, 95],
              ] as const
            ).map(([width, percent]) => (
              <rect key={width} {...rect(3 * s)} fill="none" stroke={rgba(color, percent)} strokeWidth={width * s} />
            ))}
          </>
        );
      }
      case 'blueprint': {
        const grid = 10 * s;
        const line = Math.max(1, 0.8 * s);
        return (
          <>
            <defs>
              <pattern id={`${id}g`} width={grid} height={grid} patternUnits="userSpaceOnUse">
                <path d={`M ${line / 2} 0 V ${grid} M 0 ${line / 2} H ${grid}`} stroke={rgba(accent, 12)} strokeWidth={line} fill="none" />
              </pattern>
            </defs>
            <rect {...rect(0)} fill={`url(#${id}g)`} />
            <rect {...rect(0.5 * s)} fill="none" stroke={rgba(accent, 45)} strokeWidth={s} />
          </>
        );
      }
      default:
        return null;
    }
  }

  return (
    <span ref={ref} aria-hidden data-backdrop={kind} className="pointer-events-none absolute inset-0" style={{ zIndex: -1 }}>
      {size && (
        <svg width={size.w} height={size.h} className="absolute inset-0 overflow-visible">
          {body}
          {outline(size.w, size.h)}
        </svg>
      )}
    </span>
  );
}

/**
 * A dial's bezel (backdrop "bezel"): a band behind its track in the tiles' color and a fine ring in the accent around
 * it, kept inside the tile's padding, as the recorder's DrawBezel. It follows the dial's own size (a dial grows with its
 * tile), and the track's thickness as the recorder works it out from that size.
 */
export function DialBezel({ px, pad, s, accent, tint }: { px: number; pad: number; s: number; accent: string; tint: string }) {
  const [ref, size] = useSize();
  let ring = null;
  if (size) {
    const d = size.w;
    const thickness = Math.max(2 * s, Math.min(d * 0.085, px * 0.9));
    const outer = d / 2 + Math.min(0.35 * thickness + s, Math.max(0, pad - 2.5 * s));
    const inner = Math.max(0, d / 2 - 1.35 * thickness);
    const band = (outer + inner) / 2;
    const hair = outer + s;
    const line = Math.max(1, 0.9 * s);
    const half = hair + line;
    ring = (
      <svg width={2 * half} height={2 * half} className="absolute overflow-visible" style={{ left: d / 2 - half, top: d / 2 - half }}>
        <circle cx={half} cy={half} r={band} fill="none" stroke={tint} strokeWidth={outer - inner} />
        <circle cx={half} cy={half} r={hair} fill="none" stroke={rgba(accent, 45)} strokeWidth={line} />
      </svg>
    );
  }

  return (
    <span ref={ref} aria-hidden data-bezel className="pointer-events-none absolute left-0 top-0" style={{ width: '100%', aspectRatio: '1', zIndex: -1 }}>
      {ring}
    </span>
  );
}
