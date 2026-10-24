import { AnimatePresence, motion, useReducedMotion } from 'motion/react';
import { Camera, Loader2 } from 'lucide-react';
import { useId, useRef, useState, type MouseEvent } from 'react';
import { Button } from '@/components/ui/button';
import { USER_INCIDENT_TYPES, type UserIncidentType } from '@/lib/api/types';
import { formatRemaining } from '@/lib/format';
import { useNow } from '@/lib/useNow';
import { cn } from '@/lib/utils';

export interface CaptureBarProps {
  /** Whether the recorder Agent is reachable and can capture. */
  canCapture: boolean;
  /** Why capture is unavailable, shown to the user. */
  unavailableReason?: string;
  /** A capture is in progress. */
  busy?: boolean;
  /** The incident still recording its post-window, and when that ends. */
  activeIncident?: string | null;
  activeUntil?: string | null;
  onCapture?: (type: UserIncidentType | null, note: string) => void;
  /** Quick types offered (all when omitted). */
  quickTypes?: string[];
  /** Show the note field. */
  showNote?: boolean;
  className?: string;
}

/** Longest note the API accepts. */
export const MAX_NOTE_LENGTH = 2000;

interface Ripple {
  id: number;
  x: number;
  y: number;
}

/**
 * The primary action of the product: CAPTURE INCIDENT, plus one-tap quick types and an optional note.
 * The moment the user presses a button becomes the incident's center point.
 */
export function CaptureBar({ canCapture, unavailableReason, busy = false, activeIncident, activeUntil, onCapture, quickTypes, showNote = true, className }: CaptureBarProps) {
  const reasonId = useId();
  const noteId = useId();
  const [note, setNote] = useState('');
  const [ripples, setRipples] = useState<Ripple[]>([]);
  const nextRipple = useRef(0);
  const reduced = useReducedMotion() ?? false;
  const now = useNow(activeIncident != null);
  const disabled = !canCapture || busy;

  const capture = (type: UserIncidentType | null) => {
    onCapture?.(type, note);
    setNote('');
  };

  const pressMain = (event: MouseEvent<HTMLButtonElement>) => {
    if (!reduced) {
      const box = event.currentTarget.getBoundingClientRect();
      const ripple = { id: nextRipple.current++, x: event.clientX - box.left, y: event.clientY - box.top };
      setRipples((all) => [...all, ripple]);
      window.setTimeout(() => setRipples((all) => all.filter((r) => r.id !== ripple.id)), 700);
    }
    capture(null);
  };

  return (
    <section aria-label="Capture incident" className={cn('rounded-lg border border-border bg-surface p-4', className)}>
      <div className="flex flex-col gap-4 lg:flex-row lg:items-center">
        <div className="flex shrink-0 flex-wrap items-center gap-3">
          <Button
            variant="danger"
            size="lg"
            className={cn(
              'relative h-14 w-full overflow-hidden text-lg font-bold tracking-wide lg:w-auto lg:min-w-64',
              !disabled && !activeIncident && 'capture-ready',
            )}
            disabled={disabled}
            aria-describedby={canCapture ? undefined : reasonId}
            onClick={pressMain}
          >
            {busy ? <Loader2 className="animate-spin" aria-hidden /> : <Camera aria-hidden />}
            CAPTURE INCIDENT
            <AnimatePresence>
              {ripples.map((r) => (
                <motion.span
                  key={r.id}
                  aria-hidden
                  className="pointer-events-none absolute size-10 rounded-full bg-white/40"
                  style={{ left: r.x - 20, top: r.y - 20 }}
                  initial={{ scale: 0, opacity: 0.7 }}
                  animate={{ scale: 9, opacity: 0 }}
                  exit={{ opacity: 0 }}
                  transition={{ duration: 0.65, ease: 'easeOut' }}
                />
              ))}
            </AnimatePresence>
          </Button>

          <AnimatePresence>
            {activeIncident && (
              <motion.div
                role="status"
                initial={reduced ? false : { opacity: 0, scale: 0.8 }}
                animate={{ opacity: 1, scale: 1 }}
                exit={{ opacity: 0, scale: 0.8 }}
                className="flex shrink-0 items-center gap-2 rounded-full border border-danger/50 bg-danger/10 py-1 pl-2 pr-3"
              >
                <span className="relative flex size-3">
                  <span className="absolute inline-flex size-full animate-ping rounded-full bg-danger opacity-70" />
                  <span className="relative inline-flex size-3 rounded-full bg-danger" />
                </span>
                <span className="text-xs font-semibold text-danger">REC</span>
                <span className="font-mono text-xs">{activeIncident}</span>
                <span className="text-xs tabular-nums text-muted">{formatRemaining(activeUntil, now)}</span>
              </motion.div>
            )}
          </AnimatePresence>
        </div>

        <div className="flex min-w-0 flex-1 flex-wrap gap-2" role="group" aria-label="Report what you observed">
          {USER_INCIDENT_TYPES.filter((type) => !quickTypes || quickTypes.includes(type.id)).map((type) => (
            <motion.div key={type.id} whileTap={reduced || disabled ? undefined : { scale: 0.94 }}>
              <Button variant="outline" size="sm" disabled={disabled} onClick={() => capture(type.id)} className="hover:border-danger/60">
                {type.label}
              </Button>
            </motion.div>
          ))}
        </div>
      </div>

      {showNote && (
      <div className="mt-3 flex flex-col gap-1">
        <label htmlFor={noteId} className="text-xs text-muted">
          Note (optional, sent with the next capture)
        </label>
        <input
          id={noteId}
          type="text"
          value={note}
          maxLength={MAX_NOTE_LENGTH}
          disabled={disabled}
          placeholder="What did you see? e.g. black screen for 2 seconds in game"
          onChange={(e) => setNote(e.target.value)}
          className="h-9 rounded-md border border-border bg-background px-3 text-sm placeholder:text-muted disabled:opacity-50"
        />
      </div>
      )}

      <p id={reasonId} className="mt-3 text-sm text-muted">
        {canCapture
          ? 'The moment you press a button becomes the center of the incident. In games, use the capture hotkey (default Ctrl+Alt+Shift+I).'
          : unavailableReason}
      </p>
    </section>
  );
}
