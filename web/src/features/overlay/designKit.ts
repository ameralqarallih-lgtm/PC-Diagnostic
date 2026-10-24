import type { OverlayReading, OverlaySource, OverlayStyle } from '@/lib/api/types';
import { DEFAULT_STYLE } from './model';

/** Finds this PC's values for a design: the first graphics card that reports, drives with temperatures… */
export function picker(sources: readonly OverlaySource[]) {
  const has = (id: string) => sources.some((s) => s.id === id);
  const first = (prefix: string, suffix: string) =>
    sources.find((s) => s.id.startsWith(prefix) && s.id.endsWith(suffix) && s.value != null)?.id ?? sources.find((s) => s.id.startsWith(prefix) && s.id.endsWith(suffix))?.id;
  const gpu = (suffix: string) => first('gpu.', `.${suffix}`);
  const disks = sources.filter((s) => s.id.startsWith('disk.') && s.id.endsWith('.temp') && s.value != null).map((s) => s.id);
  /** The readings whose values this PC has, in order. */
  const take = (readings: (OverlayReading | null | undefined | false)[]) => readings.filter((r): r is OverlayReading => !!r && has(r.source));
  return { first, gpu, disks, take };
}

export const style = (look: Partial<OverlayStyle>): OverlayStyle => ({ ...DEFAULT_STYLE, ...look });

