import { AnimatePresence, motion } from 'motion/react';
import { X } from 'lucide-react';
import { useEffect, useId, useRef, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { cn } from '@/lib/utils';

export interface DialogProps {
  open: boolean;
  title: string;
  description?: string;
  onClose: () => void;
  children: ReactNode;
  footer?: ReactNode;
  className?: string;
}

/**
 * A modal dialog: Escape or the backdrop closes it, focus moves into it when it opens and back to where it was
 * when it closes, and Tab stays inside.
 */
export function Dialog({ open, title, description, onClose, children, footer, className }: DialogProps) {
  const titleId = useId();
  const descriptionId = useId();
  const panel = useRef<HTMLDivElement>(null);
  const closeRef = useRef(onClose);
  closeRef.current = onClose;

  useEffect(() => {
    if (!open) return undefined;
    const previous = document.activeElement as HTMLElement | null;
    const focusable = () =>
      [...(panel.current?.querySelectorAll<HTMLElement>('button, input, select, textarea, [tabindex]:not([tabindex="-1"])') ?? [])].filter((e) => !e.hasAttribute('disabled'));
    window.setTimeout(() => (focusable()[1] ?? focusable()[0])?.focus(), 0);

    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        event.preventDefault();
        closeRef.current();
      } else if (event.key === 'Tab') {
        const items = focusable();
        const first = items[0];
        const last = items[items.length - 1];
        if (!first || !last) return;
        if (event.shiftKey && document.activeElement === first) {
          event.preventDefault();
          last.focus();
        } else if (!event.shiftKey && document.activeElement === last) {
          event.preventDefault();
          first.focus();
        }
      }
    };
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('keydown', onKey);
      previous?.focus?.();
    };
  }, [open]);

  // On document.body: a transformed or contained ancestor (a dashboard widget) would otherwise become the
  // reference for its fixed position and clip it.
  return createPortal(
    <AnimatePresence>
      {open && (
        <motion.div
          className="fixed inset-0 z-50 flex items-end justify-center bg-black/50 p-4 backdrop-blur-[2px] sm:items-center"
          initial={{ opacity: 0 }}
          animate={{ opacity: 1 }}
          exit={{ opacity: 0 }}
          transition={{ duration: 0.2 }}
          onMouseDown={(e) => {
            if (e.target === e.currentTarget) onClose();
          }}
        >
          <motion.div
            ref={panel}
            role="dialog"
            aria-modal="true"
            aria-labelledby={titleId}
            aria-describedby={description ? descriptionId : undefined}
            initial={{ opacity: 0, y: 20, scale: 0.97 }}
            animate={{ opacity: 1, y: 0, scale: 1, transition: { type: 'spring', stiffness: 460, damping: 34, mass: 0.9 } }}
            exit={{ opacity: 0, y: 12, scale: 0.98, transition: { duration: 0.15, ease: [0.4, 0, 1, 1] } }}
            className={cn('flex max-h-[85vh] w-full max-w-lg flex-col rounded-lg border border-border bg-surface shadow-xl', className)}
          >
            <header className="flex items-start justify-between gap-3 border-b border-border px-5 py-4">
              <div>
                <h2 id={titleId} className="text-base font-semibold">
                  {title}
                </h2>
                {description && (
                  <p id={descriptionId} className="mt-0.5 text-sm text-muted">
                    {description}
                  </p>
                )}
              </div>
              <button type="button" aria-label="Close" className="rounded p-1 text-muted hover:text-foreground" onClick={onClose}>
                <X className="size-4" aria-hidden />
              </button>
            </header>
            <div className="min-h-0 flex-1 overflow-auto px-5 py-4">{children}</div>
            {footer && <footer className="flex flex-wrap justify-end gap-2 border-t border-border px-5 py-3">{footer}</footer>}
          </motion.div>
        </motion.div>
      )}
    </AnimatePresence>,
    document.body,
  );
}
