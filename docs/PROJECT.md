You are a senior Windows systems engineer, performance diagnostics engineer, software architect, and desktop application developer.

I want you to help me design and build a production-quality Windows application called PC Diagnostic & Incident Recorder.

The purpose of this application is to continuously monitor a Windows PC and help diagnose intermittent problems such as:

Screen flickering

Graphical artifacts/scattering

GPU problems

Application crashes

System freezes

Performance degradation

High CPU/RAM/GPU usage

Disk stalls

Network problems

Driver problems

Hardware overheating

Random application hangs

BSOD-related incidents

Display driver resets

Unexpected system behavior

The application should function like a black box recorder for a Windows PC.

The core concept is:

The application continuously maintains a rolling history of system activity. When a problem occurs, the user can press "Capture Incident", or the application can automatically detect an anomaly and preserve the data immediately before and after the incident.

Do NOT build this as merely another Task Manager clone.

The primary value is historical correlation and incident investigation.

1. Target Platform
Initial target:

Windows 10

Windows 11

64-bit

Desktop application

Local-first

No cloud dependency required

Must work without an Internet connection

Preferred technology:

C#

.NET 8 or newer LTS-compatible architecture

WPF or WinUI 3 for desktop UI

SQLite for local persistent storage

ETW/Event Tracing for Windows where appropriate

Windows Performance Counters where appropriate

Windows Event Log APIs

WMI/CIM only where appropriate

Direct Windows APIs where necessary

Hardware sensor integration should be abstracted behind an interface so that we can integrate:

LibreHardwareMonitor

HWiNFO SDK if licensing permits

Windows-native hardware information

GPU vendor APIs where appropriate

Do not tightly couple the architecture to one hardware-monitoring library.

2. Product Philosophy
The application should answer:

What was the PC doing?

Which process was responsible?

What changed immediately before the problem?

Was CPU/RAM/GPU/disk/network under abnormal load?

Did temperature or clock speed change?

Did a driver or Windows component generate an event?

Did a process crash or hang?

Did the display configuration change?

Did the GPU reset?

What was happening 30 seconds, 1 minute, 5 minutes and 10 minutes before the problem?

The application should prioritize:

Correlation

Timeline

Historical data

Incident capture

Diagnostics

Explainability

Low overhead

Avoid meaningless dashboards containing hundreds of gauges.

3. Core Architecture
Design the system using modular components.

Recommended architecture:

Desktop UI
     |
Application/API Layer
     |
Diagnostic Engine
     |
+-------------------------------+
| Telemetry Engine              |
| Event Engine                  |
| Incident Engine               |
| Anomaly Detection Engine      |
| Snapshot Engine               |
| Report Engine                 |
+-------------------------------+
     |
Data Access Layer
     |
SQLite + Rolling Telemetry Store
     |
Windows / Hardware APIs

Create clear interfaces between these components.

For example:

interface IMetricsCollector
{
    Task<MetricSnapshot> CollectAsync();
}

interface IEventCollector
{
    Task<IReadOnlyList<SystemEvent>> CollectAsync();
}

interface IHardwareSensorProvider
{
    Task<HardwareSnapshot> GetSnapshotAsync();
}

interface IIncidentManager
{
    Task<Incident> CreateIncidentAsync(IncidentTrigger trigger);
}

Use dependency injection.

Avoid creating one giant class.

4. Monitoring Categories
The application must monitor at least:

CPU
Collect:

Overall CPU utilization

Per-core utilization

CPU frequency

Maximum frequency

CPU temperature

CPU package power where available

CPU throttling indicators

CPU time

User/kernel CPU time

Load averages where meaningful

Top CPU-consuming processes

RAM
Collect:

Total RAM

Used RAM

Available RAM

Free RAM

Committed memory

Commit limit

Working set

Private memory

Page faults

Paging activity

Non-paged pool

Paged pool

Memory pressure

Top memory-consuming processes

GPU
Collect where available:

GPU utilization

Per-engine utilization

3D utilization

Compute utilization

Video encode/decode utilization

GPU temperature

GPU hotspot temperature

GPU clock

Memory clock

GPU power

GPU power limit/throttling

VRAM usage

VRAM available

GPU memory allocation by process

GPU engine by process

Support NVIDIA, AMD and Intel as much as practical.

Do not assume every sensor exists on every GPU.

The application must gracefully handle unavailable metrics.

Disk
Collect:

Read throughput

Write throughput

IOPS

Read latency

Write latency

Queue length

Active time

Disk utilization

Disk temperature

Disk health where available

Disk capacity

Free space

Per-process disk I/O

SMART information where legally/technically appropriate

Support:

NVMe

SATA SSD

HDD

USB storage

Network
Collect:

Upload bandwidth

Download bandwidth

Packets sent

Packets received

Errors

Drops

Interface utilization

Link speed

Wi-Fi signal information where available

Per-process network activity where practical

Connection changes

Support:

Ethernet

Wi-Fi

VPN interfaces

Virtual network adapters

5. Process Monitoring
Maintain a process inventory.

For each process collect where available:

Process name

PID

Parent PID

Executable path

Command line where permissions allow

User

Start time

CPU usage

CPU time

RAM usage

Private bytes

Working set

Commit

Disk read

Disk write

Network activity where available

GPU utilization

GPU engine

GPU memory

Thread count

Handle count

Priority

Process state

Digital signature information where practical

Track process lifecycle:

PROCESS_STARTED
PROCESS_STOPPED
PROCESS_CRASHED
PROCESS_HUNG

The system must detect changes rather than only storing snapshots.

6. Windows Services
Monitor Windows services.

Collect:

Service name

Display name

Status

Startup type

PID

Service type

Start time where available

Track:

Service started

Service stopped

Service restarted

Startup type changed

Do not continuously poll excessively.

Use event-driven mechanisms where practical.

7. Display Diagnostics
Create a dedicated display diagnostics subsystem.

Collect:

Number of displays

Monitor manufacturer

Model

Resolution

Refresh rate

Connection type

HDR state

VRR state

G-Sync/FreeSync information where available

Display orientation

Primary display

Display topology

Display driver

GPU driving the display

Detect:

Display connected

Display disconnected

Display configuration changed

Resolution changed

Refresh rate changed

HDR changed

Display driver events

GPU reset events where detectable

This subsystem is particularly important because the application is intended to investigate screen flickering and graphical artifacts.

8. Windows Event Monitoring
Monitor relevant Windows Event Logs.

Prioritize:

Application crashes

Application hangs

Windows Error Reporting

Display driver events

GPU-related events

Kernel events

Disk errors

NTFS errors

Network adapter errors

Device errors

Service failures

Unexpected shutdowns

Bug checks

Hardware errors

Do NOT simply dump the entire Windows Event Log.

Create a filtering and classification system.

Represent events internally like:

SystemEvent
{
    Timestamp
    Provider
    EventId
    Level
    Category
    Message
    ProcessId
    ProcessName
    RelatedDevice
    Severity
}

9. Rolling Black-Box Recorder
This is the most important feature.

The application must continuously maintain a rolling buffer.

Default:

10 minutes before incident

2 minutes after incident

Make this configurable:

1 minute
5 minutes
10 minutes
15 minutes
30 minutes

Telemetry should be sampled efficiently.

Default sampling:

System metrics: 1 second

Hardware sensors: 1 second

Process metrics: 1–2 seconds

Events: event-driven

Detailed process inventory: 5–10 seconds

Hardware inventory: on startup and when changed

Do not unnecessarily consume CPU, disk or RAM.

Old telemetry should automatically expire from the rolling buffer unless preserved as part of an incident.

10. Incident Capture
Provide a prominent button:

CAPTURE INCIDENT

When clicked:

Record timestamp.

Preserve the previous N minutes.

Continue collecting for the configured post-incident period.

Capture a complete system snapshot.

Capture process snapshot.

Capture service snapshot.

Capture hardware snapshot.

Capture display configuration.

Capture relevant Windows Events.

Capture driver information.

Save the incident permanently.

Generate a unique Incident ID.

Example:

INC-20260928-00027

11. Automatic Incident Triggers
Create an extensible trigger framework.

Examples:

CPU > 95% for 30 seconds

RAM available < 1 GB

GPU > 98% for 20 seconds

GPU temperature > configured threshold

GPU clock suddenly drops

Disk latency > configured threshold

Disk queue becomes excessive

Network packet errors spike

Application crashes

Application becomes unresponsive

Display configuration changes

Display driver error detected

GPU reset detected

Windows hardware error detected

Users should be able to enable/disable individual triggers.

Do NOT claim that a trigger proves the root cause.

The application should say:

"Correlation detected"

rather than:

"GPU caused the crash."

12. Anomaly Detection
Implement a simple, transparent anomaly engine first.

Avoid machine learning in version 1.

Use:

Thresholds

Baselines

Rate-of-change detection

Moving averages

Standard deviation

Sudden transitions

Duration-based thresholds

For example:

GPU normally:
40–70%

Current:
99%

Duration:
18 seconds

Classification:
GPU utilization anomaly

Make the reasoning visible to the user.

13. Incident Timeline
Every incident must have a timeline.

Example:

12:30:51
GPU utilization increased

12:30:54
GPU clock dropped from 2100 MHz to 300 MHz

12:30:56
DWM GPU activity increased

12:30:58
Display-related Windows event detected

12:31:00
User marked "Screen Flicker"

12:31:03
Application became unresponsive

12:31:05
Application crashed

The timeline is more important than raw graphs.

14. User-Reported Incident Types
Provide quick buttons:

Screen Flicker
Screen Artifact
System Freeze
Application Crash
Game Crash
Slowdown
High CPU
High RAM
High GPU
Disk Problem
Network Problem
Other

The user can press one when they observe a problem.

This timestamp becomes the center point of the incident.

15. Dashboard
Create a modern dashboard with:

System Health
CPU       42%
RAM       63%
GPU       31%
VRAM      48%
DISK      8%
NETWORK   12 Mbps

Temperatures
CPU       61°C
GPU       64°C
GPU Hot   72°C
SSD       47°C

Current Top Processes
Show:

CPU

RAM

GPU

Disk

Recent Events
Show the latest important events.

Incident Status
Show:

Active monitoring

Last incident

Number of incidents

Current anomalies

16. Historical Charts
Charts must support:

1 minute

5 minutes

15 minutes

1 hour

6 hours

24 hours

Charts:

CPU

RAM

GPU

VRAM

Disk

Network

Temperatures

Clocks

Power

Allow overlaying metrics.

For example:

GPU utilization
GPU temperature
GPU clock
VRAM

on the same timeline.

This is important for identifying correlations.

17. Incident Investigation UI
Create a dedicated incident page.

Layout:

-------------------------------------------------
INCIDENT #00027
Screen Flicker
28 Sep 2026 12:31:06
-------------------------------------------------

SUMMARY

CPU       52%
RAM       68%
GPU       99%
VRAM      81%
DISK      14%
NETWORK   4 Mbps

-------------------------------------------------

TIMELINE

12:30:45 ─────────
12:30:50 ─────────
12:30:55 ─────────
12:31:00 ─────────
12:31:05 🔴 INCIDENT
12:31:10 ─────────

-------------------------------------------------

CPU
[graph]

RAM
[graph]

GPU
[graph]

DISK
[graph]

NETWORK
[graph]

-------------------------------------------------

PROCESSES

-------------------------------------------------

WINDOWS EVENTS

-------------------------------------------------

DRIVERS

-------------------------------------------------

DISPLAY

-------------------------------------------------

The user should be able to click any event and see related metrics around that timestamp.

18. Reports
Provide:

Quick Report
Human-readable summary.

Technical Report
Detailed metrics.

Export
Support:

HTML

PDF

CSV

JSON

ZIP

An incident ZIP should contain:

incident.json
summary.html
metrics.csv
processes.csv
services.csv
events.csv
hardware.json
display.json
drivers.json
system.json

If ETW traces are collected:

traces/
    incident.etl

19. Diagnostic Summary
At the end of a report, generate a factual summary.

Example:

OBSERVATIONS

1. GPU utilization reached 99% approximately
   4 seconds before the reported screen flicker.

2. GPU clock dropped from 2050 MHz to 300 MHz
   during the same interval.

3. A display-related Windows event occurred
   approximately 2 seconds before the incident.

4. No significant RAM pressure was observed.

5. Disk latency remained within the configured
   normal range.

6. No network anomaly was detected.

POSSIBLE CORRELATIONS

- GPU/display subsystem activity correlated with
  the reported incident.

NOT PROVEN

- The collected telemetry does not establish that
  the GPU caused the problem.

The diagnostic engine must distinguish:

Observation

Correlation

Possible explanation

Confirmed event

Never present speculation as fact.

20. Performance Requirements
The monitoring application itself must not become the problem.

Target:

<1–2% average CPU usage during normal monitoring

Low memory usage

Minimal disk writes

Configurable sampling rate

Batch writes

Efficient SQLite usage

Automatic data retention

Background operation

No UI blocking

The application should be able to monitor continuously for days.

21. Privacy
Everything should be local by default.

Do not upload telemetry.

Provide settings for:

Data retention

Process command-line capture

User/account information

Network information

Windows Event collection

Clearly explain potentially sensitive data.

22. Security
The application should:

Follow least privilege

Request administrator privileges only when necessary

Clearly explain elevated operations

Validate external paths

Avoid arbitrary command execution

Protect local diagnostic data

Avoid collecting passwords/secrets

Avoid network communication unless explicitly enabled

23. Development Strategy
Do NOT attempt to build the entire application in one response.

Build incrementally.

Phase 1:

Create the architecture and solution structure.

Phase 2:

Implement CPU/RAM monitoring.

Phase 3:

Implement GPU monitoring abstraction.

Phase 4:

Implement disk/network monitoring.

Phase 5:

Implement process monitoring.

Phase 6:

Implement hardware sensors.

Phase 7:

Implement Windows Event monitoring.

Phase 8:

Implement rolling recorder.

Phase 9:

Implement incident capture.

Phase 10:

Implement anomaly detection.

Phase 11:

Implement dashboard.

Phase 12:

Implement incident investigation UI.

Phase 13:

Implement reporting/export.

Phase 14:

Performance optimization.

Phase 15:

Testing and packaging.

24. Important Engineering Rules
Before writing code:

Explain the architecture.

Identify Windows APIs required.

Identify APIs that require administrator privileges.

Identify metrics that cannot reliably be obtained on every Windows machine.

Design graceful fallbacks.

Identify third-party dependencies.

Consider licensing.

Design the database schema.

Design the telemetry model.

Design the incident model.

Do not invent APIs.

If you are uncertain whether a Windows API exists or how it behaves, verify it against current Microsoft documentation.

Prefer official Microsoft documentation for Windows APIs.

25. Code Quality
Use:

Clean Architecture principles where appropriate

SOLID principles

Dependency injection

Async APIs

CancellationToken

Structured logging

Nullable reference types

Unit tests

Integration tests

XML documentation for public APIs

Meaningful exceptions

Configuration classes

Interfaces around OS-specific functionality

Avoid:

Giant classes

Static global state

Hard-coded thresholds

Blocking UI operations

Unbounded collections

Excessive polling

Writing every metric individually to disk

26. Database
Design a SQLite schema for at least:

SystemSnapshots
CpuMetrics
MemoryMetrics
GpuMetrics
DiskMetrics
NetworkMetrics
ProcessSnapshots
ProcessEvents
ServiceSnapshots
ServiceEvents
HardwareSnapshots
DisplaySnapshots
WindowsEvents
Incidents
IncidentEvents
Anomalies
Drivers
SystemInformation

Optimize for:

Fast recent-history queries

Incident queries

Timeline queries

Data retention

Large telemetry volume

Explain indexing strategy.

27. Plugin Architecture
Design the collector system so additional collectors can be added later.

For example:

ICpuCollector
IMemoryCollector
IGpuCollector
IDiskCollector
INetworkCollector
IProcessCollector
IServiceCollector
IDisplayCollector
IEventCollector
ISensorCollector

Future plugins might include:

AudioCollector
USBCollector
BluetoothCollector
BatteryCollector
WiFiCollector
GameCollector
BrowserCollector

28. Hardware Compatibility
The application must never crash because a metric is unavailable.

For example:

GPU Hotspot:
Unavailable

GPU Power:
Unavailable

Fan RPM:
Unavailable

rather than:

Application crashed because sensor unavailable.

Represent sensor availability explicitly.

29. Testing
Create test scenarios for:

Normal PC

High CPU

High RAM

High GPU

GPU temperature spike

Disk saturation

Disk latency

Network interruption

Application crash

Application hang

Display configuration change

GPU driver reset

Multiple GPUs

Multiple monitors

Laptop

Desktop

AMD GPU

NVIDIA GPU

Intel GPU

Missing sensors

Permission denied

Service unavailable

Also test long-running monitoring.

30. What I expect from you
Act as the technical lead.

Do not immediately dump thousands of lines of code.

Start with:

Product architecture

Component diagram

Technology decisions

Data model

Database schema

Project/solution structure

Windows API mapping

Telemetry sampling architecture

Rolling buffer architecture

Incident lifecycle

Security model

Development roadmap

Then ask me to approve the architecture before implementing Phase 1.

After approval, implement one phase at a time.

For every phase:

Explain what you are implementing.

Show files being created/changed.

Provide complete compilable code.

Explain dependencies.

Provide build instructions.

Provide tests.

Identify limitations.

Do not silently omit functionality.

The final product should feel like a professional Windows diagnostic product, not a coding demo.

The most important product principle is:

When something goes wrong, the application should preserve enough evidence to understand what happened.