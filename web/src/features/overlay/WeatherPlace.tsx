import { CloudSun } from 'lucide-react';
import { useQuery } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { Button } from '@/components/ui/button';
import { Dialog } from '@/components/ui/dialog';
import { apiGet } from '@/lib/api/client';
import type { OverlayWeather, WeatherAnswer } from '@/lib/api/types';

/** How a report's kind is said. */
const KIND_NAMES: Record<string, string> = { clear: 'Clear', clouds: 'Cloudy', fog: 'Fog', rain: 'Rain', snow: 'Snow', storm: 'Thunderstorm' };

/**
 * The place whose weather the characters show (the layout's `weather`; off without one): searched by name, chosen
 * from the places found, or turned off. Saved with the layout's other changes.
 */
export function WeatherPlace({ value, onChange }: { value: OverlayWeather | null | undefined; onChange: (weather: OverlayWeather | null) => void }) {
  const [open, setOpen] = useState(false);
  const [name, setName] = useState('');
  const [found, setFound] = useState<OverlayWeather[] | null>(null);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const report = useQuery({ queryKey: ['overlay-weather'], queryFn: ({ signal }) => apiGet<WeatherAnswer>('/overlay/weather', signal), refetchInterval: 60_000, enabled: !!value }).data?.report;
  const now = report && value && report.place === value.place ? `${KIND_NAMES[report.kind] ?? report.kind}, ${Math.round(report.temperature)} °C` : null;

  const search = async (e: FormEvent) => {
    e.preventDefault();
    if (!name.trim()) return;
    setBusy(true);
    setProblem(null);
    try {
      setFound(await apiGet<OverlayWeather[]>(`/overlay/weather/places?name=${encodeURIComponent(name.trim())}`));
    } catch (err) {
      setFound(null);
      setProblem((err as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <>
      <Button size="sm" variant="outline" onClick={() => setOpen(true)} title="The characters show the weather where you are, now and then">
        <CloudSun aria-hidden /> {value ? `Weather: ${value.place.split(',')[0]}${now ? ` (${now})` : ''}` : 'Weather'}
      </Button>
      {open && (
        <Dialog
          open
          title="Weather for the characters"
          description="Now and then the characters show the weather where you are: an umbrella in the rain, snowflakes, wind, a fan on a hot day, and a word when it changes."
          onClose={() => setOpen(false)}
          footer={
            <>
              {value && (
                <Button
                  variant="outline"
                  onClick={() => {
                    onChange(null);
                    setOpen(false);
                  }}
                >
                  No weather
                </Button>
              )}
              <Button onClick={() => setOpen(false)}>Done</Button>
            </>
          }
        >
          <div className="space-y-3 text-sm">
            <p className="text-muted">
              {value ? (
                <>
                  Now: <span className="text-foreground">{value.place}</span>
                  {now && <> · {now}</>}
                </>
              ) : (
                'Off: nothing is fetched.'
              )}
            </p>
            <form className="flex gap-2" onSubmit={(e) => void search(e)}>
              <input className="h-8 min-w-0 flex-1 rounded-md border border-border bg-background px-2 text-sm" value={name} onChange={(e) => setName(e.target.value)} placeholder="A town or city" aria-label="Place" maxLength={120} />
              <Button size="sm" type="submit" disabled={busy || !name.trim()}>
                {busy ? 'Searching…' : 'Search'}
              </Button>
            </form>
            {problem && <p className="text-danger">{problem}</p>}
            {found && found.length === 0 && <p className="text-muted">No place by that name.</p>}
            {found && found.length > 0 && (
              <ul className="space-y-1" aria-label="Places found">
                {found.map((p) => (
                  <li key={`${p.latitude},${p.longitude}`}>
                    <button
                      type="button"
                      className="w-full rounded-md border border-border px-2 py-1.5 text-left hover:border-accent"
                      onClick={() => {
                        onChange(p);
                        setFound(null);
                        setOpen(false);
                      }}
                    >
                      {p.place}
                    </button>
                  </li>
                ))}
              </ul>
            )}
            <p className="text-xs text-muted">
              Once you save, this PC fetches the forecast for the place every 30 minutes from MET Norway (api.met.no, CC BY 4.0), which sees this PC's address and the place. Places are searched with OpenStreetMap's Nominatim (© OpenStreetMap contributors). Only the place is kept.
            </p>
          </div>
        </Dialog>
      )}
    </>
  );
}
