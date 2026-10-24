import { X } from 'lucide-react';
import { AnimatePresence, motion } from 'motion/react';
import { createContext, useCallback, useContext, useMemo, useRef, useState, type ReactNode } from 'react';
import { cn } from '@/lib/utils';

/** How long a toast that is not an error stays. */
const TOAST_MS = 6000;

export type ToastTone = 'info' | 'success' | 'error';

interface Toast {
  id: number;
  title: string;
  description?: string;
  tone: ToastTone;
}

interface ToastApi {
  show: (toast: { title: string; description?: string; tone?: ToastTone }) => void;
}

const ToastContext = createContext<ToastApi | null>(null);

const TONE_CLASSES: Record<ToastTone, string> = {
  info: 'border-border',
  success: 'border-success/50',
  error: 'border-danger/60',
};

const TONE_BAR: Record<ToastTone, string> = {
  info: 'bg-accent',
  success: 'bg-success',
  error: 'bg-danger',
};

/** Short-lived notifications, announced to screen readers. Errors stay until dismissed. */
export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([]);
  const nextId = useRef(1);

  const dismiss = useCallback((id: number) => setToasts((all) => all.filter((t) => t.id !== id)), []);

  const show = useCallback<ToastApi['show']>(
    ({ title, description, tone = 'info' }) => {
      const id = nextId.current++;
      setToasts((all) => [...all.slice(-3), { id, title, description, tone }]);
      if (tone !== 'error') window.setTimeout(() => dismiss(id), TOAST_MS);
    },
    [dismiss],
  );

  const api = useMemo(() => ({ show }), [show]);

  return (
    <ToastContext.Provider value={api}>
      {children}
      <div
        aria-live="polite"
        className="pointer-events-none fixed inset-x-4 bottom-20 z-50 flex flex-col items-end gap-2 md:bottom-6 md:left-auto md:right-6 md:w-96"
      >
        <AnimatePresence initial={false}>
          {toasts.map((toast) => (
            <motion.div
              key={toast.id}
              layout
              role={toast.tone === 'error' ? 'alert' : 'status'}
              initial={{ opacity: 0, y: 24, scale: 0.94 }}
              animate={{ opacity: 1, y: 0, scale: 1, transition: { type: 'spring', stiffness: 420, damping: 30 } }}
              exit={{ opacity: 0, x: 48, transition: { duration: 0.2, ease: [0.4, 0, 1, 1] } }}
              className={cn('pointer-events-auto relative flex w-full items-start gap-3 overflow-hidden rounded-lg border bg-surface p-3 shadow-lg', TONE_CLASSES[toast.tone])}
            >
              <div className="min-w-0 flex-1">
                <p className="text-sm font-semibold">{toast.title}</p>
                {toast.description && <p className="mt-0.5 text-sm text-muted">{toast.description}</p>}
              </div>
              <button
                type="button"
                aria-label="Dismiss"
                className="rounded p-0.5 text-muted hover:text-foreground"
                onClick={() => dismiss(toast.id)}
              >
                <X className="size-4" aria-hidden />
              </button>
              {toast.tone !== 'error' && (
                <motion.span
                  aria-hidden
                  className={cn('absolute inset-x-0 bottom-0 h-0.5 origin-left opacity-60', TONE_BAR[toast.tone])}
                  initial={{ scaleX: 1 }}
                  animate={{ scaleX: 0 }}
                  transition={{ duration: TOAST_MS / 1000, ease: 'linear' }}
                />
              )}
            </motion.div>
          ))}
        </AnimatePresence>
      </div>
    </ToastContext.Provider>
  );
}

export function useToast(): ToastApi {
  const api = useContext(ToastContext);
  if (!api) throw new Error('useToast must be used inside <ToastProvider>');
  return api;
}
