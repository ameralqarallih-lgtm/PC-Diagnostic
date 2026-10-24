import { Redo2, Save, Undo2 } from "lucide-react";
import { AnimatePresence, motion } from "motion/react";
import { Button } from "./button";

/** The v2 save bar: floats at the bottom while something is unsaved, with how many changes, undo, redo, discard and save. */
export function SaveBar(props: {
  show: boolean;
  changes: number;
  saving: boolean;
  error: string | null;
  canUndo?: boolean;
  canRedo?: boolean;
  onUndo?: () => void;
  onRedo?: () => void;
  onDiscard: () => void;
  onSave: () => void;
}) {
  return (
    <AnimatePresence>
      {props.show && (
        <motion.div
          role="region"
          aria-label="Changes not saved"
          className="fixed inset-x-0 bottom-4 z-40 flex justify-center px-4"
          initial={{ opacity: 0, y: 16 }}
          animate={{ opacity: 1, y: 0 }}
          exit={{ opacity: 0, y: 16 }}
          transition={{ duration: 0.2, ease: [0.16, 1, 0.3, 1] }}
        >
          <div className="flex items-center gap-2 rounded-2xl border border-border bg-surface py-2 pl-4 pr-2 text-sm shadow-[var(--shadow-float)]">
            <span className="size-2 rounded-full bg-warning" aria-hidden />
            <span className="mr-2">
              {props.error ? (
                <span className="text-danger">{props.error}</span>
              ) : (
                `${props.changes} unsaved change${props.changes === 1 ? "" : "s"}`
              )}
            </span>
            {props.onUndo && (
              <>
                <Button
                  size="icon"
                  variant="ghost"
                  className="size-8"
                  aria-label="Undo (Ctrl+Z)"
                  title="Undo (Ctrl+Z)"
                  disabled={!props.canUndo}
                  onClick={props.onUndo}
                >
                  <Undo2 aria-hidden />
                </Button>
                <Button
                  size="icon"
                  variant="ghost"
                  className="size-8"
                  aria-label="Redo (Ctrl+Y)"
                  title="Redo (Ctrl+Y)"
                  disabled={!props.canRedo}
                  onClick={props.onRedo}
                >
                  <Redo2 aria-hidden />
                </Button>
              </>
            )}
            <Button
              size="sm"
              variant="outline"
              onClick={props.onDiscard}
              disabled={props.saving}
            >
              Discard
            </Button>
            <Button size="sm" onClick={props.onSave} disabled={props.saving}>
              <Save aria-hidden /> {props.saving ? "Saving…" : "Save"}
            </Button>
          </div>
        </motion.div>
      )}
    </AnimatePresence>
  );
}
