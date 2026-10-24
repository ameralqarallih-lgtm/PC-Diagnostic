import { motion } from 'motion/react';
import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { cn } from '@/lib/utils';

export interface MenuItem {
  label: string;
  onSelect: () => void;
  danger?: boolean;
  disabled?: boolean;
  /** Why it is disabled (shown as its tooltip). */
  title?: string;
  /** A heading above this item (starts a section). */
  section?: string;
  /** A line under the label. */
  description?: string;
  /** A small picture of what it makes (the menu then shows cards). */
  preview?: ReactNode;
}

/**
 * A button that opens a list of actions below it (on the page body, so a scrolling table does not clip it).
 * Escape or a click elsewhere closes it; the arrow keys move between the actions.
 */
export function Menu({
  label,
  items,
  children,
  className,
  width = 240,
  columns = 1,
}: {
  label: string;
  /** The actions, or a function that finds them when the menu opens (shown as loading meanwhile). */
  items: MenuItem[] | (() => Promise<MenuItem[]>);
  children: ReactNode;
  className?: string;
  /** The list's width in pixels. */
  width?: number;
  /** Cards side by side (for items with previews). */
  columns?: number;
}) {
  const [open, setOpen] = useState(false);
  const [loaded, setLoaded] = useState<MenuItem[] | null>(null);
  const shown = typeof items === 'function' ? loaded : items;
  const [position, setPosition] = useState<{ top: number; left: number } | null>(null);
  const trigger = useRef<HTMLButtonElement>(null);
  const list = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open || typeof items !== 'function') return undefined;
    let current = true;
    setLoaded(null);
    items()
      .then((found) => current && setLoaded(found))
      .catch(() => current && setLoaded([{ label: 'The recorder could not be asked', onSelect: () => {}, disabled: true }]));
    return () => {
      current = false;
    };
    // Loaded each time the menu opens.
  }, [open]);

  useLayoutEffect(() => {
    if (!open || !trigger.current) return;
    const rect = trigger.current.getBoundingClientRect();
    setPosition({ top: Math.min(rect.bottom + 4, window.innerHeight - 40), left: Math.max(8, Math.min(rect.right - width, window.innerWidth - width - 8)) });
  }, [open, width]);

  useEffect(() => {
    if (!open) return undefined;
    list.current?.querySelector<HTMLButtonElement>('button:not([disabled])')?.focus();
    // (Focus moves again once loaded items arrive.)
    const close = (event: MouseEvent) => {
      if (!list.current?.contains(event.target as Node) && !trigger.current?.contains(event.target as Node)) setOpen(false);
    };
    const key = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setOpen(false);
        trigger.current?.focus();
      } else if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
        event.preventDefault();
        const buttons = [...(list.current?.querySelectorAll<HTMLButtonElement>('button:not([disabled])') ?? [])];
        const index = buttons.indexOf(document.activeElement as HTMLButtonElement);
        buttons[(index + (event.key === 'ArrowDown' ? 1 : -1) + buttons.length) % buttons.length]?.focus();
      }
    };
    document.addEventListener('mousedown', close);
    document.addEventListener('keydown', key);
    return () => {
      document.removeEventListener('mousedown', close);
      document.removeEventListener('keydown', key);
    };
  }, [open]);

  return (
    <>
      <button
        ref={trigger}
        type="button"
        aria-label={label}
        title={label}
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={() => setOpen((o) => !o)}
        className={cn('rounded p-1 text-muted hover:bg-surface-muted hover:text-foreground [&_svg]:size-4', className)}
      >
        {children}
      </button>
      {open &&
        position &&
        createPortal(
          <motion.div
            ref={list}
            role="menu"
            aria-label={label}
            className={cn('fixed z-50 origin-top rounded-md border border-border bg-surface p-1 shadow-xl', columns > 1 ? 'grid gap-1 overflow-y-auto' : 'flex flex-col')}
            style={{ top: position.top, left: position.left, width, maxHeight: `calc(100vh - ${position.top + 8}px)`, gridTemplateColumns: columns > 1 ? `repeat(${columns}, minmax(0, 1fr))` : undefined }}
            initial={{ opacity: 0, scale: 0.96, y: -4 }}
            animate={{ opacity: 1, scale: 1, y: 0 }}
            transition={{ duration: 0.16, ease: [0.16, 1, 0.3, 1] }}
          >
            {shown === null && <p className="px-2 py-1.5 text-sm text-muted">Loading…</p>}
            {(shown ?? []).map((item) => (
              <div key={item.label} className="contents">
                {item.section && <p className="px-2 pb-0.5 pt-1.5 text-[11px] font-semibold uppercase tracking-wide text-muted" style={columns > 1 ? { gridColumn: '1 / -1' } : undefined}>{item.section}</p>}
                <button
                  type="button"
                  role="menuitem"
                  disabled={item.disabled}
                  title={item.title}
                  onClick={() => {
                    setOpen(false);
                    item.onSelect();
                  }}
                  className={cn(
                    'rounded px-2 py-1.5 text-left text-sm hover:bg-surface-muted focus:bg-surface-muted focus:outline-none disabled:opacity-40',
                    item.preview != null && 'motion-hover flex flex-col gap-1 p-1.5',
                    item.danger && 'text-danger',
                  )}
                >
                  {item.preview != null && (
                    <span aria-hidden className="flex h-[76px] items-center justify-center overflow-hidden rounded bg-[linear-gradient(135deg,#1e3a5f,#3b2a4a_60%,#1f2937)]">
                      {item.preview}
                    </span>
                  )}
                  <span className={cn(item.preview != null && 'text-xs font-medium leading-tight')}>{item.label}</span>
                  {item.description && <span className={cn('block text-xs leading-snug text-muted', item.preview != null && 'line-clamp-2 text-[11px] leading-tight')}>{item.description}</span>}
                </button>
              </div>
            ))}
          </motion.div>,
          document.body,
        )}
    </>
  );
}
