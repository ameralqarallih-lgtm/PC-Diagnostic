# Changelog

All notable changes to PC Diagnostic & Incident Recorder. Each release's installer is on the
[releases page](https://github.com/ameralqarallih-lgtm/PC-Diagnostic/releases).

## [1.0.7] - 2026-10-25

### Added
- **The space limit can be raised on the Storage page.** *Change* beside the limit, and *Raise the limit* in the
  over-limit warning, open an editor that shows what the recordings use and what the drive could hold.

### Fixed
- **The space limit counts only recordings.** It counted the whole data folder, including the character generator's
  AI models (about 55 GB once downloaded) and its Python. No clean-up of recordings can free that, so with the default
  10 GB limit the recorder kept History at its shortest (2 hours of full detail, 7 days of summaries), removed the
  oldest unpinned incidents, and reported being over the limit because of "pinned or still recording" incidents. The
  generator's folder is now its own line on the Storage page and the limit leaves it out.

## [1.0.6] - 2026-10-25

### Added
- **The GeForce RTX 50 series hotspot temperature is recorded.** NVIDIA left it out of its public interfaces (NVAPI,
  NVML) for Blackwell cards, so the recorder reads it from the GPU's own thermal registers through the PawnIO driver
  it already installs, as HWMonitor 1.65.1 and HWiNFO do: the hottest of six thermal channels. The reading uses the
  signed `Nvidia.bin` module from PawnIO.Modules 0.2.11, shipped unmodified (LGPL-2.1, credited in
  THIRD-PARTY-NOTICES). Other GPUs keep their hotspot from where they had it; the module only answers for RTX 50
  series chips. ([#1](https://github.com/ameralqarallih-lgtm/PC-Diagnostic/issues/1))

### Fixed
- **1.0.0 was wrong about the RTX 50 hotspot.** It said NVIDIA had removed the sensor and no tool could read it; the
  sensor is there, only left out of NVIDIA's public interfaces. Where the reading still fails (PawnIO missing), the
  Readings tab says why and what to do instead of calling it a reading the card cannot have.

## [1.0.5] - 2026-10-25

### Fixed
- **Drives behind Intel RST (VMD) show their real temperature.** Windows' reliability counters hold a placeholder
  there (a fixed 60 °C on every drive, whatever its temperature); an NVMe drive's temperature now comes from the
  drive's own health log.
- **Drives behind Intel RST show their wear and power-on hours** (and spare capacity, media errors, unsafe
  shutdowns, data read and written) when the RST driver does not pass Windows' health request on: LibreHardwareMonitor's
  storage library, which reaches drives through Intel RST and VROC and makers' own NVMe drivers, fills what Windows
  cannot. Seen on a Samsung 970 EVO.
- **Intel integrated graphics:** their temperature and core clock show as "Not available on this GPU" (Intel's driver
  gives monitoring programs neither; the CPU package temperature covers the chip they share), and the power share of
  its limit as not applying (integrated graphics have no power limit of their own), instead of unavailable readings.

## [1.0.4] - 2026-10-25

### Fixed
- **The recorder uses less CPU while an animated background plays** (on monitors and USB screens): what lies over the
  picture (pattern, sheen, accent edge) is drawn once into a kept layer instead of on every frame, and a picture at its
  own size is copied pixel for pixel. On a 1920 x 480 USB screen at 20 frames a second a motion frame takes 7.9 ms
  instead of 16.5 and a full redraw 21 ms instead of 33; the recorder went from 4.1% to 3.4% of the PC. Recording
  itself costs about 2 s of CPU a minute; the rest follows the screen's motion frame rate (its setting).
  ([#6](https://github.com/ameralqarallih-lgtm/PC-Diagnostic/issues/6))
- **The Recorder cost tab adds up**: a part measured inside another (a USB screen's drawing inside the motion timer)
  was counted in both.
- **A process seen in only one sample shows "Just started"**, with why, instead of "Unavailable": CPU use is measured
  between two samples, 2 seconds apart. ([#7](https://github.com/ameralqarallih-lgtm/PC-Diagnostic/issues/7))

## [1.0.2] - 2026-10-25

### Changed
- A moment's sections are a bar of buttons pinned at its top: **Values · Processes · Services**, plus **Chart** and
  **Events** on History. Each scrolls to its part; Processes and Services also open that list. Picking a moment on
  History scrolls straight to it. ([#5](https://github.com/ameralqarallih-lgtm/PC-Diagnostic/issues/5))

## [1.0.1] - 2026-10-25

### Fixed
- **History moments list every process again.** A process's name is sent once, when it is first seen; while the
  recorder only watched (dashboard open, not recording) those were dropped, so processes already running when a
  recording started had no stored name and were left out of every moment. They are now kept while nothing is stored
  and written with each process's first stored figures. Moments recorded before this fix stay incomplete.
  ([#2](https://github.com/ameralqarallih-lgtm/PC-Diagnostic/issues/2))
- **History moments list the services.** The hourly service list was dropped the same way; it is now kept and written
  with the first stored sample, with the changes since. ([#3](https://github.com/ameralqarallih-lgtm/PC-Diagnostic/issues/3))
- **CPU usage matches Task Manager.** It is now Windows' "% Processor Time" (what Task Manager shows since Windows 11's
  2025 update) instead of "% Processor Utility", which counts work against the base clock and ran far above it while the
  CPU boosted. Memory, GPU, disk and network readings were checked against Windows the same way and matched.
  ([#4](https://github.com/ameralqarallih-lgtm/PC-Diagnostic/issues/4))

## [1.0.0] - 2026-10-24

The first public release: free and open source under the GPL-3.0, every feature for everyone.

### Fixed
- On a GeForce RTX 50 series GPU the hotspot temperature shows as "Not available on this GPU" (NVIDIA removed the
  sensor) instead of counting as an unavailable reading. ([#1](https://github.com/ameralqarallih-lgtm/PC-Diagnostic/issues/1))

[1.0.7]: https://github.com/ameralqarallih-lgtm/PC-Diagnostic/releases/tag/v1.0.7
[1.0.6]: https://github.com/ameralqarallih-lgtm/PC-Diagnostic/releases/tag/v1.0.6
[1.0.5]: https://github.com/ameralqarallih-lgtm/PC-Diagnostic/releases/tag/v1.0.5
[1.0.4]: https://github.com/ameralqarallih-lgtm/PC-Diagnostic/releases/tag/v1.0.4
[1.0.2]: https://github.com/ameralqarallih-lgtm/PC-Diagnostic/releases/tag/v1.0.2
[1.0.1]: https://github.com/ameralqarallih-lgtm/PC-Diagnostic/compare/v1.0.0...v1.0.2
[1.0.0]: https://github.com/ameralqarallih-lgtm/PC-Diagnostic/releases/tag/v1.0.0
