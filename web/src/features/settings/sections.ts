import { AppWindow, Database, Gauge, LifeBuoy, ScrollText, ShieldCheck, Siren, Smartphone, Sparkles, Video, type LucideIcon } from 'lucide-react';
import type { UserSettings } from '@/lib/api/types';

/** The settings' topics (the Settings page's list and the command palette's search). */
export interface SectionInfo {
  id: string;
  title: string;
  group: 'Account' | 'Recording' | 'Data' | 'Advanced';
  icon: LucideIcon;
  /** Words a search finds the section by (its settings' names and what they are about). */
  keywords: string;
  /** The parts of the settings the section edits (for its unsaved-changes mark). */
  parts: (keyof UserSettings)[];
}

/** The settings, by topic, in the order they are shown. */
export const SECTIONS: SectionInfo[] = [
  { id: 'reports', title: 'Problem reports', group: 'Account', icon: LifeBuoy, keywords: 'problem report bug crash error send makers support consent automatic feedback', parts: [] },
  { id: 'recording', title: 'Recording', group: 'Recording', icon: Video, keywords: 'when to record manual always scheduled schedule time window days kept before incident recorded after memory recent minutes ring buffer', parts: ['recorder'] },
  { id: 'triggers', title: 'Automatic incidents', group: 'Recording', icon: Siren, keywords: 'anomaly anomalies rules triggers threshold cooldown automatic incidents per hour measurements events baseline', parts: ['triggers'] },
  { id: 'enhanced', title: 'Enhanced mode', group: 'Recording', icon: Sparkles, keywords: 'enhanced flight recorder trace etw etl wpr per-process disk network io administrator', parts: ['enhanced'] },
  { id: 'privacy', title: 'Privacy', group: 'Data', icon: ShieldCheck, keywords: 'privacy command lines user names wi-fi wifi network names collect windows events network details upload', parts: ['privacy'] },
  { id: 'events', title: 'Windows events', group: 'Data', icon: ScrollText, keywords: 'windows events read back start crash backfill rule per minute message length', parts: ['events'] },
  { id: 'storage', title: 'Storage', group: 'Data', icon: Database, keywords: 'storage full detail kept hours summaries days write disk incidents kept retention space', parts: ['storage'] },
  { id: 'sampling', title: 'Sampling', group: 'Advanced', icon: Gauge, keywords: 'sampling interval system metrics sensors processes inventory every ms seconds kept per sample cpu budget performance', parts: ['sampling'] },
  { id: 'app', title: 'Recorder app', group: 'Advanced', icon: AppWindow, keywords: 'tray icon notify notification hotkey shortcut capture keyboard', parts: ['agent'] },
  { id: 'devices', title: 'Other devices', group: 'Advanced', icon: Smartphone, keywords: 'other devices lan network phone android app pairing pair code paired qr https port address localhost 127.0.0.1', parts: [] },
];

export const GROUPS = ['Account', 'Recording', 'Data', 'Advanced'] as const;

export function matches(section: SectionInfo, text: string): boolean {
  if (!text) return true;
  const haystack = `${section.title} ${section.group} ${section.keywords}`.toLowerCase();
  return text
    .toLowerCase()
    .split(/\s+/)
    .filter(Boolean)
    .every((word) => haystack.includes(word));
}
