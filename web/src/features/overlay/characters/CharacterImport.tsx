import { useQueryClient } from '@tanstack/react-query';
import { X } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { Button } from '@/components/ui/button';
import { Dialog } from '@/components/ui/dialog';
import { Check, Segmented, Slider } from '../PanelInspector';
import { uploadBlob } from '../useOverlay';
import { onEveryFrame, reducedMotion } from './CharacterSprite';
import { backgroundOf, buildCharacter, decodeClip, type KeyColour, type SheetGrid, type SourceClip } from './importer';
import { CharacterFrames, keepCharacter } from './library';
import type { CharacterPack } from './pack';

/** The actions a character may have an animation for, in the order offered (idle is needed). */
export const IMPORT_ACTIONS: readonly (readonly [string, string])[] = [
  ['idle', 'Idle'],
  ['walk', 'Walk'],
  ['run', 'Run'],
  ['jump', 'Jump'],
  ['play', 'Play'],
  ['dance', 'Dance'],
  ['sit', 'Sit'],
  ['sleep', 'Sleep'],
];

const ACCEPTED = 'image/gif,image/webp,image/png,image/apng,image/avif,image/jpeg,video/webm,video/mp4,video/quicktime';

interface Chosen {
  files: File[];
  fps: number;
  grid: SheetGrid | null;
  scale: number;
  decoded: { fps: number; frames: ImageBitmap[] } | null;
  /** One picture with no animation: it may be a sprite sheet. */
  still: boolean;
  error: string | null;
}

const hex = (k: KeyColour) => `#${[k.r, k.g, k.b].map((v) => v.toString(16).padStart(2, '0')).join('')}`;
const fromHex = (text: string, tolerance: number): KeyColour => ({ r: parseInt(text.slice(1, 3), 16), g: parseInt(text.slice(3, 5), 16), b: parseInt(text.slice(5, 7), 16), tolerance });

/**
 * Importing a character: an animation for each thing it does (idle at least), a background colour taken out when the
 * animations have one, the way the drawing faces; a preview of the result as it will play, then the character added
 * to the library and chosen.
 */
export function CharacterImport({ onClose, onImported }: { onClose: () => void; onImported: (id: string) => void }) {
  const client = useQueryClient();
  const [name, setName] = useState('');
  const [faces, setFaces] = useState<'1' | '-1'>('1');
  const [chosen, setChosen] = useState<Record<string, Chosen>>({});
  const [key, setKey] = useState<{ on: boolean; colour: KeyColour; touched: boolean }>({ on: false, colour: { r: 0, g: 255, b: 0, tolerance: 48 }, touched: false });
  const [colours, setColours] = useState<'256' | 'full'>('256');
  const [busy, setBusy] = useState<{ what: string; done: number } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [preview, setPreview] = useState<{ pack: CharacterPack; bytes: Uint8Array; made: string } | null>(null);

  // What the result is made from (a preview made from other settings is made again).
  const settings = JSON.stringify([name, faces, colours, key.on && key.colour, Object.entries(chosen).map(([a, c]) => [a, c.files.map((f) => f.name + f.size), c.fps, c.grid, c.scale])]);

  const decode = async (action: string, next: Chosen) => {
    setChosen((all) => ({ ...all, [action]: { ...next, decoded: null, error: null } }));
    try {
      const decoded = await decodeClip(next.files, { fps: next.fps, grid: next.grid });
      const single = next.files.length === 1 && !next.files[0]!.type.startsWith('video/');
      setChosen((all) => ({ ...all, [action]: { ...next, decoded, still: single && decoded.frames.length === 1 && !next.grid ? true : next.still && !!next.grid, error: null } }));
      // A flat background (opaque corners alike): offered to be taken out.
      if (!key.touched && decoded.frames[0]) {
        const f = decoded.frames[0];
        const canvas = new OffscreenCanvas(f.width, f.height);
        const ctx = canvas.getContext('2d');
        if (ctx) {
          ctx.drawImage(f, 0, 0);
          const found = backgroundOf({ data: ctx.getImageData(0, 0, f.width, f.height).data, width: f.width, height: f.height });
          if (found) setKey({ on: true, colour: found, touched: false });
        }
      }
    } catch (e) {
      setChosen((all) => ({ ...all, [action]: { ...next, decoded: null, error: e instanceof Error ? e.message : 'That file could not be read.' } }));
    }
  };

  const choose = (action: string, files: File[]) => {
    if (!files.length) return;
    if (!name) setName(files[0]!.name.replace(/[-_ ]*(idle|walk|run|jump|play|dance|sit|sleep)?\.[^.]+$/i, '').slice(0, 60));
    void decode(action, { files, fps: 12, grid: null, scale: 100, decoded: null, still: false, error: null });
  };

  const clips = (): SourceClip[] =>
    IMPORT_ACTIONS.flatMap(([action]) => {
      const c = chosen[action];
      return c?.decoded ? [{ action, fps: c.decoded.fps, frames: c.decoded.frames, scale: c.scale }] : [];
    });

  const make = async () => {
    setError(null);
    setBusy({ what: 'Reading the frames', done: 0 });
    try {
      const made = await buildCharacter(name || 'Character', clips(), { faces: faces === '1' ? 1 : -1, key: key.on ? key.colour : null, fullColour: colours === 'full', onProgress: (what, done) => setBusy({ what, done }) });
      setPreview({ ...made, made: settings });
      return made;
    } catch (e) {
      setError(e instanceof Error ? e.message : 'The character could not be made.');
      return null;
    } finally {
      setBusy(null);
    }
  };

  const save = async () => {
    const made = preview?.made === settings ? preview : await make();
    if (!made) return;
    setBusy({ what: 'Saving the character', done: 1 });
    try {
      const saved = await uploadBlob(new Blob([made.bytes as BlobPart], { type: 'application/octet-stream' }), { name: made.pack.name });
      keepCharacter(saved.id, made.pack);
      await client.invalidateQueries({ queryKey: ['overlay-characters'] });
      onImported(saved.id);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'The character could not be saved.');
    } finally {
      setBusy(null);
    }
  };

  const ready = !!chosen.idle?.decoded && !busy;
  return (
    <Dialog
      open
      title="Import a character"
      description="An animation for each thing it does: idle at least. Import only characters you made or have the right to use."
      onClose={onClose}
      className="max-w-3xl"
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button variant="outline" disabled={!ready} onClick={() => void make()}>
            Preview
          </Button>
          <Button disabled={!ready} onClick={() => void save()}>
            Import
          </Button>
        </>
      }
    >
      <div className="flex max-h-[70vh] flex-col gap-3 overflow-y-auto pr-1 text-sm">
        <label className="flex items-center gap-3 text-xs text-muted">
          <span className="w-32 shrink-0">Name</span>
          <input value={name} maxLength={60} onChange={(e) => setName(e.target.value)} placeholder="Its name" className="h-8 flex-1 rounded-md border border-border bg-background px-2 text-sm text-foreground" aria-label="Name" />
        </label>
        <ol aria-label="Its animations" className="flex flex-col gap-1.5">
          {IMPORT_ACTIONS.map(([action, label]) => (
            <ClipRow key={action} action={action} label={label} chosen={chosen[action]} onChoose={(files) => choose(action, files)} onChange={(next, again) => (again ? void decode(action, next) : setChosen((all) => ({ ...all, [action]: next })))} onRemove={() => setChosen(({ [action]: _, ...rest }) => rest)} />
          ))}
        </ol>
        <p className="text-xs text-muted">
          Animated GIF, WebP or PNG; a video (WebM with transparency, or one on a flat background); several PNG frames at once; or one picture of frames in a grid (a sprite sheet). Actions without an animation play the closest one it has. Live2D models: export their motions as GIF or WebM first.
        </p>
        <div className="flex flex-col gap-2 rounded-md border border-border p-2">
          <div className="flex flex-wrap items-center gap-3 text-xs">
            <Check label="Take out a background colour" checked={key.on} onChange={(on) => setKey({ ...key, on, touched: true })} />
            {key.on && (
              <label className="flex items-center gap-1.5 text-muted">
                Colour
                <input type="color" value={hex(key.colour)} onChange={(e) => setKey({ ...key, colour: fromHex(e.target.value, key.colour.tolerance), touched: true })} aria-label="The background colour" className="h-6 w-8 rounded border border-border" />
              </label>
            )}
          </div>
          {key.on && <Slider label="How close counts" value={key.colour.tolerance} min={4} max={160} step={2} onChange={(tolerance) => setKey({ ...key, colour: { ...key.colour, tolerance }, touched: true })} />}
          <div className="flex flex-wrap items-center gap-3 text-xs text-muted">
            <span className="w-32 shrink-0">The drawing faces</span>
            <Segmented
              label="The drawing faces"
              value={faces}
              options={[
                ['1', 'Right'],
                ['-1', 'Left'],
              ]}
              onChange={setFaces}
            />
          </div>
          <div className="flex flex-wrap items-center gap-3 text-xs text-muted">
            <span className="w-32 shrink-0">Colours</span>
            <Segmented
              label="Colours"
              value={colours}
              options={[
                ['256', '256 (smaller)'],
                ['full', 'Full (sharper, larger)'],
              ]}
              onChange={setColours}
            />
          </div>
        </div>
        {busy && (
          <div role="status" className="flex items-center gap-2 text-xs text-muted">
            <span>{busy.what}…</span>
            <span className="h-1.5 flex-1 overflow-hidden rounded bg-surface-muted">
              <span className="block h-full bg-accent" style={{ width: `${Math.round(busy.done * 100)}%` }} />
            </span>
          </div>
        )}
        {error && (
          <p role="alert" className="text-xs text-danger">
            {error}
          </p>
        )}
        {preview && <PackPreview pack={preview.pack} bytes={preview.bytes.length} stale={preview.made !== settings} />}
      </div>
    </Dialog>
  );
}

function ClipRow({ action, label, chosen, onChoose, onChange, onRemove }: { action: string; label: string; chosen: Chosen | undefined; onChoose: (files: File[]) => void; onChange: (next: Chosen, decodeAgain: boolean) => void; onRemove: () => void }) {
  const input = useRef<HTMLInputElement>(null);
  const video = chosen?.files.length === 1 && chosen.files[0]!.type.startsWith('video/');
  const frames = chosen?.decoded?.frames.length ?? 0;
  return (
    <li aria-label={label} className="flex flex-col gap-1.5 rounded-md border border-border p-2">
      <div className="flex items-center gap-2">
        <span className="w-16 shrink-0 text-xs font-medium">
          {label}
          {action === 'idle' && <span className="text-muted"> *</span>}
        </span>
        <input ref={input} type="file" accept={ACCEPTED} multiple hidden aria-label={`${label}: its animation`} onChange={(e) => onChoose([...(e.target.files ?? [])])} />
        <Button size="sm" variant="outline" onClick={() => input.current?.click()}>
          {chosen ? 'Another…' : 'Choose…'}
        </Button>
        <span className="min-w-0 flex-1 truncate text-xs text-muted">
          {!chosen
            ? action === 'idle'
              ? 'Needed: standing, breathing'
              : 'Optional'
            : chosen.error
              ? chosen.error
              : chosen.decoded
                ? `${chosen.files.length > 1 ? `${chosen.files.length} pictures` : chosen.files[0]!.name} · ${frames} frame${frames === 1 ? '' : 's'} · ${chosen.decoded.fps} a second`
                : 'Reading…'}
        </span>
        {chosen && (
          <Button size="icon" variant="ghost" aria-label={`No ${label.toLowerCase()} animation`} title="Remove" onClick={onRemove}>
            <X aria-hidden />
          </Button>
        )}
      </div>
      {chosen?.decoded && (
        <div className="flex flex-col gap-1.5 pl-[4.5rem]">
          {(chosen.still || chosen.grid) && (
            <div className="flex flex-wrap items-center gap-2 text-xs text-muted">
              <span>A sprite sheet:</span>
              {(['columns', 'rows'] as const).map((k) => (
                <label key={k} className="flex items-center gap-1">
                  {k}
                  <input
                    type="number"
                    min={1}
                    max={40}
                    value={chosen.grid?.[k] ?? 1}
                    aria-label={`The sheet's ${k}`}
                    onChange={(e) => {
                      const grid = { columns: chosen.grid?.columns ?? 1, rows: chosen.grid?.rows ?? 1, count: 0, [k]: Math.max(1, Math.min(40, Number(e.target.value) || 1)) };
                      onChange({ ...chosen, grid: grid.columns * grid.rows > 1 ? grid : null, still: true }, true);
                    }}
                    className="h-7 w-14 rounded border border-border bg-background px-1 text-foreground"
                  />
                </label>
              ))}
            </div>
          )}
          {(video || chosen.files.length > 1 || chosen.grid) && (
            <Slider label="Frames a second" value={chosen.fps} min={2} max={30} step={1} onChange={(fps) => onChange({ ...chosen, fps, decoded: video ? chosen.decoded : { ...chosen.decoded!, fps } }, video)} />
          )}
          <Slider label="Its size" value={chosen.scale} min={50} max={200} step={5} unit=" %" onChange={(scale) => onChange({ ...chosen, scale }, false)} />
        </div>
      )}
    </li>
  );
}

/** The character made, every clip playing side by side on the ground, as it will play on the screen. */
function PackPreview({ pack, bytes, stale }: { pack: CharacterPack; bytes: number; stale: boolean }) {
  const ref = useRef<HTMLCanvasElement>(null);
  useEffect(() => {
    const el = ref.current;
    const ctx = el?.getContext('2d');
    if (!el || !ctx) return undefined;
    const frames = new CharacterFrames(pack);
    const cell = 120;
    const tall = 84;
    const dpr = typeof devicePixelRatio === 'number' ? devicePixelRatio : 1;
    el.width = Math.round(cell * pack.clips.length * dpr);
    el.height = Math.round(150 * dpr);
    el.style.width = `${cell * pack.clips.length}px`;
    el.style.height = '150px';
    let last = 0;
    const draw = (now: number) => {
      last = now;
      ctx.clearRect(0, 0, el.width, el.height);
      pack.clips.forEach((clip, i) => {
        const x = (i + 0.5) * cell * dpr;
        const ground = 128 * dpr;
        ctx.strokeStyle = 'rgba(127,127,127,0.5)';
        ctx.beginPath();
        ctx.moveTo(i * cell * dpr + 8, ground);
        ctx.lineTo((i + 1) * cell * dpr - 8, ground);
        ctx.stroke();
        frames.draw(ctx, clip.action, now / 1000, 1, tall * dpr, x, ground, () => draw(last));
        ctx.fillStyle = 'rgba(127,127,127,0.9)';
        ctx.font = `${11 * dpr}px system-ui`;
        ctx.textAlign = 'center';
        ctx.fillText(`${clip.action} · ${clip.frames.length}`, x, 146 * dpr);
      });
    };
    draw(reducedMotion() ? 1200 : performance.now());
    return reducedMotion() ? undefined : onEveryFrame(draw);
  }, [pack]);
  return (
    <figure className="flex flex-col gap-1">
      <div className="overflow-x-auto rounded-md border border-border bg-[repeating-conic-gradient(rgba(127,127,127,0.12)_0_25%,transparent_0_50%)] bg-[length:16px_16px]">
        <canvas ref={ref} aria-label={`${pack.name}, as it will play`} />
      </div>
      <figcaption className="text-xs text-muted">
        {pack.name} · {pack.width} × {pack.height} · {(bytes / 1024 / 1024).toFixed(1)} MB{stale ? ' · settings changed since: Import makes it again' : ''}
      </figcaption>
    </figure>
  );
}
