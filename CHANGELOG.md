# Changelog

## 1.1 — 2 September 2026

### Added

- **Never dim while media is playing.** Dimly asks the Windows audio engine whether anything is
  actually coming out of the speakers, so a video in a browser tab, VLC, PotPlayer or a music
  player all hold the countdown. Pausing genuinely stops the stream, and the countdown then
  starts from that moment rather than dimming on the spot. Any sound counts, music included.
- **Ignore devices that keep the PC awake.** Windows' idle clock is reset by any HID report at
  all, so a game controller with drifting analogue sticks pins it at zero forever and nothing —
  not the screen saver, not the display timeout, not Dimly — can ever fire. With this on, Dimly
  counts real keyboard, mouse and gamepad use instead. Off by default.
- The executable now carries proper version information, so Explorer's tooltip and **Properties
  → Details** show the version, description and publisher.
- `build.ps1` prints a SHA-256 of the build, for publishing alongside a release.

### Fixed

- **A maximised window was mistaken for a fullscreen one**, which made *Never dim over a
  fullscreen app* block dimming indefinitely. A maximised window overhangs its monitor by the
  invisible resize border, and when the taskbar is set to auto-hide the work area is the whole
  monitor — so an ordinary maximised browser matched the old test exactly. Dimly now tells the
  two apart by window placement and frame.
- The away-brightness gauge drew the `%` hard against the number, which collided for narrow
  readings such as `1%`.
- The gauge sat directly beneath the card heading with no space between them.

### Changed

- Activity is read through **Raw Input instead of a low-level keyboard hook**.
  `SetWindowsHookEx(WH_KEYBOARD_LL)` is the API every keylogger reaches for, and installing one
  across the machine is the most heuristic-tripping thing a small unsigned utility can do.
  Dimly now has Windows post notifications to one private window, and only ever looks at which
  kind of device sent them — never at what was typed.
- The two new rules are worded to match the rest, and the settings window is taller to fit them.

### Known issues

- Some antivirus engines flag the unsigned executable. On VirusTotal it scores 3 of 60, and all
  three are machine-learning verdicts rather than signatures — Microsoft's `Wacatac.B!ml`,
  Trapmine's `Suspicious.low.ml.score`, and SecureAge, an ML-only engine. Every signature-based
  engine returns clean. See **Windows says it does not recognise this app** in the README.

## 1.0

First release.
