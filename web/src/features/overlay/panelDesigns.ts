import type { OverlayReading, OverlaySource, OverlayStyle } from '@/lib/api/types';
import { picker, style } from './designKit';
import type { PanelDesign } from './designs';
import { LOOK, type Look } from './screenDesigns';

type Pick = ReturnType<typeof picker>;

/** A panel on a monitor: the screen look, translucent over the desktop or game, with rounded corners and a soft shadow. */
const onMonitor = (look: Look, extra: Partial<OverlayStyle> = {}): OverlayStyle =>
  style({ ...LOOK[look], size: 14, padding: 12, valueScale: 135, backgroundOpacity: 86, cornerRadius: 12, shadow: 40, ...extra });

/** A designed panel of tiles. */
function design(
  id: string,
  name: string,
  description: string,
  title: string | null,
  columns: number,
  look: Look,
  extra: Partial<OverlayStyle>,
  readings: (p: Pick) => (OverlayReading | false | null | undefined)[],
): PanelDesign {
  return {
    id,
    name,
    description,
    build: (sources: readonly OverlaySource[]) => {
      const p = picker(sources);
      return { title, layout: 'tiles', columns, readings: p.take(readings(p).filter((r): r is OverlayReading => !!r && !!r.source)), style: onMonitor(look, extra) };
    },
  };
}

const gpu = (p: Pick, suffix: string) => p.gpu(suffix) ?? '';

/** Panels for standard monitors with backdrops fitted to each reading: cards, wide strips and upright columns (Phase 46). */
export const MORE_DESIGNS: PanelDesign[] = [
  design('hud-cockpit', 'HUD Cockpit', 'A cyan heads-up display: brackets around two rings, thermometers and the frame rate', 'HUD', 3, 'hud', {}, (p) => [
    { source: 'cpu.usage', visual: 'ring', label: 'CPU', warning: 85, critical: 95 },
    { source: gpu(p, 'usage'), visual: 'ring', label: 'GPU', warning: 97 },
    { source: 'game.fps', visual: 'line', label: 'FPS' },
    { source: 'cpu.temp', visual: 'thermo', max: 100, label: 'CPU temp', warning: 85, critical: 95 },
    { source: gpu(p, 'temp'), visual: 'thermo', max: 100, label: 'GPU temp', warning: 80, critical: 90 },
    { source: 'net.ping.internet', label: 'Ping', warning: 80, critical: 150 },
  ]),
  design('machined-plate', 'Machined Plate', 'Brushed metal plates with screws, three speedometers and segment thermometers in orange', 'Garage', 3, 'plate', {}, (p) => [
    { source: 'game.fps', visual: 'speed', max: 240, label: 'FPS' },
    { source: gpu(p, 'usage'), visual: 'speed', label: 'GPU', warning: 97 },
    { source: 'cpu.usage', visual: 'speed', label: 'CPU', warning: 90 },
    { source: gpu(p, 'temp'), visual: 'thermo', max: 100, label: 'GPU temp', warning: 80, critical: 90, colSpan: 2 },
    { source: 'cpu.temp', visual: 'thermo', max: 100, label: 'CPU temp', warning: 85, critical: 95 },
  ]),
  design('gauge-cluster', 'Gauge Cluster', 'An instrument cluster: dials in gold bezels around the frame rate', null, 4, 'bezel', {}, (p) => [
    { source: 'cpu.usage', visual: 'arc', label: 'CPU', warning: 85, critical: 95 },
    { source: 'game.fps', visual: 'speed', max: 240, label: 'FPS', colSpan: 2, rowSpan: 2 },
    { source: gpu(p, 'usage'), visual: 'arc', label: 'GPU', warning: 97 },
    { source: 'cpu.temp', visual: 'arc', max: 100, label: 'CPU temp', warning: 85, critical: 95 },
    { source: gpu(p, 'temp'), visual: 'arc', max: 100, label: 'GPU temp', warning: 80, critical: 90 },
  ]),
  design('neon-frames', 'Neon Frames', 'Moving: glowing pink outlines around every reading, a breathing light behind', 'Neon', 3, 'neon', { motion: 'pulse', motionSpeed: 60 }, (p) => [
    { source: 'cpu.usage', visual: 'ring', label: 'CPU', warning: 85, critical: 95 },
    { source: gpu(p, 'usage'), visual: 'ring', label: 'GPU', warning: 97 },
    { source: 'memory.usage', visual: 'ring', label: 'RAM', warning: 85, critical: 95 },
    { source: 'game.fps', visual: 'graph', label: 'FPS', colSpan: 3 },
    { source: 'cpu.temp', label: 'CPU temp', warning: 85, critical: 95 },
    { source: gpu(p, 'temp'), label: 'GPU temp', warning: 80, critical: 90 },
    { source: 'time.clock' },
  ]),
  design('frosted-cards', 'Frosted Cards', 'Moving: frosted glass cards over drifting aurora light, arcs and bars', null, 3, 'glass', {}, (p) => [
    { source: 'time.clock', colSpan: 3 },
    { source: 'cpu.usage', visual: 'arc', label: 'CPU', warning: 85, critical: 95 },
    { source: gpu(p, 'usage'), visual: 'arc', label: 'GPU', warning: 97 },
    { source: 'memory.usage', visual: 'arc', label: 'RAM', warning: 85, critical: 95 },
    { source: 'cpu.temp', visual: 'bar', max: 100, label: 'CPU temp', warning: 85, critical: 95 },
    { source: gpu(p, 'temp'), visual: 'bar', max: 100, label: 'GPU temp', warning: 80, critical: 90 },
    { source: gpu(p, 'vram'), visual: 'bar', show: 'of', label: 'VRAM' },
  ]),
  design('blueprint-sheet', 'Blueprint Sheet', 'An engineering sheet: every figure in its own grid frame, in a monospaced face', 'SPEC', 2, 'blueprint', { caps: true }, (p) => [
    { source: 'cpu.usage', visual: 'line', label: 'CPU', colSpan: 2 },
    { source: 'cpu.clock', label: 'CPU clock' },
    { source: gpu(p, 'clock'), label: 'GPU clock' },
    { source: 'cpu.power', label: 'CPU power' },
    { source: gpu(p, 'power'), label: 'GPU power' },
    { source: 'cpu.temp', label: 'CPU temp', warning: 85, critical: 95 },
    { source: gpu(p, 'temp'), label: 'GPU temp', warning: 80, critical: 90 },
  ]),
  design('mech-armour', 'Mech Armour', 'Armour plates with cut corners and yellow tabs, segment bars and big numbers', 'MECH', 3, 'mech', { valueScale: 150 }, (p) => [
    { source: 'game.fps', label: 'FPS', colSpan: 2 },
    { source: 'game.low1', label: '1% low' },
    { source: 'cpu.usage', visual: 'bar', label: 'CPU', warning: 90 },
    { source: gpu(p, 'usage'), visual: 'bar', label: 'GPU', warning: 97 },
    { source: 'memory.usage', visual: 'bar', label: 'RAM', warning: 85, critical: 95 },
    { source: 'cpu.temp', visual: 'thermo', max: 100, label: 'CPU temp', warning: 85, critical: 95 },
    { source: gpu(p, 'temp'), visual: 'thermo', max: 100, label: 'GPU temp', warning: 80, critical: 90 },
    { source: 'game.frametime', label: 'Frame time' },
  ]),
  design('stealth-sockets', 'Stealth Sockets', 'Recessed sockets in graphite with a quiet green: for a dark desktop that stays calm', null, 2, 'socket', {}, (p) => [
    { source: 'cpu.usage', visual: 'bar', label: 'CPU', warning: 85, critical: 95 },
    { source: gpu(p, 'usage'), visual: 'bar', label: 'GPU', warning: 97 },
    { source: 'cpu.temp', visual: 'thermo', max: 100, label: 'CPU temp', warning: 85, critical: 95 },
    { source: gpu(p, 'temp'), visual: 'thermo', max: 100, label: 'GPU temp', warning: 80, critical: 90 },
    { source: 'memory.usage', visual: 'bar', label: 'RAM', warning: 85, critical: 95 },
    { source: 'time.clock' },
  ]),
  design('phosphor-terminal', 'Phosphor Terminal', 'A green-screen terminal: readings framed on a grid over scan lines', 'SYS', 2, 'phosphor', {}, (p) => [
    { source: 'cpu.usage', visual: 'columns', label: 'CPU', colSpan: 2 },
    { source: gpu(p, 'usage'), visual: 'columns', label: 'GPU', colSpan: 2 },
    { source: 'cpu.temp', visual: 'bar', max: 100, label: 'CPU temp', warning: 85, critical: 95 },
    { source: gpu(p, 'temp'), visual: 'bar', max: 100, label: 'GPU temp', warning: 80, critical: 90 },
    { source: 'net.ping.internet', label: 'Ping', warning: 80, critical: 150 },
    { source: 'time.clock' },
  ]),
  design('thermal-core', 'Thermal Core', 'Every temperature in an ember recess, with the power each part draws', 'Thermals', 2, 'thermal', {}, (p) => [
    { source: 'cpu.temp', visual: 'thermo', max: 100, label: 'CPU', warning: 85, critical: 95 },
    { source: gpu(p, 'temp'), visual: 'thermo', max: 100, label: 'GPU', warning: 80, critical: 90 },
    { source: gpu(p, 'hotspot'), visual: 'thermo', max: 110, label: 'Hot spot', warning: 95, critical: 105 },
    ...p.disks.slice(0, 1).map((source): OverlayReading => ({ source, visual: 'thermo', max: 80, label: 'Drive', warning: 60, critical: 70 })),
    { source: 'cpu.power', label: 'CPU power' },
    { source: gpu(p, 'power'), label: 'GPU power' },
  ]),
  design('ice-hud', 'Ice HUD', 'A light heads-up display in frosted white and deep blue, for light desktops', 'HUD', 3, 'ice', { backgroundOpacity: 80 }, (p) => [
    { source: 'cpu.usage', visual: 'ring', label: 'CPU', warning: 85, critical: 95 },
    { source: gpu(p, 'usage'), visual: 'ring', label: 'GPU', warning: 97 },
    { source: 'memory.usage', visual: 'ring', label: 'RAM', warning: 85, critical: 95 },
    { source: 'cpu.temp', label: 'CPU temp', warning: 85, critical: 95 },
    { source: gpu(p, 'temp'), label: 'GPU temp', warning: 80, critical: 90 },
    { source: 'time.clock' },
  ]),
  design('starfield-glass', 'Starfield Glass', 'Moving: glass tiles over a drifting star field in lavender', null, 3, 'stars', {}, (p) => [
    { source: 'time.clock', colSpan: 3 },
    { source: 'cpu.usage', visual: 'ring', label: 'CPU', warning: 85, critical: 95 },
    { source: gpu(p, 'usage'), visual: 'ring', label: 'GPU', warning: 97 },
    { source: 'game.fps', visual: 'ring', max: 240, label: 'FPS' },
    { source: 'cpu.temp', visual: 'status', label: 'CPU temp', warning: 85, critical: 95 },
    { source: gpu(p, 'temp'), visual: 'status', label: 'GPU temp', warning: 80, critical: 90 },
    { source: 'net.ping.internet', visual: 'status', label: 'Ping', warning: 80, critical: 150 },
  ]),

  // Wide strips, for the top or bottom of a monitor.
  design('hud-strip', 'HUD Strip', 'A one-row heads-up strip for the top of a monitor: rings, the frame rate and temperatures', null, 6, 'hud', { size: 13, valueScale: 125 }, (p) => [
    { source: 'cpu.usage', visual: 'ring', label: 'CPU', warning: 85, critical: 95 },
    { source: gpu(p, 'usage'), visual: 'ring', label: 'GPU', warning: 97 },
    { source: 'game.fps', label: 'FPS' },
    { source: 'cpu.temp', label: 'CPU temp', warning: 85, critical: 95 },
    { source: gpu(p, 'temp'), label: 'GPU temp', warning: 80, critical: 90 },
    { source: 'time.clock' },
  ]),
  design('plate-strip', 'Plate Strip', 'A one-row strip of metal plates with segment bars, for the bottom of a monitor', null, 6, 'racing', { size: 13, valueScale: 125 }, (p) => [
    { source: 'game.fps', label: 'FPS' },
    { source: 'cpu.usage', visual: 'bar', label: 'CPU', warning: 90 },
    { source: gpu(p, 'usage'), visual: 'bar', label: 'GPU', warning: 97 },
    { source: 'memory.usage', visual: 'bar', label: 'RAM', warning: 85, critical: 95 },
    { source: gpu(p, 'temp'), visual: 'bar', max: 100, label: 'GPU temp', warning: 80, critical: 90 },
    { source: 'net.ping.internet', label: 'Ping', warning: 80, critical: 150 },
  ]),

  // Upright columns, for a monitor turned upright or the side of a wide one.
  design('hud-column', 'HUD Column', 'An upright column of HUD brackets: one reading to a row, for an upright monitor or a screen edge', null, 1, 'hud', {}, (p) => [
    { source: 'time.clock' },
    { source: 'cpu.usage', visual: 'bar', label: 'CPU', warning: 85, critical: 95 },
    { source: gpu(p, 'usage'), visual: 'bar', label: 'GPU', warning: 97 },
    { source: 'memory.usage', visual: 'bar', label: 'RAM', warning: 85, critical: 95 },
    { source: 'cpu.temp', visual: 'thermo', max: 100, label: 'CPU temp', warning: 85, critical: 95 },
    { source: gpu(p, 'temp'), visual: 'thermo', max: 100, label: 'GPU temp', warning: 80, critical: 90 },
    { source: 'game.fps', visual: 'line', label: 'FPS' },
  ]),
  design('gauge-column', 'Gauge Column', 'Dials in teal bezels stacked upright, for an upright monitor or a screen edge', null, 1, 'teal', {}, (p) => [
    { source: 'cpu.usage', visual: 'arc', label: 'CPU', warning: 85, critical: 95 },
    { source: gpu(p, 'usage'), visual: 'arc', label: 'GPU', warning: 97 },
    { source: 'memory.usage', visual: 'ring', label: 'RAM', warning: 85, critical: 95 },
    { source: 'game.fps', visual: 'speed', max: 240, label: 'FPS' },
  ]),
  design('neon-column', 'Neon Column', 'Neon outlines stacked upright, two to a row: rings, temperatures and the network', null, 2, 'neon', { motion: 'none' }, (p) => [
    { source: 'cpu.usage', visual: 'ring', label: 'CPU', warning: 85, critical: 95 },
    { source: gpu(p, 'usage'), visual: 'ring', label: 'GPU', warning: 97 },
    { source: 'cpu.temp', visual: 'bar', max: 100, label: 'CPU temp', warning: 85, critical: 95, colSpan: 2 },
    { source: gpu(p, 'temp'), visual: 'bar', max: 100, label: 'GPU temp', warning: 80, critical: 90, colSpan: 2 },
    { source: p.first('net.', '.down') ?? '', visual: 'graph', label: 'Download', colSpan: 2 },
    { source: 'time.clock', colSpan: 2 },
  ]),
];
