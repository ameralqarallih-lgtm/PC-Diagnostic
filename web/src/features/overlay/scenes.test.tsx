import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { LibraryPicture, OverlayPanel, OverlaySource, OverlayStyle } from '@/lib/api/types';
import { mockApi } from '@/test/app';
import { frameAt, packAnimation, readAnimation } from './anim';
import { SCENE_PANEL_DESIGNS, SCENE_SCREEN_DESIGNS } from './artDesigns';
import { BackgroundChooser, glassFits } from './BackgroundChooser';
import { GENERATIVE_KINDS } from './generative';
import { bakeArt, drawsLive, glassLayout, isAnimationId, isGenerative } from './liveArt';
import { DEFAULT_STYLE, level } from './model';
import { PanelPreview } from './PanelPreview';
import { fitScreenDesign, sizeOf } from './screens';

const sources: OverlaySource[] = [
  { id: 'cpu.usage', label: 'CPU', group: 'Processor', unit: '%', value: 52 },
  { id: 'cpu.temp', label: 'CPU temp', group: 'Processor', unit: '°C', value: 61 },
  { id: 'memory.usage', label: 'RAM', group: 'Memory', unit: '%', value: 40 },
  { id: 'gpu.g1.usage', label: 'GPU', group: 'RTX 4090', unit: '%', value: 79 },
  { id: 'gpu.g1.temp', label: 'GPU temp', group: 'RTX 4090', unit: '°C', value: 48 },
  { id: 'gpu.g1.vram', label: 'VRAM', group: 'RTX 4090', unit: 'B', value: 8e9, max: 25e9 },
  { id: 'game.fps', label: 'FPS', group: 'Game', unit: 'fps', value: 144 },
  { id: 'game.low1', label: '1% low', group: 'Game', unit: 'fps', value: 98 },
  { id: 'game.frametime', label: 'Frame time', group: 'Game', unit: 'ms', value: 6.9 },
  { id: 'net.n1.down', label: 'Download', group: 'Ethernet', unit: 'B/s', value: 2e6 },
  { id: 'net.n1.up', label: 'Upload', group: 'Ethernet', unit: 'B/s', value: 3e5 },
  { id: 'net.ping.internet', label: 'Ping', group: 'Network', unit: 'ms', value: 9 },
  { id: 'time.clock', label: 'Time', group: 'This PC', unit: '', value: null, text: '07:51' },
];
const sourceMap = new Map(sources.map((s) => [s.id, s]));

const library: LibraryPicture[] = [
  { id: '0123456789abcdef.jpg', name: 'Alpine lake', kind: 'upload', width: 1920, height: 1280, frames: 1, size: 412_000, added: '2026-10-08T10:00:00Z', used: true, here: true },
  { id: 'fedcba9876543210.anim', name: 'Rain on glass', kind: 'upload', width: 1280, height: 720, frames: 180, size: 14_200_000, added: '2026-10-08T09:00:00Z', used: false, here: true },
  { id: '1111111111111111.jpg', name: 'Night city', kind: 'upload', width: 1920, height: 1080, frames: 1, size: 520_000, added: '2026-10-07T09:00:00Z', used: false, here: false },
  { id: '2222222222222222.anim', name: null, kind: 'generated', width: 1920, height: 480, frames: 96, size: 6_400_000, added: '2026-10-09T09:00:00Z', used: true, here: true },
];

const scenePanel = (): OverlayPanel => ({ ...fitScreenDesign(SCENE_SCREEN_DESIGNS[0]!, sources, sizeOf(SCENE_SCREEN_DESIGNS[0]!))[0]!, id: 'p1', x: 0, y: 0 });

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Moving scenes and the picture library (Phase 48)', () => {
  it('packs an animation as the recorder reads it, and plays it frame by frame', async () => {
    const jpeg = (n: number) => new Blob([new Uint8Array([0xff, 0xd8, n, n, 0xff, 0xd9])], { type: 'image/jpeg' });
    const blob = await packAnimation(12, 480, 1920, [jpeg(1), jpeg(2), jpeg(3)]);
    const bytes = await blob.arrayBuffer();
    expect(new TextDecoder().decode(bytes.slice(0, 4))).toBe('PCDA');
    expect(bytes.byteLength).toBe(16 + 3 * 4 + 3 * 6);
    const animation = readAnimation(bytes)!;
    expect([animation.fps, animation.width, animation.height, animation.frames.length]).toEqual([12, 480, 1920, 3]);
    expect(new Uint8Array(await animation.frames[1]!.arrayBuffer())).toEqual(new Uint8Array([0xff, 0xd8, 2, 2, 0xff, 0xd9]));
    // Cut short, or not one at all: refused.
    expect(readAnimation(bytes.slice(0, bytes.byteLength - 1))).toBeNull();
    expect(readAnimation(new TextEncoder().encode('PNG------------------').buffer)).toBeNull();
    // The frame by the clock, round and round, as the recorder picks it.
    expect([0, 0.09, 0.17, 0.25].map((s) => frameAt(animation, s))).toEqual([0, 1, 2, 0]);
  });

  it('lays a card of glass over every tile and a scale under every dial', () => {
    const panel = scenePanel();
    const { cards, dials } = glassLayout(panel, 1);
    expect(cards).toHaveLength(panel.readings.length);
    expect(dials.length).toBe(panel.readings.filter((r) => ['ring', 'arc', 'speed'].includes(r.visual ?? '')).length);
    // Inside the panel, in its padding.
    for (const c of cards) {
      expect(c.x).toBeGreaterThanOrEqual(panel.style.padding - 0.01);
      expect(c.x + c.w).toBeLessThanOrEqual(panel.width! - panel.style.padding + 0.01);
    }
    // At twice the size, twice as far.
    expect(glassLayout(panel, 2).cards[1]!.x).toBeCloseTo(cards[1]!.x * 2, 5);
    // Not tiles, or no fixed size: no cards.
    expect(glassLayout({ ...panel, layout: 'rows' }, 1).cards).toEqual([]);
  });

  it('builds every scene design at a fixed size, drawn live here and made for the recorder when saved', () => {
    expect(SCENE_SCREEN_DESIGNS).toHaveLength(55);
    expect(SCENE_PANEL_DESIGNS).toHaveLength(8);
    // Every scene for the 8.8″, wide and upright.
    for (const kind of GENERATIVE_KINDS) {
      expect(SCENE_SCREEN_DESIGNS.some((d) => d.size === 'bar-88' && fitScreenDesign(d, sources, sizeOf(d))[0]!.style.art === kind), kind).toBe(true);
      expect(SCENE_SCREEN_DESIGNS.some((d) => d.size === 'bar-88-portrait' && fitScreenDesign(d, sources, sizeOf(d))[0]!.style.art === kind), kind).toBe(true);
    }
    for (const design of SCENE_SCREEN_DESIGNS) {
      for (const p of fitScreenDesign(design, sources, sizeOf(design))) {
        expect(isGenerative(p.style.art), design.id).toBe(true);
        expect(p.style.image ?? null, design.id).toBeNull();
        expect(p.style.tileOpacity, design.id).toBe(0);
        expect(glassFits(p), design.id).toBe(true);
        expect(drawsLive(p), design.id).toBe(true);
        expect(glassLayout(p, 1).cards.length, design.id).toBe(p.readings.length);
      }
    }
    for (const design of SCENE_PANEL_DESIGNS) {
      const built = design.build(sources);
      expect(isGenerative(built.style.art), design.id).toBe(true);
      expect(built.readings.length, design.id).toBeGreaterThan(0);
    }
  });

  it('draws a scene or an animation on a canvas, and a still as a picture', () => {
    const panel = scenePanel();
    const { container, rerender } = render(<PanelPreview panel={panel} sources={sourceMap} scale={1} />);
    expect(container.querySelector('canvas[data-live-art]')?.getAttribute('data-live-art')).toBe('silk');
    expect(container.querySelector('[data-art]')?.getAttribute('data-art')).toBe('silk');

    const animated: OverlayPanel = { ...panel, style: { ...panel.style, art: null, image: 'fedcba9876543210.anim' } };
    expect(drawsLive(animated)).toBe(true);
    expect(isAnimationId(animated.style.image)).toBe(true);
    rerender(<PanelPreview panel={animated} sources={sourceMap} scale={1} />);
    expect(container.querySelector('canvas[data-live-art]')?.getAttribute('data-live-art')).toBe('animation');

    const still: OverlayPanel = { ...panel, style: { ...panel.style, art: null, image: '0123456789abcdef.jpg' } };
    expect(drawsLive(still)).toBe(false);
    rerender(<PanelPreview panel={still} sources={sourceMap} scale={1} />);
    expect(container.querySelector('canvas[data-live-art]')).toBeNull();
    expect(container.querySelector<HTMLElement>('[data-image] > span')!.style.backgroundImage).toContain('0123456789abcdef.jpg');
  });

  it('makes nothing for the recorder without a GPU (it is made again in a browser that has one)', async () => {
    expect(await bakeArt(scenePanel())).toBeNull();
    expect(await bakeArt({ ...scenePanel(), style: { ...scenePanel().style, art: null } })).toBeNull();
  });

  it('judges a reading shown as a percentage by that percentage', () => {
    const vram = { source: 'gpu.g1.vram', show: 'percent' as const, warning: 90, critical: 97 };
    expect(level(vram, 8e9, 25e9)).toBe(0);
    expect(level(vram, 23e9, 25e9)).toBe(1);
    expect(level(vram, 24.5e9, 25e9)).toBe(2);
    // Shown as a value, its limits are values.
    expect(level({ source: 'cpu.temp', warning: 85 }, 90, 100)).toBe(1);
  });

  function chooser(panel: OverlayPanel, onChange = vi.fn()) {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={client}>
        <BackgroundChooser open panel={panel} sourceMap={sourceMap} onClose={() => undefined} onChange={onChange} />
      </QueryClientProvider>,
    );
    return onChange;
  }

  it('chooses a moving scene, shown behind the panel’s own readings', async () => {
    mockApi({ 'GET /api/v1/overlay/images': () => library, 'GET /api/v1/storage/locations': () => ({ locations: [], job: {}, kinds: [], onThisPc: [] }) });
    const panel = { ...scenePanel(), style: { ...DEFAULT_STYLE, size: 24 } as OverlayStyle };
    const onChange = chooser(panel);
    const dialog = screen.getByRole('dialog', { name: 'Background' });
    // A card for every scene, each a preview of this panel.
    for (const name of ['Liquid Silk', 'Aurora Veil', 'Prism Light', 'Ocean Swell', 'Lava Lamp', 'Hyperspace', 'Snowfall', 'Data Stream']) expect(within(dialog).getByRole('listitem', { name })).toBeInTheDocument();
    expect(within(dialog).getAllByRole('listitem')).toHaveLength(GENERATIVE_KINDS.length);
    await userEvent.click(within(dialog).getByRole('listitem', { name: 'Aurora Veil' }));
    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ art: 'aurora', image: null, tileOpacity: 0, imageFit: 'stretch' }));
  });

  it('lists the library, chooses a picture with glass over the readings or as it is, and brings one back from storage', async () => {
    const fetchMock = mockApi({
      'GET /api/v1/overlay/images': () => library,
      'GET /api/v1/storage/locations': () => ({ locations: [{ location: { id: 's3', name: 'Office S3', kind: 's3', keeps: ['pictures'], pathStyle: true }, hasKeys: true, accessKeyEnd: null, online: true, problem: null }], job: {}, kinds: [], onThisPc: [] }),
      'POST /api/v1/overlay/images/1111111111111111.jpg/fetch': () => undefined,
    });
    const onChange = chooser(scenePanel());
    await userEvent.click(screen.getByRole('tab', { name: /Pictures and animations/ }));
    await screen.findByText('Alpine lake');
    // Uploads only (pictures made for panels behind a switch); where uploads are copied.
    expect(screen.queryByText('Made for a panel')).toBeNull();
    expect(screen.getByText(/copied to Office S3/)).toBeInTheDocument();
    expect(screen.getByText('180 frames')).toBeInTheDocument();
    // In use: not deleted.
    expect(screen.getByRole('button', { name: 'Delete Alpine lake' })).toBeDisabled();

    // With glass over the readings (the default for a panel of tiles at a fixed size).
    await userEvent.click(screen.getByRole('button', { name: 'Use Rain on glass' }));
    expect(onChange).toHaveBeenLastCalledWith(expect.objectContaining({ art: 'glass', artSource: 'fedcba9876543210.anim' }));
    // As it is.
    await userEvent.click(screen.getByRole('checkbox', { name: /Frosted glass cards/ }));
    await userEvent.click(screen.getByRole('button', { name: 'Use Rain on glass' }));
    expect(onChange).toHaveBeenLastCalledWith(expect.objectContaining({ art: null, artSource: null, image: 'fedcba9876543210.anim' }));

    // Only in storage: brought here.
    await userEvent.click(screen.getByRole('button', { name: 'Bring Night city to this PC' }));
    await waitFor(() => expect(fetchMock.mock.calls.some(([url, init]) => String(url).endsWith('/overlay/images/1111111111111111.jpg/fetch') && init?.method === 'POST')).toBe(true));

    await userEvent.click(screen.getByRole('checkbox', { name: /Show pictures made for panels/ }));
    expect(screen.getByText('Made for a panel')).toBeInTheDocument();
  });
});
