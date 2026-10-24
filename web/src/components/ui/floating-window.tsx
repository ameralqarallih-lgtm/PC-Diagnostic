import { X } from 'lucide-react';
import { type PointerEvent as ReactPointerEvent, type ReactNode, useEffect, useId, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { readStored, writeStored } from '@/lib/useStoredState';

/** Where the window is and how big, in CSS pixels. */
interface Placement {
  x: number;
  y: number;
  width: number;
  height: number;
}

const MIN_WIDTH = 360;
const MIN_HEIGHT = 240;
const MARGIN = 8;

function isPlacement(value: unknown): value is Placement {
  const v = value as Partial<Placement> | null;
  return !!v && [v.x, v.y, v.width, v.height].every((n) => typeof n === 'number' && Number.isFinite(n));
}

/** Keeps the window on the screen and at least its minimum size (the screen may have changed since it was stored). */
function clamp(p: Placement): Placement {
  const width = Math.max(Math.min(p.width, window.innerWidth - 2 * MARGIN), Math.min(MIN_WIDTH, window.innerWidth - 2 * MARGIN));
  const height = Math.max(Math.min(p.height, window.innerHeight - 2 * MARGIN), Math.min(MIN_HEIGHT, window.innerHeight - 2 * MARGIN));
  return {
    width,
    height,
    x: Math.min(Math.max(p.x, MARGIN), Math.max(MARGIN, window.innerWidth - width - MARGIN)),
    y: Math.min(Math.max(p.y, MARGIN), Math.max(MARGIN, window.innerHeight - height - MARGIN)),
  };
}

function centered(): Placement {
  const width = Math.min(1000, window.innerWidth - 2 * MARGIN);
  const height = Math.min(720, window.innerHeight - 2 * MARGIN);
  return { width, height, x: (window.innerWidth - width) / 2, y: Math.max(MARGIN, (window.innerHeight - height) / 2) };
}

export interface FloatingWindowProps {
  open: boolean;
  title: string;
  onClose: () => void;
  children: ReactNode;
  /** Remembers the position and size in this browser under this key. */
  storageKey: string;
}

/**
 * A window over the page that does not block it: drag its title bar to move it, its corner to resize it; Escape or
 * the close button closes it. It keeps its position and size in this browser and always stays on the screen.
 */
export function FloatingWindow({ open, title, onClose, children, storageKey }: FloatingWindowProps) {
  const titleId = useId();
  const panel = useRef<HTMLDivElement>(null);
  const [placement, setPlacement] = useState<Placement>(() => clamp(readStored(storageKey, isPlacement) ?? centered()));
  const placementRef = useRef(placement);
  placementRef.current = placement;
  const closeRef = useRef(onClose);
  closeRef.current = onClose;

  // Escape closes it; a window resize keeps it on the screen.
  useEffect(() => {
    if (!open) return undefined;
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') closeRef.current();
    };
    const onResize = () => setPlacement((p) => clamp(p));
    document.addEventListener('keydown', onKey);
    window.addEventListener('resize', onResize);
    return () => {
      document.removeEventListener('keydown', onKey);
      window.removeEventListener('resize', onResize);
    };
  }, [open]);

  // The corner handle (CSS resize) changes the element's size: keep and store it.
  useEffect(() => {
    const element = panel.current;
    if (!open || !element || typeof ResizeObserver === 'undefined') return undefined;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const observer = new ResizeObserver(() => {
      const width = Math.round(element.offsetWidth);
      const height = Math.round(element.offsetHeight);
      const current = placementRef.current;
      if (Math.abs(width - current.width) < 1 && Math.abs(height - current.height) < 1) return;
      const next = { ...current, width, height };
      placementRef.current = next;
      clearTimeout(timer);
      timer = setTimeout(() => {
        setPlacement(next);
        writeStored(storageKey, next);
      }, 200);
    });
    observer.observe(element);
    return () => {
      observer.disconnect();
      clearTimeout(timer);
    };
  }, [open, storageKey]);

  const startDrag = (event: ReactPointerEvent<HTMLDivElement>) => {
    if (event.button !== 0 || (event.target as HTMLElement).closest('button')) return;
    const start = { px: event.clientX, py: event.clientY, ...placementRef.current };
    const target = event.currentTarget;
    target.setPointerCapture?.(event.pointerId);
    const move = (e: PointerEvent) => setPlacement(clamp({ ...start, x: start.x + e.clientX - start.px, y: start.y + e.clientY - start.py }));
    const end = () => {
      target.removeEventListener('pointermove', move);
      target.removeEventListener('pointerup', end);
      target.removeEventListener('pointercancel', end);
      writeStored(storageKey, placementRef.current);
    };
    target.addEventListener('pointermove', move);
    target.addEventListener('pointerup', end);
    target.addEventListener('pointercancel', end);
  };

  if (!open) return null;
  // On document.body: a transformed or contained ancestor (a dashboard widget) would otherwise become the reference
  // for its fixed position and clip it.
  return createPortal(
    <div
      ref={panel}
      role="dialog"
      aria-modal="false"
      aria-labelledby={titleId}
      className="fixed z-40 flex resize flex-col overflow-hidden rounded-lg border border-border bg-surface shadow-2xl"
      style={{ left: placement.x, top: placement.y, width: placement.width, height: placement.height, minWidth: Math.min(MIN_WIDTH, window.innerWidth - 2 * MARGIN), minHeight: MIN_HEIGHT }}
    >
      <div
        className="flex cursor-move touch-none select-none items-center justify-between gap-2 border-b border-border px-4 py-2"
        onPointerDown={startDrag}
        onDoubleClick={() => {
          const next = centered();
          setPlacement(next);
          writeStored(storageKey, next);
        }}
        title="Drag to move; double-click to center; drag the bottom-right corner to resize"
      >
        <h2 id={titleId} className="truncate text-sm font-semibold">
          {title}
        </h2>
        <button type="button" aria-label="Close" onClick={onClose} className="rounded p-1 text-muted hover:text-foreground">
          <X className="size-4" aria-hidden />
        </button>
      </div>
      <div className="min-h-0 flex-1 overflow-auto p-4">{children}</div>
    </div>,
    document.body,
  );
}
