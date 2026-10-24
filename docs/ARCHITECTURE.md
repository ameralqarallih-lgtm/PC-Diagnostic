# PC Diagnostic & Incident Recorder — Architecture & Plan

Status: **Approved — Phases 1–9 implemented** (plus local Docker deployment)
Source requirements: [PROJECT.md](PROJECT.md)

> Product principle: *When something goes wrong, the application must preserve enough evidence to understand what happened.*

---

## 0. Refinements to the original plan

These are the changes I made to the plan in PROJECT.md, and why:

| # | Refinement | Why |
|---|---|---|
| R1 | **Three tiers: a headless Recorder Agent, a non-elevated `PcDiag.Web` host (ASP.NET Core), and a browser UI (React SPA).** The Agent and the Web host talk over a named pipe that only the current user can access. | The recorder has to keep running when the UI is closed, and it must not stall when the UI is busy rendering charts. If the UI crashes, no evidence is lost. The elevated Agent never exposes HTTP. |
| R2 | **The Agent is elevated through an on-demand Scheduled Task** ("Run with highest privileges"). The task has no logon trigger unless the user enables one: it is used to start the Agent elevated without a UAC prompt each time. A standard-user mode is also available. | CPU temperatures, SMART data, kernel ETW and per-process network all need admin rights. A scheduled task is less privileged than a LocalSystem service, and the user decides when it runs (see R13). The UI always runs non-elevated. |
| R3 | **Three storage tiers**: an in-memory ring buffer, a rolling `telemetry.db`, and **one self-contained SQLite file per incident**. | Capture reads from RAM, so it still works during a disk stall. An incident is a portable folder, which makes ZIP export, deletion and retention simple. Pruning the rolling DB never touches incidents. |
| R4 | **Crash/BSOD survival**: telemetry is flushed to disk every 5 s, plus a heartbeat and a clean-shutdown marker. On the next boot the Agent automatically creates an *"Unexpected shutdown"* incident from the persisted data. | A BSOD or hard freeze wipes RAM. Those are exactly the incidents the user cares about most. |
| R5 | **Global hotkey and tray menu (owned by the Agent)** for Capture Incident, the quick incident types, and "Open dashboard". | When the screen flickers during a full-screen game, the user can't reach a window. |
| R6 | **Display subsystem gets its own phase** (the original roadmap had no phase for display or services). | Screen flicker and artifacts are the headline use case. |
| R7 | **Storage and scheduler move into Phase 2**, so data is persisted from the first collector onward. | Every later phase can then be verified against real stored data. |
| R8 | **Deterministic scenario/replay testing** with `TimeProvider` and fake collectors, plus a `StressKit` tool that generates real load. | This makes anomaly and incident logic testable without having to break a real PC. |
| R9 | **Optional "Enhanced mode" ETW flight recorder**: Windows Performance Recorder in memory mode with its own capped profile (the GPU and desktop composition providers plus CPU scheduling and disk, at most 384 MB), saved compressed to `traces/incident.etl` on capture, plus a real-time kernel network/disk session for true per-process I/O. It is off by default. | This gives deep evidence for GPU/display problems, and costs nothing when disabled. |
| R10 | **Target .NET 10 (LTS)**, not .NET 8. | .NET 8 support ends Nov 2026. .NET 10 is the current LTS and is already installed here. |
| R11 | **The UI is a responsive React + TypeScript single-page app built with Vite and served as static files by `PcDiag.Web`**, in place of WPF. Node.js is needed only at build time and is never shipped. Next.js was considered and rejected: served from .NET it would have to be a static export, which switches off everything Next.js adds (SSR, server components, API routes, middleware) and forces query-string routes. | Keeps the app local-first, offline and low-overhead, with a modern responsive UI and all diagnostic logic in .NET. Vite builds faster and emits no inline scripts, so a strict CSP is easy. Real routes such as `/incidents/INC-…` work through an ASP.NET SPA fallback. |
| R12 | **Hardened loopback HTTP** (see §11): binds to 127.0.0.1 only, one-time token → cookie, Host/Origin checks, strict CSP. LAN/mobile access is a later opt-in only. | A local web server is reachable by any local process, so it must authenticate even on loopback. |
| R13 | **Recording is under user control**: Manual, Always, or Scheduled time windows, plus pause-for-N-minutes. Whether the Agent process runs (autostart at logon or on demand) is a separate setting from whether it records. | Requested by the user. Keeping the process alive but idle (no collectors) costs almost nothing and lets the tray, hotkey and UI start recording instantly. |

---

## 1. Product architecture

```
┌──────────────────── Browser UI (React + Vite SPA, responsive) ─────────────────────────┐
│ Dashboard │ History charts │ Incidents │ Incident investigation │ System │ Settings      │
│        TanStack Query (REST)  ·  EventSource SSE (live)  ·  ECharts  ·  widget grid      │
└──────────────────────────────────────────┬───────────────────────────────────────────────┘
               HTTP 127.0.0.1 only: /api/v1 REST + /api/v1/live/stream SSE (cookie)
┌──────────────────────────────────────────┴───────────────────────────────────────────────┐
│                    PcDiag.Web (ASP.NET Core / Kestrel, ALWAYS standard user)             │
│  Serves static UI (wwwroot) · REST API · live SSE stream · Report/Export generation      │
└───────────────┬──────────────────────────────────────────────────┬───────────────────────┘
      live feed + commands (named pipe, JSON, per-user ACL)   read-only SQLite (WAL readers)
┌───────────────┴──────────────────────────────────────────────────┴───────────────────────┐
│                         PcDiag.Agent (headless, Generic Host, elevated*)                 │
│  Hidden window (WM_DISPLAYCHANGE, WM_DEVICECHANGE, WM_POWERBROADCAST, hotkey) + tray icon│
│ ┌──────────────────────────────── Diagnostic Engine ──────────────────────────────────┐  │
│ │ Telemetry Engine  → SampleBus (bounded Channels) → Ring Buffer / Storage / Live / AE │  │
│ │ Event Engine      → classify → SystemEvent stream                                   │  │
│ │ Anomaly Engine    → rules (threshold, duration, baseline, ROC) → Anomaly / Trigger  │  │
│ │ Incident Engine   → lifecycle state machine, dedupe, capture, finalization          │  │
│ │ Snapshot Engine   → process/service/hardware/display/driver/system snapshots        │  │
│ │ Diagnostic Analyzer → Observations / Correlations / Possible explanations / Confirmed│  │
│ │ Report Engine (shared lib; invoked by UI for export)                                │  │
│ └──────────────────────────────────────────────────────────────────────────────────────┘ │
│                 Collectors (plugins) & Sensor Providers (abstracted)                     │
│                 Data Access Layer (Microsoft.Data.Sqlite + Dapper, batched writer)       │
└───────────────┬──────────────────────────────────────────────────────────────────────────┘
      PDH · NtQuerySystemInformation · IP Helper · WLAN · SCM · QueryDisplayConfig · DXGI
      D3DKMT · EvtSubscribe · SetupAPI/CfgMgr · WMI/CIM (inventory only) · NVML · LHM · ETW
```

*Elevated when the scheduled task is installed. Otherwise it runs in standard-user mode with reduced metrics, and those metrics are shown explicitly as `RequiresElevation`.

### Component responsibilities

| Component | Responsibility | Must not |
|---|---|---|
| **Collectors** | Read the OS once per tick and return typed samples, with an explicit availability for every value. | Hold history, raise incidents, or throw into the scheduler. |
| **Sensor providers** | Wrap LHM, NVML, D3DKMT, HWiNFO and similar sources behind `IHardwareSensorProvider`. They are merged by priority. | Leak library types into Core. |
| **Telemetry Engine** | Tiered scheduler: 1 s / 2 s / 10 s / on-change. Runs collectors in parallel with per-collector timeouts and overrun detection, then publishes to the SampleBus. | Block on a slow collector. |
| **Event Engine** | Event Log subscriptions, SCM notifications, display/device notifications, and process lifecycle. Classifies and dedupes events into `SystemEvent`. | Dump whole logs. |
| **Rolling Recorder** | In-memory ring buffer (configurable pre-window, max 30 min) plus a batched write-behind to `telemetry.db`, retention and 1-minute rollups. | Write each metric individually. |
| **Anomaly Engine** | Streams samples through configured rules and emits `Anomaly` records (with reasoning) and `IncidentTrigger`s. | Use ML (v1), or claim causality. |
| **Incident Engine** | Runs the lifecycle state machine: capture, post-window recording, snapshotting, finalization, merging of overlapping triggers, and the incident ID sequence. | |
| **Snapshot Engine** | Takes on-demand full snapshots: processes (all), services, hardware, drivers, display, system info. | |
| **Diagnostic Analyzer** | Rule-based analyzers that produce typed `Finding`s, with wording templates that enforce the evidence levels. | Present speculation as fact. |
| **Report Engine** | Produces HTML, PDF, CSV, JSON and ZIP from an incident folder. It runs in `PcDiag.Web`. | Run inside the elevated process. |
| **PcDiag.Web** | Serves the UI, provides the REST API and the live Server-Sent Events stream, reads history read-only from SQLite, relays commands to the Agent, stores the user's dashboard layout, and streams exports as browser downloads. | Collect telemetry, run elevated, or listen on anything other than loopback by default. |
| **Browser UI** | Shows state and sends commands through the API. All data comes from `PcDiag.Web`. | Hold any logic that decides what is an anomaly or a finding. |

---

## 2. Technology decisions

| Area | Choice | License | Notes / alternatives |
|---|---|---|---|
| Runtime | .NET 10 LTS, `net10.0-windows10.0.19041.0`, x64 | MIT | Nullable enabled, warnings treated as errors. |
| Web host | **ASP.NET Core** (Kestrel, minimal APIs) with **Server-Sent Events** (`TypedResults.ServerSentEvents`, .NET 10) | MIT | Loopback only. SSE replaced SignalR: the live feed is one-way, needs no client library (the browser's `EventSource`), reconnects by itself and fits the CSP `connect-src 'self'`. API types are hand-written mirrors checked by the Web tests. |
| UI framework | **React 19** + **TypeScript** + **Vite** | MIT | A plain SPA. `PcDiag.Web` serves the built files and falls back to `index.html` for client routes. |
| Routing | **TanStack Router** | MIT | Type-safe routes and **typed, validated URL search params**, so view state (`range`, `series`, `t`) lives in the URL and can be deep-linked. |
| Styling / components | **Tailwind CSS** + **shadcn/ui** (Radix primitives) | MIT | Responsive, accessible, themeable (dark by default). The component source is copied into the repo, so there's no runtime dependency on a registry. |
| Data fetching / live | **TanStack Query** + browser `EventSource` | MIT | |
| Charts and gauges | **Apache ECharts 6** (modular imports: gauge, line, effect scatter) | Apache-2.0 | Replaced uPlot: one library for animated gauges, streaming lines, zoom, markers and (Phase 12) large histories with LTTB sampling. Canvas rendering; no eval, CSP-compatible. |
| Animation | **Motion 14** (`motion/react`) | MIT | Transitions and effects; `MotionConfig reducedMotion="user"` honours the OS setting. |
| Dashboard layout | **react-grid-layout 2** + react-resizable | MIT | Drag, resize and per-breakpoint layouts for the customizable dashboard. |
| QR codes | **qrcode-generator 2** | MIT | The companion app download and pairing links in Settings (drawn as one SVG path). |
| Companion app | Android (Java, platform APIs only; AGP 8.5, Gradle 8.7, JDK 17) | — | Pairing, Find my PC, the dashboard in a pinned WebView, one-tap capture. |
| API client | `openapi-typescript` (+ `openapi-fetch`) | MIT | Types are generated from the .NET OpenAPI document, so the front and back ends can't drift apart. |
| Front-end tests | Vitest + React Testing Library, **Playwright** (e2e, desktop + mobile viewports) | MIT / Apache-2.0 | |
| Build toolchain | Node.js LTS (v24 installed) + npm, **build time only** | MIT | An MSBuild target runs `npm ci && npm run build` and copies `web/dist` into `wwwroot`. |
| Tray + hotkey | Windows Forms `NotifyIcon` + `RegisterHotKey` on a `NativeWindow` (in the Agent) | MIT (part of .NET) | Phase 9: chosen over H.NotifyIcon, a first-party, documented API with no extra dependency |
| Host/DI/config/logging | Microsoft.Extensions.Hosting, Options, Logging | MIT | |
| Logging sink | Serilog (+ File sink, rolling, size-capped) | Apache-2.0 | Structured JSON logs. |
| Win32 interop | Hand-written **`LibraryImport`** declarations for documented functions only (PDH, kernel32, powrprof), behind small safe wrappers in `PcDiag.Platform.Windows` | — | Explicit, AOT-friendly marshalling, and no generator dependency. CsWin32 (MIT) remains an option if the interop surface grows large. |
| SQLite | Microsoft.Data.Sqlite + **Dapper** | MIT / Apache-2.0 | No EF Core: we need hot-path control and batched prepared statements. |
| Event Log | `System.Diagnostics.Eventing.Reader` (EvtSubscribe/EvtQuery) | MIT | |
| ETW (Enhanced mode) | Hand-written `LibraryImport` for the documented ETW controller/consumer API (`StartTraceW`, `EnableTraceEx2`, `OpenTraceW`, `ProcessTrace`, `ControlTraceW`), plus `wpr.exe` from System32 for the flight recorder | — (part of Windows) | Phase 14: TraceEvent (MIT) was not needed for two providers with fixed payload offsets, so there is no extra dependency. Both need admin. |
| Hardware sensors | **LibreHardwareMonitorLib 0.9.6** (+ DiskInfoToolkit, RAMSPDToolkit-NDD: MPL-2.0; HidSharp: Apache-2.0) | **MPL-2.0** | Used unmodified as libraries. **Confirmed:** since 0.9.5 CPU MSR and Super I/O access uses the **PawnIO** driver, which is *not* in the NuGet package and must be installed separately (`winget install namazso.PawnIO`); the process must also be elevated. Without both, CPU and board readings are reported as `RequiresElevation`/`DriverMissing` (LHM itself would return misleading zeros). |
| NVIDIA | NVML (`nvml.dll` ships with the driver), P/Invoke | NVIDIA driver EULA | Loaded dynamically. Absent means Unavailable. |
| AMD | ADLX (optional, Phase 6+) | MIT (ADLX SDK) | Otherwise the fallback is LHM plus D3DKMT. |
| Intel GPU | D3DKMT + PDH; LHM for integrated graphics power. Intel's control library (IGCL), which LHM uses for Arc cards, is loaded only when an Intel discrete GPU is present. | — | |
| HWiNFO | Optional shared-memory reader provider | HWiNFO terms | The user must enable "Shared Memory Support" (in the free edition this lapses after 12 h). Commercial redistribution needs an agreement with REALiX. Off by default. |
| Report templates | Scriban | BSD-2 | |
| File storage | **AWSSDK.S3** (S3-compatible services, Phase 43); System.Security.Cryptography.ProtectedData (keys under DPAPI) | Apache-2.0 / MIT | In the Web host only; checksums and chunked signing only when a service requires them. |
| PDF | **Headless Microsoft Edge** (`--headless --print-to-pdf`) on the generated HTML, run by `PcDiag.Web` | Uses the Edge already on the system | One source for HTML and PDF. The Edge path comes from its known install location, the arguments are fixed, and the input/output are generated temp files. If Edge is missing, the fallback is a print stylesheet plus the browser's "Save as PDF". QuestPDF was rejected because of its revenue-based license. |
| Tests | xUnit, NSubstitute, Shouldly, Verify (snapshot) | Apache / BSD / BSD / MIT | FluentAssertions v8+ is commercially licensed, so avoid it. Moq was rejected (SponsorLink history). |
| Packaging | Unpackaged installer: **Inno Setup 7.1** (pinned, checked by SHA-256 and signature, unpacked in its portable mode by `installer\build.ps1`) | Inno Setup License: free, also commercially | Phase 15. WiX 6+ was rejected: since April 2025 it carries the Open Source Maintenance Fee for organisations with revenue. MSIX is a poor fit for an elevated background agent. Inno asks commercial users to buy a commercial license (requested, not required): see the commercial checklist. |

### 2a. Dependency licensing policy (the project is GPL-3.0)
- **Allowed without review:** MIT, Apache-2.0, BSD, ISC, MS-PL. **MPL-2.0** only for LibreHardwareMonitorLib and its MPL-2.0 dependencies (DiskInfoToolkit, RAMSPDToolkit-NDD), used unmodified as separate assemblies. If we ever modify their files, we must publish those files' source.
- **PawnIO (sensor driver), exception by aggregation:** the user must not install anything separately (requirement 2026-10-06), so the installer ships the **unmodified** `PawnIO_setup.exe` and runs it silently (`-install -silent`). PawnIO is GPL-2.0 with an exception for independent programs that use it through its device-control interface, which is how LibreHardwareMonitor uses it; our code never links to it, so this is aggregation of a separate program (FanControl ships it the same way). Obligations: GPL-2.0 text and a link to the PawnIO source in THIRD-PARTY-NOTICES, and no modification. The author offers alternative licensing, to be confirmed before a commercial release (see the checklist).
- **Fonts under the SIL Open Font License (OFL-1.1)** are allowed for the dashboard's bundled fonts (`@fontsource-variable/inter`, `@fontsource-variable/jetbrains-mono`): the license allows bundling them with any software, commercial included, and forbids only selling the fonts on their own.
- **Public-domain dedications** (Unlicense, CC0) count as permissive (the dashboard bundles `isbot`, Unlicense, through TanStack Router).
- **Not allowed:** licenses that cannot be combined with the GPL-3.0 in one program (GPL-2.0-only code linked in, SSPL) and "free below revenue X" or non-commercial licenses (e.g. QuestPDF, FluentAssertions 8+). The build keeps its list of allowed licenses short on purpose: a new one is reviewed before it is added.
- **Never redistributed:** `nvml.dll` (it ships with the NVIDIA driver). HWiNFO integration stays optional, off by default, and is not bundled; commercial use needs an agreement with REALiX.
- **Enforced at build:** `installer\Generate-Notices.ps1` (run by the installer build) reads every NuGet package the Agent and Web host resolve and every npm package bundled into the dashboard, quotes each package's own license file (or the standard SPDX text from `installer\licenses`), adds the bundled .NET runtime, SQLite, PawnIO (GPL-2.0 text and source links) and Inno Setup, and **fails the build** on a license this policy does not allow. The result, `THIRD-PARTY-NOTICES.txt`, is installed next to the program (93 components at Phase 15).
- **Release checklist:**
  - the characters' weather (opt-in): MET Norway's terms want a contact (a domain or an email) in the User-Agent, which now names only the app (`PcDiagnostic/<version>`); add the project's site.
  - a code-signing certificate (Authenticode, required for SmartScreen trust; `installer\build.ps1 -CertificateThumbprint` signs our programs and the installer, timestamped), a release key for the Android app (`android\build.ps1 -Release`, key kept outside the repository), privacy policy text, and confirmation of LHM's driver licensing (PawnIO) at that time.

---

## 3. Solution structure

```
PcDiagnostic.sln
Directory.Build.props            (net10.0-windows, Nullable, TreatWarningsAsErrors, analyzers)
Directory.Packages.props         (central package versions)
src/
  PcDiag.Core/                   Domain models, interfaces, options, evidence model. No Win32.
    Telemetry/  Events/  Incidents/  Anomalies/  Diagnostics/  Sensors/  Abstractions/
  PcDiag.Platform.Windows/       All Win32/PDH/NT/IPHLP/SCM/DisplayConfig/DXGI/D3DKMT interop (LibraryImport)
    Pdh/  Kernel/  (later: Nt/  Display/  Gpu/  Net/  Storage/)
  PcDiag.Collectors/             Collector implementations (use Platform.Windows)
    Cpu/ Memory/ Gpu/ Disk/ Network/ Process/ Service/ Display/ EventLog/ Inventory/
  PcDiag.Sensors.LibreHardwareMonitor/   IHardwareSensorProvider adapter (isolates MPL lib)
  PcDiag.Sensors.Nvml/                   NVML provider
  PcDiag.Sensors.Native/                 D3DKMT / WMI thermal / storage reliability provider
  PcDiag.Engine/                 Scheduler, SampleBus, RingBuffer, Anomaly, Incident, Snapshot,
                                 Diagnostic analyzers, retention, self-monitoring
  PcDiag.Storage.Sqlite/         Schema + migrations, batched writer, readers, incident store
  PcDiag.Reporting/              HTML/CSV/JSON/ZIP/PDF generation (used by PcDiag.Web)
  PcDiag.Ipc/                    Pipe contracts (System.Text.Json source-gen), client/server
  PcDiag.Hosting/                Shared Agent/Web bootstrap: settings.json, validated options, DataPaths,
                                 storage registration, database initialization, Serilog
  PcDiag.Agent/                  Generic Host exe, hidden message window, tray, hotkey, task setup
  PcDiag.Web/                    ASP.NET Core host: static UI (wwwroot), /api/v1, live SSE stream,
                                 loopback auth, exports. MSBuild target builds web/ → wwwroot
                                 (skip with -p:SkipWebBuild=true)
web/                             React + Vite SPA (TypeScript)
  src/routes/                    / · /history · /incidents · /incidents/$key · /system · /settings
  components/                    ui/ (shadcn-style), charts/ (ECharts wrapper), layout/
  lib/api/                       generated OpenAPI types + typed fetch client
  features/live/                 EventSource live feed + chart history hooks
  e2e/                           Playwright tests
tests/
  PcDiag.Core.Tests/             Models, evidence wording rules
  PcDiag.Engine.Tests/           Ring buffer, anomaly rules, incident state machine (fake clock)
  PcDiag.Storage.Tests/          Integration: schema, batching, retention, incident copy
  PcDiag.Collectors.IntegrationTests/   Run against the real machine; skip gracefully if unsupported
  PcDiag.Scenarios.Tests/        Scripted replay scenarios (High GPU, TDR, crash, missing sensors…)
  PcDiag.Web.Tests/              API + auth tests (WebApplicationFactory, fake Agent)
tools/
  PcDiag.StressKit/              CPU burn, RAM hog, disk saturate, hang-window app, crash app
                                 (for manual/soak tests; not shipped)
installer/                       build.ps1, PcDiagnostic.iss, RecorderTask.ps1, Generate-Notices.ps1,
                                 Make-Icon.ps1, licenses/ (SPDX texts)
```

Projects are created in the phase that first gives them content (no empty shells). Phase 1 contains Core, Storage.Sqlite, Hosting, Agent, Web, `web/` and three test projects.

Dependencies point one way only: `web (browser) → HTTP → Web → Ipc, Reporting, Storage(read), Core`. `Agent → Engine → Collectors → Platform.Windows → Core`. Core depends on nothing.

---

## 4. Core abstractions (sketch)

```csharp
// Every value carries explicit availability — never a silent 0.
public enum Availability : byte { Available, Unsupported, RequiresElevation, NotPresent, Error, Pending }
public readonly record struct Reading<T>(T? Value, Availability Availability) where T : struct;

public interface ICollector                       // plugin base
{
    string Id { get; }                            // "cpu", "gpu.pdh", "sensors.lhm"
    CollectorInfo Describe();                     // name, required privilege, cost class
    ValueTask<CollectorStatus> InitializeAsync(CancellationToken ct);  // probes capabilities
}
public interface IPolledCollector<TSample> : ICollector
{
    SamplingTier Tier { get; }                    // Fast(1s) | Process(2s) | Inventory(10s)
    ValueTask<TSample> CollectAsync(SampleContext ctx, CancellationToken ct);
}
public interface IEventCollector : ICollector     // push-based
{
    IAsyncEnumerable<SystemEvent> ReadEventsAsync(CancellationToken ct);
}

// Spec-named specializations (thin marker interfaces for DI/discovery)
public interface ICpuCollector     : IPolledCollector<CpuSample> {}
public interface IMemoryCollector  : IPolledCollector<MemorySample> {}
public interface IGpuCollector     : IPolledCollector<GpuSample> {}      // one per tick, all adapters
public interface IDiskCollector    : IPolledCollector<DiskSample> {}
public interface INetworkCollector : IPolledCollector<NetworkSample> {}
public interface IProcessCollector : IPolledCollector<ProcessSample> {}
public interface IServiceCollector : ICollector, IEventCollector { Task<ServiceSnapshot> SnapshotAsync(CancellationToken ct); }
public interface IDisplayCollector : ICollector, IEventCollector { Task<DisplaySnapshot> SnapshotAsync(CancellationToken ct); }
public interface ISensorCollector  : IPolledCollector<SensorSample> {}  // merges providers

public interface IHardwareSensorProvider          // LHM, NVML, D3DKMT, HWiNFO…
{
    string ProviderId { get; }
    int Priority { get; }                         // higher wins when two report same logical sensor
    ValueTask<HardwareSnapshot> GetSnapshotAsync(CancellationToken ct);
}

public interface IIncidentManager
{
    Task<Incident> CreateIncidentAsync(IncidentTrigger trigger, CancellationToken ct);
    IObservable<IncidentStateChanged> StateChanges { get; }
}

public interface IAnomalyRule                     // configured, not hard-coded
{
    string RuleId { get; }
    AnomalyEvaluation Evaluate(in SampleFrame frame, RuleState state);  // returns reasoning
}

public interface IDiagnosticAnalyzer
{
    IEnumerable<Finding> Analyze(IncidentEvidence evidence);
}
public enum EvidenceLevel { Observation, Correlation, PossibleExplanation, ConfirmedEvent }
```

Collectors are discovered by DI registration (`services.AddCollector<T>()`). New plugins such as Battery, USB or Audio just add a new registration and a sample type. Unknown sample types are persisted generically (see `ExtensionMetrics` in §7).

---

## 5. Windows API mapping

Legend for **Admin**: ✅ works as a standard user, 🔒 requires admin, ◐ partial without admin.
Legend for **Reliability**: **A** always available, **B** usually available, **C** hardware/driver dependent (may be Unavailable).

### CPU
| Metric | Source | Admin | Rel. |
|---|---|---|---|
| Total / per-core usage (as Task Manager shows it) | PDH `\Processor Information(*)\% Processor Time` (until 25 Oct 2026 `% Processor Utility`, which counts work against the base clock and ran well above Task Manager while the CPU boosted; Windows 11 Task Manager moved to `% Processor Time` in 2025) | ✅ | A |
| User / kernel % | PDH `% User Time`, `% Privileged Time` | ✅ | A |
| Effective frequency | PDH `Processor Frequency` × `% Processor Performance` / 100 (per core; important for hybrid P/E cores) | ✅ | A |
| Max frequency | `CallNtPowerInformation(ProcessorInformation)` → `MaxMhz` | ✅ | A |
| Throttling indicators | PDH `% Performance Limit`, `Performance Limit Flags` | ✅ | B |
| "Load average" analog | PDH `\System\Processor Queue Length` (Windows has no load average; labelled accordingly) | ✅ | A |
| Context switches / interrupts | PDH `\System\Context Switches/sec`, `\Processor Information(_Total)\Interrupts/sec` | ✅ | A |
| Package temperature, package power | LHM (MSR / RAPL via kernel driver) | 🔒 | C |
| Power plan / AC vs battery | `PowerGetActiveScheme`, `GetSystemPowerStatus` | ✅ | A |

### Memory
| Metric | Source | Admin | Rel. |
|---|---|---|---|
| Total / available / load | `GlobalMemoryStatusEx` | ✅ | A |
| Commit / commit limit / paged & non-paged pool / system cache | `GetPerformanceInfo` | ✅ | A |
| Free (zero + free lists) | PDH `\Memory\Free & Zero Page List Bytes` | ✅ | A |
| Page faults, hard faults, paging | PDH `\Memory\Page Faults/sec`, `Pages Input/sec`, `Pages/sec` | ✅ | A |
| Compressed memory | PDH `\Memory\Compressed…` if present | ✅ | B |
| Memory pressure | **Derived**: available %, commit ratio, hard-fault rate (formula shown in UI) | — | A |

### GPU (multi-adapter; the adapter is keyed by LUID)
| Metric | Source | Admin | Rel. |
|---|---|---|---|
| Per-engine & per-process utilization (3D, Compute, VideoDecode/Encode, Copy) | PDH `\GPU Engine(*)\Utilization Percentage` (instance `pid_X_luid_…_engtype_Y`), aggregated like Task Manager | ✅ | A (WDDM 2.0+) |
| Per-process GPU memory | PDH `\GPU Process Memory(*)\Dedicated Usage`, `Shared Usage` | ✅ | A |
| Adapter VRAM usage (system-wide) / total | PDH `\GPU Adapter Memory(*)\Dedicated Usage`, `Shared Usage`; DXGI `DXGI_ADAPTER_DESC1.DedicatedVideoMemory` for the total. (`IDXGIAdapter3::QueryVideoMemoryInfo` is **per process** and is not used; see the spike note.) | ✅ | A |
| Temperature, fan, memory clock, power % (vendor-neutral) | `D3DKMTQueryAdapterInfo` type **62** (ADAPTERPERFDATA, 64 bytes) plus type 63 caps (max/warning temperature). **Verified** against nvidia-smi; see [spike](spikes/2026-09-28-gpu-apis.md). An all-zero answer (e.g. Intel iGPU) means *not reported*. | ✅ | C |
| Engine clock (vendor-neutral) | Type **61** (NODEPERFDATA), node 0. The OS accepts **56 bytes**, not the documented 64. **Verified** against nvidia-smi. | ✅ | C |
| NVIDIA temp / clocks / power (W) / power limit / clock-event reasons | NVML (`System32\nvml.dll` only): `nvmlDeviceGetTemperature`, `GetClockInfo`, `GetPowerUsage`, `GetEnforcedPowerLimit`, `GetCurrentClocksEventReasons` (falls back to `…ThrottleReasons`). Matched to DXGI adapters by PCI device id, then bus number. | ✅ | B (NVIDIA only) |
| GPU hotspot temperature | LHM (vendor-specific paths, NVIDIA via NVAPI) / ADLX for AMD | ◐ | C — often Unavailable |
| AMD metrics | ADLX (optional) → LHM → D3DKMT | ✅/🔒 | C |

Virtual twins are dropped (7 Oct 2026): DXGI lists WSL2's GPU paravirtualization adapter as a second copy of the real card (same vendor, device and subsystem ids; no PCI location, D3DKMT type 6 answers bus/device/function all ones; a new LUID on every start). Recorded as a second GPU it showed the real card's clocks and temperatures with none of its load, and the analyzers then said "utilization stayed low (up to 0 %)" and "no clock drop under load" next to the real card's clock drop. An adapter without a PCI location is now skipped when a twin with a real one exists (two real identical cards both have PCI locations and stay separate; a GPU with no PCI location and no twin is kept), in the GPU and display collectors alike, and its LUID in PDH does not trigger adapter refreshes.

### Disk
| Metric | Source | Admin | Rel. |
|---|---|---|---|
| Throughput, IOPS, latency (read/write), queue length, active time | PDH `\PhysicalDisk(*)\Disk Read Bytes/sec`, `Disk Reads/sec`, `Avg. Disk sec/Read`, `Current Disk Queue Length`, `% Idle Time` (active = 100 − idle) | ✅ | A |
| Capacity / free per volume | `GetDiskFreeSpaceEx`, volume→disk mapping via `IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS` | ✅ | A |
| Bus type (NVMe/SATA/USB), media type, health status | CIM `root\Microsoft\Windows\Storage` `MSFT_PhysicalDisk` (inventory, 10 min / on change) | ✅ | A |
| Temperature, wear, error counts | `MSFT_StorageReliabilityCounter` | 🔒 | B |
| NVMe SMART/health log | `IOCTL_STORAGE_QUERY_PROPERTY` (`StorageDeviceProtocolSpecificProperty`, NVMe health info log) | 🔒 | B |
| SATA SMART | `root\wmi MSStorageDriver_FailurePredictData` / LHM | 🔒 | C (USB bridges often block SMART) |
| Per-process I/O | `NtQuerySystemInformation` per-process I/O counters (**includes non-disk I/O**, labelled "I/O", not "disk") | ✅ | A |
| True per-process disk I/O | ETW `Microsoft-Windows-Kernel-Disk` reads and writes, attributed to the process in the event header (Enhanced mode) | 🔒 | B |

### Network
| Metric | Source | Admin | Rel. |
|---|---|---|---|
| Bytes/packets/errors/discards, link speed, type, oper status | `NetworkInterface.GetIPStatistics()` (GetIfEntry2) per interface; `GetIfTable2` `InterfaceAndOperStatusFlags` to **exclude NDIS filter layers**, which otherwise count the same traffic several times | ✅ | A |
| Utilization | Derived: bps / link speed | — | A |
| Connection changes | `NotifyIpInterfaceChange`, `NotifyUnicastIpAddressChange` | ✅ | A |
| Wi-Fi signal (RSSI → quality) | Native WLAN `WlanQueryInterface(wlan_intf_opcode_rssi)`, which is not location-restricted. `current_connection` (SSID) is location-restricted and can show a system prompt, so it is used **only when the user opts in**; denied → `PermissionDenied`. | ✅ | B |
| Per-process connection count | `GetExtendedTcpTable` / `GetExtendedUdpTable` | ✅ | A |
| Per-process bandwidth | ETW `Microsoft-Windows-Kernel-Network` (Enhanced mode) | 🔒 | B |

### Processes
| Data | Source | Admin | Rel. |
|---|---|---|---|
| Bulk per-tick stats (CPU times, WS, private, commit, threads, handles, I/O, priority, create time, parent PID) | **`NtQuerySystemInformation(SystemProcessInformation)`**: one call covers all processes, into a reused buffer (partially documented in `winternl.h`; fallback is `System.Diagnostics.Process`) | ✅ | A |
| Path | `QueryFullProcessImageName` with `PROCESS_QUERY_LIMITED_INFORMATION` | ◐ (protected processes → name only) | B |
| Command line (opt-in, privacy) | `NtQueryInformationProcess(ProcessCommandLineInformation)` | ◐ | B |
| User (opt-in, privacy) | `OpenProcessToken` + `GetTokenInformation(TokenUser)` | ◐ | B |
| Signature | `WinVerifyTrust`: the file's own Authenticode signature, else the system catalogs (`CryptCATAdmin*`, SHA-256 then SHA-1; most of System32 is catalog-signed). No revocation check and cache-only URL retrieval, so nothing goes online. Checked once per path, size and write time on a below-normal thread (at most 60 a minute, files up to 512 MB); Files of installed app packages (`Program Files\WindowsApps`, Microsoft Store/MSIX) are signed as a package, which Windows verifies at install, so they are reported as Package with the publisher from `AppxManifest.xml`. The identity is stored again with `signer` and `sig_status` (Embedded, Catalog, Package, NotSigned, NotTrusted, Unknown). On the dev PC: 97 running programs checked in 1.9 s. Phase 15. | ✅ | A |
| Started/stopped | Snapshot diff every process tick (✅, misses processes shorter than one tick, default 2 s). In Enhanced mode the flight-recorder trace contains every process start and exit (view it in WPA); they are not added to the timeline in v1. | | |
| **Crashed** | Application log: `Application Error` 1000, `Windows Error Reporting` 1001; `.NET Runtime` 1026 | ✅ | A |
| **Hung** | Application log `Application Hang` 1002; plus live `IsHungAppWindow` on top-level windows (every 2 s) → `PROCESS_HUNG` | ✅ | A |
| PID reuse | Processes are keyed by `(PID, CreateTime)` | | |

### Services
| Data | Source | Admin | Rel. |
|---|---|---|---|
| Inventory (name, display name, status, start type, PID, type) | `EnumServicesStatusEx` + `QueryServiceConfig` (snapshot on startup, reconciled every 5 min) | ✅ | A |
| Started/stopped | `EnumServicesStatusEx` poll every 10 s with a diff (one call, a few ms). SCM **7036** is not used: this Windows 11 build logged none in 30 days | ✅ | A |
| Start type changed | Poll diff (SCM 7040 not needed) | ✅ | A |
| Installed / removed | Poll diff (SCM 7045 not needed) | ✅ | A |
| Failures / crashes | SCM **7000, 7009, 7011, 7022, 7023, 7024, 7031, 7034** in the event engine → `ServiceEvents` kind Failed with `event_ref` | ✅ | A |
| Restarted | Derived: a stop followed by a start within N s | — | A |
| Service start time | The process create time of the service PID | ◐ | B |

### Display
Implemented in Phase 7b. Struct layouts and constants were taken from the Windows SDK 10.0.26100 headers (wingdi.h, WinUser.h, Dbt.h, WinNT.h, WtsApi32.h) and struct sizes are asserted by tests.

| Data | Source | Admin | Rel. |
|---|---|---|---|
| Active paths, resolution (source mode), position, primary (source at 0,0), rotation, adapter LUID | `GetDisplayConfigBufferSizes` + `QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS)`, retried on `ERROR_INSUFFICIENT_BUFFER` | ✅ | A |
| Refresh rate | Target mode `vSyncFreq` (the signal), falling back to the path's refresh rate | ✅ | A |
| Monitor name, EDID manufacturer/product, connector instance, connection type, monitor device path | `DisplayConfigGetDeviceInfo(GET_TARGET_NAME)`; EDID IDs only when `edidIdsValid`; the decoded IDs are checked by a test against the PnP ID in the device path | ✅ | A |
| HDR / advanced color | `GET_ADVANCED_COLOR_INFO_2` (Windows 11 24H2+: HDR supported, active color mode SDR/WCG/HDR, bits per channel, policy limits), falling back to `GET_ADVANCED_COLOR_INFO` | ✅ | A |
| GPU driving the display | Path adapter LUID → DXGI adapter → the same adapter key as the GPU collector; adapters DXGI does not list are named from `GET_ADAPTER_NAME` | ✅ | A |
| Display driver name/version/date | Inventory (Phase 6, Display device class) | ✅ | A |
| VRR | `VRROptimizeEnable` in `HKCU\Software\Microsoft\DirectX\UserGpuPreferences\DirectXUserGlobalSettings` | ✅ | C — labelled "Windows setting", not "active state" |
| G-Sync / FreeSync | NVAPI / ADLX (later, optional) | ✅ | C — Unavailable in v1 unless the vendor API is integrated |
| Change detection | Hidden **top-level** window on its own thread (message-only windows don't receive broadcasts): `WM_DISPLAYCHANGE`, `WM_SETTINGCHANGE`, `RegisterDeviceNotification(GUID_DEVINTERFACE_MONITOR)` → arrival/removal, `RegisterPowerSettingNotification(GUID_SESSION_DISPLAY_STATUS)` → displays off/on/dimmed (the GUID Microsoft documents for interactive user sessions), `WTSRegisterSessionNotification` → console/Remote Desktop connect and disconnect | ✅ | A |

How changes are recorded (`DisplayCollector`, polled every 1 s tick; `DisplayDiff` in the engine):
- Notifications keep the time Windows delivered them. Monitor arrival/removal becomes **Connected/Disconnected** immediately, so an unplug and replug within one second is still recorded. Display power becomes **PowerStateChanged** (the state reported right after registering is the baseline). Session notifications become **SessionChanged**.
- After a notification (and every 30 s as a safety net) the configuration is re-read and compared with the previous one: **ResolutionChanged**, **RefreshRateChanged** (tolerance 0.05 Hz, so 60 → 59.94 Hz counts), **HdrChanged**, **OrientationChanged**, and **TopologyChanged** (a display becoming active/inactive without a monitor notification, primary, position or adapter changes). Changes are dated to the notification that announced them.
- The configuration is re-read again 3 s after a notification. If Windows announced a display change and nothing differs once it has settled, **Reconfigured** is recorded ("display change reported; configuration unchanged"), the pattern of a mode set during driver recovery or a brief signal loss. It is recorded as an observation only.
- Display configuration is per session. In a Remote Desktop session the recorder sees the remote session's virtual display, not the physical monitors; snapshots carry `IsRemoteSession`, the collector's status says so, and nothing is compared across a console/remote switch (that is a SessionChanged, not monitors disconnecting). While a session is disconnected, `QueryDisplayConfig` returns access denied; this is logged once.
- Notifications from while the recorder was idle are discarded. Snapshots are stored when they differ from the last stored one.
- `IDisplayCollector` is now a polled collector (`IPolledCollector<DisplaySample?>` plus `SnapshotAsync`), not an event collector, so its changes arrive in `SampleFrame.Display` next to the other telemetry.

### Windows events (curated, classified; not a log dump)
Implemented in Phase 7. The shipped rules (`src/PcDiag.Engine/Events/event-rules.json`) cover:

| Category | Channel / Provider | Event IDs | Notes |
|---|---|---|---|
| GPU reset (TDR) | System / `Display` | **4101** | |
| NVIDIA / AMD / Intel driver | System / `nvlddmkm`; `amdkmdag`, `amdkmdap`, `amdwddmg`; `igfx`, `igfxn`, `igfxnd` | critical/error levels | nvlddmkm 153 ships no message text; the device (`\Device\VideoN`) and detail are kept from the raw data |
| Graphics kernel | System / `Microsoft-Windows-DxgKrnl` | critical/error/warning | |
| Live kernel events | Application / `Windows Error Reporting` | 1001 with `LiveKernelEvent`, P1 141 or 117 → GPU | Other WER reports are kept at Low severity |
| App crash / hang | Application / `Application Error`, `Application Hang`, `.NET Runtime` | 1000, 1002, 1026 | 1000/1002 carry PID **and** process start time, so they link to process history |
| Unexpected shutdown | System / `Microsoft-Windows-Kernel-Power` 41 (bug check code split out); `EventLog` 6008 | | read back to the last boot |
| Bug check | System / `Microsoft-Windows-WER-SystemErrorReporting` 1001 | | read back to the last boot; dump path kept |
| Hardware errors | System / `Microsoft-Windows-WHEA-Logger` | by level: error → Critical, warning (corrected) → Medium, information → Info | |
| Disk / storage | System / `disk` 7, 11, 51, 154 (High), 153, 157; controller resets 129 and errors 11 from `storahci`, `stornvme`, `iaStor*` and similar | | |
| NTFS | System / `Ntfs`, `Microsoft-Windows-Ntfs` | 55, 98, 137, 140 at warning or above | Ntfs 98 is also logged as "volume is healthy" (information) |
| Low resources | System / `Resource-Exhaustion-Detector` 2004 | | |
| Network | System / `Tcpip` 4231, 4266 (no ephemeral ports); Intel Ethernet `e1*express`/`e2f*express` 27 (link down); `WLAN-AutoConfig/Operational` 8003 (Wi-Fi disconnected) | | the Wi-Fi network name is never stored |
| Devices | System / `Kernel-PnP` 219, 411; `UserPnp` 20001, 20003 | | |
| Services | System / Service Control Manager 7000, 7009, 7011, 7022, 7023, 7024, 7031, 7034 | | service key name decoded from the event's binary data |
| Power and time | System / `Kernel-General` 12, 13 (OS start/stop), 1 (time change of at least 2 s); `Kernel-Power` 42, 107 (sleep/resume); `User32` 1074 (shutdown requested by) | | |
| Other errors | System / any provider | critical/error | Low severity, generic title |
| Ignored | System / `DistributedCOM` 10010, 10016 | | documented noise; suppressed inside Windows |

How it works:
- **Rules are data.** Each rule matches channel, providers, event IDs, levels and optional field conditions (equals / not equals / minimum absolute value), and sets category, severity, a title template and the fields to extract. Ignore rules win; otherwise the first matching rule wins. A rule without providers must be limited to critical/error/warning, so a channel is never collected wholesale. User rules in `%LOCALAPPDATA%\PcDiagnostic\event-rules.json` replace shipped rules by id, can disable them, or add new ones; invalid user rules are skipped and logged. Provider and channel names are restricted to a safe character set, so a rules file cannot inject into the query.
- **Windows filters first.** One structured `QueryList` per channel is generated from the rules, with one small `Select` per rule and `Suppress` for ignore rules (Microsoft's docs require a structured query beyond 20 expressions or for several sources). On this PC, 476 DCOM events in 30 days were suppressed inside Windows and none reached the Agent.
- **No gaps, no duplicates.** When recording starts, the collector subscribes (`EventLogWatcher`, one subscription per channel, so a missing channel does not affect the others) and *then* reads back the last `Events:BackfillMinutes` (30) with `EventLogReader`; rules marked `sinceBoot` (Kernel-Power 41, 6008, bug check, WHEA errors, OS start) are read back to the last boot, capped at `Events:BootBackfillMaxHours` (24). Records seen twice are de-duplicated by `(channel, record ID)`, in memory and by the table's unique key.
- **Event storms are bounded.** At most `Events:MaxPerRulePerMinute` (20) events per rule per minute are stored; the rest are counted and summarised in one event from provider `PcDiagnostic` that says how many were not recorded.
- **Message text** is formatted only for kept events; providers without message resources keep their raw values instead. Messages are cut to `Events:MaxMessageLength` (2000).
- **Into the timeline.** Events go into the frame of the next 1 s tick (`SampleFrame.Events`), so every consumer (storage now; ring buffer, live feed and incident engine later) sees them. The recorder's own observations (network link up/down/speed changes) use the same path with channel `pcdiag.network`.
- **Links.** An application crash that names its process start time adds a `Crashed` row to `ProcessEvents` (once per process, even when 1000 and 1026 both report it). An SCM failure adds a `Failed` row to `ServiceEvents` with `event_ref` pointing to the Windows event.
- Overhead: the read-back takes about 0.3 s once per recording start; steady-state Agent CPU was unchanged (0.16 % mean of all cores, median tick 12 ms).

### Inventory (startup + on change; never on the fast tick)
WMI/CIM is used **only** here, on a background thread with timeouts: `Win32_ComputerSystem`, `Win32_BaseBoard`, `Win32_BIOS`, `Win32_Processor`, `Win32_PhysicalMemory`, `Win32_VideoController`, `Win32_OperatingSystem`. Drivers come from **SetupAPI** (`SetupDiGetClassDevs` + driver info) rather than `Win32_PnPSignedDriver`, which is slow. The inventory is refreshed on `WM_DEVICECHANGE` (debounced) and every 6 h.

### Verification commitment
Items marked *spike* (D3DKMT perf data, LHM driver model, NVML function names) get a small proof-of-concept against current Microsoft/vendor documentation at the start of the phase that uses them, before any design depends on them. If a spike fails, the metric falls back to the next provider or shows as `Unsupported`.

---

## 6. Privilege model

| Capability | Standard user | Elevated Agent |
|---|---|---|
| CPU/RAM/GPU utilization, VRAM, disk/network counters | ✅ | ✅ |
| Processes (most), services, display, event logs (System/Application) | ✅ | ✅ (+ protected-process details) |
| CPU temp/power, motherboard sensors, fan RPM (LHM) | ❌ Unavailable (`RequiresElevation`) | ✅ |
| SMART / storage reliability counters | ❌ | ✅ |
| ETW Enhanced mode (true per-process disk/network, flight-recorder `.etl`) | ❌ (the recorder card says why) | ✅ (opt-in) |
| Minidump listing (`C:\Windows\Minidump`) | ❌ | ✅ (metadata only; we don't parse dumps in v1) |

- `PcDiag.Web` **never** runs elevated, and the UI runs in the browser sandbox. Elevation happens once, when the installer registers the scheduled task `\PC Diagnostic\Recorder` (an option, on by default): at the user's sign-in it starts the Agent in that user's session with the highest rights the user has (InteractiveToken, RunLevel Highest, normal priority, no time limit, also on battery). Without the task the Agent runs as a standard user and the elevated readings are `RequiresElevation`.
- **An elevated Agent never starts anything elevated for the user.** The tray's *Open dashboard* asks the desktop shell (Explorer, through the desktop folder view's `Shell.Application.ShellExecute`, the documented way) to start the Web host and open the browser, so both get Explorer's ordinary token; if Explorer is not available it falls back to copying the link. The Start menu's *PC Diagnostic* runs `PcDiag.Agent.exe open` as the user: it starts the recorder through the task (or as the user when there is none) and opens the dashboard.
- The elevated Agent exposes only a **fixed command set** over IPC. There is no "run command" and no path-taking commands. It has no HTTP listener. **Exports are generated by the non-elevated Web host and delivered as browser downloads**, so no process ever writes to a server-side path chosen by the user.

---

## 7. Data model & database schema

### Conventions
- Timestamps are `INTEGER` Unix epoch **milliseconds UTC** (`ts`). The UI converts them to local time. Each tick also carries a monotonic sequence number so clock jumps can be detected.
- `NULL` means *not available*. Availability reasons are stored per series in `SensorAvailability`, not per row, which saves space.
- Strings are normalized into catalog tables (process identity, sensor catalog, devices).
- Telemetry tables are `WITHOUT ROWID` with composite PKs that start with `ts`. Time-range scans are then sequential B-tree reads, and inserts are append-mostly.
- Pragmas: `journal_mode=WAL`, `synchronous=NORMAL`, `temp_store=MEMORY`, `auto_vacuum=INCREMENTAL`, `journal_size_limit` 32 MB (the write-ahead log is cut back after a checkpoint; Phase 24), `cache_size` capped (~8 MB). The UI opens with `mode=ro`.

### Files
```
%LOCALAPPDATA%\PcDiagnostic\
  telemetry.db            rolling store (disposable — rebuilt if corrupt)
  incidents.db            incident index + sequence (precious)
  incidents\INC-20260928-00027\
      incident.db         same telemetry schema, window-sliced copy + snapshots + timeline
      traces\incident.etl (Enhanced mode only)
  logs\                   Serilog rolling files
  settings.json
  storage-log.json        the storage manager's recent clean-ups (Phase 24)
  overlay.json            the on-screen readings layout (Phase 23)
```
Per-incident DBs reuse **the exact same schema and DAL** as `telemetry.db`, so the investigation UI reads an incident and live history with the same code. Capture is `ATTACH` + `INSERT … SELECT … WHERE ts BETWEEN`.

> The SQL migration scripts in `src/PcDiag.Storage.Sqlite/Migrations/` are the source of truth. They extend the sketch below with `ExtensionSeries` (names for `ExtensionMetrics.series_id`), `RollupSeries` (names for `Rollup1m.series_id`), and `Incidents.snapshot_only`.

### telemetry.db (and per-incident db)
```sql
-- Spine: one row per fast tick; lets timeline know what was sampled and how healthy sampling was
CREATE TABLE SystemSnapshots (
  ts INTEGER PRIMARY KEY, seq INTEGER NOT NULL,
  cpu_pct REAL, mem_used_pct REAL, gpu_pct REAL, vram_pct REAL,   -- headline values (dashboard/history fast path)
  disk_active_pct REAL, net_bps REAL,
  tick_ms REAL, collectors_missed INTEGER, agent_cpu_pct REAL, agent_ws_bytes INTEGER
);

CREATE TABLE CpuMetrics (
  ts INTEGER PRIMARY KEY,
  total_pct REAL, user_pct REAL, kernel_pct REAL,
  freq_mhz REAL, max_freq_mhz REAL, perf_pct REAL, perf_limit_pct REAL, perf_limit_flags INTEGER,
  queue_len REAL, ctx_switches_ps REAL, interrupts_ps REAL,
  package_temp_c REAL, package_power_w REAL,
  per_core_util BLOB,     -- packed uint8 % per logical core
  per_core_mhz  BLOB      -- packed uint16 MHz per logical core
) WITHOUT ROWID;

CREATE TABLE MemoryMetrics (
  ts INTEGER PRIMARY KEY,
  total_b INTEGER, available_b INTEGER, free_b INTEGER, cache_b INTEGER,
  commit_b INTEGER, commit_limit_b INTEGER, paged_pool_b INTEGER, nonpaged_pool_b INTEGER,
  compressed_b INTEGER, page_faults_ps REAL, hard_faults_ps REAL, pages_ps REAL,
  pressure REAL           -- derived 0..1, formula versioned
) WITHOUT ROWID;

CREATE TABLE GpuMetrics (
  ts INTEGER NOT NULL, adapter_id INTEGER NOT NULL,   -- → Devices
  util_pct REAL, util_3d REAL, util_compute REAL, util_decode REAL, util_encode REAL, util_copy REAL,
  vram_used_b INTEGER, vram_budget_b INTEGER, shared_used_b INTEGER,
  temp_c REAL, hotspot_c REAL, core_mhz REAL, mem_mhz REAL,
  power_w REAL, power_limit_w REAL, throttle_flags INTEGER, fan_rpm REAL,
  PRIMARY KEY (ts, adapter_id)
) WITHOUT ROWID;

CREATE TABLE DiskMetrics (
  ts INTEGER NOT NULL, disk_id INTEGER NOT NULL,
  read_bps REAL, write_bps REAL, read_iops REAL, write_iops REAL,
  read_lat_ms REAL, write_lat_ms REAL, queue_len REAL, active_pct REAL, temp_c REAL,
  PRIMARY KEY (ts, disk_id)
) WITHOUT ROWID;

CREATE TABLE NetworkMetrics (
  ts INTEGER NOT NULL, if_id INTEGER NOT NULL,
  rx_bps REAL, tx_bps REAL, rx_pps REAL, tx_pps REAL,
  rx_errors INTEGER, tx_errors INTEGER, rx_discards INTEGER, tx_discards INTEGER,  -- deltas per tick
  link_bps INTEGER, util_pct REAL, oper_status INTEGER, wifi_quality REAL, wifi_rssi REAL,
  PRIMARY KEY (ts, if_id)
) WITHOUT ROWID;

-- Dynamic hardware sensors (LHM exposes hundreds) — narrow table
CREATE TABLE SensorCatalog (sensor_id INTEGER PRIMARY KEY, provider TEXT, device_id INTEGER,
  kind TEXT /*Temperature|Clock|Power|Fan|Voltage|Load*/, name TEXT, unit TEXT, logical_key TEXT UNIQUE);
CREATE TABLE SensorReadings (ts INTEGER NOT NULL, sensor_id INTEGER NOT NULL, value REAL,
  PRIMARY KEY (ts, sensor_id)) WITHOUT ROWID;
CREATE TABLE SensorAvailability (series TEXT PRIMARY KEY, availability INTEGER, reason TEXT, since_ts INTEGER);

-- Extension point for future plugins (Battery, USB, Audio…) without schema changes
CREATE TABLE ExtensionMetrics (ts INTEGER NOT NULL, series_id INTEGER NOT NULL, value REAL,
  PRIMARY KEY (ts, series_id)) WITHOUT ROWID;

CREATE TABLE Devices (device_id INTEGER PRIMARY KEY, kind TEXT, stable_key TEXT UNIQUE,  -- LUID/PnP id/ifGuid
  name TEXT, vendor TEXT, details_json TEXT, first_seen INTEGER, last_seen INTEGER);

-- Processes: identity normalized; per-tick samples only for top-N union (+ all in ring buffer)
CREATE TABLE ProcessIdentity (proc_key INTEGER PRIMARY KEY,           -- hash(pid, create_time)
  pid INTEGER, ppid INTEGER, name TEXT, path TEXT, cmdline TEXT /*opt-in*/, user TEXT /*opt-in*/,
  start_ts INTEGER, end_ts INTEGER, signer TEXT, sig_status INTEGER, is_service INTEGER);
CREATE TABLE ProcessSnapshots (
  ts INTEGER NOT NULL, proc_key INTEGER NOT NULL,
  cpu_pct REAL, cpu_time_ms INTEGER, ws_b INTEGER, private_b INTEGER, commit_b INTEGER,
  io_read_bps REAL, io_write_bps REAL, gpu_pct REAL, gpu_engine TEXT, gpu_mem_b INTEGER,
  threads INTEGER, handles INTEGER, priority INTEGER, state INTEGER,
  PRIMARY KEY (ts, proc_key)
) WITHOUT ROWID;
CREATE TABLE ProcessEvents (id INTEGER PRIMARY KEY, ts INTEGER NOT NULL, proc_key INTEGER,
  kind INTEGER /*Started|Stopped|Crashed|Hung|Recovered*/, detail_json TEXT);
CREATE INDEX IX_ProcessEvents_ts ON ProcessEvents(ts);

CREATE TABLE ServiceSnapshots (ts INTEGER NOT NULL, name TEXT NOT NULL, display_name TEXT,
  status INTEGER, start_type INTEGER, pid INTEGER, svc_type INTEGER, PRIMARY KEY (ts, name)) WITHOUT ROWID;
CREATE TABLE ServiceEvents (id INTEGER PRIMARY KEY, ts INTEGER NOT NULL, name TEXT,
  kind INTEGER /*Started|Stopped|Restarted|StartTypeChanged|Failed|Installed*/, old TEXT, new TEXT, event_ref INTEGER);
CREATE INDEX IX_ServiceEvents_ts ON ServiceEvents(ts);

-- Snapshots stored only on change (hash dedupe)
CREATE TABLE HardwareSnapshots (id INTEGER PRIMARY KEY, ts INTEGER NOT NULL, hash TEXT, json TEXT);
CREATE TABLE DisplaySnapshots  (id INTEGER PRIMARY KEY, ts INTEGER NOT NULL, hash TEXT, json TEXT);
CREATE TABLE DisplayEvents     (id INTEGER PRIMARY KEY, ts INTEGER NOT NULL, kind INTEGER, target TEXT, old_value TEXT, new_value TEXT,
  target_key TEXT);  -- v5: monitor PnP instance ID
CREATE TABLE Drivers (id INTEGER PRIMARY KEY, snapshot_ts INTEGER, device_class TEXT, device_name TEXT,
  hardware_id TEXT, provider TEXT, version TEXT, driver_date TEXT, inf TEXT, signer TEXT);
CREATE TABLE SystemInformation (key TEXT PRIMARY KEY, value TEXT, updated_ts INTEGER);

CREATE TABLE WindowsEvents (id INTEGER PRIMARY KEY, ts INTEGER NOT NULL,
  channel TEXT, record_id INTEGER, provider TEXT, event_id INTEGER, level INTEGER,
  category INTEGER, severity INTEGER, title TEXT, message TEXT,
  pid INTEGER, process_name TEXT, related_device TEXT, rule_id TEXT,
  proc_key INTEGER, service_name TEXT, data_json TEXT,   -- v4: process/service links, extracted fields
  UNIQUE (channel, record_id));
CREATE INDEX IX_WindowsEvents_ts ON WindowsEvents(ts);
CREATE INDEX IX_WindowsEvents_cat_ts ON WindowsEvents(category, ts);

CREATE TABLE Anomalies (id INTEGER PRIMARY KEY, start_ts INTEGER NOT NULL, end_ts INTEGER,
  rule_id TEXT, series TEXT, observed REAL, baseline_mean REAL, baseline_sd REAL,
  duration_ms INTEGER, severity INTEGER, reasoning_json TEXT, incident_key TEXT);
CREATE INDEX IX_Anomalies_ts ON Anomalies(start_ts);

-- Long-range history (6h/24h charts beyond raw retention, and 30-day trends)
CREATE TABLE Rollup1m (ts INTEGER NOT NULL, series_id INTEGER NOT NULL,
  min REAL, avg REAL, max REAL, PRIMARY KEY (ts, series_id)) WITHOUT ROWID;

CREATE TABLE AgentHeartbeat (id INTEGER PRIMARY KEY CHECK (id = 1), last_ts INTEGER,
  boot_id TEXT, clean_shutdown INTEGER);
CREATE TABLE RecorderSessions (id INTEGER PRIMARY KEY, start_ts INTEGER NOT NULL, end_ts INTEGER,
  start_reason INTEGER /*User|Schedule|Always|Capture*/, end_reason INTEGER /*User|Schedule|Pause|Shutdown|Crash*/,
  elevated INTEGER, app_version TEXT);
CREATE INDEX IX_RecorderSessions_start ON RecorderSessions(start_ts);
CREATE TABLE SchemaVersion (version INTEGER NOT NULL);
```

### incidents.db (index)
```sql
CREATE TABLE Incidents (
  id INTEGER PRIMARY KEY,                 -- global sequence → "INC-20260928-00027", "#00027"
  incident_key TEXT UNIQUE NOT NULL,
  created_ts INTEGER NOT NULL, center_ts INTEGER NOT NULL,
  pre_ms INTEGER, post_ms INTEGER,
  state INTEGER,                          -- Recording|Finalizing|Analyzing|Complete|Partial|Failed
  source INTEGER,                         -- User|Hotkey|Rule|Event|BootRecovery
  user_type TEXT,                         -- ScreenFlicker|ScreenArtifact|…|Other
  title TEXT, user_notes TEXT, severity INTEGER,
  headline_json TEXT,                     -- summary numbers at center_ts
  folder TEXT, app_version TEXT, pinned INTEGER DEFAULT 0);
CREATE INDEX IX_Incidents_center ON Incidents(center_ts);
CREATE TABLE IncidentTriggers (id INTEGER PRIMARY KEY, incident_id INTEGER, ts INTEGER,
  source INTEGER, rule_id TEXT, detail_json TEXT);
```
Inside each `incident.db` there are also two more tables. `IncidentEvents`, the unified timeline, is `(ts, kind, source_table, source_id, title, detail_json, importance)`. `Findings` holds `(level, text, evidence_refs_json)`.

### Indexing strategy
- **Recent-history / chart queries:** PK prefix `ts` → a range scan. The headline `SystemSnapshots` row answers the dashboard sparkline and the 1 min–1 h charts without joins. 6 h and 24 h charts read `Rollup1m` (1,440 points), or raw data decimated into min/max buckets.
- **Timeline queries:** each event table has a `ts` index. The timeline is a `UNION ALL` across small event tables, ordered by `ts`.
- **Incident queries:** these are local to the incident file, which contains only its window, so they are trivially fast. The index DB has an index on `center_ts`.
- **Retention:** raw rows older than the retention window (default 24 h) are deleted in chunks (`DELETE … WHERE ts < ? LIMIT 5000`, with a pause between chunks) and followed by `incremental_vacuum`. Rollups are kept 30 days. Incidents are never auto-deleted unless the user sets a cap.

### Estimated volume (default settings, 24-core CPU, 2 GPUs, 3 disks, 4 interfaces)
~ system+cpu+mem 0.4 KB/s, gpu/disk/net 0.6 KB/s, sensors (~60 selected) 1 KB/s, processes (top-25 union @ 2 s) 1.5 KB/s → **~300 MB/day raw**, bounded by 24 h retention (configurable 1–72 h). The in-memory ring (30 min, all processes) is capped at **~60 MB**. Both limits are enforced and shown in Settings.

**Measured (Phase 8, dev PC, all collectors, 11-minute run):** about **285 MB/day** on disk (process snapshots ~58 %), so 24 h retention keeps telemetry.db near 300 MB; rollups add about 39 series × 1 440 minutes per day (well under 10 MB/day). The ring buffer estimate is about **94 KB per frame** with ~350 processes, so the default 10-minute pre-window (11 minutes held) needs about 62 MB of the 64 MB cap; longer pre-windows are limited by the cap (a warning is logged), and the older part of the window then comes from telemetry.db (top processes only).

---

## 8. Telemetry sampling architecture

```
 PeriodicTimer(1s, aligned)──┐
 PeriodicTimer(2s)───────────┼─► TickScheduler ─► parallel CollectAsync (per-collector timeout = 60% of tier)
 PeriodicTimer(10s)──────────┘        │ overrun? → skip that collector's tick, count it, never queue up
                                      ▼
                          SampleFrame (immutable, pooled arrays)
                                      ▼
                  SampleBus: bounded Channel<SampleFrame> fan-out (DropOldest for Live/UI only)
       ┌───────────────┬───────────────┼────────────────┬─────────────────┐
   RingBuffer     StorageWriter   AnomalyEngine     LiveFeed(IPC)    Rollup aggregator
   (lock-free    (batch 5 s, one   (streaming       (1 Hz to UI,
    append)       transaction)      rules)           coalesced)
```

- **Cost classes**: PDH queries are opened once, with all counters in one query and one `PdhCollectQueryData` per tick. The process list is one NT call into a reused buffer, and the GPU per-process counters come from a single wildcard array read. Nothing uses LINQ allocations on the hot path.
- **Tiers** (all configurable): system 1 s, sensors 1 s, processes 2 s, detailed inventory (paths, signatures, services reconcile) 10 s, hardware inventory on change, events push-based.
- **Self-monitoring**: every tick records the agent's own CPU and working set. If average CPU goes over budget (2%), the sensor tier backs off to 2 s and the process tier to 5 s, and this is logged and visible in the UI.
- **Sleep/resume**: `SystemEvents.PowerModeChanged` (Suspend/Resume) ends the recording session before sleep (reason Suspend) and starts a new one on wake (reason Resume), so recorded periods never span sleep and charts show the gap.

---

## 9. Rolling buffer architecture

| Tier | Contents | Resolution | Capacity | Survives |
|---|---|---|---|---|
| **T0 RAM ring** | Every frame, including **all** processes | 1 s / 2 s | max(pre-window) = up to 30 min, hard cap on bytes | App running |
| **T1 telemetry.db** | Every frame, processes = top-N union (CPU, RAM, GPU, IO) + any process involved in an event | 1 s / 2 s | retention 24 h (1–72 h) | Crash, BSOD, power loss (≤ 5 s loss) |
| **T1r Rollup1m** | min/avg/max per headline series | 1 min | 30 days | Same |
| **T2 incident.db** | Window slice + full snapshots + timeline + findings | original | permanent | Everything |

The ring buffer is a pre-allocated array of frame slots (sized once for the longest window at the fastest tick) fed by its own sample-bus queue; capture copies the frame references in a time range under a short lock. It holds the configured pre-window plus one minute, capped by `Recorder:RingBufferMaxMegabytes` using a per-frame estimate (`SampleFrameSize`); it logs when the window first fills and when the cap first limits it. Frames share what did not change (7 Oct 2026): the process collector hands back the previous `ProcessMetrics` object for a process that reads exactly as before, and the LHM provider keeps each sensor's descriptor and hands back the previous reading when the value is unchanged, so the estimate charges such an object once (in the oldest frame holding it) and later frames only for the reference. There is no unbounded collection anywhere. Every queue is a bounded `Channel` with an explicit full-mode policy.

**Maintenance (`TelemetryMaintenance`, Agent only, telemetry.db only):**
- **Rollups** every minute: min/avg/max per complete minute (15 s after it ends) for headline series: CPU, memory, GPU, disk, network, the Agent itself, and per GPU adapter, disk and network interface. A marker in SystemInformation makes passes incremental; at most 120 minutes per pass.
- **Retention** every 10 minutes: telemetry rows (system, CPU, memory, GPU, disk, network, volumes, processes, process events, service snapshots, sensors) after `Storage:TelemetryRetentionHours` (24 h); Windows/service/display events, anomalies, rollups, recorder sessions and older snapshots after `Storage:RollupRetentionDays` (30 days). The latest hardware, display and driver snapshots are always kept. Deletes run in 15-minute time slices (short write transactions next to the recorder).
- **Space** hourly: `PRAGMA incremental_vacuum` returns free pages above 4 MB to the file system (100 MB per pass at most), in 10 MB steps each followed by a checkpoint, and the write-ahead log is emptied afterwards (one large vacuum passed every moved page through the log at once and left it that large).
- **The storage manager** (Phase 24) also trims ahead of the retention, through `ITelemetrySpace`: see §10 *Storage manager*.

**Heartbeat and boot recovery (`HeartbeatService`):** every 10 s the Agent writes AgentHeartbeat (time, boot ID, recording or not). A normal stop, and Windows ending the session (`SystemEvents.SessionEnded`, which also stops the recording with reason SessionEnd), mark a clean stop. At startup, if the previous run did not stop cleanly, the System log since the last heartbeat is read with the Phase 7 rules and `BootRecoveryClassifier` decides: **BugCheck** (with code and dump file), **UnexpectedShutdown** (Kernel-Power 41 / 6008; worded as possibilities), **WindowsRestarted** (normal restart, recorder not stopped cleanly), or **RecorderStopped** (Windows kept running). With Fast Startup the uptime does not reset, so "Windows started" (Kernel-General 12) and crash events after the heartbeat also count as a restart. Findings go to `BootRecoveries` in incidents.db with `incident_id` empty until the incident engine (Phase 9) creates the incident.

---

## 10. Incident lifecycle

```
  trigger (button | hotkey | tray | quick-type | rule | event | boot-recovery)
      │
      ▼
 [Created] ── allocate INC id, write Incidents row immediately (state=Recording)
      │       freeze T0 pre-window → write to incident.db NOW (evidence safe before post-window)
      │       take snapshots: processes(all), services, display, drivers, hardware, system
      ▼
 [Recording post-window] (default 2 min) ── new triggers in this window are MERGED as extra
      │                                      IncidentTriggers (no duplicate incidents; cooldown);
      │                                      every 10 s the post-window so far is saved with every
      │                                      process from the ring, so a restart or crash keeps them
      │                                      (telemetry.db holds only the busiest processes)
      ▼
 [Finalizing] ── copy post-window from T0/T1, backfill Windows events for full window
      │         (events can be logged late, e.g. WER 1001), build unified timeline
      ▼
 [Analyzing]  ── run diagnostic analyzers → Findings (Observation/Correlation/Possible/Confirmed)
      ▼
 [Complete]  (or [Partial] if agent stopped mid-recording — resumed/finalized on next start)

 Boot recovery: on start, if AgentHeartbeat.clean_shutdown=0 or Kernel-Power 41 / 6008 / BugCheck
 1001 found since last heartbeat → create incident (source=BootRecovery) centered on last heartbeat
 using T1 data; attach bug-check parameters and dump file names (if elevated).
```

- **Pre/post windows:** pre-window choices are 1, 5, 10 (default), 15 and 30 min. The post-window defaults to 2 min and is configurable.
- **Implemented (Phase 9, `IncidentManager` + `IncidentStore`):** capture flushes the sample bus, allocates the key inside an incidents.db transaction, creates `incidents\<key>\incident.db` (the telemetry migrations plus incident tables) and copies the pre-window *before* returning: telemetry.db rows by `ATTACH` + `INSERT OR IGNORE … SELECT` over the columns both databases have (row IDs kept, so cross-references stay valid), the latest hardware/display/driver/service snapshots before the window, reference tables, every process from the in-memory ring, and fresh display and service snapshots. A second capture within the post-window is added as a trigger. Ten seconds after the post-window (telemetry is written every 5 s) the post-window is copied, Windows events for the whole window are copied (some are logged late), the timeline (IncidentEvents) is built from Windows, process, service, display and recorder events plus the triggers, the headline numbers at the center are stored, and the database is checkpointed and switched to rollback journaling so the finished folder is a single self-contained file. Capturing while not recording starts a temporary recording (session reason Capture, ended with reason CaptureComplete unless the mode or the user wants recording by then) and marks the incident snapshot-only. Analysis (findings) arrives in Phase 10.
- **Incident ID:** `INC-{yyyyMMdd local}-{seq:D5}`, where `seq` is a global monotonic counter in `incidents.db`.
- **Trigger framework:** `IIncidentTriggerSource` implementations are RuleTriggerSource (anomalies), EventTriggerSource (classified events such as TDR, crash, WHEA or display change), UserTriggerSource and BootRecoveryTriggerSource. Each trigger has enabled, cooldown and severity settings in configuration.

### 10a. Recorder control (user-configurable)

The **Agent process lifetime** and the **recording state** are separate.

| Setting | Options | Default |
|---|---|---|
| Agent startup | *On demand* (Start menu / tray shortcut runs the scheduled task, so it starts elevated without a UAC prompt) · *At logon* (the task gets a logon trigger) | On demand |
| Recording mode | *Manual* (Start/Stop in the UI, tray or hotkey) · *Always* (while the Agent runs) · *Scheduled* (one or more weekly time windows, e.g. Mon–Fri 09:00–18:00) | Manual |
| Watch for problems when not recording | While not recording (and not paused), the problem rules keep running on samples kept in memory only. A rule that creates incidents then stores the watched minutes (as the incident's pre-window), starts a capture recording and saves the incident. Nothing is stored until then. Added 15 Oct 2026, after a stopped Manual recorder missed GPU memory at 90 % for minutes. | On |
| Pause | Pause for a duration or until resumed (works in every mode; a user Start ends a pause; "until resumed" does not survive an Agent restart) | — |
| Stop on exit | Stop recording when the Agent is closed from the tray (always true) | — |

- `RecordingController` owns the state machine: `Idle → Starting → Recording → Stopping → Idle`, plus `Paused`. `Scheduled` mode evaluates windows against local time with `TimeProvider` (unit-tested across DST changes).
- When **Idle**, no collectors or timers run and no telemetry is written. Only the hidden window, tray, hotkey and IPC server stay alive (target < 0.1 % CPU, < 40 MB).
- Every start and stop is recorded as a `RecorderSession` (start, end, reason: user / schedule / pause / shutdown). Charts show "not recording" gaps explicitly instead of drawing lines across them.
- **Capture while not recording** is allowed, but the incident has **no pre-window**. It is labelled "Snapshot only — recorder was not running". The post-window is recorded, because capture starts recording temporarily.
- Automatic triggers and boot-recovery work only for periods that were being recorded. The dashboard shows the recorder state prominently, and the tray icon changes when it isn't recording.

### Anomaly engine (transparent, no ML)
Rules are defined in `anomaly-rules.json` (defaults ship with the app and the user can edit them in Settings):

| Rule type | Params | Example |
|---|---|---|
| `ThresholdDuration` | series, op, value, duration | CPU > 95 % for 30 s; GPU > 98 % for 20 s |
| `Threshold` | series, op, value | RAM available < 1 GB; GPU temp > 85 °C |
| `SuddenDrop` / `RateOfChange` | series, delta%, window | GPU clock drops > 60 % within 2 s while util > 30 % |
| `BaselineDeviation` | series, EWMA α, z-score, min duration | GPU util z > 3 vs 30-min baseline for ≥ 15 s |
| `CounterSpike` | series (delta), threshold | Network errors > 50 / 10 s |

Each rule's output includes its **reasoning payload**, for example: `{baseline: "40–70 % (mean 55, sd 8, 30 min)", current: 99, duration: "18 s", rule: "GPU util > baseline+3σ for ≥15 s"}`. The UI renders this verbatim, as specified in PROJECT.md §12.

Implemented in Phase 10:
- **Rules** ship embedded in PcDiag.Core (`Anomalies/anomaly-rules.json`, 23 rules) so the Agent evaluates them and the Web host can show and validate them. The user never edits the file: Settings writes overrides to the `Triggers` section of settings.json (enabled, creates an incident, threshold, duration, window, cooldown), and only values that differ from the shipped ones are stored, so improved defaults still reach users. Unknown rule IDs (from an older version) are ignored by the Agent and refused by the Settings API. A sixth rule type, `Event`, matches classified Windows events by rule ID (prefix `*` allowed), process crash/hang transitions and display changes.
- **Rules of the user's own and deleted rules** (15 Oct 2026): *Settings › Automatic incidents › Your rules › Add a rule* defines a rule as fully as a shipped one: a measurement (any metric, with any of the five measurement kinds, direction, value, duration or window) or events (process crashes and hangs, display changes, classified Windows events, from `GET /api/v1/triggers/choices`), its severity, whether it creates incidents, and its cooldown. They are stored whole in `Triggers:Custom` (IDs `my-…`) and edited or deleted there (deleting asks first). A shipped rule can be deleted too (`Triggers:Removed`, after confirming) and brought back from *Deleted rules*. Saving refuses a rule that cannot work and says why; the recorder skips one rather than failing.
- **Categories** (15 Oct 2026): rules are listed by category: Processor, Memory, Graphics, Storage, Network, Sensors and other readings, Events (`AnomalyCategories`, by what a rule watches). The user renames, reorders, adds and deletes them (`Triggers:Categories`; deleting one lists its rules under *Uncategorized*, still working) and moves any rule (`Triggers:RuleCategories` for shipped rules, `category` on the user's own).
- **Any reading** (15 Oct 2026): a rule of the user's own can watch any of the system's readings, the values the on-screen panels can show (every core, GPU, drive, adapter, motherboard and memory sensor, and driver-package sensors; 326 on the dev PC), as `reading:<id>`. The editor lists them by device with a search (`GET /api/v1/triggers/readings`, from the recorder's known sources). The engine computes only the readings rules watch, from the newest sample of each collector (`MetricExtractor.Readings`).
- **Events, narrowed and described** (15 Oct 2026): process rules can be limited to programs (`game.exe`, `chrome*`) and display rules to displays (by name), with suggestions of the programs running and the displays recorded (`GET /api/v1/triggers/suggestions`). A rule can also watch a Windows event the user describes (log, source, event IDs, levels; logs and sources suggested from the PC's own): `EventRuleProvider` turns it into a classified event rule (`<rule>.w<n>`), and the event collector subscribes again when the rules change.
- **Triggers count from when the rule fires**: a trigger that waited more than 2 minutes for its turn is dropped. It used to be counted from the anomaly's start, so a rule with a duration over 2 minutes (VRAM above 90 % for 5 minutes) never created an incident; fixed 15 Oct 2026.
- **Metrics** the rules can use: `cpu`, `cpu.temp`, `memory.available` (GB), `memory.commit`, and per device `gpu.util|temp|hotspot|core|vram`, `disk.latency` (slower of read/write), `disk.queue`, `net.errors` (receive + send errors only; discards are normal on many networks).
- **Engine** (`AnomalyEngine`, a sample-bus consumer) evaluates **recorded frames only**; the dashboard's in-memory live view never creates anomalies. Duration rules date an anomaly from when the condition began and end it after 5 s clear (a value hovering at the threshold is one anomaly). Baselines are an exponentially weighted mean and variance with the rule's time constant, trusted after a warm-up, with a minimum standard deviation, and frozen for up to 5 min while the condition holds (so an anomaly is not absorbed into "normal" while it happens, yet a lasting change becomes the new normal). Rate-of-change rules compare with the latest extreme in the window and can require a guard (the GPU clock rule only counts while the GPU stays ≥ 30 % busy, so clocks falling with the load are not anomalies). Events older than 5 min (read back when recording starts) are ignored, and one subject (process, provider, display) counts once per minute, so a crash seen as a process exit and as a WER event is one anomaly. A 2-second sweep ends anomalies whose data stopped (recording stopped, sleep).
- **Storage:** `Anomalies` in telemetry.db (with the device), written when an anomaly starts and ends and every 30 s while it runs; incidents copy overlapping anomalies (the latest state wins) and show them in their timeline (`anomaly` lane). Anomalies left open by a crash are closed at startup at their stored duration.
- **Defaults:** load-type rules (high CPU or GPU load, unusual load, high CPU temperature, slow disks, display changes) record anomalies but do not create incidents, because they are normal while gaming, compiling or changing settings; failure-type rules (GPU reset, display driver error, application crash or hang, WHEA hardware error, storage error, resource exhaustion, low memory, GPU overheating, GPU clock collapse under load, network errors) create incidents. The user can change every one.
- **Triggers** (`TriggerService`) receive anomalies through a bounded channel (incident capture flushes the sample bus, so it can never run on a consumer's queue; the hop also keeps the engine independent of the incident manager in DI). Limits: the master switch, a per-rule cooldown (30 min), and at most `MaxIncidentsPerHour` (4) new incidents; a trigger while an incident is still recording joins it as an extra trigger. Rule incidents get source `Rule` (event rules `Event`), the rule's severity, and a title made of the rule name and what was observed, e.g. "Sudden GPU clock drop (NVIDIA GeForce RTX 4090): 2100 MHz → 300 MHz (−86 % in 1 s); GPU utilization 96 %".

### Diagnostic analyzer & wording discipline
- Analyzers are small single-purpose classes: `GpuActivityAnalyzer`, `GpuClockAnalyzer`, `DisplayEventAnalyzer`, `MemoryPressureAnalyzer`, `DiskLatencyAnalyzer`, `NetworkAnalyzer`, `ProcessLifecycleAnalyzer`, `ThermalAnalyzer`, and so on. Each one emits `Finding { Level, Text, EvidenceRefs[] }`, and every finding links to the data points behind it.
- **ConfirmedEvent** is used only for facts Windows logged, such as "Display driver nvlddmkm stopped responding and recovered (Event 4101) at 12:30:58".
- **Correlation** always uses "coincided with" or "occurred N s before". **PossibleExplanation** always uses "may", "could be consistent with".
- A unit-tested **language guard** rejects causal verbs ("caused", "because of", "due to") in anything that isn't a ConfirmedEvent quote. The report always ends with a **NOT PROVEN** section.
- Negative observations are reported explicitly, for example "No significant RAM pressure was observed (available ≥ 9.1 GB throughout)".

Implemented in Phase 10:
- Analyzers (`CpuAnalyzer`, `GpuActivityAnalyzer`, `GpuClockAnalyzer`, `DisplayEventAnalyzer`, `MemoryPressureAnalyzer`, `DiskLatencyAnalyzer`, `NetworkAnalyzer`, `ProcessLifecycleAnalyzer`, `ThermalAnalyzer`, `SystemEventAnalyzer`, `AnomalyAnalyzer`) read the incident's own incident.db through `IIncidentEvidenceSource` (metric tracks at about 1 s, the timeline, anomalies and triggers), so boot-recovery incidents and incidents from older versions are analyzed the same way. "Configured" limits are the rule thresholds in effect.
- Each analyzer notes notable activity per subsystem; the correlation step then writes one **Correlation** per subsystem with activity from 60 s before to 15 s after the incident ("GPU and display activity coincided with the reported screen flicker: … (3 s before); … (2 s before)") and, for what the user reported, hedged **Possible explanations** for the plausible subsystems only ("may be related …; it could be consistent with a display driver or GPU problem"). An automatic or unlabelled capture gets correlations but no explanations.
- A new evidence level, **NotProven**, ends every summary: "The collected telemetry does not establish that the GPU or the display driver caused the reported screen flicker." (one per correlated subsystem, up to three, plus "Timing alone does not establish a connection"), or "…does not establish what caused …; no measured activity stood out" when nothing correlated.
- The **language guard** rejects causal wording (caused, because, due to, led to, resulted in, responsible for, triggered by, root cause, proves, confirms, definitely…) everywhere except inside quotation marks (text quoted from Windows) and in NotProven statements, which must be negations; correlations must say "coincided with"/"before"/"after"; possible explanations "may"/"could". A finding that fails is dropped and logged; scenario tests assert no analyzer ever produces one.
- Findings are stored in incident.db (`Findings`, with evidence links `{kind: metric|range|event|anomaly|trigger, ts, sourceId}`) after the incident is finalized; `Incidents.analysis_version` records the analyzer version, and incidents analyzed by an older version (or never) are analyzed again when the Agent starts (newest 25). The investigation page shows OBSERVATIONS / POSSIBLE CORRELATIONS / NOT PROVEN; evidence links pick the moment and add the series to the charts.
- Analyzer version 3 (7 Oct 2026): GPU utilization below 50 % is "stayed low", 50–90 % is reported as its peak without counting as notable, and 90 % or more is notable (version 2 called anything under 90 % "low", so an 84 % peak was described as low). Finished incidents are analyzed again when the Agent starts.
- Analyzer version 4 (7 Oct 2026): incidents recorded before the GPU collector dropped virtual twins still hold the twin as a second GPU. The evidence reader leaves out a GPU whose name matches another GPU's, whose stable key is that GPU's key plus "-N", and which lacks the NVML source the other one has (NVML finds cards by PCI location, which the twin does not have). Its zero-load tracks no longer produce "stayed low (up to 0 %)" or "no clock drop under load" next to the real card's findings.

### Enhanced mode (Phase 14)
- Settings → Enhanced mode has two independent switches (`Enhanced:FlightRecorder`, `Enhanced:ProcessIo`), both off by default. `EnhancedModeService` checks every 5 s and runs each part only **while recording**, when it is switched on and the Agent is elevated; otherwise it is stopped and costs nothing. A failed start (not elevated, wpr.exe missing, session in use) is reported in the recorder status ("Enhanced mode" on the recorder card) and retried only when the part is wanted again (switched on again or recording restarts).
- **Flight recorder:** `wpr.exe -start <program folder>\PcDiagnostic.wprp!PcDiagnostic.Light -instancename PcDiagnostic` (memory mode, circular buffers, its own instance name so it never touches a recording the user runs with WPR). The profile ships next to the program (Program Files when installed, so only administrators can change it) and uses the providers and keywords of the built-in GPU.Light and DesktopComposition.Light profiles plus CPU scheduling, sampling, DPC/ISR and disk, with a **fixed cap of 256 + 128 one-MB buffers**, no stack walks, and the GPU state captured again on save. First live run (6 Oct 2026) used the built-in profiles directly: they size buffers as a share of RAM (about 3.9 GB each on the 64 GB dev PC), and a save ran past the timeout while writing a 9 GB file; that design was replaced the same day. Measured with the capped profile: a minute with 20 s of full CPU and disk load saved in 24 s to a 108 MB compressed trace. A new (not merged) incident queues a save in the background so capture is never slowed: `wpr -stop incidents\<key>\traces\incident.etl "<description>" -skipPdbGen -compress -instancename PcDiagnostic`, then recording starts again. A save that fails or runs past 5 minutes is cancelled, its partial file deleted and the problem logged; the next incident still gets its trace. A trace left running by a killed Agent is cancelled and restarted. The ZIP export adds `traces/incident.etl` last (fastest compression).
- **True per-process I/O:** a real-time session `PcDiagnostic-ProcessIo` with `Microsoft-Windows-Kernel-Network` (TCP/UDP send/receive over IPv4/IPv6; the PID and size are the first two payload fields) and `Microsoft-Windows-Kernel-Disk` (events 10 read and 11 write; size at offset 8; the payload has no thread field, so the process is the one in the event header, which a decoded trace confirmed: a program's write-through writes carried its own PID, the system's background writes PID 4). The callback only adds to per-PID totals; the process collector takes them every process tick and stores rates in four new `ProcessSnapshots` columns (schema v8). The first read after a start is dropped (it covers less than a tick). Top processes gets a Network list and a disk share under Disk I/O; the incident's Processes tab, the technical report and processes.csv show them.
- **Overhead budget (§20):** the self-monitor now reads `Environment.CpuUsage`/`Environment.WorkingSet` instead of refreshing a `Process` object every tick, and the Agent runs with workstation non-concurrent GC and `GCConserveMemory=5`. `GET /api/v1/recorder/overhead?minutes=` summarizes the Agent's own CPU (average, 95th percentile, highest), working set and sampling time from the stored samples; the System page shows it against the 2 % budget.
- **Agent memory (7 Oct 2026).** Broken down by walking the Agent's address space and working set while it recorded (not elevated, 12 minutes, the ring full at 660 frames): working set 252 MB, private bytes 199 MB, private working set 144 MB. The GC heap held 75 MB, of which the ring buffer was about 63 MB (about 100 KB per frame) although most of each frame repeated the previous one: a new sensor descriptor, key string and reading for every sensor every second, and a new row for each of about 470 processes every 2 s. Images were 93 MB (23 MB private, of which nvml.dll 18.5 MB), unnamed shared sections 35 MB (not private), native heaps 23 MB. LHM's Intel GPU group also loaded Intel's control library, Level Zero and Intel's graphics compiler (about 18 MB working set, 48 MB private commit) only to read a fixed core clock of the integrated graphics. With unchanged objects shared between frames and the control library skipped unless an Intel discrete GPU is present, the same run measured: working set 189 MB, private bytes 104 MB, private working set 94.5 MB, GC heap 27 MB (ring estimate 16 MB, was 60 MB). What remains is mostly NVML (needed for clocks, power and throttle reasons), shared sections and native heaps.

### Custom recording (Phase 16)
- **What can be selected** (`RecordingItems`, stable IDs): CPU (`cpu.usage`, `cpu.cores`, `cpu.clock`, `cpu.scheduler`, `cpu.thermal`), memory (`memory.usage`, `memory.paging`), per GPU `gpu:{adapter key}.usage|vram|thermal|clocks|power|processes`, per disk `disk:{key}.activity|latency|temperature`, `volumes.space`, per network adapter `net:{key}.throughput|errors|link`, every hardware sensor `sensor:{logical key}` (a sensor's shared key, e.g. the CPU package temperature the CPU table uses, follows it), `processes.metrics`, `processes.lifecycle`, Windows events per category `events.{category}`, `display.config`, `services.changes`, `inventory.hardware`. The catalog of this PC (`recording-catalog` pipe command, `GET /api/v1/recorder/catalog`) comes from the devices and sensors the live feed has sampled; while idle the command starts the live view and waits up to 6 s for them. On the dev PC: 21 sources, 219 items.
- **How it records**: the scheduler builds each frame whole and publishes it twice: whole to the live view (`ISampleConsumer.SeesUnrecordedData`), and through `RecordingFilter` to storage, the ring buffer and the anomaly rules. A device or source with nothing selected is left out of the frame; within a source, unselected readings become `Availability.NotSelected` ("Not recorded: left out of a custom recording"), so History shows why a series has no value. The recorder's own figures are always kept. Unchanged objects keep their identity, so the ring buffer still shares them between frames.
- **When**: `POST /recorder/start` with `{ selection }` (pipe `start` with `Selection`, at most 2000 IDs of up to 400 characters; requests may now be 1 MB) starts a custom recording and remembers it as `Recorder:CustomSelection`; without a body it records everything. A user's start holds its selection in every mode until the user stops; another selection, or Record everything, ends the session and starts a new one. Mode Always records everything. In mode Scheduled each window (`RecordingWindow.Selection`) records everything or its own selection; overlapping windows record what any of them selects (everything when one of them records everything), and moving into a window with another selection starts a new session. `RecorderSessions.selection_json` (telemetry schema v9) keeps each session's selection; the status reports `recordingItems`.
- **Incidents**: an incident captured during a custom recording stores the selection (`IncidentInfo.recording_selection`) and leaves out the display and service snapshots it did not select; a capture that has to start the recorder records everything. The analysis states first what was not recorded ("not recorded, so nothing is said about them: ...") and makes its statements of absence (no display change or graphics driver event, no network events, no application crash or hang) only when that data was recorded. The incident page header says "custom recording (N items)".
- **Checked on the dev PC** (in-process, own data folder, not elevated, 7 Oct 2026): a selection of CPU usage, the RTX 4090's usage and temperatures, one disk sensor, process figures and display events stored exactly those (no memory, disk or network rows; the 4090's clocks and power empty; no process events or snapshots), the left-out CPU series were marked NotSelected, and the incident kept its selection.

### Moments of the rolling history
- Clicking the History chart (kept in the URL as `?at=`) or the dashboard's live chart (a dialog with "Open in History") shows everything recorded around that moment, incident or not: every chartable value and hardware sensor (±1.5 s; beyond the raw retention, the one-minute summary of that minute), and every process running then. The busiest processes (the top of each ranking, stored every process tick) come with their figures; the others are known from their start and end times. `GET /api/v1/history/moment?t=`; on real data 193 ms for 179 values and 485 processes (54 with figures).
- A process without an end time counted only if it is still running (the Web host checks the processes running now), because one that exited while nothing was recorded never got its end. The recorder now ends those itself: the first full process list after a gap (or after the Agent starts) ends the identities that are no longer running at that moment (`ProcessesEndedUnseen`).
- **Every process has figures** (7 Oct 2026): besides the busiest processes every tick, the storage consumer writes every process every 30 s when its figures changed and every 10 minutes when not (`FullListIntervalMs`, `FullListRefreshMs`; idle processes keep the same object, so they cost almost nothing). A moment takes each running process's latest row of the last 11 minutes (one grouped pass over the time range, no extra index) and shows when it was measured ("as of"). On the dev PC: 590 of 591 processes with figures (55 from the moment's own sample, 535 from the full list); the one without is the System Idle Process. Cost: about 1,930 process rows per minute instead of 1,630 (+19 %, plus the 10-minute refresh of roughly 60 a minute on average). A measured process without GPU use shows 0 %.
- **Services mapped to processes**: service changes keep the process the service runs in (`ServiceEvents.pid`, telemetry schema v10; a service found running in another process between two polls is a restart). A moment rebuilds the services then from the hourly snapshot plus the changes after it and maps each to its process by ID: a process lists the services it hosts, the Services tab names each service's process, each links to the other (filtered by process ID), and the process filter also matches service names. On the dev PC all 173 running services were mapped (e.g. one svchost.exe hosting BrokerInfrastructure, DcomLaunch, PlugPlay, Power, SystemEventsBroker).
- On the dashboard the moment opens in a floating window that does not block the page (clicking another point updates it): drag the title bar to move it, the corner to resize it, double-click to center; it remembers its place in this browser and stays on the screen.

### Period diagnostic (Phase 17)
- **What:** choose a period of up to 31 days, either in History (*Diagnose this period*, or *Diagnose the zoomed part* when zoomed) or on the Diagnostics page (From/To, or Last hour, 6 hours, 24 hours, 7 days). Everything recorded in it is analyzed aspect by aspect: recording coverage (gaps, custom recordings, unclean stops), incidents, CPU, memory, GPUs, storage, network, processes and applications, services, display and graphics driver, Windows events, hardware sensors, and the anomalies the rules detected. Each section has figures, tables and findings ranked by severity; the summary counts the findings by severity and lists the 12 most important. API: `GET /api/v1/diagnostics/period?from=&to=[&fresh=true]` and `GET /api/v1/diagnostics/period/export?from=&to=&format=html|pdf|json`. Page: `/diagnostics?from=&to=`.
- **Evidence** (`PeriodEvidenceReader`, read-only):
  - Every History series in up to 1,500 buckets: raw samples within the retention, one-minute summaries beyond it.
  - Recording sessions, events (importance 1 and above, up to 2,000), anomalies, incidents and boot recoveries.
  - From the raw data only:
    - Each process instance's first, last and peak figures. CPU time used is the difference of its cumulative CPU time; memory and handle growth come from the same first and last samples.
    - Process starts, exits and hangs per program; service changes per service.
    - Sensor ranges, the lowest free space per volume, and packet errors per adapter.
    - The 5 processes using the most at the CPU, GPU and disk peaks (the busiest sample in the bucket with the highest average, so not a one-second spike) and at the memory low point (the lowest sample).
- **Thresholds** (`PeriodDiagnostics`, Engine): the user's anomaly rules (Settings → Triggers: `cpu-high`, `cpu-temp-high`, `memory-low`, `memory-commit-high`, `gpu-high`, `gpu-temp-high`, `gpu-hotspot-high`, `gpu-vram-full`, `disk-latency-high`, `disk-queue-high`, `network-errors`). A bucket is over a threshold when its average is. Adjacent buckets join into runs, and runs lasting at least the rule's duration are episodes: one finding each with the total time, how often, the longest run and the extreme value.
- **Further checks:**
  - Memory: in use at or above 90 %; hard page faults above 1,000/s.
  - CPU: a performance limit below 95 %.
  - Private memory growth over at least 30 minutes, of at least 1 GiB, or at least 300 MB and double: a possible leak. A virtual machine host's growth is described as what runs inside the machine.
  - Handle growth of 10,000 or more.
  - Storage: disk temperature at or above 70 °C; free space below 10 % (High below 5 %).
  - Network: packet errors; Wi-Fi signal below 30 %.
  - Programs started at least max(20, 10 an hour) times; signatures Windows does not trust.
  - Services: failures, frequent restarts, start type changes.
  - Windows events grouped by title, with severity by category: a bug check is Critical; unexpected shutdowns, hardware errors, resource exhaustion, application crashes, display and graphics driver errors and disk errors are High.
  - Temperatures at or above 95 °C. Sensor limits and distances to TjMax are not temperatures, and unlabeled motherboard inputs ("Nuvoton … / Temperature #6") are doubted.
- **Wording and ranking:** every finding passes the language guard (one that does not is left out). The anomaly recap is neither counted nor in the top list, because the other sections cover the same aspects; incidents below High stay in their own section.
- **Report:** in the browser, sections fold (those with findings open). Each finding links to History around it with its series and moment picked, where the moment panel shows the processes running then; incident findings link to the incident. Export gives one self-contained HTML file (inline CSS, no script, an in-file CSP), a PDF (headless Edge) or JSON. Since Phase 19 a diagnostic is a background job that survives leaving the page (see *Usability round* below); its report is kept, so a download matches the report on screen, and *Run again* analyzes afresh. One analysis runs at a time.
- **Measured on the dev PC (7–8 Oct 2026):**
  - Timing: 6 hours analyzed in 1.3–2 s (55 series, about 18,000 process instances); 24 hours in 2.9 s; 7 days in 2.1 s (7-minute buckets).
  - The 6-hour report found:
    - Committed memory at or above 95 % for 11 minutes from 22:17, with vmmemWSL holding 38 GB at the low point.
    - VRAM at or above 97 % for 15 minutes, and 5 RTX 4090 clock drops under load.
    - G:\ down to 8.7 % free.
    - docker.exe started about 6,500 times (about 1,100 an hour).
    - A dllhost.exe hang.
    - An unlabeled Nuvoton input reading 125 °C, reported as doubtful.

### Licensing removed (open source)
PC Diagnostic is free and open source under the GPL-3.0 (`LICENSE`). The commercial licensing built in Phase 18 (signed
keys, online activation, a trial, Standard and Pro editions) was taken out when it was published: every feature
(recording, Enhanced mode, access from other devices, the period diagnostic) works for everyone, with no key and no
time limit.

### Usability round (Phase 19)
Requested 8 Oct 2026.

- **Period diagnostics run in the background.**
  - `POST /api/v1/diagnostics/jobs {from, to, fresh}` starts a job (202). It answers 429 when 4 are already running.
  - The page shows the job (`/diagnostics?job=…`). Leaving the page doesn't stop it, and coming back shows its progress, then the report.
  - *Cancel* stops a job, and the same call (`DELETE /jobs/{id}`) removes a finished one. `GET /jobs` lists recent runs and `GET /jobs/{id}/export` downloads one.
  - Jobs move through Queued, Reading, Analyzing, then Completed, Failed or Cancelled. One analysis runs at a time.
  - A request for the same period within 5 minutes reuses the last run (unless *Run again*).
  - Finished reports are saved to `{data}\diagnostics\{id}.json`, so they survive a restart. The last 20 are kept.
- **Grouping and sorting in every table and list** (`web/src/components/data`). It covers process lists, moment values and services, the System tables, top processes, incidents, incident timeline, events, findings and reports, History events, recent events, anomalies, rules, paired devices, recent diagnostics and the custom widgets' tables. Since 9 Oct 2026 also: the dashboard's sensors, graphics, displays, network adapters and incidents widgets; the widget catalog and My widgets; Tune-up's processes, services, startup, clean-up targets, drives and advice; problem reports; the Motherboard tab's temperatures, fans, voltages, memory modules and expansion slots; the device register, each device's parts and changes, and recent device changes; what needs attention and the displays on the System page; and an incident's folder details. Only selection pickers (History series, a widget's values, a custom recording's sources) keep their own folding groups.
  - Drag a column header, or in a list a field chip, onto *Group by* or *Sort by*.
  - Drag the chips to reorder them, or use the arrow keys.
  - Click a header to sort, and Shift+click to add a further key.
  - Groups nest and fold.
  - Each table opens with its own default, remembers the user's choice in the browser (`pcdiag.view.{table}`), and *Reset* returns to the default.
  - Values like "38.9 GB", "2 h 5 min" or "8.8 %" sort as numbers.
- **System page.**
  - **Tabs** (kept in `?tab=`):
    - **Overview:** the PC by name, its Windows version, a *Needs attention* list, and a spec sheet covering processor, memory, graphics, storage, network, motherboard and BIOS, displays and monitoring.
    - **Hardware:** devices (a removed one is marked), memory modules, displays and every inventory detail.
    - **Drivers.**
    - **Sensors:** filtered by kind or text.
    - **Readings.**
    - **Recorder cost.**
  - **Needs attention** lists unavailable readings, devices not seen for a day, unhealthy drives and displays recorded over Remote Desktop.
  - **Readings** (`readings.ts`) names each unavailable reading by device and sorts it into one of three groups:
    - *Unavailable:* it applies to this PC. It shows the reason and what can be done.
    - *Does not apply:* Wi-Fi on wired or virtual adapters, a fan, hotspot, memory clock or power limit on integrated graphics, and the read/write error totals and temperature limit on NVMe drives.
    - *Removed device:* for example the RTX 4090 that was listed again under a new key after a driver update.
  - The incident page's System tab uses the same view for the PC at the time.
- **Settings page.**
  - **Layout:** a topic list (Account; Recording; Data; Advanced) with a search ("hotkey", "retention"…) that narrows the topics shown. The topic in view is marked while scrolling.
  - **Rows:** each setting sits in a row with what it does on the left and its control on the right. Ranges are shown, and on/off settings are switches.
  - **Unsaved changes:** topics with unsaved changes carry a mark, and the save bar names them.
  - **Links:** `#triggers` and `#network` still lead to their topic.
- **Widgets you build.** *Add widget* has two lists: **Preconfigured widgets** (the 13 built-in ones) and **Build your own**.
  - **Designs:** start from one of 11 — number, dials (arc, ring, speedometer, thermometer), bars, line chart, area chart (optionally stacked), column chart, table, status tiles, process list, event feed and text note.
  - **Values:** choose up to the design's limit from every recorded series and every hardware sensor (`GET /api/v1/history/catalog?sensors=true`). The live-only values (memory in use, download and upload, running processes, per-GPU VRAM share, link speed) are offered too.
  - **Look:**
    - Label and color per value.
    - Palette, widget accent color, title and text size.
    - Window (1 minute to 1 hour), and what is shown (current, average, lowest or highest).
    - Decimals, legend, smoothing, stacking and trend lines.
  - **Scale and limits:** warning and critical limits in the direction that is worse (higher, or lower for fan speed or free memory).
  - **Preview:** the builder shows the widget with the live data while it is edited.
  - **Where values come from:**
    - Values the live feed has update once a second: the browser keeps 10 minutes of every live value for all widgets, and the headline values start from the Agent's trend.
    - The rest come from the recording, polled every few seconds while recording, which also fills in the time before the page was opened.
  - **Source ids** stay valid across databases and restarts:
    - Whole-PC values keep their series id (`cpu.temp`).
    - Devices use their stable key (`gpu:{adapter key}:temp`, `disk:{disk key}:active`, `net:net-{GUID}:rx`).
    - Sensors use their logical key (`sensor:lhm/gpu-nvidia/0/fan/1`). The few keys over 100 characters keep the recorded id.
  - **My widgets:** built widgets are kept in `dashboard.json` (`library`), so they can be added again, edited or deleted from *Build your own*.
  - **Accent color for every widget:** every widget's settings now include one, and preconfigured widgets take it too.
  - **Validation** (Web host): the `custom` type, the library (at most 48 widgets), settings up to 4 KB, texts up to 500 characters, and source ids up to 120 characters.
- **Readings the recorder now takes** (the user's list of unavailable readings):
  - **NVMe drives:** the drive's own SMART/health log (log page 02h through `IOCTL_STORAGE_QUERY_PROPERTY`, StorageDeviceProtocolSpecificProperty, elevated). It fills temperature, wear (life used) and power-on hours when Windows' reliability counters are empty, as they are for NVMe drives on the standard driver. It also adds available spare, media and data integrity errors, unsafe shutdowns, critical warning flags, data read and written, power cycles and error log entries.
  - **Compressed memory:** the working set of the *Memory Compression* process, read every 10 s from the system process list. No process handle is needed, which that protected process refuses even to administrators' limited queries from a standard user.
  - **Intel integrated graphics power:** LibreHardwareMonitor's *GPU Power* (and NVIDIA's *GPU Package*) is an alias keyed by adapter name. It fills the adapter's power when the driver reports none, in the database and the live feed.
  - **Still unavailable on this PC, by nature:** the Intel iGPU's temperature and clocks. Intel exposes them only through its own control library (IGCL), which isn't available to other programs through Windows, so the Readings tab says so instead of calling it a fault.
- **Platform (the user's questions).**
  - **Linux:** the recorder is Windows-only by design. Its sources are Windows APIs: PDH, D3DKMT, WMI, ETW, the event logs, Display Configuration and PawnIO/LibreHardwareMonitor. Only the Web viewer could run elsewhere (it already runs in a container). A Linux recorder would be a separate collector set (`/proc`, `/sys`, hwmon, NVML, `journald`, DRM) feeding the same storage and UI. That is a sizeable project, not a setting.
  - **Unsupported drivers or sensors:** the product doesn't download drivers. The sensor providers it ships (LibreHardwareMonitor with PawnIO, NVML, the Windows storage counters and now the NVMe log) are updated with the product, signed. The Readings tab says for each missing reading whether anything can be done: run elevated, repair PawnIO, update the vendor's driver, a Windows privacy setting, or nothing because it doesn't apply.
  - **Possible later:** a signed sensor-provider update channel and an optional HWiNFO shared-memory provider, for PCs whose sensors LibreHardwareMonitor doesn't know.
- **Tests:**
  - Storage: the sensor catalog and sensor series.
  - Web: custom widgets and the library, background jobs (survive a restart, cancel), 404s.
  - Collectors: the NVMe log parser and a live read, compressed memory, GPU power keys.
  - UI: DataTable, readings classification, the widget model and sources, building a widget end to end, System tabs, Settings search and unsaved marks.

### Control and tune-up (Phase 20)
Requested 8 Oct 2026: control processes and services from the process lists, and a page of tweaks that make the PC faster.

- **Who does it.** The elevated Agent carries out every action through fixed IPC commands (`control-processes`, `control-services`, `process-action`, `service-action`, `tuneup-scan`, `tuneup-apply`). The Web host only asks; it has a second pipe connection for these commands, with longer timeouts, so the live feed never waits behind a clean-up.
- **Safety.**
  - A process is named by PID **and** start time, checked on the opened handle, so a reused PID is never hit.
  - `ControlPolicy` sorts every process and service into *Protected* (refused: Windows' critical processes, security software, the recorder itself), *Caution* (part of Windows: allowed after the user confirms that Windows may misbehave until restarted) or *None*.
  - Every action is confirmed in the browser first, and is refused from other devices on the network (POSTs to `/api/v1/control` and `/api/v1/tuneup` are local only).
  - Every action is recorded as an event (channel `pcdiag.control`, "You ended …"), so an incident shows what the user did.
- **Processes and services.** From the dashboard's top processes, a moment's process list or the Tune-up page: end a process (or its whole tree), change its priority; start, stop or restart a service and change its start type. Each row says what the program is (an app with a window, a background program, part of Windows, a service host) and why it is protected.
- **Tune-up page** (`/tuneup`), tabs:
  - **Advice:** what would help now, from the live values and a scan: memory pressure and who holds it, programs not responding, busy background programs, WSL's virtual machine, full drives, what can be cleaned, hibernation on a desktop, the power plan, Game Mode, crowded startup, background services. Each leads to the tab or action that deals with it.
  - **Processes**, **Services**, **Startup** (registry Run keys and Startup folders, enabled or disabled the way Task Manager does it).
  - **Clean up:** user and Windows temporary files older than 7 days, the Windows Update download cache, Delivery Optimization, thumbnails, shader caches, browser caches, crash dumps, the recycle bin. Sizes are measured first; clean-up never follows junctions or symbolic links and never leaves its root.
  - **System:** power plan, hibernation (and the size of `hiberfil.sys`), Game Mode, hardware-accelerated GPU scheduling, empty the standby memory, clear the DNS cache, shut down WSL.
- **Optimize** (15 Oct 2026, `GET /api/v1/tuneup/plan`, IPC `tuneup-plan`): what each step would act on now, from the recorder's newest samples.
  - *GPU memory:* background programs holding dedicated GPU memory (64 MB or more), most first. Windows lets a program give GPU memory back only by ending it, so the step ends the ones chosen (`free-vram`). *Restart the graphics driver* (`restart-gpu`, `pnputil /restart-device`, after an acknowledgement) gives back memory a driver kept; the screens go dark for a moment.
  - *Memory:* trims the working sets of the programs holding 200 MB or more (`trim-memory`, `EmptyWorkingSet`: idle pages go to the standby list, given straight back if needed); empties the standby memory; shuts WSL down.
  - *CPU:* busy background programs (no window, 5 % CPU or more) to below-normal priority until they end (`lower-priority`).
  - *Drives:* retrims solid-state drives (`retrim-drives`, `defrag /L`); hard disks are left to Windows' scheduled defragmentation.
  - *Network:* clears the DNS cache.
  - *Optimize now* runs the steps that never end a program, one after another, and lists what each did and left.
- **Safe mode** (on by default, the switch at the top of Tune-up, remembered in the browser): every tune-up and control action carries it, and the recorder (`ResourceGuard`) leaves what other programs are using now and says why (`ControlResult.Skipped`): the program in front, a program with a window open (it is not ended: unsaved work), a program busy with the CPU (2 %), the GPU (2 %), the network (10 KB/s) or a disk (256 KB/s); a busy service process; a running browser's cache, shader caches while a game draws, Windows Update's downloads while it works, crash dumps while a report is written; the standby memory while a game draws or a drive is busy; the graphics driver while any program uses the GPU; WSL while its virtual machine works or a WSL or Docker window is open; a drive more than 20 % active.
- **What a clean-up target holds** (`GET /api/v1/tuneup/cleanup/{id}/files?root=&path=`, IPC `cleanup-files`): each target opens as a tree of its folders, folder by folder, with each entry's size, what the clean-up would remove of it (old enough, of its kind), its files and when it changed; folders too big to measure in 4 s show a lower bound (+); the 300 largest entries per folder. The path asked for must stay inside the target's own folder (no "..", no rooted path, no junction or symbolic link on the way).
- **Measured on the dev PC:** 509 processes and 365 services listed; the scan took 7 s and found 55 GB of old user temporary files, 29.5 GB of crash dumps, 5.9 GB of Delivery Optimization files, 35 startup entries and a 27 GB hibernation file.

### Problem reports (Phase 21)
Requested 8 Oct 2026: the user can report a problem to the makers, and with the user's consent problems are reported automatically, with their cause and details.

- **Written by the user:** *Report a problem* in the header. A line about the problem (required), details, an optional contact, and two choices: include the recent log, include the PC's description. *Preview* shows exactly what will be sent.
- **Caught automatically:** a Serilog sink in both hosts (`ProblemCapture`) turns every logged error and every crash (unhandled and unobserved task exceptions) into a report in the local outbox (`{data}\reports\outbox`), with the exception chain and stack as the cause, the 60 log lines before it, and the product and environment facts. The dashboard reports its own script errors (`POST /api/v1/reports/client-error`). Reports are fingerprinted (component, exception type, top frames, numbers removed), so the same problem counts occurrences instead of piling up; at most 20 new reports an hour.
- **Consent:** asked once (a banner, "What is sent?" explains it). Without consent, automatic reports stay on the PC and can be downloaded or sent one by one from Settings → *Problem reports*. The choice can be changed there at any time.
- **Privacy:** reports never contain what the recorder recorded about the PC. User names, the computer name, e-mail and IP addresses are removed before a report is saved (`ReportRedactor`).
- **Sending:** the Web host posts to the makers' report service (`Reporting:Endpoint`, HTTPS; set at build time with `installer\build.ps1 -ReportEndpoint`). Sent reports move to `reports\sent`; a failed send is retried later.
- **The makers' side:** `tools\PcDiag.ReportService` (ASP.NET Core, Dockerfile) stores each report and files a new problem as an Azure DevOps work item (Bug) with the report rendered as HTML; a report of a problem already filed adds a comment with the new occurrences and versions instead. Configured through `AzureDevOps:Url`, `:Project`, `:Pat` (environment variables).

### Motherboard and devices (Phase 22)
Requested 8 Oct 2026: monitor and register the motherboard, and monitor and register input/output devices, in detail.

- **Motherboard facts** (inventory, every 6 h and at start): maker, model and revision; the **chipset** (from the LPC/eSPI bridge's name when Intel's chipset driver names it, else from the board's model name, which nearly always carries it, else the Intel chipset family from the bridge's PCI ID); firmware type (UEFI or legacy BIOS, `GetFirmwareType`) and Secure Boot; TPM version, maker and state (`Win32_Tpm`, elevated); processor socket and virtualization (on, on with a hypervisor running, or off in the firmware); memory slots used and total and the most memory the board takes (`Win32_PhysicalMemoryArray`); expansion slots as SMBIOS lists them (`Win32_SystemSlot`); SMBIOS version.
- **Board monitoring** (System → *Motherboard*, refreshed every 5 s on the System page): the board's monitoring chip (Super I/O or embedded controller, from LibreHardwareMonitor's `lpc` hardware, e.g. Nuvoton NCT6701D) with its temperatures, fans and voltages, each judged:
  - **Supply rails** (+3.3 V, 3 V standby, AVCC, +5 V, +12 V) against ATX limits: within ± 5 % normal, beyond it worth a look, beyond ± 10 % act now.
  - **CMOS battery:** below 2.8 V getting low, below 2.5 V replace it (the clock and firmware settings are lost when the PC is unplugged).
  - **Core voltages** (Vcore, termination, DRAM) are for information: they follow load and settings. **Unlabeled inputs** ("Voltage #11") are named as such.
  - A reading far from any sensible value (a 12 V rail reading 1 V, a temperature of 3 °C) is marked **not a real reading**: the chip's input is wired or scaled differently on this board, or nothing is attached to it.
  - **Fans** are shown with the duty the board drives them at; 0 RPM at 68 % is "no fan on this header, or a stopped fan", not a fault.
  - **Memory:** modules running below the speed their part number states (Corsair …5600C36, G.Skill F5-6000…, TeamGroup …6000HC38) mean the XMP/EXPO profile is off; modules of different sizes run partly in single channel; modules from different kits are the first suspect when XMP is unstable. On the dev PC: 16 GB + 48 GB rated 5600, running at 4800.
- **Device register** (System → *Devices*): every input and output device connected now **or before**. Windows keeps a node for every device it has seen, with when it was first installed, last connected and last removed; the register is read from the device tree (CfgMgr32: `CM_Get_Device_ID_List`, `CM_Get_DevNode_Property`; no WMI, about 0.3 s for 840 nodes) at every inventory and within seconds after a device change.
  - **One device, not its parts:** Windows creates many nodes for one device (a wireless receiver: a composite parent, interfaces, HID collections, a keyboard and a mouse). Nodes are grouped by walking up to the node on a hub port; Bluetooth services join their device by address; monitors by model; a network printer's nodes by its container. Windows' own container IDs are not used for this: everything behind a hub with built-in ports shares one. Disconnected nodes have no parent, so USB devices are keyed by vendor and product ID, which also brings together the nodes left by earlier connections on other ports.
  - **Named by the device**, not the driver: "USB Composite Device" becomes the product name the device reports ("Razer Huntsman V3 Pro TKL"), "Generic Monitor (PG32UCDP)" becomes "PG32UCDP"; the maker comes from the vendor ID (USB, PCI, a monitor's PnP code).
  - **Detail per device:** kind (keyboard, mouse, game controller, audio, camera, monitor, drive, phone, network, Bluetooth, printer, serial port, security key…) and every kind its parts have, connection (USB, Bluetooth, Bluetooth LE, HD Audio, built in over I²C), vendor and product ID, port, driver (provider, version, date, INF), the parts Windows created, Device Manager's problem code and what it means, first installed, last connected, last removed, and when the recorder registered it.
  - **Audio endpoints** ("Speakers (2- Realtek(R) Audio)", which the elevated recorder sees as nodes of their own) become parts of the device they name; endpoints of programs (Steam Streaming, Remote Audio) go with those programs' virtual devices.
  - **Plumbing is hidden** until asked: hubs, controllers, composite parents, HID collections, Bluetooth services and programs' virtual devices (Logitech G HUB, Razer, Remote Desktop, virtual audio).
  - Devices of the PC itself appear only when they have a problem (a disabled Wi-Fi card, a controller with code 43); a device that failed to start ("Unknown USB Device (Device Descriptor Request Failed)") is called out.
  - Stored in `IoDevices` (telemetry schema v11), one row per device, updated when the register changes; devices no longer listed are marked gone.
- **Device monitoring** while recording (`DeviceChangeCollector`): Plug and Play notifications (`CM_Register_Notification` for all device instances, else all device interfaces; no window) say when something changed; once the tree has been quiet for 1.5 s it is scanned and compared with the previous scan, and every 30 s anyway, so a missed notification or a device that fails without being removed is caught. Events (channel `pcdiag.devices`, category Device), one per physical device:
  - *Device connected* (with what it is: "LIGHTSPEED Receiver (keyboard, mouse)"), *disconnected*, *dropped out and came back* (removed and back between two scans),
  - *keeps disconnecting* (3 drops in 10 minutes; a warning with the usual causes: cable, connector, port power, USB selective suspend),
  - *device of this PC disappeared* (real hardware on PCI, USB, HD Audio or storage leaving while Windows runs: driver crash or reset, power saving, overheating, a loose card),
  - *stopped working* (a problem code appeared, e.g. 43), *disabled*, *works again*.
  - At most 30 device events a minute. Programs' virtual devices coming and going are not news.
- **UI:** System → *Motherboard* and *Devices* tabs (filters: all, connected, not connected, with a problem; grouped by status and kind; details and recorded changes per device; recent changes). The overview's *Needs attention* names a device that is not working, and its spec sheet gains *Devices* and the chipset.
- **API:** `GET /system` carries `ioDevices`; `GET /system/sensors?prefix=` (newest sensor values only, for the board's readings); `GET /system/device-events?limit=`.
- **Measured on the dev PC:** 96 devices registered (52 shown, 44 plumbing), among them earbuds, headphones and monitors last connected months ago and the devices of the previous motherboard; chipset Intel Z890 (Windows names the bridge only "PCI standard ISA bridge"); the disabled Wi-Fi card is the one device of the PC listed.

### On-screen readings (Phase 23)
Requested 9 Oct 2026: show readings on the screens, arranged on a page that shows the monitors as monitor shapes, where readings are added, removed, moved and styled; with several screens, each screen has its own.

- **Who draws:** the recorder, on its desktop, on a UI thread of its own that is per-monitor DPI aware (panels are placed in physical pixels and grow with each monitor's scaling). Each panel is a layered window (UpdateLayeredWindow, per-pixel transparency) that is always on top, never takes the focus or a click (WS_EX_TRANSPARENT, HTTRANSPARENT), is not in Alt+Tab or the taskbar, and is lifted above other always-on-top windows every two seconds. Text is drawn as outlines (GDI+), so it can carry a dark edge that keeps it readable over any picture.
- **Values:** from the recorder's newest samples (`OverlaySources`): the processor (load, clock, temperature, power), memory (in use, used, free, commit), each graphics adapter (load, temperature, hot spot, VRAM, power, clocks, fan), each drive (active, read, write, temperature), each network adapter (download, upload), every hardware sensor by its logical key (fans, voltages, the board's temperatures…), the time and date, and the recorder's state: 312 values on the dev PC. While panels are shown the recorder samples in memory only, like the dashboard's live view; nothing is recorded unless recording is on.
- **Monitors:** EnumDisplayMonitors and GetDpiForMonitor give each monitor's area and scaling; the display configuration names it and gives its stable key (its device path), so a layout follows the monitor and comes back when it is connected again. They are checked every 2.5 s (a monitor connected, removed, moved or rescaled moves its panels with it). *Identify screens* shows each monitor's number, name and mode in its middle for 4 s.
- **Layout** (`overlay.json` in the data folder, written by the Web host after validation, read by the recorder, which also notices the file change):
  - Per monitor, up to 12 panels; per panel, up to 16 readings, a title, one reading per line or all on one line, hidden or shown.
  - A panel's position is its share of the room it leaves (0 at the left or top edge, 1 at the right or bottom edge), so a panel at an edge stays at that edge whatever its size, scaling or the monitor's resolution.
  - Each reading: its label, decimals, warning and critical limits (bad when higher, or when lower), a bar (percentages, or with a full scale), its own color.
  - Each panel's look: font (from the fonts every Windows 10/11 PC has), size, colors for labels, values, units, title, warning and critical, background and its opacity, border, corners, padding, overall opacity, dark outline, labels and units on or off; five ready-made looks (Card, Classic OSD, Minimal, Neon, Light).
  - Options: on or off, the show/hide hotkey (Ctrl+Alt+Shift+O), refresh every 0.25, 0.5, 1 or 2 s, and *keep out of screenshots and recordings* (WDA_EXCLUDEFROMCAPTURE).
- **Page** (`/on-screen`): the screens as they are arranged in Windows, drawn to scale as monitor shapes with miniatures of their panels; the chosen screen large, as a monitor with bezel and stand, with its panels where they will be (drag them, or select one and use the arrow keys; preview true to scale, 2× or 3×); templates to start a panel (essentials, temperatures, gaming, network, clock); the panel's readings (a grouped, searchable list of every value with its reading now), place (a 3 × 3 snap grid, another screen) and look; panels of screens that are not connected are kept and can be moved or deleted; all panels in one table that groups and sorts. Changes are saved half a second after the last one and appear on the screens at once.
- **Formatting** is the same in the recorder and the page (bytes and rates scaled, the unit's usual decimals, halves rounded away from zero).
- **Verified on the dev PC** (9 Oct 2026): a panel in the top-right corner of the 4K main screen at 150 % and a line on the second screen, both with live values over other windows; Identify on both; with *keep out of screenshots* on, the panels were missing from a screen capture and still on the screens.

#### Redesign (Phase 25)
Asked for 9 Oct 2026: the first look was plain; redesign it, with everything in the new look changeable in the editor.

- **Three layouts:** one per line (icon, label, inline graph, value, ring; bars under), **tiles** (a grid of 1–6 columns, each tile with its icon, label in capitals, a large value and its ring, bar or graph; a tile past a limit is outlined in the warning or critical color) and all on one line.
- **With each value** (per reading): nothing, a **bar**, a **ring** (a circle filling up) or a **graph** of its recent history (15 s to 5 min; the recorder keeps one point per refresh while the graph is on a screen); a full scale for non-percentages; an **icon** (15 drawn icons: processor, graphics card, memory, temperature, fan, power, clock, calendar, drive, download, upload, gauge, activity, recording, frames), chosen by what the reading measures or by hand.
- **Look** (per panel, every part a setting): accent color (bars, rings, graphs, icons, edge, title band), a gradient background (second color and direction), a glass sheen, a soft shadow, a glow in each value's color, a border width, an accent stripe along an edge, the title on an accent band (its text kept readable against the accent), spaced capitals, values' size (80–300 %), spacing, icons and their color, bar style (rounded, square, segments) and thickness; and everything the first look had.
- **Nine themes:** Aurora (new panels start with it), Glass, Carbon, Neon, Frost, Terminal, Classic OSD, Minimal and Card (the first look). The editor shows each one **applied to the panel being edited** and can give one look to every panel; the themes come from the recorder's own list (`GET /overlay` returns them), so the two never differ.
- **Same drawing in both places:** the recorder draws with GDI+ (`OverlayRenderer`; icons from SVG path data through `SvgPath`, gradients at CSS angles, shadows from layered soft rectangles, glows from widening faint strokes); the editor's preview uses the same metrics and rules in HTML, CSS and SVG; a test keeps the editor's icon data equal to the recorder's.
- **Templates:** Essentials (with bars), **Gaming HUD** (tiles with rings), **Live graphs**, Temperatures (bars to 110 °C), Gaming, Network (with graphs), Clock (large), Empty.
- Older layouts keep their look: every new setting has a default that draws as before.

### Storage manager (Phase 24)
Asked for 9 Oct 2026 ("we need to involve a storage manager to save our data; is it recommended?"). The data stays in SQLite, a database engine inside the app (telemetry.db, incidents.db, one incident.db per incident): a database server (SQL Server, PostgreSQL) would add an installed service, hundreds of MB of memory and a network port to secure, for one PC with one writer saving in 5-second batches; it only pays off for collecting many PCs in one place, which would be a central service the PCs send to. What was missing was managing the space, now done by the recorder:

- **Limits** (Settings › Storage): *most space used* (`Storage:DataLimitGb`, 10 GB, 0 = no limit), *keep free on the drive* (`Storage:KeepFreeGb`, 5 GB, 0 = only the 1 GB below), *most incidents kept* (`Storage:MaxIncidents`, now enforced), and the retention of full detail (24 h) and summaries (30 days).
- **`StorageManager`** (Engine, a hosted service in the Agent): every 30 s it reads the drive's free space; every 10 minutes, 5 s after the limits change, as soon as the drive drops below the free space to keep, and when asked, it applies the limits:
  1. the most incidents kept: the oldest unpinned, finished incidents beyond it;
  2. folders left by deletions whose files were still open (no incident in the index, over an hour old);
  3. space for the limit (down to 95 % of it) and for the free space to keep: first full detail is kept for a shorter time (down to 2 hours, so an incident's pre-window is still there), then summaries and events (down to 7 days), each round measured after compacting; then, **for the space limit only**, the oldest unpinned incidents. Incidents are never removed for the drive's free space, and pinned ones or the one still recording never are.
- **Below 1 GB free nothing new is written**: the telemetry writer keeps the last 30 minutes in memory, a new incident is refused with a clear message (a trigger into the incident still recording still joins it), and writing resumes at 1.5 GB. The condition is in every status (`AgentStatus.Storage`) and every page shows a notice: *Not recording to disk*, *The drive is getting full*, *Over the space limit*.
- **Measured from the files** (`StorageMeter`): the data folder by kind (incidents, recording database, incident list, logs, diagnoses, problem reports, settings) and per incident, without opening anything; links are not followed.
- **Compacting** gives the recording database's free pages back to the drive in steps and empties the write-ahead log; it is safe while recording (no exclusive lock, unlike `VACUUM`).
- **Clean-up log** (`storage-log.json`, the last 100): what was removed, why, how much space came back, automatic or asked for; repeated automatic trims of one kind within an hour are one entry with a count.
- **Deleting** an incident whose file is open elsewhere (the Web host's pooled connection) removes it from the index and leaves its folder to the manager; the Web host now releases its own idle connections to the file first.
- **Page** (`/storage`, in the sidebar; on phones from Settings and the notice): the space used against the limit, the drive (PC Diagnostic by kind, other files, free, the free space to keep), how far full detail and summaries go back, the free space inside the database; what uses the space; incidents by size (select unpinned, delete after confirming, pin and unpin); recent clean-ups; the rules in the order they apply. Every list groups and sorts. *Clean up now* applies the limits and compacts.
- **Measured on the dev PC** (9 Oct 2026): 680 MB in all, incidents 450 MB (28), the recording database 202 MB with a 17 MB write-ahead log, logs 7 MB.

### Live chart, dashboard load and on-screen panels, round 2 (Phase 26)
Reported 9 Oct 2026, with a video and two snapshots.

- **Live chart:** old points kept changing while new data arrived. ECharts matched points by position, so each update slid every point into its neighbour's place. Points are now keyed by their time (`keyedChartData`), so only new points enter and the old ones stay put; the first draw has no animation.
- **Dashboard load:** the components appeared one after another from left to right. The staggered entrance and the gauges' count-up are gone: everything shows at once. Rows that arrive while a list is shown (events, anomalies) still slide in (`useArrivals` tells them from the opening rows).
- **Panels kept resizing** as values changed. Text is now measured by its advance width (not its ink), with every digit counted as the widest, and an automatic size only grows while the panel is shown; it starts again when its readings or the monitor's scaling change.
- **Size:** automatic, or a fixed width and height in pixels at 100 % (dragging the panel's corner in the editor); content is clipped to the panel's shape.
- **Opacity:** quick choices (text only, see-through, glass, solid) and sliders for the background, the tiles and the whole panel; the tiles' color (automatic: light on dark backgrounds, dark on light ones).
- **Reads as:** a reading with a total shows its value, a percentage, both, or "of" its total (VRAM *21.2/23.6 GB*, RAM used of installed); totals come with the values (`OverlaySource.Max`).
- **Grid designer** (Layout tab): tiles in 1–6 columns, rows in 1–4 columns; a tile spans 1–4 columns and rows, and a larger tile shows its value larger (+60 % per extra cell of its smaller side). Tiles are arranged by dragging or with arrow buttons and packed densely, as CSS grid's `row dense` (`Pack`, the same in C# and TypeScript).
- **Shown with**, asked the same day ("consider the options like the ones in the widget designs list"): 12 designs in a grouped picker with a drawn sample of each, as the dashboard's widget designs list them:
  - value only;
  - dials: ring, arc dial (250°), speedometer (220° with ticks and a needle), status light (OK, warning or critical against the limits, its own OK color);
  - graphs of the recent values: area, line, columns (each the highest of its slice);
  - under the value: bar, thermometer;
  - text after the value: *lo 41 · avg 52 · hi 78* and the trend (*▲ 12*).
  A design that cannot be drawn (no full scale for a dial, text for a graph) falls back to the value alone; the recorder and the editor draw each the same way.
- **Verified on the dev PC** (9 Oct 2026): a tiles panel with every design and a two-column rows panel of the gaming readings, on the 4K screen at 150 %; sizes stayed put while values changed; the status light turned with the limits; filmed in the browser, the dashboard showed complete in its first frame and the live chart's old points kept their places.

### Each reading's own look (Phase 52)
Requested 15 Oct 2026: customize each value's look apart from the panel's Look section, for each kind of visual.
- **Model:** `OverlayReading.Look` (`OverlayReadingLook`); every field is optional and null follows the panel: font, value size (50–300 % of the panel's), bold, value color (the visual keeps the reading's *Color*; limits still color the value), label size (50–250 %) and color, unit color, label and unit shown, alignment; the visual's size (dial, chart or status light, 50–250 %), thickness (rings, arcs, bars, thermometers, graph lines, 25–400 %), track or baseline color and opacity (22 % by default), bar style, and a graph's area opacity (43 % by default). `OverlayStyles.Validate` checks each.
- **Drawing:** the recorder derives the reading's own `OverlayStyle` and `Metrics` from the panel's (the panel's text size, places and gaps stay) and each item carries them, so every visual draws with its reading's look; row, line and tile layouts size each part by its own reading (a row's ring column is as wide as its widest ring). A smaller dial or chart in a tile shrinks inside it; a larger one makes the tile larger. The editor's `PanelPreview` does the same (`lookOf`), in the same order.
- **Editor:** Readings tab › a reading › *Its look*: Value (font, value color, size, bold, where it sits), Label and unit, and the visual's own settings, only those that apply to how it is shown (ring, arc, speedometer: dial size, thickness, track; bar: thickness, style, track; thermometer: thickness, track; graph and line: chart size, line thickness, baseline, and the area for a graph; columns: chart size, baseline; status light: its size). *Same as the panel* clears it.

### Saving, value placement, character movement and the generated characters (Phase 53)
Requested 15 Oct 2026, with a screenshot of tiles whose values sat at different heights.
- **Saved when asked:** the On-screen page no longer saves each change. *Save changes* saves the layout (the screens show it then), *Discard changes* goes back to the layout as saved, and the status reads *Changes not saved*. Choosing another panel, or leaving the page (TanStack Router's `useBlocker`; the browser asks when the tab closes), with changes not saved asks *Save your changes?* (Save, Don't save, Keep editing). Arranging on the screens waits for a save, since it arranges the saved panels.
- **Up and down in tiles:** `OverlayStyle.ValueVertical` (top, middle, bottom; null: the value alone in the middle, over a bar or thermometer at the top), and a reading's own `OverlayReadingLook.Vertical`. Over a bar the value sits in the room above the bar. A dial's value stays in its dial and a chart's above its chart. Look › Text › *Up and down in tiles*; a reading's *Its look* › *Up and down*. Left, centre and right (*Values sit*) were already offered.
- **Character movement:** `OverlayCharacter.Plays` (the animations it uses: it pauses doing only those, walks or runs only when one is there, otherwise it stays; it hops around a panel only when it may jump), `Tempo` (its animations' speed, 25–300 %, apart from its moving speed) and `Pause` (how long it stops between walks, 25–400 %). `CharacterMotion` and `behaviour.ts` follow the same rules, checked against `motion-parity.json`.
- **The shipped characters** are the eight generated ones, approved on 15 Oct: Mochi (a bunny) and Biscuit (a shiba), the ids the old drawn pair had (panels using them keep them), Mikan (a cat), Kuma (a corgi), Yuki, Akari, Hana and Ren, about 31 MB in PcDiag.Core. The drawn SVG pair stays only for previewing the effects (`web/characters`); baking now makes the effects only.

### Characters kept on their screen, and a screen's own characters
Reported 15 Oct 2026 with a screenshot: a character added on top of a panel that filled a USB screen from its top edge was not seen: it stood above the screen.
- **Kept on its screen:** `CharacterMotion.FrameOf` and `behaviour.ts`'s `frameOf` keep a character's feet at least its height below the screen's top, above its bottom, and its middle within the screen's sides; on a panel at the top edge it stands just inside the screen.
- **A screen's own characters:** `OverlayScreen.Characters`, apart from any panel's, along the screen's bottom (`screen`) or its top (`top`), reacting to the readings of every panel it shows (`OverlayScreen.Stage`: a stand-in panel, id `~screen`, the whole screen). Monitors show each in its own window in front of the panels (on the working area: not behind the taskbar); USB screens draw them over their panels into the picture. In the editor: click an empty spot of the screen, then *Characters on this screen*.

### Characters: smooth from the first frame, wandering while reacting, facing the way they walk (15 Oct 2026)
- **Decoded at once:** the editor (`CharacterFrames`) decodes every frame of every clip in the background when a character is first shown at a height, at a few steps of height (48, 96, 160, 256, 384 px, at most its own) drawn scaled from the nearest, keeping two heights; until a frame is ready the same frame at another height or the nearest ready one of its clip is drawn, never nothing. Resizing within a step decodes nothing; the picker's previews share the same frames. The recorder (`CharacterPlayer.Prepare`) decodes every frame, both ways it faces, in the background as soon as a size is first shown; a frame not ready is decoded when shown; past a 256 MB budget the frames used longest ago are let go (it used to drop them all).
- **Reacting while wandering:** a reaction done in place (dancing with notes, sitting in the rain) is done when the character pauses; it keeps wandering or pacing between, its effect shown throughout. Asleep it stays where it is; a reaction that moves (running) changes its pace, as before.
- **Repeating without starting over:** a clip may say how it repeats (`loop`: the first frame repeated, `end`: the last, `bounce`: forwards and back; `CharacterClip.FrameAt` and `clipFrame` alike). A pose (sit, sleep) whose end did not lead back to its start sat down again on every loop; now its opening plays once, then its settled part plays forwards and back. Idle, play and dance clips whose end does not meet their start play forwards and back. `tools/character-gen/loops.py` measures each clip (the seam between its end and start against its usual step) and sets them; `run.py` and the generator service run it after packing.
- **One decoding queue (editor):** every character's frames are decoded through one queue, three at a time, the clip on screen first; eight characters shown at once in the picker had hung the page decoding all their frames together. The decoding itself runs in two background workers (`decoder.worker.ts`, the bitmaps transferred back), off the page's thread; a preview waiting for frames is woken once per animation frame (waking each as its redraw waited again had hung the page when one was pointed at or added).
- **Their own thread on the monitors (`CharacterStage`):** the characters' windows are moved and drawn about 60 times a second on a thread of their own, on a steady clock (`Stopwatch`, the 1 ms timer period), with their own copies of the frames (`CharacterPlayer.Copy`: a frame is never drawn on two threads at once). The overlay's thread tells it where each panel's box is and what the readings are, ten times a second and as the panels change. Drawn on the overlay's thread, they stopped while the panels were drawn and stepped unevenly (a 33 ms timer, a 16 ms clock). USB screens draw them into their pictures: a screen's picture is kept without its characters (when none is behind a panel) and they are drawn over it and sent 30 times a second, on the same steady clock (`DeviceCharactersTick`); the whole picture used to be made again 10 times a second. Panels' moving backgrounds keep their own pace.
- **A frame let go is never measured:** past the budget a frame's size was read after it was disposed, which crashed the overlay with Windows Forms' dialog when a character was added to a USB screen. A problem in a window's message is now logged and the overlay goes on (`Application.SetUnhandledExceptionMode`, per thread).
- **An area of its own:** `OverlayCharacter.From` and `To` (0–1 along its place, at least 5 % apart) keep a pacing or wandering character to part of its line (*Keeps from* / *Keeps to*), recorder and editor alike.
- **Reactions for each reading:** `OverlayCharacter.ReactionsBy` (by source) over its general `Reactions`: *Its reactions for* chooses every reading or one of the panel's (or screen's) readings, whose own table applies when it is the reading reacted to (`ReactionsFor` / `reactionsFor`). *The panel's (screen's) most urgent reading* and one reading stay.
- **A frame rate is poor only with its own limits:** the frame rate's and frame time's kinds no longer have low and critical limits of their own (the desktop draws few frames while nothing moves, and the alarm showed with everything well); a reading's own warning and critical limits still count, and a high frame rate still dances.
- **Dragged on the screen:** *Drag it on the screen* (`Draggable`) makes a character's window take the mouse where it is drawn (its see-through pixels let clicks by, it never takes the focus); dropped, it stays there (`DroppedAt`), or one that walks keeps to 30 % of its line around the spot; a screen's own one goes to the top or the bottom by the half it is dropped in. The layout is saved as when a panel is moved on the screen. Monitors only (USB screens take no mouse).
- **Dragging shows, dropping lands:** a character held on the screen plays its own `held` clip when it has one, else its jump's highest moment, swinging under the mouse; let go, it lands where it was dropped (its `land` clip, else the end of its jump) and plays there from then on: place `spot`, across its screen at the height `Y` it was dropped (staying, or keeping to 30 % around the spot when it walks). Packs may carry `held` and `land` clips (the generator can make them); every shipped character carries both (made with the generator, 15 Oct 2026).
- **Values moved and sized from a menu (editor):** pointing at a value on the selected panel shows a small menu: *Move* (it follows the mouse, sitting live on the left, middle or right third of its tile and its top, middle or bottom third: its own look's `Align` and `Vertical`) and *Size* (the mouse up or down: its own `ValueSize`, 50–300 %); a click sets it, Escape puts it back. A dial's value stays in its dial. A value double-clicked opens its reading in the panel's card (Readings tab, the row open and in sight). Pressing and dragging a value no longer moves it (it showed nothing while it moved).
- **Readings left out:** each reading's item has a tick; one not ticked is not reacted to (`OverlayCharacter.Ignore`).
- **Calmer poses:** asleep a character holds its calmest frame after lying down (its zZ moves); sitting, its calmest six frames play forwards and back (a sleep still moving, repeated forwards and back, looked restless).
- **This screen:** with a panel chosen, *This screen* on the panel card shows the screen's own settings and characters.
- **Readings as items, in an order (15 Oct):** *Reacts to* is nothing, one reading, or the panel's (screen's) readings in the order set: the first critical one in the order is reacted to, else the first high one, else the first reading whatever it shows (low or normal) (`OverlayCharacter.Priority`, `OverlayReactions.Ordered` / `ordered`; on 15 Oct it was the first that is not normal, which let an idle reading put first drown out a CPU at 100 %). Below, *Every reading* and each reading are items, one open at a time, each with its table (the general `Reactions`, or the reading's own `ReactionsBy`), moved first or later with their arrows; the one reacted to now is outlined.
- **Walks and runs loop seamlessly:** `loops.py` finds the stretch of a walk or run whose next frame would most look like its first (a step cycle) and repeats only it (`loop`, `end`), when it joins clearly better than the whole clip: walking no longer jumps back at the end of its clip.
- **Facing:** Hana, Akari and Yuki were drawn facing left but their packs said right, so they walked backwards; their packs now say left.
- **Saving:** a bar at the bottom of the page shows while there are changes not saved (Save, Discard): a character added but not saved was not on the screens.

### The dashboard's address (15 Oct 2026)
- **One port:** the Web host listened on any free loopback port (`Web:Port` 0), a new address at every start. It now listens on 47440 by default, chosen in *Settings › Other devices › Address on this PC* (`PUT /api/v1/lan/address`, this PC only, saved into settings.json as `Web:Port`; the host starts again in its process and the page opens at the new address, the session carrying over). Another program holding it: a free one is used and the Settings page says so (`AddressState.Problem`). The screen stays in sight beside the panel's settings while they scroll (sticky at xl and wider).

### Livelier characters, step A1: a mind, bubbles, shadow and shake (19 Oct 2026)
- **The plan** (agreed with the user): A, a mind, bubbles, pointing at and perching on tiles, recorder events and particles; B, gauge play (hamster-wheel ring and needle ride first); C, prop packs (objects held at hand points found by a pose model); D, the cursor, a click, two characters together, weather.
- **The mind** (`CharacterMind` in Core, `mind.ts` in the editor, the same rules checked by `mind-parity.json`: the editor's tests write a scripted day, the recorder's replay it step by step). It watches what the character reacts to (`ReadingJudged`, its status as held) and the user:
  - a **bubble** when the reading turns bad ("CPU at 100 %!", "GPU temp 88 °C, getting hot", "FPS dropped to 40") or good again ("Phew, CPU back to 40 %", with a dance and notes; "238 fps! Smooth");
  - a **fast rise** while still normal (35 % of the range from the quiet limit to the critical one within 6 s, kinds that go bad upward with fixed limits): a jump with "!" and "CPU rising: 70 %";
  - **away and back**: after 3 minutes without the keyboard or mouse (`GetLastInputInfo` in the recorder; not known in the editor) it sleeps with zZ unless something is bad; back, a jump and "Welcome back!";
  - a **shake** while the reading is critical;
  - an **attention budget**: a bubble at most every 8 s (a reading turning critical or back from critical, and a hello, excepted), a fast-rise warning at most every 15 s.
  What it decides comes over the reaction (`CharacterReacting.FrameOf` / `reactingFrame`); the animations a character does not use are skipped (a relief without a dance jumps). Values are said short (`CharacterMind.Format` / `formatSpoken`: whole percents and degrees, bytes in binary units).
- **Drawn with it** (`CharacterExtras` in the recorder: the monitors' character windows and the USB screens' pictures; `CharacterSprite` in the editor): a soft shadow under the feet, the shake (a fortieth of its height), and the bubble over the head: dark, edged red (critical), amber (high), green (relief) or blue (info), rising and fading in, fading out after 4 s (5 s critical), kept on a USB screen.

### Livelier characters, steps A2 and A3: going to the tile, and events (19 Oct 2026)
- **Going to the tile** (`CharacterReacting.GoTo` / `Reacting.goTo`): while what it reacts to is bad (high, or low for a frame rate, or critical) a character on a line (on top of or inside its panel, its screen's bottom, a spot) goes to that reading's tile's middle along its line, running when it is critical (3 heights a second, else walking at 1.6), and does its reaction there; good again, it does its mind's moment (the relief dance) where it is and walks back into its route. Beside its panel or hopping around it, it keeps to its route. The tiles' places: the recorder takes them from each panel's hit areas (`PanelHits.Readings`; on a monitor from its window, on a USB screen from where the panel is placed in the picture, the first panel showing a reading winning); the editor finds the tile (`data-tile`) or the value (`data-value`) marked `data-source` on its screen.
- **Events** (`CharacterEvent`, said by `CharacterEvents.Raise` in the recorder; the editor has none to say): an incident saved (`IncidentManager.Captured`: a jump with "!" and "Incident saved: <title>", so an app crash caught by a rule is seen too), recording turned on ("Recording started") or off ("Recording stopped"), a game come to the front (an app in front presenting 20 frames a second or more while a game reading is shown: a dance with notes and "<game> started: have fun!"). Each mind reacts to each event once, the budget aside; those from before it first thinks, and while the user is away, are let go. Long titles are cut to 28 characters. The scenario in `mind-parity.json` has them too.

### Livelier characters, step B1: playing on the gauges (19 Oct 2026)
- **Two places** (`OverlayCharacter.Place` wheel and needle, *In its ring* and *On its needle* in the editor; a screen's own characters too) and its gauge (`OverlayCharacter.Gauge`: a reading's source, or the first ring, arc or speedometer of its panel or screen; without one it plays on the top edge and the editor says why).
  - **Wheel** (a hamster in its wheel): drawn to fit inside the ring (1.15 × its inner radius), at its bottom, running in place, faster as the gauge fills (asleep below 4 %, walking below 30 %, running above), its clip and the wheel's eight faint spokes and hub turning with it (`CharacterExtras.DrawWheel` / `drawWheel`). What its mind or reaction makes it do in place (a relief dance, sleeping while the user is away) it does in the wheel.
  - **Needle**: on the end of the gauge's fill (a ring's, an arc's, or a speedometer's needle), sitting, facing out, jumping when critical; it follows the fill as the value glides.
- **Where the gauges are**: the recorder's renderer records each gauge as it draws it (`GaugeHit`: its box, kind, fill and stroke; start and sweep as drawn: a ring from the top over a full turn, an arc from 145° over 250°, a speedometer from 160° over 220°) in the panel's hit areas (`PanelHits.Gauges`), gathered with the tiles by screen (`ReadingSpots`). The editor marks each ring and arc it draws (`data-gauge`, `data-source`, `data-fill`, `data-size`, `data-thickness`, `data-start`, `data-sweep`) and reads them back (`characters/gauges.ts`). The same cases are tested on both sides.

### Livelier characters, step A4: particles and breathing (19 Oct 2026)
- **Drawn in code** (`CharacterParticles` in Core, `particles.ts` in the editor, the same numbers checked by `particle-parity.json`): each a function of time alone.
  - **Confetti** (24 bits in six colors, bursting up from its head, falling, turning and fading over 1.6 s) on a happy moment: its mind's relief, a game come to the front, the user back (`CharacterThought.Burst`, in `mind-parity.json` too).
  - **Dust** puffing behind its feet while it runs (a puff every 0.17 s, growing and fading over 0.5 s).
  - **Breathing**: 1.8 % taller and shorter every 2.6 s while it stands, sits or sleeps, its feet where they are.
- Foam and water spray go with the objects that make them (the extinguisher and the hose, step C).

### Livelier characters, step E: new animations from a prompt, and emotions (19 Oct 2026)
- **Asked for**: animation generation in the character generator (a prompt, made for every current character, listed with what characters do and how they react), and expressions that fit the moment (no smiling jumps when readings are bad).
- **Made from the character's own pose** (`tools/character-gen/animate.py`, `run.py animate`): its first idle frame (already cut out) is staged on white as `clips.py` stages a still, Wan 2.2 animates it with the prompt (`CHARGEN_PROMPTS`), RIFE loops it, rembg cuts it out (finish.py's steps), and it is packed alone (an extra `.char`: no idle clip, its frames sized for this clip) at the character's own scale: as tall in its first frame as the idle frame stands in the character's file, the feet on the same spot, the head where the character's is (checked on Ren: 238 px standing in both, `tall` 240, the head's offset the same). The generator service takes the character's file (`POST /v1/files`, up to 32 MB) and the animation (`POST /v1/animations` {character, name, prompt}); its health says `animations: true`.
- **Kept and followed by the Web host** (`CharacterAnimationService`): `animations.json` (`CharacterAnimations`: each animation's id, name, prompt and each character's clip: queued, running, done or failed, its file in the `animations` folder, named by its content). The clips waiting are sent together as one job (at most 64: `POST /v1/animations` {items: […]}, answered with each clip's file or why it failed in the same order), so the generator loads its video model once for them all (`animate.py --batch`, `run.py animate-batch`: the 36 emotion clips of the 9 shipped characters in one load instead of 36), followed every 3 s; they wait while there is no generator. `GET/POST /api/v1/overlay/animations`, `POST …/emotions`, `POST …/{id}/retry` (failed clips, and characters it does not have yet), `DELETE …/{id}` (its files too), `GET …/{id}/{character}` (a clip's file). Changes are this PC's only, as everything under /overlay.
- **Played as actions**: an animation's id (2-24 lowercase letters, digits or dashes, not a built-in action) is an action everywhere (`OverlayCharacters.IsAction`: what it does, the animations it uses, its reactions). The recorder attaches each character's clips (`CharacterPlayer.Extras`, read with `CharacterPack.ReadExtra`; the characters read again when animations.json changes, `CharacterPlayer.Generation`); the editor too (`CharacterFrames.extras`, `refreshCharacterAnimations` when a clip is made). A character without an animation's clip does the closest it has.
- **Emotions** (`CharacterAnimations.Emotions`, *Make the emotions*): worried, panic, tired and cheer, new animations with prompts of their own ("no smile" where it matters). The reactions use them: load high worried, critical panic; temperature high tired, critical panic; a frame rate dropping worried, critical panic; latency, frame time and other readings worried and panic; power high tired; the mind's relief, a game started and the user back cheer; a fast rise worried; an incident panic. Until a character has them: worried and tired sit, panic jumps, cheer dances. In a wheel, worried or tired keeps running (the running is the reading).
- **The page** (the generator's page, *Animations*): the emotions with one button; a name and what it does, made for every character; each animation with its clips as they come along (made, under way with its progress, waiting, failed with why), made again where it failed, or removed.
- **Managed, per character** (19 Oct 2026): an animation has a kind (`Kind`: animation or emotion; older ones are emotions by a built-in emotion's id, `KindOf`) and is on or off (`Active`), and so is each character's clip (`AnimationClip.Active`). What a character plays is `CharacterAnimations.FilesFor` (the animation and the clip on, a file made), in the recorder (`OverlayService.ExtrasOf`) and the editor (`attachAnimations`) alike; off, it does the closest it has, its clip kept. Made again (`Regenerate`: one character's clip, or every character's and those it lacks), a clip keeps playing its last file until the new one is made, which then replaces it (the old file deleted). `PUT …/{id}` (its name, its prompt), `POST …/{id}/regenerate`, `POST …/{id}/{character}/regenerate`, `PUT …/{id}/active`, `PUT …/{id}/{character}/active`, `DELETE …/{id}/{character}`; `POST /api/v1/overlay/animations` takes `kind`.
- **The generator's page in tabs**: *Characters* (a new character and those being made), *Animations* and *Emotions* (the same section for each kind: the built-in emotions with one click on the second, a name and a prompt made for every character, each one turned on or off, renamed or described again (saved, or saved and made again), made again for every character, made where it failed or is missing, or removed). Each character is a tile with its preview playing the clip once made and on, how it is coming along (made, making with its percent, waiting, failed with why on hover, not made), and its own make or make again, on or off and remove.

### Livelier characters, step B2: pushing a bar and surfing a graph (19 Oct 2026)
- **Two more places on a gauge**: bar (*Pushing its bar*: on the bar, at the end of its fill, walking while it grows, as it reacts while it holds or shrinks, panicking when critical) and graph (*Surfing its graph*: along a line graph left to right, 6 s across at its speed and round again, up and down with the line, running). It keeps its own height on them. The renderer records a bar's track and a graph's line as drawn (`GaugeHit` with `Line`, `LineAt`); the editor marks its bars (`data-gauge="bar"`, `data-fill`) and line graphs (`data-gauge="graph"`, `data-line` in the drawing's units, `data-units`). The gauge chosen fits the place (`CharacterReacting.Fits` / `fits`: a ring, arc or speedometer for a wheel or a needle, a bar, a line graph), the most urgent first as before.
- Left out of B2: catching digits and pulling a rope need clips of their own (they can be made as new animations, step E).

### Livelier characters, step C1: props (19 Oct 2026)
- **Twelve props, made like the characters** (`tools/character-gen/props.py`: Animagine XL 4.0, an object alone on white, four seeds each, cut out with rembg's isnet-general-use; the best of each chosen from a contact sheet and baked by `bake_props.py` and `pack-props.mts` into `src/PcDiag.Core/Overlay/Characters/props.char`, a clip per prop in the characters' format, read as the effects' file is: `CharacterPack.ReadProps`, `readPack(…, { effects: true })`, served at `GET /api/v1/overlay/props`): ice pack, siren light, umbrella, desk fan, coffee, trophy, traffic cone, toolbox, boxes, thermometer, battery, popcorn.
- **In the reactions table** (`reactions.json` `props`: each one's place, its size and width against the character, where it is held, how it moves, what it gives off, drawn behind or turned with it; each kind's `props` by status): load high a cone, critical a siren; temperature low a coffee, high an ice pack on its head, critical a desk fan blowing at it; frame rate low and frame time high an umbrella, frame rate high popcorn; fans a desk fan; power high a battery; other critical ones a siren. A character's own reactions choose another or none (`OverlayReaction.Prop`, `"none"`; the *Prop* column of its reactions, by status and by reading), checked (`There is no prop …`). Its mind brings one with its moments: coffee when the user is back, a trophy on relief, a toolbox for an incident, popcorn when a game starts (`CharacterThought.Prop`).
- **Drawn in code alike** (`CharacterProps.cs` and `props.ts`, checked number for number by `prop-parity.json`): on its head, over it, beside it on the ground the way it faces (clear of its body: how far it reaches as it stands, plus half the prop's width), or just before it; arriving (popping up on it, dropping in and bouncing on the ground), going (fading as the next arrives: `PropTrack`), bobbing, swaying or wobbling, and giving off drips, steam, wind blowing back at it, sparkles, or a siren's glow flashing red and blue (behind it). On the ground they are left while it walks or runs or plays on a gauge.
- **What is on its head goes with it**: each frame decoded is measured (`CharacterPlayer.Measure`, the editor's `tops.ts` in its decoding worker): the top of its head is the first row with a solid stretch a quarter of its height across (ears, an antenna and hair spikes passed over) and the head's middle that stretch's, so a prop stays on it as it sits, jumps or bends; and its edges, for how far it reaches. Checked on a contact sheet of the nine shipped characters with every prop (an ice pack on each head standing and sitting, ground props clear of the cat's and the corgi's long bodies).
- **Step C2, equipment held** (21 Oct 2026): a fire extinguisher and a hose, made like the other props (the hose turned to face the file's way: `bake_props.py` `FLIPPED`), held before it at its front edge a third of the way up (`at: "hand"`), spraying from their nozzle (`nozzle`) the way it faces: thick white foam bursting and falling (`foam`), a stream of drops arcing out (`water`). Temperature critical brings the extinguisher (it panics, a flame on it, spraying), power critical the hose. Made as new animations instead (the object in the prompt, tried on Ren and Hana) they were not good enough: the video starts from the character's own pose without the object, so it appears part way (or not at all, Ren's), would vanish each loop, and no foam or water showed.
- **The closest clip counts the new animations** (`CharacterPack.CloseTo`, `CharacterPlayer.Plays`, the editor's `closeTo` and `plays`): a character without an action does the closest it has, its generated clips included (a cheer becomes its generated dance before its own play).

### Livelier characters, step D: the mouse, clicks and petting, two together, weather and the time of day (21 Oct 2026)
- **Senses** (`CharacterSenses` in Core, `company.ts` in the editor): a mind thinks with where its feet were last drawn, the mouse, its clicks, the nearest other character on its line, the weather and the local time, besides its reading. What it decides comes back on the thought: where along its line it goes or stays (`Toward`, walked to as a tile is, `GoTo`), the way it faces when not walking (`Facing`), and the weather's effect and prop, under its reaction's (`Weather`, `WeatherProp`). The rules are `CharacterMind` and `mind.ts` alike, checked by `mind-parity.json` (its scenario now has a part with every sense: 499 steps).
- **The mouse** (`OverlayCharacter.Mouse`, *Notices the mouse*, on by default): within 4 of its heights to either side and 2.5 up or down it hops once (at most every 20 s), then faces the mouse and stays; the mouse resting there 1.2 s brings it over (walking, to 0.7 of its height from it); 2 s after the mouse leaves it walks back to its route. Its reading comes first: while that is bad it does not watch. On the monitors the mouse is read once a frame (`GetCursorPos`) only while it is free: shown, not held by a game, nothing full screen in front (the panel handles' check, `OverlayHost.HandlesAllowed`, every 0.25 s). USB screens have no mouse; the editor uses the mouse over its screen.
- **Petting**: the mouse rubbed back and forth over it (three turns of at least 6 % of its height within 1.5 s; no click, so it works through its click-through window) makes it dance with hearts and say "Hehe, that tickles!", "Aww, thank you!" or "More, please!" in turn; it stays where it is while petted, whatever its reading says.
- **Clicks** (`OverlayCharacter.Clickable`, *Answers a click*, off by default: its window then takes the mouse where it is drawn, as for dragging): "Hi there!", "Hehe!", "That tickles!" with a jump; a fourth within 4 s: dizzy stars, sitting, "Whoa, I'm dizzy…". A character that can be dragged answers a click too: it is taken only once the mouse moves 4 pixels while pressed (it used to be taken at the press). In the editor a click on a character that answers clicks pokes it as well as opening it.
- **Two together** (`CharacterCompany`: each screen's characters as last drawn, `CharacterSpot`; the nearest on its line, feet within 0.35 of the taller one's height): two free characters (nothing bad, no moment, not meeting, not watching the mouse) within 1.3 of their heights meet for 5 s: both stop and face each other, the first by id says "Hi <name>!" and the other answers "Hey <name>!" 1.2 s later, with hearts, then both dance with notes. The same two meet again after 45 s at the least. Within 5 heights, the first of the two walks over to the other at most once a minute (giving up after 10 s or when the other gets busy). On a gauge, beside or hopping around a panel, or held, a character does not meet. Monitors, USB screens and the editor (one `Company` per screen) alike.
- **The time of day**: "Good morning!" once a day between 5 and 11 with a coffee (in place of "Welcome back!" when the user comes back then); between midnight and 5, a yawn with zZ every 20 minutes ("It's late… bedtime?").
- **Weather** (off by default): the layout names a place (`OverlayConfig.Weather`: a name and its latitude and longitude, chosen in the On-screen page's *Weather* dialog from a search by name). The Web host (`WeatherService`) fetches MET Norway's compact location forecast for it (api.met.no, CC BY 4.0, commercial use allowed: Open-Meteo's free plan is for non-commercial use only) at most every 30 minutes and never before its Expires, 5 minutes after a failure, and writes `weather.json` (`WeatherReport`: its time, place, the next hour's symbol, temperature, wind); without a place nothing is fetched and the file is removed. Places are searched with OpenStreetMap's Nominatim (`GET /api/v1/overlay/weather/places?name=`, at most one a second, in the browser's language); `GET /api/v1/overlay/weather` gives the report to the editor. The overlay reads the file when it changes (`OverlayService.WatchWeather`, as animations.json), not older than 3 hours. MET's symbols make six kinds (`WeatherReport.KindOf`): storm, snow, rain (with sleet), fog, clear, clouds. A character says it when it changes ("Raining outside: 12 °C", "Brr, -3 °C outside", "Hot outside: 31 °C", "Thunderstorm outside!", "Sunny outside", "Clear night") and shows it for 40 s of every 5 minutes while it is free: rain with its umbrella, snowflakes, sweat and a desk fan when hot (30 °C or more), a coffee when cold (0 °C or less), wind at 10 m/s or more.
- **Tested**: the parity scenario (mouse near, resting, rubbed; four clicks; a friend met, one that meets it, one walked over to; the morning and late night; four kinds of weather) on both sides; the recorder walking to a resting mouse and back to its spot, facing it, then showing the rain (`CharacterPlayerTests`); the forecast read and the kinds (`CharacterMindTests`); fetching when due, a new place at once, removing without one, and the place search (`WeatherServiceTests`). `CharacterReacting.LocalTime` lets tests fix the hour (a quiet PC's sleeping test had said good morning first at 9 a.m.).

### The internal character generator started by PC Diagnostic (15 Oct 2026)
Reported 15 Oct 2026: choosing *On this PC (internal)* and pressing Test said *Character generation is off*: Test checked the setting saved (still off), and the internal generator had to be started by hand.
- **Test checks the choice on the page** (`POST /overlay/generator/test` with it), saved or not.
- **Started when needed:** the internal generator (serve.py) is started by the Web host, hidden, with its Python and folder, when a test or a generation finds it not answering; it then waits for it to answer (up to 40 s). Both are found on this PC (`tools\character-gen` above where PC Diagnostic runs; the kit's `.venv\Scripts\python.exe` named in its settings.py or `KITGEN`) and can be set in the setup (`GeneratorSettings.Python`, `Folder`). **Installed with PC Diagnostic** (19 Oct 2026): the installer ships the generator (`installer/build.ps1` stages the repo's `tools/character-gen` scripts in `{app}\tools\character-gen`, with the editor's `pack.ts` and `png8.ts` in its `lib`, which `pack.mts` uses when they are there), so an installed copy finds it as the dev build does; it cannot write beside itself there, so its outputs go to `%data%\characters\generator-out` (`CHARGEN_OUT`, set by the Web host when the folder is not a repo's, and by settings.py's own default outside a repo). Before, an installed copy reported *The generator's folder (tools\character-gen, with serve.py) was not found*.

- **Its own section (15 Oct):** *Character generator* on the On-screen page holds the setup (this PC or another service) and, for the one on this PC, its state (running, busy, not running; asked every 4 s: `GET /overlay/generator/status`) with **Start** and **Stop** (`POST /overlay/generator/start`, `/stop`; stopping asks the generator to stop, `POST /v1/stop` in serve.py, and ends the process started here if it does not). *Generate a character* uses it: without one set up it offers to go there; with one, it shows the generator's state and Start.
- **Requests the generator could read:** the Web host sent a generation's JSON in chunks (no Content-Length), which serve.py read as empty ("a prompt … is needed"); it is now sent with its length, and serve.py reads chunked bodies too.

- **What it needs, step by step:** the section checks the generator's prerequisites on this PC one by one (`tools/character-gen/setup.py check`, run with its Python: Python, the packages, an NVIDIA GPU with CUDA and its memory, the models in the kit, RIFE 4.26, the cut-out model, ffmpeg, Node.js 22.18 or later for packing) and offers, for each, to install or download what is missing (`setup.py packages | models | cutout | ffmpeg | node`, or a Python environment of its own made with uv or the Python launcher, which then becomes its Python) or to use what this PC already has (a Python, a kit folder: `GeneratorSettings.Kit`, given to the generator as `KITGEN`). One step runs at a time, in the background, its output followed in the section (`/overlay/generator/prerequisites`, `/prerequisites/{action}`, `/prerequisites/run`).
- **The generator's process followed:** the one started here has its output written to `%data%\logs\character-generator.log` and its id and start time kept (generator.json), so a generator already running is recognised (also after PC Diagnostic starts again), not started twice, stopped by Stop, and shown *Starting…* while its process runs but does not answer yet; one that ends as it starts is reported with its last words, at once rather than after waiting. (A stray carriage return in serve.py kept it from starting at all; a test now keeps the scripts free of them.)
- **Characters being made, grouped:** the list groups each character with its stages: its drafts and each animation of a draft (`groupGenerations`: a character generation joins the drafts it came `from`). A group shows the character's name (or *New character*), a thumbnail, its stage (*Drawing drafts*, *Choose a draft*, *Animating*, *In the library*, failed) and its progress; one group opens at a time, and in it one stage, to choose a draft or follow the generator's output. Animating a draft closes the drafts and opens the new animation.
- **Astro** (generated 15 Oct from "Astronaut who can walk and run like no/low gravity") ships with the other nine.
- **Several generations, each opened:** drafts and characters can be asked for while others run (the generator makes one at a time, waiting for the GPU); each in the list opens to show its step, progress, prompt and the generator's latest output (`GenerationJob.Log`). The generator's output is read as UTF-8 (a generation failed on a character the Windows code page could not read).
- **Screen characters' readings:** each reading's item shows its state now (quiet, normal, high, critical): a character reacting to a quiet reading whose reaction does nothing visible looked as if it did not react.

### Values still loading
Reported 15 Oct 2026 with a screenshot: right after the recorder starts, a panel read its values' ids ("SENSOR.LHM/GPU-NVIDIA/0/TEMPERATURE/2", "GPU.GPU-10DE-….VRAM") until the hardware sensors were read.
- `OverlayKnownSources` (Engine) keeps every value's name, group, unit and total as last seen in `%data%\overlay-sources.json` (written at most every 30 s when one changes). The recorder's values are completed with a stand-in for each shown value that is missing: its name as last seen (or one made from its id, `OverlayStyles.LabelOf`: "GPU temperature", "VRAM"), no value, and `OverlaySource.Loading` for the first 90 s.
- A loading value is drawn with a moving mark (one to three dots, `OverlayStyles.LoadingMark`) in its value's space; the editor shows the same dots, pulsing. After 90 s a value still missing reads "—" under its name.

### Gaming readings (Phase 27)
Requested 9 Oct 2026: "add more readings covering the gaming stats".

- **Frames** (`EtwFrameMonitor`, elevated): an ETW real-time session, *PcDiagnostic-Frames*, on the present events of DXGI (events 42 and 55) and Direct3D 9 (event 1). Its buffers are handed over every 100 ms (`EVENT_TRACE_USE_MS_FLUSH_TIMER`) with raw QPC timestamps (`PROCESS_TRACE_MODE_RAW_TIMESTAMP`; without it ETW hands real-time consumers system time, which cannot be compared with `Stopwatch`). It runs only while a shown panel or the editor uses it and stops a minute after the last use.
- **Measures** (`FrameTimes`): FPS is the presents in the last second, a second that ends 0.2 s ago so all its frames are in; frame time is the average interval; the **1 % low** is 1000 divided by the average of the slowest 1 % of frame times over 30 s (from 100 frames). The app measured is the one in front (`GetForegroundWindow`) if it presented in the last 2 s, else the one presenting most.
- **Readings:** FPS, Frame time, 1 % low, Game (its name), Game CPU and Game RAM (from the process sample); Ping router (the default gateway) and Ping internet (1.1.1.1), once a second, 900 ms timeout, below 1 ms measured precisely (Windows reports whole milliseconds), the internet ping only while a shown panel uses it; Busiest core and Fastest core.
- **Templates:** Gaming HUD and Gaming use them.
- **Verified on the dev PC** (9 Oct 2026): the app in front read 22 fps at 45.8 ms a frame (they agree); the router 0.8–0.9 ms, the internet 7–12 ms. The first build read 511 fps with a 17 ms frame time: its frames were in system time and "now" in QPC, so every frame kept counted as the last second.

### Expected hardware (Phase 28)
Requested 9 Oct 2026: "an option to discover missing devices/resources … disk disconnected, monitor disconnected, device disconnected".

- **Remember this setup** (System › Devices): the recorder lists this PC's hardware (`HardwareInventory`): devices (physical, present, not system parts), monitors (from the display snapshot), drives, network adapters (present, and hardware by Windows' own interface flags, the ones the network collector uses; not virtual, VPN, the kernel debugger's or the filters Windows binds to an adapter), graphics cards, installed memory and processor threads. The user picks what to expect and whether a missing item makes an incident. The Web host writes the choice to `expected-hardware.json` after validation (kinds, 500 items, unique keys).
- **Checked** (`HardwareWatch`, a hosted service in the Agent) 15 s after start, every 20 s and on demand (`hardware-check`): an expected item that is not there is missing, keeping since when; memory and processors count as missing when there is less of them (3 % tolerance, so 31.9 of 32 GB is fine); new items are listed as added.
- **Told:** the Devices tab lists what is missing and since when; every page shows *Expected hardware missing: …* (`AgentStatus.Hardware`); optionally a high-severity incident (rule `expected-hardware`) when something goes missing; the on-screen readings *Missing hardware* and *Missing*.
- **Verified on the dev PC** (9 Oct 2026): 24 items (14 devices, 3 drives, the Ethernet adapter in use, 2 graphics cards, 64 GB, 24 threads, 2 monitors); a remembered setup with a monitor and a drive that were not connected showed both missing within a second, on the page and in the notice; forgetting it cleared both. The first list had 13 adapters: Windows' filter bindings, the kernel debugger's, and, seen only by the elevated recorder, eight left behind by a disabled Wi-Fi card and earlier installs; they are left out now.

### Round 3: the dashboard's widths, gauges' numbers, panels' widths, the grid toolbar (Phase 30)
Reported 9 Oct 2026, with a video and three snapshots.

- **Dashboard load, again:** the widgets still slid into place from left to right. The grid laid itself out for a guessed width (react-grid-layout's 1280 px) and slid every widget to the measured width; the further right, the further it moved. Now the width is measured before the first layout (`measureBeforeMount`), and widgets glide only while the dashboard is customized; opening a page, resizing the window or a scrollbar appearing places them at once.
- **Gauges' numbers changed size**, at 100 % among others: the dashboard's gauges chose their number's size by the text's length, and a dial in a widget did too. Now each gauge kind has one size, and a widget's dial sizes its number by the widest it can show on its scale.
- **On-screen panels much wider on the screen than in the editor:** when the recorder starts, the first refreshes come before every value source is there, and a reading without one is labelled with its source's id (*GPU.GPU-10DE-2684-467519DA.USAGE*); the never-shrink rule then kept the panel that wide. The size now starts afresh when a reading's source appears or goes, in the recorder and the editor. And a value keeps room for its full scale (100 for a percentage, the total for VRAM), so 99 % to 100 % changes nothing; the number sits at the end of that room, its unit beside it.
- **The editor's screen** was drawn with panels at 2× by default against a true-size monitor; zooming now enlarges the screen and its panels together (Fit, 2×, 3×, scrolled within the card), so panels always keep their true size on it.
- **The grid toolbar** (six buttons over a small tile) was cut off: choosing a tile now shows its name, order, width and height in a toolbar under the grid.
- **Ping** runs all the time (once a second to the router and to 1.1.1.1), so the round trips are there wherever they are shown, at once.
- **Verified on the dev PC** (9 Oct 2026): straight after the recorder restarted, a tiles panel with every design came up 695 pixels wide on the 4K screen at 150 % (it had been about 1690); filmed, the dashboard laid itself out once (one layout over 341 frames); the round trips read 0.8 ms to the router and 8 ms to the internet as soon as the recorder was up.

### Arranging on the screens (Phase 31)
Asked for 9 Oct 2026: panels movable, resizable and closable on the screen itself, and readings sortable, removable and their design changeable there, all reflected in the system.

- **Starting and ending:** a hotkey (*Ctrl+Alt+Shift+A*, a layout setting), the tray menu or the editor's *Arrange on the screens*; Esc, the *Done* button of the bar at the top of the main screen, or the same again, end it. While arranging, every panel is shown, hidden ones faded.
- **On the screens:** the panels take the mouse (they stop being click-through, still never take the focus) and show a dashed outline, an options button, a hide button and a resize corner. Drag a panel to move it (onto another screen moves it there), its corner to resize it (it keeps that size), a reading onto another to reorder; the reading under the mouse is highlighted.
- **Right-click a reading:** *Shown with* (the 12 designs), *Reads as* (value, percentage, both, of its total), move earlier or later, wider, narrower, taller, shorter (tiles), remove. **The panel's options:** layout, look (the themes), background (text only, see-through, glass, solid), size (automatic or keep this size), hide or show, delete (confirmed), done.
- **Saved and shown:** each change is checked like the editor's (`OverlayStyles.Validate`), saved to overlay.json by the recorder, and the editor reloads the layout when its revision changes (every answer carries it) and nothing is unsaved there; it says the layout was changed on the screens.
- `OverlayRenderer.RenderPanel` draws a panel as arranged and says where its parts are (`PanelHits`); `OverlayHost` handles the mouse, the drags and the menus (Windows Forms context menus).
- **Verified** (9 Oct 2026) with the published recorder hosted in a test harness, not elevated (Windows lets no other program's input reach the elevated recorder's windows), driven by the real mouse and keyboard: a panel moved, resized (to 364 × 293), a reading reordered and one removed from its menu, the panel hidden and shown, *Done*; each change was in overlay.json at once.

### Pictures, patterns and designed panels (Phase 32)
Asked for 9 Oct 2026: an image background with styling options, and beautiful, elegant ready-made panels.

- **A picture behind the readings** (Look › Picture and pattern): chosen in the editor, shrunk there to 1920 pixels on its longest side and stored as PNG (when it may be transparent) or JPEG under the data folder's overlay-images, named by its content's hash (`POST/GET /api/v1/overlay/images`); pictures no panel uses are removed an hour after upload when the layout is saved. It fills the panel (cover), fits (contain), stretches, tiles or is centred, at a position, with its opacity, a blur, grey, and a tint of any color and strength, clipped to the panel's corners.
- **A pattern over the background:** dots, grid, diagonal lines, carbon, hexagons or scanlines, in an automatic or chosen color, opacity and size; the recorder repeats the same tile the editor draws as SVG.
- **Designed panels** (*Designs*): Obsidian Glass, Carbon Racing, Synthwave, Frost Minimal, Terminal, Streamer Strip, Thermals, Network Pulse, Esports Compact, Sunset Glass, Elegant Clock and Aurora HUD, previewed live with this PC's values over a backdrop like a desktop; each adds a whole panel (layout, readings, look, pattern) that can then be changed like any other.
- **Verified on the dev PC** (9 Oct 2026): the gallery and a picture's upload in Edge; on the screen, the deployed recorder drew a panel over a picture (cover, blurred, tinted) and one with a hexagon pattern, as the editor shows them.

### Frame rates for every graphics API (Phase 33)
Asked for 9 Oct 2026: the frame counter had been checked only with an editor in front, and Vulkan and OpenGL games were not counted.

- **Measured, not assumed:** a test app (scratch `framegen`) presents at an exact rate with Direct3D 11, Direct3D 9, OpenGL or Vulkan; an elevated ETW probe counted every DXGI, Direct3D 9, DxgKrnl and Win32k event per process while it ran, 4 s at 100 fps for each. On the dev PC (RTX 4090, its default Vulkan/OpenGL present method): OpenGL and Vulkan send **no** DXGI events; DxgKrnl's Blit_Info (166), Present_Info (184) and PresentHistoryDetailed_Start (215) each fire exactly once per frame for all four APIs (399–400 of 400 frames); Flip_Info (168), PresentHistory_Start (171) and FlipMultiPlaneOverlay_Info (252) do not fire for them. No event was lost.
- **Counted:** the frame session also enables Microsoft-Windows-DxgKrnl with an event id filter, so only its present events (166, 168, 171, 184, 215, 252) reach the session out of its many thousands a second. An app that reports presents through its runtime (DXGI, Direct3D 9) is counted from those; any other app from the kernel event it logs most (one per frame: its presents take one path, so the events are never added up). The desktop compositor (dwm), csrss and the system are left out, so the compositor's own flips never read as a game at the refresh rate.
- **Verified** by the probe's counts (the events chosen are exactly one per frame for each API) and by tests of the counting (a kernel-only app at 100 fps reads 100, an app with runtime presents is not counted twice).
- **Verified live** with the deployed recorder (9 Oct 2026): the test app at 100 fps, in front, read 99–100 fps at 10.0 ms a frame with Direct3D 11, Direct3D 9, OpenGL and Vulkan. Its 1 % low agreed with the app's own measurement of its frames within 2 fps (80.1 against 80.2 for Direct3D 11, 82.1 against 82.3 for Direct3D 9, 78.4 against 80.4 for OpenGL); a first run that read 8–15 for OpenGL had a real stall at the start, which the 30 s window kept. While Edge was in front (240 fps) instead, Edge was the app read, as intended.

### Panels keep their size whatever the values (Phase 34)
Reported 9 Oct 2026 with a snapshot: a line of the frame rate, 1 % low, GPU, CPU, its temperature, ping and the time still changed width as the values changed; then (the same day) that the readings kept moving: each value should have a fixed, rational space it sits in (in the middle, or as chosen), and a value changing should never move another.

- **Why:** only a reading with a full scale (percentages, totals) kept room for its widest. A frame rate (9 to 144), a ping (8.0 to 12.3 to 112 ms), a size or rate whose unit changes (KB/s to MB/s), the lowest/average/highest text and "no reading yet" each took their current width; and the rule that a panel never shrinks started afresh every time the layout was saved.
- **Every number keeps room for the widest it can read** over its range: its full scale, or what its unit usually reaches (999 fps, ms and W; 100 °C, as processors do read; 9999 MHz and RPM; 9.99 GHz; sizes up to their total or 999 GB; rates up to 99 GB/s). The texts are found by formatting the values just under each place the number gains a digit or changes unit, where it rounds up (`OverlayStyles.Widest`, the editor's `widest`). The unit keeps the widest unit's room (KB/s, MB/s, GB/s), also while there is no reading, and so does the lowest/average/highest or trend text.
- **Never four digits before the point:** a size or rate moves to the next unit once it would read 1000 (1000 MB reads 1.0 GB, 999.7 KB/s 1.0 MB/s), and a time keeps its tenth of a millisecond only while it rounds under 100 (99.96 reads 100, not 100.0). The dashboard's widgets share the editor's formatting, so they read the same.
- **Saving keeps the size:** a panel keeps its size when the layout is saved again, unless the panel itself changed (moving or hiding it is not a change).
- **A fixed space for every value:** the value, its unit and the text after it sit in their space as one, on the left, in the middle (the default) or on the right (*Values sit*, in the editor's Text settings and the panel's menu on the screens; `OverlayStyle.ValueAlign`). A row's space is the value column; a line's, the reading's own room; a tile's, the tile's width (in a dial, always the middle). Every reading's place depends only on the spaces, so a value changing moves nothing else.
- **Texts too:** a text value declares its space (`OverlaySource.Room`): the time as 00:00, the date as the widest day and month ("Wed 00 May"), the recorder's two states, and a width for free texts (an app's name 14 digits wide, missing hardware 24), which are shortened with "…" to fit. An app's process name loses its engine's build suffix (FortniteClient-Win64-Shipping reads FortniteClient).
- **Verified** by a test that draws a line, a rows and a tiles panel of these readings with nine sets of values (no reading, 9 to 999 fps, 0.8 to 999 ms, 9 to 100 °C, 0 KB/s to 45.6 GB/s, 300 MB to 999 GB, app names from "—" to 33 letters, four dates, with and without history): one size and one place for every reading in each layout; by a test of where a value's pixels fall on the left, in the middle and on the right; and in the editor, a ping of 8.0, of 112 and none keep the same room, a long name is cut to its space.
- **A panel grew a pixel a redraw** (reported the same day: the bottom panel's height kept increasing, then stopped, its readings no longer in the middle): the size a shown panel keeps came back as its last bitmap less the margins, and adding the shadow's fractional offset again in float could land a hair above a whole pixel, which rounding up made a pixel more at every redraw (104 to 113 px, 1 px a second). Whole pixels now ignore float noise (a test redraws every layout at 100–200 % with five shadows 30 times at the size it keeps); a line taller than its readings has them in the middle, every reading of a line sharing one height, in the recorder and the editor.
- **Saving while the other program reads:** the recorder and the Web host both read and save overlay.json (the editor's revision check reads it every second). A save could be refused ("Access to the path is denied": a file open for reading cannot be replaced), and a read meanwhile gave the empty layout, so the panels vanished for a moment. Reads now never block a replace and are tried again briefly, a save uses a temporary file of its own and waits a moment for a reader, the recorder keeps its panels when the file cannot be read just then, and the editor is answered "try again" (503) rather than an empty layout it could save over the real one.
- **Verified live** (9 Oct 2026, after deploying): over 20 s of changing values the line panel stayed 1096 × 113 and the Gaming HUD 639 × 416 in all ten samples; three screenshots of the line panel with FPS at 54, 19 and 15, GPU at 35, 30 and 84 % had every label, icon and unit in the same place and each value in the middle of its space.

### Animated characters, PC Diagnostic Storage, more scenes, and panels over each other (Phase 49)
Requested 10 Oct 2026, in four parts:
- two panels over each other kept racing to be in front (they flickered);
- more moving scenes, and a more compact list of them;
- animated characters (kitties, puppies, anime characters, lots of them) playing around a panel or in place, their place, size and movement set, several per panel;
- a default S3 storage installed with the system, SeaweedFS as in Video Clipper, with a button to set it up, any other service or a folder as the default instead, and a choice of what is kept where.

- **Panels over each other**: the recorder raised its panel windows to the top of the always-on-top band one after another. This happened every 2 s, and again after taskbar, focus and window-order changes (Phase 41a). So at every raise the panel underneath was briefly in front of the other.
  - The windows are now stacked in one deferred batch (`LayeredWindow.Stack`), each just under the one before it, so their order never flips.
  - The order is the layout's: a later panel in front, as the editor draws it, with the handles, the arranging bar and the screens' numbers above.
- **More scenes:** twelve more, each a seamless loop (20 in all):
  - Ocean Swell (swells under a low sun, its path glittering), Lava Lamp (wax rising and merging), Ink in Water (noise warped by noise);
  - Liquid Marble (gold veins), Holo Foil (thin-film colours softened, a shine sweeping), Fireflies (wandering and blinking on whole-turn loops), Hyperspace (streaks on whole-number cycles);
  - Spiral Galaxy (two arms turning half a turn a loop), Cloud Drift, Snowfall (four layers, each falling a whole number of its cells, its hash wrapped so it repeats), Fluid Ribbons, and Data Stream (pulses along a grid).

  Each has an 8.8″ design, wide and upright (55 scene designs in all). The chooser shows them as compact cards (four to a row, seven for an upright panel), with the name under each and its description on hover.
- **Animated characters** (`characters/`, `OverlayPanel.Characters`, at most 8 a panel): 35 original characters from five rigs: 8 kitties, 8 puppies, 12 anime-style chibi characters, 4 bunnies and 3 birds. *Withdrawn in Phase 50: the characters and their rig engine were replaced by character packs; the settings, motion rules and windows below stay.*
  - **The file:** one file, `characters.json`, written by `tools/characters/generate.py`. The editor draws it as SVG (`rig.ts`) and PcDiag.Core embeds it, so the recorder draws it with GDI+ (`CharacterArt.cs`; the Dockerfile copies it to the .NET stage).
  - **Rigs:** a rig's parts sit in a 100 × 100 box, each a group, an ellipse or SVG path data, hanging from a parent with a pivot. Keyframes of rotation, offset, scale and opacity move them, eased and looping, with a blink on the eyes. A character is a rig, a palette and variants: ears floppy or pointy, tails, spots, saddles, masks, socks, hair styles, outfits, hats, headphones, glasses, hoods.
  - **Actions:** idle, walk, run, sit, sleep, jump and play (dance for the chibis; a rig without an action plays the closest it has).
  - **Settings:**
    - where it plays: on the panel's top edge, inside it along its bottom, beside it on the left or right, along its screen's bottom (above the taskbar), or all around it (along its top, hopping down beside it and back up);
    - how it moves: stay, pace from end to end with a pause, or wander between six spots, doing something at each;
    - what it does, its size (24–240 px), its speed, its facing, in front of the panel or behind it, and its opacity.
  - **Motion rules** (`behaviour.ts`, `CharacterMotion.cs`): they keep no state (FNV-1a and xorshift integer hashes), so the editor and the recorder put a character at the same place at the same moment.
  - **Parity:** the editor's tests write `parity.json` (poses of five characters in three actions, motion frames for six settings, hashes); the recorder's tests read it and must match within 1e-4. This caught an editor bug that squashed characters over time (a shared default object changed in place).
  - **On the monitors:** each character has its own small layered window, click-through, out of screenshots with the panels, stacked in front of its panel or behind it. It is moved and drawn about 30 times a second.
  - **On a USB screen:** characters are drawn into its pictures with its motion frames.
  - **When they keep still:** whenever motion does (Phase 42's rules). They are not shown while arranging, and the layout has a *Characters* switch.
  - **In the editor:** they move on the editor's screen as on the real one. A *Characters* tab per panel holds each character's settings and a gallery by kind, where each plays under the mouse.
- **PC Diagnostic Storage** (`LocalObjectStore`, `LocalObjectStoreHost`, `LocalStoreSetup`): SeaweedFS run on this PC as the default storage location. It follows Video Clipper's built-in storage (`bundled.py`); the differences are noted below.
  - **How it runs:** SeaweedFS 4.48, `weed mini`, bound to 127.0.0.1 only (nothing reachable from the network, no firewall rule).
    - **Ports:** the first free block of five from 21300 in steps of 100, each with its gRPC port 10000 above (Video Clipper's start at 18300, so both can run).
    - **Data:** its data in `object-store\data` in the data folder, or a folder chosen at set-up.
    - **Bucket and keys:** one bucket, `pcdiagnostic`, and keys made up for it (an access key of 20 characters, a secret of 40), kept with DPAPI like a service's keys. Its identity file is written at every start.
  - **The program:**
    - **The installer** ships `tools\seaweedfs\weed.exe`. `build.ps1` downloads the release archive and checks the archive and `weed.exe` against pinned SHA-256 hashes (SeaweedFS releases are not Authenticode-signed). The notices list it as Apache-2.0.
    - **On the first start** the storage host sets it up as the default location: it keeps every kind of file, and This PC keeps only what the recorder reads.
    - **Otherwise** *Set it up* downloads it once (about 47 MB), checked the same way (Video Clipper checks only an MD5).
  - **Looked after:** by the storage host from the moment the recorder starts, or by the Web host when there is none: whichever holds its lock file, the other taking over.
    - SeaweedFS runs hidden at below-normal priority, in a job object of its host, so it never outlives it.
    - It is ready when its S3 port answers. It is started again when it stops, at most 5 times in 10 minutes, then it rests for 10 minutes (Video Clipper does not restart it). A copy left by a crash is stopped first, by its pid file, and only if it is weed.
    - Its output goes to `weed.log`, and its state to `storage/local-store.json` for the page.
  - **A set-up that fails** keeps nothing: the location and its keys are taken out again (Video Clipper keeps them).
  - **Removing it:** its files are copied to the others first. A store that cannot be reached can be removed anyway. Once removed by the user, it is not set up again by itself.
  - **The default location** (`StorageLocation.Default`):
    - **Making one the default** (`POST /storage/locations/{id}/default`) makes it keep every kind. The previous default stops keeping them: This PC keeps what the recorder reads, another location keeps nothing until told to. The files are copied first by the job.
    - **Removing the default** makes This PC the default again.
  - **The Storage page:**
    - **The PC Diagnostic Storage card** shows its version, whether it was installed or downloaded, *Set it up* (its folder, whether it becomes the default, what is downloaded), progress, running or stopped, its port and folder, and Start and Stop (`GET/POST /storage/local`, `/storage/local/start|stop`).
    - **A "What goes where" table** of kinds against locations replaces the per-location chips. Each location has a Default badge or *Make default*.
- **Verified:**
  - **Tests:**
    - the stacking (an exact order, kept, restored after a window jumps to the top);
    - the scenes' designs;
    - SeaweedFS for real (when `PCDIAG_TEST_SEAWEEDFS` names a folder with it): set up on a first start, files kept in it and read back, started again after it was killed, stopped when asked;
    - its download (archive and program hashes, nothing kept on a mismatch), ports, keys and command line;
    - the default's rules, removal and forced removal; the Web endpoints; the page's card and table;
    - every character posed in every action without a broken part or a colour missing from its palette; easing, looping and facing;
    - the motion rules; the parity file; the Characters tab and picker;
    - the layout's checks for characters (Core); a character drawn in its colours with its feet at its anchor, and a USB screen's characters around its panel (Agent).
  - **Harness:** all 20 scenes at 8.8″ wide and upright, the compact chooser, every character in every action, and the editor's screen with five characters around a panel.
  - **Live on this PC** (9 Oct 2026, after deploying):
    - Two solid panels were put over each other on the main monitor. A pixel where they overlap, sampled every 50 ms for 8 s (160 samples) and every 30 ms for 6 s while the foreground window changed three times (200 samples), was the front panel's colour every time.
    - The recorder drew five characters there: a kitty pacing on the back panel's top, an anime character running around it, a kitty asleep beside it, a bunny hopping inside the front panel, and a corgi wandering on top of the taskbar (the screen's working area). The front panel stayed in front.
    - PC Diagnostic Storage was set up with the Storage page's *Set it up* in 6 s: downloaded, checked, started on 127.0.0.1:21300, and made the default, This PC keeping pictures and drivers. The storage host looked after it (its parent process), and the 14 layout backups were read from it.
    - Killed, it was running again a second later.
    - The page did not show the new default until its next refresh; it now reads the locations again when the set-up ends.
    - The layout was brought back afterwards.

### Character packs: a redrawn kitten and puppy, and importing characters (Phase 50)
Requested 10 Oct 2026: the first characters looked poor, samples were to come before implementing, and the anime characters wanted were popular ones (Naruto, Itachi, Gojo, Kurisu), with movable models used in the character system.
- **Decided:**
  - Popular anime characters are not shipped: they belong to their studios and publishers, and fan-made models of them are unlicensed. A PC's user imports the characters they have.
  - Animation files are imported now. Live2D models, the format most anime character models use, come once the Live2D agreement is signed. An app that loads its users' models is an *Expandable Application*: Live2D approves it before release and takes 20 % of each sale or ¥300, whichever is higher.
  - Only the kitten and the puppy ship.
- **Withdrawn:** Phase 49's 35 characters, `characters.json`, the rig engine (`rig.ts`, `CharacterArt.cs`) and its parity file.
- **Character packs** (`.char`, `CharacterPack.cs`, `pack.ts`): every character, shipped or imported, is one file.
  - **Format:** `"PCDC"`, version 1, the manifest's length, the manifest (JSON), then PNG frames.
  - **Manifest:** name; frame width and height (at most 1024); the anchor (where the feet stand, in frame pixels); `tall`, the standing height in frame pixels; `faces` (the way the drawing looks); and a clip per action: its action, frames a second (1–60) and each frame's length.
  - **Rules:** idle is needed. At most 300 frames a clip, 1,200 in all, 48 MB. Every frame is a PNG of the manifest's size, and nothing follows the last.
  - **Missing actions:** an action without a clip plays the closest one (dance → play, run → walk, sit or sleep → idle, …).
  - **Size:** a character's *Height* setting (24–240 px) is its standing height on the screen.
- **The shipped two** (`OverlayCharacters.Shipped`): Mochi, a cream tabby kitten, and Biscuit, a golden puppy with a red collar.
  - **Drawn** as layered SVG in `web/characters`: fills with gradients, shading and highlights clipped to each part, line art, a neck joining the head to the body.
  - **Clips:** idle, walk, run (a gallop), jump, play (Mochi crouches, wiggles and pounces; Biscuit hops, wagging) and sleep.
  - **Baked:** `node characters/bake-characters.mjs` renders every frame in headless Edge at 200 px standing height, 24 frames a second (idle 15, sleep 10). Each frame is cut to what any frame draws and written in 256 colours (`png8.ts`: median cut, one palette per character, no dithering, which would shimmer).
  - **Sizes:** 2.0 MB and 1.4 MB, embedded in PcDiag.Core.
- **Import a character** (*Characters* tab › *Add a character* › *Imported* › *Import a character…*; `CharacterImport.tsx`, `importer.ts`):
  - **An animation per action:** an animated GIF, WebP or PNG; a video (WebM with transparency, or one on a flat background, at most 12 s); several PNG frames (taken in name order); or one picture cut as a sprite sheet (columns × rows).
  - **A flat background** is found from the frame's corners and taken out, its colour and closeness adjustable. A shadow cast on it (the colour darkened) becomes a see-through black shadow. Pixels along the cut's edge have the key colour unmixed from them by how far they lean its way from grey, so there is no fringe; pixels further in are left alone.
  - **Each clip** is cut to what its frames draw, bottom-centred on one spot for the feet and scaled alike: the idle clip sets the standing height, at most 320 px. A clip can be drawn larger or smaller (50–200 %).
  - **Output:** frames in 256 colours (a palette per clip) or full colour. *Preview* plays the result as it will play. *Import* saves it to the library and puts it on the panel.
  - **Memory:** a frame's pixels are made when needed, so a long video does not need memory for all of them.
- **The library:**
  - **Upload:** an imported character goes up like a picture (`POST /overlay/images`, named by its hash, `.char`) and is copied to the storage locations that keep pictures.
  - **Lists:** it is not listed with the pictures, and cannot be a panel's picture (`OverlayStyles.IsImageId`).
  - **Endpoints:** `GET /overlay/characters` lists the shipped ones, then the imported ones (name, actions, used, on this PC). `GET /overlay/characters/{id}` serves a file, bringing one kept only in storage here first. `DELETE /overlay/characters/{id}` is refused for a shipped one or one a panel has.
- **Playing them:**
  - **The recorder** (`CharacterPlayer`): each frame is decoded when first shown, at the size shown and mirrored when facing the other way, then kept (48 MB a character at most). The bitmap goes straight to the character's layered window, and is drawn into a USB screen's picture.
  - **Loading:** `OverlayService` loads characters by id, the shipped ones from PcDiag.Core and imported ones from the data folder. It lets go of imported ones no panel has, on the overlay's thread.
  - **The editor** (`CharacterFrames`): `ImageBitmap`s decoded at the size shown (600 kept), drawn on a canvas per character, mirrored about the feet.
- **Verified:**
  - **Tests:**
    - **Format:** read back as written; every unsound file refused (cut short, trailing bytes, no idle, an unknown or doubled action, a frame of another size or not a PNG, too tall, too large).
    - **Clips:** the closest one played, and looping.
    - **Shared test file:** the editor's `test.char`, read by both sides, and written back the same by the editor.
    - **256 colours:** few colours kept exactly, and a valid palette PNG.
    - **Importer:** a background taken out (a shadow on it, an outline half on it), bounds found, clips placed on the feet.
  - **In a real browser** (headless Edge, the editor's importer): a puppy's idle and walk as GIFs on green and a kitten's run as a 4 × 3 sprite sheet made a 301 × 271 character of 68 frames (760 KB) in 0.5 s. The background was found and taken out, the shadows came out see-through, and every clip stood on the same spot.
    - **Recorder:** frames by time and facing, mirrored feet, the shipped two at their height standing on their feet, a USB screen's picture.
    - **Motion parity:** the motion rules checked against `motion-parity.json`.
    - **Web:** listing, serving, importing, removal refused while used.
    - **Editor:** the picker, the delete button, the importer dialog.
  - **Samples:** every frame of the shipped two looked at on dark and light grounds before shipping.

### Characters reacting to readings (Phase 51)
Requested 10 Oct 2026: characters interact with the readings' values by their status (low, normal, high), differently for different readings (performance, temperature, bandwidth…).
- **The table** (`characters/reactions.json`, embedded in PcDiag.Core for the recorder; the Dockerfile copies it): the effects, and for each reading kind its limits, its bad way and a reaction per status.
  - **The rules** (`reactions.ts`, `OverlayReactions.cs`): the editor's tests write `reaction-parity.json` (32 cases) and the recorder's tests check them.
- **Kinds,** by source and unit:
  - performance (%), temperature (°C), bandwidth (B/s: network and disk), frame rate (fps), frame time (`game.frametime`, ms), latency (other ms: ping);
  - fan speed (RPM), power (W), and other (only the reading's own limits).
- **Statuses:**
  - **low:** the quiet end (an idle CPU, a cold part, an idle link; for frame rate, the poor end);
  - **normal;**
  - **high:** past the warning limit (for frame rate, the great end);
  - **critical:** past the critical limit the bad way.
  - **Limits:** a reading's own warning and critical limits count when it has them (going down when it says so). Otherwise the kind's, for example 12 / 75 / 92 % for performance and 40 / 75 / 88 °C. Bandwidth and power use shares of the reading's full scale when it has one, otherwise 20 KB/s, 5 MB/s and 50 MB/s (15, 150 and 300 W).
- **Reactions** (an action and an effect):

  | Kind | Low | Normal | High | Critical |
  |---|---|---|---|---|
  | Performance | sleeps, zZ | its own way | runs, sweat | jumps, ! (it sat with dizzy stars until 16 Oct, which looked asleep) |
  | Temperature | stands, snowflakes | its own way | sits, steam | jumps, a flame |
  | Bandwidth | sits, waiting (…) | walks | runs, data stream | runs, sparks |
  | Frame rate | sits, rain cloud | its own way | dances, music notes | jumps, alarm (!) |
  | Frame time | dances, music notes | its own way | sits, rain cloud | jumps, alarm (!) |
  | Latency | plays, hearts | its own way | sits, waiting (…) | jumps, alarm (!) |
  | Fan speed | sleeps, zZ | its own way | walks, wind | runs, wind |
  | Power | sleeps, zZ | its own way | runs, sparks | jumps, sparks |
  | Other | its own way | its own way | stands, alarm (!) | jumps, alarm (!) |
- **Per character** (`OverlayCharacter.React`, `.Reactions`):
  - it reacts to its panel's most urgent reading (`auto`: critical before high before low; the default for new characters), to one reading, or to none;
  - each status's action and effect can be its own (*Characters* tab › *Reacts to readings*, which also says what it is doing now and why).
- **Moving with it** (`Reacting`, `CharacterReacting`):
  - **No flicker:** a new status must last 2 s before the character changes.
  - **Pace:** each character keeps its own clock along its route. It runs at 1.8× while running and stops while doing something in place (sleep, sit, jump, play, dance, stand), so it carries on from where it was instead of jumping to where the route would put it. While moving, the reaction's way of moving replaces its walk.
- **Effects** (`web/characters/effects.js`, baked into `effects.char` with the characters: 14 loops, 1.1 MB):
  - **Attached** to the top of the head or the feet. A character's head is in its file (`head`); an imported one's is the top of its height above its feet.
  - **Mirroring:** glyph-like effects (zZ, !, notes) never mirror. Streams (data, wind) turn with the character.
  - **On the monitors** a character and its effect are drawn into one picture for its window. On USB screens and in the editor they are drawn one over the other.
- **Verified:**
  - **Tests:** kinds, statuses (own limits, the bad way down, full-scale shares) and reactions, against the shared parity file; own reactions; `auto`; the hold; the clock; validation and JSON.
  - **Recorder:** a kitten on a quiet PC asleep where it was, its zZ above its head.
  - **Editor:** the *Reacts to readings* controls.
  - **Live on this PC** (10 Oct 2026, after deploying):
    - **The Gaming HUD:** the kitten reacted to the HUD's most urgent reading (GPU at 4 %, low) and slept on the panel's top edge with zZ, staying where it fell asleep. The puppy reacted to CPU temperature (63 °C, normal) with its own reaction for normal, dancing with music notes.
    - **Phase 50:** a character imported in the live editor from GIFs on green was drawn by the recorder cleanly keyed, with its soft shadow.
    - **Afterwards:** the layout was brought back.

### Moving scenes with frosted glass over the readings, and a picture library (Phase 48)
Requested 10 Oct 2026, on seeing the Phase 47 samples: the drawn art and its motion looked dated, while the real pictures with frosted cards were good. The request was for generated, animated backgrounds that outline the layout, and for pictures chosen from the object store or uploaded (stills or animations), which also go to the storage configured.

- **Moving scenes** (`generative.ts`, WebGL2), drawn on the graphics card:
  - Eight scenes: Liquid Silk, Mesh Gradient, Aurora Veil, Nebula Drift, Deep Caustics, City Bokeh, Topography and Prism Light. Each is made from 4D simplex noise, with its time on a circle in noise space, so it loops with no seam.
  - Each scene has its own palette and accent (`OverlayStyle.Art`, the generated kinds).
- **Frosted glass over the readings:** under each tile (`panelGeometry`, as in Phase 47) a card of glass shows the scene blurred behind it (a separable blur at a quarter size). Each card has:
  - a veil that darkens over bright parts, so light text always reads;
  - a light running round its edge, a highlight along its top, and a shadow;
  - under each dial, an etched track and fine ticks along the dial's own sweep.
- **Glass over a picture** (`Art` "glass", `OverlayStyle.ArtSource`): the same cards over a library picture or animation, its frames blurred one by one.
- **Animations** (".anim", `OverlayAnimation` in Core): JPEG frames played in a loop.
  - **Format:** "PCDA", version, frame count (at most 300), frames a second, size, then each frame's length and the frames.
  - **Checks:** every frame must be a JPEG of the animation's size; the reader refuses anything cut or out of bounds.
  - **The recorder** (`AnimatedPicture`, `OverlayRenderer.MotionLayers`) draws the frame due by the clock under the readings, on the motion timer. It keeps every frame decoded when they fit in 48 MB; otherwise it decodes only the one due.
- **Made for the recorder** (`liveArt.tsx`, `bakeArt`): when a scene is chosen, a design is added, or the panel's size, readings or look change (1.5 s after it settles), the editor bakes:
  - a scene into 96 frames (an 8-second loop at 12 fps) at up to twice the panel's size (a 150 % monitor drew a 1× bake soft), at most 1920 on its long side;
  - glass over a still into one JPEG, and glass over an animation frame by frame.

  It is saved as the panel's `Image`, marked as made for a panel (`?generated=true`), with "Making the moving background… n %" shown while it runs. An 8.8″ bar's loop took 2.7 s on a software GPU and is 6.5 MB.
- **Live in the editor:** one WebGL context, shared by every preview (a page may hold only a few), draws each preview on its canvas at the size it shows. Render targets are kept for the eight most recent sizes.
  - **When previews move:** at most 20 times a second each, never off screen, in a gallery or menu only while the mouse is on that card, and never when the system asks for less motion.
  - **Fallbacks:** without WebGL2 a scene shows its colours as a gradient. A lost GPU context is made again.
  - **Animated pictures** play on a canvas too.
- **The picture library** (`GET /overlay/images`):
  - **What it lists:** the pictures on this PC and those in the other storage locations that keep pictures, with name, kind, size, frames, whether a panel uses one, and whether it is here. Names and kinds are kept in `storage/pictures.json`.
  - **Uploads** (`POST /overlay/images?name=`) are kept until deleted and copied to the storage locations that keep pictures (Phase 43) in the background.
  - **Pictures made for panels** are not copied (they are made again at every change). They are removed an hour after no panel uses them.
  - **Other actions:**
    - `POST /overlay/images/{id}/fetch` brings back a picture that is only in storage.
    - `DELETE /overlay/images/{id}` removes one here and in every location; it is refused (409) while a panel shows it.
  - **Limits:** 8 MB for a still, 48 MB for an animation.
- **Uploading** (`library.ts`):
  - **Stills:** a photo or artwork is shrunk to 1920 pixels.
  - **Animated pictures:** an animated GIF, WebP or PNG is decoded frame by frame (WebCodecs `ImageDecoder`, at most 300 frames, 1280 pixels, at its own rate).
  - **Videos:** a short MP4 or WebM, up to 20 s at 15 fps, has its last 0.6 s faded into its start so it loops.
  - **Size:** an animation is compressed further when it would pass 46 MB.
- **The background chooser** (Look › Background › Picture, `BackgroundChooser.tsx`):
  - **Moving scenes:** each shown live behind this panel's own readings.
  - **Pictures and animations:** upload (or drop) a file. It is listed with where uploads are copied, thumbnails that play while the mouse is on them, *In use*, *In storage only* with a button to bring it here, and delete.
  - **Glass:** the option *Frosted glass cards over the readings* is on by default for a panel of tiles at a fixed size.
- **31 whole-screen designs and 8 panels** (`SCENE_SCREEN_DESIGNS`, `SCENE_PANEL_DESIGNS`), listed first:
  - every scene for the 8.8″ wide (four layouts) and upright (three layouts);
  - designs for the 9.2″ and 12.3″ bars, portable monitors, monitors upright and wide, the 10.1″, 7″, 5″, 480 × 480 and 3.5″;
  - roomy layouts (padding 28, spacing 170) so the scene shows round each card.
- **Fixed on the way:** a reading shown as a percentage of its total (VRAM in bytes against the card's memory) was judged against its limits in bytes, so VRAM always showed its warning colour. It is now judged by its percentage, in the editor and the recorder (`level`, `OverlayStyles.Level`).
- **Verified:**
  - **Tests:**
    - Animations are read back as written, and anything cut, not JPEG or too long is refused, in Core and the editor.
    - The recorder plays an animation frame by frame under the readings.
    - The library keeps uploads and animations, clears pictures made for panels once unused, refuses to delete one in use, and brings one back from storage.
    - Glass cards follow every tile and dial.
    - Every scene design is a fixed size with its scene, drawn live, with a card per reading.
    - A still stays a picture.
    - Without a GPU nothing is made.
    - Percentages are judged as percentages.
    - The chooser picks a scene, picks a picture with glass or as it is, and brings one back from storage.
  - **Harness:** every 8.8″ scene design was checked wide and upright, and the chooser's two tabs. A baked frame matches the live one.
  - **Live on this PC** (9 Oct 2026, after deploying):
    - "Liquid Silk Trio" added from the gallery had its animation made and saved by the editor; the recorder played it on the main monitor at 150 %, the silk moving between screen captures and each dial in its etched track. A 1× bake looked soft there, hence the bake at twice the size.
    - An animated GIF uploaded through the chooser became a 24-frame animation in the library (named after its file). It was copied to a folder location that keeps pictures; the pictures made for the panel were not.
    - Chosen with glass, it was saved within seconds, and the recorder showed its glow moving behind the cards.
    - The layout was then brought back from the backup kept by the first save.

### Designs with their own pictures drawn around their readings (Phase 47)
Requested 10 Oct 2026: designs with background pictures made for them that outline the readings, charts, gauges and controls (the tile backdrops of Phase 46 were too subtle to be seen as that), and a small preview of each panel in *Add a panel*.

- **Art** (`OverlayStyle.Art`, `art.ts`): a picture drawn from where the panel's readings are, using the tile layout the editor and the recorder share at 100 % (`panelGeometry`: each tile, its label line, and the room under it for a dial, a graph, a bar, a value or a light).
  - **Behind each dial:** a gauge face with a rim. Inside the dial's track sits its scale, along the dial's own sweep (an arc 250° from 145°, a speedometer 220° from 160°, a ring all round): major and minor ticks, numbers when the reading's full scale is known (0–100 for percentages, its max otherwise), and a red zone from its warning.
  - **Graphs, bars, values and lights:** a scope grid with divisions behind each graph, a channel with a scale and a red zone under each bar, a window behind each value and a socket around each status light.
  - **Around the tiles:** frames (notched and glowing, beveled, instrument plates, hologram brackets, neon, terminal boxes, gold or brass with rivets), traces with nodes across the gaps, LEDs, toggle switches and screws, over a textured background (hexagons, carbon, grain, scan lines, pinstripes, stars, a synthwave sunset and grid floor).
  - **Eight kinds:** cockpit, racing, aviation, holo, synthwave, brass, terminal and lux. Each art's tile is clipped to its tile.
- **Shown at once, saved for the recorder:**
  - The editor draws the art live as an SVG behind the panel, so it lines up whatever the panel's size.
  - When a design is added, and again 0.6 s after the panel's size, readings or look change (resize, fill, fit, grid cell, upright layout), the editor draws it to a picture and saves it as the panel's `Image`. It draws at up to twice the size, as PNG, or JPEG when large (a 480 × 1920 tower is a 960 × 3840 JPEG of about 450 KB). The recorder draws it like any picture, stretched under its readings, so it needed no change of its own. `Art` only tells the editor what to draw again; a recorder that does not know it ignores it.
- **26 whole-screen designs** (`artDesigns.ts`), listed first:
  - one of each art for the 8.8″ upright and the 8.8″ wide;
  - the rest for portable monitors, monitors, the 7″, the 10.1″ upright, the 5″, the 480 × 480 and the 3.5″ upright.
  They are built around dials, graphs and bars so the art has faces and grids to draw. There are also **8 panels** for monitors at fixed sizes, also listed first.
- **Add a panel:** a card for each template with a preview of the panel it adds, in the look it will take (the last panel's, without a picture made for that one). Menu items can now carry a description and a preview, shown as cards side by side.
- **Verified:**
  - **Tests:** the geometry (tiles in the padding, dials inside their tiles with their sweeps). Every art draws a face per dial, its ticks, the numbers for a known scale and its red zone, and nothing for a panel that is not tiles or has no fixed size. What a picture depends on is a panel's size and readings, not its picture. Every art design fits its size with its art and no picture yet. The editor draws the art live, stretched. The recorder accepts an art with or without a picture and refuses an unknown one.
  - **Harness:** every art design was checked at its own size with no tile cut off.
  - **Live on this PC:** "Starship Console" added from the gallery had its picture made and saved within seconds. The recorder drew it on the main monitor with the gauge faces' ticks and numbers under its own dials. The layout was then brought back from the backup kept by that save.

### Designs for every screen, upright and wide, with fitted backdrops (Phase 46)
Requested 10 Oct 2026: more designs for standard and portable screens, especially the Lian Li Universal Screen 8.8″ (L-Connect), upright and wide; designs whose background fits where their readings are; many of them.

- **Backdrops** (`OverlayStyle.Backdrop`; *Look › Tiles › Behind each tile*): drawn behind every tile and around every dial, fitted to the tile wherever it is and whatever its size. They line up after a resize, a zoom or a fit to another screen, which a picture made for one layout would not. Rather than one picture per design, each tile draws its own:
  - *plate*: a metal plate with a screw in each corner;
  - *hud*: corner brackets in the accent and a line under the label;
  - *bezel*: a band and a fine ring around each dial, a double frame around the rest;
  - *socket*: a recess, dark at the top and lit at the bottom;
  - *chevron*: cut corners outlined in the accent, with a tab; the tile itself takes the cut shape;
  - *glass*: frosted, with a fine edge and a highlight;
  - *neon*: a glowing outline in the accent, in the warning or critical color past a limit;
  - *blueprint*: a fine grid in a frame.
- **Drawn the same in both:** sizes are in units of the panel's scale, and the recorder (`OverlayBackdrops.cs`) and the editor (`TileBackdrop.tsx`, an SVG measured to its tile) draw the same shapes. They are drawn over the tile's color and under its readings; a tile's outline past a limit stays on top. A panel never changes size for its backdrop.
- **Screen sizes** (`screenKit.ts`):
  - upright versions of the 8.8″, 9.2″ and 12.3″ bars (480 × 1920, 464 × 1920, 720 × 1920), the 5″, 7″ and 10.1″ screens and portable monitors (1080 × 1920);
  - standard monitors used whole as a dashboard: 27″, 2560 × 1440 and upright.
  Every size has designs made for it.
- **50 whole-screen designs** (`screenDesigns.ts`, 64 in all), each a look and its backdrop:
  - 14 for the 8.8″ upright (HUD, Machined, Gauge, Neon, Frosted, Blueprint, Mech, Stealth, Command Column, Game, Thermal, Starfield, Phosphor and Pulse towers);
  - 9 for the 8.8″ wide;
  - the rest for the other bars, small screens, round screens, tablets, portable monitors and standard monitors, wide and upright.
  On an 8.8″ (about 220 pixels per inch) the text is larger than on a monitor. Several designs are more than one panel (a clock header, the readings, the network at the foot).
- **17 panels for monitors** (`panelDesigns.ts`, 45 in all): cards, one-row strips for the top or bottom of a monitor, and upright columns for a monitor turned upright or a screen edge.
- **Verified:**
  - **Tests:** every backdrop is drawn inside its tiles and never changes a panel's size, and an unknown one is refused. The editor draws one layer per tile under the readings, at the tile's measured size, with a bezel only around a dial. Every design builds from this PC's values within the recorder's limits (at most 6 columns).
  - **Recorder:** the new screen designs all pass its validation.
  - **Harness (scratch):** the editor and the recorder drew the eight backdrops alike. Every new screen design was composed at its own size and checked for tiles reaching past their panel or values wider than their tile; the 15 found were fixed, so none remain. The recorder drew the 8.8″ upright towers as the editor does.
  - **Known limit:** a design fitted to a much wider screen of another shape (an 8.8″ upright tower on a 12.3″ upright) can need more height than it has. The gallery shows designs of another shape only on asking.

### Where files are kept (Phase 43)
Requested 10 Oct 2026: a file storage module, S3-like, configured as freely as Video Clipper's, for driver packages and any other files. Answer chosen: folders and S3-compatible services.

- **Built in, nothing to install**: the storage is part of PC Diagnostic: no database or object server, no container, no Docker. This PC needs nothing. A folder or an S3-compatible service is used only when one is added, and the service is one the user already has (a cloud bucket, a NAS); PC Diagnostic is only its client.
- **The module** (`PcDiag.Storage.Files`): `FileStore` keeps files of nine kinds, *panel pictures*, *characters*, *character drafts*, *settings and layouts*, *layout backups*, *device drivers* (driver packages and Windows drivers imported, Phases 44 and 45), *diagnostic reports*, *problem reports* and *kept exports*, in **storage locations**, like Video Clipper's:
  - *This PC*, the data folder, always first. Pictures and characters stay where the recorder reads them (`overlay-images`), the other kinds go in `files\<kind>`.
  - *Folders*, on another drive or a network share: `<folder>\PcDiagnostic\<kind>`, each file written under a temporary name and then moved into place.
  - *S3-compatible services* (AWS, MinIO, Cloudflare R2, Wasabi, Backblaze B2, SeaweedFS…; AWSSDK.S3): address, region, bucket, folder in the bucket, bucket in the path or not. Each file is the object `<folder>/<kind>/<name>`, with its SHA-256 in its metadata. Checksums and chunked signing are used only when the service needs them, because several compatible services refuse the SDK's defaults.
- **What each keeps** is chosen per location. A file is written to every location that keeps its kind, and read from the first that has it. Every kind is always kept somewhere. What the recorder reads (pictures, characters and drivers) always stays on This PC, so the recorder never depends on a network; other locations keep copies of them. **The default location keeps every kind** (also kinds added in later versions), so it holds everything PC Diagnostic has.
- **Keys** are stored only on this PC, protected for the user by Windows DPAPI (`storage\keys.dat`). The browser learns only whether keys are set and their last four characters.
- **Started with Windows, by itself** (`PcDiag.Agent.exe storage-host`, `StorageHostMode`, `StorageUpkeep`): when the recorder starts at logon, it starts the storage host (`StorageHostLink`, *Agent:StorageHost*, on).
  - **Without administrator rights**, through Explorer like the screen host, so files are written to folders and services as the signed-in user, never as administrator. It connects back with a one-time token over a pipe only this user can open.
  - **Its life** follows the recorder's: a host that stops is started again (at most 5 times in 10 minutes), it ends when the recorder does, and one runs per data folder.
  - **Its work**, with or without the dashboard open: it sets the storage up on its first start (This PC's folders, `storage\locations.json`), carries out a change left unfinished, and brings the locations up to date every 15 minutes, so a share or a service that was away gets what it missed. It also keeps the on-screen layout as it was before it changed outside the editor (arranged or closed on the screens), by the editor's rules.
  - **Shared with the Web host** (the page, uploads, layout saves), through the storage files: each takes in the locations and keys the other saved, one job runs at a time (`storage\sync.lock`; the other waits, at most 10 minutes), and the job's state and the problems seen are shared (`storage\state.json`, the newest winning; a job of a process that has gone is not running).
- **A test before adding**: writes, reads back and deletes a small file there. Refusals are said plainly: a relative path, no keys, keys not allowed to write there (or a wrong clock), no such bucket, a service that cannot be reached, an answer that is not S3.
- **Bringing them up to date**: when a location is added or keeps more, a job copies to it what it is missing, from the first location that has each file, checking each copy's size. A marker file makes the job resume after a restart. Removing a location first copies what only it has to the others; its own files stay where they are.
- **Layout backups** (`LayoutBackups`): every save of the on-screen layout first keeps the layout as it was (at most every 5 minutes while editing, never the same content as the newest; the newest 50), and *Bring back* restores one while keeping the layout it replaces. This would have saved the layout lost on 9 Oct. `GET /api/v1/overlay/backups`, `POST /api/v1/overlay/backups/{name}/restore`.
- **Kept exports**: the incident's *Export* menu has *Keep a copy too*. The report, PDF, JSON or ZIP is then also kept in the locations that keep exports; CSV files are streamed as they are written, so they are only downloaded.
- **The app's own files** (`StoreKeeper`, in the Web host, 20 s after start then every 2 minutes): the live files stay on this PC, where the recorder and the Web host read them, and the locations keep copies, sent again only when the content changed (SHA-256s in `storage\kept.json`):
  - *Characters*: the 9 that come with PC Diagnostic (`<id>.char`, from the program itself) and those imported or generated (earlier versions kept them with the pictures; they are read from either).
  - *Settings and layouts*: settings.json, overlay.json, dashboard.json, event-rules.json, expected-hardware.json. Never the paired devices' keys (devices.json) or the network certificate.
  - *Diagnostic reports* (`diagnostics\*.json`), *problem reports* (`reports\outbox`) and *character drafts* (`characters\generations\<job>\still-<n>.png`, kept as `<job>-still-<n>.png`); one deleted here is deleted from the locations too.
  - *Device drivers*: each zip imported (screen and sensor packages, Windows drivers) is kept after a successful import, named by its SHA-256.
- **Pictures** added to panels are copied to the other locations that keep pictures in the background, so a slow service never holds the editor.
- **Page** (*Storage › Where files are kept*): every location with whether it works, where it is, its key's end, what it keeps (chips; This PC's pictures, characters and drivers fixed, and every kind for the default), up and down (the reading order), keys changed and removal after confirming. *Add a folder* and *Add an S3 service* forms each have *Test*. *Bring up to date* shows the job's progress. *Layout backups* lists the backups newest first, each with *Bring back*. Locations are changed on this PC only (`/api/v1/storage` refuses changes from other devices).
- **Verified** by tests:
  - Each kind in its place on This PC.
  - A folder added gets the backups kept before, and new ones as they are made.
  - Reading falls back to another location when This PC lost a file.
  - The rules that keep every kind somewhere.
  - Refusals of bad locations and unsafe names.
  - A fake S3 service on this PC: path-style PUT/GET/DELETE/list, SHA-256 metadata, the probe deleted, a refusal said plainly.
  - The secret key absent from the file on disk and read again by the next run.
  - The API: add, test, keeps, removal rules, backups kept on saves and brought back.
  - The page.
  - Two stores on one data folder (the Web host and the storage host): locations added and removed by one are the other's, the newer job is shown, a job waits for the other's and goes ahead after its patience.
  - The upkeep: the storage set up on its first start; the layout before a change kept, and not again within 5 minutes; the same content never kept twice.
  - The storage host started through the launcher with its token and data folder, the storage set up, the host ended with the recorder; an impostor without the token turned away; bad arguments refused.
- **Verified on this PC** (9 Oct 2026, Web host and storage host deployed):
  - A layout save kept a backup.
  - A folder added was tested (a relative path refused) and got the backup.
  - An S3-compatible server on this PC with signatures checked (SeaweedFS, used only for this check and removed afterwards): wrong keys refused plainly, the right ones tested, the service added and given the backup with its SHA-256 in its metadata (the same as the file's).
  - A panel picture uploaded and copied to the service in the background.
  - A backup brought back: the recorder reloaded it and the layout was exactly the one before; the layout it replaced was kept too.
  - An incident exported with *Keep a copy too*, landing on This PC, in the folder and on the service.
  - The secret key in neither file as plain text.
  - The test locations removed afterwards.

### More themes and moving backgrounds (Phase 42)
Requested 10 Oct 2026: more panel themes, more compact previews, and elegant, futuristic panels with moving backgrounds.

- **24 themes** (from 9): Obsidian, Graphite Gold, Nordic, Ocean, Midnight Rose, Forest, Paper, Holo and Synthwave, and six that move: Aurora Flow, Cyber Grid, Starfield, Pulse Core, Scan HUD and Liquid Glass. The theme picker is compact: four small samples a row, each this panel itself in the theme.
- **Moving backgrounds** (`OverlayStyle.Motion`, `MotionSpeed` 25-300 %; *Look › Background › Motion*): aurora (soft lights in the accent and gradient colors drifting), grid (accent lines scrolling down), stars (a drifting, twinkling field), pulse (a breathing glow), scan (a band of light sweeping down) and shimmer (a sheen sweeping across), over the panel's color, picture and pattern and under its readings, as strong as its background is opaque.
- **In the recorder:** a moving panel is drawn once per refresh as two layers, its background and everything above it (`OverlayRenderer.RenderLayers`, `MotionLayers`), and a separate timer composes them with the motion 30 times a second without drawing the readings again; USB screens as often as their *Moving backgrounds* setting says (every 33 ms to every second, `OverlayDevice.MotionFps` 1–30, default 30, at most the model's own rate), their pictures encoded off the panels' thread (one at a time per screen, a picture coming meanwhile passed over). A panel without motion is drawn as before. Motion keeps still, at its first moment, while the panels are hidden, arranged or dragged, the PC is locked, used over Remote Desktop or on battery saver, Windows' animations are off; a full-screen game keeps nothing still (since 16 Oct 2026: the panels are there to be seen with it; `PauseMotionInGames` is no longer used); *Moving backgrounds* switches it off. An animated picture (baked at 12 frames a second) shows, between two frames, a blend of them (`AnimatedPicture.FrameAt`: both premultiplied, blended two channels at a time), so it moves smoothly at 30. **Gliding values** (`OverlayConfig.SmoothValues`, *Smooth value changes*, on; `OverlayGlide.cs`): at a refresh a number that changed counts to its new value over 0.45 s, easing out, its bar, ring or dial sweeping along, drawn 30 times a second (a USB screen at its own rate); text, limits, units and new readings change at once, and values jump when the refresh is faster than every 0.5 s, while arranging or dragging, and whenever motion keeps still.
- **In the editor:** the same motions as CSS animations of transform and opacity only, still when the system asks for less motion and, in the galleries, until the mouse is on a card.
- **Six moving designs:** Aurora Flow HUD, Cyber Grid Strip, Starfield Clock and Pulse Core (panels, 28 in all), Aurora Drift Bar and Starfield Command (whole screen, 14 in all).
- **Verified** by tests (the layers composed at a still moment match the panel drawn in one go within 3 levels; each motion changes the panel between two moments and stays inside it; motion settings are checked; the designs and themes build), and on this PC with the recorder hosted in a test program: three moving panels changed on the screen from one half-second to the next, stopped when moving backgrounds were switched off, and cost 10.3 % of one core (about 2.3 ms a panel frame; 0.4 % of this 24-core processor).

### Panels on the screens, the editor's controls and orientation (Phase 41)
Reported and requested 10 Oct 2026 with two snapshots: clicking a panel over the taskbar put it behind the taskbar until it came back; Designs should suit the selected screen; the editor's panels should have a control box; monitors should have portrait and landscape layouts, detected; panels should be movable, resizable and closable on the screens (off by default); and the panel editor's groups were in the wrong places.

- **Above the taskbar** (41a): a click on a click-through panel reaches the taskbar under it, which Windows then raises above every always-on-top window; the panels took the top back only every 2 seconds. A spike logging the window events showed the first click is a foreground change to the taskbar and later ones only a mouse capture in explorer (the taskbar is already in front), with a change of the windows' order each time. The overlay now hooks those three (`SetWinEventHook`, out of context, not for its own windows: `TopGuard`) and raises the panels at once and again 100, 300 and 1000 ms later, at most 10 times a second; a window that keeps taking the top back (more than 20 times in 5 s) is left to the 2-second raise for 30 s. Start, Search and the notification flyouts are above every program's windows by design: the panels return when they close.
- **The panel editor regrouped** (41b): Themes, Text, Colors, Background (its color, gradient, opacity and sheen, then Picture and Pattern), Frame and effects (corners, padding, border, shadow, glow, whole-panel opacity, accent edge, title band, outline), Tiles (only for tiles) and Bars, rings and graphs; Layout gains *On the screen*.
- **A control box** on the selected panel in the editor's screen (41c): fill the screen, put it in a cell of a 2×1, 1×2, 2×2, 3×2 or 3×3 grid (each cell a button), fit it to the screen keeping its proportions (its text and fixed size grow or shrink by the room there is, padding and margins apart), and restore its size and place from before (`OverlayPanel.Restore`, kept until restored; ignored when deciding whether a panel's size is kept). *Arrange all* puts every shown panel of the screen into a grid's cells, or an automatic one for their number, as square as the screen allows.
- **Designs for this screen** (41d): each designed panel is measured once at 100 % and marked *Fits this screen* or *Larger than this screen: shrunk to fit*; those that fit come first, and a filter keeps only them; one added to a smaller screen is shrunk to fit. The whole-screen designs show those made for the screen's shape, with *Show all shapes*.
- **Portrait and landscape** (41e): each monitor reports its rotation and its shape (`OverlayDisplay.Rotation`, `Orientation`: upright when taller than wide, since some screens are upright untouched). A screen can keep panels of its own for when it is upright (`OverlayScreen.Portrait`, null meaning the same panels); the recorder shows the list for the screen's shape now, looked at again as soon as Windows says the displays changed, and a drag in progress then ends. Panel ids are unique across both lists, so a panel is found and changed in whichever holds it, and the whole layout's pictures and readings include both. The editor edits *Wide* or *Upright* (the screen's own marked "now", the other drawn in its shape), and *Make a layout for upright* copies the panels with new ids, each shrunk if it is too large.
- **Handles on the screens** (41f): *Move it with the mouse*, *Resize it from its corner* and *Close it with ×* (each off by default). Resting the mouse on such a panel for 0.3 s shows a window of its size with a grip bar, a close button and a resize corner over it, transparent everywhere else: Windows sends a click on its transparent pixels to what is under it, so only the handles take the mouse and the panel stays click-through (a click on its body still reaches the game or the taskbar). The mouse is looked at 10 times a second (30 near a panel) only while a shown panel allows handles, with no mouse hook; no handles while the cursor is hidden or held in a window (games), a full-screen program is in front, or a button is already down. Dragging reuses arranging's moving and resizing; a closed panel is hidden without changing the layout, until the show/hide hotkey shows the panels again or the tray's *Show the closed panel*.
- **Verified** by tests (the control box's geometry, grids for a count, portrait lists, both orientations' validation and choice, a place to restore, the handles' picture: opaque on every pixel of a handle, transparent elsewhere), editor tests (the control box, *Arrange all*, *On the screen*, making an upright layout), and with real mouse input on the recorder hosted in a test program: resting shows the handles (crossing does not), a click on the body reached a window under the panel, the grip bar moved it and the window followed, the corner resized it, × closed it with the layout unchanged, and it came back.
- **Verified live** (9 Oct 2026, after deploying): with the line panel over the taskbar, three clicks on the taskbar (real input) left the panel above it at 50, 150, 500 and 1200 ms after each; the recorder never backed off, and the USB screen kept showing its panels.
- **Fixed after reports the same day:** the editor's canvas showed a panel stuck huge after turning the screen in the editor (Upright, then Wide) or zooming: the size a panel keeps while shown (so it does not jump as values change) was applied once at the new scale before being measured again, and so kept; it now applies only at the scale it was measured at (seen: the line panel 230 × 22 became 401 × 30, and stays 230 × 22 now). And the control box after a panel was resized by hand: Restore went back to a size from before earlier changes, *Fill the screen* left readings cut off, and snapping to an edge did not line up. Now a move, resize, snap or size change by hand is the panel's size and place (what Restore goes back to after the next fill, grid or fit); filling the screen or a cell makes the text smaller when the readings, measured at their own size, would not fit; resizing from the corner keeps the top-left corner; *Fit* reaches the screen's edge exactly; and panels are placed by their box rather than their shadow, in the recorder and the editor alike, so at an edge, filling the screen or in a cell they line up exactly (the shadow going past the edge). Verified live on the 8.8″ screen in the editor: set to 1500 × 300 by hand, filled (1920 × 480, text 34 → 30.5), restored (1500 × 300, 34), fitted (1920 × 384), restored, snapped to the bottom right (its box at both edges).

### Twelve more designed panels (Phase 40)
Requested 9 Oct 2026: more ready-made panels, elegant ones. *Designs › Panels* now has 24.

- **The twelve:** Glass Mono (monochrome frosted glass, white rings, a hairline border), Neon Tokyo (pink and cyan over scanlines, graphs and columns), Nordic Minimal (the Nord palette, bars), Carbon Fiber Pro (a red title band over carbon, speedometers), Graphite Gold (graphite, gold rings and a gold edge), Ocean Depth (abyss blue to lagoon, arc dials and sea-glass tiles), Retro LCD (a handheld's green LCD with segment bars and a pixel grid), Holo Cyan (a see-through holographic HUD with a glowing frame), Matte Paper (light and warm, the time and date first), Midnight Rose (plum and rose rings), Forest (pine to moss on a honeycomb) and Stealth Ring (four quiet rings on smoked black). Each uses this PC's readings, the value spaces and alignment of Phase 34, and the patterns and visuals the recorder draws.
- **Verified** with the parity harness: the recorder accepts all 24 designs, and the editor and the recorder draw the new ones the same (within 4 % at the editor's screen scale, as the others); tests build every design from this PC's values and draw it.

### Screen sizes and whole-screen designs (Phase 39)
Requested 9 Oct 2026, with the USB screens: designs for the sizes such screens come in.

- **Screen sizes** (`web/src/features/overlay/screens.ts`): bars of 1920 × 480 (8.8″), 1920 × 464 (9.2″) and 1920 × 720 (12.3″); rounds and squares of 480 × 480 and 400 × 400; 480 × 320 and 320 × 480 (3.5″), 800 × 480 (5″), 1024 × 600 (7″), 1280 × 800 (10.1″) and 1920 × 1080 (portable 14–16″), each with the screens it fits. A USB screen's size comes from its driver; a monitor's from Windows.
- **Twelve whole-screen designs**, at least one for each kind: Racing Strip and Aurora Glass Bar (8.8″), Neon Ribbon (9.2″), Command Deck (12.3″), Pump Dial (480 round, room for the cut corners) and Quad Rings (400), Pocket Stats (3.5″, light) and Ocean Tower (3.5″ upright), Midnight Grid (5″), Studio Seven (7″, graphite and gold), Paddock Telemetry (10.1″) and Mission Control (portable: a header line and a grid of sixteen readings, two panels). Each is opaque, square to the screen's edges and without a shadow.
- **Fitted to the screen:** *Designs › Whole screen* previews each live on this screen's shape (or at the size it is made for), those made for its shape first, filtered by the size made for. *Use* replaces the screen's panels with the design at the screen's size at 100 % scaling: each panel keeps its share of the screen, and its text, padding and corners follow the smaller of the two ratios so its readings keep fitting; a design of another shape says it is stretched. *Undo* brings the screen's own panels back. A USB screen opens the gallery on these designs.
- **A panel the size of its screen fills it:** the room kept around a panel for its glow is kept only for a glowing border (text and dials glow within the padding), and a borderless panel's background covers its edge pixels; before, a glowing design was drawn 16 px larger than its screen and cut at two edges.
- **Verified** with the parity harness (each design at its own size, at the other sizes of its kind and on a portable monitor at 125 %, 42 screens): the editor and the recorder draw every panel exactly its fixed size, and none needs more room than it has (a panel drawn at the size its readings need is never larger; Ocean Tower was rearranged when it was); the 41 earlier panels still agree as before. Tests: every design fills its size exactly with this PC's readings and stays within the recorder's limits when fitted; the gallery fills a USB screen and undoes it; a borderless glowing panel is exactly its size, a glowing border keeps its room.

### USB screens with external drivers (Phase 38)
Requested 9 Oct 2026: support screens that connect over USB (the PC has a Lian Li Universal Screen 8.8″ that L-Connect drives), dynamically, with drivers from outside the app, and in many sizes. The user chose *take over on demand*: WinUSB lets one program use a device at a time.

- **The screen:** USB 1CBE:A088 on Microsoft's WinUSB driver (interface 0, bulk OUT 0x01, IN 0x81), a TURZX panel 480 × 1920 mounted as a 1920 × 480 bar. Every command is a 512-byte header: its 500-byte plaintext (command, 0, 1A 6D, a millisecond timestamp, parameters) DES-CBC encrypted with key and IV "slv3tuzx" to 504 bytes, then six zero bytes and A1 1A; a picture is PushJpg (0x65) with the JPEG (at most 512,000 bytes) in the same transfer. The panel answers each command (the command, C8 for success), a full answer followed by a zero-length packet. Facts from Hypercolor's spec 84 and lian-li-linux (MIT), checked on the screen: firmware lianli88_0001_0023, each picture acknowledged in about 11 ms, a program without administrator rights can open it once L-Connect lets go.
- **Drivers outside the recorder** (`PcDiag.Screens.Sdk`): a driver says which screens it drives (vendor and product ids, size, picture format, frame rate, how it is usually mounted) and opens sessions that show pictures and set the backlight. A **package** is a folder in `%ProgramData%\PcDiagnostic\screen-drivers` (only administrators write there) with a `driver.json`: either a known protocol family with its screens (no code; `turzx-winusb` is the first), or a .NET assembly and its driver type. A package is used only once approved by its SHA-256 (of driver.json and the assembly) in the editor's *Screen drivers*; changing a file needs approving again. The Lian Li package is built in: the 8.8″ (tested), and the Vision 9.2″, HydroShift II round and square and Lancool 207 Digital from the same family (untested).
- **The screen host:** drivers never run in the elevated recorder. It starts `PcDiag.Agent.exe screen-host` without administrator rights (through Explorer), on a named pipe only the user and the system can open, and checks the one-time token the host answers with. The host loads the built-in and approved packages, looks for their screens every 2 seconds, opens the ones asked for and shows the newest picture it was sent (older waiting ones are dropped). It reports each screen's firmware and the pictures shown, and writes `screen-host.log`. A host that stops is started again (at most 5 times in 10 minutes).
- **Taken over on demand:** *Show my panels on this screen* on the screen's card pauses the programs the package names as holders (L-Connect's watcher, then its service), written down first in `screen-holders.json`, and waits for each service's own program to end: L-Connect's once took over a minute after its service said it had stopped. Switching off, or no screen in use, starts them again in the opposite order; their start types are never changed. Screens to be used that are missing for a moment (the host starting again, a screen reconnecting) keep them paused; after 30 s missing (unplugged) they are started again. A holder that Windows starts again at sign-in is paused again. A screen still busy is asked for again a moment later (up to 8 times), and afresh as soon as it is free.
- **In the layout:** USB screens are listed after the monitors (*USB screens*, with their state: on, busy and who holds it, available, or the problem), and their panels are made and arranged like a monitor's over the screen's background color. The recorder draws all of a screen's panels into one picture at the screen's size, turns it for how it is mounted (*Turned*), encodes it under the panel's limit and sends it when it changed; *Panels' size* zooms them, *Brightness* sets the backlight.
- **Found live:** the first take-over showed nothing. The host's picture loop ended at its first picture: `Interlocked.Exchange` on an `int?` throws in .NET (only references, primitives and enums), the next picture then met its closed queue and stopped the host, and the host starting again listed no screen for a moment, which started L-Connect again. The loop now keeps an `int`, a screen whose session fails is reported as stopped (and opened again) rather than ending the host, one message the host cannot act on is logged and passed over, and the programs are started again only as above.
- **Verified** by tests: the headers against known answers, a session against a panel that answers like the real one (with 40 answers left unread by another program and one late), packages and their hashes, turning a bar's picture onto the portrait panel, pausing and starting the holders (and pausing one Windows started again), when they are started again, and a screen's picture loop (newest picture, backlight, a failing session reported, closing twice).
- **Verified live** (9 Oct 2026): from a running L-Connect, switching the screen on paused both services, waited for L-Connect to end and opened the screen within a second. A 1920 × 480 panel of rings, thermometers, a graph, the ping and the time showed upright and updated about once a second, every picture acknowledged (208 of 208). Switching off closed the screen and started L-Connect, which took its screen back.
- **Layouts refused when they are not one:** the editor's layout save (`PUT /api/v1/overlay`) now refuses JSON whose top-level names are not a layout's (a whole GET answer sent back, a misspelt name), instead of saving it as an empty layout over the real panels; a scratch script that sent the GET answer back had wiped the panels on the dev PC (restored from its backup).

### Readable percentages and units (Phase 37)
Reported 9 Oct 2026: some percentages still read as bare numbers. The line panel's units were slate (#64748B) on a 72 %-opaque dark panel over the light taskbar, about 1.4:1, so the "%" all but disappeared.

- **A percentage's sign belongs to its number** ("50%"): drawn in the value's color and weight at 70 % of its size, close to it, in the recorder and the editor (its room counted the same way).
- **Labels and units stay readable on the panel:** each keeps at least 4.5:1 against the panel's background and its gradient's second color (`OverlayStyles.Readable`, the editor's `readable`), moved toward the value color (or white or black) only as far as needed, so a design keeps its look. A panel with under 40 % background shows what is behind it, so its colors are kept (its outline is the help there).
- **Verified** by tests that give the same colors on both sides (slate on the dark panel becomes #728196), the sign in the value's color and the unit and label colors readable; drawn by the recorder over a light background, the panels read "79%" and "32%".

### Its value or a percentage for every reading (Phase 36)
Reported 9 Oct 2026 with a snapshot: the *Value only* design's sample read "42" without its unit, as if a percentage would read without its %; and the choice between a reading's value and its percentage could not be found.

- **Why it could not be found:** *Reads as* was offered only for a reading with a total that was not a percentage already, and most readings are percentages (GPU, CPU, VRAM %, RAM %).
- **Now offered for every number**, beside *Shown with* (Value, Percent, Both, Of total). RAM's and VRAM's percentages read as their amounts from the matching source (`OverlayStyles.ValueOf`: memory.usage → memory.used, gpu.*.vram_pct → gpu.*.vram), their warning, critical and full-scale limits turned from % into the value's units (80 % of 24 GB is 19.2 GB), and back; the same in the reading's menu on the screens (`OverlayStyles.ReadAs`). A percentage that is only one (CPU load) says so; a value without a total (watts, a temperature) asks for its *Full at* to read as a percentage.
- **Samples with their unit:** *Value only* and *Trend* show "42%".
- **Captions that do not name buttons:** a caption over buttons or checkboxes is a group, not a label (a label named its first button after itself: *Value* was announced as *Reads as*).
- **Verified** by tests: VRAM's percentage read as its value switches source with its limits converted, the setting stays open, CPU load says it is a percentage only, a power reading asks for its full scale; the recorder's rule for the same.

### The editor shows panels as the screens do (Phase 35)
Reported 9 Oct 2026 with two snapshots: the Gaming HUD (tiles) looked different and was a very different size in the editor's screen than on the screen (659 × 416 there, about 1180 × 350 in the editor at true scale).

- **Measured both ways:** a scratch harness draws the same panels with the same live values with the editor's preview (headless Edge) and with the recorder's renderer, at 150 % and at the editor's screen scale (0.43 on a 4K screen at 150 %): 41 panels (the user's, the 12 designs, every layout in every theme), comparing sizes and pictures.
- **Tiles twice as wide in the editor's screen:** a graph in a tile is an SVG whose own width (100–300 px) does not follow the scale; on the small screen it set every tile column's width (80–214 % too wide). A graph now never widens its tile (inline-size containment), as in the recorder.
- **Line heights:** the editor used Segoe UI's for every font; it now uses each font's own, as Windows reports it (Bahnschrift 1.20, Consolas 1.17, Cascadia Mono 1.16 of the size…).
- **Spaced capitals:** the recorder put hair spaces between the letters, which a monospaced font draws a whole letter wide (Synthwave 18 % wider); it now places the letters one by one with 0.08 em after each, as the editor's letter-spacing.
- **Dials in tiles:** the editor sized a dial by its value's number of characters and kept it at that size; it now measures text in the real font (the browser's canvas text measurement) and lets the dial fill its tile, as the recorder.
- **Colors:** the recorder blended in linear light (GDI+ "high quality" compositing), so every translucent layer (a tile's tint, the sheen, shadow, glow, tints) looked washed out and grey on the screens; it now blends as a browser does, and the colors agree within 2–4 levels.
- **Verified:** after the fixes, 28 of the 41 panels agree within 3 % at both scales and the rest within 6 % at 150 % (the most is a line panel's height); at the editor's screen scale monospaced line panels stay up to 14 % taller, from the one-pixel minimums a 0.43 × drawing keeps (bars, borders, the shadow's margin rounded up). Pictures side by side show the same layout, dials and colors.
- **Verified live** (9 Oct 2026, after deploying): on the editor's screen (Fit, 806 px for the 3840-px screen) the Gaming HUD measured 653 × 425 at true scale against its window's 639 × 416 (it had been about 1180 × 350), the line panel 1096 × 105 against 1096 × 113; on the screen the HUD's rings fill their tiles and its tiles are no longer washed out.

### Motion (Phase 29)
Requested 9 Oct 2026: "enable some motions in the website that make it a masterpiece". Quick and purposeful, never delaying content (nothing builds up when a page opens), and off for users who ask Windows for less motion (`MotionConfig reducedMotion="user"` and the CSS rules).

- **Page changes** (View Transitions, started by the router with the type *page* when the path changes; not when the app opens, nor for a tab or range on the same page): only the page moves. The old one lifts away in 140 ms; the new one rises 14 px into place and comes into focus in 380 ms; the navigation and header stay put.
- **Theme switch:** the new theme grows in a circle from the toggle (a *theme* view transition, its clip-path animated); the toggle's sun turns into a moon.
- **Sliding highlights:** the sidebar's highlight (a pill with an accent edge) and the phone bar's top line slide to the page chosen; every tab list's underline or pill slides to the tab chosen (shared layout springs); a tab's content fades in rising a little.
- **Feedback:** buttons give way when pressed; dialogs spring in over a softened backdrop; menus pop from their button; toasts spring in, slide away and show the time they have left; notices that appear while the app is open slide open (one there as it opens is just shown).
- **Alive:** the brand's heartbeat is drawn when the app opens and a pulse runs along it while recording; the recorder badge's light sends out a ring while recording; the header gains a shadow when the page scrolls under it.
- **Verified** in Edge (9 Oct 2026): filmed page changes, the theme reveal and a tab change; each page change started one *page* transition and the theme one *theme* transition, tabs none.

---

### Driver packages for sensors and other devices (Phase 44, 15 Oct 2026)

Driver packages read sensors as well as drive USB screens: `driver.json` may have **sensors**, either a family with no code (`http-json`, `serial-lines`, `hid-report`) or an `ISensorDriver` in the package's assembly. The format, the limits and examples are in [DRIVER-PACKAGES.md](DRIVER-PACKAGES.md).

- **Folder:** packages live in `%ProgramData%\PcDiagnostic\drivers`, where administrators write and everyone reads; the old `screen-drivers` folder is moved there. Imports are checked in `driver-staging`, which only administrators can open. Both folders' rights are set again each time the recorder starts.
- **Hash:** an approval names the SHA-256 of every file in the package (path and bytes).
- **Import:** `POST /api/v1/drivers/import` (a zip as the body; at most 64 MB). The Web host saves the zip in the user's `driver-uploads` folder, and the recorder (`driver-import`) copies it into staging and checks only that copy (`DriverImport`):
  - at most 256 files and 64 MB, counted as they are unpacked;
  - no file packed more than 200 times smaller;
  - safe relative names only;
  - `driver.json` at the top or in the zip's one folder;
  - not a built-in package's id.

  The checked files are copied (not moved) into `drivers\<id>`, so they take that folder's rights.
- **Approve and remove:** approving (`/api/v1/drivers/{id}/approve`) and removing (`DELETE /api/v1/drivers/{id}`) restart the screen host. The answer waits (up to 10 s) for the screen host to report the change. All of it works only on the PC itself (`LanGuardMiddleware`); the app asks before approving or removing.
- **Screen host:** it loads approved sensor packages and reads each at its `every` (1–60 s), with 5 s per read (`SensorDriverState`). A failed read marks its values unavailable and shows why; after 5 failures in a row the driver is set aside until the recorder starts again. At most 64 values per driver are sent, at most once a second (`Readings` on the pipe).
- **Network and devices:** drivers reach their devices through `ISensorIo` (`PcDiag.Drivers.Io`, used only by the screen host, so the recorder's assembly keeps no network code):
  - HTTP only to this PC or private, link-local or unique-local addresses, checked on the connected address; no redirects, no proxy, 64 KB, 2 s;
  - serial ports (`System.IO.Ports`);
  - HID input reports (`HidDevice`: `CM_Get_Device_Interface_List`, `HidP_GetCaps`, overlapped reads).
- **Recorder:** it keeps only the readings of packages the screen host loaded (`ExternalReadings`). It cleans the device and sensor names into key parts and limits names and units. `ExternalSensorProvider` (priority −100, never fails to start) gives them to the sensors under `ext/<package>/<device>/<sensor>`, so they can never take a built-in sensor's key. They are recorded, shown in History and on the panels (`sensor.ext/...`).
- **App:** *Devices › Drivers* lists every package with what it is for (screens, sensors), its state and problems. It has *Import a package*, *Approve* (asked, naming the publisher and hash), *Withdraw approval* and *Remove* (asked). The Screen editor's *Screen drivers* tab shows the same list.

Checked end to end on the dev PC: an `http-json` package reading a local server was imported, waited for approval, was approved, and its two values came through (changed values within seconds). When the server stopped, the driver showed "Not read" and was set aside after 5 failures.

### Windows drivers for devices that have none (Phase 45, 15 Oct 2026)

`IWindowsDrivers` (Core) is implemented by the recorder's `WindowsDriverService`; the commands are `windows-drivers`, `windows-driver-import`, `windows-driver-install` and `windows-driver-remove`, under `/api/v1/drivers/windows`.

- **Devices:** connected nodes with problem 28, 1 or 18, excluding boot classes (`DeviceTree.Nodes`, which now reads compatible ids too).
- **Import:** reuses `DriverImport.WithUnpacked` (the same copy and checks); the zip must hold one INF with everything beside it (`InfTop`). Packages go to `%ProgramData%\PcDiagnostic\windows-drivers` (administrators only, set each time).
- **Reading the INF:** `InfFile` (Core) reads [Version] (Provider, DriverVer, Class, CatalogFile.<arch>) and the hardware ids of the [Manufacturer] models sections decorated for this architecture. Strings, quotes, comments and continuations are understood. A device matches by its hardware ids, then its compatible ids.
- **Signature:** the catalog is checked with `FileSignatures` (WinVerifyTrust, offline). Only a signed one can be installed.
- **Install:**
  1. `PcConfirm` shows a window on the PC's own desktop (Cancel is the default; 2 minutes, then no).
  2. Optionally, a restore point (WMI `SystemRestore.CreateRestorePoint`, a device driver install); if it fails, nothing is installed.
  3. `DriverInstaller.Install` calls `SetupCopyOEMInf`, then `UpdateDriverForPlugAndPlayDevices` with no flags (never forced).
  4. `installed.json` keeps the `oemNN.inf`.
- **Remove:** confirmed on the PC, then `DiUninstallDriver`, then the package is deleted.
- **Rules:** these commands need the elevated recorder and work only on the PC itself. The Web host waits up to 6 minutes for install and remove (the confirmation, then Windows).

Checked on the dev PC: the list (about 0.5 s; no device without a driver here), and importing a package with an unsigned catalog (read, shown as not signed and not installable, removed). An install on real hardware still needs a device without a driver.

### Recorder CPU (15 Oct 2026)

The recorder writes its CPU use to its log every minute, split by part. The same split, for the last minute, is on *System › Recorder cost* (`recorder-cost` over the pipe, `GET /api/v1/recorder/cost`). There, a USB screen's motion appears once: it is also inside *Moving backgrounds*, so that line is left out. `CostMeter` counts each part's thread CPU cycles: each collector, the sensors, panels, motion, and the USB screens' refresh, motion and encoding. `CostReportService` turns cycles into milliseconds with the process's own cycles-to-time ratio.

Measured on the dev PC (24 logical processors; 3 monitor panels and a Lian Li 8.8″ USB screen with three panels, one with the "stars" motion):

| Change | Recorder's share of the PC |
| --- | --- |
| Before | 0.73–1.04 % |
| The USB screen's still part kept; premultiplied motion layers; the JPEG quality remembered; unchanged pictures not encoded | 0.68–0.73 % |
| Moving backgrounds on a USB screen at 5 pictures a second (`OverlayDevice.MotionFps`, then 1–15; now 1–30, default 30) | 0.61–0.63 % |
| A panel that shows what it showed is not drawn again (same panel, values and scale; no graph, nothing loading), on the monitors and the USB screens; the USB screens' whole and still pictures reused | 0.53–0.58 % (0.45 % with a full-screen game in front, its motion paused) |

What is left, per minute:
- **USB screen:** about 3.7 s (the refresh every second about 1.3 s, motion about 1.15 s, encoding about 1.3 s).
- **Sensors (LibreHardwareMonitor):** about 0.9 s.
- **Other collectors:** about 0.9 s.
- **Panels on the monitors:** about 0.25 s.

The characters' thread sleeps while no character is shown. It keeps Windows' timer at 1 ms only while characters move (it did for as long as the recorder ran).

Helpers started through Explorer (`DesktopShell`) start one at a time, on a single-threaded thread, with a 15 s limit. A launch that hung left the USB screen's helper never started.

## 11. Security & privacy model

- **Local only.** Nothing recorded ever leaves the PC. The Web host connects out only for what the user turns on: the characters' weather (MET Norway's forecast for the place chosen, and OpenStreetMap's place search), problem reports to a report service set in the build, and storage locations the user adds. The recorder, which may run elevated, has no network code: a test asserts that the Agent references no HTTP client or socket assemblies, and Kestrel is configured only in `PcDiag.Web`.
- **Loopback web server hardening (`PcDiag.Web`):**
  - Kestrel binds to `127.0.0.1` only, on a random free port. The port is written to `%LOCALAPPDATA%\PcDiagnostic\web.json`.
  - Tray "Open dashboard" launches the default browser at `http://127.0.0.1:<port>/auth/exchange?t=<one-time token>`. The Agent has no network code, so it cannot ask the Web host for a token: it writes a **launch ticket** file named after the SHA-256 of a random token into the per-user data folder (the token itself is never written), and the Web host redeems it once by atomically renaming the file, accepting only files owned by the user, Administrators or SYSTEM. Tokens are single-use and expire after 2 minutes; the server exchanges one for an `HttpOnly`, `SameSite=Strict` session cookie, then redirects to strip it from the URL. Every API request without the cookie gets a **401**. An elevated Agent never starts the Web host or a browser (they would inherit its rights); it copies the link to the clipboard instead. The Web host refuses to run elevated.
  - `Host` header allowlist (`127.0.0.1:<port>`, `localhost:<port>`) to block DNS rebinding. Mutating requests must carry a matching `Origin`.
  - No CORS. Strict `Content-Security-Policy` (`default-src 'self'`, `script-src 'self'` with no inline scripts), `X-Content-Type-Options`, `Referrer-Policy: no-referrer`, `frame-ancestors 'none'`.
  - **Access from other devices (opt-in):** off by default; Settings → *Access from other devices* (this PC only) writes `Web:Lan` and the host restarts itself in-process (same loopback port, sessions kept). When on, Kestrel also listens on all interfaces on a fixed HTTPS port (default 47443) with a per-install self-signed ECDSA P-256 certificate whose SANs are this PC's names and private addresses; the PFX is stored DPAPI-protected for the user (`lan-certificate.bin`) and kept across address changes (so the companion app's pinned fingerprint stays valid) and renewed only when it nears expiry. Settings shows the addresses and the SHA-256 fingerprint to compare with the browser's certificate warning.
    - Only private-network clients (10/8, 172.16/12, 192.168/16, 169.254/16, 100.64/10, fc00::/7, fe80::/10) are served; everything else gets 403, so a forwarded port does not expose it.
    - **Pairing:** this PC shows a one-time 8-digit code (5 minutes, single use, discarded after 5 wrong attempts; at most 10 attempts per address per minute). The device opens `/pair` (plain HTML form, no script), enters it, and receives a random 256-bit device token in an `HttpOnly; Secure; SameSite=Strict` cookie (90 days, sliding); only its SHA-256 hash is stored in `devices.json` with the device name, last use and address. Paired devices are listed in Settings and can be removed.
    - Paired devices can use the dashboard, control recording, capture and edit incidents. **Only this PC** can change settings (they include the program path the Agent launches and the privacy filters), turn network access on or off, show pairing codes or remove devices; one-time launch links are refused from the network.
    - **Companion app (Android, `android/`, platform APIs only):** a pairing design (QR deep link, UDP discovery, `hello` + `pair` JSON endpoints, token in a header and the WebView cookie, the PC's own dashboard in a WebView) with its gaps closed: HTTPS with the certificate fingerprint pinned (in the QR link and the discovery answer; typed or discovered addresses need the user to confirm the fingerprint), attempt-limited codes, expiring tokens, the token encrypted with an Android Keystore key, and a `X-PCDiag-Device` header that also exempts the app's calls from the Origin check (no page on another site can send a custom header without a CORS preflight, which this host never allows). `GET /api/v1/devices/hello` → `{app, id (install), name, paired, version}`; `POST /api/v1/devices/pair {code, name}` → `{token, device, id}`; `/app/pcdiagnostic.apk` downloads the app. Discovery: UDP 47444, question `PCDIAG?`, answer `{app, id, name, host, port, fp}`, private senders only, only while listening. A home-screen shortcut captures an incident through `POST /api/v1/incidents/capture`.
    - The Host allowlist gains this PC's names and addresses while listening; the Origin check and CSP are unchanged. The installer adds inbound rules for `PcDiag.Web.exe` only (TCP and UDP, private profile, remote addresses limited to the local subnet; an option, on by default), which allow nothing until network access is turned on; without them Windows Firewall asks the first time.
- **The browser is a less trusted client.** All inputs are validated server-side (incident key format, enums, time-range limits, `maxPoints` caps) and nothing from the UI is used as a file path.
- **IPC:** a named pipe with a DACL that allows only the interactive user SID (and SYSTEM). Commands are a fixed, versioned, schema-validated message set, with size limits and no reflection-based deserialization.
  - Implemented in Phase 9: pipe `PcDiagnostic.Agent.<user SID>`; owner = the user (so clients connecting with `PipeOptions.CurrentUserOnly` verify the Agent belongs to them, also when the Agent is elevated), read/write for the user, full control for SYSTEM, **deny** for the NETWORK logon SID. The first instance uses `FirstPipeInstance`, so a pre-created pipe makes the Agent refuse instead of sharing it.
  - Frames: 4-byte little-endian length + UTF-8 JSON, 1 B–64 KB; source-generated `System.Text.Json` (no type names, no polymorphism). Commands: `status`, `start`, `stop`, `pause` (1–1440 min or until resumed), `resume`, `capture` (optional quick type and note ≤ 2000 chars). A malformed frame is answered once and the connection closed; at most 4 clients; idle connections close after 2 minutes.
  - `PcDiag.Agent.exe status|start|stop|pause [min]|resume|capture [type] [note]` is a command-line client for the same pipe.
- **Data at rest:** everything is under `%LOCALAPPDATA%`, so it is protected by the user's profile ACL. Opt-in sensitive fields (command lines, usernames, Wi-Fi SSID) are **off by default**. When on, command lines are passed through a redaction filter that drops tokens that look like `password=`, `token=` or `--key`.
- **Windows events:** when user-name capture is off (the default), user profile folder names, the account name and the computer name are replaced with `<user>`/`<computer>` in event titles, messages and fields (best effort). Rules for events whose text holds personal data (Wi-Fi network names in WLAN 8003, the account in User32 1074) do not store the message at all. `Privacy:CollectWindowsEvents` turns event collection off.
- **Settings explain what each sensitive option collects,** for example "Command lines can contain file paths, URLs and occasionally credentials".
- **Paths:** exports are streamed to the browser, which handles where they're saved. Incident folder names are generated, never taken from input.
- **No arbitrary execution.** Only these programs are ever launched, each with fixed arguments and a fully resolved path: `wpr.exe` from `%SystemRoot%\System32` (Agent, Enhanced mode), `msedge.exe` from its installed location (Web host, headless PDF printing of a generated temp file), `schtasks.exe` from System32 (`/Run /TN "\PC Diagnostic\Recorder"` only, by the Start menu launcher, as the user), and our own `PcDiag.Web.exe` plus our own loopback dashboard URL (handed to Explorer by an elevated Agent, so they run without administrator rights). The installer runs the bundled PawnIO setup, `RecorderTask.ps1`, `netsh advfirewall` and `taskkill`, all with fixed arguments.
- **Kernel driver disclosure.** Settings clearly state that LHM loads a kernel driver for CPU/motherboard sensors, which can be disabled (the result is "Unavailable").

---

### 11b. Installer (Phase 15)
- `installer\build.ps1` → `artifacts\installer\PcDiagnostic-Setup-<version>.exe` (about 77 MB). It publishes the Agent and the Web host **self-contained** for win-x64 into `agent\` and `web\` (separate folders: each carries its own shared frameworks, which must not mix). `Directory.Build.targets` lets the build bundle the **latest .NET runtime patch** from Microsoft's release metadata (`PcDiagRuntimePatch`, e.g. 10.0.12) for all three shared frameworks, whatever SDK patch the build machine has. Inno Setup and the PawnIO setup are pinned by version, URL and SHA-256 and must carry a valid Authenticode signature.
- Setup (`PrivilegesRequired=admin`, x64, Windows 10 2004+) installs to `Program Files\PC Diagnostic`, replaces the application folders on update after stopping the task, the Agent and the Web host, and offers: the recorder task (registered by `RecorderTask.ps1` with the ScheduledTasks cmdlets, for the account that started Setup, found with `ExecAsOriginalUser` + `whoami /user`, so an over-the-shoulder administrator does not get the task instead), PawnIO (when missing or older; `-install -silent`), the firewall rules, and a desktop icon. The Start menu entry runs `PcDiag.Agent.exe open`; *Start PC Diagnostic and open the dashboard* at the end runs it as the original user.
- **Version numbers** (`installeruild.ps1`): every build is numbered major.minor from `Directory.Build.props` and the number of commits as the patch (0.1.0 until 16 Oct 2026, then 0.1.163 and up), passed to the programs (`-p:Version`) and the installer (`AppVersion`, the file name `PcDiagnostic-Setup-<version>.exe`); `-Version` sets one by hand. A build with changes not committed warns that it shares the last commit's number. Raise major.minor in `Directory.Build.props` for a release that deserves it.
- **Installed versions** (`InitializeSetup`): the welcome page and the window title name the version this setup installs. With the same version installed it asks *Repair* (files, task and firewall rules put back, data kept, no data question) or *Uninstall* (runs the installed uninstaller, which asks about drivers and data); with an older one it upgrades, saying so on the data page ("PC Diagnostic X is installed: this upgrades it to Y", Keep by default); with a newer one it installs the older version only when the user confirms. A silent setup repairs, upgrades or replaces without asking.
- **Existing data on install:** a new installation (not an update of this one) that finds `%LOCALAPPDATA%\PcDiagnostic` (from an earlier installation, or the development copy, which uses the same folder) asks what to do: keep using it, start fresh with the old data moved to `PcDiagnostic.backup-<date-time>` beside it, or start fresh and delete it. Data an uninstall kept (marked `kept-at-uninstall.txt`) is used again by default; any other data is set aside by default. `/DATA=keep|fresh|delete` for a silent install (default keep). An upgrade keeps them by default; a repair keeps them without asking.
- Uninstall stops both programs, removes the task (and its folder), the firewall rules and the driver packages in `%ProgramData%\PcDiagnostic` (drivers, driver-staging, screen-drivers, windows-drivers). It then asks whether to also remove the drivers PC Diagnostic installed (default No): PawnIO (through its own quiet uninstaller; other monitoring programs may use it) and the Windows drivers installed from Settings › Devices (`pnputil /delete-driver oemNN.inf /uninstall`, never forced, from `windows-drivers\installed.json`); `/REMOVEDRIVERS` does the same for a silent uninstall. Last it asks whether to delete `%LOCALAPPDATA%\PcDiagnostic` (default No). The license and the trial's record (`%ProgramData%\PcDiagnostic\license`, `HKLM\SOFTWARE\PcDiagnostic\Licensing`) stay, so a reinstall keeps the license and does not start a new trial.
- The application icon (`src\PcDiag.Agent\app.ico`, drawn by `installer\Make-Icon.ps1` from the dashboard's favicon) is used by both executables, Setup and the shortcuts.

### 11a. Local Docker deployment

| Runs in Docker | Stays native on Windows |
|---|---|
| `PcDiag.Web` (dashboard + API), built from the multi-stage `Dockerfile` | `PcDiag.Agent`: collectors need the host's PDH counters, NT process data, DXGI/D3DKMT/NVML, display configuration, event logs and sensor drivers, none of which are visible from a Linux container |
| Cross-platform build and tests (`docker build --target test .`) | Windows-only tests (collectors, Agent) |

- **Container mode:** Kestrel binds to all container interfaces only when `DOTNET_RUNNING_IN_CONTAINER=true`. Compose publishes the port as `127.0.0.1:5180`, so the dashboard stays unreachable from the network. Launch links use `Web:PublicBaseUrl`.
- **Hardening:** non-root user, read-only root filesystem, `cap_drop: ALL`, `no-new-privileges`, and an unauthenticated `/healthz` that returns only `ok` (probed by `PcDiag.Web --healthcheck`, since the image has no curl).
- **Data:** a Linux-native named volume (`pcdiag-data`). Windows bind mounts are avoided because SQLite WAL needs POSIX locking and shared memory.
- **Agent connection from a container:** the named-pipe IPC (Phase 9) is host-local. If the containerized web host needs live data later, the option is an authenticated loopback TCP channel from the Agent. That is out of scope for v1.

## 12. UI plan (React SPA, responsive)

### Pages (static routes)
| Route | Content |
|---|---|
| `/` Dashboard | A large **CAPTURE INCIDENT** button, the quick-type chips (Screen Flicker, Screen Artifact, System Freeze, App Crash, Game Crash, Slowdown, High CPU, High RAM, High GPU, Disk, Network, Other) and the hotkey hint. Below that, a **customizable widget grid** (react-grid-layout): gauges, live charts, single values, recorder controls, top processes, incidents, recent events, sensors, graphics, displays and network adapters. Users drag, resize, add, duplicate and remove widgets and choose what each one includes, or **build their own** from 11 designs fed by any recorded series, hardware sensor or live value (Phase 19); presets and per-breakpoint layouts are stored by the Web host in `dashboard.json` (`GET/PUT/DELETE /api/v1/dashboard`, strictly validated). Live values update at 1 Hz over Server-Sent Events; charts are seeded with the Agent's 10-minute trend. When the recorder is idle, the Agent samples for open dashboards in memory only (*live view*). |
| `/history?range=1h&series=…&t=…` | Ranges 1 m, 5 m, 15 m, 1 h, 6 h, 24 h. Several series can be overlaid with independent Y axes (for example GPU util + temp + core clock + VRAM), with a shared crosshair and event/anomaly markers on the time axis. The state is kept in the URL, so views can be shared and reloaded. |
| `/incidents` | A filterable list (type, source, date) with the headline numbers for each incident. |
| `/incidents/INC-…?t=…` | Follows the layout in PROJECT.md §17. The timeline is the primary element. **Clicking any timeline item or event recenters all charts on that timestamp (±30 s), highlights the matching process/event rows and updates `?t=`.** Also has findings (grouped by evidence level), processes, Windows events, drivers, display, notes, a user type selector, and export. |
| `/system?tab=` | Tabs: Overview (the PC, what needs attention, a spec sheet), Hardware, Motherboard (board, chipset, firmware and security, memory and expansion slots, the monitoring chip's temperatures, fans and voltages judged against their limits), Devices (expected hardware: remember this setup and what is missing since when (Phase 28); the device register: connected now and before, problems, recent changes), Drivers, Sensors, Readings (each unavailable reading named by device: unavailable with what to do, does not apply, or removed device) and Recorder cost. |
| `/tuneup?tab=` | Advice, Processes, Services, Startup, Clean up, System (Phase 20). Every action is confirmed first. |
| `/on-screen` | Readings on the screens (Phase 23, redesigned in Phase 25, Phase 26): the monitors as monitor shapes, each with its own panels; drag, snap, resize; readings shown with any of 12 designs (dials, graphs, bars, text) and icons, as values, percentages or of their totals, in rows (1–4 columns), tiles (a grid designer with spans) or a line; automatic or fixed sizes; opacity; values in a fixed space, on the left, in the middle or on the right (Phase 34); a picture and a pattern behind them (Phase 32); 24 themes previewed on the panel, six of them with moving backgrounds (Phase 42), and every part of the look adjustable; templates and 24 designed panels, gaming readings among them (Phase 27, Phase 40); arranging on the screens themselves (Phase 31); USB screens (a Lian Li 8.8″ and others through driver packages) with their own panels, switched on and off, turned, zoomed and lit, and their drivers approved (Phase 38); 12 whole-screen designs for 11 screen sizes, fitted to the screen and undone (Phase 39); a control box (fill, grid cell, fit, restore), arranging all into a grid, a layout for upright, designs marked by whether they fit, panels movable, resizable and closable on the screens (Phase 41); backdrops behind each tile fitted to it, 64 whole-screen designs for 20 sizes wide and upright and 45 panels (Phase 46); designs with pictures drawn around their readings, 26 for screens and 8 panels, listed first, and previews in *Add a panel* (Phase 47); moving scenes generated for a panel with frosted glass under its readings, a picture library of uploads (stills, animated pictures, short videos) kept in storage, glass over any picture, 31 scene designs for screens and 8 panels (Phase 48); 20 scenes in a compact list, animated characters playing on, in or around panels with their place, movement, size and speed set (Phase 49); a redrawn kitten and puppy, and characters imported from animations (GIF, WebP, APNG, video, PNG frames, sprite sheets) (Phase 50); characters reacting to readings by kind and status, with effects (Phase 51); all panels in a table. |
| `/storage` | The storage manager (Phase 24): space against the limit and on the drive, what uses it, incidents by size (delete, pin), recent clean-ups, the rules; where files are kept (This PC, folders, S3-compatible services) and the layout's backups (Phase 43). |
| `/settings` | Topics in a list with search and unsaved marks: Recording, Automatic incidents, Enhanced mode; Privacy, Windows events, Storage; Sampling, Recorder app, Other devices. One setting per row with what it does. |
| `/diagnostics?job=` | Period diagnostics as background jobs: progress, cancel, recent runs, the report. |

Values that aren't available show as "Unavailable" with a tooltip explaining why.

### UI v2 (the default look)

UI v2 (the PC Diagnostic UI v2 plan, 15 Oct 2026) moved the dashboard over in five phases. **Phase 5 (15 Oct 2026): the new look is the only look.** The old one (v1) and the switch between them were removed. The tokens in `index.css` are the design system's values; Comfortable/Compact density remains, per browser. **Phase 1, Foundation:**
- **Tokens:** the existing token names take (`background`, `surface`, `surface-muted`, `border`, `foreground`, `muted`, `accent`, `success`, `warning`, `danger`, `unavailable`) the design system's values, light and dark. It also adds `--shadow-float`, and Inter and JetBrains Mono, bundled with the app, so nothing is fetched. Values use tabular figures. The values come from the *PC Diagnostic v2* design system in Claude Design.
- **Shell (`ShellV2`):** the sidebar in four areas (Monitor, On screen, Devices, Settings; the page shown marked by a sliding fill) and a header with search, the recorder's state, problem reports and the theme.
- **Search (Ctrl+K, `CommandPalette`):** finds pages, settings topics and actions. The settings topics live in `features/settings/sections.ts`, shared with the Settings page.

**Phase 2, the On-screen editor (`OverlayPage`):**
- **Fits the window:** at 1280 px and wider the page does not scroll; the inspector scrolls in its own column beside the screen, so the screen stays in view.
- **The screen:** fitted to the room it has (`ScreenEditor` `fitHeight`), with the screens as a strip of choices above it.
- **Tabs in the header** (moving between them is animated): Editor, Screens (the map of the monitors and USB screens, with each USB screen's state, and screens not connected, whose panels can be moved or deleted), All panels, Options, Screen drivers.
- **Inspector:** slides in for each panel or screen chosen.
- **Undo and redo:** `undo.ts`, also in v1. Ctrl+Z and Ctrl+Y, or Ctrl+Shift+Z; text fields keep their own undo. Changes within 0.6 s are one step (a drag, a slider); 100 steps are kept. Discard can be undone; a layout loaded from the screens starts afresh.
- **Save bar:** floats while something is unsaved. It shows how many panels and settings changed (`changedCount`), with undo, redo, Discard and Save.

**Phase 3, Characters and the generator:** two pages in the sidebar's *On screen* area, in the new look only (v1 reaches both from the On-screen page).
- **Characters (`/characters`, `CharactersPage`):** the library as cards that move when pointed at. The chosen character's detail shows its animations to play, where it is used (`placesOf`), *Put on* the chosen screen, and Delete (for an imported one not in use). Beside them, each screen's own characters with placement, movement and reactions (`CharactersTab`), saved with the save bar.
- **Generator (`/generator`, `GeneratorPage`):** a new character asked for and every character being made, with its stages, beside the generator's setup and its service (start, stop). The Screen editor's generator tab is gone; `showGeneratorSection` opens this page in the new look.
- **Click a character to edit it:** clicking a character on the editor's screen opens its settings (`onCharacter`, in both looks). A panel's character opens the panel's Characters tab at it; a screen's own opens the screen's.

**Phase 4, Dashboard, Incidents and Settings:** every page now has its new-look version.
- **Dashboard:** already a grid of cards that can be rearranged by dragging; it takes the new look's tokens as it is.
- **Incidents:** in the new look, an incident clicked opens in a drawer over the list (`Drawer`, `components/ui/drawer.tsx`). The drawer shows its state, ID, time, how much was recorded, what captured it, its reports, whether it is pinned and its notes, with *Investigate* into its timeline.
- **Settings:** in the new look, one topic at a time. Choosing a topic in the list, or following a link to it (`#topic`), shows it with no scrolling to it and no scroll-spy; the list and the topic each scroll by themselves.
- **Density:** Comfortable or Compact, per browser (`pcdiag.density`), from the header's density button or Ctrl+K. Compact makes every rem-based size about 12 % smaller (`html.compact`).

### Responsive behaviour
- The dashboard grid goes from 1 column on phones to 2 on tablets and 3 on desktops.
- On desktop, the Capture button and quick-type chips are a sticky header. On narrow screens they become a bottom action bar.
- On desktop, the incident timeline is a horizontal band above the charts. On narrow screens it's a vertical list, and tapping an item scrolls to the charts.
- Charts always fill the available width and resize with the container. On mobile, the range and overlay controls move into a drawer.
- Dark theme by default, light theme supported. The app is keyboard-accessible, and meets WCAG AA contrast (checked in Playwright with axe).
- A PWA manifest lets the user install it as an app-style window. (A WebView2 shell is an optional future addition.)

### API surface (`/api/v1`, typed via OpenAPI)
| Area | Endpoints |
|---|---|
| Live | `GET /status`, `GET /live` (latest frame), `GET /processes/top?by=cpu|ram|gpu|io` |
| History | `GET /history/catalog[?sensors=true]` (chartable series, per device with its stable key and last sighting; with every hardware sensor as `sensor.{id}` on request), `GET /history/series?ids=&from=&to=&points=` (min/avg/max buckets: raw within the retention, `Rollup1m` beyond it), `GET /history/events?from=&to=&min=` (the incident timeline queries run on the live database) |
| Storage | `GET /storage` (sizes by kind and per incident, the drive, the limits, how far the recording goes back, the recorder's condition and recent clean-ups), `POST /storage/clean` (`storage-clean`: the limits now, then compacting), `POST /storage/incidents/delete` (up to 500, each by the recorder); `GET/POST /storage/locations`, `POST /storage/locations/test`, `/order`, `/sync`, `PATCH/DELETE /storage/locations/{id}`, `GET /storage/files/{kind}` (Phase 43). Changes only on the PC itself. |
| Incidents | `GET /incidents?type=&source=&from=&to=&q=`, `POST /incidents/capture`, `GET/PATCH/DELETE /incidents/{key}` (edits and deletes go through the Agent pipe: `incident-update`, `incident-delete`), sub-resources `/timeline`, `/catalog`, `/series`, `/processes?t=`, `/findings`, `/system` (drivers, display, inventory at the time) |
| System | `GET /system` (inventory, devices, drivers, display, sensor availability, latest sensor values and the device register `ioDevices`), `GET /system/sensors?prefix=` (newest sensor values only), `GET /system/device-events?limit=` (connections, drop-outs, failures) |
| Control | `GET /control/processes`, `GET /control/services`, `POST /control/processes/{pid}/end` and `/priority`, `POST /control/services/{name}/{start|stop|restart|start-type}`; `GET /tuneup` (scan), `POST /tuneup/{action}` (clean, end-processes, stop-services, power-plan, hibernation, game-mode, gpu-scheduling, free-standby, flush-dns, shutdown-wsl, startup). Local only. |
| On-screen readings | `GET /overlay` (the layout, the recorder's monitors and values, its themes and the layout's revision), `GET /overlay/state` (monitors and values now, whether arranging, the layout's revision; sampling and the frame session keep going while the editor asks), `PUT /overlay` (validated, written to overlay.json, applied at once; unused pictures removed), `POST /overlay/identify`, `POST /overlay/arrange` (`{ on }`: arranging on the screens), `GET /overlay/images` (the picture library: here and in storage), `POST /overlay/images?name=&generated=` (a PNG or JPEG, at most 8 MB, or an animation, at most 48 MB: its id, size and frames), `GET /overlay/images/{id}`, `POST /overlay/images/{id}/fetch` (from storage), `DELETE /overlay/images/{id}` (409 while in use). Changes only on the PC itself. |
| Expected hardware | `GET /hardware` (the recorder's last check: what is here now, what is missing since when, what was added; the remembered setup), `PUT /hardware/expected` (`{ items, incidentOnMissing }`, validated, then checked at once), `DELETE /hardware/expected`, `POST /hardware/check`. Changes only on the PC itself. |
| Problem reports | `GET /reports`, `GET /reports/{id}`, `GET /reports/{id}/download`, `POST /reports/preview`, `POST /reports`, `POST /reports/{id}/send`, `DELETE /reports/{id}`, `PUT /reports/consent`, `POST /reports/client-error`. Local only. |
| Export | `GET /incidents/{key}/export?format=zip|html|pdf|csv|json` → file download |
| Config | `GET/PUT /settings` (validated with the Agent's option validators, written to settings.json keeping other sections; `triggers` holds the rule overrides), `GET /triggers` (the rules: shipped values, ranges, units and the rule in plain words with the saved changes applied), `GET/PUT/DELETE /dashboard` |
| Anomalies | `GET /anomalies?from=&to=&limit=` (telemetry.db, overlapping the range, newest first), `GET /incidents/{key}/anomalies` |
| Diagnostics | `POST /diagnostics/jobs {from, to, fresh}`, `GET /diagnostics/jobs`, `GET/DELETE /diagnostics/jobs/{id}`, `GET /diagnostics/jobs/{id}/export?format=html|pdf|json`; the direct `GET /diagnostics/period` remains |
| Recorder | `GET /recorder/overhead?minutes=1..1440` (the recorder's own CPU, memory and sampling time over the period), `POST /recorder/start` (`{ selection }` for a custom recording), `GET /recorder/catalog` (what a custom recording can select) |
| SSE `GET /api/v1/live/stream` | `live` event at 1 Hz: Agent connection, recorder status (including live view) and the live snapshot (headline metrics, top processes, recent events, displays, recorder overhead). `GET /api/v1/live` returns one snapshot; `GET /api/v1/live/trend?minutes=1..10` returns the columnar trend that seeds the charts. Since Phase 10 each snapshot carries `anomalies`: running anomalies and those that ended in the last 10 minutes, with their reasoning and incident link. |

---

## 13. Testing strategy

| Layer | Approach |
|---|---|
| Unit | Anomaly rules, ring buffer, incident state machine, ID generation, finding wording guard. All use `FakeTimeProvider` (Microsoft.Extensions.TimeProvider.Testing). |
| Scenario replay | Scripted `.scenario.json` files, for example "GPU util ramps to 99 % at t=40 s, clock drops at t=44 s, event 4101 at t=46 s, user marks ScreenFlicker at t=48 s". Fake collectors play them back, and we assert the expected anomalies, incident, timeline order and findings. This covers every scenario in PROJECT.md §29 that can't be produced safely on a real PC: TDR, multiple GPUs/monitors, AMD/Intel/NVIDIA variants, missing sensors, permission denied, service unavailable, laptop on battery. |
| Integration | Collectors run against the real machine and must never throw. Every value is either Available or has a reason. Storage tests run on temp DBs: batching, retention, incident slice copy, concurrent reader. |
| Web API | `WebApplicationFactory` with a fake Agent: every endpoint, the auth flow (missing cookie → 401, bad Host → 400, bad Origin on POST → 403, token single-use), input validation limits, and export content. |
| Front end | Vitest + React Testing Library for components (Unavailable rendering, timeline → chart recenter, evidence-level grouping). **Playwright** e2e against the Web host with a fake Agent replaying scenarios, run at desktop and mobile viewports, with an axe accessibility check. |
| Real-load (manual/CI-optional) | StressKit (`tools\PcDiag.StressKit`): `cpu`, `memory` (refuses to leave under 0.5 GB), `disk` (write-through and random I/O on a temporary file), `hang` (a window that stops responding), `crash` (unhandled exception: Application Error 1000). Display-mode flips and adapter disables stay manual. |
| Soak | 72 h run with overhead assertions: CPU < 2 % avg, stable working set (no growth trend), DB size within cap. Postponed to the end of the project (7 Oct 2026) so it runs on the finished build; `soak_report.py` evaluates it from the recorder's one-minute summaries, including the recorder's working set. A first 1.4 h run: 100 % of minutes recorded, CPU 0.15 % on average, working set falling from 236 to 215 MB. |
| Fault injection | A collector that throws, times out, or returns garbage must never stop the scheduler. The DB is locked or the disk is full: write-behind retries, then drops with a logged warning, and the ring buffer is unaffected. |

---

## 14. Development roadmap (revised)

Each phase delivers: an explanation, a file list, complete compilable code, dependencies, build instructions, tests, and known limitations. I stop for your review after each phase.

| Phase | Deliverable | Verifiable outcome |
|---|---|---|
| **1** ✅ | Solution skeleton, `Directory.Build.props`, central packages, Core models and interfaces, `Availability` model, options classes, DI host for the Agent, Serilog, SQLite schema + migrations, test projects. **`web/` scaffold** (Vite + React + TypeScript + TanStack Router + Tailwind + shadcn/ui) and a minimal `PcDiag.Web` with the MSBuild integration and loopback auth | `dotnet build` and `dotnet test` pass; `npm run build` produces `web/dist`; the Agent starts, creates the DBs and logs; `PcDiag.Web` serves a placeholder dashboard on 127.0.0.1, and a request without the token cookie gets a 401 |
| **2** ✅ | TickScheduler, SampleBus, **StorageWriter (batched)**, PDH wrapper, **CPU + Memory collectors**, self-monitoring, and a minimal **RecordingController** (Manual/Always/Scheduled, sessions, crash-closed sessions) pulled forward from Phase 8 so recording works before the IPC exists | 1 s CPU/RAM rows in `telemetry.db`; measured Agent overhead is 0.19 % average CPU and about 64 MB |
| **3** ✅ | GPU: PDH engines (per adapter/process), DXGI inventory, D3DKMT perf data (verified by spike), NVML provider, `IGpuDetailProvider` merge by priority, schema v2 | Both the 4090 and the Intel iGPU are reported; iGPU sensors show Unsupported; overhead 0.15 % CPU. Per-process GPU usage is collected per tick and persisted with process snapshots in Phase 5. |
| **4** ✅ | Disk (PDH + volume mapping via PDH instance names + IOCTL identity + WMI health + volume free space) and Network (GetIfEntry2 via .NET, NDIS filter layers excluded via GetIfTable2 flags, change notifications, WLAN RSSI), schema v3 | 2 NVMe disks and 3 interfaces reported on the dev PC; a 2 GB write shows 1562 MB/s on the right disk; headline throughput matches Windows; overhead 0.18 % CPU. See [network spike](spikes/2026-09-29-network-interfaces.md). |
| **5** ✅ | Processes (NT bulk query with layout verified by tests, identity, top-N with activity thresholds, lifecycle diff, IsHungAppWindow with DWM ghost windows excluded, prioritized enrichment, command-line redaction, GPU join) + Services (EnumServicesStatusEx polling with diff, start types, restarts). Signature checks followed in Phase 15 (embedded and catalog). | Started/Stopped/Hung/Recovered verified with real processes; 0.15 % CPU. See [process spike](spikes/2026-09-29-process-information.md). |
| **6** ✅ | Hardware sensors: `IHardwareSensorProvider` merge layer (priority, per-provider intervals, shared logical keys and aliases), LHM provider (prerequisite-aware, GPU updates every 5 s, SMART every 30 s), storage reliability provider, deadband sensor storage, hardware/OS/driver inventory (WMI, 6 h, stored on change) | Not elevated: CPU temperature/power are `RequiresElevation` (no fake zeros); GPU hotspot 55 °C, the AIO pump/liquid and GPU fans recorded; 281 drivers inventoried. Elevated + PawnIO path **not yet verified on hardware**. Later (7 Oct 2026): elevated with PawnIO, updating the Core Ultra 9 285K's sensors takes 400–500 ms, which made sensor ticks average about 300 ms and run late; LHM is now read on its own background thread about once a second and the collector takes the latest readings without waiting. |
| **7** ✅ | Windows event engine: `event-rules.json` (44 rules, user overrides), structured XPath queries with Suppress, `EventLogWatcher` subscriptions + backfill (30 min, and to the last boot for boot-critical rules), de-duplication, storm guard, user-name redaction, events in frames, schema v4 with crash/service links; network link changes recorded as events | 26 real events from the dev PC (personal data replaced) replay to the expected rules, noise is rejected; on 30 days of real history the Windows-side selection and the classifier agree (563 selected, 39 dropped only by field conditions); a live test event arrives through the subscription; a bug check before a late recording start is recorded through the since-boot read-back; overhead unchanged (0.16 % CPU) |
| **7b** ✅ | **Display subsystem**: QueryDisplayConfig model (SDK-verified layouts), hidden-window notifications (display change, monitor arrival/removal, display power, console/Remote Desktop switches), diffing with settle re-read, Reconfigured detection, remote-session handling, schema v5 | Scripted notification scenarios pass (refresh change dated to the notification, sub-second unplug/replug, slow mode set, power, session switch). Live on the dev PC over Remote Desktop: a disconnect/reconnect produced power off/on, Disconnected/Connected and SessionChanged within 0.1 s of each other, plus one Reconfigured; overhead 0.09 % CPU. A **physical** refresh-rate change could not be made over Remote Desktop and is still to be verified at the console. |
| **8** ✅ | Rolling recorder: T0 ring (pre-allocated, time + size bounded), 1-minute rollups, retention with time-sliced deletes and incremental vacuum, heartbeat, **boot-recovery detection** (bug check / unexpected shutdown / normal restart / recorder stopped, Fast Startup aware), clean stop on Windows session end, **pause** (duration or until resumed), sleep/wake session handling, schema v6 + incidents v2. Tray and IPC wiring moves to Phase 9 with the IPC server. | Scripted: pause/resume, sleep/wake, session end, schedule windows, retention and rollups on real SQLite. Real event log: the 6 Oct bug check 0x50 is classified as BugCheck with its dump file. Live: a killed Agent is detected on restart as "recorder stopped; Windows kept running" and stored; rollups written every minute; 0.15 % CPU. |
| **9** ✅ | Incident engine: lifecycle, capture to `incident.db` (pre-window saved before returning, all processes from the ring, fresh snapshots, late events, timeline, single-file finish), merge, temporary recording when idle, unfinished incidents finished as Partial at startup, boot recoveries become incidents; secured **IPC pipe** + command-line client; Windows Forms tray (capture, quick types, record/pause/resume, exit, state icon, notifications) and global hotkey (Ctrl+Alt+Shift+I, configurable); the Agent is a windowed app that attaches to a parent console for logs. Cooldown for automatic triggers comes with them in Phase 10. | Live on the dev PC: pressing the hotkey created INC-20261006-00001 with 60 s before and 30 s after (90 samples, 583 processes, services and display snapshots, 248 drivers, 144 timeline entries) and completed it 40 s later; a tray capture completed too; `PcDiag.Agent.exe status` answered over the pipe; Exit from the tray stopped cleanly (session reason Shutdown, heartbeat clean). |
| **10** ✅ | Anomaly engine + trigger framework + diagnostic analyzers + wording guard | Scenario tests pass |
| **11** ✅ | `PcDiag.Web` API + live SSE stream + Agent pipe client; recorder controls and capture from the browser; interactive dashboard (ECharts gauges and charts, Motion effects) with a customizable widget grid stored by the Web host; in-memory live view while idle; "Open dashboard" in tray via launch tickets | Dashboard updates at 1 Hz; Capture from the browser creates an incident; layout works at 375 px and 1920 px |
| **12** ✅ | History charts with overlays (ECharts, server-side decimation + LTTB) + Incident list + Investigation page (timeline-driven, deep-linkable) + System page + Settings | Playwright e2e on replay scenarios passes |
| **13** ✅ | Reports: Quick, Technical; HTML/PDF (headless Edge)/CSV/JSON/ZIP (the exact ZIP layout from PROJECT.md §18), downloaded through the browser | Done: `GET /api/v1/incidents/{key}/export?format=html|pdf|json|csv|zip&report=quick|technical&file=metrics|processes|services|events`. Reports are one self-contained HTML file (inline CSS and SVG charts, no script, an in-file CSP that forbids loading anything, every value HTML-encoded) with the diagnostic summary last (§19); `summary.html` in the ZIP is the technical report. PDF: Edge `--headless --print-to-pdf` from its installed path, fixed arguments, a throwaway profile in a private temp folder that is deleted afterwards, 60 s timeout; 501 with a fallback hint when Edge is missing. CSV streams from SQLite (UTF-8 with BOM, RFC 4180, formula-injection prefix); the ZIP is built in a delete-on-close temp file. |
| **14** ✅ | Performance pass + optional **Enhanced mode** (ETW per-process disk/net, WPR flight recorder `.etl`), see §10 *Enhanced mode* | Self-monitoring without a `Process` refresh per tick, non-concurrent GC with `GCConserveMemory=5`, `GET /recorder/overhead` and the System page card. Measured before the pass on the elevated dev PC with LibreHardwareMonitor: 0.18–0.19 % average CPU, 95th percentile 0.40–0.50 %, about 40 ms per sample, working set 200–240 MB, within the 2 % budget. Enhanced mode: WPR flight recorder saved to `traces/incident.etl` (ZIP export), Kernel-Network/Kernel-Disk true per-process I/O (schema v8: Network list, incident Processes tab, technical report, processes.csv); tests cover the native layouts, the no-administrator path, the collector's rates and the service's start/stop/save/retry rules. Live with the elevated recorder (6 Oct 2026, after replacing the first design; see §10): Enhanced mode starts within 3 s of recording; per-process disk matches the physical disk (StressKit write-through: 749 MB/s attributed, 605 MB/s on the drive in a slightly different window); network per process; flight-recorder traces of 84 and 97 MB saved in 19 and 27 s, one of them for a real automatic incident (an NVIDIA display driver error, Event 153) captured while it ran; recorder 0.23 % CPU and about 257 MB working set with Enhanced mode on. Side by side without elevation, the pass cut sampling time from 35 to 28 ms and CPU from 0.122 to 0.112 %, but not memory (192 vs 200 MB working set): reducing the Agent's memory stays open. |
| **15** ✅ | StressKit, installer (scheduled task registration, optional logon autostart, PawnIO, firewall, uninstall cleanup), third-party notices, signing guidance, Start menu launcher and de-elevated dashboard opening, see §11b | `installer\build.ps1` builds a 77 MB setup with .NET 10.0.12 bundled; the license gate passes (93 components; MPL-2.0 only for LHM and its libraries, GPL-2.0 only for the separate, unmodified PawnIO setup); the task definition was built and checked without registering it; the self-contained Web host and command-line client start on their own; a program started through Explorer runs at Medium integrity. Run end to end on the dev PC (7 Oct 2026): silent install in 14 s (658 files, 258 MB), task registered for the user (Interactive, Highest), both firewall rules (private, local subnet), Start menu entry; the Start menu launcher started the recorder through the task (elevated, no prompt), started the dashboard host without administrator rights and opened the browser within 7 s; CPU package temperature and power read (64 °C, 92 W); silent uninstall removed the program, task, rules, shortcut and Apps entry and kept the recorded data. Still to do on a clean PC: PawnIO's own install (it was already present) and a machine without .NET. |
| **16** | **Custom recording** (requested 7 Oct 2026): record only chosen sources and data; see §10 *Custom recording* | Record everything or a custom selection from the recorder card; each scheduled window records everything or its own selection; incidents keep their selection and the analysis makes no claim about what was not recorded. Built, tested and deployed to the dev PC 7 Oct 2026. |
| **17** | **Period diagnostic** (requested 7 Oct 2026): a deep diagnostic of any recorded period, aspect by aspect; see §10 *Period diagnostic* | Choose a period in History or on the Diagnostics page; the report shows every issue and abnormal behaviour by section and severity, links each finding to History and incidents, and downloads as HTML, PDF or JSON. Built, tested and deployed to the dev PC 8 Oct 2026. |
| **18** | **Commercial licensing** (8 Oct 2026): removed when the project was published as open source (GPL-3.0); see §10 *Licensing removed* | — |
| **19** | **Usability round** (requested 8 Oct 2026): background diagnostics, grouping and sorting in every table, widgets you build, System and Settings redesign, more readings (NVMe health log, compressed memory, iGPU power); see §10 *Usability round* | Leaving the Diagnostics page keeps the run and shows its result on return; every table groups and sorts by drag and drop; a widget built from any value appears with live data and is kept in My widgets; on the dev PC the readings that do not apply are set apart and the NVMe drives report temperature, wear and power-on hours. |
| **20** | **Control and tune-up** (requested 8 Oct 2026): end processes, control services, startup, clean-up, power and memory settings, advice; see §10 *Control and tune-up* | Every action runs in the elevated Agent after the user confirms, never on a protected process or a reused PID, and is recorded as an event; the scan lists what can be cleaned with its size. Built, tested and deployed to the dev PC 8 Oct 2026. |
| **21** | **Problem reports** (requested 8 Oct 2026): reports written by the user, and with consent the errors and crashes caught automatically, sent to the makers' report service; see §10 *Problem reports* | The preview shows exactly what is sent and contains no user or computer name; without consent nothing leaves the PC; a repeated problem adds to its work item. Built and tested 8 Oct 2026; the service is hosted at release. |
| **22** | **Motherboard and devices** (requested 8 Oct 2026): board facts and monitoring, the device register and device monitoring; see §10 *Motherboard and devices* | The Motherboard tab judges the board's rails, battery, temperatures and fans; the Devices tab lists each physical device once, connected now or before, with its parts, driver and dates; connecting, removing or a device failing while recording is an event, and a device that keeps dropping out is a warning. Built, tested and deployed to the dev PC 8 Oct 2026. |
| **23** | **On-screen readings** (requested 9 Oct 2026): panels of readings on each screen, arranged on monitor shapes; see §10 *On-screen readings* | Each screen shows its own panels on top of everything, click-through, at its scaling, with live values, limits and the chosen look; the page shows the screens as arranged and every change appears at once; panels of a disconnected screen come back with it. Built, tested and deployed to the dev PC 9 Oct 2026. |
| **24** | **Storage manager** (requested 9 Oct 2026): a space limit, free space kept on the drive, writing paused when it is almost full, and the Storage page; see §10 *Storage manager* | The recorder keeps its data within the limits, giving back what can be recorded again before any incident, never removing pinned incidents, and stops writing before the drive fills; the page shows what uses the space and cleans up. |
| **25** | **On-screen readings redesign** (requested 9 Oct 2026): tiles, rings, graphs, icons, gradients, glow, shadow and nine themes, all adjustable; see §10 *On-screen readings › Redesign* | Every theme draws in every layout on the screens as the editor previews it; every part of the look is a setting. |
| **26** | **Live chart, dashboard load and on-screen panels, round 2** (reported 9 Oct 2026): steady sizes, fixed sizes, opacity, value modes, the grid designer and the 12 widget designs; see §10 | The live chart's old points stay put; the dashboard shows at once; panels keep their size while values change; every design draws on the screens as previewed. Built, tested and deployed to the dev PC 9 Oct 2026. |
| **27** | **Gaming readings** (requested 9 Oct 2026): frame rate, frame time, 1 % low, the game, ping; see §10 | FPS and frame time agree for the app in front; pings read below a millisecond on the LAN. Built, tested and deployed to the dev PC 9 Oct 2026. |
| **28** | **Expected hardware** (requested 9 Oct 2026): remember this PC's setup, tell what goes missing; see §10 | A remembered monitor or drive that is not connected shows missing within 20 s on every page, optionally as an incident. Built, tested and deployed to the dev PC 9 Oct 2026. |
| **29** | **Motion** (requested 9 Oct 2026): page transitions, theme reveal, sliding highlights, springs, live indicators; see §10 | Page changes, the theme switch and tabs move as described and not at all with reduced motion; nothing delays content. Built, tested and deployed to the dev PC 9 Oct 2026. |
| **30** | **Round 3** (reported 9 Oct 2026): the dashboard's measured width, gauges' number sizes, panels' widths at start and at full scale, the editor's true-scale zoom, the grid toolbar, ping always on; see §10 | The dashboard's widgets appear in place; no number changes size as the reading changes; a panel on the screen has the size the editor shows. |
| **31** | **Arranging on the screens** (requested 9 Oct 2026): move, resize, hide panels and reorder, restyle and remove readings on the screens themselves; see §10 | Every change made on the screens is in the editor, and every one is checked like the editor's. |
| **32** | **Pictures, patterns and designed panels** (requested 9 Oct 2026); see §10 | A picture and a pattern draw the same in the editor and on the screens; every design builds from this PC's values. |
| **33** | **Frame rates for every graphics API** (requested 9 Oct 2026): OpenGL and Vulkan from the graphics kernel; see §10 | A test app presenting at 100 fps with Direct3D 11, Direct3D 9, OpenGL or Vulkan reads 100 fps. |
| **34** | **Panels keep their size whatever the values** (reported 9 Oct 2026): every value a fixed space it sits in (left, middle, right); see §10 | A panel of frame rates, pings, temperatures, rates, sizes, names and dates keeps one size, and every reading its place, as the values run over their ranges. |
| **35** | **The editor shows panels as the screens do** (reported 9 Oct 2026); see §10 | The same panels drawn by the editor and the recorder agree in size, spacing, dials and colors, at 150 % and at the editor's screen scale. |
| **36** | **Its value or a percentage for every reading** (reported 9 Oct 2026); see §10 | Every number offers *Reads as*; RAM and VRAM read as their amount or percentage, limits following. |
| **37** | **Readable percentages and units** (reported 9 Oct 2026); see §10 | "%" reads with its number; labels and units keep 4.5:1 on the panel. |
| **38** | **USB screens with external drivers** (requested 9 Oct 2026): the Lian Li Universal Screen 8.8" live, taken over from L-Connect on demand; see §10 | The 8.8″ shows its panel upright, updating every second, while switched on; L-Connect has it back when switched off. |
| **39** | **Screen sizes and full-screen designs** (requested 9 Oct 2026): 11 sizes, 12 whole-screen designs fitted to the screen; see §10 | Each design fills its screen exactly, in the editor and on the screen, at its own size and others. |
| **40** | **Twelve more designed panels** (requested 9 Oct 2026): 24 in all; see §10 | Every design is accepted by the recorder and drawn the same by the editor and the recorder. |
| **42** | **More themes and moving backgrounds** (10 Oct 2026): 24 themes, compact previews, six motions, six moving designs; see §10 | Moving panels move on the screens (still when hidden, locked, in games…) at about 2 ms a frame. |
| **41** | **Panels on the screens, the editor's controls and orientation** (10 Oct 2026): above the taskbar, the editor regrouped, a control box, designs for the screen, portrait layouts, handles on hover; see §10 | Clicking the taskbar under a panel leaves it in front; a panel with handles is moved, resized and closed on the screen while a click on its body reaches what is under it. |
| **43** | **Where files are kept** (10 Oct 2026): This PC, folders and S3-compatible services, what each keeps, layout backups; see §10 | A backup made before a save is in every location that keeps backups and brings the layout back; a location added gets what it is missing. |
| **46** | **Designs for every screen, upright and wide, with fitted backdrops** (10 Oct 2026): eight backdrops, upright and monitor sizes, 50 screen designs and 17 panels; see §10 | Every new design fits its screen with no tile cut off, and the recorder draws it as the editor does. |
| **47** | **Designs with their own pictures** (10 Oct 2026): art drawn from where the readings are, 26 screen designs and 8 panels, previews in Add a panel; see §10 | A design's picture lines up with its dials, graphs and bars in the editor and on the screen, also after a resize. |
| **49** | **Characters, PC Diagnostic Storage, more scenes** (10 Oct 2026): 35 animated characters playing around panels, SeaweedFS on this PC as the default storage location, 12 more scenes, panels over each other stacked without flicker; see §10 | A character moves alike in the editor and on the screen; files are kept in PC Diagnostic Storage by default and it comes back when it stops. |
| **50** | **Character packs** (10 Oct 2026): the first characters withdrawn; a redrawn kitten and puppy shipped as packs of frames; *Import a character* from animation files; Live2D waiting for its agreement; see §10 | The editor and the recorder play the same frames; an imported character stands on the same spot in every clip. |
| **51** | **Characters react to readings** (10 Oct 2026): low, normal, high and critical, by reading kind (performance, temperature, bandwidth, frame rate and time, latency, fan, power), each with an action and an effect, changeable per character; see §10 | A character sleeps on an idle PC with zZ and stays where it fell asleep; the editor and the recorder judge a reading alike. |
| **48** | **Moving scenes and a picture library** (10 Oct 2026): eight generated scenes with frosted glass cards over the readings, animations played by the recorder, a library of uploads (stills, animated pictures, short videos) copied to storage, 31 screen designs and 8 panels; see §10 | A scene's glass cards sit under its readings in the editor and on the screen, moving; an upload is in every location that keeps pictures and can be chosen from there. |

### Explicit v1 limitations (not silently omitted)
- G-Sync/FreeSync active state: only the Windows VRR setting is shown unless NVAPI/ADLX is integrated later.
- When the recorder runs in a Remote Desktop session, physical monitors are not visible (display configuration is per session); only the remote display and session switches are recorded.
- GPU hotspot, fan RPM and some power values: depend on the vendor and the provider. Intel iGPUs have no fan, hotspot or memory clock of their own (shown as *does not apply*); their temperature and clocks need Intel's own control library and stay unavailable; their power comes from the sensor provider.
- Windows only: the recorder's sources are Windows APIs. A Linux recorder would be a separate collector set; only the Web viewer runs elsewhere (container).
- Per-process network bandwidth and true per-process disk I/O: Enhanced mode (admin) only. Disk I/O issued by the system on a process's behalf (lazy writes, paging) counts towards System (PID 4).
- Short-lived processes (shorter than one process tick): missing from the process list and timeline; with Enhanced mode's flight recorder on they are in the incident's `.etl` trace.
- Crash dumps: listed, not analyzed.
- Windows event rules whose payload layout could not be checked against a real event on the dev PC: WER 1001 (no reports existed; layout taken from the documented message format), AMD/Intel display driver provider names, `disk`/storport/NTFS error layouts (only IDs and levels are used for those). DxgKrnl's own Admin/Operational channels are not subscribed yet, only its System-log events.
- Event messages are in the Windows display language; titles are generated from rules in English.
- Data from the final ≤ 5 s before a BSOD or power loss may be lost (the write-behind interval, which is configurable).
- The in-memory ring holds about 11 minutes with ~350 processes at the default 64 MB; a longer pre-window is filled from telemetry.db, which keeps only the top processes.
- Sleep and end-of-session notifications need the interactive session (the Agent's scheduled task runs there); in a Windows service they would not arrive.
- Board monitoring needs a monitoring chip LibreHardwareMonitor supports (and PawnIO); laptops keep these readings in their embedded controller, which is often not readable. Rails are judged by their names; a board that labels them differently gets information, not judgements.
- On-screen graphs start when their panel is shown (the recorder keeps their history only while they are on a screen), and the editor's previews start when the page opens; so do the graphs' *lo · avg · hi* and trends.
- The frame rate is the rate a game hands to Windows (frame generation in the driver is not seen), and needs the elevated recorder (an ETW session).
- While arranging, the panels take the mouse: a click on a panel does not reach the game or window under it until arranging ends. Esc ends arranging and is taken from other programs meanwhile.
- Background pictures are stored at 1920 pixels on their longest side; a tiled or centred picture is drawn at that size (at 100 % scaling).
- Expected hardware is checked every 20 seconds, so a device that drops out and back between two checks is not reported there (the device monitoring's events still record it while recording). A monitor that is switched off but still connected counts as connected when Windows keeps it in the display configuration.
- Page and theme transitions need the browser's View Transitions with types (Edge and Chrome 125+); elsewhere pages simply change.
- The storage limits are applied every 10 minutes, so the data can be slightly over its limit between passes; the free space is checked every 30 seconds.
- On-screen readings are windows on the desktop: they show over borderless and windowed games and every app, not over a game in exclusive full-screen mode (Windows hands that game the screen). They are drawn only on the desktop the recorder runs on (not over the sign-in screen or a locked PC).
- Device monitoring runs while recording (like every event collector); the register itself is read at every inventory. A device's connection history before recording started comes from Windows' own dates (first installed, last connected, last removed), not from events.
- The command pipe with an **elevated** Agent and a non-elevated client (the normal setup after Phase 15) has not been verified on hardware yet; development ran unelevated.

---

## 15. Decisions (resolved)

| Decision | Outcome |
|---|---|
| Process model / UI technology | .NET Agent + .NET Web host + React/Vite browser UI (R1, R11). The user asked for the best option, not necessarily Next.js. |
| Elevation / when recording runs | The user controls it: Manual, Always or Scheduled recording; the Agent can optionally start at logon (R2, R13, §10a). |
| LibreHardwareMonitor | **Yes**, as the default sensor provider (can be disabled in Settings). |
| Distribution | **Free and open source** under the GPL-3.0. Dependencies follow the policy in §2a. |
