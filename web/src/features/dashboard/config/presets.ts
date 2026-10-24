import { fromWidgets } from './layout';
import type { DashboardConfig, WidgetInstance, WidgetSettings, WidgetType } from './types';
import { WIDGETS } from './widgets';

type Placement = [id: string, type: WidgetType, x: number, y: number, w: number, h: number, settings?: WidgetSettings, title?: string];

function build(placements: Placement[]): DashboardConfig {
  const widgets: WidgetInstance[] = placements.map(([id, type, , , , , settings, title]) => ({
    id,
    type,
    title: title ?? null,
    settings: { ...structuredClone(WIDGETS[type].defaults), ...settings },
  }));
  // Placements are listed in reading order; positions are computed from the widths.
  return fromWidgets(widgets, placements.map(([id, , , , w, h]) => ({ i: id, w, h })));
}

export interface Preset {
  id: string;
  name: string;
  description: string;
  config: () => DashboardConfig;
}

export const PRESETS: Preset[] = [
  {
    id: 'default',
    name: 'Default',
    description: 'Everything at a glance: gauges, live chart, controls, processes, events and sensors.',
    config: () =>
      build([
        ['capture', 'capture', 0, 0, 12, 5],
        ['gauges', 'gauges', 0, 5, 12, 4],
        ['chart', 'chart', 0, 9, 8, 10],
        ['recorder', 'recorder', 8, 9, 4, 10],
        ['processes', 'processes', 0, 20, 8, 10],
        ['incidents', 'incidents', 8, 20, 4, 10],
        ['events', 'events', 0, 30, 8, 8],
        ['anomalies', 'anomalies', 8, 30, 4, 8],
        ['sensors', 'sensors', 0, 38, 4, 8],
        ['gpu', 'gpu', 4, 38, 4, 8],
        ['network', 'network', 8, 38, 4, 8],
        ['displays', 'displays', 0, 46, 4, 4],
      ]),
  },
  {
    id: 'gaming',
    name: 'Gaming / GPU',
    description: 'GPU and temperatures first, for flicker, artifacts and game crashes.',
    config: () =>
      build([
        ['capture', 'capture', 0, 0, 12, 5, { quickTypes: ['ScreenFlicker', 'ScreenArtifact', 'GameCrash', 'SystemFreeze', 'HighGpu', 'Other'] }],
        ['gauges', 'gauges', 0, 5, 12, 4, { metrics: ['gpu', 'vram', 'gpuTemp', 'cpu', 'cpuTemp', 'memory'] }],
        ['temps', 'chart', 0, 10, 6, 10, { view: 'temperature', window: '10' }, 'Temperatures'],
        ['usage', 'chart', 6, 10, 6, 10, { view: 'usage', series: ['gpu', 'vram', 'cpu'] }, 'GPU and CPU load'],
        ['processes', 'processes', 0, 20, 6, 9, { tab: 'gpu' }],
        ['gpu', 'gpu', 6, 20, 3, 9],
        ['displays', 'displays', 9, 20, 3, 4],
        ['recorder', 'recorder', 9, 24, 3, 5, { overhead: false }],
        ['events', 'events', 0, 29, 8, 7, { minimum: '2' }],
        ['anomalies', 'anomalies', 8, 29, 4, 7],
      ]),
  },
  {
    id: 'minimal',
    name: 'Minimal',
    description: 'Capture, four dials and one chart.',
    config: () =>
      build([
        ['capture', 'capture', 0, 0, 12, 5, { note: false }],
        ['gauges', 'gauges', 0, 5, 12, 4, { metrics: ['cpu', 'memory', 'gpu', 'disk'] }],
        ['chart', 'chart', 0, 10, 12, 10, { series: ['cpu', 'memory', 'gpu', 'disk'] }],
      ]),
  },
  {
    id: 'everything',
    name: 'Everything',
    description: 'Every widget, including single values and all three chart views.',
    config: () =>
      build([
        ['capture', 'capture', 0, 0, 12, 5],
        ['cpu-value', 'metric', 0, 5, 3, 4, { metric: 'cpu' }],
        ['memory-value', 'metric', 3, 5, 3, 4, { metric: 'memory' }],
        ['gpu-value', 'metric', 6, 5, 3, 4, { metric: 'gpu' }],
        ['processes-value', 'metric', 9, 5, 3, 4, { metric: 'processCount' }],
        ['gauges', 'gauges', 0, 9, 12, 5],
        ['usage', 'chart', 0, 14, 6, 10, { view: 'usage' }],
        ['temps', 'chart', 6, 14, 6, 10, { view: 'temperature' }],
        ['net', 'chart', 0, 24, 6, 9, { view: 'network' }],
        ['recorder', 'recorder', 6, 24, 3, 9],
        ['incidents', 'incidents', 9, 24, 3, 9],
        ['processes', 'processes', 0, 33, 8, 10],
        ['sensors', 'sensors', 8, 33, 4, 10],
        ['events', 'events', 0, 43, 8, 8],
        ['gpu', 'gpu', 8, 43, 4, 8],
        ['network', 'network', 0, 51, 6, 6],
        ['displays', 'displays', 6, 51, 6, 6],
      ]),
  },
];

export function defaultDashboard(): DashboardConfig {
  return PRESETS[0]!.config();
}
