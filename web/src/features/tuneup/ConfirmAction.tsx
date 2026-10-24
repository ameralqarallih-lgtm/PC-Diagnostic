import { useEffect, useState, type ReactNode } from 'react';
import { Button } from '@/components/ui/button';
import { Dialog } from '@/components/ui/dialog';

/** An action waiting for the user's yes: what it does, and what to know first. */
export interface PendingAction {
  title: string;
  body: ReactNode;
  confirmLabel: string;
  danger?: boolean;
  /** Parts of Windows are involved: the user ticks that they understand before confirming. */
  acknowledge?: string;
  run: (confirmed: boolean) => void;
}

/** Asks before anything that ends, stops, removes or changes. */
export function ConfirmAction({ pending, onClose }: { pending: PendingAction | null; onClose: () => void }) {
  const [understood, setUnderstood] = useState(false);
  useEffect(() => {
    setUnderstood(false);
  }, [pending]);

  return (
    <Dialog
      open={pending !== null}
      title={pending?.title ?? ''}
      onClose={onClose}
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button
            variant={pending?.danger ? 'danger' : 'default'}
            disabled={!!pending?.acknowledge && !understood}
            onClick={() => {
              pending?.run(!!pending.acknowledge && understood);
              onClose();
            }}
          >
            {pending?.confirmLabel ?? 'OK'}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-3 text-sm">
        {pending?.body}
        {pending?.acknowledge && (
          <label className="flex items-start gap-2 rounded-md border border-warning/50 bg-warning/10 p-2.5">
            <input type="checkbox" checked={understood} onChange={(e) => setUnderstood(e.target.checked)} className="mt-0.5 size-4 accent-[var(--accent)]" />
            <span>{pending.acknowledge}</span>
          </label>
        )}
      </div>
    </Dialog>
  );
}
