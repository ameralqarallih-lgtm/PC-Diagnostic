import { Badge } from '@/components/ui/badge';
import type { OverlayDevice, OverlayDisplay } from '@/lib/api/types';
import { cn } from '@/lib/utils';

/** How often a USB screen's moving backgrounds send a picture: pictures a second, from 30 (every 33 ms) to 1 (every second). */
export const MOTION_RATES = [30, 24, 20, 15, 10, 5, 2, 1] as const;

/** A rate as the list shows it: how often, then how many a second. */
export const rateLabel = (fps: number) => (fps === 1 ? 'Every second (1 a second)' : `Every ${fps === 2 ? '0.5 s' : `${Math.round(1000 / fps)} ms`} (${fps} a second)`);

/** The listed rate nearest to a saved one (older layouts could have 1–15). */
export const nearestRate = (fps: number) => MOTION_RATES.reduce((best, r) => (Math.abs(r - fps) < Math.abs(best - fps) ? r : best), 30);

const ROTATIONS: [number, string][] = [
  [-1, 'Usual'],
  [0, '0°'],
  [90, '90°'],
  [180, '180°'],
  [270, '270°'],
];

const STATUS: Record<string, { tone: 'success' | 'warning' | 'danger' | 'neutral'; text: string }> = {
  on: { tone: 'success', text: 'Showing your panels' },
  available: { tone: 'neutral', text: 'Not used' },
  busy: { tone: 'warning', text: 'In use by another program' },
  problem: { tone: 'danger', text: 'Problem' },
};

/**
 * A USB screen's own settings, over its editor: whether it shows your panels (the program that drives it otherwise is
 * paused meanwhile), how its picture is turned, its panels' size, backlight and background. Saved with the layout.
 */
export function DeviceScreenSettings({ display, settings, onChange }: { display: OverlayDisplay; settings: OverlayDevice; onChange: (patch: Partial<Omit<OverlayDevice, 'key'>>) => void }) {
  const status = STATUS[display.status ?? 'available'] ?? STATUS.available!;
  return (
    <div className="flex flex-col gap-3 rounded-md border border-border bg-surface-muted/40 p-3" role="group" aria-label="USB screen settings">
      <div className="flex flex-wrap items-center gap-x-4 gap-y-2">
        <label className="flex items-center gap-2 text-sm font-medium">
          <input type="checkbox" role="switch" checked={settings.use} onChange={(e) => onChange({ use: e.target.checked })} className="size-4 accent-[var(--accent)]" />
          Show my panels on this screen
        </label>
        <Badge tone={status.tone}>{status.text}</Badge>
        {display.detail && <span className="text-xs text-muted">{display.detail}</span>}
      </div>
      <p className="text-xs text-muted">
        {display.model ?? display.name}, {display.width} × {display.height}. While it shows your panels, the program that drives it otherwise (L-Connect for Lian Li screens) is paused; it starts again when
        you turn this off, or when the recorder stops. The on-screen readings switch and hotkey do not hide it.
      </p>
      <div className="flex flex-wrap items-center gap-x-6 gap-y-2 text-sm">
        <span className="flex items-center gap-2">
          <span className="text-muted">Turned</span>
          <span role="radiogroup" aria-label="Turned" className="flex gap-0.5 rounded-md bg-surface-muted p-0.5 text-xs">
            {ROTATIONS.map(([value, text]) => (
              <button key={value} type="button" role="radio" aria-checked={settings.rotation === value} onClick={() => onChange({ rotation: value })} className={cn('rounded px-2 py-0.5', settings.rotation === value ? 'bg-surface font-medium shadow-sm' : 'text-muted')}>
                {text}
              </button>
            ))}
          </span>
        </span>
        <label className="flex items-center gap-2">
          <span className="text-muted">Panels' size</span>
          <input type="range" min={50} max={300} step={10} value={settings.zoom} onChange={(e) => onChange({ zoom: Number(e.target.value) })} aria-label="Panels' size" className="accent-[var(--accent)]" />
          <span className="w-10 tabular-nums">{settings.zoom}%</span>
        </label>
        <label className="flex items-center gap-2">
          <span className="text-muted">Brightness</span>
          <input type="range" min={0} max={100} step={5} value={settings.brightness} onChange={(e) => onChange({ brightness: Number(e.target.value) })} aria-label="Brightness" className="accent-[var(--accent)]" />
          <span className="w-10 tabular-nums">{settings.brightness}%</span>
        </label>
        <label className="flex items-center gap-2" title="How often a moving background or a gliding value sends a new picture: each is drawn, encoded and sent over USB, so less often uses less CPU">
          <span className="text-muted">Moving backgrounds</span>
          <select className="h-8 rounded-md border border-border bg-background px-2 text-sm" value={nearestRate(settings.motionFps ?? 30)} onChange={(e) => onChange({ motionFps: Number(e.target.value) })} aria-label="Moving backgrounds: a new picture every">
            {MOTION_RATES.map((fps) => (
              <option key={fps} value={fps}>
                {rateLabel(fps)}
              </option>
            ))}
          </select>
        </label>
        <label className="flex items-center gap-2">
          <span className="text-muted">Background</span>
          <input type="color" value={settings.background} onChange={(e) => onChange({ background: e.target.value.toUpperCase() })} aria-label="Background" className="h-7 w-10 rounded border border-border bg-transparent" />
        </label>
      </div>
    </div>
  );
}
