import { CloudDownload, Film, ImagePlus, Layers, Sparkles, Trash2, Upload } from 'lucide-react';
import { useRef, useState, type DragEvent } from 'react';
import { Button } from '@/components/ui/button';
import { useConfirm } from '@/components/ui/confirm';
import { Dialog } from '@/components/ui/dialog';
import type { LibraryPicture, OverlayGeneratedArt, OverlayPanel, OverlaySource, OverlayStyle } from '@/lib/api/types';
import { cn } from '@/lib/utils';
import { useFileLocations } from '@/features/storage/useStorage';
import { artLook } from './artDesigns';
import { GENERATIVE, GENERATIVE_KINDS } from './generative';
import { ACCEPTED, bytes, uploadToLibrary, useDeletePicture, useFetchPicture, useLibrary } from './library';
import { LiveArt, isAnimationId, isGenerative } from './liveArt';
import { imageUrl, type History } from './model';
import { ShrunkPreview } from './PanelPreview';

type Tab = 'scenes' | 'library';

export interface BackgroundChooserProps {
  open: boolean;
  panel: OverlayPanel;
  sourceMap: ReadonlyMap<string, OverlaySource>;
  history?: History;
  onClose: () => void;
  onChange: (patch: Partial<OverlayStyle>) => void;
}

/** Whether glass cards can be laid over the readings: tiles at a fixed size (the cards follow the tiles). */
export const glassFits = (panel: Pick<OverlayPanel, 'layout' | 'width' | 'height'>) => panel.layout === 'tiles' && panel.width != null && panel.height != null;

/**
 * Choosing a panel's background: a moving scene generated for it (frosted glass cards over its readings), or a
 * picture of the library — an upload (a photo, an animated GIF, WebP or PNG, or a short video), kept on this PC and
 * copied to the storage locations that keep pictures — shown as it is or with glass cards over the readings.
 */
export function BackgroundChooser({ open, panel, sourceMap, history, onClose, onChange }: BackgroundChooserProps) {
  const [tab, setTab] = useState<Tab>(isGenerative(panel.style.art) || !panel.style.image ? 'scenes' : 'library');
  const fits = glassFits(panel);
  const choose = (patch: Partial<OverlayStyle>) => {
    onChange(patch);
    onClose();
  };
  return (
    <Dialog open={open} title="Background" description="A moving scene made for this panel, or a picture of your library." onClose={onClose} className="max-w-4xl">
      <div className="flex flex-col gap-3">
        <div role="tablist" aria-label="Kind of background" className="flex gap-1 rounded-lg bg-surface-muted p-1 text-sm">
          {(
            [
              ['scenes', 'Moving scenes', Sparkles],
              ['library', 'Pictures and animations', ImagePlus],
            ] as const
          ).map(([id, name, Icon]) => (
            <button
              key={id}
              type="button"
              role="tab"
              aria-selected={tab === id}
              onClick={() => setTab(id)}
              className={cn('flex flex-1 items-center justify-center gap-1.5 rounded-md px-3 py-1.5', tab === id ? 'bg-surface font-medium shadow-sm' : 'text-muted hover:text-fg')}
            >
              <Icon className="size-4" aria-hidden /> {name}
            </button>
          ))}
        </div>
        {tab === 'scenes' ? <Scenes panel={panel} sourceMap={sourceMap} history={history} fits={fits} onChoose={choose} /> : <Library panel={panel} fits={fits} onChoose={choose} />}
      </div>
    </Dialog>
  );
}

/** The generated scenes, compact: each a small live preview of this panel's own readings on it (moving under the mouse). */
function Scenes({ panel, sourceMap, history, fits, onChoose }: { panel: OverlayPanel; sourceMap: ReadonlyMap<string, OverlaySource>; history?: History; fits: boolean; onChoose: (patch: Partial<OverlayStyle>) => void }) {
  const sized = panel.width != null && panel.height != null;
  const wide = !sized || panel.width! >= panel.height!;
  return (
    <div className="flex flex-col gap-2">
      <p className="text-xs text-muted">
        {GENERATIVE_KINDS.length} scenes drawn on the graphics card, each a seamless loop. {fits ? 'Each tile sits on frosted glass with a light running round its edge.' : 'Give the panel tiles and a fixed size (Layout) for frosted glass under its readings.'} Point at one to see it move.
      </p>
      {!sized && <p className="rounded-md border border-warning/40 bg-warning/10 px-2 py-1 text-xs">The panel needs a fixed size (Layout › Size) for the recorder to show a moving scene.</p>}
      <div role="list" aria-label="Moving scenes" className={cn('grid max-h-[56vh] gap-1.5 overflow-y-auto pr-1', wide ? 'grid-cols-3 md:grid-cols-4' : 'grid-cols-5 md:grid-cols-7')}>
        {GENERATIVE_KINDS.map((kind) => {
          const look = artLook(kind);
          const preview: OverlayPanel = { ...panel, id: `scene-${kind}`, style: { ...panel.style, ...look } as OverlayStyle };
          const current = panel.style.art === kind;
          return (
            <button
              key={kind}
              type="button"
              role="listitem"
              disabled={!sized}
              aria-pressed={current}
              aria-label={GENERATIVE[kind].name}
              onClick={() => onChoose(look)}
              title={`${GENERATIVE[kind].name}: ${GENERATIVE[kind].description}`}
              className={cn('motion-hover group flex flex-col overflow-hidden rounded-md border bg-surface text-left transition-colors disabled:opacity-50', current ? 'border-accent ring-2 ring-accent/30' : 'border-border hover:border-accent/60')}
            >
              <span className={cn('flex items-center justify-center overflow-hidden bg-black', wide ? 'h-[62px]' : 'h-[118px]')}>
                <ShrunkPreview panel={preview} sources={sourceMap} history={history} scale={1} fit={wide ? { width: 196, height: 58 } : { width: 76, height: 114 }} />
              </span>
              <span className="flex items-center gap-1.5 px-1.5 py-1">
                <span aria-hidden className="h-2 w-4 shrink-0 rounded-full" style={{ background: `linear-gradient(90deg, ${GENERATIVE[kind as OverlayGeneratedArt].palette.slice(1).join(', ')})` }} />
                <span className="truncate text-[11px] font-medium">{GENERATIVE[kind].name}</span>
              </span>
            </button>
          );
        })}
      </div>
    </div>
  );
}

/** The library: upload, choose, bring back from storage, delete. */
function Library({ panel, fits, onChoose }: { panel: OverlayPanel; fits: boolean; onChoose: (patch: Partial<OverlayStyle>) => void }) {
  const library = useLibrary();
  const locations = useFileLocations();
  const remove = useDeletePicture();
  const confirm = useConfirm();
  const fetchPicture = useFetchPicture();
  const input = useRef<HTMLInputElement>(null);
  const [glass, setGlass] = useState(panel.style.art === 'glass' || fits);
  const [made, setMade] = useState(false);
  const [progress, setProgress] = useState<{ what: string; done: number } | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const [dragging, setDragging] = useState(false);

  const current = panel.style.art === 'glass' ? panel.style.artSource : !panel.style.art ? panel.style.image : null;
  const pick = (id: string) =>
    onChoose(
      glass && fits
        ? { ...artLook('glass'), artSource: id }
        : { art: null, artSource: null, image: id, imageFit: 'cover', imageOpacity: 100, imageBlur: 0 },
    );

  const upload = async (file: File | undefined) => {
    if (!file) return;
    setProblem(null);
    setProgress({ what: 'Reading', done: 0 });
    try {
      const picture = await uploadToLibrary(file, (what, done) => setProgress({ what, done }));
      await library.refetch();
      pick(picture.id);
    } catch (e) {
      setProblem((e as Error).message);
    } finally {
      setProgress(null);
      if (input.current) input.current.value = '';
    }
  };
  const drop = (e: DragEvent) => {
    e.preventDefault();
    setDragging(false);
    void upload(e.dataTransfer.files[0]);
  };

  // Where uploads are copied (the storage locations that keep pictures besides this PC).
  const elsewhere = (locations.data?.locations ?? []).filter((l) => l.location.kind !== 'pc' && l.location.keeps.includes('pictures')).map((l) => l.location.name);
  const pictures = (library.data ?? []).filter((p) => made || p.kind === 'upload');

  return (
    <div className="flex flex-col gap-3">
      <div
        onDragOver={(e) => {
          e.preventDefault();
          setDragging(true);
        }}
        onDragLeave={() => setDragging(false)}
        onDrop={drop}
        className={cn('flex flex-wrap items-center gap-3 rounded-lg border border-dashed p-3', dragging ? 'border-accent bg-accent/10' : 'border-border')}
      >
        <input ref={input} type="file" accept={ACCEPTED} className="hidden" aria-label="Picture, animation or video file" onChange={(e) => void upload(e.target.files?.[0])} />
        <Button size="sm" onClick={() => input.current?.click()} disabled={progress != null}>
          <Upload aria-hidden /> Upload…
        </Button>
        <span className="min-w-0 flex-1 text-xs text-muted">
          {progress ? (
            <span role="status" className="flex items-center gap-2">
              {progress.what}… {Math.round(progress.done * 100)} %
              <span className="h-1.5 w-32 overflow-hidden rounded-full bg-surface-muted">
                <span className="block h-full bg-accent transition-[width]" style={{ width: `${progress.done * 100}%` }} />
              </span>
            </span>
          ) : (
            <>
              A photo or artwork, an animated GIF, WebP or PNG, or a short video (MP4, WebM; up to 20 s, made to loop) — or drop it here. Kept on this PC
              {elsewhere.length ? <> and copied to {elsewhere.join(', ')}</> : <> (add a storage location under <a className="underline" href="/storage">Storage</a> to keep a copy elsewhere)</>}.
            </>
          )}
        </span>
      </div>
      {problem && <p className="text-xs text-danger">{problem}</p>}

      <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-sm">
        <label className={cn('flex items-center gap-2', !fits && 'opacity-50')} title={fits ? undefined : 'For a panel of tiles with a fixed size (Layout).'}>
          <input type="checkbox" checked={glass && fits} disabled={!fits} onChange={(e) => setGlass(e.target.checked)} />
          <Layers className="size-4 text-muted" aria-hidden /> Frosted glass cards over the readings
        </label>
        <label className="flex items-center gap-2 text-xs text-muted">
          <input type="checkbox" checked={made} onChange={(e) => setMade(e.target.checked)} /> Show pictures made for panels
        </label>
      </div>

      {library.isLoading ? (
        <p className="text-sm text-muted">Loading the library…</p>
      ) : pictures.length === 0 ? (
        <p className="rounded-lg border border-border p-6 text-center text-sm text-muted">No pictures yet. Upload one, or choose a moving scene.</p>
      ) : (
        <div className="grid max-h-[50vh] grid-cols-2 gap-2 overflow-y-auto pr-1 sm:grid-cols-3 md:grid-cols-4">
          {pictures.map((p) => (
            <LibraryCard
              key={p.id}
              picture={p}
              current={p.id === current}
              busy={fetchPicture.isPending && fetchPicture.variables === p.id}
              onChoose={() => pick(p.id)}
              onFetch={() => fetchPicture.mutate(p.id)}
              onDelete={async () => {
                if (await confirm({ title: `Delete “${p.name ?? p.id}”?`, description: 'It is deleted from the library, on this PC and in every storage location.' })) remove.mutate(p.id);
              }}
            />
          ))}
        </div>
      )}
      {remove.error && <p className="text-xs text-danger">{(remove.error as Error).message}</p>}
      {fetchPicture.error && <p className="text-xs text-danger">{(fetchPicture.error as Error).message}</p>}
    </div>
  );
}

function LibraryCard({ picture: p, current, busy, onChoose, onFetch, onDelete }: { picture: LibraryPicture; current: boolean; busy: boolean; onChoose: () => void; onFetch: () => void; onDelete: () => void }) {
  const animation = isAnimationId(p.id);
  // An animation plays (on a canvas) while the mouse is on it.
  const thumb: OverlayPanel = { id: `library-${p.id}`, title: null, x: 0, y: 0, layout: 'rows', readings: [], style: { image: p.id, art: null, imageFit: 'cover', imageOpacity: 100 } as OverlayStyle };
  return (
    <div className={cn('motion-hover group relative flex flex-col overflow-hidden rounded-lg border bg-surface', current ? 'border-accent ring-2 ring-accent/30' : 'border-border hover:border-accent/60')}>
      <button type="button" onClick={onChoose} disabled={!p.here} className="relative block h-28 overflow-hidden bg-black text-left disabled:cursor-default" aria-label={`Use ${p.name ?? 'this picture'}`}>
        {p.here ? animation ? <LiveArt panel={thumb} /> : <img src={imageUrl(p.id)} alt="" loading="lazy" className="size-full object-cover" /> : <span className="flex size-full items-center justify-center text-xs text-white/70">In storage only</span>}
        {animation && (
          <span className="absolute left-1.5 top-1.5 flex items-center gap-1 rounded bg-black/60 px-1.5 py-0.5 text-[10px] text-white">
            <Film className="size-3" aria-hidden /> {p.frames > 0 ? `${p.frames} frames` : 'Animation'}
          </span>
        )}
        {p.used && <span className="absolute right-1.5 top-1.5 rounded bg-accent px-1.5 py-0.5 text-[10px] font-medium text-white">In use</span>}
      </button>
      <div className="flex items-center gap-1 px-2 py-1.5">
        <span className="min-w-0 flex-1">
          <span className="block truncate text-xs font-medium">{p.name ?? (p.kind === 'generated' ? 'Made for a panel' : 'Picture')}</span>
          <span className="block text-[11px] text-muted">
            {p.width > 0 ? `${p.width} × ${p.height} · ` : ''}
            {bytes(p.size)}
          </span>
        </span>
        {!p.here && (
          <Button size="icon" variant="ghost" className="size-7" onClick={onFetch} disabled={busy} title="Bring it to this PC" aria-label={`Bring ${p.name ?? 'it'} to this PC`}>
            <CloudDownload className="size-4" aria-hidden />
          </Button>
        )}
        <Button size="icon" variant="ghost" className="size-7" onClick={onDelete} disabled={p.used} title={p.used ? 'A panel shows it' : 'Delete'} aria-label={`Delete ${p.name ?? 'it'}`}>
          <Trash2 className="size-4" aria-hidden />
        </Button>
      </div>
    </div>
  );
}
