<div align="center">

<img src="assets/preview/dimly-256.png" width="96" alt="Dimly">

# Dimly

**Dims your screens while you are away, and puts them back the moment you return.**

One file. No installer, no runtime download, no background service.

[![Download](https://img.shields.io/badge/download-Dimly.exe-6E8CFF?style=for-the-badge)](../../releases/latest)

![Platform](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4)
![Framework](https://img.shields.io/badge/.NET%20Framework-4.8-512BD4)
![Size](https://img.shields.io/badge/size-360%20KB-brightgreen)
![Licence](https://img.shields.io/badge/licence-MIT-blue)

<img src="assets/preview/ui-away.png" width="820" alt="Dimly's Away &amp; dimming page">

</div>

---

## Why

Walking away from a bright monitor wastes power and burns backlight hours, and every "dimmer"
utility either needs an installer, drags in a runtime, or just throws a black sheet over the
screen. Dimly is a single 360 KB executable that turns the *actual* backlight down and puts
your exact previous brightness back when you sit down again.

## Features

- **Dims when you leave.** After an idle delay you choose, screens fade down to your away level.
- **Restores exactly what you had.** Not a guess, not a default — the brightness read at the
  moment it dimmed.
- **Dim on demand.** *Dim now* drops the screens straight away and holds them there, ignoring
  the mouse, until you press *Restore brightness*. Good for reading or watching in the dark.
- **Real backlight control**, not an overlay, wherever the hardware allows it — see below.
- **Multiple monitors**, each handled through the best channel it supports, switchable
  individually — verified dimming and restoring two monitors together.
- **Laptops and desktops.** Internal panels and external monitors both work.
- **Costs nothing to run:** measured at **~5 MB of memory and 0 ms of CPU across 30 seconds
  idle**.
- **Three themes**, a proper settings window, and a tray icon that stays out of the way.

## Install

1. Download `Dimly.exe` from the [latest release](../../releases/latest).
2. Put it anywhere you like — a folder you own, a USB stick, wherever.
3. Run it.

That is the whole installation. .NET Framework 4.8 is already part of Windows 10 and 11, so
there is nothing else to fetch. Dimly writes one small text file to
`%AppData%\Dimly\settings.ini` and nothing else; delete the exe and that file and it is gone.

> Windows SmartScreen will warn about an unrecognised publisher, because the executable is not
> code-signed (a certificate costs several hundred pounds a year). Choose **More info →
> Run anyway**, or build it yourself from source in one command — see [Building](#building).

## How brightness is actually changed

This is the part that decides whether an app like this works on your hardware. Dimly probes
each display at startup and picks the strongest channel it will accept:

| Display | Channel | Real backlight? |
| --- | --- | --- |
| Laptop and all-in-one panels | WMI, `WmiMonitorBrightnessMethods` | **Yes** |
| External monitors | DDC/CI over the video cable, `dxva2.dll` | **Yes** |
| Anything that refuses both | A click-through black overlay | No, but it always works |

If a monitor advertises DDC/CI and then rejects the write — common with cheap cables, KVMs and
some docks — Dimly notices the failure and moves that display to the overlay by itself. The
Displays page always shows which channel is genuinely in use, so you are never guessing.

Dimly only ever **darkens**. A display already dimmer than your away level is left alone; it
will never brighten a screen you deliberately turned down.

## Screenshots

<table>
<tr>
<td width="50%"><img src="assets/preview/ui-displays.png" alt="Displays page"></td>
<td width="50%"><img src="assets/preview/ui-appearance.png" alt="Appearance page"></td>
</tr>
<tr>
<td align="center"><b>Displays</b><br>Every screen, the channel it really uses, and a switch each.</td>
<td align="center"><b>Appearance</b><br>Three themes, startup options, and where settings live.</td>
</tr>
</table>

### Themes

| Midnight | Neon | Daylight |
| :---: | :---: | :---: |
| <img src="assets/preview/ui-away.png" alt="Midnight theme"> | <img src="assets/preview/ui-away-neon.png" alt="Neon theme"> | <img src="assets/preview/ui-away-daylight.png" alt="Daylight theme"> |
| Deep slate, calm blue | Black glass, electric cyan | Clean white, crisp ink |

## Settings

**Away & dimming**

| Setting | What it does |
| --- | --- |
| Away brightness | 0–100%. The *Dim now* button applies it immediately so you can judge it. |
| Idle delay | Presets from 5s to 5m, plus a slider covering 5 seconds to 30 minutes. |
| Fade | Off, fast, smooth or slow. |
| Dim when Windows locks | Dims on Win+L instead of waiting out the delay. |
| Never dim over a fullscreen app | Films and games count as being at the desk. |

**Displays** — switch any display in or out. Per-display choices are keyed to the monitor's
hardware ID, so they survive reboots and reconnections.

**Appearance** — theme, start with Windows, start hidden in the tray.

**Tray menu** — Open, Dim now / Restore brightness, Pause, Exit. Double-click the icon to open
the window. Closing the window leaves Dimly running; only Exit quits, and it restores every
display on the way out.

## Safety

Dimly hands your displays back before it goes away, whatever the reason: a normal exit, Windows
signing out or shutting down, waking from sleep, a monitor being unplugged, or an unexpected
crash. If it ever does crash, it writes the stack trace to `%AppData%\Dimly\crash.txt` and tells
you where to find it, because "it just vanished" is not a bug report anyone can act on.

## Building

You need nothing but Windows. Dimly targets .NET Framework 4.8 and is compiled by the compiler
that ships with it, so there is no SDK, no NuGet restore and no project file.

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

The result is `dist/Dimly.exe`, about 360 KB, with the icon embedded. Add `-Run` to launch it
straight after building. The icon itself is generated from code by
`tools/make-icon.ps1` — there are no binary art assets to trust.

Targeting Framework 4.8 rather than .NET 8 is a deliberate trade: it is what makes a genuinely
single-file, install-free 360 KB executable possible, where a modern .NET build would be either
a ~150 MB self-contained file or a runtime download. The cost is C# 5 language level.

## Tests

```powershell
powershell -ExecutionPolicy Bypass -File tools/test.ps1
```

That runs the checks that need nobody at the keyboard:

- **22 checks against the real `DimEngine`** — dimming, restoring, pause, lock and unlock, the
  manual override, the fullscreen guard, per-display exclusion and shutdown — with stand-ins
  for the Win32 idle clock and the hardware, so the code under test is the shipping code.
- **Display enumeration** against whatever is actually plugged into the machine.

Three more scripts move real screen brightness, so they are kept separate. Each puts your
displays back if anything goes wrong.

```powershell
powershell -ExecutionPolicy Bypass -File tools/functest.ps1    # Dim now, measured over DDC/CI
powershell -ExecutionPolicy Bypass -File tools/idletest.ps1    # the automatic idle path
powershell -ExecutionPolicy Bypass -File tools/multitest.ps1   # every display at once
powershell -ExecutionPolicy Bypass -File tools/restore.ps1     # safety net: all displays to 100%
```

`multitest.ps1` is the interesting one on a multi-monitor desk: it records a baseline for every
display, presses *Dim now*, and then checks that each monitor answering DDC/CI actually dropped
*and* that every monitor that does not answer DDC/CI grew a layered, click-through, on-top
overlay instead — then that all of it came back.

`tools/shoot.ps1` regenerates the screenshots in this file.

## Project layout

```
src/Program.cs         entry point, single instance, crash handling
src/TrayApp.cs         tray icon, menu, Windows session and power events
src/DimEngine.cs       the state machine: when to dim, when to come back
src/Displays.cs        display enumeration and the three brightness channels
src/Native.cs          the Win32 surface
src/AppSettings.cs     the INI in %AppData%\Dimly, and the Run key
src/SettingsWindow.cs  the window, its sidebar and its three pages
src/Controls.cs        the custom-drawn control set
src/Theme.cs           the three palettes and the drawing helpers
tools/                 icon generator, tests and screenshot harness
```

## FAQ

**It never dims by itself.**
Something may be generating input continuously — mouse jigglers, some gaming peripherals, and
remote-control sessions all do this. Windows' own "turn off the display after N minutes" would
be equally stuck. The tray's *Dim now* still works regardless.

**A monitor is listed as "Software overlay".**
It refused both the WMI and DDC/CI routes. Check whether DDC/CI is enabled in the monitor's own
menu (it is often off by default), and note that many KVMs, docks and adapters block it. The
overlay still dims the picture, it just cannot touch the backlight.

**Where are my settings?**
`%AppData%\Dimly\settings.ini`, a plain text file you can read, edit or delete.

**Does it need admin rights?**
No. It runs as you and only touches your own display settings and your own `HKCU` Run key.

## Credits

Made by Aitha & AI.

## Licence

[MIT](LICENSE) — do what you like with it, just keep the copyright notice.
