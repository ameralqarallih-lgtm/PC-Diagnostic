import { LayoutDashboard } from 'lucide-react';
import { useCallback, useMemo, useRef, useState } from 'react';
import { Button } from '@/components/ui/button';
import { useConfirm } from '@/components/ui/confirm';
import { useToast } from '@/components/ui/toast';
import { useIncidents } from '@/features/incidents/useIncidents';
import type { LiveSeries, SeriesKey } from '@/features/live/series';
import { useLiveFeed } from '@/features/live/useLiveFeed';
import { useRecorderActions } from '@/features/recorder/useRecorderActions';
import { useStatus } from '@/features/status/useStatus';
import type { AgentStatus, LiveMessage } from '@/lib/api/types';
import { useNow } from '@/lib/useNow';
import { addWidget, duplicateWidget, moveWidget, newWidget, newWidgetId, removeWidget, reorderFromLayout, resizeWidget, spanFromColumns } from './config/layout';
import { PRESETS } from './config/presets';
import type { Breakpoint, DashboardConfig, WidgetInstance, WidgetType } from './config/types';
import { WIDGETS } from './config/widgets';
import { useDashboardConfig } from './config/useDashboardConfig';
import { CustomizeBar } from './CustomizeBar';
import { CustomWidgetBuilder, type BuilderResult } from './custom/CustomWidgetBuilder';
import { defaultCustom, DESIGN_INFO, readCustom, writeCustom, type CustomDesign } from './custom/model';
import { DashboardDataProvider, type DashboardData } from './DashboardContext';
import { DashboardGrid } from './DashboardGrid';
import { LiveToolbar, type FeedMode } from './LiveToolbar';
import { AddWidgetDialog, WidgetSettingsDialog } from './WidgetDialogs';

/** Values older than this are not live (the recorder samples once a second). */
const STALE_MS = 5000;

/** The widget builder: a new widget, a widget on the dashboard, or one in My widgets. */
interface Builder {
  mode: 'add' | 'edit' | 'library';
  targetId: string | null;
  initial: { title: string | null; settings: WidgetInstance['settings'] };
}

interface Frozen {
  at: number;
  message: LiveMessage | null;
  series: LiveSeries;
}

/**
 * The live dashboard: a grid of widgets the user arranges, sizes and configures (Customize). One live feed serves
 * every widget; freezing keeps the current picture on screen while data keeps arriving in the background.
 */
export function DashboardPage() {
  const feed = useLiveFeed();
  const { data: statusResponse } = useStatus();
  const { command, capture } = useRecorderActions();
  const { data: incidents } = useIncidents(5, 0, 15_000);
  const { config, save } = useDashboardConfig();
  const toast = useToast();
  const now = useNow(true);
  const [frozen, setFrozen] = useState<Frozen | null>(null);
  const [focus, setFocusState] = useState<DashboardData['focus']>({ gauge: null, series: null });
  const [draft, setDraft] = useState<DashboardConfig | null>(null);
  const [adding, setAdding] = useState(false);
  const [editingWidget, setEditingWidget] = useState<string | null>(null);
  const [builder, setBuilder] = useState<Builder | null>(null);

  const shown = frozen ?? { message: feed.message, series: feed.series };
  // Controls always use the current status, even while the view is frozen.
  const connected = feed.message?.connected ?? statusResponse?.agent.connected ?? false;
  const status = feed.message?.status ?? statusResponse?.agent.status ?? null;
  const detail = feed.message?.detail ?? statusResponse?.agent.detail ?? null;
  const lastSample = feed.message?.live?.timestamp ? Date.parse(feed.message.live.timestamp) : null;
  const fresh = lastSample != null && now - lastSample < STALE_MS;
  const mode: FeedMode = !connected ? 'offline' : !fresh ? 'stale' : status?.state === 'Recording' ? 'recording' : status?.liveView ? 'liveView' : 'stale';
  const ageSeconds = feed.receivedAt != null ? Math.max(0, Math.round((now - feed.receivedAt) / 1000)) : null;

  const setFocus = useCallback((gauge: string | null, series: SeriesKey | null) => setFocusState({ gauge, series }), []);
  const doCapture = capture.mutate;
  const doCommand = command.mutate;

  const data = useMemo<DashboardData>(
    () => ({
      live: shown.message?.live ?? null,
      series: shown.series,
      status,
      connected,
      detail,
      incidents: incidents?.items ?? [],
      activeIncident:
        status?.activeIncident && status.activeIncidentUntil
          ? {
              key: status.activeIncident,
              from: incidents?.items.find((i) => i.key === status.activeIncident)?.centerAt ?? status.activeIncidentUntil,
              until: status.activeIncidentUntil,
            }
          : null,
      focus,
      setFocus,
      capture: (type, note) => doCapture({ type, note }),
      capturing: capture.isPending,
      command: (c) => doCommand(c),
      commandBusy: command.isPending,
    }),
    [shown.message, shown.series, status, connected, detail, incidents, focus, setFocus, doCapture, doCommand, capture.isPending, command.isPending],
  );

  const editing = draft != null;
  const current = draft ?? config;
  const draftRef = useRef(draft);
  draftRef.current = draft;

  const update = useCallback((change: (config: DashboardConfig) => DashboardConfig) => setDraft((d) => (d ? change(d) : d)), []);
  const confirm = useConfirm();
  // The grid reports the screen size; nothing depends on it here yet (layouts follow the space by themselves).
  const onBreakpointChange = useCallback((_bp: Breakpoint) => {}, []);
  const onArrange = useCallback((arranged: readonly { i: string; x: number; y: number }[]) => update((d) => reorderFromLayout(d, arranged)), [update]);
  const onResize = useCallback(
    (id: string, w: number, h: number, columns: number) => update((d) => resizeWidget(d, id, columns === 1 ? (d.layouts.lg?.find((e) => e.i === id)?.w ?? 12) : spanFromColumns(w, columns), h)),
    [update],
  );
  const onSpan = useCallback((id: string, span: number) => update((d) => resizeWidget(d, id, span, null)), [update]);
  const onRemoveWidget = useCallback(
    async (id: string) => (await confirm({ title: 'Remove this widget?', description: 'It is taken off the dashboard (save the dashboard to keep the change).', action: 'Remove' })) && update((d) => removeWidget(d, id)),
    [update, confirm],
  );
  const onDuplicateWidget = useCallback((id: string) => update((d) => duplicateWidget(d, id)), [update]);
  const onMoveWidget = useCallback((id: string, direction: -1 | 1) => update((d) => moveWidget(d, id, direction)), [update]);

  /** Adds a custom widget at the bottom, at its design's starting size. */
  const addCustom = (d: DashboardConfig, title: string | null, settings: WidgetInstance['settings']): DashboardConfig => {
    const widget: WidgetInstance = { id: newWidgetId('custom', d.widgets.map((w) => w.id)), type: 'custom', title, settings };
    const size = DESIGN_INFO[readCustom(settings).design].size;
    return resizeWidget(addWidget(d, widget), widget.id, size.w, size.h);
  };

  const keep = (d: DashboardConfig, title: string | null, settings: WidgetInstance['settings']): DashboardConfig => {
    const library = d.library ?? [];
    return { ...d, library: [...library, { id: newWidgetId('custom', library.map((w) => w.id)), type: 'custom', title, settings }] };
  };

  const saveBuilt = (result: BuilderResult) => {
    if (!builder) return;
    const { title, settings, saveToLibrary } = result;
    if (builder.mode === 'add') {
      update((d) => (saveToLibrary ? keep(addCustom(d, title, settings), title, settings) : addCustom(d, title, settings)));
      toast.show({ title: `${title ?? DESIGN_INFO[readCustom(settings).design].name} added`, description: saveToLibrary ? 'It is at the bottom of the dashboard, and in My widgets.' : 'It is at the bottom of the dashboard.', tone: 'success' });
    } else if (builder.mode === 'edit') {
      update((d) => {
        const changed = { ...d, widgets: d.widgets.map((w) => (w.id === builder.targetId ? { ...w, title, settings } : w)) };
        return saveToLibrary ? keep(changed, title, settings) : changed;
      });
    } else {
      update((d) => ({ ...d, library: (d.library ?? []).map((w) => (w.id === builder.targetId ? { ...w, title, settings } : w)) }));
    }
  };

  const buildDesign = (design: CustomDesign) => {
    setAdding(false);
    setBuilder({ mode: 'add', targetId: null, initial: { title: null, settings: writeCustom(defaultCustom(design), '') } });
  };

  const addFromLibrary = (id: string) => {
    const item = current?.library?.find((w) => w.id === id);
    if (!item) return;
    update((d) => addCustom(d, item.title ?? null, structuredClone(item.settings)));
    setAdding(false);
    toast.show({ title: `${item.title ?? 'Custom widget'} added`, description: 'It is at the bottom of the dashboard.', tone: 'success' });
  };

  const editLibrary = (id: string) => {
    const item = current?.library?.find((w) => w.id === id);
    if (!item) return;
    setAdding(false);
    setBuilder({ mode: 'library', targetId: id, initial: { title: item.title ?? null, settings: item.settings } });
  };

  const removeFromLibrary = async (id: string) =>
    (await confirm({ title: 'Delete this widget from My widgets?', description: 'Widgets on the dashboard made from it stay.' })) && update((d) => ({ ...d, library: (d.library ?? []).filter((w) => w.id !== id) }));

  /** A built widget opens the builder; the others their settings. */
  const editWidget = useCallback(
    (id: string) => {
      const widget = draftRef.current?.widgets.find((w) => w.id === id);
      if (widget?.type === 'custom') setBuilder({ mode: 'edit', targetId: id, initial: { title: widget.title ?? null, settings: widget.settings } });
      else setEditingWidget(id);
    },
    [],
  );

  const addType = (type: WidgetType) => {
    update((d) => addWidget(d, newWidget(type, d.widgets.map((w) => w.id))));
    setAdding(false);
    toast.show({ title: `${WIDGETS[type].name} added`, description: 'It is at the bottom of the dashboard.', tone: 'success' });
  };

  const applyWidget = (widget: WidgetInstance, size: { w: number; h: number } | null) =>
    update((d) => {
      const replaced = { ...d, widgets: d.widgets.map((w) => (w.id === widget.id ? widget : w)) };
      return size ? resizeWidget(replaced, widget.id, size.w, size.h) : replaced;
    });

  const finish = () => {
    if (!draft) return;
    save.mutate(draft, {
      onSuccess: () => {
        setDraft(null);
        toast.show({ title: 'Dashboard saved', tone: 'success' });
      },
      onError: (error) => toast.show({ title: 'The dashboard could not be saved', description: error instanceof Error ? error.message : undefined, tone: 'error' }),
    });
  };

  const editedWidget = current?.widgets.find((w) => w.id === editingWidget) ?? null;
  const editedEntry = current?.layouts.lg?.find((e) => e.i === editingWidget) ?? null;

  return (
    <DashboardDataProvider value={data}>
      <div className="flex flex-col gap-4">
        <h1 className="sr-only">Dashboard</h1>

        <div className="flex flex-col gap-2 sm:flex-row sm:items-stretch">
          <div className="min-w-0 flex-1">
            <LiveToolbar
              connection={feed.connection}
              mode={mode}
              ageSeconds={ageSeconds}
              frozenAt={frozen?.at ?? null}
              onToggleFreeze={() => setFrozen(frozen ? null : { at: Date.now(), message: feed.message, series: feed.series })}
            />
          </div>
          {!editing && (
            <Button variant="outline" className="h-auto" disabled={!config} onClick={() => config && setDraft(structuredClone(config))}>
              <LayoutDashboard aria-hidden /> Customize
            </Button>
          )}
        </div>

        {editing && (
          <CustomizeBar
            saving={save.isPending}
            onAdd={() => setAdding(true)}
            onPreset={(id) => {
              const preset = PRESETS.find((p) => p.id === id);
              // A preset replaces the layout; the widgets kept in My widgets stay.
              if (preset) setDraft((d) => ({ ...preset.config(), ...(d?.library?.length ? { library: d.library } : {}) }));
            }}
            onCancel={() => setDraft(null)}
            onDone={finish}
          />
        )}

        <FeedNotice connection={feed.connection} connected={connected} status={status} />

        {current ? (
          current.widgets.length === 0 ? (
            <div className="rounded-lg border border-dashed border-border p-10 text-center text-sm text-muted">
              The dashboard is empty. {editing ? 'Use Add widget to put something on it.' : 'Press Customize to add widgets.'}
            </div>
          ) : (
            <DashboardGrid
              config={current}
              editing={editing}
              onBreakpointChange={onBreakpointChange}
              onArrange={onArrange}
              onResize={onResize}
              onSpan={onSpan}
              onEditWidget={editWidget}
              onRemoveWidget={onRemoveWidget}
              onDuplicateWidget={onDuplicateWidget}
              onMoveWidget={onMoveWidget}
            />
          )
        ) : (
          <p className="text-sm text-muted">Loading the dashboard…</p>
        )}

        {statusResponse && (
          <p className="text-xs text-muted">
            {statusResponse.product} {statusResponse.version} · data stored locally in <span className="font-mono">{statusResponse.dataDirectory}</span>
          </p>
        )}
      </div>

      <AddWidgetDialog
        open={adding}
        onClose={() => setAdding(false)}
        onAdd={addType}
        onBuild={buildDesign}
        library={current?.library ?? []}
        onAddFromLibrary={addFromLibrary}
        onEditLibrary={editLibrary}
        onRemoveFromLibrary={removeFromLibrary}
      />
      <WidgetSettingsDialog widget={editedWidget} entry={editedEntry} onClose={() => setEditingWidget(null)} onApply={applyWidget} />
      <CustomWidgetBuilder open={builder != null} mode={builder?.mode ?? 'add'} initial={builder?.initial ?? null} onClose={() => setBuilder(null)} onSave={saveBuilt} />
    </DashboardDataProvider>
  );
}

function FeedNotice({ connection, connected, status }: { connection: string; connected: boolean; status: AgentStatus | null }) {
  let text: string | null = null;
  if (connection === 'unauthorized') {
    text = 'Your dashboard session ended. Open the dashboard again from the PC Diagnostic tray icon.';
  } else if (connection === 'open' && !connected) {
    text = 'The recorder Agent is not running, so there are no live values. Start PC Diagnostic to see them.';
  } else if (status?.state === 'Paused') {
    text = 'Recording is paused, so live values are paused too. Resume to continue.';
  } else if (status?.watching && status.state !== 'Recording') {
    text = 'Not recording, but watching for problems: one that creates incidents starts a recording, and its incident keeps the minutes before it. Press Record everything to keep everything.';
  } else if (status?.liveView && status.state !== 'Recording') {
    text = 'Not recording: these live values are for this dashboard only and are not saved. Press Record everything (or Custom recording) to keep evidence.';
  }

  return text ? (
    <p role="status" className="rounded-md border border-warning/40 bg-warning/10 px-3 py-2 text-sm">
      {text}
    </p>
  ) : null;
}
