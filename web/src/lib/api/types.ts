// API contract types. Hand-written mirrors of the C# records (camelCase JSON, enums as strings); the Web tests
// deserialize the same shapes, so a renamed property fails there.

export type RecordingMode = 'Manual' | 'Always' | 'Scheduled';
export type RecorderState = 'Idle' | 'Starting' | 'Recording' | 'Paused' | 'Stopping';
export type Severity = 'Info' | 'Low' | 'Medium' | 'High' | 'Critical';
export type IncidentState = 'Recording' | 'Finalizing' | 'Analyzing' | 'Complete' | 'Partial' | 'Failed';
export type IncidentSource = 'User' | 'Hotkey' | 'Tray' | 'Rule' | 'Event' | 'BootRecovery';

/** Mirrors PcDiag.Core.Ipc.AgentStatus. */
export interface AgentStatus {
  version: string;
  elevated: boolean;
  mode: RecordingMode;
  state: RecorderState;
  /** End of the pause; "9999-12-31T23:59:59.9999999+00:00" means until resumed. */
  pausedUntil: string | null;
  activeIncident: string | null;
  activeIncidentUntil: string | null;
  /** Sampling for the dashboard only (in memory, nothing recorded) because the recorder is idle. */
  liveView: boolean;
  /** Not recording, but watching for problems in memory: a problem starts a recording. */
  watching?: boolean;
  /** What Enhanced mode is doing; absent when it is off. */
  enhanced?: string | null;
  /** Items a custom recording selects; absent or null when it records everything (or is not recording). */
  recordingItems?: number | null;
  /** A storage problem (the drive almost full, the space limit exceeded); absent when all is well. */
  storage?: StorageNotice | null;
  /** Expected hardware that is missing; absent when everything expected is there. */
  hardware?: { missing: number; message: string } | null;
}

/** A custom recording's items (see the recorder's RecordingItems); absent or null records everything. */
export interface RecordingSelection {
  items: string[];
}

/** What a custom recording can select on this PC. */
export interface RecordingCatalog {
  sources: RecordingSource[];
}

export interface RecordingSource {
  id: string;
  label: string;
  /** cpu, memory, gpu, disk, volumes, network, sensors, processes, events, display, services or inventory. */
  kind: string;
  items: RecordingItem[];
}

export interface RecordingItem {
  id: string;
  label: string;
  detail?: string | null;
}

export interface AgentConnection {
  connected: boolean;
  detail: string | null;
  status: AgentStatus | null;
}

export interface StatusResponse {
  product: string;
  version: string;
  serverTime: string;
  dataDirectory: string;
  agent: AgentConnection;
}

/** A live value, or why it is missing (an Availability name such as "DriverMissing"). */
export interface LiveMetric {
  value: number | null;
  unavailable: string | null;
}

export interface LiveCpu {
  totalPercent: LiveMetric;
  frequencyMhz: LiveMetric;
  packageTemperatureC: LiveMetric;
  packagePowerW: LiveMetric;
  logicalProcessors: number;
}

export interface LiveMemory {
  usedPercent: LiveMetric;
  usedBytes: LiveMetric;
  totalBytes: LiveMetric;
  commitPercent: LiveMetric;
  hardFaultsPerSec: LiveMetric;
}

export interface LiveGpu {
  key: string;
  name: string;
  utilizationPercent: LiveMetric;
  vramUsedBytes: LiveMetric;
  vramTotalBytes: LiveMetric;
  temperatureC: LiveMetric;
  hotspotC: LiveMetric;
  powerW: LiveMetric;
  coreClockMhz: LiveMetric;
}

export interface LiveDisk {
  name: string;
  volumes: string;
  activePercent: LiveMetric;
  readBytesPerSec: LiveMetric;
  writeBytesPerSec: LiveMetric;
  temperatureC: LiveMetric;
}

export interface LiveNetwork {
  name: string;
  kind: string;
  up: boolean;
  receiveBytesPerSec: LiveMetric;
  sendBytesPerSec: LiveMetric;
  linkBitsPerSec: LiveMetric;
  wifiSignalPercent: LiveMetric;
}

export interface LiveProcess {
  pid: number;
  name: string;
  cpuPercent: number | null;
  workingSetBytes: number | null;
  privateBytes: number | null;
  gpuPercent: number | null;
  /** Every transfer together (files, devices and network). */
  ioBytesPerSec: number | null;
  notResponding: boolean;
  /** Disk only; Enhanced mode. */
  diskBytesPerSec?: number | null;
  /** Network only; Enhanced mode. */
  netBytesPerSec?: number | null;
  /** Dedicated GPU memory in use (all adapters). */
  gpuMemoryBytes?: number | null;
}

export interface LiveProcesses {
  count: number;
  byCpu: LiveProcess[];
  byMemory: LiveProcess[];
  byGpu: LiveProcess[];
  /** By dedicated GPU memory. */
  byGpuMemory?: LiveProcess[] | null;
  byIo: LiveProcess[];
  /** Enhanced mode only. */
  byNetwork?: LiveProcess[] | null;
}

export interface LiveEvent {
  timestamp: string;
  source: string;
  title: string;
  category: string;
  severity: Severity;
}

export interface LiveDisplay {
  remoteSession: boolean;
  displays: string[];
}

export interface LiveAgent {
  cpuPercent: LiveMetric;
  workingSetBytes: LiveMetric;
  tickMs: LiveMetric;
  ringFrames: number;
  ringMegabytes: number;
  /** The managed heap after the last garbage collection. */
  managedHeapBytes?: LiveMetric;
}

export interface LiveSnapshot {
  timestamp: string | null;
  recording: boolean;
  cpu: LiveCpu | null;
  memory: LiveMemory | null;
  gpus: LiveGpu[];
  disks: LiveDisk[];
  networks: LiveNetwork[];
  processes: LiveProcesses | null;
  recentEvents: LiveEvent[];
  display: LiveDisplay | null;
  agent: LiveAgent;
  /** Running anomalies and those that ended in the last minutes, newest first. */
  anomalies?: AnomalyInfo[];
}

/** A detected anomaly with its reasoning (live feed, GET /api/v1/anomalies). Times are Unix milliseconds. */
export interface AnomalyInfo {
  id: number;
  ruleId: string;
  classification: string;
  /** The metric, or "event:…", "process:…", "display:…" for event rules. */
  series: string;
  device: string | null;
  startedAt: number;
  /** Null while it is still going on. */
  endedAt: number | null;
  observed: number;
  unit: string;
  severity: Severity;
  /** The rule in plain words. */
  rule: string;
  /** The normal range, for baseline rules. */
  baseline: string | null;
  current: string;
  durationSeconds: number;
  incidentKey: string | null;
  summary: string;
}

/** Recent headline values, columnar (GET /api/v1/live/trend). Times are Unix milliseconds. */
export interface LiveTrend {
  times: number[];
  cpu: (number | null)[];
  memory: (number | null)[];
  gpu: (number | null)[];
  vram: (number | null)[];
  disk: (number | null)[];
  netReceive: (number | null)[];
  netSend: (number | null)[];
  cpuTemperature: (number | null)[];
  gpuTemperature: (number | null)[];
  gpuHotspot: (number | null)[];
}

/** One Server-Sent Event from /api/v1/live/stream. */
export interface LiveMessage {
  connected: boolean;
  detail: string | null;
  status: AgentStatus | null;
  live: LiveSnapshot | null;
}

export interface AgentCaptureResult {
  incidentKey: string;
  merged: boolean;
  snapshotOnly: boolean;
  recordingUntil: string;
}

export interface CaptureResponse {
  capture: AgentCaptureResult;
  status: AgentStatus;
}

export interface IncidentSummary {
  key: string;
  createdAt: string;
  centerAt: string;
  preSeconds: number;
  postSeconds: number;
  state: IncidentState;
  source: IncidentSource;
  userType: UserIncidentType | null;
  title: string;
  notes: string | null;
  severity: Severity;
  snapshotOnly: boolean;
  pinned: boolean;
  triggerCount: number;
}

export interface IncidentPage {
  total: number;
  items: IncidentSummary[];
}

/** Mirrors PcDiag.Core.Incidents.UserIncidentType. */
export const USER_INCIDENT_TYPES = [
  { id: 'ScreenFlicker', label: 'Screen Flicker' },
  { id: 'ScreenArtifact', label: 'Screen Artifact' },
  { id: 'SystemFreeze', label: 'System Freeze' },
  { id: 'ApplicationCrash', label: 'Application Crash' },
  { id: 'GameCrash', label: 'Game Crash' },
  { id: 'Slowdown', label: 'Slowdown' },
  { id: 'HighCpu', label: 'High CPU' },
  { id: 'HighRam', label: 'High RAM' },
  { id: 'HighGpu', label: 'High GPU' },
  { id: 'DiskProblem', label: 'Disk Problem' },
  { id: 'NetworkProblem', label: 'Network Problem' },
  { id: 'Other', label: 'Other' },
] as const;

export type UserIncidentType = (typeof USER_INCIDENT_TYPES)[number]['id'];

/** A pause "until resumed" is reported as DateTimeOffset.MaxValue. */
export function isIndefinitePause(pausedUntil: string | null): boolean {
  return pausedUntil != null && pausedUntil.startsWith('9999-');
}

// History, investigation, system and settings (Phase 12).

export interface SeriesInfo {
  id: string;
  label: string;
  unit: string;
  group: string;
  device: string | null;
  longTerm: boolean;
  /** The device's stable key, or a hardware sensor's logical key (what the series stays attached to). */
  key?: string | null;
  /** When the device was last seen (Unix ms). */
  lastSeen?: number | null;
}

export interface SeriesData {
  id: string;
  /** Buckets as [time, min, average, max]. */
  points: [number, number, number, number][];
}

export interface HistoryResult {
  from: number;
  to: number;
  bucketMs: number;
  series: SeriesData[];
}

export interface TimelineEntry {
  ts: number;
  kind: string;
  title: string;
  importance: number;
  detail: Record<string, unknown> | null;
  sourceId: number | null;
}

export interface ProcessAtTime {
  procKey: number;
  pid: number;
  name: string;
  path: string | null;
  cpuPercent: number | null;
  workingSetBytes: number | null;
  privateBytes: number | null;
  ioReadBytesPerSec: number | null;
  ioWriteBytesPerSec: number | null;
  gpuPercent: number | null;
  gpuMemoryBytes: number | null;
  threads: number | null;
  handles: number | null;
  state: string;
  /** Disk only (read + write); Enhanced mode. */
  diskBytesPerSec?: number | null;
  /** Network only (receive + send); Enhanced mode. */
  netBytesPerSec?: number | null;
  /** Who signed the program file (for catalog-signed Windows files, the catalog's signer). */
  signer?: string | null;
  /** The program file's signature; null until it was checked. */
  signature?: SignatureStatus | null;
  /** When the process started (Unix ms). */
  startTs?: number | null;
  /**
   * False for a process known only to have been running: outside incidents the figures are stored for the busiest
   * processes of each tick.
   */
  measured?: boolean;
  /** When the figures were measured, when that is not the moment's own sample (every process is stored every 30 s when it changes). */
  figuresTs?: number | null;
}

/** A Windows service at a moment, with the process it ran in. */
export interface MomentService {
  name: string;
  displayName: string;
  status: string;
  startType: string;
  pid: number | null;
  /** The hosting process's key in the moment's process list. */
  procKey: number | null;
}

export type SignatureStatus = 'Unknown' | 'Embedded' | 'Catalog' | 'NotSigned' | 'NotTrusted' | 'Package';

export interface ProcessesAtTime {
  sampleTs: number | null;
  processes: ProcessAtTime[];
}

/** A series' value around a moment. */
export interface MomentValue {
  id: string;
  label: string;
  unit: string;
  group: string;
  value: number;
}

/** Everything recorded about one moment of the rolling history. */
export interface HistoryMoment {
  at: number;
  /** False when the recorder was not recording then. */
  recorded: boolean;
  values: MomentValue[];
  processSampleTs: number | null;
  /** Measured processes busiest first, then the others by name. */
  processes: ProcessAtTime[];
  /** Windows services then, running first. */
  services?: MomentService[] | null;
}

export type EvidenceLevel = 'Observation' | 'Correlation' | 'PossibleExplanation' | 'ConfirmedEvent' | 'NotProven';

/** A link from a finding to its data: a metric moment, an event row, an anomaly. */
export interface EvidenceLink {
  kind: 'metric' | 'range' | 'event' | 'anomaly' | 'trigger' | string;
  ts: number;
  sourceId: string | null;
}

export interface FindingEntry {
  ordinal: number;
  level: EvidenceLevel;
  text: string;
  evidence: EvidenceLink[] | null;
}

export interface TriggerEntry {
  ts: number;
  source: IncidentSource;
  userType: UserIncidentType | null;
  ruleId: string | null;
  detail: string | null;
  note: string | null;
}

export interface IncidentDetail {
  summary: IncidentSummary;
  from: number;
  to: number;
  triggers: TriggerEntry[];
  info: Record<string, string | null>;
  hasData: boolean;
}

export interface DeviceInfo {
  id: number;
  kind: string;
  name: string | null;
  vendor: string | null;
  details: Record<string, unknown> | null;
  firstSeen: number;
  lastSeen: number;
  /** The device's stable key (availability rows use it, e.g. "gpu.{key}.power_w"). */
  key?: string | null;
}

export interface InstalledDriver {
  deviceClass: string | null;
  deviceName: string | null;
  hardwareId: string | null;
  provider: string | null;
  version: string | null;
  date: string | null;
  inf: string | null;
  signer: string | null;
}

export interface AvailabilityInfo {
  series: string;
  availability: string;
  reason: string | null;
  since: number;
}

export interface SensorInfo {
  provider: string;
  kind: string;
  name: string;
  unit: string;
  key: string;
  value: number | null;
}

export interface SystemOverview {
  information: Record<string, string | null>;
  devices: DeviceInfo[];
  driversTs: number | null;
  drivers: InstalledDriver[];
  displayTs: number | null;
  display: { isRemoteSession?: boolean; displays?: Record<string, unknown>[] } | null;
  hardwareTs: number | null;
  hardware: Record<string, unknown> | null;
  availability: AvailabilityInfo[];
  sensors: SensorInfo[];
  /** The device register (absent in incidents recorded before it existed). */
  ioDevices?: RegisteredDevice[] | null;
}

/** One device node of an I/O device (the keyboard function of a wireless receiver, a HID collection). */
export interface IoDevicePart {
  instanceId: string;
  name: string;
  kind: string;
  class: string | null;
  present: boolean;
  technical: boolean;
  problemCode: number;
  service: string | null;
  driverProvider: string | null;
  driverVersion: string | null;
  driverDate: string | null;
}

/** A physical I/O device, connected now or before, or a device of the PC itself that has a problem (kind "system"). */
export interface IoDevice {
  key: string;
  name: string;
  /** keyboard, mouse, game-controller, input, audio, camera, imaging, display, storage, portable, network, bluetooth, printer, ports, security, sensor, usb, other, system. */
  kind: string;
  kinds: string[];
  type: string | null;
  class: string | null;
  bus: string;
  manufacturer: string | null;
  vendorId: string | null;
  productId: string | null;
  present: boolean;
  connected: number;
  external: boolean;
  technical: boolean;
  problemCode: number;
  problem: string | null;
  instanceId: string;
  location: string | null;
  service: string | null;
  driverProvider: string | null;
  driverVersion: string | null;
  driverDate: string | null;
  driverInf: string | null;
  hardwareId: string | null;
  /** ISO times from Windows. */
  firstInstalled: string | null;
  lastArrival: string | null;
  lastRemoval: string | null;
  parts: IoDevicePart[];
}

/** A device in the register with when the recorder first listed it and last found it connected (Unix ms). */
export interface RegisteredDevice {
  device: IoDevice;
  firstSeen: number;
  lastSeen: number;
}

/** A device connected, disconnected, dropping out or failing (GET /api/v1/system/device-events). */
export interface DeviceEventInfo {
  ts: number;
  /** 1 connected, 2 disconnected, 3 dropped out, 4 keeps disconnecting, 5 PC device disappeared, 6 failed, 7 disabled, 8 works again. */
  eventId: number;
  title: string;
  message: string | null;
  severity: number;
  device: string | null;
  data: Record<string, string> | null;
}

export interface RecordingWindowSetting {
  days: string[];
  start: string;
  end: string;
  /** What the window records; absent or null records everything. */
  selection?: RecordingSelection | null;
}

export interface UserSettings {
  recorder: {
    mode: RecordingMode;
    schedule: RecordingWindowSetting[];
    /** The last custom recording started from the dashboard, offered again next time. */
    customSelection?: RecordingSelection | null;
    startAgentAtLogon: boolean;
    /** While not recording (and not paused), watch for problems in memory; a problem starts a recording. */
    watchWhenNotRecording?: boolean;
    preIncidentMinutes: number;
    postIncidentSeconds: number;
    ringBufferMaxMegabytes: number;
  };
  sampling: {
    systemIntervalMs: number;
    sensorIntervalMs: number;
    processIntervalMs: number;
    inventoryIntervalSeconds: number;
    topProcessCount: number;
    cpuBudgetPercent: number;
  };
  storage: { telemetryRetentionHours: number; rollupRetentionDays: number; writeBehindSeconds: number; maxIncidents: number; dataLimitGb: number; keepFreeGb: number };
  events: { backfillMinutes: number; bootBackfillMaxHours: number; maxPerRulePerMinute: number; maxMessageLength: number };
  privacy: {
    captureCommandLines: boolean;
    captureUserNames: boolean;
    captureWifiNetworkNames: boolean;
    collectWindowsEvents: boolean;
    collectNetworkDetails: boolean;
  };
  agent: { showTrayIcon: boolean; captureHotkey: string; notifyOnCapture: boolean; webHostPath: string | null };
  /** Automatic incident triggers: the user's changes to the shipped rules. */
  triggers?: TriggerSettings;
  /** Enhanced mode: deeper evidence from Event Tracing for Windows (needs administrator). */
  enhanced?: EnhancedSettings;
}

export interface EnhancedSettings {
  /** WPR flight recorder saved as traces/incident.etl with each new incident. */
  flightRecorder: boolean;
  /** True per-process disk and network rates. */
  processIo: boolean;
}

/** A change to one rule; a missing or null field keeps the shipped value. */
export interface TriggerRuleSettings {
  enabled?: boolean | null;
  createIncident?: boolean | null;
  threshold?: number | null;
  durationSeconds?: number | null;
  windowSeconds?: number | null;
  cooldownMinutes?: number | null;
}

export interface TriggerSettings {
  automaticIncidents: boolean;
  maxIncidentsPerHour: number;
  rules: Record<string, TriggerRuleSettings>;
  /** The user's own rules. */
  custom?: CustomTriggerRule[];
  /** Shipped rules the user deleted (taking one off brings it back). */
  removed?: string[];
  /** The categories rules are listed under, in order; empty or absent: the built-in ones. */
  categories?: TriggerCategory[];
  /** Shipped rules moved to another category, by rule ID. */
  ruleCategories?: Record<string, string>;
}

/** A category anomaly rules are listed under. */
export interface TriggerCategory {
  id: string;
  name: string;
}

/** A Windows event a rule describes: its log, source, event IDs and levels (1 critical … 4 information). */
export interface CustomEventMatch {
  channel: string;
  provider?: string | null;
  eventIds: number[];
  levels: number[];
}

/** One of the system's readings a rule can watch (the rule's metric is "reading:" + id). */
export interface TriggerReading {
  id: string;
  label: string;
  group: string;
  unit: string;
}

/** Names a rule can be narrowed to. */
export interface TriggerSuggestions {
  processes: string[];
  displays: string[];
  channels: string[];
  providers: string[];
}

/** One of the user's own anomaly rules (IDs start with "my-"). */
export interface CustomTriggerRule {
  id: string;
  name: string;
  kind: AnomalyRuleKind;
  metric: string | null;
  /** The reading's name and unit, when it watches one of the system's readings. */
  readingLabel?: string | null;
  readingUnit?: string | null;
  /** Its category; null: by what it watches. */
  category?: string | null;
  direction: 'Above' | 'Below';
  threshold: number;
  durationSeconds: number;
  windowSeconds: number;
  eventRules: string[];
  processEvents: string[];
  /** Only these programs; empty: any. */
  processNames: string[];
  displayChanges: string[];
  /** Only these displays; empty: any. */
  displayTargets: string[];
  /** Windows events the user describes. */
  windowsEvents: CustomEventMatch[];
  severity: Severity;
  enabled: boolean;
  createIncident: boolean;
  cooldownMinutes: number;
}

/** What a rule of the user's own can watch. */
export interface TriggerChoices {
  metrics: { id: string; label: string; unit: string; perDevice: boolean; min: number; max: number }[];
  processEvents: string[];
  displayChanges: string[];
  windowsEvents: { id: string; title: string }[];
  /** The built-in categories. */
  categories: TriggerCategory[];
}

export type AnomalyRuleKind = 'Threshold' | 'ThresholdDuration' | 'RateOfChange' | 'BaselineDeviation' | 'CounterSpike' | 'Event';

/** An anomaly rule as the Settings page shows it (GET /api/v1/triggers). */
export interface TriggerRuleInfo {
  id: string;
  name: string;
  classification: string;
  kind: AnomalyRuleKind;
  metric: string | null;
  perDevice: boolean;
  /** The rule in plain words, with the saved changes applied. */
  description: string;
  note: string | null;
  severity: Severity;
  unit: string;
  thresholdMin: number;
  thresholdMax: number;
  usesThreshold: boolean;
  usesDuration: boolean;
  usesWindow: boolean;
  defaults: { enabled: boolean; createIncident: boolean; threshold: number; durationSeconds: number; windowSeconds: number; cooldownMinutes: number };
  /** One of the user's own rules. */
  custom?: boolean;
  /** A shipped rule the user deleted. */
  removed?: boolean;
  /** The category it starts in. */
  defaultCategory?: string;
}

export interface SettingsResponse {
  settings: UserSettings;
  file: string;
}

export const INCIDENT_SOURCES: { id: IncidentSource; label: string }[] = [
  { id: 'User', label: 'Dashboard' },
  { id: 'Hotkey', label: 'Hotkey' },
  { id: 'Tray', label: 'Tray' },
  { id: 'Rule', label: 'Anomaly rule' },
  { id: 'Event', label: 'Windows event' },
  { id: 'BootRecovery', label: 'After a crash or restart' },
];

/** A device paired for access from the local network. */
export interface PairedDevice {
  id: string;
  name: string;
  pairedAt: string;
  lastSeenAt: string;
  lastAddress: string | null;
}

/** Access from other devices (GET /api/v1/lan; this PC only). */
export interface LanStatus {
  supported: boolean;
  unsupported: string | null;
  enabled: boolean;
  listening: boolean;
  port: number;
  urls: string[];
  fingerprint: string | null;
  problem: string | null;
  devices: PairedDevice[];
  /** The current pairing code, with the companion app's pcdiag://pair link when listening. */
  pairing: { code: string; expiresAt: string; link: string | null } | null;
  installId: string;
  /** Path of the companion app download, when it is included. */
  apkUrl: string | null;
  discoveryPort: number;
}

/** The recorder's own cost over a period (GET /api/v1/recorder/overhead). */
export interface RecorderOverhead {
  from: number;
  to: number;
  samples: number;
  cpuAvgPercent: number | null;
  cpuP95Percent: number | null;
  cpuMaxPercent: number | null;
  workingSetAvgBytes: number | null;
  workingSetMaxBytes: number | null;
  tickAvgMs: number | null;
  tickMaxMs: number | null;
  samplesWithMissedCollectors: number;
}

/** A summary figure of a period diagnostic section. */
export interface PeriodStat {
  label: string;
  value: string;
  /** The History series it comes from. */
  seriesId: string | null;
}

export interface PeriodTable {
  title: string;
  columns: string[];
  rows: string[][];
}

/** An issue or abnormal behaviour found in the period. */
export interface PeriodFinding {
  severity: Severity;
  level: EvidenceLevel;
  title: string;
  text: string;
  from: string | null;
  to: string | null;
  seriesIds: string[] | null;
  occurrences: number;
  /** The section it belongs to (on the summary's top findings). */
  section: string | null;
  incidentKey: string | null;
}

export interface PeriodSection {
  id: string;
  title: string;
  stats: PeriodStat[];
  findings: PeriodFinding[];
  tables: PeriodTable[];
}

export interface PeriodSummary {
  periodMinutes: number;
  recordedMinutes: number;
  /** Finding counts by severity (Critical, High, Medium, Low, Info). */
  findingsBySeverity: Record<string, number>;
  top: PeriodFinding[];
  note: string | null;
}

/** GET /diagnostics/period: the period diagnostic. */
export interface PeriodReport {
  from: string;
  to: string;
  generatedAt: string;
  summary: PeriodSummary;
  sections: PeriodSection[];
}

export type DiagnosticJobState = 'Queued' | 'Reading' | 'Analyzing' | 'Completed' | 'Failed' | 'Cancelled';

/** A period diagnostic run in the background (POST/GET /diagnostics/jobs). */
export interface DiagnosticJob {
  id: string;
  from: string;
  to: string;
  state: DiagnosticJobState;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
  error: string | null;
  findingsBySeverity: Record<string, number> | null;
  /** The incidents it was asked for (their recordings make its period), when it was. */
  incidents?: string[] | null;
  /** Still queued or running. */
  running: boolean;
}

/** GET /diagnostics/jobs/{id}: the job and, once completed, its report. */
export interface DiagnosticJobDetail {
  job: DiagnosticJob;
  report: PeriodReport | null;
}

// System control and tune-up (Phase 20).

export type ControlProtection = 'None' | 'Caution' | 'Protected';

export interface ControlProcess {
  pid: number;
  /** Creation time (UTC ticks): actions name a process by PID and this. */
  startTicks: number;
  parentPid: number;
  name: string;
  path: string | null;
  publisher: string | null;
  /** "app", "background", "windows" or "service". */
  kind: 'app' | 'background' | 'windows' | 'service';
  protection: ControlProtection;
  protectionReason: string | null;
  cpuPercent: number | null;
  workingSetBytes: number | null;
  privateBytes: number | null;
  threads: number | null;
  handles: number | null;
  priority: string | null;
  notResponding: boolean;
  sessionId: number;
  services: string[];
  startedAt: string;
  /** GPU use, dedicated GPU memory and network, from the newest samples. */
  gpuPercent?: number | null;
  gpuMemoryBytes?: number | null;
  netBytesPerSec?: number | null;
}

export interface ControlService {
  name: string;
  displayName: string;
  description: string | null;
  status: string;
  startType: string;
  processId: number | null;
  path: string | null;
  publisher: string | null;
  windows: boolean;
  canStop: boolean;
  protection: ControlProtection;
  protectionReason: string | null;
}

export interface StartupEntry {
  id: string;
  name: string;
  command: string;
  location: string;
  publisher: string | null;
  enabled: boolean;
}

export interface CleanupTarget {
  id: string;
  name: string;
  description: string;
  bytes: number;
  files: number;
  recommended: boolean;
  caution: string | null;
}

export interface PowerPlan {
  id: string;
  name: string;
  active: boolean;
}

export interface VolumeSpace {
  name: string;
  label: string | null;
  totalBytes: number;
  freeBytes: number;
  system: boolean;
}

export interface SystemTuning {
  powerPlans: PowerPlan[];
  hasBattery: boolean;
  hibernationEnabled: boolean | null;
  hibernationFileBytes: number | null;
  gameMode: boolean | null;
  gpuScheduling: boolean | null;
  standbyBytes: number | null;
  freeBytes: number | null;
  wslRunning: boolean;
  wslBytes: number | null;
  volumes: VolumeSpace[];
}

export interface TuneUpReport {
  at: string;
  cleanup: CleanupTarget[];
  system: SystemTuning;
  startup: StartupEntry[];
}

export interface ControlResult {
  ok: boolean;
  message: string;
  freedBytes?: number | null;
  changed: number;
  problems?: string[] | null;
  restartNeeded: boolean;
  /** What safe mode left because other programs were using it, and why. */
  skipped?: string[] | null;
}

/** A process an optimization step would act on. */
export interface OptimizeCandidate {
  pid: number;
  startTicks: number;
  name: string;
  kind: string;
  cpuPercent: number | null;
  workingSetBytes: number | null;
  gpuPercent: number | null;
  gpuMemoryBytes: number | null;
  netBytesPerSec: number | null;
  diskBytesPerSec: number | null;
  /** Why safe mode leaves it. */
  inUse: string | null;
  /** Why it is never acted on. */
  protected: string | null;
}

export interface OptimizeDrive {
  mount: string;
  model: string;
  ssd: boolean;
  activePercent: number | null;
  inUse: string | null;
}

export interface OptimizePlan {
  at: string;
  gpuMemory: OptimizeCandidate[];
  memory: OptimizeCandidate[];
  cpu: OptimizeCandidate[];
  drives: OptimizeDrive[];
  gpus: { instanceId: string; name: string }[];
  standbyBytes: number | null;
  standbyInUse: string | null;
  gpuRestartInUse: string | null;
  wslBytes: number | null;
  wslInUse: string | null;
  foregroundName: string | null;
}

/** A file or folder of a clean-up target. */
export interface CleanupEntry {
  root: number;
  path: string;
  name: string;
  folder: boolean;
  bytes: number;
  files: number;
  removableBytes: number;
  removableFiles: number;
  modified: string | null;
  partial: boolean;
}

export interface CleanupListing {
  target: string;
  location: string | null;
  entries: CleanupEntry[];
  more: number;
  note: string | null;
}

// Problem reports to the makers (Phase 21).

export interface ProblemCause {
  type: string;
  message: string;
  stack: string | null;
  hResult: number | null;
  inner: ProblemCause | null;
}

export interface ProblemReport {
  id: string;
  kind: 'manual' | 'automatic';
  at: string;
  component: string;
  summary: string;
  description: string | null;
  contact: string | null;
  cause: ProblemCause | null;
  product: { name: string; version: string; app: string; elevated: boolean; installId: string };
  environment: { os: string; architecture: string; processors: number; memoryBytes: number; runtime: string; language: string; hardware: string[] };
  log: { at: string; level: string; source: string; message: string }[];
  context: Record<string, string>;
  fingerprint: string;
  occurrences: number;
  firstSeen: string;
}

export interface ProblemReportEntry {
  id: string;
  kind: 'manual' | 'automatic';
  component: string;
  summary: string;
  at: string;
  occurrences: number;
  /** "pending", "sent", "failed" or "local" (kept here: automatic reports are off). */
  status: 'pending' | 'sent' | 'failed' | 'local';
  sentAt: string | null;
  reference: string | null;
  lastError: string | null;
}

export interface ReportsOverview {
  serviceConfigured: boolean;
  automatic: boolean;
  askedAt: string | null;
  contact: string | null;
  items: ProblemReportEntry[];
}

export interface SendOutcome {
  sent: boolean;
  reference: string | null;
  problem: string | null;
}

/** How a panel of on-screen readings looks (sizes at 100 % display scaling). */
/** A panel's moving background. */
export type OverlayMotion = 'none' | 'aurora' | 'grid' | 'stars' | 'pulse' | 'scan' | 'shimmer';

/** What is drawn behind each tile, fitted to it and to its dial. */
export type OverlayBackdrop = 'none' | 'plate' | 'hud' | 'bezel' | 'socket' | 'chevron' | 'glass' | 'neon' | 'blueprint';

/** Arts drawn as instruments around the readings (gauge faces, scales, frames and controls). */
export type OverlayDrawnArt = 'cockpit' | 'racing' | 'aviation' | 'holo' | 'synthwave' | 'brass' | 'terminal' | 'lux';

/** Arts generated as moving scenes (on the GPU), with frosted-glass cards over the readings. */
export type OverlayGeneratedArt = 'silk' | 'mesh' | 'aurora' | 'nebula' | 'caustics' | 'bokeh' | 'topo' | 'prism' | 'ocean' | 'lava' | 'ink' | 'marble' | 'foil' | 'fireflies' | 'warp' | 'galaxy' | 'clouds' | 'snow' | 'ribbons' | 'circuit';

/** The art a panel's picture is made from, fitted to where its readings are; "glass" is frosted cards over a library picture. */
export type OverlayArt = OverlayDrawnArt | OverlayGeneratedArt | 'glass';

export interface OverlayStyle {
  font: string;
  size: number;
  bold: boolean;
  labelColor: string;
  valueColor: string;
  unitColor: string;
  titleColor: string;
  warningColor: string;
  criticalColor: string;
  background: string;
  backgroundOpacity: number;
  border: string | null;
  cornerRadius: number;
  padding: number;
  opacity: number;
  outline: boolean;
  showLabels: boolean;
  showUnits: boolean;
  /** Bars, rings, graphs, icons, the accent edge and the title bar. */
  accent: string;
  /** A second background color: a gradient from one to the other. */
  background2: string | null;
  /** The gradient's direction in degrees, as in CSS. */
  gradientAngle: number;
  borderWidth: number;
  /** A soft shadow under the panel, 0–100. */
  shadow: number;
  /** Values, icons and the border glow, 0–100. */
  glow: number;
  /** A light sheen over the top (glass). */
  sheen: boolean;
  accentEdge: 'none' | 'left' | 'top' | 'bottom';
  /** The title on a band in the accent. */
  titleBar: boolean;
  /** Labels and the title in small spaced capitals. */
  caps: boolean;
  /** Values' size against the text size, 80–300 %. */
  valueScale: number;
  /** Space between readings, 50–250 %. */
  spacing: number;
  icons: boolean;
  /** Icons' color (the accent when null). */
  iconColor: string | null;
  barStyle: 'solid' | 'rounded' | 'segments';
  /** Bars' and rings' thickness, 50–400 %. */
  barHeight: number;
  /** How much history a graph shows, 15–300 s. */
  graphSeconds: number;
  /** Tiles' color (null: a light tint on a dark panel, a dark one on a light panel). */
  tileColor: string | null;
  /** Tiles' opacity, 0–100. */
  tileOpacity: number;
  /** A status light's color within the limits. */
  okColor: string;
  /** A picture behind the readings (an uploaded picture's id), none when null. */
  image?: string | null;
  /** How the picture fills the panel. */
  imageFit?: OverlayImageFit;
  /** Where it sits when it does not fill exactly. */
  imagePosition?: 'center' | 'top' | 'bottom' | 'left' | 'right';
  /** The picture's opacity, 0–100. */
  imageOpacity?: number;
  /** Blur, 0–30 px at 100 %. */
  imageBlur?: number;
  /** The picture in grey. */
  imageGray?: boolean;
  /** A color laid over the picture. */
  imageTint?: string | null;
  /** The tint's opacity, 0–100. */
  imageTintOpacity?: number;
  /** A pattern over the background. */
  pattern?: OverlayPattern | null;
  /** The pattern's color (null: light on dark, dark on light). */
  patternColor?: string | null;
  /** The pattern's opacity, 0–100. */
  patternOpacity?: number;
  /** The pattern's size, 50–400 %. */
  patternScale?: number;
  /** Where a value sits in its fixed space (room for the widest it can read): left, center or right. */
  valueAlign?: OverlayValueAlign;
  /** Where a value sits up and down in its tile (absent: middle alone, top over a bar). */
  valueVertical?: OverlayValueVertical | null;
  /** A moving background under the readings. */
  motion?: OverlayMotion;
  /** How fast it moves, 25–300 %. */
  motionSpeed?: number;
  /** What is drawn behind each tile, fitted to it and to its dial wherever the tile is. */
  backdrop?: OverlayBackdrop;
  /**
   * The art its picture was drawn from (gauge faces behind its dials, grids behind its graphs, frames and controls),
   * drawn again by the editor when the panel changes size. "art:…" in image means not drawn yet (never saved).
   */
  art?: OverlayArt | null;
  /** The library picture the glass art is drawn over (a still or an animation). */
  artSource?: string | null;
}

/** Where a value sits in its space. */
export type OverlayValueAlign = 'left' | 'center' | 'right';
export type OverlayValueVertical = 'top' | 'middle' | 'bottom';

/** How a background picture fills its panel. */
export type OverlayImageFit = 'cover' | 'contain' | 'stretch' | 'tile' | 'center';

/** The patterns a panel's background can have. */
export type OverlayPattern = 'dots' | 'grid' | 'diagonal' | 'carbon' | 'hexagons' | 'scanlines';

/** An uploaded background picture. */
export interface OverlayImage {
  id: string;
  width: number;
  height: number;
  /** 1 for a still; an animation's frames. */
  frames?: number;
}

/** A picture of the library: an upload (kept until deleted) or one generated for a panel (removed once unused). */
export interface LibraryPicture {
  id: string;
  name: string | null;
  kind: 'upload' | 'generated';
  width: number;
  height: number;
  /** 1 for a still; an animation's frames (0 when not known). */
  frames: number;
  size: number;
  added: string;
  /** A panel shows it. */
  used: boolean;
  /** On this PC (where the recorder reads it); otherwise only in another storage location. */
  here: boolean;
}

/** How a value reads: the value, a percentage of its total, both, or used of total. */
export type OverlayShow = 'value' | 'percent' | 'both' | 'of';

/** What goes with a value (the dashboard widgets' designs): dials, charts of its history, bars, text. */
export type OverlayVisual = 'none' | 'bar' | 'ring' | 'graph' | 'arc' | 'speed' | 'thermo' | 'line' | 'columns' | 'stats' | 'status' | 'trend';

/** One reading in a panel. */
export interface OverlayReading {
  source: string;
  label?: string | null;
  decimals?: number | null;
  warning?: number | null;
  critical?: number | null;
  below?: boolean;
  bar?: boolean;
  max?: number | null;
  color?: string | null;
  /** What goes with the value; absent follows `bar`. */
  visual?: OverlayVisual | null;
  /** An icon id, "none", or absent to choose by what it measures. */
  icon?: string | null;
  /** How the value reads (absent: the value). */
  show?: OverlayShow | null;
  /** Columns and rows its tile spans in the tiles grid. */
  colSpan?: number;
  rowSpan?: number;
  /** Its own look over the panel's (absent follows the panel). */
  look?: OverlayReadingLook | null;
}

/** A reading's own look over its panel's: each absent or null follows the panel's Look. */
export interface OverlayReadingLook {
  font?: string | null;
  /** Its value's size against the panel's, 50–300 %. */
  valueSize?: number | null;
  bold?: boolean | null;
  valueColor?: string | null;
  /** Its label's size against the panel's, 50–250 %. */
  labelSize?: number | null;
  labelColor?: string | null;
  unitColor?: string | null;
  showLabel?: boolean | null;
  showUnit?: boolean | null;
  align?: OverlayValueAlign | null;
  /** Its value at the top, middle or bottom of its tile. */
  vertical?: OverlayValueVertical | null;
  /** Its dial's or chart's size, 50–250 %. */
  visualSize?: number | null;
  /** Its ring's, arc's, bar's, thermometer's or graph line's thickness, 25–400 %. */
  thickness?: number | null;
  trackColor?: string | null;
  /** 0–100 % (22 when absent). */
  trackOpacity?: number | null;
  barStyle?: OverlayStyle['barStyle'] | null;
  /** A graph's area opacity, 0–100 % (43 when absent). */
  fillOpacity?: number | null;
}

/** A box of readings on a monitor; x and y are shares of the room the panel leaves (0 left/top, 1 right/bottom). */
export interface OverlayPanel {
  id: string;
  title: string | null;
  x: number;
  y: number;
  layout: 'rows' | 'line' | 'tiles';
  readings: OverlayReading[];
  style: OverlayStyle;
  hidden?: boolean;
  /** Columns of the tiles grid (1–6). */
  columns?: number;
  /** Columns of readings in the rows layout (1–4). */
  rowColumns?: number;
  /** Fixed size at 100 % scaling; absent: as large as the readings need (and never shrinking while shown). */
  width?: number | null;
  height?: number | null;
  /** Its place and size before it was maximized, put in a grid cell or fitted to the screen. */
  restore?: OverlayPanelPlace | null;
  /** Can be dragged on the screen itself (a handle shows when the mouse rests on it). */
  movable?: boolean;
  /** Can be resized on the screen itself (a corner handle). */
  resizable?: boolean;
  /** Can be closed on the screen itself (hidden until shown again). */
  closable?: boolean;
  /** Animated characters playing on, in or around it (at most 8). */
  characters?: OverlayCharacter[] | null;
  /** The ready-made design it was made from (it can be reset to it). */
  design?: string | null;
}

/** Where a character plays: on the panel's top edge, inside it, beside it, along its screen's bottom, or all around it. */
/** A character's clip of a new animation: queued, running, done or failed, its file once made, and how it is coming along. */
export interface AnimationClip {
  state: 'queued' | 'running' | 'done' | 'failed';
  file?: string | null;
  step?: string | null;
  progress?: number;
  error?: string | null;
  /** Played (false: this character does the closest it has; its clip kept). */
  active?: boolean;
}

/** A new animation made by the character generator from a prompt for every character (what a character does, as an action). */
export interface CharacterAnimation {
  id: string;
  name: string;
  prompt: string;
  created: string;
  clips: Record<string, AnimationClip>;
  /** animation (something it does) or emotion (a face and a mood), as the recorder judges it. */
  kindOf?: 'animation' | 'emotion';
  /** Played (false: the characters do the closest they have; its clips kept). */
  active?: boolean;
}

/** The new animations, and the characters there are now. */
export interface AnimationsAnswer {
  animations: CharacterAnimation[];
  characters: string[];
}

export type CharacterPlace = 'top' | 'inside' | 'left' | 'right' | 'screen' | 'around' | 'spot' | 'wheel' | 'needle' | 'bar' | 'graph';

/** How it moves: staying in place, pacing from end to end, or wandering. */
export type CharacterMovement = 'stay' | 'pace' | 'wander';

/** An animated character of a panel: a shipped one or one the user imported. */
export interface OverlayCharacter {
  id: string;
  /** Which character: a shipped one's id, or an imported one's (a ".char" file of the picture library). */
  character: string;
  place: CharacterPlace;
  movement: CharacterMovement;
  /** What it does when staying or pausing (idle, sit, sleep, play, jump, dance), or how it moves (walk, run). */
  action: string;
  /** Where it stays along its place, 0–1. */
  x: number;
  /** How tall it stands, in pixels at 100 % (24–240). */
  size: number;
  /** How fast it moves, 25–300 %. */
  speed: number;
  /** Faces left when staying. */
  flip: boolean;
  /** Behind the panel (outside it only). */
  behind: boolean;
  /** 20–100 %. */
  opacity: number;
  /** The animations it uses (absent or null: all); without walk and run it stays. */
  plays?: string[] | null;
  /** How fast its animations play, 25–300 % (absent: as fast as it moves). */
  tempo?: number | null;
  /** How long it stops between walks, 25–400 % (absent: 100). */
  pause?: number;
  /** Its own reactions for a reading (by source), over `reactions`: when that reading is the one it reacts to. */
  reactionsBy?: Record<string, Partial<Record<'low' | 'normal' | 'high' | 'critical', { action?: string | null; effect?: string | null; prop?: string | null }>>> | null;
  /** The readings it does not react to (sources). */
  ignore?: string[] | null;
  /** The readings it reacts to first (sources, the first first; the rest after, as on the panel). */
  priority?: string[] | null;
  /** Where across its screen it plays (0 top, 1 bottom), at the spot it was dropped (place 'spot'). */
  y?: number;
  /** It can be dragged on the screen itself: dropped, it stays there (or keeps to an area around it). */
  draggable?: boolean;
  /** It notices the mouse on a monitor: watches it, comes over when it rests near it, enjoys being petted (on when absent). */
  mouse?: boolean;
  /** It answers a click on a monitor (its window takes the mouse where it is drawn). */
  clickable?: boolean;
  /** The reading whose gauge a wheel or needle character plays on (null: the first ring, arc or speedometer of its panel). */
  gauge?: string | null;
  /** The area it keeps to along its place, 0–1 from its start to its end (absent: all of it). */
  from?: number;
  to?: number;
  /** Reacts to a reading: 'auto' (the panel's most urgent one), a reading's source, or null (it does not). */
  react?: string | null;
  /** Its own reactions in place of its reading kind's, by status: what it does ('default': its own), the effect and the prop ('none': none). */
  reactions?: Partial<Record<'low' | 'normal' | 'high' | 'critical', { action?: string | null; effect?: string | null; prop?: string | null }>> | null;
}

/** A character the panels may have (GET /overlay/characters). */
export interface CharacterInfo {
  id: string;
  name: string;
  /** Comes with PC Diagnostic (otherwise imported). */
  shipped: boolean;
  /** What it does (its clips); the other actions play the closest of these. */
  actions: string[];
  /** A panel has it. */
  used: boolean;
  /** On this PC; otherwise only in another storage location (brought here when it is first shown). */
  here: boolean;
  added: string | null;
}

/** A panel's place and size to go back to. */
export interface OverlayPanelPlace {
  x: number;
  y: number;
  width: number | null;
  height: number | null;
  /** Its text size. */
  size: number;
}

export interface OverlayScreen {
  key: string;
  name: string | null;
  /** Its panels (while it is wider than tall, and always when it has no portrait panels). */
  panels: OverlayPanel[];
  /** Its panels while it is taller than wide; absent: the same as `panels`. */
  portrait?: OverlayPanel[] | null;
  /** Its own characters, apart from any panel's: along its bottom ('screen') or its top ('top'). */
  characters?: OverlayCharacter[] | null;
}

/** The on-screen readings layout (overlay.json). */
export interface OverlayConfig {
  version: number;
  enabled: boolean;
  hotkey: string;
  intervalMs: number;
  hideFromCapture: boolean;
  screens: OverlayScreen[] | null;
  /** Moving backgrounds move (off: still). */
  motion?: boolean;
  /** No longer used: a full-screen game keeps nothing still (kept so older layouts read). */
  pauseMotionInGames?: boolean;
  /** The panels' animated characters are shown (off: none, whatever each panel has). */
  characters?: boolean;
  /** A new value glides there (numbers, bars, rings, dials) instead of jumping (on when absent). */
  smoothValues?: boolean;
  /** Starts and ends arranging the panels on the screens themselves. */
  arrangeHotkey?: string;
  /** USB screens' settings (screens that are not Windows monitors), by their key. */
  devices?: OverlayDevice[] | null;
  /** The place whose weather the characters show (absent or null: none, nothing is fetched). */
  weather?: OverlayWeather | null;
}

/** Mirrors PcDiag.Core.Overlay.OverlayWeather: a place searched by name (GET /overlay/weather/places). */
export interface OverlayWeather {
  place: string;
  latitude: number;
  longitude: number;
}

/** Mirrors PcDiag.Core.Overlay.WeatherReport: the weather now at the chosen place (MET Norway's forecast). */
export interface WeatherReport {
  at: string;
  place: string;
  latitude: number;
  longitude: number;
  /** MET Norway's symbol for the next hour ("rain", "clearsky_day"). */
  symbol: string;
  /** °C and m/s. */
  temperature: number;
  wind: number;
  expires?: string | null;
  /** clear, clouds, fog, rain, snow or storm. */
  kind: string;
  day: boolean;
}

/** GET /overlay/weather. */
export interface WeatherAnswer {
  report: WeatherReport | null;
}

/**
 * A USB screen's settings: an LCD panel in a case or on a cooler, driven by the recorder. While it is used, the program
 * that holds it otherwise (L-Connect for Lian Li's) is paused.
 */
export interface OverlayDevice {
  key: string;
  use: boolean;
  /** Degrees clockwise; -1 as its model is usually mounted. */
  rotation: number;
  /** Its panels' size, 50–300 %. */
  zoom: number;
  /** Backlight, 0–100 %. */
  brightness: number;
  /** Pictures a second its moving backgrounds and gliding values send, 1–30 (30 when absent; at most its model's). */
  motionFps?: number;
  /** The color behind its panels. */
  background: string;
}

/** A screen now: a monitor in desktop pixels, or a USB screen (not on the desktop). */
export interface OverlayDisplay {
  key: string;
  name: string;
  number: number;
  x: number;
  y: number;
  width: number;
  height: number;
  scale: number;
  primary: boolean;
  refreshHz: number | null;
  /** "device" for a USB screen. */
  kind?: 'monitor' | 'device';
  /** A USB screen's state: on, available, busy (another program holds it) or a problem. */
  status?: 'available' | 'busy' | 'on' | 'problem' | null;
  /** More about it: the program holding it, the problem, its firmware. */
  detail?: string | null;
  model?: string | null;
  /** How far Windows turns its picture (0, 90, 180 or 270°). */
  rotation?: number;
  /** Its shape now: "portrait" while it is taller than wide. */
  orientation?: 'landscape' | 'portrait';
}

/** A USB screen driver package. */
export interface ScreenDriverInfo {
  id: string;
  name: string;
  version: string;
  publisher: string;
  builtIn: boolean;
  /** What an approval names (null for a built-in one). */
  sha256: string | null;
  approved: boolean;
  loaded: boolean;
  problem: string | null;
  models: string[];
  /** What it is for: "screen", "sensors". */
  capabilities?: string[] | null;
  /** The sensors it reads (their names). */
  sensors?: string[] | null;
}

/** A value a panel can show, with its reading now. */
export interface OverlaySource {
  id: string;
  label: string;
  group: string;
  unit: string;
  value: number | null;
  text?: string | null;
  /** Its total when it has one (VRAM, memory in use), for percentages and "of". */
  max?: number | null;
  /** A text's space: the widest texts it reads, or a width in zeros for a free text (shortened with "…" to fit). */
  room?: string[] | null;
  /** Not read yet (the sensors start a few seconds after the recorder): its name as last seen, a loading mark for its value. */
  loading?: boolean;
}

export interface OverlayState {
  /** The panels are being arranged on the screens themselves. */
  arranging?: boolean;
  /** The saved layout's revision (it changes when the layout does, also when arranged on the screens). */
  revision?: string | null;
  running: boolean;
  visible: boolean;
  problem: string | null;
  displays: OverlayDisplay[];
  sources: OverlaySource[];
}

/** GET /api/v1/overlay. */
export interface OverlayResponse {
  /** The saved layout's revision. */
  revision?: string | null;
  config: OverlayConfig;
  state: OverlayState | null;
  fileProblem: string | null;
  recorderProblem: string | null;
  /** The ready-made looks, by id (the recorder's own list). */
  presets?: Record<string, OverlayStyle>;
}

/** How the space for PC Diagnostic's data stands, worst last. */
export type StorageCondition = 'Normal' | 'OverLimit' | 'LowSpace' | 'WritesPaused';

export interface StorageNotice {
  condition: StorageCondition;
  message: string;
}

export type StorageActionKind = 'incidents-removed' | 'full-detail-shortened' | 'summaries-shortened' | 'compacted' | 'writes-paused' | 'writes-resumed';

/** One clean-up (repeated automatic ones within an hour are one entry). */
export interface StorageAction {
  at: string;
  kind: StorageActionKind;
  detail: string;
  freedBytes: number;
  automatic: boolean;
  count: number;
}

export interface StorageState {
  condition: StorageCondition;
  message: string | null;
  checkedAt: string | null;
  freeBytes: number | null;
  usedBytes: number | null;
  lastPassAt: string | null;
  recent: StorageAction[];
}

export type StorageKindId = 'incidents' | 'telemetry' | 'index' | 'logs' | 'diagnostics' | 'reports' | 'other';

export interface StorageKindUsage {
  id: StorageKindId;
  bytes: number;
  files: number;
}

export interface StorageIncident {
  key: string;
  title: string;
  centerAt: string;
  state: IncidentState;
  source: IncidentSource;
  userType: UserIncidentType | null;
  severity: Severity;
  pinned: boolean;
  snapshotOnly: boolean;
  bytes: number;
}

export interface StorageLimitsView {
  dataLimitGb: number;
  keepFreeGb: number;
  maxIncidents: number;
  telemetryRetentionHours: number;
  rollupRetentionDays: number;
  fullDetailFloorHours: number;
  summariesFloorDays: number;
  pauseBelowBytes: number;
  resumeAboveBytes: number;
}

/** A kind of file the storage locations keep. */
export type FileKind = 'pictures' | 'characters' | 'drafts' | 'settings' | 'backups' | 'drivers' | 'diagnostics' | 'reports' | 'exports';

/** This PC (the data folder), a folder (another drive or a network share) or an S3-compatible service. */
export type LocationKind = 'pc' | 'folder' | 's3';

/** Where files are kept, and which kinds. */
export interface StorageLocation {
  id: string;
  name: string;
  kind: LocationKind;
  keeps: FileKind[];
  /** A folder's full path. */
  path?: string | null;
  /** An S3 service's address (none for AWS). */
  endpoint?: string | null;
  bucket?: string | null;
  region?: string | null;
  /** The folder inside (a folder location's default is "PcDiagnostic"). */
  prefix?: string | null;
  pathStyle: boolean;
  /** Where files are kept by default (it keeps every kind; This PC when no other is). */
  default?: boolean;
  /** PC Diagnostic Storage (SeaweedFS run on this PC), when this location is it. */
  local?: LocalServer | null;
}

/** PC Diagnostic Storage's folder and ports (S3 on 127.0.0.1 only), and whether it was stopped. */
export interface LocalServer {
  folder: string;
  s3Port: number;
  masterPort: number;
  volumePort: number;
  filerPort: number;
  adminPort: number;
  stopped: boolean;
}

/** Setting PC Diagnostic Storage up, and how far it is. */
export interface LocalStoreSetupState {
  running: boolean;
  step: string | null;
  done: number;
  problem: string | null;
  finished: string | null;
}

/** GET /api/v1/storage/local: PC Diagnostic Storage (SeaweedFS on this PC) as it is. */
export interface LocalStoreView {
  name: string;
  version: string;
  /** "shipped" (installed with PC Diagnostic), "downloaded", or null (downloaded once when set up). */
  program: 'shipped' | 'downloaded' | null;
  downloadBytes: number;
  defaultFolder: string;
  locationId: string | null;
  folder: string | null;
  port: number | null;
  running: boolean;
  stopped: boolean;
  problem: string | null;
  isDefault: boolean;
  setup: LocalStoreSetupState;
}

/** A location with what the browser may know of it: its keys only as set or not (and their last four characters). */
export interface StorageLocationView {
  location: StorageLocation;
  hasKeys: boolean;
  accessKeyEnd: string | null;
  online: boolean;
  problem: string | null;
}

/** The job bringing every location up to date. */
export interface StorageJob {
  running: boolean;
  done: number;
  total: number;
  copied: number;
  problem: string | null;
  finished: string | null;
}

/** GET /api/v1/storage/locations (and the answer of each change). */
export interface StorageLocationsResponse {
  locations: StorageLocationView[];
  job: StorageJob;
  kinds: FileKind[];
  /** Kinds This PC keeps always (the recorder reads them there). */
  onThisPc: FileKind[];
}

/** A location to test or add. */
export interface StorageLocationRequest {
  kind: Exclude<LocationKind, 'pc'>;
  name: string;
  keeps: FileKind[];
  path?: string;
  endpoint?: string;
  bucket?: string;
  region?: string;
  prefix?: string;
  pathStyle?: boolean;
  accessKey?: string;
  secretKey?: string;
}

/** A file kept in the storage locations. */
export interface StoredFile {
  kind: FileKind;
  name: string;
  size: number;
  updated: string;
  sha256?: string | null;
}

export interface StorageResponse {
  folder: string;
  drive: { name: string; totalBytes: number; freeBytes: number } | null;
  totalBytes: number;
  kinds: StorageKindUsage[];
  incidents: StorageIncident[];
  leftoverBytes: number;
  telemetry: { fullDetailFrom: string | null; summariesFrom: string | null; fileBytes: number; freeBytes: number } | null;
  limits: StorageLimitsView;
  recorder: StorageState | null;
  recent: StorageAction[];
  recorderProblem: string | null;
}

export interface StorageCleanResult {
  freedBytes: number;
  message: string;
  state: StorageState;
}

export interface StorageDeleteResult {
  deleted: string[];
  problems: { key: string; problem: string }[];
}

export type HardwareKind = 'device' | 'monitor' | 'volume' | 'network' | 'gpu' | 'memory' | 'cpu';

/** A piece of hardware the PC has; memory (GB) and the processor (threads) are compared by amount. */
export interface HardwareItem {
  key: string;
  kind: HardwareKind;
  name: string;
  detail?: string | null;
  amount?: number | null;
}

export interface ExpectedHardware {
  version: number;
  saved: string;
  items: HardwareItem[];
  incidentOnMissing: boolean;
}

export interface MissingHardware {
  item: HardwareItem;
  since: string;
  /** What is there instead, for amounts. */
  now?: number | null;
}

export interface HardwareStatus {
  checkedAt: string;
  expected: ExpectedHardware | null;
  current: HardwareItem[];
  missing: MissingHardware[];
  added: HardwareItem[];
}

export interface HardwareResponse {
  status: HardwareStatus | null;
  expected: ExpectedHardware | null;
  recorderProblem: string | null;
}

/** What the recorder cost in CPU over the last minute, by part (GET /recorder/cost). */
export interface RecorderCost {
  at: string;
  seconds: number;
  usedMs: number;
  sharePercent: number;
  /** Each part's CPU time in ms, most first ("other": what no part counted). */
  parts: { part: string; ms: number }[];
}

export interface RecorderCostResponse {
  cost: RecorderCost | null;
  problem: string | null;
}

/** A connected device Windows has no working driver for. */
export interface DeviceWithoutDriver {
  instanceId: string;
  name: string;
  class: string | null;
  problemCode: number;
  problem: string;
  hardwareIds: string[];
  compatibleIds: string[];
}

/** A Windows driver package imported for such a device. */
export interface WindowsDriverInfo {
  id: string;
  inf: string;
  provider: string | null;
  version: string | null;
  date: string | null;
  class: string | null;
  /** Signed, NotSigned, NotTrusted or Unknown. */
  signature: string;
  signer: string | null;
  hardwareIds: string[];
  /** The devices without a driver it fits (instance ids). */
  matches: string[];
  /** Its name in Windows' driver store once installed here (oemNN.inf). */
  installed: string | null;
  problem: string | null;
}

export interface WindowsDriversState {
  devices: DeviceWithoutDriver[];
  packages: WindowsDriverInfo[];
  /** What the last install did. */
  message: string | null;
}
