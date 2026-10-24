import type { OverlayPanel, OverlayReading, OverlaySource, OverlayStyle } from '@/lib/api/types';
import { DEFAULT_STYLE } from './model';

/** A kind of screen by its shape: what a design is made for. */
export type ScreenFamily = 'bar' | 'square' | 'small' | 'tablet' | 'portable' | 'monitor';

/** A screen size the designs are made for (USB screens, small monitors used as sensor panels). */
export interface ScreenSize {
  id: string;
  name: string;
  width: number;
  height: number;
  family: ScreenFamily;
  /** Screens of this size. */
  examples: string;
}

/**
 * Common sizes of the screens people use for readings: bars, rounds and squares, small and portable monitors and
 * standard monitors used whole, each the way it is usually mounted and turned upright.
 */
export const SCREEN_SIZES: ScreenSize[] = [
  { id: 'bar-88', name: '8.8″ bar', width: 1920, height: 480, family: 'bar', examples: 'Lian Li Universal Screen 8.8″, 8.8″ case screens' },
  { id: 'bar-88-portrait', name: '8.8″ bar, upright', width: 480, height: 1920, family: 'bar', examples: 'Lian Li Universal Screen 8.8″ turned upright, upright 8.8″ case screens' },
  { id: 'bar-92', name: '9.2″ bar', width: 1920, height: 464, family: 'bar', examples: 'Lian Li Vision 9.2″' },
  { id: 'bar-92-portrait', name: '9.2″ bar, upright', width: 464, height: 1920, family: 'bar', examples: 'Lian Li Vision 9.2″ upright' },
  { id: 'bar-123', name: '12.3″ bar', width: 1920, height: 720, family: 'bar', examples: '12.3″ stretched bar monitors' },
  { id: 'bar-123-portrait', name: '12.3″ bar, upright', width: 720, height: 1920, family: 'bar', examples: '12.3″ bar monitors standing upright' },
  { id: 'square-480', name: '480 × 480 round or square', width: 480, height: 480, family: 'square', examples: 'HydroShift II LCD, cooler pump screens' },
  { id: 'square-400', name: '400 × 400 round', width: 400, height: 400, family: 'square', examples: 'Small round cooler and hub screens' },
  { id: 'small-35', name: '3.5″ landscape', width: 480, height: 320, family: 'small', examples: '3.5″ USB sensor screens' },
  { id: 'small-35-portrait', name: '3.5″ portrait', width: 320, height: 480, family: 'small', examples: '3.5″ USB sensor screens, upright' },
  { id: 'small-5', name: '5″', width: 800, height: 480, family: 'small', examples: '5″ HDMI and USB displays' },
  { id: 'small-5-portrait', name: '5″ upright', width: 480, height: 800, family: 'small', examples: '5″ displays standing upright' },
  { id: 'tablet-7', name: '7″', width: 1024, height: 600, family: 'tablet', examples: '7″ case and desk displays' },
  { id: 'tablet-7-portrait', name: '7″ upright', width: 600, height: 1024, family: 'tablet', examples: '7″ case side-panel displays, upright' },
  { id: 'tablet-10', name: '10.1″', width: 1280, height: 800, family: 'tablet', examples: '10.1″ panels' },
  { id: 'tablet-10-portrait', name: '10.1″ upright', width: 800, height: 1280, family: 'tablet', examples: '10.1″ panels standing upright' },
  { id: 'portable-fhd', name: 'Portable 14–16″', width: 1920, height: 1080, family: 'portable', examples: 'USB-C portable monitors at 100 % scaling' },
  { id: 'portable-fhd-portrait', name: 'Portable 14–16″, upright', width: 1080, height: 1920, family: 'portable', examples: 'USB-C portable monitors on a stand, upright' },
  { id: 'monitor-qhd', name: 'Monitor 27″', width: 2560, height: 1440, family: 'monitor', examples: 'A standard monitor used whole as a dashboard (a second monitor), 1440p or 4K at 150 %' },
  { id: 'monitor-qhd-portrait', name: 'Monitor 27″, upright', width: 1440, height: 2560, family: 'monitor', examples: 'A standard monitor turned upright beside the main one' },
];

/** A panel of a screen design: where it goes on the design's own screen, in pixels, and what it is. */
export interface ScreenDesignPart {
  rect: { x: number; y: number; width: number; height: number };
  panel: Omit<OverlayPanel, 'id' | 'x' | 'y' | 'width' | 'height'>;
}

/** A design that fills a whole screen of one size (and is fitted to others). */
export interface ScreenDesign {
  id: string;
  name: string;
  description: string;
  /** The size it is made for (an id of {@link SCREEN_SIZES}). */
  size: string;
  build: (sources: readonly OverlaySource[]) => ScreenDesignPart[];
}

/** Finds this PC's values: the first graphics card that reports, the first network adapter with traffic… */
export function picker(sources: readonly OverlaySource[]) {
  const has = (id: string) => sources.some((s) => s.id === id);
  const first = (prefix: string, suffix: string) =>
    sources.find((s) => s.id.startsWith(prefix) && s.id.endsWith(suffix) && s.value != null && s.value !== 0)?.id ??
    sources.find((s) => s.id.startsWith(prefix) && s.id.endsWith(suffix) && s.value != null)?.id ??
    sources.find((s) => s.id.startsWith(prefix) && s.id.endsWith(suffix))?.id;
  const gpu = (suffix: string) => first('gpu.', `.${suffix}`) ?? '';
  const net = (suffix: string) => first('net.', `.${suffix}`) ?? '';
  /** Drives that report a temperature. */
  const disks = sources.filter((s) => s.id.startsWith('disk.') && s.id.endsWith('.temp') && s.value != null).map((s) => s.id);
  /** The readings whose values this PC has, in order. */
  const take = (readings: OverlayReading[]) => readings.filter((r) => has(r.source));
  return { gpu, net, disks, take };
}

/** A look that fills a screen: opaque, square to its edges, no shadow (there is nothing behind it). */
export const full = (look: Partial<OverlayStyle>): OverlayStyle => ({ ...DEFAULT_STYLE, backgroundOpacity: 100, shadow: 0, cornerRadius: 0, border: null, ...look });
export const whole = (size: string) => {
  const s = SCREEN_SIZES.find((x) => x.id === size)!;
  return { x: 0, y: 0, width: s.width, height: s.height };
};

