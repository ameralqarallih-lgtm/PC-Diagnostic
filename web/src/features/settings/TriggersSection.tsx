import { useQuery } from '@tanstack/react-query';
import { ArrowDown, ArrowUp, ChevronRight, FolderCog, Pencil, Plus, RotateCcw, Trash2 } from 'lucide-react';
import { useEffect, useId, useState } from 'react';
import { DataTable, type Column } from '@/components/data/DataTable';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { useConfirm } from '@/components/ui/confirm';
import { apiGet } from '@/lib/api/client';
import type { CustomTriggerRule, Severity, TriggerCategory, TriggerRuleInfo, TriggerRuleSettings, TriggerSettings } from '@/lib/api/types';
import { cn } from '@/lib/utils';
import { categoryForMetric, CustomRuleEditor, describeRule, newRule, useTriggerChoices } from './CustomRuleEditor';

const SEVERITY_TONE: Record<Severity, 'neutral' | 'warning' | 'danger' | 'accent'> = {
  Info: 'neutral',
  Low: 'accent',
  Medium: 'warning',
  High: 'danger',
  Critical: 'danger',
};

type Field = keyof TriggerRuleSettings;

/** A row of the rules list: a shipped rule (with the user's changes) or one of the user's own. */
type Row = { type: 'shipped'; id: string; info: TriggerRuleInfo } | { type: 'custom'; id: string; rule: CustomTriggerRule };

/** The built-in categories (as the recorder has them), until the choices have loaded. */
const BUILT_IN: TriggerCategory[] = [
  { id: 'processor', name: 'Processor' },
  { id: 'memory', name: 'Memory' },
  { id: 'graphics', name: 'Graphics' },
  { id: 'storage', name: 'Storage' },
  { id: 'network', name: 'Network' },
  { id: 'sensors', name: 'Sensors and other readings' },
  { id: 'events', name: 'Events' },
];

export const DEFAULT_TRIGGERS: TriggerSettings = { automaticIncidents: true, maxIncidentsPerHour: 4, rules: {} };

/** Where a rule of the user's own is listed until moved: by what it watches (as the recorder decides). */
function defaultCategory(rule: CustomTriggerRule): string {
  return rule.kind === 'Event' ? 'events' : categoryForMetric(rule.metric);
}

/** A new category's ID from its name (lowercase letters, digits and dashes), not used by another. */
function categoryId(name: string, taken: string[]): string {
  const base = (name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '') || 'category').slice(0, 32);
  let id = base;
  for (let n = 2; taken.includes(id); n++) id = `${base}-${n}`;
  return id;
}

/**
 * The anomaly rules and automatic incident triggers, by category. Each rule shows what it watches in plain words and
 * why its default is what it is; only values that differ from the shipped ones are saved.
 */
export function TriggersSection({ value, onChange }: { value: TriggerSettings; onChange: (value: TriggerSettings) => void }) {
  const { data: rules, error } = useQuery({ queryKey: ['triggers'], queryFn: ({ signal }) => apiGet<TriggerRuleInfo[]>('/triggers', signal), staleTime: 60_000 });
  const { data: choices } = useTriggerChoices();
  const confirm = useConfirm();
  const maxId = useId();
  const [editing, setEditing] = useState<{ rule: CustomTriggerRule; isNew: boolean } | null>(null);
  const [managing, setManaging] = useState(false);
  // Categories start closed; a rule just added opens its own and is brought into view.
  const [open, setOpen] = useState<Set<string>>(() => new Set());
  const [focus, setFocus] = useState<string | null>(null);
  const toggle = (id: string) => setOpen((o) => {
    const next = new Set(o);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    return next;
  });
  useEffect(() => {
    if (!focus) return;
    const element = document.getElementById(`rule-${focus}`);
    if (!element) return;
    element.scrollIntoView?.({ block: 'center', behavior: 'smooth' });
    element.focus({ preventScroll: true });
    setFocus(null);
  });

  const custom = value.custom ?? [];
  const removed = value.removed ?? [];
  const moved = value.ruleCategories ?? {};
  const categories: TriggerCategory[] = value.categories?.length ? value.categories : (choices?.categories ?? BUILT_IN);
  const shipped = (rules ?? []).filter((r) => !r.custom);

  const setRule = (rule: TriggerRuleInfo, field: Field, next: number | boolean) => {
    const current: TriggerRuleSettings = { ...(value.rules[rule.id] ?? {}) };
    if (next === rule.defaults[field as keyof TriggerRuleInfo['defaults']]) delete current[field];
    else (current as Record<Field, number | boolean>)[field] = next;
    const nextRules = { ...value.rules };
    if (Object.values(current).every((v) => v == null)) delete nextRules[rule.id];
    else nextRules[rule.id] = current;
    onChange({ ...value, rules: nextRules });
  };
  const reset = (rule: TriggerRuleInfo) => {
    const nextRules = { ...value.rules };
    delete nextRules[rule.id];
    onChange({ ...value, rules: nextRules });
  };

  const categoryOf = (row: Row) => (row.type === 'shipped' ? (moved[row.id] ?? row.info.defaultCategory ?? 'events') : (row.rule.category ?? defaultCategory(row.rule)));
  const moveRule = (row: Row, category: string) => {
    if (row.type === 'custom') {
      onChange({ ...value, custom: custom.map((c) => (c.id === row.id ? { ...c, category } : c)) });
      return;
    }
    const next = { ...moved };
    if (category === row.info.defaultCategory) delete next[row.id];
    else next[row.id] = category;
    onChange({ ...value, ruleCategories: next });
  };

  const saveCustom = (rule: CustomTriggerRule) => {
    onChange({ ...value, custom: custom.some((c) => c.id === rule.id) ? custom.map((c) => (c.id === rule.id ? rule : c)) : [...custom, rule] });
    setEditing(null);
    // Shown where it is listed: its category opened, the rule scrolled to and focused.
    const category = rule.category ?? defaultCategory(rule);
    setOpen((o) => new Set(o).add(categories.some((c) => c.id === category) ? category : ''));
    setFocus(rule.id);
  };
  const deleteCustom = async (rule: CustomTriggerRule) => {
    if (await confirm({ title: `Delete ${rule.name}?`, description: 'The rule stops watching once the settings are saved. Its past anomalies and incidents stay.', action: 'Delete' }))
      onChange({ ...value, custom: custom.filter((c) => c.id !== rule.id) });
  };
  const deleteShipped = async (rule: TriggerRuleInfo) => {
    if (await confirm({ title: `Delete ${rule.name}?`, description: 'It comes with PC Diagnostic: it can be brought back from Deleted rules below.', action: 'Delete' }))
      onChange({ ...value, removed: [...removed, rule.id] });
  };

  // Categories: the list is materialized on the first change, so the built-in ones can be renamed, reordered and deleted too.
  const setCategories = (next: TriggerCategory[]) => onChange({ ...value, categories: next });
  const addCategory = () => setCategories([...categories, { id: categoryId('New category', categories.map((c) => c.id)), name: 'New category' }]);
  const deleteCategory = async (category: TriggerCategory, count: number) => {
    if (
      await confirm({
        title: `Delete the category ${category.name}?`,
        description: count ? `Its ${count} rule${count > 1 ? 's keep' : ' keeps'} working and ${count > 1 ? 'are' : 'is'} listed under Uncategorized until moved.` : 'It has no rules.',
        action: 'Delete',
      })
    )
      setCategories(categories.filter((c) => c.id !== category.id));
  };

  const rows: Row[] = [
    ...shipped.filter((r) => !removed.includes(r.id)).map((info) => ({ type: 'shipped' as const, id: info.id, info })),
    ...custom.map((rule) => ({ type: 'custom' as const, id: rule.id, rule })),
  ];
  const known = new Set(categories.map((c) => c.id));
  const sections = [
    ...categories.map((c) => ({ id: c.id, name: c.name, rows: rows.filter((r) => categoryOf(r) === c.id) })),
    { id: '', name: 'Uncategorized', rows: rows.filter((r) => !known.has(categoryOf(r))) },
  ].filter((s) => s.id !== '' || s.rows.length > 0);
  const deleted = shipped.filter((r) => removed.includes(r.id));

  return (
    <Card id="triggers" className="scroll-mt-28 lg:col-span-2">
      <CardHeader>
        <CardTitle>Anomaly rules and automatic incidents</CardTitle>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        <p className="text-sm text-muted">
          These rules watch the data and record an <strong className="font-medium text-foreground">anomaly</strong> with its reasoning (the normal range, the value, how
          long). Rules marked as triggers also capture an incident. A trigger never claims a cause; the incident's summary lists what happened around it.
        </p>
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          <label className="flex items-start gap-2.5 text-sm">
            <input
              type="checkbox"
              checked={value.automaticIncidents}
              onChange={(e) => onChange({ ...value, automaticIncidents: e.target.checked })}
              className="mt-0.5 size-4 accent-[var(--accent)]"
            />
            <span>
              <span className="font-medium">Create incidents automatically</span>
              <span className="block text-xs text-muted">When off, rules still record anomalies but never capture incidents.</span>
            </span>
          </label>
          <div className="flex flex-col gap-1">
            <label htmlFor={maxId} className="text-sm font-medium">
              Automatic incidents per hour, at most
            </label>
            <input
              id={maxId}
              type="number"
              min={1}
              max={60}
              value={value.maxIncidentsPerHour}
              onChange={(e) => onChange({ ...value, maxIncidentsPerHour: e.target.value === '' ? 0 : Number(e.target.value) })}
              aria-invalid={value.maxIncidentsPerHour < 1 || value.maxIncidentsPerHour > 60 || undefined}
              className={`${INPUT} max-w-28 aria-[invalid=true]:border-danger`}
            />
            <p className="text-xs text-muted">1–60. A trigger during an incident that is still recording joins it instead.</p>
          </div>
        </div>

        <div className="flex flex-wrap items-center gap-2">
          <Button type="button" size="sm" variant="outline" onClick={() => setEditing({ rule: newRule(custom.map((c) => c.id)), isNew: true })}>
            <Plus aria-hidden /> Add a rule
          </Button>
          <Button type="button" size="sm" variant={managing ? 'default' : 'ghost'} aria-pressed={managing} onClick={() => setManaging((m) => !m)}>
            <FolderCog aria-hidden /> Categories
          </Button>
        </div>

        {managing && (
          <section aria-label="Categories" className="flex flex-col gap-2 rounded-md border border-border p-3">
            <p className="text-xs text-muted">Rename, reorder, add or delete the categories rules are listed under. Each rule can be moved with its Category list.</p>
            <ul className="flex flex-col gap-1.5">
              {categories.map((c, i) => {
                const count = rows.filter((r) => categoryOf(r) === c.id).length;
                return (
                  <li key={c.id} className="flex items-center gap-2">
                    <input
                      aria-label={`Name of ${c.name}`}
                      value={c.name}
                      maxLength={40}
                      onChange={(e) => setCategories(categories.map((x) => (x.id === c.id ? { ...x, name: e.target.value } : x)))}
                      aria-invalid={!c.name.trim() || undefined}
                      className={`${INPUT} max-w-72 aria-[invalid=true]:border-danger`}
                    />
                    <span className="w-16 text-xs text-muted">
                      {count} rule{count === 1 ? '' : 's'}
                    </span>
                    <Button type="button" size="icon" variant="ghost" aria-label={`Move ${c.name} up`} disabled={i === 0} onClick={() => setCategories(swap(categories, i, i - 1))}>
                      <ArrowUp aria-hidden />
                    </Button>
                    <Button type="button" size="icon" variant="ghost" aria-label={`Move ${c.name} down`} disabled={i === categories.length - 1} onClick={() => setCategories(swap(categories, i, i + 1))}>
                      <ArrowDown aria-hidden />
                    </Button>
                    <Button type="button" size="icon" variant="ghost" aria-label={`Delete the category ${c.name}`} className="hover:text-danger" onClick={() => void deleteCategory(c, count)}>
                      <Trash2 aria-hidden />
                    </Button>
                  </li>
                );
              })}
            </ul>
            <div className="flex flex-wrap gap-2">
              <Button type="button" size="sm" variant="outline" onClick={addCategory}>
                <Plus aria-hidden /> Add a category
              </Button>
              {value.categories?.length ? (
                <Button type="button" size="sm" variant="ghost" onClick={() => onChange({ ...value, categories: [] })}>
                  <RotateCcw aria-hidden /> Back to the built-in categories
                </Button>
              ) : null}
            </div>
          </section>
        )}

        {error && <p className="text-sm text-danger">The rules could not be loaded.</p>}
        {!rules && !error && <p className="text-sm text-muted">Loading the rules…</p>}
        {rules &&
          sections.map((section) => {
            const expanded = open.has(section.id);
            const at = categories.findIndex((c) => c.id === section.id);
            return (
            <section key={section.id || 'none'} aria-label={section.name} className="flex flex-col gap-2 rounded-md border border-border">
              <header className="flex items-center gap-1 pr-1">
                <h3 className="min-w-0 flex-1">
                  <button
                    type="button"
                    aria-expanded={expanded}
                    onClick={() => toggle(section.id)}
                    className="flex w-full items-center gap-2 px-3 py-2 text-left text-xs font-semibold uppercase tracking-wide text-muted hover:text-foreground"
                  >
                    <ChevronRight className={cn('size-4 shrink-0 transition-transform', expanded && 'rotate-90')} aria-hidden />
                    <span className="truncate">{section.name}</span>
                    <span className="font-normal normal-case">· {section.rows.length}</span>
                  </button>
                </h3>
                {at >= 0 && (
                  <>
                    <Button type="button" size="icon" variant="ghost" aria-label={`Move ${section.name} up`} disabled={at === 0} onClick={() => setCategories(swap(categories, at, at - 1))}>
                      <ArrowUp aria-hidden />
                    </Button>
                    <Button type="button" size="icon" variant="ghost" aria-label={`Move ${section.name} down`} disabled={at === categories.length - 1} onClick={() => setCategories(swap(categories, at, at + 1))}>
                      <ArrowDown aria-hidden />
                    </Button>
                  </>
                )}
              </header>
              {!expanded ? null : section.rows.length === 0 ? (
                <p className="px-3 pb-3 text-sm text-muted">No rules here.</p>
              ) : (
                <div className="px-3 pb-3">
                <DataTable
                  viewKey="settings.rules"
                  label={section.name}
                  rows={section.rows}
                  columns={ruleColumns(value)}
                  rowKey={(row) => row.id}
                  defaultView={{ groupBy: [], sort: [] }}
                  listClassName="divide-y divide-border rounded-md border border-border"
                  compact
                  renderRow={(row) => {
                    const mover = <CategorySelect label={row.type === 'shipped' ? row.info.name : row.rule.name} categories={categories} value={categoryOf(row)} onChange={(c) => moveRule(row, c)} />;
                    return (
                      <div id={`rule-${row.id}`} tabIndex={-1} className="outline-none focus-visible:ring-2 focus-visible:ring-[var(--accent)] focus:bg-accent/5">
                        {row.type === 'shipped' ? (
                      <RuleRow
                        rule={row.info}
                        change={value.rules[row.id]}
                        automatic={value.automaticIncidents}
                        mover={mover}
                        onChange={(field, next) => setRule(row.info, field, next)}
                        onReset={() => reset(row.info)}
                        onDelete={() => void deleteShipped(row.info)}
                      />
                    ) : (
                      <CustomRow
                        rule={row.rule}
                        description={describeRule(row.rule, choices)}
                        mover={mover}
                        onToggle={(enabled) => onChange({ ...value, custom: custom.map((c) => (c.id === row.id ? { ...c, enabled } : c)) })}
                        onEdit={() => setEditing({ rule: row.rule, isNew: false })}
                        onDelete={() => void deleteCustom(row.rule)}
                      />
                        )}
                      </div>
                    );
                  }}
                />
                </div>
              )}
            </section>
            );
          })}
        {deleted.length > 0 && (
          <section aria-label="Deleted rules" className="flex flex-col gap-2">
            <h3 className="text-xs font-semibold uppercase tracking-wide text-muted">Deleted rules</h3>
            <ul className="divide-y divide-border rounded-md border border-border">
              {deleted.map((rule) => (
                <li key={rule.id} className="flex items-center justify-between gap-2 p-2 text-sm">
                  <span className="text-muted">{rule.name}</span>
                  <Button type="button" size="sm" variant="ghost" onClick={() => onChange({ ...value, removed: removed.filter((id) => id !== rule.id) })}>
                    <RotateCcw aria-hidden /> Bring back
                  </Button>
                </li>
              ))}
            </ul>
          </section>
        )}
        {editing && <CustomRuleEditor rule={editing.rule} isNew={editing.isNew} categories={categories} onSave={saveCustom} onClose={() => setEditing(null)} />}
      </CardContent>
    </Card>
  );
}

function swap<T>(list: T[], a: number, b: number): T[] {
  const next = [...list];
  [next[a], next[b]] = [next[b]!, next[a]!];
  return next;
}

const INPUT = 'h-8 w-full rounded-md border border-border bg-background px-2 text-sm';

const SEVERITY_RANK: Record<Severity, number> = { Info: 0, Low: 1, Medium: 2, High: 3, Critical: 4 };

/** What the rules can be grouped and sorted by (with the user's changes applied). */
function ruleColumns(value: TriggerSettings): Column<Row>[] {
  const get = (row: Row, field: 'enabled' | 'createIncident') => (row.type === 'custom' ? row.rule[field] : (value.rules[row.id]?.[field] ?? row.info.defaults[field]));
  const severity = (row: Row) => (row.type === 'custom' ? row.rule.severity : row.info.severity);
  return [
    { id: 'name', header: 'Rule', value: (r) => (r.type === 'custom' ? r.rule.name : r.info.name), groupable: false },
    { id: 'severity', header: 'Severity', value: (r) => SEVERITY_RANK[severity(r)], group: (r) => severity(r), firstDesc: true },
    { id: 'on', header: 'On or off', value: (r) => (get(r, 'enabled') ? 'On' : 'Off') },
    { id: 'incident', header: 'Creates an incident', value: (r) => (get(r, 'enabled') && get(r, 'createIncident') ? 'Creates incidents' : 'Anomaly only') },
    { id: 'origin', header: 'Origin', value: (r) => (r.type === 'custom' ? 'Yours' : 'Shipped') },
  ];
}

function CategorySelect({ label, categories, value, onChange }: { label: string; categories: TriggerCategory[]; value: string; onChange: (category: string) => void }) {
  return (
    <select aria-label={`Category of ${label}`} value={categories.some((c) => c.id === value) ? value : ''} onChange={(e) => onChange(e.target.value)} className="h-7 rounded-md border border-border bg-background px-1.5 text-xs">
      {!categories.some((c) => c.id === value) && <option value="">Uncategorized</option>}
      {categories.map((c) => (
        <option key={c.id} value={c.id}>
          {c.name}
        </option>
      ))}
    </select>
  );
}

function CustomRow({
  rule,
  description,
  mover,
  onToggle,
  onEdit,
  onDelete,
}: {
  rule: CustomTriggerRule;
  description: string;
  mover: React.ReactNode;
  onToggle: (enabled: boolean) => void;
  onEdit: () => void;
  onDelete: () => void;
}) {
  return (
    <div className={cn('flex flex-wrap items-start justify-between gap-2 p-3', !rule.enabled && 'opacity-60')}>
      <div className="min-w-0 flex-1">
        <p className="flex flex-wrap items-center gap-2 text-sm font-medium">
          {rule.name}
          <Badge tone={SEVERITY_TONE[rule.severity]}>{rule.severity}</Badge>
          <Badge tone="accent">Yours</Badge>
          {rule.createIncident && <span className="text-xs font-normal text-muted">creates incidents</span>}
        </p>
        <p className="text-xs text-muted">{description}</p>
      </div>
      <div className="flex flex-wrap items-center gap-2 text-sm">
        {mover}
        <label className="flex items-center gap-1.5">
          <input type="checkbox" checked={rule.enabled} onChange={(e) => onToggle(e.target.checked)} className="size-4 accent-[var(--accent)]" aria-label={`${rule.name}: on`} />
          On
        </label>
        <Button type="button" size="icon" variant="ghost" aria-label={`Edit ${rule.name}`} onClick={onEdit}>
          <Pencil aria-hidden />
        </Button>
        <Button type="button" size="icon" variant="ghost" aria-label={`Delete ${rule.name}`} className="hover:text-danger" onClick={onDelete}>
          <Trash2 aria-hidden />
        </Button>
      </div>
    </div>
  );
}

function RuleRow({
  rule,
  change,
  automatic,
  mover,
  onChange,
  onReset,
  onDelete,
}: {
  rule: TriggerRuleInfo;
  change: TriggerRuleSettings | undefined;
  automatic: boolean;
  mover: React.ReactNode;
  onChange: (field: Field, value: number | boolean) => void;
  onReset: () => void;
  onDelete: () => void;
}) {
  const get = <K extends keyof TriggerRuleInfo['defaults']>(field: K): TriggerRuleInfo['defaults'][K] => {
    const changed = change?.[field as Field];
    return changed == null ? rule.defaults[field] : (changed as TriggerRuleInfo['defaults'][K]);
  };
  const enabled = get('enabled');
  const changed = !!change && Object.values(change).some((v) => v != null);

  return (
    <div className={cn('flex flex-col gap-2 p-3', !enabled && 'opacity-60')}>
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div className="min-w-0 flex-1">
          <p className="flex flex-wrap items-center gap-2 text-sm font-medium">
            {rule.name}
            <Badge tone={SEVERITY_TONE[rule.severity]}>{rule.severity}</Badge>
            {rule.perDevice && <span className="text-xs font-normal text-muted">per device</span>}
          </p>
          <p className="text-xs text-muted">{rule.description}</p>
          {rule.note && <p className="mt-0.5 text-xs text-muted italic">{rule.note}</p>}
        </div>
        <div className="flex flex-wrap items-center gap-3 text-sm">
          {mover}
          <label className="flex items-center gap-1.5">
            <input type="checkbox" checked={enabled} onChange={(e) => onChange('enabled', e.target.checked)} className="size-4 accent-[var(--accent)]" aria-label={`${rule.name}: on`} />
            On
          </label>
          <label className={cn('flex items-center gap-1.5', (!enabled || !automatic) && 'text-muted')}>
            <input
              type="checkbox"
              checked={get('createIncident')}
              disabled={!enabled}
              onChange={(e) => onChange('createIncident', e.target.checked)}
              className="size-4 accent-[var(--accent)]"
              aria-label={`${rule.name}: creates an incident`}
            />
            Creates an incident
          </label>
          {changed && (
            <button type="button" onClick={onReset} className="flex items-center gap-1 text-xs text-accent hover:underline" title="Back to the shipped values">
              <RotateCcw className="size-3" aria-hidden /> Default
            </button>
          )}
          <Button type="button" size="icon" variant="ghost" aria-label={`Delete ${rule.name}`} title="Delete this rule (it can be brought back)" className="hover:text-danger" onClick={onDelete}>
            <Trash2 aria-hidden />
          </Button>
        </div>
      </div>
      {enabled && (
        <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
          {rule.usesThreshold && (
            <RuleNumber
              label={rule.kind === 'BaselineDeviation' ? 'Deviation' : rule.kind === 'RateOfChange' ? 'Change' : rule.kind === 'CounterSpike' ? 'Count' : 'Threshold'}
              unit={rule.unit}
              min={rule.thresholdMin}
              max={rule.thresholdMax}
              step={rule.unit === 'GB' || rule.unit === 'σ' ? 0.1 : 1}
              value={get('threshold')}
              onChange={(v) => onChange('threshold', v)}
            />
          )}
          {rule.usesDuration && <RuleNumber label="For at least" unit="s" min={1} max={3600} value={get('durationSeconds')} onChange={(v) => onChange('durationSeconds', v)} />}
          {rule.usesWindow && <RuleNumber label="Within" unit="s" min={1} max={600} value={get('windowSeconds')} onChange={(v) => onChange('windowSeconds', v)} />}
          <RuleNumber label="Cooldown" unit="min" min={0} max={1440} value={get('cooldownMinutes')} onChange={(v) => onChange('cooldownMinutes', v)} />
        </div>
      )}
    </div>
  );
}

function RuleNumber({ label, unit, min, max, step = 1, value, onChange }: { label: string; unit: string; min: number; max: number; step?: number; value: number; onChange: (v: number) => void }) {
  const id = useId();
  return (
    <div className="flex flex-col gap-0.5">
      <label htmlFor={id} className="text-xs text-muted">
        {label}
      </label>
      <span className="flex items-center gap-1.5">
        <input
          id={id}
          type="number"
          min={min}
          max={max}
          step={step}
          value={value}
          onChange={(e) => onChange(e.target.value === '' ? 0 : Number(e.target.value))}
          aria-invalid={value < min || value > max || undefined}
          title={`${min}–${max}${unit ? ` ${unit}` : ''}`}
          className={`${INPUT} aria-[invalid=true]:border-danger`}
        />
        {unit && <span className="shrink-0 text-xs text-muted">{unit}</span>}
      </span>
    </div>
  );
}
