import { useQuery } from '@tanstack/react-query';
import { ChevronRight, Plus, Search, X } from 'lucide-react';
import { useId, useMemo, useState } from 'react';
import { Button } from '@/components/ui/button';
import { Dialog } from '@/components/ui/dialog';
import { apiGet } from '@/lib/api/client';
import type { AnomalyRuleKind, CustomEventMatch, CustomTriggerRule, Severity, TriggerCategory, TriggerChoices, TriggerReading, TriggerSuggestions } from '@/lib/api/types';
import { cn } from '@/lib/utils';

const INPUT = 'h-8 w-full rounded-md border border-border bg-background px-2 text-sm';
const SEVERITIES: Severity[] = ['Info', 'Low', 'Medium', 'High', 'Critical'];
const READING = 'reading:';
const LEVELS: { level: number; label: string }[] = [
  { level: 1, label: 'Critical' },
  { level: 2, label: 'Error' },
  { level: 3, label: 'Warning' },
  { level: 4, label: 'Information' },
];

/** How a measurement rule decides, in plain words. */
export const KINDS: { kind: Exclude<AnomalyRuleKind, 'Event'>; label: string }[] = [
  { kind: 'ThresholdDuration', label: 'Stays past a value for a while' },
  { kind: 'Threshold', label: 'Goes past a value' },
  { kind: 'RateOfChange', label: 'Changes suddenly' },
  { kind: 'BaselineDeviation', label: 'Departs from its usual level' },
  { kind: 'CounterSpike', label: 'Adds up to a count within a window' },
];

export function useTriggerChoices() {
  return useQuery({ queryKey: ['triggers', 'choices'], queryFn: ({ signal }) => apiGet<TriggerChoices>('/triggers/choices', signal), staleTime: Infinity });
}

function useReadings(enabled: boolean) {
  return useQuery({ queryKey: ['triggers', 'readings'], queryFn: ({ signal }) => apiGet<TriggerReading[]>('/triggers/readings', signal), staleTime: 60_000, enabled });
}

function useSuggestions(enabled: boolean) {
  return useQuery({ queryKey: ['triggers', 'suggestions'], queryFn: ({ signal }) => apiGet<TriggerSuggestions>('/triggers/suggestions', signal), staleTime: 60_000, enabled });
}

/** A new rule's starting point: VRAM above 90 % for 5 minutes. */
export function newRule(taken: string[], category: string | null = null): CustomTriggerRule {
  let n = 1;
  while (taken.includes(`my-rule-${n}`)) n++;
  return {
    id: `my-rule-${n}`,
    name: '',
    kind: 'ThresholdDuration',
    metric: 'gpu.vram',
    category,
    direction: 'Above',
    threshold: 90,
    durationSeconds: 300,
    windowSeconds: 10,
    eventRules: [],
    processEvents: [],
    processNames: [],
    displayChanges: [],
    displayTargets: [],
    windowsEvents: [],
    severity: 'Medium',
    enabled: true,
    createIncident: true,
    cooldownMinutes: 30,
  };
}

const isReading = (metric: string | null | undefined) => !!metric?.startsWith(READING);

/**
 * The category a measurement belongs to (as the recorder decides): a shipped metric ("gpu.vram") or a reading
 * ("reading:sensor.lhm/gpu-nvidia/0/clock/0"). Hardware sensors go with their hardware; fans, pumps and voltages with the sensors.
 */
export function categoryForMetric(metric: string | null | undefined): string {
  const m = (metric ?? '').replace(/^reading:/, '').toLowerCase();
  if (m.startsWith('sensor.')) {
    const hw = m.slice('sensor.'.length).replace(/^lhm\//, '');
    if (hw.startsWith('gpu')) return 'graphics';
    if (/^(intelcpu|amdcpu|cpu)/.test(hw)) return 'processor';
    if (/^(memory|ram|vram)/.test(hw)) return 'memory';
    if (/^(nvme|hdd|ssd|disk)/.test(hw)) return 'storage';
    if (/^(nic|network)/.test(hw)) return 'network';
    return 'sensors';
  }
  if (m.startsWith('cpu')) return 'processor';
  if (m.startsWith('memory')) return 'memory';
  if (m.startsWith('gpu')) return 'graphics';
  if (m.startsWith('disk') || m.startsWith('volume')) return 'storage';
  if (m.startsWith('net')) return 'network';
  return 'sensors';
}

/** The allowed threshold range and unit of a rule (as the recorder checks it). */
export function thresholdRange(rule: CustomTriggerRule, choices: TriggerChoices | undefined): { min: number; max: number; unit: string } {
  if (rule.kind === 'BaselineDeviation') return { min: 1, max: 10, unit: 'σ' };
  if (rule.kind === 'RateOfChange') return { min: 10, max: 100, unit: '%' };
  if (rule.kind === 'CounterSpike') return { min: 1, max: 100000, unit: '' };
  if (isReading(rule.metric)) return { min: -1e12, max: 1e12, unit: rule.readingUnit ?? '' };
  const metric = choices?.metrics.find((m) => m.id === rule.metric);
  return { min: metric?.min ?? 0, max: metric?.max ?? 0, unit: metric?.unit ?? '' };
}

function metricLabel(rule: CustomTriggerRule, choices: TriggerChoices | undefined) {
  if (isReading(rule.metric)) return rule.readingLabel ?? rule.metric!.slice(READING.length);
  return choices?.metrics.find((m) => m.id === rule.metric)?.label ?? rule.metric ?? 'a measurement';
}

/** The rule in plain words (shown in its row before it is saved). */
export function describeRule(rule: CustomTriggerRule, choices: TriggerChoices | undefined): string {
  if (rule.kind === 'Event') {
    const who = rule.processNames.length ? rule.processNames.join(', ') : 'a program';
    const which = rule.displayTargets.length ? rule.displayTargets.join(', ') : 'a display';
    const parts = [
      ...rule.processEvents.map((p) => `${who} ${p.toLowerCase()}`),
      ...rule.displayChanges.map((d) => `${which}: ${spaced(d)}`),
      ...rule.eventRules.map((id) => choices?.windowsEvents.find((e) => e.id === id)?.title ?? id),
      ...rule.windowsEvents.map((e) => `${e.channel}: ${e.provider || 'any source'}${e.eventIds.length ? ` event ${e.eventIds.join('/')}` : ''}`),
    ];
    return parts.length ? `When ${parts.join('; ')}.` : 'Choose what it watches.';
  }
  const metric = metricLabel(rule, choices);
  const { unit } = thresholdRange(rule, choices);
  const side = rule.direction === 'Above' ? 'above' : 'below';
  const value = `${rule.threshold}${unit ? ` ${unit}` : ''}`;
  switch (rule.kind) {
    case 'Threshold':
      return `${metric} ${side} ${value}.`;
    case 'ThresholdDuration':
      return `${metric} ${side} ${value} for at least ${rule.durationSeconds} s.`;
    case 'RateOfChange':
      return `${metric} ${rule.direction === 'Above' ? 'rises' : 'drops'} by ${value} within ${rule.windowSeconds} s.`;
    case 'BaselineDeviation':
      return `${metric} more than ${rule.threshold} σ ${side} its usual level for at least ${rule.durationSeconds} s.`;
    default:
      return `${metric} adds up to ${rule.threshold} within ${rule.windowSeconds} s.`;
  }
}

const spaced = (name: string) => name.replace(/([a-z])([A-Z])/g, '$1 $2');

/** What is wrong with a Windows event a rule describes; null when it can be kept. */
function eventProblem(e: CustomEventMatch, index: number): string | null {
  if (!e.channel.trim()) return `Windows event ${index + 1}: choose its log.`;
  if (!e.provider?.trim() && e.eventIds.length === 0 && (e.levels.length === 0 || e.levels.some((l) => l > 3)))
    return `Windows event ${index + 1}: choose a source, event IDs, or only critical, error and warning.`;
  return null;
}

/** What is wrong with a rule before saving; null when it can be kept. */
export function ruleProblem(rule: CustomTriggerRule, choices: TriggerChoices | undefined): string | null {
  if (!rule.name.trim()) return 'Give the rule a name.';
  if (rule.kind === 'Event') {
    if (rule.eventRules.length + rule.processEvents.length + rule.displayChanges.length + rule.windowsEvents.length === 0) return 'Choose at least one event to watch.';
    return rule.windowsEvents.map(eventProblem).find((p) => p) ?? null;
  }
  if (!rule.metric) return 'Choose a measurement.';
  const { min, max } = thresholdRange(rule, choices);
  if (!Number.isFinite(rule.threshold) || rule.threshold < min || rule.threshold > max) return `The value must be between ${min} and ${max}.`;
  if ((rule.kind === 'ThresholdDuration' || rule.kind === 'BaselineDeviation') && (rule.durationSeconds < 1 || rule.durationSeconds > 3600)) return 'The duration must be 1–3600 s.';
  if ((rule.kind === 'RateOfChange' || rule.kind === 'CounterSpike') && (rule.windowSeconds < 1 || rule.windowSeconds > 600)) return 'The window must be 1–600 s.';
  if (rule.cooldownMinutes < 0 || rule.cooldownMinutes > 1440) return 'The cooldown must be 0–1440 minutes.';
  return null;
}

/** Adds or edits one of the user's own rules. */
export function CustomRuleEditor({
  rule,
  isNew,
  categories,
  onSave,
  onClose,
}: {
  rule: CustomTriggerRule;
  isNew: boolean;
  categories: TriggerCategory[];
  onSave: (rule: CustomTriggerRule) => void;
  onClose: () => void;
}) {
  const { data: choices } = useTriggerChoices();
  const [draft, setDraft] = useState(rule);
  const event = draft.kind === 'Event';
  const { data: readings } = useReadings(!event);
  const { data: suggestions } = useSuggestions(event);
  const set = (change: Partial<CustomTriggerRule>) => setDraft((d) => ({ ...d, ...change }));
  const ids = { name: useId(), watch: useId(), kind: useId(), direction: useId(), value: useId(), duration: useId(), window: useId(), severity: useId(), cooldown: useId(), category: useId() };
  const { min, max, unit } = thresholdRange(draft, choices);
  const problem = ruleProblem(draft, choices);
  const toggle = (key: 'eventRules' | 'processEvents' | 'displayChanges', item: string) =>
    set({ [key]: draft[key].includes(item) ? draft[key].filter((x) => x !== item) : [...draft[key], item] } as Partial<CustomTriggerRule>);
  const setEvent = (index: number, change: Partial<CustomEventMatch>) => set({ windowsEvents: draft.windowsEvents.map((e, i) => (i === index ? { ...e, ...change } : e)) });

  return (
    <Dialog
      open
      title={isNew ? 'Add a rule' : `Edit ${rule.name || 'rule'}`}
      description="Your rule runs like the shipped ones: it records an anomaly, and creates an incident when you choose."
      onClose={onClose}
      className="max-w-3xl"
      footer={
        <>
          {problem && <p className="mr-auto text-xs text-danger">{problem}</p>}
          <Button type="button" variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button type="button" disabled={!!problem} onClick={() => onSave({ ...draft, name: draft.name.trim() })}>
            {isNew ? 'Add the rule' : 'Keep the changes'}
          </Button>
        </>
      }
    >
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
        <Field id={ids.name} label="Name">
          <input id={ids.name} value={draft.name} maxLength={80} onChange={(e) => set({ name: e.target.value })} placeholder="e.g. GPU memory full while gaming" className={INPUT} />
        </Field>
        <Field id={ids.category} label="Category">
          <select id={ids.category} value={draft.category ?? ''} onChange={(e) => set({ category: e.target.value || null })} className={INPUT}>
            <option value="">By what it watches</option>
            {categories.map((c) => (
              <option key={c.id} value={c.id}>
                {c.name}
              </option>
            ))}
          </select>
        </Field>
        <Field id={ids.watch} label="It watches" wide>
          <select id={ids.watch} value={event ? 'event' : 'measure'} onChange={(e) => set({ kind: e.target.value === 'event' ? 'Event' : 'ThresholdDuration' })} className={INPUT}>
            <option value="measure">A measurement or reading</option>
            <option value="event">Events (programs, displays, Windows events)</option>
          </select>
        </Field>
        {!event && (
          <>
            <div className="sm:col-span-2">
              <ReadingPicker
                value={draft.metric}
                label={metricLabel(draft, choices)}
                metrics={choices?.metrics ?? []}
                readings={readings ?? []}
                onPick={(metric, readingLabel, readingUnit) => {
                  // The category follows the measurement, when one of the categories fits it.
                  const fits = categoryForMetric(metric);
                  set({ metric, readingLabel, readingUnit, category: categories.some((c) => c.id === fits) ? fits : draft.category });
                }}
              />
            </div>
            <Field id={ids.kind} label="Abnormal when it">
              <select id={ids.kind} value={draft.kind} onChange={(e) => set({ kind: e.target.value as AnomalyRuleKind })} className={INPUT}>
                {KINDS.map((k) => (
                  <option key={k.kind} value={k.kind}>
                    {k.label}
                  </option>
                ))}
              </select>
            </Field>
            {draft.kind !== 'CounterSpike' && (
              <Field id={ids.direction} label="Direction">
                <select id={ids.direction} value={draft.direction} onChange={(e) => set({ direction: e.target.value as CustomTriggerRule['direction'] })} className={INPUT}>
                  <option value="Above">{draft.kind === 'RateOfChange' ? 'Rises' : 'Above'}</option>
                  <option value="Below">{draft.kind === 'RateOfChange' ? 'Drops' : 'Below'}</option>
                </select>
              </Field>
            )}
            <Field
              id={ids.value}
              label={`${draft.kind === 'BaselineDeviation' ? 'Deviation' : draft.kind === 'RateOfChange' ? 'Change' : draft.kind === 'CounterSpike' ? 'Count' : 'Value'}${unit ? ` (${unit})` : ''}`}
              hint={isReading(draft.metric) && draft.kind !== 'BaselineDeviation' && draft.kind !== 'RateOfChange' ? 'In the reading’s own unit' : `${min}–${max}`}
            >
              <input id={ids.value} type="number" step="any" value={draft.threshold} onChange={(e) => set({ threshold: Number(e.target.value) })} className={INPUT} />
            </Field>
            {(draft.kind === 'ThresholdDuration' || draft.kind === 'BaselineDeviation') && (
              <Field id={ids.duration} label="For at least (s)" hint="1–3600">
                <input id={ids.duration} type="number" min={1} max={3600} value={draft.durationSeconds} onChange={(e) => set({ durationSeconds: Number(e.target.value) })} className={INPUT} />
              </Field>
            )}
            {(draft.kind === 'RateOfChange' || draft.kind === 'CounterSpike') && (
              <Field id={ids.window} label="Within (s)" hint="1–600">
                <input id={ids.window} type="number" min={1} max={600} value={draft.windowSeconds} onChange={(e) => set({ windowSeconds: Number(e.target.value) })} className={INPUT} />
              </Field>
            )}
          </>
        )}
        {event && (
          <div className="flex flex-col gap-4 sm:col-span-2">
            <Group title="Programs">
              <Checks items={(choices?.processEvents ?? []).map((p) => ({ id: p, label: spaced(p) }))} chosen={draft.processEvents} onToggle={(p) => toggle('processEvents', p)} />
              <Names
                label="Only these programs"
                hint="Empty: any program. * matches any text, e.g. chrome* or game.exe."
                names={draft.processNames}
                suggestions={suggestions?.processes ?? []}
                onChange={(processNames) => set({ processNames })}
              />
            </Group>
            <Group title="Displays">
              <Checks items={(choices?.displayChanges ?? []).map((d) => ({ id: d, label: spaced(d) }))} chosen={draft.displayChanges} onToggle={(d) => toggle('displayChanges', d)} />
              <Names label="Only these displays" hint="Empty: any display." names={draft.displayTargets} suggestions={suggestions?.displays ?? []} onChange={(displayTargets) => set({ displayTargets })} />
            </Group>
            <Group title="Windows events PC Diagnostic knows">
              <SearchChecks items={(choices?.windowsEvents ?? []).map((e) => ({ id: e.id, label: e.title, hint: e.id }))} chosen={draft.eventRules} onToggle={(e) => toggle('eventRules', e)} />
            </Group>
            <Group title="Windows events you describe">
              <p className="text-xs text-muted">Any event Windows logs: its log, its source and its event IDs (as Event Viewer shows them).</p>
              {draft.windowsEvents.map((e, i) => (
                <EventRow
                  key={i}
                  index={i}
                  value={e}
                  channels={suggestions?.channels ?? []}
                  providers={suggestions?.providers ?? []}
                  onChange={(change) => setEvent(i, change)}
                  onRemove={() => set({ windowsEvents: draft.windowsEvents.filter((_, j) => j !== i) })}
                />
              ))}
              <div>
                <Button type="button" size="sm" variant="outline" onClick={() => set({ windowsEvents: [...draft.windowsEvents, { channel: 'System', provider: '', eventIds: [], levels: [] }] })}>
                  <Plus aria-hidden /> Add a Windows event
                </Button>
              </div>
            </Group>
          </div>
        )}
        <Field id={ids.severity} label="Severity">
          <select id={ids.severity} value={draft.severity} onChange={(e) => set({ severity: e.target.value as Severity })} className={INPUT}>
            {SEVERITIES.map((s) => (
              <option key={s}>{s}</option>
            ))}
          </select>
        </Field>
        <Field id={ids.cooldown} label="Cooldown (min)" hint="0–1440: no new incident from it for this long">
          <input id={ids.cooldown} type="number" min={0} max={1440} value={draft.cooldownMinutes} onChange={(e) => set({ cooldownMinutes: Number(e.target.value) })} className={INPUT} />
        </Field>
        <label className="flex items-center gap-2 text-sm sm:col-span-2">
          <input type="checkbox" checked={draft.createIncident} onChange={(e) => set({ createIncident: e.target.checked })} className="size-4 accent-[var(--accent)]" />
          Creates an incident (otherwise it records an anomaly only)
        </label>
      </div>
      <p className="mt-3 rounded-md bg-muted/10 px-3 py-2 text-xs text-muted">{describeRule(draft, choices)}</p>
    </Dialog>
  );
}

/**
 * The measurement a rule watches: the common ones (each GPU, drive or adapter separately), then every reading the
 * system has, by device, with a search.
 */
function ReadingPicker({
  value,
  label,
  metrics,
  readings,
  onPick,
}: {
  value: string | null;
  label: string;
  metrics: TriggerChoices['metrics'];
  readings: TriggerReading[];
  onPick: (metric: string, readingLabel?: string, readingUnit?: string) => void;
}) {
  const searchId = useId();
  const [query, setQuery] = useState('');
  // Groups start closed; the chosen measurement's group is open, and a search opens the groups it finds.
  const [open, setOpen] = useState<Set<string>>(() => new Set());
  const toggle = (group: string) => setOpen((o) => {
    const next = new Set(o);
    if (next.has(group)) next.delete(group);
    else next.add(group);
    return next;
  });
  const groups = useMemo(() => {
    const q = query.trim().toLowerCase();
    const hit = (...texts: string[]) => !q || texts.some((t) => t.toLowerCase().includes(q));
    const result: { group: string; items: { value: string; label: string; unit: string; readingLabel?: string }[] }[] = [];
    const common = metrics.filter((m) => hit(m.label, m.id, 'common')).map((m) => ({ value: m.id, label: `${m.label}${m.perDevice ? ' (each device)' : ''}`, unit: m.unit }));
    if (common.length) result.push({ group: 'Common measurements', items: common });
    const byGroup = new Map<string, { value: string; label: string; unit: string; readingLabel?: string }[]>();
    for (const r of readings) {
      if (!hit(r.label, r.group, r.id)) continue;
      const items = byGroup.get(r.group) ?? [];
      items.push({ value: READING + r.id, label: r.label, unit: r.unit, readingLabel: `${r.group} › ${r.label}` });
      byGroup.set(r.group, items);
    }
    for (const [group, items] of byGroup) result.push({ group, items });
    return result;
  }, [metrics, readings, query]);
  const count = groups.reduce((n, g) => n + g.items.length, 0);

  return (
    <div className="flex flex-col gap-1">
      <label htmlFor={searchId} className="text-xs font-medium">
        Measurement: <span className="font-normal text-muted">{value ? label : 'none chosen'}</span>
      </label>
      <span className="relative">
        <Search className="pointer-events-none absolute left-2 top-2 size-4 text-muted" aria-hidden />
        <input id={searchId} type="search" value={query} onChange={(e) => setQuery(e.target.value)} placeholder={`Search ${metrics.length + readings.length} readings (CPU, GPU, temperature, fan…)`} className={`${INPUT} pl-8`} />
      </span>
      <div role="listbox" aria-label="Readings" className="max-h-56 overflow-auto rounded-md border border-border">
        {count === 0 && <p className="p-2 text-sm text-muted">No reading matches.</p>}
        {groups.map((g) => {
          const expanded = !!query.trim() || open.has(g.group);
          const chosen = g.items.some((i) => i.value === value);
          return (
          <div key={g.group} role="group" aria-label={g.group}>
            <button
              type="button"
              aria-expanded={expanded}
              onClick={() => toggle(g.group)}
              className="sticky top-0 flex w-full items-center gap-1.5 bg-surface px-2 py-1 text-left text-xs font-semibold uppercase tracking-wide text-muted hover:text-foreground"
            >
              <ChevronRight className={cn('size-3.5 shrink-0 transition-transform', expanded && 'rotate-90')} aria-hidden />
              <span className="truncate">{g.group}</span>
              <span className="font-normal normal-case">· {g.items.length}</span>
              {chosen && <span className="ml-auto font-normal normal-case text-accent">chosen</span>}
            </button>
            {expanded && g.items.map((item) => (
              <button
                key={item.value}
                type="button"
                role="option"
                aria-selected={item.value === value}
                onClick={() => onPick(item.value, item.readingLabel, item.unit)}
                className={cn('flex w-full items-center justify-between gap-2 px-3 py-1 text-left text-sm hover:bg-accent/10', item.value === value && 'bg-accent/15 font-medium')}
              >
                <span className="truncate">{item.label}</span>
                {item.unit && <span className="shrink-0 text-xs text-muted">{item.unit}</span>}
              </button>
            ))}
          </div>
          );
        })}
      </div>
    </div>
  );
}

/** Names to narrow a rule to (programs or displays), typed or picked from what the PC has. */
function Names({ label, hint, names, suggestions, onChange }: { label: string; hint: string; names: string[]; suggestions: string[]; onChange: (names: string[]) => void }) {
  const id = useId();
  const listId = useId();
  const [text, setText] = useState('');
  const add = () => {
    const name = text.trim();
    if (name && !names.some((n) => n.toLowerCase() === name.toLowerCase())) onChange([...names, name]);
    setText('');
  };
  return (
    <div className="flex flex-col gap-1">
      <label htmlFor={id} className="text-xs font-medium">
        {label}
      </label>
      <div className="flex flex-wrap items-center gap-1.5">
        {names.map((n) => (
          <span key={n} className="flex items-center gap-1 rounded-full border border-border bg-muted/10 px-2 py-0.5 text-xs">
            {n}
            <button type="button" aria-label={`Remove ${n}`} onClick={() => onChange(names.filter((x) => x !== n))} className="text-muted hover:text-danger">
              <X className="size-3" aria-hidden />
            </button>
          </span>
        ))}
        <input
          id={id}
          list={listId}
          value={text}
          onChange={(e) => setText(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              e.preventDefault();
              add();
            }
          }}
          placeholder="Type or pick, then Enter"
          className={`${INPUT} max-w-64`}
        />
        <Button type="button" size="sm" variant="ghost" onClick={add} disabled={!text.trim()}>
          Add
        </Button>
        <datalist id={listId}>
          {suggestions.map((s) => (
            <option key={s} value={s} />
          ))}
        </datalist>
      </div>
      <span className="text-xs text-muted">{hint}</span>
    </div>
  );
}

/** One Windows event a rule describes: its log, source, event IDs and levels. */
function EventRow({
  index,
  value,
  channels,
  providers,
  onChange,
  onRemove,
}: {
  index: number;
  value: CustomEventMatch;
  channels: string[];
  providers: string[];
  onChange: (change: Partial<CustomEventMatch>) => void;
  onRemove: () => void;
}) {
  const ids = { channel: useId(), provider: useId(), eventIds: useId(), channels: useId(), providers: useId() };
  const [idsText, setIdsText] = useState(value.eventIds.join(', '));
  const problem = eventProblem(value, index);
  return (
    <div className="flex flex-col gap-2 rounded-md border border-border p-2" aria-label={`Windows event ${index + 1}`} role="group">
      <div className="grid grid-cols-1 gap-2 sm:grid-cols-[1fr_1fr_10rem_auto]">
        <Field id={ids.channel} label="Log">
          <input id={ids.channel} list={ids.channels} value={value.channel} onChange={(e) => onChange({ channel: e.target.value })} placeholder="System" className={INPUT} />
        </Field>
        <Field id={ids.provider} label="Source">
          <input id={ids.provider} list={ids.providers} value={value.provider ?? ''} onChange={(e) => onChange({ provider: e.target.value })} placeholder="Any source" className={INPUT} />
        </Field>
        <Field id={ids.eventIds} label="Event IDs">
          <input
            id={ids.eventIds}
            value={idsText}
            onChange={(e) => {
              setIdsText(e.target.value);
              onChange({ eventIds: e.target.value.split(/[\s,;]+/).filter(Boolean).map(Number).filter((n) => Number.isInteger(n) && n >= 0 && n <= 65535) });
            }}
            placeholder="Any, or 41, 6008"
            className={INPUT}
          />
        </Field>
        <div className="flex items-end">
          <Button type="button" size="icon" variant="ghost" aria-label={`Remove Windows event ${index + 1}`} className="hover:text-danger" onClick={onRemove}>
            <X aria-hidden />
          </Button>
        </div>
      </div>
      <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-sm">
        <span className="text-xs font-medium">Levels</span>
        {LEVELS.map((l) => (
          <label key={l.level} className="flex items-center gap-1.5">
            <input
              type="checkbox"
              checked={value.levels.includes(l.level)}
              onChange={() => onChange({ levels: value.levels.includes(l.level) ? value.levels.filter((x) => x !== l.level) : [...value.levels, l.level].sort() })}
              className="size-4 accent-[var(--accent)]"
            />
            {l.label}
          </label>
        ))}
        <span className="text-xs text-muted">(none ticked: any level)</span>
      </div>
      {problem && <p className="text-xs text-danger">{problem}</p>}
      <datalist id={ids.channels}>
        {channels.map((c) => (
          <option key={c} value={c} />
        ))}
      </datalist>
      <datalist id={ids.providers}>
        {providers.map((p) => (
          <option key={p} value={p} />
        ))}
      </datalist>
    </div>
  );
}

function Field({ id, label, hint, wide, children }: { id: string; label: string; hint?: string; wide?: boolean; children: React.ReactNode }) {
  return (
    <div className={wide ? 'flex flex-col gap-1 sm:col-span-2' : 'flex flex-col gap-1'}>
      <label htmlFor={id} className="text-xs font-medium">
        {label}
      </label>
      {children}
      {hint && <span className="text-xs text-muted">{hint}</span>}
    </div>
  );
}

function Group({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <fieldset className="flex flex-col gap-2">
      <legend className="mb-1 text-xs font-semibold uppercase tracking-wide text-muted">{title}</legend>
      {children}
    </fieldset>
  );
}

function Checks({ items, chosen, onToggle }: { items: { id: string; label: string }[]; chosen: string[]; onToggle: (id: string) => void }) {
  return (
    <div className="flex flex-wrap gap-x-4 gap-y-1">
      {items.map((item) => (
        <label key={item.id} className="flex items-center gap-1.5 text-sm">
          <input type="checkbox" checked={chosen.includes(item.id)} onChange={() => onToggle(item.id)} className="size-4 accent-[var(--accent)]" />
          {item.label}
        </label>
      ))}
    </div>
  );
}

function SearchChecks({ items, chosen, onToggle }: { items: { id: string; label: string; hint?: string }[]; chosen: string[]; onToggle: (id: string) => void }) {
  const id = useId();
  const [query, setQuery] = useState('');
  const q = query.trim().toLowerCase();
  const shown = items.filter((i) => !q || i.label.toLowerCase().includes(q) || i.id.toLowerCase().includes(q));
  return (
    <div className="flex flex-col gap-1">
      <label htmlFor={id} className="sr-only">
        Search Windows events
      </label>
      <span className="relative">
        <Search className="pointer-events-none absolute left-2 top-2 size-4 text-muted" aria-hidden />
        <input id={id} type="search" value={query} onChange={(e) => setQuery(e.target.value)} placeholder={`Search ${items.length} Windows events`} className={`${INPUT} pl-8`} />
      </span>
      <div className="grid max-h-48 grid-cols-1 gap-1 overflow-auto rounded-md border border-border p-2 sm:grid-cols-2">
        {shown.length === 0 && <p className="text-sm text-muted">No event matches.</p>}
        {shown.map((item) => (
          <label key={item.id} className="flex items-center gap-1.5 text-sm" title={item.hint}>
            <input type="checkbox" checked={chosen.includes(item.id)} onChange={() => onToggle(item.id)} className="size-4 accent-[var(--accent)]" />
            {item.label}
          </label>
        ))}
      </div>
    </div>
  );
}
