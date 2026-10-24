import type { OverlayArt, OverlayGeneratedArt, OverlayReading, OverlaySource, OverlayStyle } from '@/lib/api/types';
import { picker as panelPicker, style } from './designKit';
import type { PanelDesign } from './designs';
import { full, picker, whole, type ScreenDesign } from './screenKit';

type Pick = ReturnType<typeof picker>;

/** The text and colors that go with each art (the art itself is drawn from the panel: see art.ts). */
const TEXT: Record<OverlayArt, Partial<OverlayStyle>> = {
  cockpit: { font: 'Bahnschrift', background: '#020814', accent: '#22D3EE', labelColor: '#7DD3FC', valueColor: '#E6FBFF', unitColor: '#67E8F9', caps: true, icons: true, iconColor: '#22D3EE', glow: 25 },
  racing: { font: 'Bahnschrift', background: '#0A0A0A', accent: '#EF4444', labelColor: '#A3A3A3', valueColor: '#FAFAFA', unitColor: '#FCA5A5', caps: true, icons: true, iconColor: '#F87171', barStyle: 'segments' },
  aviation: { font: 'Segoe UI Semibold', background: '#101113', accent: '#FBBF24', labelColor: '#FBBF24', valueColor: '#FFFFFF', unitColor: '#D4D4D8', caps: true },
  holo: { font: 'Bahnschrift', background: '#01161C', accent: '#2DD4BF', labelColor: '#99F6E4', valueColor: '#F0FDFA', unitColor: '#E879F9', caps: true, icons: true, iconColor: '#E879F9', glow: 30 },
  synthwave: { font: 'Bahnschrift', background: '#12002B', accent: '#FF2E97', labelColor: '#F0ABFC', valueColor: '#FFF1FB', unitColor: '#22D3EE', caps: true, icons: true, iconColor: '#22D3EE', glow: 35 },
  brass: { font: 'Segoe UI Semibold', background: '#140D08', accent: '#D4A24C', labelColor: '#E8D3A3', valueColor: '#FFF8E7', unitColor: '#F5D58A', caps: true },
  terminal: { font: 'Consolas', background: '#010A03', accent: '#22C55E', labelColor: '#4ADE80', valueColor: '#DCFCE7', unitColor: '#86EFAC', caps: true, glow: 30, barStyle: 'segments' },
  lux: { font: 'Segoe UI', background: '#08080B', accent: '#C9A227', labelColor: '#CDBB86', valueColor: '#FBF6E6', unitColor: '#E7D9A8', caps: true },
  // Generated scenes: light text on frosted glass, quiet labels and the scene's own accent on bars and dials.
  silk: { font: 'Segoe UI', background: '#0A0614', accent: '#F7C59F', labelColor: '#F3D8E4', valueColor: '#FFFFFF', unitColor: '#F7C59F', caps: true },
  mesh: { font: 'Segoe UI', background: '#070B1E', accent: '#C4B5FD', labelColor: '#DDD6FE', valueColor: '#FFFFFF', unitColor: '#A5F3FC', caps: true },
  aurora: { font: 'Segoe UI', background: '#01030A', accent: '#5EEAD4', labelColor: '#CCFBF1', valueColor: '#FFFFFF', unitColor: '#C4B5FD', caps: true },
  nebula: { font: 'Segoe UI', background: '#02030C', accent: '#FDBA74', labelColor: '#FDE6D2', valueColor: '#FFFFFF', unitColor: '#FDBA74', caps: true },
  caustics: { font: 'Segoe UI', background: '#011422', accent: '#7FF5EE', labelColor: '#CFFAFE', valueColor: '#FFFFFF', unitColor: '#7FF5EE', caps: true },
  bokeh: { font: 'Segoe UI', background: '#06070D', accent: '#FBBF24', labelColor: '#FEF3C7', valueColor: '#FFFFFF', unitColor: '#FDA4AF', caps: true },
  topo: { font: 'Bahnschrift', background: '#03070F', accent: '#5EEAD4', labelColor: '#BAE6FD', valueColor: '#F0FDFA', unitColor: '#5EEAD4', caps: true },
  prism: { font: 'Segoe UI', background: '#05050A', accent: '#E0E7FF', labelColor: '#E0E7FF', valueColor: '#FFFFFF', unitColor: '#A5F3FC', caps: true },
  ocean: { font: 'Segoe UI', background: '#030714', accent: '#FFB27A', labelColor: '#FFE4CC', valueColor: '#FFFFFF', unitColor: '#FFB27A', caps: true },
  lava: { font: 'Segoe UI', background: '#12030A', accent: '#FFA94D', labelColor: '#FFE0C2', valueColor: '#FFFFFF', unitColor: '#FFD08A', caps: true },
  ink: { font: 'Segoe UI', background: '#05070D', accent: '#A5B4FC', labelColor: '#E0E7FF', valueColor: '#FFFFFF', unitColor: '#C4B5FD', caps: true },
  marble: { font: 'Segoe UI', background: '#050505', accent: '#E7C77B', labelColor: '#E9DDBF', valueColor: '#FFFDF5', unitColor: '#E7C77B', caps: true },
  foil: { font: 'Segoe UI', background: '#07060D', accent: '#C4F1F9', labelColor: '#E0F2FE', valueColor: '#FFFFFF', unitColor: '#F5D0FE', caps: true },
  fireflies: { font: 'Segoe UI', background: '#020604', accent: '#D9F99D', labelColor: '#ECFCCB', valueColor: '#FFFFFF', unitColor: '#FDE68A', caps: true },
  warp: { font: 'Bahnschrift', background: '#01020A', accent: '#7DD3FC', labelColor: '#E0F2FE', valueColor: '#FFFFFF', unitColor: '#C4B5FD', caps: true },
  galaxy: { font: 'Segoe UI', background: '#02020A', accent: '#C7D2FE', labelColor: '#E0E7FF', valueColor: '#FFFFFF', unitColor: '#F0ABFC', caps: true },
  clouds: { font: 'Segoe UI', background: '#0B1023', accent: '#FBCFE8', labelColor: '#FDF2E9', valueColor: '#FFFFFF', unitColor: '#FBCFE8', caps: true },
  snow: { font: 'Segoe UI', background: '#050A16', accent: '#BFDBFE', labelColor: '#E0F2FE', valueColor: '#FFFFFF', unitColor: '#BFDBFE', caps: true },
  ribbons: { font: 'Segoe UI', background: '#070712', accent: '#F9A8D4', labelColor: '#FCE7F3', valueColor: '#FFFFFF', unitColor: '#A5F3FC', caps: true },
  circuit: { font: 'Bahnschrift', background: '#02060A', accent: '#67E8F9', labelColor: '#CFFAFE', valueColor: '#ECFEFF', unitColor: '#67E8F9', caps: true },
  glass: { font: 'Segoe UI', background: '#0B0F17', accent: '#FFFFFF', labelColor: '#E2E8F0', valueColor: '#FFFFFF', unitColor: '#CBD5E1', caps: true },
};

/** A look whose picture is drawn from the panel's own layout by its art (none drawn yet: the editor draws it). */
const look = (art: OverlayArt, text: Partial<OverlayStyle>): Partial<OverlayStyle> => ({
  ...TEXT[art],
  backgroundOpacity: 100,
  art,
  image: null,
  imageFit: 'stretch',
  imageOpacity: 100,
  imageBlur: 0,
  imageTint: null,
  tileOpacity: 0,
  backdrop: 'none',
  pattern: null,
  motion: 'none',
  ...text,
});

// Readings the layouts use.
const cpu = (x: Partial<OverlayReading> = {}): OverlayReading => ({ source: 'cpu.usage', label: 'CPU', warning: 85, critical: 95, ...x });
const gpuUse = (p: Pick, x: Partial<OverlayReading> = {}): OverlayReading => ({ source: p.gpu('usage'), label: 'GPU', warning: 90, critical: 98, ...x });
const ram = (x: Partial<OverlayReading> = {}): OverlayReading => ({ source: 'memory.usage', label: 'RAM', warning: 85, critical: 95, ...x });
const vram = (p: Pick, x: Partial<OverlayReading> = {}): OverlayReading => ({ source: p.gpu('vram'), label: 'VRAM', show: 'percent', warning: 90, ...x });
const cpuTemp = (x: Partial<OverlayReading> = {}): OverlayReading => ({ source: 'cpu.temp', label: 'CPU temp', max: 100, warning: 85, critical: 95, ...x });
const gpuTemp = (p: Pick, x: Partial<OverlayReading> = {}): OverlayReading => ({ source: p.gpu('temp'), label: 'GPU temp', max: 100, warning: 80, critical: 90, ...x });
const fps = (x: Partial<OverlayReading> = {}): OverlayReading => ({ source: 'game.fps', label: 'FPS', max: 240, ...x });
const ping = (x: Partial<OverlayReading> = {}): OverlayReading => ({ source: 'net.ping.internet', label: 'Ping', warning: 80, critical: 150, ...x });
const down = (p: Pick, x: Partial<OverlayReading> = {}): OverlayReading => ({ source: p.net('down'), label: 'Download', ...x });
const clock = (x: Partial<OverlayReading> = {}): OverlayReading => ({ source: 'time.clock', ...x });

type Layout = (p: Pick) => (OverlayReading | false | null | undefined)[];

/** Layouts for an upright 8.8″ (480 × 1920), two columns, built around dials so the art has faces to draw. */
const TOWERS: Record<string, Layout> = {
  gauges: (p) => [clock({ colSpan: 2 }), cpu({ visual: 'ring' }), gpuUse(p, { visual: 'ring' }), fps({ visual: 'arc', colSpan: 2, rowSpan: 2 }), cpuTemp({ visual: 'thermo', colSpan: 2 }), gpuTemp(p, { visual: 'thermo', colSpan: 2 }), ram({ visual: 'bar' }), vram(p, { visual: 'bar' }), ping({ visual: 'line', colSpan: 2 })],
  speed: (p) => [fps({ visual: 'speed', colSpan: 2, rowSpan: 2 }), gpuUse(p, { visual: 'speed' }), cpu({ visual: 'speed' }), gpuTemp(p, { visual: 'arc' }), cpuTemp({ visual: 'arc' }), { source: 'game.low1', label: '1% low' }, { source: 'game.frametime', label: 'Frame time' }, vram(p, { visual: 'bar', colSpan: 2 }), clock({ colSpan: 2 })],
  dials: (p) => [cpu({ visual: 'arc', colSpan: 2, rowSpan: 2 }), gpuUse(p, { visual: 'arc', colSpan: 2, rowSpan: 2 }), ram({ visual: 'speed' }), vram(p, { visual: 'arc' }), cpuTemp({ visual: 'status' }), gpuTemp(p, { visual: 'status' }), clock({ colSpan: 2 })],
  mixed: (p) => [clock({ colSpan: 2 }), cpu({ visual: 'arc' }), gpuUse(p, { visual: 'arc' }), fps({ visual: 'graph', colSpan: 2, rowSpan: 2 }), cpuTemp({ visual: 'bar', colSpan: 2 }), gpuTemp(p, { visual: 'bar', colSpan: 2 }), ram({ visual: 'ring' }), ping({ visual: 'ring', max: 150 }), down(p, { visual: 'columns', colSpan: 2 })],
};

/** Layouts for an 8.8″ bar (1920 × 480), six columns over two rows. */
const BARS: Record<string, Layout> = {
  cluster: (p) => [cpu({ visual: 'arc', rowSpan: 2 }), cpuTemp({ visual: 'arc', rowSpan: 2 }), fps({ visual: 'speed', colSpan: 2, rowSpan: 2 }), gpuTemp(p, { visual: 'arc', rowSpan: 2 }), gpuUse(p, { visual: 'arc', rowSpan: 2 })],
  rings: (p) => [cpu({ visual: 'ring', rowSpan: 2 }), gpuUse(p, { visual: 'ring', rowSpan: 2 }), fps({ visual: 'graph', colSpan: 2, rowSpan: 2 }), ram({ visual: 'ring', rowSpan: 2 }), vram(p, { visual: 'ring', rowSpan: 2 })],
  gauges: (p) => [fps({ visual: 'speed', rowSpan: 2 }), gpuUse(p, { visual: 'speed', rowSpan: 2 }), cpu({ visual: 'speed', rowSpan: 2 }), gpuTemp(p, { visual: 'thermo', colSpan: 2 }), clock({ rowSpan: 2 }), cpuTemp({ visual: 'thermo', colSpan: 2 })],
  scope: (p) => [cpu({ visual: 'arc', rowSpan: 2 }), gpuUse(p, { visual: 'arc', rowSpan: 2 }), fps({ visual: 'line', colSpan: 2 }), cpuTemp({ visual: 'bar' }), gpuTemp(p, { visual: 'bar' }), { source: 'game.frametime', label: 'Frame time', visual: 'graph', colSpan: 2 }, ram({ visual: 'bar' }), clock()],
};

/** A whole-screen design of one panel whose picture is drawn by its art from the panel's layout. */
function screen(id: string, name: string, description: string, size: string, columns: number, art: OverlayArt, text: Partial<OverlayStyle>, layout: Layout): ScreenDesign {
  return {
    id,
    name,
    description,
    size,
    build: (sources: readonly OverlaySource[]) => {
      const p = picker(sources);
      return [{ rect: whole(size), panel: { title: null, layout: 'tiles', columns, readings: p.take(layout(p).filter((r): r is OverlayReading => !!r && !!r.source)), style: full(look(art, text)) } }];
    },
  };
}

const TOWER = { size: 25, padding: 18, valueScale: 175 } satisfies Partial<OverlayStyle>;
const BAR = { size: 24, padding: 18, valueScale: 160 } satisfies Partial<OverlayStyle>;

/** Designs with a picture drawn for them around their readings: gauge faces, scope grids, frames and controls (Phase 47). */
export const ART_SCREEN_DESIGNS: ScreenDesign[] = [
  // ---- 8.8″ upright: one for every art ----
  screen('art-tower-cockpit', 'Starship Cockpit Tower', 'Upright, with its own picture: glowing notched frames, gauge faces with ticks and numbers behind each dial, circuit traces and LEDs', 'bar-88-portrait', 2, 'cockpit', TOWER, TOWERS.gauges!),
  screen('art-tower-racing', 'Carbon Dash Tower', 'Upright, with its own picture: carbon fibre, beveled plates, speedometer faces with red zones, toggle switches', 'bar-88-portrait', 2, 'racing', TOWER, TOWERS.speed!),
  screen('art-tower-aviation', 'Flight Deck Tower', 'Upright, with its own picture: matte instruments with screws, white ticks and numbers, red arcs, amber labels', 'bar-88-portrait', 2, 'aviation', TOWER, TOWERS.dials!),
  screen('art-tower-holo', 'Hologram Tower', 'Upright, with its own picture: a hexagon field, dashed hologram frames with magenta brackets and scanning rings', 'bar-88-portrait', 2, 'holo', TOWER, TOWERS.mixed!),
  screen('art-tower-synthwave', 'Sunset Tower', 'Upright, with its own picture: a banded sun over a neon grid floor, stars, neon frames', 'bar-88-portrait', 2, 'synthwave', TOWER, TOWERS.gauges!),
  screen('art-tower-brass', 'Brass Chronometer Tower', 'Upright, with its own picture: brass-rimmed gauges with guilloché faces, riveted frames, red zones', 'bar-88-portrait', 2, 'brass', TOWER, TOWERS.dials!),
  screen('art-tower-terminal', 'Terminal Tower', 'Upright, with its own picture: a green CRT with scan lines, framed windows, scope grids and LEDs', 'bar-88-portrait', 2, 'terminal', { ...TOWER, size: 23 }, TOWERS.mixed!),
  screen('art-tower-lux', 'Midnight Gold Tower', 'Upright, with its own picture: gold pinstripes, gold-framed tiles and watch-like guilloché dials', 'bar-88-portrait', 2, 'lux', TOWER, TOWERS.speed!),

  // ---- 8.8″ wide: one for every art ----
  screen('art-bar-cockpit', 'Starship Cockpit Bar', 'With its own picture: an instrument cluster in glowing notched frames, gauge faces, traces and LEDs', 'bar-88', 6, 'cockpit', BAR, BARS.cluster!),
  screen('art-bar-racing', 'Carbon Dash Bar', 'With its own picture: speedometer faces with red zones on carbon fibre, beveled plates and switches', 'bar-88', 6, 'racing', BAR, BARS.gauges!),
  screen('art-bar-aviation', 'Flight Deck Bar', 'With its own picture: a row of aircraft instruments with screws, ticks, numbers and red arcs', 'bar-88', 6, 'aviation', BAR, BARS.cluster!),
  screen('art-bar-holo', 'Hologram Bar', 'With its own picture: rings in hologram frames over a hexagon field, a scope for the frame rate', 'bar-88', 6, 'holo', BAR, BARS.rings!),
  screen('art-bar-synthwave', 'Sunset Bar', 'With its own picture: a banded sun and a neon grid floor behind rings and the frame rate', 'bar-88', 6, 'synthwave', BAR, BARS.rings!),
  screen('art-bar-brass', 'Brass Chronometer Bar', 'With its own picture: brass-rimmed gauges with guilloché faces in riveted frames', 'bar-88', 6, 'brass', BAR, BARS.cluster!),
  screen('art-bar-terminal', 'Terminal Bar', 'With its own picture: a green CRT with framed windows, scope grids and bar scales', 'bar-88', 6, 'terminal', { ...BAR, size: 22 }, BARS.scope!),
  screen('art-bar-lux', 'Midnight Gold Bar', 'With its own picture: gold-framed speedometers on gold pinstripes', 'bar-88', 6, 'lux', BAR, BARS.gauges!),

  // ---- Other screens ----
  screen('art-portable-cockpit', 'Starship Bridge', 'A portable monitor with its own picture: a bridge console of gauge faces, scopes, traces and LEDs', 'portable-fhd', 6, 'cockpit', { size: 24, padding: 22, valueScale: 150 }, (p) => [
    cpu({ visual: 'arc', rowSpan: 2 }),
    gpuUse(p, { visual: 'arc', rowSpan: 2 }),
    fps({ visual: 'speed', colSpan: 2, rowSpan: 2 }),
    cpuTemp({ visual: 'arc', rowSpan: 2 }),
    gpuTemp(p, { visual: 'arc', rowSpan: 2 }),
    { source: 'game.frametime', label: 'Frame time', visual: 'graph', colSpan: 3 },
    down(p, { visual: 'columns', colSpan: 3 }),
    ram({ visual: 'bar', colSpan: 2 }),
    vram(p, { visual: 'bar', colSpan: 2 }),
    clock({ colSpan: 2 }),
  ]),
  screen('art-portable-aviation', 'Cockpit Panel', 'A portable monitor with its own picture: an aircraft panel of six instruments with screws and red arcs', 'portable-fhd', 3, 'aviation', { size: 26, padding: 24, valueScale: 150 }, (p) => [
    cpu({ visual: 'arc' }),
    fps({ visual: 'speed' }),
    gpuUse(p, { visual: 'arc' }),
    cpuTemp({ visual: 'arc' }),
    ram({ visual: 'speed' }),
    gpuTemp(p, { visual: 'arc' }),
  ]),
  screen('art-portable-tower-racing', 'Carbon Pit Wall', 'A portable monitor upright with its own picture: speedometer faces with red zones on carbon fibre', 'portable-fhd-portrait', 2, 'racing', { size: 28, padding: 24, valueScale: 160 }, TOWERS.speed!),
  screen('art-monitor-holo', 'Hologram Wall', 'A whole monitor with its own picture: hologram frames, scanning rings and scopes over a hexagon field', 'monitor-qhd', 6, 'holo', { size: 26, padding: 28, valueScale: 150 }, (p) => [
    cpu({ visual: 'ring', rowSpan: 2 }),
    gpuUse(p, { visual: 'ring', rowSpan: 2 }),
    fps({ visual: 'graph', colSpan: 2, rowSpan: 2 }),
    ram({ visual: 'ring', rowSpan: 2 }),
    vram(p, { visual: 'ring', rowSpan: 2 }),
    cpuTemp({ visual: 'thermo', colSpan: 2 }),
    gpuTemp(p, { visual: 'thermo', colSpan: 2 }),
    down(p, { visual: 'columns', colSpan: 2 }),
  ]),
  screen('art-monitor-tower-brass', 'Brass Clock Tower', 'A monitor upright with its own picture: brass chronometers in riveted frames', 'monitor-qhd-portrait', 2, 'brass', { size: 30, padding: 30, valueScale: 160 }, TOWERS.dials!),
  screen('art-seven-cockpit', 'Cockpit Seven', 'A 7″ screen with its own picture: three gauge faces and two scopes in glowing frames', 'tablet-7', 3, 'cockpit', { size: 18, padding: 14, valueScale: 145 }, (p) => [cpu({ visual: 'arc' }), fps({ visual: 'speed' }), gpuUse(p, { visual: 'arc' }), cpuTemp({ visual: 'bar' }), { source: 'game.frametime', label: 'Frame time', visual: 'line' }, gpuTemp(p, { visual: 'bar' })]),
  screen('art-ten-tower-aviation', 'Flight Deck Ten', 'An upright 10.1″ with its own picture: four instruments with screws and red arcs, and the temperatures', 'tablet-10-portrait', 2, 'aviation', { size: 21, padding: 18, valueScale: 150 }, (p) => [cpu({ visual: 'arc' }), gpuUse(p, { visual: 'arc' }), fps({ visual: 'speed' }), ram({ visual: 'speed' }), cpuTemp({ visual: 'thermo' }), gpuTemp(p, { visual: 'thermo' })]),
  screen('art-five-racing', 'Carbon Five', 'A 5″ screen with its own picture: three speedometer faces on carbon fibre', 'small-5', 3, 'racing', { size: 15, padding: 12, valueScale: 140 }, (p) => [fps({ visual: 'speed' }), gpuUse(p, { visual: 'speed' }), cpu({ visual: 'speed' }), gpuTemp(p, { visual: 'bar' }), cpuTemp({ visual: 'bar' }), clock()]),
  screen('art-pump-brass', 'Brass Pump Gauges', 'A 480 × 480 screen with its own picture: four brass chronometer gauges', 'square-480', 2, 'brass', { size: 15, padding: 18, valueScale: 135 }, (p) => [cpu({ visual: 'arc' }), gpuUse(p, { visual: 'arc' }), cpuTemp({ visual: 'arc' }), gpuTemp(p, { visual: 'arc' })]),
  screen('art-mini-terminal', 'Terminal Mini', 'An upright 3.5″ with its own picture: a green CRT with two scopes and bar scales', 'small-35-portrait', 2, 'terminal', { size: 13, padding: 10, valueScale: 130 }, (p) => [cpu({ visual: 'line', colSpan: 2 }), gpuUse(p, { visual: 'line', colSpan: 2 }), cpuTemp({ visual: 'bar' }), gpuTemp(p, { visual: 'bar' })]),
];

/** A panel for a monitor with its own picture (a fixed size, so the picture is drawn around its readings). */
function panel(id: string, name: string, description: string, art: OverlayArt, size: { width: number; height: number }, columns: number, readings: (p: ReturnType<typeof panelPicker>) => (OverlayReading | false | null | undefined)[]): PanelDesign {
  return {
    id,
    name,
    description,
    build: (sources) => {
      const p = panelPicker(sources);
      return {
        title: null,
        layout: 'tiles',
        columns,
        width: size.width,
        height: size.height,
        readings: p.take(readings(p)),
        style: style({ ...look(art, { size: 14, padding: 12, valueScale: 130 }), cornerRadius: 10, shadow: 40 }),
      };
    },
  };
}

const g = (p: ReturnType<typeof panelPicker>, suffix: string) => p.gpu(suffix) ?? '';

/** Panels with a picture drawn for them, for monitors (Phase 47). */
export const ART_PANEL_DESIGNS: PanelDesign[] = [
  panel('art-cockpit-console', 'Starship Console', 'Its own picture: glowing notched frames, gauge faces with ticks and numbers, traces and LEDs', 'cockpit', { width: 520, height: 300 }, 3, (p) => [
    { source: 'cpu.usage', visual: 'arc', label: 'CPU', warning: 85, critical: 95 },
    { source: 'game.fps', visual: 'speed', label: 'FPS', max: 240 },
    { source: g(p, 'usage'), visual: 'arc', label: 'GPU', warning: 90 },
    { source: 'cpu.temp', visual: 'bar', label: 'CPU temp', max: 100, warning: 85 },
    { source: 'game.frametime', visual: 'line', label: 'Frame time' },
    { source: g(p, 'temp'), visual: 'bar', label: 'GPU temp', max: 100, warning: 80 },
  ]),
  panel('art-racing-cluster', 'Carbon Cluster', 'Its own picture: three speedometer faces with red zones on carbon fibre', 'racing', { width: 540, height: 220 }, 3, (p) => [
    { source: g(p, 'usage'), visual: 'speed', label: 'GPU', warning: 90 },
    { source: 'game.fps', visual: 'speed', label: 'FPS', max: 240 },
    { source: 'cpu.usage', visual: 'speed', label: 'CPU', warning: 85 },
  ]),
  panel('art-aviation-six', 'Six Pack', 'Its own picture: the six instruments of a cockpit, with screws, ticks and red arcs', 'aviation', { width: 480, height: 330 }, 3, (p) => [
    { source: 'cpu.usage', visual: 'arc', label: 'CPU', warning: 85 },
    { source: 'game.fps', visual: 'speed', label: 'FPS', max: 240 },
    { source: g(p, 'usage'), visual: 'arc', label: 'GPU', warning: 90 },
    { source: 'cpu.temp', visual: 'arc', label: 'CPU temp', max: 100, warning: 85 },
    { source: 'memory.usage', visual: 'speed', label: 'RAM', warning: 85 },
    { source: g(p, 'temp'), visual: 'arc', label: 'GPU temp', max: 100, warning: 80 },
  ]),
  panel('art-holo-hud', 'Hologram HUD', 'Its own picture: hologram frames with magenta brackets, scanning rings and a scope', 'holo', { width: 500, height: 300 }, 3, (p) => [
    { source: 'cpu.usage', visual: 'ring', label: 'CPU' },
    { source: g(p, 'usage'), visual: 'ring', label: 'GPU' },
    { source: 'memory.usage', visual: 'ring', label: 'RAM' },
    { source: 'game.fps', visual: 'graph', label: 'FPS', colSpan: 3 },
  ]),
  panel('art-synthwave-deck', 'Sunset Deck', 'Its own picture: a banded sun over a neon grid floor behind two rings and a graph', 'synthwave', { width: 480, height: 300 }, 2, (p) => [
    { source: 'cpu.usage', visual: 'ring', label: 'CPU' },
    { source: g(p, 'usage'), visual: 'ring', label: 'GPU' },
    { source: 'game.fps', visual: 'line', label: 'FPS', colSpan: 2 },
  ]),
  panel('art-brass-chronometer', 'Brass Chronometer', 'Its own picture: two brass chronometer gauges with guilloché faces in a riveted frame', 'brass', { width: 420, height: 230 }, 2, (p) => [
    { source: 'cpu.temp', visual: 'arc', label: 'CPU temp', max: 100, warning: 85 },
    { source: g(p, 'temp'), visual: 'arc', label: 'GPU temp', max: 100, warning: 80 },
  ]),
  panel('art-terminal-scope', 'Terminal Scope', 'Its own picture: a green CRT with two scopes and bar scales', 'terminal', { width: 460, height: 280 }, 2, (p) => [
    { source: 'cpu.usage', visual: 'line', label: 'CPU', colSpan: 2 },
    { source: g(p, 'usage'), visual: 'line', label: 'GPU', colSpan: 2 },
    { source: 'cpu.temp', visual: 'bar', label: 'CPU temp', max: 100, warning: 85 },
    { source: g(p, 'temp'), visual: 'bar', label: 'GPU temp', max: 100, warning: 80 },
  ]),
  panel('art-lux-watch', 'Midnight Gold Watch', 'Its own picture: gold-framed guilloché dials on gold pinstripes', 'lux', { width: 460, height: 230 }, 3, (p) => [
    { source: 'cpu.usage', visual: 'arc', label: 'CPU' },
    { source: 'time.clock', label: null },
    { source: g(p, 'usage'), visual: 'arc', label: 'GPU' },
  ]),
];

/** The look an art gives a panel when chosen for it (its text and colors; the picture made from its layout). */
export const artLook = (art: OverlayArt): Partial<OverlayStyle> => look(art, {});

// ---- Moving scenes (Phase 48): generated on the GPU, frosted glass cards under the readings ----

/** Roomy: wide margins and gaps, so each card of glass has the scene around it. */
const ROOMY = { padding: 28, spacing: 170 } satisfies Partial<OverlayStyle>;
const SCENE_BAR = { ...ROOMY, size: 24, valueScale: 165 } satisfies Partial<OverlayStyle>;
const SCENE_TOWER = { ...ROOMY, size: 25, valueScale: 175 } satisfies Partial<OverlayStyle>;

/** Layouts for an 8.8″ bar with few, large cards. */
const SCENE_BARS: { columns: number; layout: Layout }[] = [
  { columns: 4, layout: (p) => [cpu({ visual: 'ring' }), gpuUse(p, { visual: 'ring' }), fps({ visual: 'arc' }), gpuTemp(p, { visual: 'arc' })] },
  { columns: 5, layout: (p) => [fps({ visual: 'graph', colSpan: 2, rowSpan: 2 }), cpu({ visual: 'arc', rowSpan: 2 }), gpuUse(p, { visual: 'arc', rowSpan: 2 }), cpuTemp({ visual: 'bar' }), gpuTemp(p, { visual: 'bar' })] },
  { columns: 5, layout: (p) => [cpu({ visual: 'arc', rowSpan: 2 }), fps({ visual: 'speed', colSpan: 2, rowSpan: 2 }), gpuUse(p, { visual: 'arc', rowSpan: 2 }), clock(), gpuTemp(p, { visual: 'thermo' })] },
  { columns: 5, layout: (p) => [cpu({ visual: 'ring', rowSpan: 2 }), gpuUse(p, { visual: 'ring', rowSpan: 2 }), ram({ visual: 'ring', rowSpan: 2 }), fps({ visual: 'line', colSpan: 2 }), ping({ visual: 'line', colSpan: 2 })] },
];

/** Layouts for an upright 8.8″, two columns, a few tall cards. */
const SCENE_TOWERS: Layout[] = [
  (p) => [clock({ colSpan: 2 }), cpu({ visual: 'arc', colSpan: 2, rowSpan: 2 }), gpuUse(p, { visual: 'arc', colSpan: 2, rowSpan: 2 }), cpuTemp({ visual: 'bar' }), gpuTemp(p, { visual: 'bar' }), fps({ visual: 'line', colSpan: 2 })],
  (p) => [cpu({ visual: 'ring' }), gpuUse(p, { visual: 'ring' }), ram({ visual: 'ring' }), vram(p, { visual: 'ring' }), fps({ visual: 'graph', colSpan: 2, rowSpan: 2 }), cpuTemp({ visual: 'thermo', colSpan: 2 }), gpuTemp(p, { visual: 'thermo', colSpan: 2 })],
  (p) => [fps({ visual: 'speed', colSpan: 2, rowSpan: 2 }), cpu({ visual: 'arc' }), gpuUse(p, { visual: 'arc' }), cpuTemp({ visual: 'bar', colSpan: 2 }), gpuTemp(p, { visual: 'bar', colSpan: 2 }), clock({ colSpan: 2 })],
];

/** What each scene is like, and its name, for its designs. */
const SCENE_WORDS: Record<OverlayGeneratedArt, string> = {
  silk: 'satin folds flowing in plum and rose',
  mesh: 'soft pools of colour drifting into each other',
  aurora: 'curtains of northern lights over a starry sky',
  nebula: 'clouds of gas and dust turning in deep space',
  caustics: 'sunlight dancing on the floor of a lagoon',
  bokeh: 'out-of-focus city lights drifting at night',
  topo: 'contour lines of a slowly reshaping landscape',
  prism: 'beams of split light sweeping across dark glass',
  ocean: 'swells rolling under a low sun at dusk',
  lava: 'warm wax rising and merging in a lamp',
  ink: 'ink spreading and curling in still water',
  marble: 'black marble with flowing veins of gold',
  foil: 'holographic foil shifting through its colours',
  fireflies: 'fireflies wandering through the woods at night',
  warp: 'stars streaking past at light speed',
  galaxy: 'a spiral galaxy slowly turning',
  clouds: 'soft clouds drifting in the blue hour',
  snow: 'snow falling quietly at night',
  ribbons: 'ribbons of colour flowing across',
  circuit: 'pulses of data racing along glowing traces',
};
const SCENE_NAMES: Record<OverlayGeneratedArt, string> = {
  silk: 'Liquid Silk',
  mesh: 'Mesh Gradient',
  aurora: 'Aurora Veil',
  nebula: 'Nebula Drift',
  caustics: 'Deep Caustics',
  bokeh: 'City Bokeh',
  topo: 'Topography',
  prism: 'Prism Light',
  ocean: 'Ocean Swell',
  lava: 'Lava Lamp',
  ink: 'Ink in Water',
  marble: 'Liquid Marble',
  foil: 'Holo Foil',
  fireflies: 'Fireflies',
  warp: 'Hyperspace',
  galaxy: 'Spiral Galaxy',
  clouds: 'Cloud Drift',
  snow: 'Snowfall',
  ribbons: 'Fluid Ribbons',
  circuit: 'Data Stream',
};

function sceneScreen(id: string, name: string, kind: OverlayGeneratedArt, where: string, size: string, columns: number, text: Partial<OverlayStyle>, layout: Layout): ScreenDesign {
  return screen(id, name, where + 'a moving scene of ' + SCENE_WORDS[kind] + ', the readings on frosted glass cards with a light running round their edges', size, columns, kind, text, layout);
}

/** Whole-screen designs on moving scenes, for every screen size (the 8.8″ first: every scene, wide and upright). */
export const SCENE_SCREEN_DESIGNS: ScreenDesign[] = [
  // 8.8″ wide: every scene, on the four layouts in turn.
  ...(['silk', 'aurora', 'nebula', 'caustics', 'mesh', 'bokeh', 'topo', 'prism', 'ocean', 'lava', 'galaxy', 'fireflies', 'marble', 'foil', 'warp', 'ink', 'clouds', 'snow', 'ribbons', 'circuit'] as const).map((kind, i) => {
    const bar = SCENE_BARS[i % SCENE_BARS.length]!;
    return sceneScreen('scene-bar-' + kind, SCENE_NAMES[kind] + ' Bar', kind, '', 'bar-88', bar.columns, SCENE_BAR, bar.layout);
  }),
  // 8.8″ upright: every scene, on the three layouts in turn.
  ...(['aurora', 'silk', 'caustics', 'nebula', 'prism', 'mesh', 'topo', 'bokeh', 'lava', 'ocean', 'snow', 'galaxy', 'ink', 'fireflies', 'foil', 'marble', 'warp', 'ribbons', 'clouds', 'circuit'] as const).map((kind, i) =>
    sceneScreen('scene-tower-' + kind, SCENE_NAMES[kind] + ' Tower', kind, 'Upright: ', 'bar-88-portrait', 2, SCENE_TOWER, SCENE_TOWERS[i % SCENE_TOWERS.length]!),
  ),
  // Other bars.
  sceneScreen('scene-bar92-aurora', 'Aurora Veil 9.2', 'aurora', 'A 9.2″ bar: ', 'bar-92', 4, SCENE_BAR, SCENE_BARS[0]!.layout),
  sceneScreen('scene-bar92-tower-nebula', 'Nebula Drift 9.2 Tower', 'nebula', 'A 9.2″ bar upright: ', 'bar-92-portrait', 2, SCENE_TOWER, SCENE_TOWERS[0]!),
  sceneScreen('scene-bar123-nebula', 'Nebula Drift 12.3', 'nebula', 'A 12.3″ bar: ', 'bar-123', 5, { ...SCENE_BAR, size: 28 }, SCENE_BARS[1]!.layout),
  sceneScreen('scene-bar123-tower-silk', 'Liquid Silk 12.3 Tower', 'silk', 'A 12.3″ bar upright: ', 'bar-123-portrait', 2, { ...SCENE_TOWER, size: 28 }, SCENE_TOWERS[1]!),
  // Portable and whole monitors.
  sceneScreen('scene-portable-caustics', 'Deep Caustics Console', 'caustics', 'A portable monitor: ', 'portable-fhd', 4, { ...ROOMY, padding: 40, size: 26, valueScale: 160 }, (p) => [
    cpu({ visual: 'arc', rowSpan: 2 }),
    fps({ visual: 'graph', colSpan: 2, rowSpan: 2 }),
    gpuUse(p, { visual: 'arc', rowSpan: 2 }),
    cpuTemp({ visual: 'bar' }),
    ram({ visual: 'bar' }),
    vram(p, { visual: 'bar' }),
    gpuTemp(p, { visual: 'bar' }),
  ]),
  sceneScreen('scene-portable-tower-aurora', 'Aurora Veil Column', 'aurora', 'A portable monitor upright: ', 'portable-fhd-portrait', 2, { ...ROOMY, padding: 40, size: 28, valueScale: 165 }, SCENE_TOWERS[0]!),
  sceneScreen('scene-monitor-nebula', 'Nebula Observatory', 'nebula', 'A whole monitor: ', 'monitor-qhd', 4, { ...ROOMY, padding: 56, size: 30, valueScale: 160 }, (p) => [
    fps({ visual: 'speed', rowSpan: 2 }),
    cpu({ visual: 'ring' }),
    gpuUse(p, { visual: 'ring' }),
    clock(),
    { source: 'game.frametime', label: 'Frame time', visual: 'graph', colSpan: 2 },
    cpuTemp({ visual: 'thermo' }),
    gpuTemp(p, { visual: 'thermo' }),
    down(p, { visual: 'columns', colSpan: 2 }),
    ping({ visual: 'line', colSpan: 2 }),
  ]),
  sceneScreen('scene-monitor-tower-prism', 'Prism Light Tower', 'prism', 'A monitor upright: ', 'monitor-qhd-portrait', 2, { ...ROOMY, padding: 56, size: 32, valueScale: 165 }, SCENE_TOWERS[1]!),
  // Small screens.
  sceneScreen('scene-ten-mesh', 'Mesh Gradient Ten', 'mesh', 'A 10.1″ screen: ', 'tablet-10', 3, { ...ROOMY, size: 22, valueScale: 150 }, (p) => [cpu({ visual: 'ring' }), fps({ visual: 'arc' }), gpuUse(p, { visual: 'ring' }), cpuTemp({ visual: 'bar' }), clock(), gpuTemp(p, { visual: 'bar' })]),
  sceneScreen('scene-ten-tower-silk', 'Liquid Silk Ten', 'silk', 'An upright 10.1″: ', 'tablet-10-portrait', 2, { ...ROOMY, size: 22, valueScale: 155 }, SCENE_TOWERS[2]!),
  sceneScreen('scene-seven-bokeh', 'City Bokeh Seven', 'bokeh', 'A 7″ screen: ', 'tablet-7', 3, { ...ROOMY, padding: 22, size: 19, valueScale: 145 }, (p) => [cpu({ visual: 'arc' }), fps({ visual: 'speed' }), gpuUse(p, { visual: 'arc' })]),
  sceneScreen('scene-seven-tower-topo', 'Topography Seven', 'topo', 'An upright 7″: ', 'tablet-7-portrait', 2, { ...ROOMY, padding: 22, size: 19, valueScale: 150 }, (p) => [cpu({ visual: 'ring' }), gpuUse(p, { visual: 'ring' }), fps({ visual: 'graph', colSpan: 2 }), cpuTemp({ visual: 'bar' }), gpuTemp(p, { visual: 'bar' })]),
  sceneScreen('scene-five-caustics', 'Deep Caustics Five', 'caustics', 'A 5″ screen: ', 'small-5', 3, { ...ROOMY, padding: 20, size: 16, valueScale: 140 }, (p) => [cpu({ visual: 'ring' }), fps({ visual: 'arc' }), gpuUse(p, { visual: 'ring' })]),
  sceneScreen('scene-pump-aurora', 'Aurora Pump', 'aurora', 'A 480 × 480 screen: ', 'square-480', 2, { ...ROOMY, padding: 22, size: 15, valueScale: 135 }, (p) => [cpu({ visual: 'arc' }), gpuUse(p, { visual: 'arc' }), cpuTemp({ visual: 'bar' }), gpuTemp(p, { visual: 'bar' })]),
  sceneScreen('scene-mini-nebula', 'Nebula Mini', 'nebula', 'A 3.5″ screen: ', 'small-35', 2, { ...ROOMY, padding: 16, size: 13, valueScale: 130 }, (p) => [cpu({ visual: 'ring' }), gpuUse(p, { visual: 'ring' })]),
];

/** A panel on a moving scene, for a monitor (a fixed size: the cards follow its tiles). */
function scenePanel(id: string, name: string, kind: OverlayGeneratedArt, size: { width: number; height: number }, columns: number, readings: (p: ReturnType<typeof panelPicker>) => (OverlayReading | false | null | undefined)[]): PanelDesign {
  return {
    id,
    name,
    description: 'A moving scene of ' + SCENE_WORDS[kind] + ', the readings on frosted glass cards',
    build: (sources) => {
      const p = panelPicker(sources);
      return {
        title: null,
        layout: 'tiles',
        columns,
        width: size.width,
        height: size.height,
        readings: p.take(readings(p)),
        style: style({ ...look(kind, { size: 14, padding: 16, spacing: 160, valueScale: 135 }), cornerRadius: 18, shadow: 45 }),
      };
    },
  };
}

const net = (p: ReturnType<typeof panelPicker>, suffix: string) => p.first('net.', '.' + suffix) ?? '';

/** Panels on moving scenes, for monitors. */
export const SCENE_PANEL_DESIGNS: PanelDesign[] = [
  scenePanel('scene-silk-trio', 'Liquid Silk Trio', 'silk', { width: 520, height: 210 }, 3, (p) => [
    { source: 'cpu.usage', visual: 'ring', label: 'CPU' },
    { source: g(p, 'usage'), visual: 'ring', label: 'GPU' },
    { source: 'game.fps', visual: 'arc', label: 'FPS', max: 240 },
  ]),
  scenePanel('scene-aurora-console', 'Aurora Console', 'aurora', { width: 540, height: 320 }, 3, (p) => [
    { source: 'cpu.usage', visual: 'arc', label: 'CPU', warning: 85 },
    { source: 'game.fps', visual: 'speed', label: 'FPS', max: 240 },
    { source: g(p, 'usage'), visual: 'arc', label: 'GPU', warning: 90 },
    { source: 'game.frametime', visual: 'line', label: 'Frame time', colSpan: 3 },
  ]),
  scenePanel('scene-nebula-temps', 'Nebula Temperatures', 'nebula', { width: 460, height: 230 }, 2, (p) => [
    { source: 'cpu.temp', visual: 'arc', label: 'CPU temp', max: 100, warning: 85 },
    { source: g(p, 'temp'), visual: 'arc', label: 'GPU temp', max: 100, warning: 80 },
  ]),
  scenePanel('scene-caustics-graphs', 'Deep Caustics Graphs', 'caustics', { width: 500, height: 280 }, 2, (p) => [
    { source: 'cpu.usage', visual: 'line', label: 'CPU' },
    { source: g(p, 'usage'), visual: 'line', label: 'GPU' },
    { source: 'game.fps', visual: 'graph', label: 'FPS', colSpan: 2 },
  ]),
  scenePanel('scene-mesh-stats', 'Mesh Gradient Stats', 'mesh', { width: 480, height: 260 }, 3, (p) => [
    { source: 'cpu.usage', visual: 'bar', label: 'CPU' },
    { source: g(p, 'usage'), visual: 'bar', label: 'GPU' },
    { source: 'memory.usage', visual: 'bar', label: 'RAM' },
    { source: 'cpu.temp', visual: 'bar', label: 'CPU temp', max: 100 },
    { source: g(p, 'temp'), visual: 'bar', label: 'GPU temp', max: 100 },
    { source: 'time.clock', label: null },
  ]),
  scenePanel('scene-bokeh-fps', 'City Bokeh FPS', 'bokeh', { width: 360, height: 260 }, 2, () => [
    { source: 'game.fps', visual: 'speed', label: 'FPS', max: 240, colSpan: 2 },
    { source: 'game.low1', label: '1% low' },
    { source: 'game.frametime', label: 'Frame time' },
  ]),
  scenePanel('scene-topo-network', 'Topography Network', 'topo', { width: 480, height: 230 }, 2, (p) => [
    { source: net(p, 'down'), visual: 'columns', label: 'Download' },
    { source: net(p, 'up'), visual: 'columns', label: 'Upload' },
    { source: 'net.ping.internet', visual: 'line', label: 'Ping', colSpan: 2 },
  ]),
  scenePanel('scene-prism-clock', 'Prism Light Clock', 'prism', { width: 420, height: 200 }, 2, () => [
    { source: 'time.clock', label: null, colSpan: 2 },
    { source: 'cpu.usage', visual: 'ring', label: 'CPU' },
    { source: 'memory.usage', visual: 'ring', label: 'RAM' },
  ]),
];
