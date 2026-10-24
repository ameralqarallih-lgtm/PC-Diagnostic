import type { AgentStatus, IncidentPage, LiveMessage, LiveSnapshot, StatusResponse } from '@/lib/api/types';

export const metric = (value: number | null, unavailable: string | null = null) => ({ value, unavailable: value == null ? (unavailable ?? 'Pending') : null });

export function agentStatus(overrides: Partial<AgentStatus> = {}): AgentStatus {
  return {
    version: '1.0.0',
    elevated: false,
    mode: 'Manual',
    state: 'Recording',
    pausedUntil: null,
    activeIncident: null,
    activeIncidentUntil: null,
    liveView: false,
    ...overrides,
  };
}

export function liveSnapshot(overrides: Partial<LiveSnapshot> = {}): LiveSnapshot {
  return {
    timestamp: new Date().toISOString(),
    recording: true,
    cpu: { totalPercent: metric(37.5), frequencyMhz: metric(4200), packageTemperatureC: metric(null, 'DriverMissing'), packagePowerW: metric(null, 'DriverMissing'), logicalProcessors: 16 },
    memory: { usedPercent: metric(55), usedBytes: metric(17 * 1024 ** 3), totalBytes: metric(32 * 1024 ** 3), commitPercent: metric(40), hardFaultsPerSec: metric(0) },
    gpus: [
      {
        key: 'gpu0',
        name: 'NVIDIA GeForce RTX 4090',
        utilizationPercent: metric(88),
        vramUsedBytes: metric(10 * 1024 ** 3),
        vramTotalBytes: metric(24 * 1024 ** 3),
        temperatureC: metric(71),
        hotspotC: metric(84),
        powerW: metric(380),
        coreClockMhz: metric(2730),
      },
    ],
    disks: [
      { name: 'Samsung SSD 990 PRO', volumes: 'C:', activePercent: metric(12), readBytesPerSec: metric(1024), writeBytesPerSec: metric(2048), temperatureC: metric(44) },
    ],
    networks: [
      { name: 'Ethernet', kind: 'Ethernet', up: true, receiveBytesPerSec: metric(5000), sendBytesPerSec: metric(1000), linkBitsPerSec: metric(1e9), wifiSignalPercent: metric(null, 'NotPresent') },
    ],
    processes: {
      count: 250,
      byCpu: [{ pid: 4242, name: 'game.exe', cpuPercent: 25.3, workingSetBytes: 2e9, privateBytes: 3e9, gpuPercent: 80, ioBytesPerSec: 1e6, notResponding: false }],
      byMemory: [{ pid: 4242, name: 'game.exe', cpuPercent: 25.3, workingSetBytes: 2e9, privateBytes: 3e9, gpuPercent: 80, ioBytesPerSec: 1e6, notResponding: false }],
      byGpu: [{ pid: 4242, name: 'game.exe', cpuPercent: 25.3, workingSetBytes: 2e9, privateBytes: 3e9, gpuPercent: 80, ioBytesPerSec: 1e6, notResponding: false }],
      byIo: [{ pid: 77, name: 'backup.exe', cpuPercent: 1, workingSetBytes: 1e8, privateBytes: 1e8, gpuPercent: null, ioBytesPerSec: 5e7, notResponding: true }],
    },
    recentEvents: [{ timestamp: new Date().toISOString(), source: 'windows', title: 'Display driver nvlddmkm stopped responding and recovered', category: 'Gpu', severity: 'High' }],
    display: { remoteSession: false, displays: ['DELL U2723QE: 3840×2160 @ 60 Hz'] },
    agent: { cpuPercent: metric(0.12), workingSetBytes: metric(90 * 1024 ** 2), tickMs: metric(11), ringFrames: 600, ringMegabytes: 54 },
    ...overrides,
  };
}

export function liveMessage(overrides: Partial<LiveMessage> = {}): LiveMessage {
  return { connected: true, detail: null, status: agentStatus(), live: liveSnapshot(), ...overrides };
}

export function statusResponse(connected = true): StatusResponse {
  return {
    product: 'PC Diagnostic & Incident Recorder',
    version: '0.1.0',
    serverTime: new Date().toISOString(),
    dataDirectory: 'C:\\Users\\me\\AppData\\Local\\PcDiagnostic',
    agent: connected ? { connected: true, detail: null, status: agentStatus() } : { connected: false, detail: 'The recorder Agent is not running.', status: null },
  };
}

export const emptyIncidents: IncidentPage = { total: 0, items: [] };

/** A controllable stand-in for the browser's EventSource. */
export class FakeEventSource {
  static readonly CONNECTING = 0;
  static readonly OPEN = 1;
  static readonly CLOSED = 2;
  static instances: FakeEventSource[] = [];

  readonly url: string;
  readyState = FakeEventSource.CONNECTING;
  private readonly listeners = new Map<string, ((event: MessageEvent<string>) => void)[]>();

  constructor(url: string) {
    this.url = url;
    FakeEventSource.instances.push(this);
  }

  addEventListener(type: string, listener: (event: MessageEvent<string>) => void) {
    this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener]);
  }

  close() {
    this.readyState = FakeEventSource.CLOSED;
  }

  emit(type: string, data?: unknown) {
    if (type === 'open') this.readyState = FakeEventSource.OPEN;
    const event = new MessageEvent(type, { data: data === undefined ? undefined : JSON.stringify(data) });
    for (const listener of this.listeners.get(type) ?? []) listener(event as MessageEvent<string>);
  }
}
