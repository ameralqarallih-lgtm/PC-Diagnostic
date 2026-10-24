import { useEffect, useRef, useState } from 'react';
import { ApiError, apiGet } from '@/lib/api/client';
import type { LiveMessage, LiveTrend, StatusResponse } from '@/lib/api/types';
import { recordLive } from '@/features/dashboard/custom/sources';
import { appendPoint, emptySeries, mergeTrend, pointFromSnapshot, seriesFromTrend, type LiveSeries } from './series';

export type LiveConnection = 'connecting' | 'open' | 'reconnecting' | 'unauthorized';

export interface LiveFeedState {
  message: LiveMessage | null;
  /** Up to ten minutes of headline values: the Agent's trend when the page opened, then one sample per update. */
  series: LiveSeries;
  connection: LiveConnection;
  /** When the last update arrived (browser clock). */
  receivedAt: number | null;
}

/**
 * Subscribes to the live Server-Sent Events stream (one update per second) and keeps the chart history. Each time
 * the stream (re)opens, the Agent's recent trend fills in what the page has not seen. The browser reconnects by
 * itself when the stream drops; when the request itself fails, a status probe tells an expired session (stop) from
 * a restarting web host (retry).
 */
export function useLiveFeed(enabled = true): LiveFeedState {
  const [state, setState] = useState<LiveFeedState>({ message: null, series: emptySeries(), connection: 'connecting', receivedAt: null });
  const retryTimer = useRef<number | undefined>(undefined);

  useEffect(() => {
    if (!enabled || typeof EventSource === 'undefined') return undefined;
    let source: EventSource | null = null;
    let disposed = false;

    const loadTrend = () => {
      apiGet<LiveTrend>('/live/trend?minutes=10')
        .then((trend) => {
          if (!disposed && trend) setState((s) => ({ ...s, series: mergeTrend(s.series, seriesFromTrend(trend)) }));
        })
        .catch(() => {
          // No trend (Agent not running yet): the chart fills as updates arrive.
        });
    };

    const connect = () => {
      source = new EventSource('/api/v1/live/stream');
      source.addEventListener('open', () => {
        setState((s) => ({ ...s, connection: 'open' }));
        loadTrend();
      });
      source.addEventListener('live', (event) => {
        const message = JSON.parse((event as MessageEvent<string>).data) as LiveMessage;
        // Every live value, for the widgets the user builds (kept ten minutes, shared by all of them).
        recordLive(message.live);
        setState((s) => {
          const time = message.live?.timestamp ? Date.parse(message.live.timestamp) : Number.NaN;
          const series = message.live && Number.isFinite(time) ? appendPoint(s.series, time, pointFromSnapshot(message.live)) : s.series;
          return { message, series, connection: 'open', receivedAt: Date.now() };
        });
      });
      source.addEventListener('error', () => {
        if (!source || source.readyState !== EventSource.CLOSED) {
          setState((s) => ({ ...s, connection: 'reconnecting' }));
          return;
        }
        // The browser gave up (non-200 response): find out why before retrying.
        source.close();
        apiGet<StatusResponse>('/status')
          .then(() => scheduleRetry())
          .catch((error: unknown) => {
            if (error instanceof ApiError && error.isUnauthorized) {
              setState((s) => ({ ...s, connection: 'unauthorized' }));
            } else {
              scheduleRetry();
            }
          });
      });
    };

    const scheduleRetry = () => {
      if (disposed) return;
      setState((s) => ({ ...s, connection: 'reconnecting' }));
      retryTimer.current = window.setTimeout(connect, 5000);
    };

    connect();
    return () => {
      disposed = true;
      window.clearTimeout(retryTimer.current);
      source?.close();
    };
  }, [enabled]);

  return state;
}
