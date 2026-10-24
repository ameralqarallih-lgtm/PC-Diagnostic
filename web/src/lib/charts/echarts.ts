// Apache ECharts, registered piece by piece so the bundle only carries what the dashboard uses.
import type { BarSeriesOption, EffectScatterSeriesOption, GaugeSeriesOption, LineSeriesOption, ScatterSeriesOption } from 'echarts/charts';
import { BarChart, EffectScatterChart, GaugeChart, LineChart, ScatterChart } from 'echarts/charts';
import type {
  DataZoomComponentOption,
  GridComponentOption,
  LegendComponentOption,
  MarkAreaComponentOption,
  MarkLineComponentOption,
  TooltipComponentOption,
} from 'echarts/components';
import {
  DataZoomComponent,
  GridComponent,
  LegendComponent,
  MarkAreaComponent,
  MarkLineComponent,
  TooltipComponent,
} from 'echarts/components';
import type { ComposeOption } from 'echarts/core';
import * as echarts from 'echarts/core';
import { CanvasRenderer } from 'echarts/renderers';

echarts.use([
  BarChart,
  GaugeChart,
  LineChart,
  EffectScatterChart,
  ScatterChart,
  GridComponent,
  TooltipComponent,
  LegendComponent,
  DataZoomComponent,
  MarkLineComponent,
  MarkAreaComponent,
  CanvasRenderer,
]);

export type ChartOption = ComposeOption<
  | BarSeriesOption
  | GaugeSeriesOption
  | LineSeriesOption
  | EffectScatterSeriesOption
  | ScatterSeriesOption
  | GridComponentOption
  | TooltipComponentOption
  | LegendComponentOption
  | DataZoomComponentOption
  | MarkLineComponentOption
  | MarkAreaComponentOption
>;

export { echarts };
