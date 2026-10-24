import { useEffect } from 'react';
import { apiGet } from '@/lib/api/client';
import type { WeatherAnswer, WeatherReport } from '@/lib/api/types';
import type { CharacterWeather, Company } from './company';

/** Older than this (ms), characters no longer show it (as the recorder's WeatherReport.ShownFor). */
export const WEATHER_SHOWN_FOR = 3 * 3600_000;

/** The weather for the characters from a report (null: none, or older than {@link WEATHER_SHOWN_FOR}). */
export function weatherOf(report: WeatherReport | null | undefined, now = Date.now()): CharacterWeather | null {
  if (!report) return null;
  const at = Date.parse(report.at);
  if (!Number.isFinite(at) || now - at > WEATHER_SHOWN_FOR) return null;
  return { id: Math.floor(at / 1000), kind: report.kind, temperature: report.temperature, wind: report.wind, day: report.day };
}

// One look a minute for every screen of the page.
const shared: { at: number; weather: CharacterWeather | null } = { at: Number.NEGATIVE_INFINITY, weather: null };

/** Keeps `company`'s weather as the Web host has it (GET /overlay/weather, looked at once a minute). */
export function useCharacterWeather(company: Company) {
  useEffect(() => {
    let live = true;
    const load = async () => {
      if (Date.now() - shared.at >= 60_000) {
        shared.at = Date.now();
        try {
          shared.weather = weatherOf((await apiGet<WeatherAnswer>('/overlay/weather')).report);
        } catch {
          // No weather (the Web host not answering): the characters do without.
        }
      }
      if (live) company.weather = shared.weather;
    };
    void load();
    const timer = setInterval(() => void load(), 60_000);
    return () => {
      live = false;
      clearInterval(timer);
    };
  }, [company]);
}
