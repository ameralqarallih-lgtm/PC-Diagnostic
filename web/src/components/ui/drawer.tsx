import { X } from 'lucide-react';
import { AnimatePresence, motion } from 'motion/react';
import { useEffect, useId, useRef, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { Button } from './button';

/**
 * A drawer: the detail of what was clicked, from the right, over the page (which stays in sight). Esc, the close
 * button or a click beside it closes it; focus moves in when it opens and back when it closes.
 */
export function Drawer({ open, title, onClose, children, footer }: { open: boolean; title: ReactNode; onClose: () => void; children: ReactNode; footer?: ReactNode }) {
  const titleId = useId();
  const panel = useRef<HTMLDivElement>(null);
  const closing = useRef(onClose);
  closing.current = onClose;

  useEffect(() => {
    if (!open) return undefined;
    const previous = document.activeElement as HTMLElement | null;
    window.setTimeout(() => panel.current?.focus(), 0);
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.preventDefault();
        closing.current();
      }
    };
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('keydown', onKey);
      previous?.focus?.();
    };
  }, [open]);

  return createPortal(
    <AnimatePresence>
      {open && (
        <motion.div className="fixed inset-0 z-50" initial={{ opacity: 0 }} animate={{ opacity: 1 }} exit={{ opacity: 0 }} transition={{ duration: 0.2 }}>
          <div className="absolute inset-0 bg-black/30" onMouseDown={() => closing.current()} aria-hidden />
          <motion.div
            ref={panel}
            role="dialog"
            aria-modal="true"
            aria-labelledby={titleId}
            tabIndex={-1}
            className="absolute inset-y-2 right-2 flex w-[min(30rem,calc(100vw-1rem))] flex-col rounded-2xl border border-border bg-surface shadow-[var(--shadow-float,0_12px_32px_-8px_rgb(0_0_0/0.5))] outline-none"
            initial={{ x: 32, opacity: 0 }}
            animate={{ x: 0, opacity: 1 }}
            exit={{ x: 32, opacity: 0 }}
            transition={{ duration: 0.2, ease: [0.16, 1, 0.3, 1] }}
          >
            <header className="flex items-start gap-3 border-b border-border p-4">
              <h2 id={titleId} className="min-w-0 flex-1 text-base font-semibold">
                {title}
              </h2>
              <Button size="icon" variant="ghost" className="size-8" aria-label="Close" onClick={() => closing.current()}>
                <X aria-hidden />
              </Button>
            </header>
            <div className="min-h-0 flex-1 overflow-y-auto p-4 text-sm">{children}</div>
            {footer && <footer className="flex justify-end gap-2 border-t border-border p-3">{footer}</footer>}
          </motion.div>
        </motion.div>
      )}
    </AnimatePresence>,
    document.body,
  );
}
