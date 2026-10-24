import { Check, LayoutDashboard, Plus, RotateCcw, Undo2 } from 'lucide-react';
import { useId, useState } from 'react';
import { Button } from '@/components/ui/button';
import { PRESETS } from './config/presets';

export interface CustomizeBarProps {
  saving: boolean;
  onAdd: () => void;
  onPreset: (presetId: string) => void;
  onCancel: () => void;
  onDone: () => void;
}

/** Customize mode: add widgets, start from a preset, then keep or discard the changes. */
export function CustomizeBar({ saving, onAdd, onPreset, onCancel, onDone }: CustomizeBarProps) {
  const [preset, setPreset] = useState(PRESETS[0]!.id);
  const presetId = useId();

  return (
    <div role="toolbar" aria-label="Customize the dashboard" className="flex flex-wrap items-center gap-2 rounded-lg border border-accent/50 bg-accent/5 px-3 py-2">
      <LayoutDashboard className="size-4 text-accent" aria-hidden />
      <span className="mr-auto text-sm">
        <span className="font-medium">Customizing.</span> <span className="text-muted">Drag widgets by their bar, resize from the corner, open ⚙ for what each shows.</span>
      </span>
      <Button size="sm" variant="outline" onClick={onAdd}>
        <Plus aria-hidden /> Add widget
      </Button>
      <div className="flex items-center gap-1">
        <label htmlFor={presetId} className="sr-only">
          Preset
        </label>
        <select id={presetId} value={preset} onChange={(e) => setPreset(e.target.value)} className="h-8 rounded-md border border-border bg-background px-2 text-xs">
          {PRESETS.map((p) => (
            <option key={p.id} value={p.id} title={p.description}>
              {p.name}
            </option>
          ))}
        </select>
        <Button size="sm" variant="outline" onClick={() => onPreset(preset)} title="Replace the layout with this preset (you can still cancel)">
          <RotateCcw aria-hidden /> Use preset
        </Button>
      </div>
      <Button size="sm" variant="ghost" onClick={onCancel}>
        <Undo2 aria-hidden /> Cancel
      </Button>
      <Button size="sm" onClick={onDone} disabled={saving}>
        <Check aria-hidden /> {saving ? 'Saving…' : 'Done'}
      </Button>
    </div>
  );
}
