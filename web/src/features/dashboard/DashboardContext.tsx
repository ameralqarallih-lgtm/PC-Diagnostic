import { createContext, useContext } from 'react';
import type { LiveSeries, SeriesKey } from '@/features/live/series';
import type { RecorderCommand } from '@/features/recorder/useRecorderActions';
import type { AgentStatus, IncidentSummary, LiveSnapshot, UserIncidentType } from '@/lib/api/types';

/** What every widget can see and do. One live feed serves all widgets. */
export interface DashboardData {
  live: LiveSnapshot | null;
  series: LiveSeries;
  status: AgentStatus | null;
  connected: boolean;
  detail: string | null;
  incidents: IncidentSummary[];
  activeIncident: { key: string; from: string; until: string } | null;
  /** The gauge the user selected and the series it stands for (charts emphasize it). */
  focus: { gauge: string | null; series: SeriesKey | null };
  setFocus: (gauge: string | null, series: SeriesKey | null) => void;
  capture: (type: UserIncidentType | null, note: string) => void;
  capturing: boolean;
  command: (command: RecorderCommand) => void;
  commandBusy: boolean;
}

const DashboardDataContext = createContext<DashboardData | null>(null);

export const DashboardDataProvider = DashboardDataContext.Provider;

export function useDashboardData(): DashboardData {
  const data = useContext(DashboardDataContext);
  if (!data) throw new Error('useDashboardData must be used inside the dashboard');
  return data;
}
