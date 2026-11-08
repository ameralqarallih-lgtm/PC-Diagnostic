# Changelog

All notable changes to PC Diagnostic & Incident Recorder. Each release's installer is on the
[releases page](https://github.com/ameralqarallih-lgtm/PC-Diagnostic/releases).

## [1.0.11] - 2026-11-08

### Added
- **A crash black box.** While the recorder only watches (the default Manual mode), its minutes were in memory and a
  crash lost them: none of one PC's 12 crashes in 30 days had any data from before it. The watched minutes are now also
  written to disk as they come (the busiest programs with their names, about 11 KB a second, up to about 15 MB), so a
  crash's incident has the minutes before it. *Settings › Recording › Crash black box*, on by default.
  ([#20](https://github.com/ameralqarallih-lgtm/PC-Diagnostic/issues/20))
- **System › Stability**: every time Windows went down uncleanly in the last 30 days, from its own log, also the crashes
  PC Diagnostic did not record: blue screens with their code in plain words and the module their dump points at (read
  without a debugger), freezes ended with the power button, and other unexpected shutdowns, each linked to its incident.
  It shows how many crash dumps Windows keeps (5 by default, so older ones are lost) with *Keep the last 50*, and Windows
  Memory Diagnostic's results with *Test the memory…*. ([#22](https://github.com/ameralqarallih-lgtm/PC-Diagnostic/issues/22))
- **A newer version is shown on every page**, with its release page, installer and SHA-256, until put away for that
  version. Once a day the app asks GitHub for the latest release; *Settings › Privacy › Check for new versions* turns it
  off. Nothing is installed by itself. ([#21](https://github.com/ameralqarallih-lgtm/PC-Diagnostic/issues/21))

### Fixed
- **A silent update could abort** when Windows' Program Compatibility Assistant still held one of the program's files
  after the recorder stopped (Restart Manager cannot close that service). The installer now waits a few seconds first.
  ([#23](https://github.com/ameralqarallih-lgtm/PC-Diagnostic/issues/23))

## [1.0.10] - 2026-11-08

### Fixed
- **Every crash becomes an incident.** A blue screen or an unexpected shutdown used to make one only while the recorder
  was recording, and was not looked for at all after the recorder had been stopped: Windows logged 12 unexpected
  shutdowns in 30 days on one PC, and PC Diagnostic made 1 incident. Now a crash while the recorder only watched is an
  incident (with no minutes from before: they were in memory), and after a clean stop the recorder still reads Windows'
  log at its next start, so a crash while it was off is one too, titled "… while the recorder was off" and dated when
  Windows came back. ([#12](https://github.com/ameralqarallih-lgtm/PC-Diagnostic/issues/12))
- **GPU memory incidents name the program and stop repeating for it.** The title says which program holds the most GPU
  memory ("…: 98 %; vmwp.exe (a virtual machine) holds 21 GB"), and the rule stays quiet about that program for 12 hours;
  another program still makes an incident after the usual cooldown. WSL's virtual machine had made 15 incidents in 36
  hours on one PC. Tune-up's WSL advice now counts the GPU memory it holds, and another virtual machine's GPU memory gets
  advice of its own. ([#13](https://github.com/ameralqarallih-lgtm/PC-Diagnostic/issues/13))
- **CPU limits are in cores, the same work on any PC:** busy background programs in the advice from 0.8 of a core, the
  optimizer's list from 0.4, safe mode's "it is busy" from 0.16 (the old 10 %, 5 % and 2 % of an 8-thread PC). A full
  core is only about 4 % of a 24-thread PC and was under every limit.
  ([#15](https://github.com/ameralqarallih-lgtm/PC-Diagnostic/issues/15))
- **Ending a program from Tune-up's advice works in safe mode.** *End it* for a program that is not responding or stuck
  was refused because it had a window or was busy; the confirmation names the program, and protected programs are still
  refused. ([#16](https://github.com/ameralqarallih-lgtm/PC-Diagnostic/issues/16))
- **Updates stop the recorder cleanly, and a clean stop says why.** The installer and the uninstaller ask the recorder to
  stop and wait up to 20 seconds before ending it by force (killed, it counted as an unexpected stop at its next start;
  an update from 1.0.9 still ends it by force once, since 1.0.9 does not listen). The log's last line says whether you
  chose Exit in the tray, Windows ended the session, the installer asked, or another program closed it.
  ([#18](https://github.com/ameralqarallih-lgtm/PC-Diagnostic/issues/18))
- **USB screens draw fewer wasted frames.** A character frame is skipped when the moving background or a gliding value
  sent the screen one in the last 25 ms (that frame shows the characters where they are); before, each made its own, up
  to 50 a second for a screen that showed about 30. ([#19](https://github.com/ameralqarallih-lgtm/PC-Diagnostic/issues/19))
- The unused `Recorder:StartAgentAtLogon` setting is removed (the installer's *Start the recorder when I sign in* option
  decides), and a collector's slow first run at each start is no longer logged as a warning (12 at every start).

### Added
- **Tune-up notices a program stuck in a loop.** A program of your own that has held about one CPU core (0.8 to 1.2)
  without drawing, in every 5-minute slice for 2 hours or more, gets a line in the advice with *End it*. Replayed over 11
  days of stored samples, only a stuck script and WSL's virtual machine (left out) reached 2 hours; compilers, Defender,
  games and node.exe did not. A long job on one thread looks the same, so it is advice, never an incident.
  ([#14](https://github.com/ameralqarallih-lgtm/PC-Diagnostic/issues/14))

### Changed
- **The Incidents list opens ungrouped and newest first on every visit**, with the *Group by* / *Sort by* bars and the
  draggable fields shown as on the other lists. ([#17](https://github.com/ameralqarallih-lgtm/PC-Diagnostic/issues/17))

## [1.0.9] - 2026-11-05

### Fixed
- **Programs that are working are no longer logged as not responding.** Windows calls a window hung once it has not
  handled messages for about 5 seconds, which games do while they load, and each time that raised an incident. Of 153
  stored periods with a known end, 68 % took under 5 seconds and all under a minute, and the games were still drawing
  through every period of 15 seconds or more. A window now counts after 30 seconds hung while its program draws nothing
  (under 1 % of the GPU over 15 seconds), dated when the hang began.
  The process list and Tune-up's advice use the same answer, so they no longer suggest ending a game that is loading.
- **Windows' hang events of type "Quiesce"** (18 of 19 here, mostly helpers such as `dllhost.exe`) are kept as quiet
  events instead of raising incidents. A window that froze is still an incident.
  ([#11](https://github.com/ameralqarallih-lgtm/PC-Diagnostic/issues/11))

## [1.0.8] - 2026-10-26

### Fixed
- **FPS showed "—" in games while another tool tracked frames.** When a present-tracking session of another program
  (an RGB or overlay app's PresentMon, for example) stops keeping up, Windows drops the present events for every
  session that is not independent, and PC Diagnostic's frame session received no presents at all (seen in ARK:
  Survival Ascended). Its tracing sessions are now independent, so other tools no longer starve them, and the frame
  session starts again if its reader stops. ([#10](https://github.com/ameralqarallih-lgtm/PC-Diagnostic/issues/10))

## [1.0.7] - 2026-10-25

### Added
- **The space limit can be raised on the Storage page.** *Change* beside the limit, and *Raise the limit* in the
  over-limit warning, open an editor that shows what the recordings use and what the drive could hold.
  ([#9](https://github.com/ameralqarallih-lgtm/PC-Diagnostic/issues/9))

### Fixed
- **The space limit counts only recordings.** It counted the whole data folder, including the character generator's
  AI models (about 55 GB once downloaded) and its Python. No clean-up of recordings can free that, so with the default
  10 GB limit the recorder kept History at its shortest (2 hours of full detail, 7 days of summaries), removed the
  oldest unpinned incidents, and reported being over the limit because of "pinned or still recording" incidents. The
  generator's folder is now its own line on the Storage page and the limit leaves it out.
  ([#8](https://github.com/ameralqarallih-lgtm/PC-Diagnostic/issues/8))

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

[1.0.9]: https://github.com/ameralqarallih-lgtm/PC-Diagnostic/releases/tag/v1.0.9
[1.0.8]: https://github.com/ameralqarallih-lgtm/PC-Diagnostic/releases/tag/v1.0.8
[1.0.7]: https://github.com/ameralqarallih-lgtm/PC-Diagnostic/releases/tag/v1.0.7
[1.0.6]: https://github.com/ameralqarallih-lgtm/PC-Diagnostic/releases/tag/v1.0.6
[1.0.5]: https://github.com/ameralqarallih-lgtm/PC-Diagnostic/releases/tag/v1.0.5
[1.0.4]: https://github.com/ameralqarallih-lgtm/PC-Diagnostic/releases/tag/v1.0.4
[1.0.2]: https://github.com/ameralqarallih-lgtm/PC-Diagnostic/releases/tag/v1.0.2
[1.0.1]: https://github.com/ameralqarallih-lgtm/PC-Diagnostic/compare/v1.0.0...v1.0.2
[1.0.0]: https://github.com/ameralqarallih-lgtm/PC-Diagnostic/releases/tag/v1.0.0
