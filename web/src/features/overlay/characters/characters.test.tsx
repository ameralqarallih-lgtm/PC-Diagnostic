import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { CharacterInfo, OverlayCharacter, OverlayPanel } from '@/lib/api/types';
import { ConfirmProvider } from '@/components/ui/confirm';
import { confirmIt, mockApi } from '@/test/app';
import { DEFAULT_STYLE } from '../model';
import { frameOf, lineOf, random, seedOf } from './behaviour';
import { CharactersTab, newCharacter, playsInstead } from './CharactersTab';
import { backgroundOf, boundsOf, keyOut, layoutClips, naturalOrder } from './importer';
import motion from './motion-parity.json';
import { clipFor, clipFrame, readPack, writePack } from './pack';
import { encodePng8, indexFrame, paletteOf } from './png8';
import { CharacterFrames } from './library';

const panelBox = { left: 100, top: 200, right: 500, bottom: 320 };
const screenBox = { left: 0, top: 0, right: 1920, bottom: 1080 };
const character = (over: Partial<OverlayCharacter> = {}): OverlayCharacter => ({ ...newCharacter('mochi', []), ...over });

/** The editor's test character (test.char), as the recorder's tests read it too. */
async function testCharacter(): Promise<Uint8Array> {
  const node = (globalThis as unknown as { process?: { cwd: () => string } }).process;
  const fs = (await import(/* @vite-ignore */ ['node', 'fs'].join(':'))) as { readFileSync: (file: string) => Uint8Array };
  return new Uint8Array(fs.readFileSync(`${node!.cwd()}/src/features/overlay/characters/test.char`));
}

/** A frame of RGBA pixels from rows of colours ('.' see-through). */
function pixels(rows: string[], colours: Record<string, [number, number, number, number]>) {
  const width = rows[0]!.length;
  const data = new Uint8ClampedArray(width * rows.length * 4);
  rows.forEach((row, y) => [...row].forEach((ch, x) => data.set(colours[ch] ?? [0, 0, 0, 0], (y * width + x) * 4)));
  return { data, width, height: rows.length };
}

/** What the recorder must move alike (CharacterPlayerTests.cs reads the same file): seeds and motion frames. */
function motionCases() {
  const settings: Partial<OverlayCharacter>[] = [
    { place: 'top', movement: 'stay', x: 0.25, flip: true },
    { place: 'top', movement: 'pace', speed: 140 },
    { place: 'inside', movement: 'wander', action: 'sit' },
    { place: 'screen', movement: 'wander', action: 'run', id: 'char-7' },
    { place: 'around', speed: 80 },
    { place: 'right', movement: 'pace', action: 'sleep' },
    // Only some animations, its own animation speed and pauses.
    { place: 'top', movement: 'wander', plays: ['idle', 'run', 'dance', 'sleep'], tempo: 50, pause: 200 },
    { place: 'around', plays: ['idle', 'walk', 'sit'], speed: 120 },
    { place: 'inside', movement: 'pace', action: 'play', plays: ['idle', 'sit'] },
    { place: 'screen', movement: 'pace', action: 'dance', plays: ['idle', 'walk', 'sit'], pause: 50 },
    // Kept to an area along its place.
    { place: 'top', movement: 'wander', from: 0.2, to: 0.5 },
    { place: 'screen', movement: 'pace', from: 0.6, to: 0.9, speed: 150 },
    // Dropped on its screen: across it at its height there.
    { place: 'spot', movement: 'stay', x: 0.3, y: 0.4 },
    { place: 'spot', movement: 'wander', y: 0.7, from: 0.2, to: 0.5 },
  ];
  const frames = settings.flatMap((over, i) =>
    [0, 3.3, 17.25, 123.9].map((t) => {
      const c = character({ id: `char-${i + 1}`, ...over });
      const f = frameOf(c, panelBox, screenBox, 64, t);
      return { character: c, t, frame: { ...f, x: Math.round(f.x * 1e6) / 1e6, y: Math.round(f.y * 1e6) / 1e6, seconds: Math.round(f.seconds * 1e6) / 1e6 } };
    }),
  );
  return { panel: panelBox, screen: screenBox, size: 64, seeds: ['char-1', 'kitty', 'Ünïcode'].map((t) => ({ text: t, seed: seedOf(t), randoms: [0, 1, 7, 21].map((k) => random(seedOf(t), k)) })), frames };
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Characters', () => {
  it('reads a character file as the recorder does, and writes it back the same', async () => {
    const bytes = await testCharacter();
    const pack = readPack(bytes)!;
    expect(pack).toMatchObject({ name: 'Test', width: 6, height: 12, anchor: [3, 11.5], tall: 9, faces: -1 });
    expect(pack.clips.map((c) => [c.action, c.fps, c.frames.length])).toEqual([
      ['idle', 4, 2],
      ['walk', 8, 3],
    ]);
    expect(writePack(pack)).toEqual(bytes);
    // Cut short, too long, without idle, a frame of another size: refused.
    expect(readPack(bytes.subarray(0, bytes.length - 1))).toBeNull();
    expect(readPack(new Uint8Array([...bytes, 0]))).toBeNull();
    expect(readPack(writePack({ ...pack, clips: [pack.clips[1]!] }))).toBeNull();
    expect(readPack(writePack({ ...pack, width: 7 }))).toBeNull();
    expect(readPack(writePack({ ...pack, tall: 13 }))).toBeNull();
  });

  it('plays the closest clip a character has, and loops each clip', () => {
    const clips = ['idle', 'walk', 'play'].map((action) => ({ action, fps: 10, frames: Array.from({ length: 5 }, () => new Uint8Array()) }));
    expect(['run', 'dance', 'jump', 'sleep', 'sit', 'idle'].map((a) => clipFor({ clips }, a).action)).toEqual(['walk', 'play', 'play', 'idle', 'idle', 'idle']);
    expect([0, 0.25, 0.55, -0.05].map((s) => clipFrame(clips[0]!, s))).toEqual([0, 2, 0, 4]);
    expect(playsInstead({ actions: ['idle', 'walk'] }, 'run')).toBe('walk');
    expect(playsInstead({ actions: ['idle', 'walk'] }, 'walk')).toBeNull();
  });

  it('writes frames in 256 colours or fewer, every colour kept when there are few', async () => {
    const frame = pixels(['..rr..', '.rggr.', 'rgbbgr', '.rggr.'], { r: [230, 30, 40, 255], g: [20, 200, 90, 255], b: [10, 20, 240, 128] });
    const palette = paletteOf([frame]);
    expect(palette.length / 4).toBe(4);
    expect([...palette.subarray(0, 4)]).toEqual([0, 0, 0, 0]);
    const indices = indexFrame(frame, palette);
    const colourAt = (i: number) => [...palette.subarray(4 * indices[i]!, 4 * indices[i]! + 4)];
    expect(colourAt(2)).toEqual([230, 30, 40, 255]);
    expect(colourAt(8)).toEqual([20, 200, 90, 255]);
    expect(colourAt(14)).toEqual([10, 20, 240, 128]);
    expect(indices[0]).toBe(0);
    const png = await encodePng8(indices, frame.width, frame.height, palette);
    expect([...png.subarray(0, 8)]).toEqual([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
    const view = new DataView(png.buffer, png.byteOffset);
    expect([view.getUint32(16), view.getUint32(20), png[24], png[25]]).toEqual([6, 4, 8, 3]);
    expect(new TextDecoder().decode(png.subarray(png.length - 8, png.length - 4))).toBe('IEND');
  });

  it('takes out a flat background, finds what a frame draws, and puts every clip’s feet on one spot', () => {
    const green: [number, number, number, number] = [0, 255, 0, 255];
    const frame = pixels(['gggggg', 'ggkkgg', 'ggkkgg', 'gkkkkg', 'gggggg'], { g: green, k: [20, 20, 20, 255] });
    expect(backgroundOf(frame)).toMatchObject({ r: 0, g: 255, b: 0 });
    expect(backgroundOf(pixels(['....', '.kk.'], { k: [20, 20, 20, 255] }))).toBeNull();
    keyOut(frame, { r: 0, g: 255, b: 0, tolerance: 40 });
    expect(frame.data[3]).toBe(0);
    expect(frame.data[(1 * 6 + 2) * 4 + 3]).toBe(255);
    expect(boundsOf(frame)).toEqual({ left: 1, top: 1, right: 5, bottom: 4 });

    // A shadow cast on the green becomes a see-through black one. At the cut's edge, an outline pixel half on the green
    // comes out half see-through in its own colour; the same colour further in is left alone.
    const shadow = pixels(['s'], { s: [0, 158, 0, 255] });
    keyOut(shadow, { r: 0, g: 255, b: 0, tolerance: 40 });
    expect([...shadow.data]).toEqual([0, 0, 0, 97]);
    const line = pixels(['geoooom'], { g: [0, 255, 0, 255], e: [50, 157, 20, 255], o: [100, 60, 40, 255], m: [50, 157, 20, 255] });
    keyOut(line, { r: 0, g: 255, b: 0, tolerance: 40 });
    const [er, eg, eb, ea] = line.data.subarray(4, 8);
    expect(Math.abs(er! - 100) + Math.abs(eg! - 60) + Math.abs(eb! - 40)).toBeLessThan(40);
    expect(ea).toBeGreaterThan(110);
    expect(ea).toBeLessThan(150);
    expect([...line.data.subarray(24, 28)]).toEqual([50, 157, 20, 255]);

    // Idle 100 tall, a jump clip taller and narrower: both stand on the same feet, scaled to 50 tall.
    const layout = layoutClips(
      [
        { box: { left: 10, top: 20, right: 70, bottom: 120 }, scale: 100 },
        { box: { left: 0, top: 0, right: 40, bottom: 160 }, scale: 100 },
      ],
      0,
      50,
    );
    expect(layout.tall).toBe(50);
    expect(layout.anchor).toEqual([layout.width / 2, layout.height - 2]);
    expect(layout.placements[0]).toMatchObject({ scale: 0.5, x: layout.anchor[0] - 15, y: layout.anchor[1] - 50 });
    expect(layout.placements[1]).toMatchObject({ scale: 0.5, x: layout.anchor[0] - 10, y: layout.anchor[1] - 80 });
    expect(layout.height).toBe(84);
    expect(['frame10.png', 'frame2.png', 'Frame1.png'].sort(naturalOrder)).toEqual(['Frame1.png', 'frame2.png', 'frame10.png']);
  });

  it('plays a pose’s opening once, then its settled part forwards and back (as the recorder)', () => {
    const clip = { fps: 10, frames: Array.from({ length: 10 }, () => new Uint8Array()), loop: 6, end: 8, bounce: true };
    const at = (n: number) => clipFrame(clip, n / 10);
    expect(Array.from({ length: 16 }, (_, n) => at(n))).toEqual([0, 1, 2, 3, 4, 5, 6, 7, 8, 7, 6, 7, 8, 7, 6, 7]);
    // Forwards and back from the start; as it was without them.
    expect(Array.from({ length: 8 }, (_, n) => clipFrame({ fps: 10, frames: clip.frames.slice(0, 4), bounce: true }, n / 10))).toEqual([0, 1, 2, 3, 2, 1, 0, 1]);
    expect(Array.from({ length: 6 }, (_, n) => clipFrame({ fps: 10, frames: clip.frames.slice(0, 4) }, n / 10))).toEqual([0, 1, 2, 3, 0, 1]);
  });

  it('keeps every height still drawn: one character shown at three sizes decodes each once', async () => {
    const pack = { ...readPack(await testCharacter())!, width: 300, height: 400, tall: 300 };
    let decodes = 0;
    vi.stubGlobal('createImageBitmap', () => {
      decodes++;
      return Promise.resolve({ close: () => {} });
    });
    const frames = new CharacterFrames(pack);
    const all = pack.clips.reduce((n, c) => n + c.frames.length, 0);
    for (let turn = 0; turn < 5; turn++) {
      for (const h of [30, 90, 200]) {
        frames.frame('idle', turn / 10, h);
        await frames.prepare(h);
      }
    }
    expect(decodes).toBe(all * 3);
    for (const h of [30, 90, 200]) expect(frames.frame('walk', 0.2, h)).not.toBeNull();
  });

  it('wakes a preview waiting for its frames once, even when its redraw waits again (that hung the page)', async () => {
    const pack = { ...readPack(await testCharacter())!, width: 300, height: 400, tall: 300 };
    let decodes = 0;
    vi.stubGlobal('createImageBitmap', () => {
      decodes++;
      // The first frames cannot be decoded: each redraw waits again.
      return decodes <= 2 ? Promise.reject(new Error('bad frame')) : Promise.resolve({ close: () => {} });
    });
    const frames = new CharacterFrames(pack);
    let redraws = 0;
    const draw = () => {
      redraws++;
      if (redraws < 1000) frames.frame('idle', 0, 90, () => draw());
    };
    draw();
    await new Promise((r) => setTimeout(r, 300));
    expect(redraws).toBeLessThan(50);
  });

  it('decodes every frame at once when first shown, and again only for a quite different size', async () => {
    // Frames as tall as a generated character's (the test drawing's are tiny).
    const pack = { ...readPack(await testCharacter())!, width: 300, height: 400, tall: 300 };
    const decoded: number[] = [];
    vi.stubGlobal('createImageBitmap', (_: Blob, o: { resizeHeight: number }) => {
      decoded.push(o.resizeHeight);
      return Promise.resolve({ height: o.resizeHeight, close: () => {} });
    });
    const frames = new CharacterFrames(pack);
    const all = pack.clips.reduce((n, c) => n + c.frames.length, 0);
    // The first ask starts them all; nothing is drawn blank once they are in.
    frames.frame('idle', 0, 90);
    await frames.prepare(90);
    expect(decoded).toHaveLength(all);
    for (const clip of pack.clips) for (let t = 0; t < 2; t += 0.05) expect(frames.frame(clip.action, t, 90)).not.toBeNull();
    // A slightly different size draws from the same frames.
    frames.frame('walk', 0.3, 80);
    await frames.prepare(80);
    expect(decoded).toHaveLength(all);
    // A much smaller one decodes them again at its height, showing the ones kept meanwhile.
    expect(frames.frame('walk', 0.3, 30)).not.toBeNull();
    await frames.prepare(30);
    expect(decoded.length).toBe(all * 2);
    expect(decoded.slice(all).every((h) => h === 48)).toBe(true);
  });

  it('stays, paces, wanders and runs around its panel, the same at the same moment', () => {
    const at = (over: Partial<OverlayCharacter>, t: number) => frameOf(character(over), panelBox, screenBox, 64, t);
    const top = lineOf('top', panelBox, screenBox, 64);
    const stay = at({ movement: 'stay', x: 0.25, action: 'sleep', flip: true }, 9);
    expect(stay).toMatchObject({ x: top.x0 + (top.x1 - top.x0) * 0.25, y: panelBox.top, action: 'sleep', facing: -1 });
    const xs = Array.from({ length: 200 }, (_, i) => at({ movement: 'pace' }, i * 0.1));
    expect(Math.min(...xs.map((f) => f.x))).toBeCloseTo(top.x0, 0);
    expect(Math.max(...xs.map((f) => f.x))).toBeCloseTo(top.x1, 0);
    expect(new Set(xs.map((f) => f.action))).toEqual(new Set(['walk', 'idle']));
    for (let t = 0; t < 120; t += 0.7) {
      const f = at({ place: 'inside', movement: 'wander' }, t);
      expect(f.x).toBeGreaterThanOrEqual(panelBox.left);
      expect(f.x).toBeLessThanOrEqual(panelBox.right);
    }
    const around = Array.from({ length: 600 }, (_, i) => at({ place: 'around' }, i * 0.1));
    expect(around.some((f) => f.y === panelBox.top)).toBe(true);
    expect(around.some((f) => f.y === panelBox.bottom && f.x > panelBox.right)).toBe(true);
    expect(around.some((f) => f.y === panelBox.bottom && f.x < panelBox.left)).toBe(true);
    expect(at({ movement: 'wander' }, 33.3)).toEqual(at({ movement: 'wander' }, 33.3));
    expect(at({ movement: 'wander', id: 'char-2' }, 33.3)).not.toEqual(at({ movement: 'wander' }, 33.3));
    // Kept on its screen: on a panel at the screen's top edge it stands just inside, not above it out of sight.
    const high = { ...panelBox, top: screenBox.top, bottom: screenBox.top + 120 };
    for (let t = 0; t < 60; t += 1.3) expect(frameOf(character({ place: 'top' }), high, screenBox, 64, t).y).toBeGreaterThanOrEqual(screenBox.top + 64);
    // Kept to its area: a wanderer from 25 % to 50 % of the panel's top never leaves it.
    const kept = Array.from({ length: 400 }, (_, i) => at({ movement: 'wander', from: 0.25, to: 0.5 }, i * 0.3));
    const span = top.x1 - top.x0;
    expect(Math.min(...kept.map((f) => f.x))).toBeGreaterThanOrEqual(top.x0 + span * 0.25 - 1e-6);
    expect(Math.max(...kept.map((f) => f.x))).toBeLessThanOrEqual(top.x0 + span * 0.5 + 1e-6);
    // Only the animations chosen: it pauses doing those, runs when it may not walk, stays when it may do neither.
    const some = Array.from({ length: 900 }, (_, i) => at({ movement: 'wander', plays: ['idle', 'run', 'dance'] }, i * 0.2));
    expect(new Set(some.map((f) => f.action))).toEqual(new Set(['idle', 'run', 'dance']));
    const staying = Array.from({ length: 100 }, (_, i) => at({ movement: 'pace', action: 'walk', plays: ['idle', 'sit'] }, i * 0.5));
    expect(new Set(staying.map((f) => f.action))).toEqual(new Set(['idle']));
    expect(new Set(staying.map((f) => f.x)).size).toBe(1);
    // Its animations play at their own speed.
    expect(at({ movement: 'stay', tempo: 50 }, 10).seconds).toBeCloseTo(5);
  });

  it('keeps the file the recorder checks its motion against up to date', async () => {
    const cases = JSON.parse(JSON.stringify(motionCases()));
    // UPDATE_CHARACTER_PARITY=1 writes it again (after a change to the motion rules).
    const node = (globalThis as unknown as { process?: { env: Record<string, string | undefined>; cwd: () => string } }).process;
    if (node?.env.UPDATE_CHARACTER_PARITY) {
      const fs = (await import(/* @vite-ignore */ ['node', 'fs'].join(':'))) as { writeFileSync: (file: string, text: string) => void };
      fs.writeFileSync(`${node.cwd()}/src/features/overlay/characters/motion-parity.json`, JSON.stringify(cases));
      return;
    }
    expect(motion).toEqual(cases);
  });

  it('adds shipped characters to a panel, changes where they play, and removes them', async () => {
    const list: CharacterInfo[] = [
      { id: 'mochi', name: 'Mochi', shipped: true, actions: ['idle', 'walk', 'run', 'jump', 'play', 'sleep'], used: false, here: true, added: null },
      { id: 'biscuit', name: 'Biscuit', shipped: true, actions: ['idle', 'walk', 'run', 'jump', 'play', 'sleep'], used: false, here: true, added: null },
      { id: '0123456789abcdef.char', name: 'Hero', shipped: false, actions: ['idle', 'walk'], used: false, here: true, added: '2026-10-09T10:00:00Z' },
    ];
    const fetchMock = mockApi({ 'GET /api/v1/overlay/characters': () => list, 'DELETE /api/v1/overlay/characters/0123456789abcdef.char': () => undefined });
    const panel: OverlayPanel = { id: 'p1', title: null, x: 0, y: 0, layout: 'rows', readings: [], style: DEFAULT_STYLE };
    const onChange = vi.fn();
    const user = userEvent.setup();
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const ui = (p: OverlayPanel) => (
      <QueryClientProvider client={client}>
        <ConfirmProvider>
          <CharactersTab panel={p} onChange={onChange} />
        </ConfirmProvider>
      </QueryClientProvider>
    );
    const { rerender } = render(ui(panel));
    await user.click(screen.getByRole('button', { name: 'Add a character' }));
    const picker = screen.getByRole('dialog', { name: 'Characters' });
    await user.click(await within(picker).findByRole('button', { name: 'Biscuit' }));
    const added = onChange.mock.calls[0]![0] as OverlayCharacter[];
    expect(added).toEqual([expect.objectContaining({ id: 'char-1', character: 'biscuit', place: 'top', movement: 'wander', size: 48 })]);

    rerender(ui({ ...panel, characters: added }));
    const row = await screen.findByRole('listitem', { name: 'Biscuit' });
    await user.click(within(row).getByRole('radio', { name: 'All around' }));
    expect(onChange).toHaveBeenLastCalledWith([expect.objectContaining({ place: 'around' })]);
    await user.click(within(row).getByRole('button', { name: 'Remove Biscuit' }));
    await confirmIt();
    expect(onChange).toHaveBeenLastCalledWith([]);

    // The imported ones: listed apart, with a way to import more; one no panel has can be deleted.
    await user.click(screen.getByRole('button', { name: 'Add a character' }));
    const again = screen.getByRole('dialog', { name: 'Characters' });
    await user.click(within(again).getByRole('tab', { name: /Imported/ }));
    expect(within(again).getByRole('button', { name: 'Hero' })).toBeInTheDocument();
    expect(within(again).getByText('Import a character…')).toBeInTheDocument();
    await user.click(within(again).getByRole('button', { name: 'Delete Hero' }));
    await confirmIt();
    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledWith(expect.stringContaining('/overlay/characters/0123456789abcdef.char'), expect.objectContaining({ method: 'DELETE' })));
    await user.click(within(again).getByText('Import a character…'));
    const importer = screen.getByRole('dialog', { name: 'Import a character' });
    expect(within(importer).getByRole('listitem', { name: 'Idle' })).toBeInTheDocument();
    expect(within(importer).getByRole('button', { name: 'Import' })).toBeDisabled();
  });
});
