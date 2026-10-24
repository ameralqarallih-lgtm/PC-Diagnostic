import { Trash2 } from 'lucide-react';
import { createContext, useCallback, useContext, useRef, useState, type ReactNode } from 'react';
import { Button } from './button';
import { Dialog } from './dialog';

export interface ConfirmOptions {
  /** The question, naming what goes: "Delete Mochi?". */
  title: string;
  /** What happens, and whether it can be undone. */
  description?: string;
  /** The button that does it ("Delete", "Remove", "Stop"). */
  action?: string;
  /** A destructive action (red button; the default). */
  danger?: boolean;
}

type Confirm = (options: ConfirmOptions) => Promise<boolean>;

const ConfirmContext = createContext<Confirm | null>(null);

/**
 * Asks before anything is deleted or removed: one dialog for the whole app, opened by `useConfirm()`, answered by
 * its button (true) or by Cancel, Esc or a click beside it (false).
 */
export function ConfirmProvider({ children }: { children: ReactNode }) {
  const [asking, setAsking] = useState<ConfirmOptions | null>(null);
  const answer = useRef<((yes: boolean) => void) | null>(null);
  const confirm = useCallback<Confirm>(
    (options) =>
      new Promise<boolean>((resolve) => {
        answer.current?.(false);
        answer.current = resolve;
        setAsking(options);
      }),
    [],
  );
  const close = (yes: boolean) => {
    answer.current?.(yes);
    answer.current = null;
    setAsking(null);
  };
  return (
    <ConfirmContext.Provider value={confirm}>
      {children}
      <Dialog
        open={asking !== null}
        title={asking?.title ?? ''}
        description={asking?.description}
        onClose={() => close(false)}
        footer={
          <>
            <Button variant="ghost" onClick={() => close(false)}>
              Cancel
            </Button>
            <Button variant={asking?.danger === false ? 'default' : 'danger'} onClick={() => close(true)}>
              {asking?.danger !== false && <Trash2 aria-hidden />}
              {asking?.action ?? 'Delete'}
            </Button>
          </>
        }
      >
        {null}
      </Dialog>
    </ConfirmContext.Provider>
  );
}

/** Asks for confirmation (resolves true when confirmed). Outside the provider it never confirms. */
export function useConfirm(): Confirm {
  return useContext(ConfirmContext) ?? (() => Promise.resolve(false));
}
