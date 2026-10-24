import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useRouterState } from '@tanstack/react-router';
import {
  AppWindow,
  Database,
  Gauge,
  Plus,
  Save,
  ScrollText,
  Search,
  ShieldCheck,
  SlidersHorizontal,
  Sparkles,
  Trash2,
  Undo2,
  Video,
  type LucideIcon,
} from 'lucide-react';
import { useEffect, useId, useMemo, useState, type ReactNode } from 'react';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { useConfirm } from '@/components/ui/confirm';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { useToast } from '@/components/ui/toast';
import { RecordingPicker } from '@/features/recorder/RecordingPicker';
import { apiGet, apiRequest } from '@/lib/api/client';
import type { RecordingMode, SettingsResponse, UserSettings } from '@/lib/api/types';
import { cn } from '@/lib/utils';
import { AddressSection } from './AddressSection';
import { LanSection } from './LanSection';
import { ReportsSection } from './ReportsSection';
import { GROUPS, matches, SECTIONS } from './sections';
import { DEFAULT_TRIGGERS, TriggersSection } from './TriggersSection';

const DAYS = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];
const PRE_MINUTES = [1, 5, 10, 15, 30];


/**
 * Settings, saved to settings.json in the data folder, by topic: a list of topics to jump to (and a search), each
 * topic's settings in rows with what they do, and a mark on the topics with unsaved changes. The values are checked
 * on the server with the same rules the recorder uses, so a bad value can never stop it. Most changes apply within
 * seconds; sampling intervals at the next recording start; the tray icon and hotkey when the recorder restarts.
 */
export function SettingsPage() {
  const queryClient = useQueryClient();
  const toast = useToast();
  const { data, error } = useQuery({ queryKey: ['settings'], queryFn: ({ signal }) => apiGet<SettingsResponse>('/settings', signal) });
  const [draft, setDraft] = useState<UserSettings | null>(null);
  const [problems, setProblems] = useState<string | null>(null);
  const [search, setSearch] = useState('');
  const [active, setActive] = useState(SECTIONS[0]!.id);
  // One topic at a time: chosen in the list, or by a link to it (#recording, #triggers).
  const hash = useRouterState({ select: (s) => s.location.hash });
  useEffect(() => {
    if (hash && SECTIONS.some((s) => s.id === hash)) setActive(hash);
  }, [hash]);

  useEffect(() => {
    if (data && !draft) setDraft(structuredClone(data.settings));
  }, [data, draft]);

  const save = useMutation({
    mutationFn: (settings: UserSettings) => apiRequest<SettingsResponse>('PUT', '/settings', settings),
    onSuccess: (response) => {
      setProblems(null);
      if (response) {
        queryClient.setQueryData(['settings'], response);
        setDraft(structuredClone(response.settings));
      }
      toast.show({ title: 'Settings saved', description: 'The recorder picks them up within a few seconds.', tone: 'success' });
    },
    onError: (e) => setProblems(e instanceof Error ? e.message : 'The settings could not be saved.'),
  });

  const dirtyParts = useMemo(() => {
    if (!data || !draft) return new Set<string>();
    return new Set((Object.keys(draft) as (keyof UserSettings)[]).filter((k) => JSON.stringify(draft[k] ?? null) !== JSON.stringify(data.settings[k] ?? null)));
  }, [data, draft]);

  if (error) return <p className="text-sm text-danger">The settings could not be loaded.</p>;
  if (!data || !draft) return <p className="text-sm text-muted">Loading…</p>;
  const dirty = dirtyParts.size > 0;
  const dirtySections = SECTIONS.filter((s) => s.parts.some((p) => dirtyParts.has(p)));
  const enhanced = draft.enhanced ?? { flightRecorder: false, processIo: false };
  const visible = SECTIONS.filter((s) => matches(s, search.trim()));
  const topic = visible.some((s) => s.id === active) ? active : (visible[0]?.id ?? null);
  const shown = (id: string) => id === topic;

  const update = <K extends keyof UserSettings>(section: K, patch: Partial<UserSettings[K]>) => setDraft((d) => (d ? { ...d, [section]: { ...d[section], ...patch } } : d));
  const go = (id: string) => setActive(id);

  return (
    <form
      className={'flex flex-col gap-4 xl:h-[calc(100dvh-6.5rem)] xl:overflow-hidden'}
      onSubmit={(e) => {
        e.preventDefault();
        save.mutate(draft);
      }}
    >
      <div className={'z-10 -mx-4 flex flex-wrap items-center justify-between gap-3 border-b border-border bg-background/95 px-4 py-3 backdrop-blur'}>
        <div className="min-w-0">
          <h1 className="text-xl font-semibold">Settings</h1>
          <p className="truncate text-xs text-muted">
            Stored in <span className="font-mono">{data.file}</span>
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          {dirty && (
            <span className="text-xs text-warning" role="status">
              Unsaved changes{dirtySections.length ? ` in ${dirtySections.map((s) => s.title).join(', ')}` : ''}
            </span>
          )}
          <Button type="button" variant="ghost" disabled={!dirty} onClick={() => setDraft(structuredClone(data.settings))}>
            <Undo2 aria-hidden /> Revert
          </Button>
          <Button type="submit" disabled={!dirty || save.isPending}>
            <Save aria-hidden /> {save.isPending ? 'Saving…' : 'Save'}
          </Button>
        </div>
      </div>

      {problems && (
        <p role="alert" className="rounded-md border border-danger/50 bg-danger/10 px-3 py-2 text-sm">
          {problems}
        </p>
      )}

      <div className={'grid min-h-0 flex-1 grid-cols-1 gap-6 lg:grid-cols-[15rem_minmax(0,1fr)]'}>
        <nav aria-label="Settings topics" className={'flex flex-col gap-3 lg:max-h-full lg:self-start lg:overflow-auto'}>
          <label className="relative block">
            <span className="sr-only">Find a setting</span>
            <Search className="pointer-events-none absolute left-2 top-1/2 size-3.5 -translate-y-1/2 text-muted" aria-hidden />
            <input
              type="search"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Find a setting, e.g. hotkey"
              className="h-9 w-full rounded-md border border-border bg-background pl-7 pr-2 text-sm placeholder:text-muted"
            />
          </label>
          <div className="flex gap-1 overflow-x-auto pb-1 lg:flex-col lg:gap-3 lg:overflow-visible lg:pb-0">
            {GROUPS.map((group) => {
              const items = visible.filter((s) => s.group === group);
              if (items.length === 0) return null;
              return (
                <div key={group} className="flex shrink-0 gap-1 lg:flex-col lg:gap-0.5">
                  <p className="hidden px-2 text-[11px] font-semibold uppercase tracking-wide text-muted lg:block">{group}</p>
                  {items.map((s) => {
                    const Icon = s.icon;
                    const changed = s.parts.some((p) => dirtyParts.has(p));
                    return (
                      <button
                        key={s.id}
                        type="button"
                        onClick={() => go(s.id)}
                        aria-current={topic === s.id ? 'page' : undefined}
                        className={cn(
                          'flex shrink-0 items-center gap-2 rounded-md px-2 py-1.5 text-left text-sm',
                          topic === s.id ? 'bg-accent/10 font-medium text-foreground' : 'text-muted hover:bg-surface-muted hover:text-foreground',
                        )}
                      >
                        <Icon className="size-4 shrink-0" aria-hidden />
                        <span className="flex-1 truncate">{s.title}</span>
                        {changed && <span className="size-2 shrink-0 rounded-full bg-warning" aria-label="unsaved changes" title="Unsaved changes" />}
                      </button>
                    );
                  })}
                </div>
              );
            })}
          </div>
          {visible.length === 0 && <p className="px-2 text-sm text-muted">No setting matches “{search.trim()}”.</p>}
        </nav>

        <div className={'flex min-h-0 min-w-0 flex-col gap-6 overflow-y-auto pb-4'}>

          {shown('reports') && (
            <Anchor id="reports">
              <ReportsSection />
            </Anchor>
          )}

          {shown('recording') && (
            <Section id="recording" title="Recording" icon={Video} description="When the recorder records, and how much history an incident keeps." changed={dirtyParts.has('recorder')}>
              <fieldset className="flex flex-col gap-1.5 py-3">
                <legend className="text-sm font-medium">When to record</legend>
                {(
                  [
                    ['Manual', 'Only when I start it (tray, dashboard or hotkey capture).'],
                    ['Always', 'Whenever the recorder runs.'],
                    ['Scheduled', 'During the times below.'],
                  ] as [RecordingMode, string][]
                ).map(([mode, text]) => (
                  <label key={mode} className="flex items-start gap-2 text-sm">
                    <input type="radio" name="mode" checked={draft.recorder.mode === mode} onChange={() => update('recorder', { mode })} className="mt-1 accent-[var(--accent)]" />
                    <span>
                      <span className="font-medium">{mode}</span> <span className="text-muted">{text}</span>
                    </span>
                  </label>
                ))}
              </fieldset>
              <ToggleRow
                label="Watch for problems when not recording"
                help="Problem rules keep running in memory while nothing is recorded; a problem that creates incidents starts a recording, and its incident keeps the minutes before it. Nothing is stored until then. A pause stops this too."
                checked={draft.recorder.watchWhenNotRecording ?? true}
                onChange={(v) => update('recorder', { watchWhenNotRecording: v })}
              />
              {draft.recorder.mode === 'Scheduled' && (
                <div className="pb-3">
                  <ScheduleEditor schedule={draft.recorder.schedule} onChange={(schedule) => update('recorder', { schedule })} />
                </div>
              )}
              <Row label="Kept before an incident" help="Minutes of history saved when you capture.">
                {(id) => (
                  <select id={id} value={draft.recorder.preIncidentMinutes} onChange={(e) => update('recorder', { preIncidentMinutes: Number(e.target.value) })} className={INPUT}>
                    {PRE_MINUTES.map((m) => (
                      <option key={m} value={m}>
                        {m} minute{m > 1 ? 's' : ''}
                      </option>
                    ))}
                  </select>
                )}
              </Row>
              <NumberRow label="Recorded after an incident" unit="seconds" min={15} max={900} value={draft.recorder.postIncidentSeconds} onChange={(v) => update('recorder', { postIncidentSeconds: v })} />
              <NumberRow
                label="Memory for the recent minutes"
                help="The recent minutes are kept in memory, so an incident can include what happened before it."
                unit="MB"
                min={16}
                max={512}
                value={draft.recorder.ringBufferMaxMegabytes}
                onChange={(v) => update('recorder', { ringBufferMaxMegabytes: v })}
              />
            </Section>
          )}

          {shown('triggers') && (
            <Anchor id="triggers">
              <TriggersSection value={draft.triggers ?? DEFAULT_TRIGGERS} onChange={(triggers) => setDraft((d) => (d ? { ...d, triggers } : d))} />
            </Anchor>
          )}

          {shown('enhanced') && (
            <Section
              id="enhanced"
              title={
                <>
                  Enhanced mode
                </>
              }
              icon={Sparkles}
              description="Deeper evidence from Event Tracing for Windows, used only while recording. Both need the recorder to run as administrator; without it they stay off and the recorder card says why."
              changed={dirtyParts.has('enhanced')}
            >
              <ToggleRow
                label="Flight recorder trace with each incident"
                help="Windows Performance Recorder keeps recent detailed system, GPU and display activity in memory (up to 384 MB of memory while recording) and saves it as traces\incident.etl with each new incident (open it with Windows Performance Analyzer; it is in the ZIP export). Saving takes about half a minute in the background; a trace is typically around 100 MB."
                checked={enhanced.flightRecorder}
                onChange={(v) => setDraft((d) => (d ? { ...d, enhanced: { ...enhanced, flightRecorder: v } } : d))}
              />
              <ToggleRow
                label="True per-process disk and network rates"
                help="Kernel events tell disk and network transfers apart for every process (without it, a process's I/O is all transfers together). Adds a Network list to Top processes. Costs a little CPU while there is heavy network or disk traffic."
                checked={enhanced.processIo}
                onChange={(v) => setDraft((d) => (d ? { ...d, enhanced: { ...enhanced, processIo: v } } : d))}
              />
            </Section>
          )}

          {shown('privacy') && (
            <Section
              id="privacy"
              title="Privacy"
              icon={ShieldCheck}
              description="Everything recorded stays on this PC; nothing of it is uploaded."
              changed={dirtyParts.has('privacy')}
            >
              <ToggleRow label="Record process command lines" help="Can contain file names, URLs and passwords passed to programs. Off by default." checked={draft.privacy.captureCommandLines} onChange={(v) => update('privacy', { captureCommandLines: v })} />
              <ToggleRow label="Record user names" help="Which account runs each process. When off, user and computer names are also removed from Windows events." checked={draft.privacy.captureUserNames} onChange={(v) => update('privacy', { captureUserNames: v })} />
              <ToggleRow label="Record Wi-Fi network names" help="The name of the network you are connected to." checked={draft.privacy.captureWifiNetworkNames} onChange={(v) => update('privacy', { captureWifiNetworkNames: v })} />
              <ToggleRow label="Collect Windows events" help="Driver resets, crashes, blue screens, disk errors and more, classified by the event rules." checked={draft.privacy.collectWindowsEvents} onChange={(v) => update('privacy', { collectWindowsEvents: v })} />
              <ToggleRow label="Collect network details" help="Adapters, link speed and Wi-Fi signal." checked={draft.privacy.collectNetworkDetails} onChange={(v) => update('privacy', { collectNetworkDetails: v })} />
            </Section>
          )}

          {shown('events') && (
            <Section id="events" title="Windows events" icon={ScrollText} description="How much of the Windows event logs the recorder reads, and how much it keeps." changed={dirtyParts.has('events')}>
              <NumberRow label="Read back on start" help="Events from before the recorder started." unit="minutes" min={0} max={1440} value={draft.events.backfillMinutes} onChange={(v) => update('events', { backfillMinutes: v })} />
              <NumberRow label="After a crash, read back up to" help="After an unexpected shutdown, the events that led to it." unit="hours" min={0} max={168} value={draft.events.bootBackfillMaxHours} onChange={(v) => update('events', { bootBackfillMaxHours: v })} />
              <NumberRow label="Most per rule per minute" help="A flood of the same event is counted, not stored a thousand times." unit="" min={1} max={1000} value={draft.events.maxPerRulePerMinute} onChange={(v) => update('events', { maxPerRulePerMinute: v })} />
              <NumberRow label="Longest message kept" unit="characters" min={200} max={32000} step={100} value={draft.events.maxMessageLength} onChange={(v) => update('events', { maxMessageLength: v })} />
            </Section>
          )}

          {shown('storage') && (
            <Section id="storage" title="Storage" icon={Database} description="About 300 MB per day of full detail at the default sampling. Pinned incidents are always kept." changed={dirtyParts.has('storage')}>
              <p className="text-sm">
                <Link to="/storage" className="text-accent hover:underline">
                  See what uses the space and clean up
                </Link>
              </p>
              <NumberRow label="Full detail kept for" help="Every sample, for History and incidents." unit="hours" min={1} max={72} value={draft.storage.telemetryRetentionHours} onChange={(v) => update('storage', { telemetryRetentionHours: v })} />
              <NumberRow label="One-minute summaries kept for" help="Lowest, average and highest of each minute, for long History ranges." unit="days" min={1} max={365} value={draft.storage.rollupRetentionDays} onChange={(v) => update('storage', { rollupRetentionDays: v })} />
              <NumberRow label="Write to disk every" unit="seconds" min={1} max={60} value={draft.storage.writeBehindSeconds} onChange={(v) => update('storage', { writeBehindSeconds: v })} />
              <NumberRow label="Most incidents kept" help="The oldest unpinned incidents are removed beyond this." unit="(0 = all)" min={0} max={100000} value={draft.storage.maxIncidents} onChange={(v) => update('storage', { maxIncidents: v })} />
              <NumberRow
                label="Most space used"
                help="Above it, full detail is kept for a shorter time (at least 2 hours), then summaries (at least 7 days), then the oldest unpinned incidents are removed."
                unit="GB (0 = no limit)"
                min={0}
                max={10000}
                value={draft.storage.dataLimitGb}
                onChange={(v) => update('storage', { dataLimitGb: v })}
              />
              <NumberRow
                label="Keep free on the drive"
                help="Below it, full detail and summaries are kept for a shorter time; incidents are never removed for it. Below 1 GB nothing new is recorded."
                unit="GB (0 = only 1 GB)"
                min={0}
                max={1000}
                value={draft.storage.keepFreeGb}
                onChange={(v) => update('storage', { keepFreeGb: v })}
              />
            </Section>
          )}

          {shown('sampling') && (
            <Section id="sampling" title="Sampling" icon={Gauge} description="How often the recorder measures. Sampling changes apply the next time recording starts." changed={dirtyParts.has('sampling')}>
              <NumberRow label="System metrics every" unit="ms" min={250} max={10000} step={250} value={draft.sampling.systemIntervalMs} onChange={(v) => update('sampling', { systemIntervalMs: v })} />
              <NumberRow label="Sensors every" unit="ms" min={250} max={10000} step={250} value={draft.sampling.sensorIntervalMs} onChange={(v) => update('sampling', { sensorIntervalMs: v })} />
              <NumberRow label="Processes every" unit="ms" min={500} max={30000} step={500} value={draft.sampling.processIntervalMs} onChange={(v) => update('sampling', { processIntervalMs: v })} />
              <NumberRow label="Inventory every" unit="seconds" min={5} max={300} value={draft.sampling.inventoryIntervalSeconds} onChange={(v) => update('sampling', { inventoryIntervalSeconds: v })} />
              <NumberRow label="Processes kept per sample" unit="" min={5} max={200} value={draft.sampling.topProcessCount} onChange={(v) => update('sampling', { topProcessCount: v })} />
              <NumberRow
                label="Recorder CPU budget"
                help="Above it, the recorder slows its sensor and process sampling by itself."
                unit="%"
                min={0.5}
                max={10}
                step={0.5}
                value={draft.sampling.cpuBudgetPercent}
                onChange={(v) => update('sampling', { cpuBudgetPercent: v })}
              />
            </Section>
          )}

          {shown('app') && (
            <Section id="app" title="Recorder app" icon={AppWindow} description="The tray icon and hotkey change when the recorder restarts." changed={dirtyParts.has('agent')}>
              <ToggleRow label="Show the tray icon" checked={draft.agent.showTrayIcon} onChange={(v) => update('agent', { showTrayIcon: v })} />
              <ToggleRow label="Notify when an incident is captured" checked={draft.agent.notifyOnCapture} onChange={(v) => update('agent', { notifyOnCapture: v })} />
              <Row label="Capture hotkey" help='Works in full-screen games. Modifiers Ctrl, Alt, Shift, Win plus a key, e.g. "Ctrl+Alt+Shift+I". Empty turns it off.'>
                {(id) => <input id={id} value={draft.agent.captureHotkey} onChange={(e) => update('agent', { captureHotkey: e.target.value })} className={INPUT} />}
              </Row>
            </Section>
          )}

          {shown('devices') && (
            <Anchor id="devices">
              <div className="flex flex-col gap-4">
                <AddressSection />
                <LanSection />
              </div>
            </Anchor>
          )}
        </div>
      </div>
    </form>
  );
}

const INPUT = 'h-9 w-full rounded-md border border-border bg-background px-2 text-sm';

/** A topic the list jumps to (the sections that draw their own card). */
function Anchor({ id, children }: { id: string; children: ReactNode }) {
  return (
    <div data-settings-section={id} className="scroll-mt-36 [&>*]:lg:col-span-1">
      {children}
    </div>
  );
}

function Section({ id, title, icon: Icon, description, changed, children }: { id: string; title: ReactNode; icon: LucideIcon; description?: string; changed: boolean; children: ReactNode }) {
  const headingId = useId();
  return (
    <section data-settings-section={id} id={id} aria-labelledby={headingId} className="scroll-mt-36">
      <Card>
        <CardHeader className="flex-col items-start gap-1">
          <CardTitle id={headingId} className="flex items-center gap-2">
            <Icon className="size-4 text-accent" aria-hidden /> {title}
            {changed && <Badge tone="warning">Unsaved</Badge>}
          </CardTitle>
          {description && <p className="text-sm text-muted">{description}</p>}
        </CardHeader>
        <CardContent className="flex flex-col divide-y divide-border">{children}</CardContent>
      </Card>
    </section>
  );
}

/** One setting: what it is and does on the left, its control on the right. */
function Row({ label, help, children }: { label: string; help?: string; children: (id: string) => ReactNode }) {
  const id = useId();
  return (
    <div className="flex flex-col gap-2 py-3 sm:flex-row sm:items-center sm:justify-between sm:gap-6">
      <div className="min-w-0">
        <label htmlFor={id} className="text-sm font-medium">
          {label}
        </label>
        {help && <p className="text-xs text-muted">{help}</p>}
      </div>
      <div className="w-full shrink-0 sm:w-60">{children(id)}</div>
    </div>
  );
}

function NumberRow({ label, help, unit, min, max, step = 1, value, onChange }: { label: string; help?: string; unit: string; min: number; max: number; step?: number; value: number; onChange: (v: number) => void }) {
  const range = `${min}–${max}${unit ? ` ${unit}` : ''}`;
  return (
    <Row label={label} help={help ? `${help} ${range}.` : range}>
      {(id) => (
        <span className="flex items-center gap-2">
          <input
            id={id}
            type="number"
            min={min}
            max={max}
            step={step}
            value={value}
            onChange={(e) => onChange(e.target.value === '' ? 0 : Number(e.target.value))}
            aria-invalid={value < min || value > max || undefined}
            className={`${INPUT} aria-[invalid=true]:border-danger`}
          />
          {unit && !unit.startsWith('(') && <span className="w-16 shrink-0 text-xs text-muted">{unit}</span>}
        </span>
      )}
    </Row>
  );
}

/** A setting that is on or off: the whole row toggles it. */
function ToggleRow({ label, help, checked, onChange }: { label: string; help?: string; checked: boolean; onChange: (v: boolean) => void }) {
  return (
    <label className="flex cursor-pointer items-start justify-between gap-6 py-3">
      <span className="min-w-0">
        <span className="text-sm font-medium">{label}</span>
        {help && <span className="block text-xs text-muted">{help}</span>}
      </span>
      <span className="relative mt-0.5 inline-flex shrink-0">
        <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} className="peer sr-only" />
        <span
          aria-hidden
          className="h-5 w-9 rounded-full bg-border transition-colors peer-checked:bg-accent peer-focus-visible:outline-2 peer-focus-visible:outline-offset-2 peer-focus-visible:outline-accent"
        />
        <span aria-hidden className="absolute left-0.5 top-0.5 size-4 rounded-full bg-surface shadow transition-transform peer-checked:translate-x-4" />
      </span>
    </label>
  );
}

function ScheduleEditor({ schedule, onChange }: { schedule: UserSettings['recorder']['schedule']; onChange: (s: UserSettings['recorder']['schedule']) => void }) {
  const confirm = useConfirm();
  const set = (index: number, patch: Partial<UserSettings['recorder']['schedule'][number]>) => onChange(schedule.map((w, i) => (i === index ? { ...w, ...patch } : w)));
  const [picking, setPicking] = useState<number | null>(null);
  return (
    <fieldset className="flex flex-col gap-3 rounded-md border border-border p-3">
      <legend className="px-1 text-sm font-medium">Schedule</legend>
      {schedule.length === 0 && <p className="text-xs text-warning">Add at least one time window.</p>}
      {schedule.map((window, index) => (
        <div key={index} className="flex flex-col gap-2 border-b border-border pb-3 last:border-0 last:pb-0">
          <div className="flex flex-wrap gap-1" role="group" aria-label={`Days for window ${index + 1}`}>
            {DAYS.map((day) => {
              const on = window.days.includes(day);
              return (
                <button
                  key={day}
                  type="button"
                  aria-pressed={on}
                  onClick={() => set(index, { days: on ? window.days.filter((d) => d !== day) : DAYS.filter((d) => d === day || window.days.includes(d)) })}
                  className={`rounded px-2 py-1 text-xs ${on ? 'bg-accent text-accent-foreground' : 'bg-surface-muted text-muted'}`}
                >
                  {day.slice(0, 3)}
                </button>
              );
            })}
          </div>
          <div className="flex flex-wrap items-center gap-2 text-sm">
            <label className="flex items-center gap-1.5">
              From
              <input type="time" value={window.start.slice(0, 5)} onChange={(e) => set(index, { start: `${e.target.value}:00` })} className="h-8 rounded-md border border-border bg-background px-2" />
            </label>
            <label className="flex items-center gap-1.5">
              to
              <input type="time" value={window.end.slice(0, 5)} onChange={(e) => set(index, { end: `${e.target.value}:00` })} className="h-8 rounded-md border border-border bg-background px-2" />
            </label>
            <Button type="button" size="icon" variant="ghost" aria-label={`Remove window ${index + 1}`} onClick={async () => (await confirm({ title: `Remove window ${index + 1}?`, description: 'Recording no longer happens in it (save the settings to keep the change).', action: 'Remove' })) && onChange(schedule.filter((_, i) => i !== index))}>
              <Trash2 aria-hidden />
            </Button>
          </div>
          <div className="flex flex-wrap items-center gap-2 text-sm">
            <span className="text-muted">Records</span>
            <span>{window.selection ? `a custom selection (${window.selection.items.length} items)` : 'everything'}</span>
            <Button type="button" size="sm" variant="ghost" onClick={() => setPicking(index)} aria-label={`Choose what window ${index + 1} records`}>
              <SlidersHorizontal aria-hidden /> Choose…
            </Button>
          </div>
        </div>
      ))}
      <RecordingPicker
        open={picking !== null}
        title={`What window ${(picking ?? 0) + 1} records`}
        initial={picking !== null ? (schedule[picking]?.selection?.items ?? null) : null}
        onClose={() => setPicking(null)}
        onConfirm={(selection) => {
          if (picking !== null) set(picking, { selection: selection ? { items: selection } : null });
          setPicking(null);
        }}
        confirmLabel="Use this selection"
      />
      <Button type="button" size="sm" variant="outline" className="self-start" onClick={() => onChange([...schedule, { days: DAYS.slice(0, 5), start: '09:00:00', end: '18:00:00' }])}>
        <Plus aria-hidden /> Add a time window
      </Button>
    </fieldset>
  );
}
