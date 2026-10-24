import { useNavigate } from '@tanstack/react-router';
import { CornerDownLeft, Search, type LucideIcon } from 'lucide-react';
import { AnimatePresence, motion } from 'motion/react';
import { useEffect, useId, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { SECTIONS } from '@/features/settings/sections';
import { cn } from '@/lib/utils';

/** Something the palette finds: a page, a settings topic or an action. */
export interface PaletteItem {
  id: string;
  kind: 'Pages' | 'Settings' | 'Actions';
  title: string;
  /** Where it is (shown after the title). */
  where?: string;
  /** More words it is found by. */
  keywords?: string;
  icon?: LucideIcon;
  run: () => void;
}

/** The items whose words hold every word typed, pages first, then settings, then actions. */
export function findItems(items: readonly PaletteItem[], text: string): PaletteItem[] {
  const words = text.toLowerCase().split(/\s+/).filter(Boolean);
  const order = { Pages: 0, Settings: 1, Actions: 2 };
  return items
    .filter((i) => {
      const hay = `${i.title} ${i.where ?? ''} ${i.keywords ?? ''}`.toLowerCase();
      return words.every((w) => hay.includes(w));
    })
    .sort((a, b) => order[a.kind] - order[b.kind]);
}

/** The settings topics as palette items (each opens Settings at its topic). */
export function useSettingsItems(): PaletteItem[] {
  const navigate = useNavigate();
  return useMemo(
    () =>
      SECTIONS.map((s) => ({
        id: `settings:${s.id}`,
        kind: 'Settings' as const,
        title: s.title,
        where: `Settings › ${s.group}`,
        keywords: s.keywords,
        icon: s.icon,
        run: () => void navigate({ to: '/settings', hash: s.id }),
      })),
    [navigate],
  );
}

/**
 * The command palette (Ctrl+K): type to find a page, a settings topic or an action; arrows move, Enter goes, Esc
 * closes. It opens near the top from a slight scale (Standard motion, 200 ms).
 */
export function CommandPalette({ open, onClose, items }: { open: boolean; onClose: () => void; items: readonly PaletteItem[] }) {
  const [text, setText] = useState('');
  const [index, setIndex] = useState(0);
  const input = useRef<HTMLInputElement>(null);
  const listId = useId();
  const found = useMemo(() => findItems(items, text), [items, text]);

  useEffect(() => {
    if (!open) return;
    setText('');
    setIndex(0);
    const previous = document.activeElement as HTMLElement | null;
    window.setTimeout(() => input.current?.focus(), 0);
    return () => previous?.focus?.();
  }, [open]);

  useEffect(() => {
    setIndex(0);
  }, [text]);

  const go = (item: PaletteItem | undefined) => {
    if (!item) return;
    onClose();
    item.run();
  };

  let lastKind: string | null = null;
  return createPortal(
    <AnimatePresence>
      {open && (
        <motion.div
          className="fixed inset-0 z-50 flex items-start justify-center bg-black/40 px-4 pt-[12vh]"
          initial={{ opacity: 0 }}
          animate={{ opacity: 1 }}
          exit={{ opacity: 0 }}
          transition={{ duration: 0.2 }}
          onMouseDown={(e) => e.target === e.currentTarget && onClose()}
        >
          <motion.div
            role="dialog"
            aria-modal="true"
            aria-label="Search"
            className="w-full max-w-xl overflow-hidden rounded-2xl border border-border bg-surface shadow-[var(--shadow-float,0_12px_32px_-8px_rgb(0_0_0/0.5))]"
            initial={{ opacity: 0, scale: 0.98, y: -6 }}
            animate={{ opacity: 1, scale: 1, y: 0 }}
            exit={{ opacity: 0, scale: 0.98, y: -6 }}
            transition={{ duration: 0.2, ease: [0.16, 1, 0.3, 1] }}
          >
            <div className="flex items-center gap-3 border-b border-border px-4">
              <Search className="size-4 text-muted" aria-hidden />
              <input
                ref={input}
                value={text}
                onChange={(e) => setText(e.target.value)}
                placeholder="Search pages, settings and actions"
                aria-label="Search"
                role="combobox"
                aria-expanded="true"
                aria-controls={listId}
                aria-activedescendant={found[index] ? `${listId}-${index}` : undefined}
                className="h-12 flex-1 bg-transparent text-sm outline-none placeholder:text-muted"
                onKeyDown={(e) => {
                  if (e.key === 'ArrowDown') {
                    e.preventDefault();
                    setIndex((i) => Math.min(found.length - 1, i + 1));
                  } else if (e.key === 'ArrowUp') {
                    e.preventDefault();
                    setIndex((i) => Math.max(0, i - 1));
                  } else if (e.key === 'Enter') {
                    e.preventDefault();
                    go(found[index]);
                  } else if (e.key === 'Escape') {
                    e.preventDefault();
                    onClose();
                  }
                }}
              />
              <kbd className="rounded border border-border px-1.5 text-[11px] text-muted">Esc</kbd>
            </div>
            <ul id={listId} role="listbox" aria-label="Results" className="max-h-[50vh] overflow-y-auto py-1">
              {found.length === 0 && <li className="px-4 py-6 text-center text-sm text-muted">Nothing found for “{text}”.</li>}
              {found.map((item, i) => {
                const heading = item.kind !== lastKind ? item.kind : null;
                lastKind = item.kind;
                const Icon = item.icon;
                return (
                  <li key={item.id} role="presentation">
                    {heading && <div className="px-4 pb-1 pt-2 text-xs text-muted">{heading}</div>}
                    <div
                      id={`${listId}-${i}`}
                      role="option"
                      aria-selected={i === index}
                      className={cn('mx-1 flex cursor-pointer items-center gap-3 rounded-md px-3 py-1.5 text-sm', i === index && 'bg-surface-muted shadow-[inset_2px_0_0_var(--accent)]')}
                      onMouseMove={() => setIndex(i)}
                      onClick={() => go(item)}
                    >
                      {Icon && <Icon className={cn('size-4 shrink-0', i === index ? 'text-accent' : 'text-muted')} aria-hidden />}
                      <span className="min-w-0 flex-1 truncate">
                        {item.title}
                        {item.where && <span className="text-muted"> · {item.where}</span>}
                      </span>
                      {i === index && <CornerDownLeft className="size-3.5 text-muted" aria-hidden />}
                    </div>
                  </li>
                );
              })}
            </ul>
          </motion.div>
        </motion.div>
      )}
    </AnimatePresence>,
    document.body,
  );
}
