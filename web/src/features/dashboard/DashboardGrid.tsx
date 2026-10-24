import { ArrowDown, ArrowUp, Copy, GripVertical, Settings2, X } from 'lucide-react';
import { memo, useEffect, useMemo, useRef } from 'react';
import ReactGridLayout, { useContainerWidth, type Layout, type LayoutItem } from 'react-grid-layout';
import 'react-grid-layout/css/styles.css';
import 'react-resizable/css/styles.css';
import { cn } from '@/lib/utils';
import { GRID_MARGIN, GRID_ROW_HEIGHT, layoutsFor, QUICK_SPANS } from './config/layout';
import { breakpointFor, COLUMNS, type Breakpoint, type DashboardConfig, type WidgetInstance } from './config/types';
import { WIDGETS } from './config/widgets';
import { accentStyle } from './custom/CustomWidget';
import { WidgetBody } from './WidgetBody';

const MARGIN: readonly [number, number] = [GRID_MARGIN, GRID_MARGIN];
const PADDING: readonly [number, number] = [0, 0];

export interface DashboardGridProps {
  config: DashboardConfig;
  editing: boolean;
  onBreakpointChange: (breakpoint: Breakpoint) => void;
  /** A drag finished: the grid in its new reading order. */
  onArrange: (arranged: Layout) => void;
  /** A resize finished: the widget's new width in columns of the current grid and its height in rows. */
  onResize: (id: string, w: number, h: number, columns: number) => void;
  onSpan: (id: string, span: number) => void;
  onEditWidget: (id: string) => void;
  onRemoveWidget: (id: string) => void;
  onDuplicateWidget: (id: string) => void;
  onMoveWidget: (id: string, direction: -1 | 1) => void;
}

/**
 * The widget grid. Positions are computed for the space available (12 columns on wide screens, 8, 4, then one
 * column on phones), so widgets wrap to new rows when they would be too narrow. In customize mode widgets are
 * dragged by their bar to change the order and resized from the corner; quick widths are on the bar.
 */
export function DashboardGrid(props: DashboardGridProps) {
  const { config, editing, onBreakpointChange, onArrange, onResize } = props;
  // Measured before the first layout: laid out at a guessed width first, every widget slid to its place afterwards.
  const { width, containerRef, mounted } = useContainerWidth({ measureBeforeMount: true });
  const breakpoint = breakpointFor(width);
  const handlers = useRef(props);
  handlers.current = props;

  useEffect(() => {
    if (mounted) onBreakpointChange(breakpoint);
  }, [mounted, breakpoint, onBreakpointChange]);

  const columns = COLUMNS[breakpoint];

  // One layout, computed for the current width (the grid's own responsive mode keeps per-size state that fights
  // a layout that follows the available space).
  const layout = useMemo<Layout>(() => {
    const types = new Map(config.widgets.map((w) => [w.id, w.type]));
    return layoutsFor(config, width)[breakpoint].map((e) => {
      const size = WIDGETS[types.get(e.i) ?? 'displays'].size;
      return { ...e, minW: Math.min(columns, Math.max(1, Math.round((size.minW * columns) / 12))), minH: Math.min(size.minH, e.h) };
    });
  }, [config, width, breakpoint, columns]);

  const gridConfig = useMemo(() => ({ cols: columns, rowHeight: GRID_ROW_HEIGHT, margin: MARGIN, containerPadding: PADDING }), [columns]);
  const dragConfig = useMemo(() => ({ enabled: editing, handle: '.widget-drag-handle', cancel: '.widget-no-drag' }), [editing]);
  const resizeConfig = useMemo(() => ({ enabled: editing, handles: ['se'] as const }), [editing]);

  const children = useMemo(
    () =>
      config.widgets.map((widget) => (
        <div key={widget.id} className={cn('@container relative', editing && 'widget-editing')}>
          <WidgetSlot widget={widget} editing={editing} />
          {editing && <EditBar widget={widget} span={config.layouts.lg?.find((e) => e.i === widget.id)?.w ?? 12} handlers={handlers} />}
        </div>
      )),
    [config, editing],
  );

  return (
    <div ref={containerRef}>
      {mounted && (
        <ReactGridLayout
          // Widgets glide only while the dashboard is customized; otherwise they are simply placed.
          className={editing ? 'dashboard-customizing' : undefined}
          width={width}
          layout={layout}
          gridConfig={gridConfig}
          dragConfig={dragConfig}
          resizeConfig={resizeConfig}
          onDragStop={(arranged) => onArrange(arranged)}
          onResizeStop={(_layout, _old, item: LayoutItem | null) => {
            if (item) onResize(item.i, item.w, item.h, columns);
          }}
        >
          {children}
        </ReactGridLayout>
      )}
    </div>
  );
}

/**
 * The widget itself. In customize mode its controls cannot be pressed (so dragging never clicks a button), but it
 * still scrolls, so a widget made smaller can be checked all the way down.
 */
const WidgetSlot = memo(function WidgetSlot({ widget, editing }: { widget: WidgetInstance; editing: boolean }) {
  return (
    <div className={cn('h-full', editing && 'widget-inert select-none pt-10 opacity-80')} style={accentStyle(typeof widget.settings.accent === 'string' ? widget.settings.accent : null)}>
      <WidgetBody widget={widget} />
    </div>
  );
});

function EditBar({ widget, span, handlers }: { widget: WidgetInstance; span: number; handlers: React.RefObject<DashboardGridProps> }) {
  const definition = WIDGETS[widget.type];
  const name = widget.title ?? definition.name;
  const Icon = definition.icon;
  return (
    <div className="widget-drag-handle absolute inset-x-0 top-0 z-10 flex h-9 cursor-move items-center gap-1.5 rounded-t-lg border-b border-accent/40 bg-surface/95 px-2 backdrop-blur">
      <GripVertical className="size-4 shrink-0 text-muted" aria-hidden />
      <Icon className="size-3.5 shrink-0 text-accent" aria-hidden />
      <span className="min-w-0 truncate text-xs font-medium">{name}</span>
      <div className="widget-no-drag ml-auto flex shrink-0 items-center gap-0.5">
        <div role="radiogroup" aria-label={`Width of ${name}`} className="mr-1 flex rounded bg-surface-muted p-0.5">
          {QUICK_SPANS.filter((q) => q.span >= definition.size.minW).map((q) => (
            <button
              key={q.span}
              type="button"
              role="radio"
              aria-checked={span === q.span}
              aria-label={`${name}: ${q.name}`}
              title={`${q.name[0]!.toUpperCase()}${q.name.slice(1)} (${q.span} of 12 columns)`}
              onClick={() => handlers.current.onSpan(widget.id, q.span)}
              className={cn(
                'min-w-6 rounded px-1 py-0.5 text-[11px] font-semibold leading-none',
                span === q.span ? 'bg-accent text-accent-foreground' : 'text-muted hover:text-foreground',
              )}
            >
              {q.label}
            </button>
          ))}
        </div>
        <BarButton label={`Move ${name} earlier`} onClick={() => handlers.current.onMoveWidget(widget.id, -1)}>
          <ArrowUp aria-hidden />
        </BarButton>
        <BarButton label={`Move ${name} later`} onClick={() => handlers.current.onMoveWidget(widget.id, 1)}>
          <ArrowDown aria-hidden />
        </BarButton>
        <BarButton label={`Settings for ${name}`} onClick={() => handlers.current.onEditWidget(widget.id)}>
          <Settings2 aria-hidden />
        </BarButton>
        <BarButton label={`Duplicate ${name}`} onClick={() => handlers.current.onDuplicateWidget(widget.id)}>
          <Copy aria-hidden />
        </BarButton>
        <BarButton label={`Remove ${name}`} onClick={() => handlers.current.onRemoveWidget(widget.id)} danger>
          <X aria-hidden />
        </BarButton>
      </div>
    </div>
  );
}

function BarButton({ label, onClick, danger, children }: { label: string; onClick: () => void; danger?: boolean; children: React.ReactNode }) {
  return (
    <button
      type="button"
      aria-label={label}
      title={label}
      onClick={onClick}
      className={cn('rounded p-1 text-muted hover:bg-surface-muted [&_svg]:size-3.5', danger ? 'hover:text-danger' : 'hover:text-foreground')}
    >
      {children}
    </button>
  );
}
