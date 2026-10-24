import type { OverlayPanel, OverlaySource, OverlayStyle } from '@/lib/api/types';
import { full, picker, SCREEN_SIZES, whole, type ScreenDesign, type ScreenSize } from './screenKit';
import { MORE_SCREEN_DESIGNS } from './screenDesigns';
import { ART_SCREEN_DESIGNS, SCENE_SCREEN_DESIGNS } from './artDesigns';

export { SCREEN_SIZES, type ScreenDesign, type ScreenDesignPart, type ScreenFamily, type ScreenSize } from './screenKit';

/** Dashboards that fill a screen, one or more for each kind of screen. */
const FIRST_SCREEN_DESIGNS: ScreenDesign[] = [
  {
    id: 'racing-strip',
    name: 'Racing Strip',
    description: 'Three speedometers and segment thermometers on carbon, an orange edge',
    size: 'bar-88',
    build: (sources) => {
      const { gpu, take } = picker(sources);
      return [
        {
          rect: whole('bar-88'),
          panel: {
            title: null,
            layout: 'tiles',
            columns: 6,
            readings: take([
              { source: 'game.fps', visual: 'speed', max: 240, label: 'FPS', rowSpan: 2 },
              { source: gpu('usage'), visual: 'speed', label: 'GPU', warning: 97, rowSpan: 2 },
              { source: 'cpu.usage', visual: 'speed', label: 'CPU', warning: 90, rowSpan: 2 },
              { source: gpu('temp'), visual: 'thermo', max: 100, label: 'GPU temp', warning: 80, critical: 90, colSpan: 3 },
              { source: 'cpu.temp', visual: 'thermo', max: 100, label: 'CPU temp', warning: 85, critical: 95, colSpan: 2 },
              { source: 'game.low1', label: '1% low' },
            ]),
            style: full({
              font: 'Bahnschrift',
              size: 26,
              background: '#0A0A0A',
              background2: '#1C1917',
              gradientAngle: 180,
              padding: 18,
              accent: '#F97316',
              accentEdge: 'bottom',
              labelColor: '#A8A29E',
              valueColor: '#FAFAF9',
              unitColor: '#FDBA74',
              caps: true,
              icons: true,
              iconColor: '#FB923C',
              valueScale: 180,
              barStyle: 'segments',
              barHeight: 160,
              glow: 25,
              tileOpacity: 5,
              pattern: 'carbon',
              patternOpacity: 10,
              patternScale: 90,
            }),
          },
        },
      ];
    },
  },
  {
    id: 'aurora-bar',
    name: 'Aurora Glass Bar',
    description: 'Four rings across an indigo-to-teal sweep, a frame-rate graph and status lights',
    size: 'bar-88',
    build: (sources) => {
      const { gpu, take } = picker(sources);
      return [
        {
          rect: whole('bar-88'),
          panel: {
            title: null,
            layout: 'tiles',
            columns: 6,
            readings: take([
              { source: 'cpu.usage', visual: 'ring', warning: 85, critical: 95, rowSpan: 2 },
              { source: gpu('usage'), visual: 'ring', label: 'GPU', rowSpan: 2 },
              { source: 'memory.usage', visual: 'ring', warning: 85, critical: 95, rowSpan: 2 },
              { source: gpu('vram_pct'), visual: 'ring', label: 'VRAM', rowSpan: 2 },
              { source: 'game.fps', visual: 'graph', label: 'FPS', colSpan: 2 },
              { source: 'cpu.temp', visual: 'status', label: 'CPU temp', warning: 85, critical: 95 },
              { source: gpu('temp'), visual: 'status', label: 'GPU temp', warning: 80, critical: 90 },
            ]),
            style: full({
              size: 26,
              background: '#1E1B4B',
              background2: '#0E7490',
              gradientAngle: 120,
              padding: 18,
              sheen: true,
              accent: '#22D3EE',
              labelColor: '#C7D2FE',
              valueColor: '#FFFFFF',
              unitColor: '#A5B4FC',
              warningColor: '#FBBF24',
              criticalColor: '#FB7185',
              caps: true,
              icons: true,
              valueScale: 165,
              tileOpacity: 8,
            }),
          },
        },
      ];
    },
  },
  {
    id: 'neon-ribbon',
    name: 'Neon Ribbon',
    description: 'A glowing frame-rate graph on a synthwave grid, frame times and the ping beside it',
    size: 'bar-92',
    build: (sources) => {
      const { gpu, take } = picker(sources);
      return [
        {
          rect: whole('bar-92'),
          panel: {
            title: null,
            layout: 'tiles',
            columns: 6,
            readings: take([
              { source: 'game.fps', visual: 'graph', label: 'FPS', colSpan: 3, rowSpan: 2 },
              { source: 'game.frametime', visual: 'line', label: 'Frame time', colSpan: 2 },
              { source: 'net.ping.internet', label: 'Ping', warning: 60, critical: 120 },
              { source: gpu('usage'), visual: 'bar', label: 'GPU' },
              { source: 'cpu.usage', visual: 'bar', label: 'CPU' },
              { source: gpu('temp'), label: 'GPU temp', warning: 80, critical: 90 },
            ]),
            style: full({
              font: 'Consolas',
              size: 24,
              background: '#12002B',
              background2: '#2A0845',
              gradientAngle: 135,
              padding: 16,
              accent: '#FF2BD6',
              labelColor: '#F0ABFC',
              valueColor: '#67E8F9',
              unitColor: '#F0ABFC',
              caps: true,
              icons: true,
              iconColor: '#FF2BD6',
              glow: 60,
              valueScale: 175,
              tileOpacity: 4,
              pattern: 'grid',
              patternColor: '#FF2BD6',
              patternOpacity: 10,
              patternScale: 200,
            }),
          },
        },
      ];
    },
  },
  {
    id: 'command-deck',
    name: 'Command Deck',
    description: 'Arc dials, a wide frame-rate graph and a row of temperatures, power and traffic',
    size: 'bar-123',
    build: (sources) => {
      const { gpu, net, take } = picker(sources);
      return [
        {
          rect: whole('bar-123'),
          panel: {
            title: null,
            layout: 'tiles',
            columns: 6,
            readings: take([
              { source: 'cpu.usage', visual: 'arc', warning: 85, critical: 95, rowSpan: 2 },
              { source: gpu('usage'), visual: 'arc', label: 'GPU', rowSpan: 2 },
              { source: 'memory.usage', visual: 'arc', warning: 85, critical: 95, rowSpan: 2 },
              { source: 'game.fps', visual: 'graph', label: 'FPS', colSpan: 3, rowSpan: 2 },
              { source: 'cpu.temp', visual: 'thermo', max: 100, label: 'CPU temp', warning: 85, critical: 95 },
              { source: gpu('temp'), visual: 'thermo', max: 100, label: 'GPU temp', warning: 80, critical: 90 },
              { source: gpu('power'), label: 'GPU power' },
              { source: net('down'), visual: 'columns', label: 'Download' },
              { source: 'net.ping.internet', visual: 'trend', label: 'Ping' },
              { source: 'time.clock' },
            ]),
            style: full({
              size: 26,
              background: '#0F1115',
              background2: '#1A1D24',
              gradientAngle: 160,
              padding: 18,
              accent: '#60A5FA',
              labelColor: '#9CA3AF',
              valueColor: '#F3F4F6',
              unitColor: '#9CA3AF',
              caps: true,
              icons: true,
              iconColor: '#60A5FA',
              valueScale: 165,
              tileOpacity: 5,
              pattern: 'dots',
              patternOpacity: 6,
              patternScale: 120,
            }),
          },
        },
      ];
    },
  },
  {
    id: 'pump-dial',
    name: 'Pump Dial',
    description: 'One large processor-temperature dial in mint, made for a round pump screen',
    size: 'square-480',
    build: (sources) => {
      const { gpu, take } = picker(sources);
      return [
        {
          rect: whole('square-480'),
          panel: {
            title: null,
            layout: 'tiles',
            columns: 2,
            readings: take([
              { source: 'cpu.temp', visual: 'arc', max: 100, label: 'CPU', warning: 85, critical: 95, colSpan: 2, rowSpan: 2 },
              { source: gpu('temp'), label: 'GPU', warning: 80, critical: 90 },
              { source: 'cpu.usage', label: 'Load' },
            ]),
            style: full({
              size: 20,
              background: '#050505',
              background2: '#111827',
              gradientAngle: 180,
              // Room for the round screen's cut corners.
              padding: 40,
              accent: '#34D399',
              labelColor: '#9CA3AF',
              valueColor: '#F9FAFB',
              unitColor: '#6EE7B7',
              caps: true,
              glow: 30,
              valueScale: 170,
              tileOpacity: 0,
            }),
          },
        },
      ];
    },
  },
  {
    id: 'quad-rings',
    name: 'Quad Rings',
    description: 'Four rose-gold rings: load and temperature of the processor and graphics card',
    size: 'square-400',
    build: (sources) => {
      const { gpu, take } = picker(sources);
      return [
        {
          rect: whole('square-400'),
          panel: {
            title: null,
            layout: 'tiles',
            columns: 2,
            readings: take([
              { source: 'cpu.usage', visual: 'ring', label: 'CPU' },
              { source: gpu('usage'), visual: 'ring', label: 'GPU' },
              { source: 'cpu.temp', visual: 'ring', max: 100, label: 'CPU °', warning: 85, critical: 95 },
              { source: gpu('temp'), visual: 'ring', max: 100, label: 'GPU °', warning: 80, critical: 90 },
            ]),
            style: full({
              size: 16,
              background: '#1A0F14',
              background2: '#3B1D2A',
              gradientAngle: 160,
              padding: 26,
              accent: '#F9A8D4',
              labelColor: '#FBCFE8',
              valueColor: '#FFF1F2',
              unitColor: '#F9A8D4',
              caps: true,
              valueScale: 130,
              tileOpacity: 6,
              tileColor: '#F9A8D4',
            }),
          },
        },
      ];
    },
  },
  {
    id: 'pocket-stats',
    name: 'Pocket Stats',
    description: 'A quiet light card: four meters and two temperatures in six tiles',
    size: 'small-35',
    build: (sources) => {
      const { gpu, take } = picker(sources);
      return [
        {
          rect: whole('small-35'),
          panel: {
            title: null,
            layout: 'tiles',
            columns: 2,
            readings: take([
              { source: 'cpu.usage', visual: 'bar', label: 'CPU' },
              { source: gpu('usage'), visual: 'bar', label: 'GPU' },
              { source: 'memory.usage', visual: 'bar', label: 'RAM' },
              { source: gpu('vram_pct'), visual: 'bar', label: 'VRAM' },
              { source: 'cpu.temp', label: 'CPU temp', warning: 85, critical: 95 },
              { source: gpu('temp'), label: 'GPU temp', warning: 80, critical: 90 },
            ]),
            style: full({
              size: 15,
              caps: true,
              background: '#F5F5F4',
              background2: '#E7E5E4',
              gradientAngle: 180,
              padding: 16,
              accent: '#0284C7',
              labelColor: '#57534E',
              valueColor: '#1C1917',
              unitColor: '#78716C',
              warningColor: '#B45309',
              criticalColor: '#B91C1C',
              okColor: '#047857',
              icons: true,
              iconColor: '#0284C7',
              valueScale: 150,
              tileOpacity: 5,
            }),
          },
        },
      ];
    },
  },
  {
    id: 'ocean-tower',
    name: 'Ocean Tower',
    description: 'An upright column of rings, thermometers, memory and the time over deep blue',
    size: 'small-35-portrait',
    build: (sources) => {
      const { gpu, take } = picker(sources);
      return [
        {
          rect: whole('small-35-portrait'),
          panel: {
            title: null,
            layout: 'tiles',
            columns: 2,
            readings: take([
              { source: 'cpu.usage', visual: 'ring', label: 'CPU' },
              { source: gpu('usage'), visual: 'ring', label: 'GPU' },
              { source: 'cpu.temp', visual: 'thermo', max: 100, label: 'CPU temp', warning: 85, critical: 95 },
              { source: gpu('temp'), visual: 'thermo', max: 100, label: 'GPU temp', warning: 80, critical: 90 },
              { source: 'memory.usage', visual: 'bar', label: 'RAM' },
              { source: 'time.clock' },
            ]),
            style: full({
              size: 15,
              background: '#021526',
              background2: '#03346E',
              gradientAngle: 180,
              padding: 14,
              accent: '#38BDF8',
              labelColor: '#93C5FD',
              valueColor: '#F0F9FF',
              unitColor: '#7DD3FC',
              caps: true,
              icons: true,
              valueScale: 130,
              tileOpacity: 7,
            }),
          },
        },
      ];
    },
  },
  {
    id: 'midnight-grid',
    name: 'Midnight Grid',
    description: 'Six tiles in indigo: arc dials, a ring, status lights and the ping as a line',
    size: 'small-5',
    build: (sources) => {
      const { gpu, take } = picker(sources);
      return [
        {
          rect: whole('small-5'),
          panel: {
            title: null,
            layout: 'tiles',
            columns: 3,
            readings: take([
              { source: 'cpu.usage', visual: 'arc', warning: 85, critical: 95 },
              { source: gpu('usage'), visual: 'arc', label: 'GPU' },
              { source: 'memory.usage', visual: 'ring', warning: 85, critical: 95 },
              { source: 'cpu.temp', visual: 'status', label: 'CPU temp', warning: 85, critical: 95 },
              { source: gpu('temp'), visual: 'status', label: 'GPU temp', warning: 80, critical: 90 },
              { source: 'net.ping.internet', visual: 'line', label: 'Ping' },
            ]),
            style: full({
              size: 18,
              background: '#0B1020',
              background2: '#111936',
              gradientAngle: 150,
              padding: 18,
              sheen: true,
              accent: '#818CF8',
              labelColor: '#A5B4FC',
              valueColor: '#EEF2FF',
              unitColor: '#A5B4FC',
              caps: true,
              icons: true,
              valueScale: 160,
              tileOpacity: 6,
            }),
          },
        },
      ];
    },
  },
  {
    id: 'studio-seven',
    name: 'Studio Seven',
    description: 'Graphite and gold: a frame-rate graph, rings, thermometers and meters',
    size: 'tablet-7',
    build: (sources) => {
      const { gpu, take } = picker(sources);
      return [
        {
          rect: whole('tablet-7'),
          panel: {
            title: null,
            layout: 'tiles',
            columns: 4,
            readings: take([
              { source: 'game.fps', visual: 'graph', label: 'FPS', colSpan: 2, rowSpan: 2 },
              { source: gpu('usage'), visual: 'ring', label: 'GPU' },
              { source: 'cpu.usage', visual: 'ring' },
              { source: gpu('temp'), visual: 'thermo', max: 100, label: 'GPU temp', warning: 80, critical: 90 },
              { source: 'cpu.temp', visual: 'thermo', max: 100, label: 'CPU temp', warning: 85, critical: 95 },
              { source: 'memory.usage', visual: 'bar', label: 'RAM' },
              { source: gpu('vram_pct'), visual: 'bar', label: 'VRAM' },
              { source: 'net.ping.internet', label: 'Ping' },
              { source: 'time.clock' },
            ]),
            style: full({
              size: 18,
              background: '#111111',
              background2: '#1C1A17',
              gradientAngle: 160,
              padding: 18,
              accent: '#D4AF37',
              labelColor: '#BFB6A0',
              valueColor: '#FAF7EF',
              unitColor: '#D4AF37',
              caps: true,
              icons: true,
              iconColor: '#D4AF37',
              glow: 10,
              valueScale: 160,
              tileOpacity: 5,
            }),
          },
        },
      ];
    },
  },
  {
    id: 'paddock',
    name: 'Paddock Telemetry',
    description: 'Race-red speedometers, the frame time as a line and segment meters on carbon',
    size: 'tablet-10',
    build: (sources) => {
      const { gpu, take } = picker(sources);
      return [
        {
          rect: whole('tablet-10'),
          panel: {
            title: null,
            layout: 'tiles',
            columns: 4,
            readings: take([
              { source: 'game.fps', visual: 'speed', max: 240, label: 'FPS', rowSpan: 2 },
              { source: gpu('usage'), visual: 'speed', label: 'GPU', warning: 97, rowSpan: 2 },
              { source: 'game.frametime', visual: 'line', label: 'Frame time', colSpan: 2 },
              { source: 'game.low1', label: '1% low' },
              { source: 'net.ping.internet', label: 'Ping', warning: 60, critical: 120 },
              { source: 'cpu.temp', visual: 'thermo', max: 100, label: 'CPU temp', warning: 85, critical: 95 },
              { source: gpu('temp'), visual: 'thermo', max: 100, label: 'GPU temp', warning: 80, critical: 90 },
              { source: 'cpu.max_core', visual: 'bar', label: 'Busiest core', warning: 95 },
              { source: gpu('power'), label: 'GPU power' },
            ]),
            style: full({
              font: 'Bahnschrift',
              size: 22,
              background: '#0D0D0D',
              background2: '#18181B',
              gradientAngle: 180,
              padding: 22,
              accent: '#E11D48',
              accentEdge: 'left',
              labelColor: '#A1A1AA',
              valueColor: '#FAFAFA',
              unitColor: '#FDA4AF',
              caps: true,
              icons: true,
              iconColor: '#FB7185',
              valueScale: 170,
              barStyle: 'segments',
              barHeight: 140,
              tileOpacity: 5,
              pattern: 'carbon',
              patternOpacity: 9,
            }),
          },
        },
      ];
    },
  },
  {
    id: 'mission-control',
    name: 'Mission Control',
    description: 'A full dashboard for a portable monitor: a header line and sixteen readings below it',
    size: 'portable-fhd',
    build: (sources) => {
      const { gpu, net, take } = picker(sources);
      const look = {
        size: 24,
        background: '#070B14',
        background2: '#0F1B33',
        gradientAngle: 160,
        accent: '#38BDF8',
        labelColor: '#94A3B8',
        valueColor: '#F8FAFC',
        unitColor: '#7DD3FC',
        caps: true,
        icons: true,
      } satisfies Partial<OverlayStyle>;
      return [
        {
          rect: { x: 0, y: 0, width: 1920, height: 96 },
          panel: {
            title: null,
            layout: 'line',
            readings: take([{ source: 'time.clock' }, { source: 'time.date' }, { source: 'game.app', label: 'App' }, { source: 'net.ping.internet', label: 'Ping' }, { source: 'recorder.state' }]),
            style: full({ ...look, background2: null, padding: 18, accentEdge: 'bottom', valueScale: 140 }),
          },
        },
        {
          rect: { x: 0, y: 96, width: 1920, height: 984 },
          panel: {
            title: null,
            layout: 'tiles',
            columns: 6,
            readings: take([
              { source: 'game.fps', visual: 'graph', label: 'FPS', colSpan: 3, rowSpan: 2 },
              { source: 'cpu.usage', visual: 'arc', warning: 85, critical: 95 },
              { source: gpu('usage'), visual: 'arc', label: 'GPU' },
              { source: 'memory.usage', visual: 'ring', warning: 85, critical: 95 },
              { source: 'cpu.temp', visual: 'thermo', max: 100, label: 'CPU temp', warning: 85, critical: 95 },
              { source: gpu('temp'), visual: 'thermo', max: 100, label: 'GPU temp', warning: 80, critical: 90 },
              { source: gpu('hotspot'), visual: 'status', label: 'Hot spot', warning: 95, critical: 105 },
              { source: 'game.frametime', visual: 'line', label: 'Frame time', colSpan: 2 },
              { source: 'game.low1', label: '1% low' },
              { source: gpu('vram'), visual: 'bar', show: 'of', label: 'VRAM' },
              { source: 'cpu.power', label: 'CPU power' },
              { source: gpu('power'), label: 'GPU power' },
              { source: net('down'), visual: 'columns', label: 'Download', colSpan: 2 },
              { source: net('up'), visual: 'columns', label: 'Upload', colSpan: 2 },
              { source: 'net.ping.router', visual: 'line', label: 'Router' },
              { source: 'net.ping.internet', visual: 'trend', label: 'Internet' },
            ]),
            style: full({ ...look, size: 26, padding: 22, valueScale: 150, tileOpacity: 6 }),
          },
        },
      ];
    },
  },
  {
    id: 'aurora-drift-bar',
    name: 'Aurora Drift Bar',
    description: 'Moving: rings and a graph across the bar over drifting aurora light',
    size: 'bar-88',
    build: (sources) => {
      const { gpu, take } = picker(sources);
      return [
        {
          rect: whole('bar-88'),
          panel: {
            title: null,
            layout: 'tiles',
            columns: 6,
            readings: take([
              { source: 'cpu.usage', visual: 'ring', warning: 85, critical: 95, rowSpan: 2 },
              { source: gpu('usage'), visual: 'ring', label: 'GPU', rowSpan: 2 },
              { source: 'memory.usage', visual: 'ring', warning: 85, critical: 95, rowSpan: 2 },
              { source: 'game.fps', visual: 'graph', label: 'FPS', colSpan: 3 },
              { source: 'cpu.temp', label: 'CPU temp', warning: 85, critical: 95 },
              { source: gpu('temp'), label: 'GPU temp', warning: 80, critical: 90 },
              { source: 'time.clock' },
            ]),
            style: full({
              size: 26,
              background: '#14123A',
              background2: '#7C3AED',
              gradientAngle: 120,
              padding: 18,
              accent: '#22D3EE',
              labelColor: '#C7D2FE',
              valueColor: '#FFFFFF',
              unitColor: '#A5B4FC',
              caps: true,
              icons: true,
              valueScale: 165,
              tileOpacity: 6,
              motion: 'aurora',
            }),
          },
        },
      ];
    },
  },
  {
    id: 'starfield-command',
    name: 'Starfield Command',
    description: 'Moving: a full dashboard for a portable monitor over a drifting star field',
    size: 'portable-fhd',
    build: (sources) => {
      const { gpu, net, take } = picker(sources);
      return [
        {
          rect: whole('portable-fhd'),
          panel: {
            title: null,
            layout: 'tiles',
            columns: 4,
            readings: take([
              { source: 'game.fps', visual: 'graph', label: 'FPS', colSpan: 2, rowSpan: 2 },
              { source: 'cpu.usage', visual: 'arc', warning: 85, critical: 95 },
              { source: gpu('usage'), visual: 'arc', label: 'GPU' },
              { source: 'cpu.temp', visual: 'thermo', max: 100, label: 'CPU temp', warning: 85, critical: 95 },
              { source: gpu('temp'), visual: 'thermo', max: 100, label: 'GPU temp', warning: 80, critical: 90 },
              { source: 'memory.usage', visual: 'ring', warning: 85, critical: 95 },
              { source: gpu('vram_pct'), visual: 'ring', label: 'VRAM' },
              { source: net('down'), visual: 'columns', label: 'Download' },
              { source: 'time.clock' },
            ]),
            style: full({
              size: 28,
              background: '#050816',
              background2: '#111C3A',
              gradientAngle: 170,
              padding: 24,
              accent: '#A5B4FC',
              labelColor: '#C7D2FE',
              valueColor: '#FFFFFF',
              unitColor: '#A5B4FC',
              caps: true,
              icons: true,
              iconColor: '#A5B4FC',
              valueScale: 150,
              tileOpacity: 6,
              motion: 'stars',
              motionSpeed: 60,
            }),
          },
        },
      ];
    },
  },
];

/** The size a design is made for. */
/** Dashboards that fill a screen: those with their own pictures first (Phase 47), then the first ones and those with backdrops (Phase 46). */
export const SCREEN_DESIGNS: ScreenDesign[] = [...SCENE_SCREEN_DESIGNS, ...ART_SCREEN_DESIGNS, ...FIRST_SCREEN_DESIGNS, ...MORE_SCREEN_DESIGNS];

export const sizeOf = (design: ScreenDesign): ScreenSize => SCREEN_SIZES.find((s) => s.id === design.size)!;

const clamp = (value: number, low: number, high: number) => Math.min(high, Math.max(low, value));

/**
 * A design's panels fitted to a screen (its size at 100 % scaling): each panel keeps its share of the screen, and its
 * text, padding and corners grow or shrink by the smaller of the two ratios, so its readings keep fitting.
 */
export function fitScreenDesign(design: ScreenDesign, sources: readonly OverlaySource[], target: { width: number; height: number }): Omit<OverlayPanel, 'id'>[] {
  const made = sizeOf(design);
  const sx = target.width / made.width;
  const sy = target.height / made.height;
  const k = Math.min(sx, sy);
  return design
    .build(sources)
    .filter((part) => part.panel.readings.length > 0)
    .map(({ rect, panel }) => {
      const width = clamp(Math.round(rect.width * sx), 40, 4000);
      const height = clamp(Math.round(rect.height * sy), 20, 3000);
      // Its place as a share of the room it leaves on the screen (as every panel's).
      const share = (at: number, room: number) => (room <= 0 ? 0 : clamp(at / room, 0, 1));
      const s = panel.style;
      return {
        ...panel,
        width,
        height,
        x: share(rect.x * sx, target.width - width),
        y: share(rect.y * sy, target.height - height),
        style: {
          ...s,
          size: clamp(Math.round(s.size * k * 2) / 2, 6, 72),
          padding: clamp(Math.round(s.padding * k), 0, 40),
          cornerRadius: clamp(Math.round(s.cornerRadius * k), 0, 32),
        },
      };
    });
}

/** How far a screen's shape is from a design's (0: the same shape), to show the closest first. */
export function shapeDistance(design: ScreenDesign, target: { width: number; height: number }): number {
  const made = sizeOf(design);
  return Math.abs(Math.log(made.width / made.height / (target.width / Math.max(1, target.height))));
}

/** A screen's size for its panels: its pixels at 100 % scaling (a USB screen's zoom is its scaling). */
export const logicalSize = (display: { width: number; height: number; scale: number }) => ({
  width: Math.round(display.width / Math.max(0.1, display.scale)),
  height: Math.round(display.height / Math.max(0.1, display.scale)),
});
