# Dimly v1.1

A single 194 KB executable. No installer, no runtime to fetch, no service, nothing written
outside `%AppData%\Dimly`.

---

## ⚠️ Read this before you first walk away — Auto restore is new, and it is on

Dimly 1.0 read your screen's brightness before dimming and put that exact value back.
**1.1 does not do that by default.**

Every display now has an **Auto restore** switch, on out of the box:

- Dimly **never reads** the display before dimming — it dims straight to your away level.
- When you come back, the screen is put at the **Restore to** level *you* chose for it, not at
  whatever it was showing before.
- **That level starts at 100%.** On a screen you keep at 75%, the first dim-and-return leaves
  you at 100% until you set it.

**Set it once, per display:** open **Displays**, put the screen where you like it, press
**Use current brightness**.

**Want 1.0's behaviour?** Switch **Auto restore** off for that display. Dimly then reads the
brightness before dimming and puts back exactly what it found, using your chosen level only if
that fails.

*Why:* reading a monitor is the fragile part — it is slow, monitors drop queries, and one that
has been switched off answers with stale values. Not asking is the most reliable way to dim and
come back.

---

## What Dimly does

- **Dims when you leave**, after an idle delay from 5 seconds to 30 minutes.
- **Brings the brightness back when you return** — by Auto restore, or by reading and replacing
  the exact level, per display.
- **Dim now** drops the screens immediately and holds them there until you press it again.
- **A brightness control centre** on the Displays page: every monitor's current brightness,
  live, on a slider that moves it.
- **Never dims while media is playing** — a browser video, VLC, PotPlayer, music. Pausing
  starts the countdown from that moment rather than dimming on the spot.
- **Never dims over a fullscreen app**, while a merely maximised window still counts as idle.
- **Dims when Windows locks**, if you want it to.
- **Works on a machine whose idle clock never advances** — a drifting game controller pins
  Windows' idle timer at zero forever; *Ignore devices that keep the PC awake* counts real
  input instead.
- **Real backlight control** — laptop panels over WMI, monitors over DDC/CI, with a
  click-through overlay only where neither is offered.
- **Multiple monitors**, each with its own settings.
- **Survives the screen being switched off** by Windows' display timeout, which otherwise
  leaves a monitor awake and stuck dim. **Smart restore** goes further: it looks the displays
  over before handing the brightness back — waiting first for the monitors to actually leave
  power save — and holds the dim through all of it even if you are already back.
- **Three themes**, and a tray icon that stays out of the way.
- **Costs almost nothing to run:** measurably nothing allocated while it sits watching, and its
  memory is handed back to Windows once you have gone — around 1–3 MB while away, against 13 MB
  and climbing if it held on to it. Closing the settings window hands that back too: 45 MB down
  to under 4 MB.

## New in 1.1

- **Auto restore**, per display, on by default — see the warning above.
- **The Displays page is now a brightness control centre**: live brightness per monitor, a
  slider that moves it, the dim switch, Auto restore, the restore level, and
  **Use current brightness**.
- **A restore level per display**, replacing the single shared one.
- **Smart restore**, on by default — when Windows switches the screen off, Dimly waits for the
  monitors to actually come out of power save (Windows calls the screen on seconds before they
  do), checks the displays over, and only then hands the brightness back. The dim is held
  through all of it, so the brightness returns once rather than being written into a monitor
  that is still dark.
- **Never dim while media is playing.**
- **Ignore devices that keep the PC awake.**
- Version information in the executable, so Explorer's tooltip and Properties → Details are
  filled in.

## Fixed in 1.1

- A screen switched off by Windows' display timeout came back dim and stayed dim until the
  monitor's own buttons were used.
- A screen left dim after the display slept, and stayed stuck dim even on quit.
- With the Displays page open, the app said "Dimmed" while every screen stayed bright.
- The brightness shown on the Displays page could be stale.
- A monitor that dropped a single DDC/CI query was treated as having no answer, and one that
  dropped a reading made the live brightness on the Displays page flicker.
- The mouse wheel did nothing when the pointer was over the scrollbar itself.
- A window dragged off the edge of the desktop could not be brought back, because it has no
  title bar for Windows to move it by.
- Quitting or signing out while Dimly was checking the displays left the screens dimmed.
- Launching Dimly while it was already running did nothing instead of showing the window.
- The "Dimly is still running" tray hint never appeared.
- A maximised window was mistaken for a fullscreen one, which blocked dimming indefinitely.
- Two internal deadlines were measured from zero rather than the system clock, which would have
  stopped restore retries and the Displays page's live readings after 24.9 days of uptime.

Full detail in [CHANGELOG.md](CHANGELOG.md).

## Changed

- **Half the size of 1.0 despite all of the above:** 374 KB → 194 KB.
- **Re-establishing the displays is around sixty times faster** — 62 ms down to under 1 ms, and
  no longer growing with the number of monitors. Nearly all of it was one redundant question
  put to each monitor over DDC/CI; the write that follows is read back and retried until the
  display agrees, so asking beforehand proved nothing. This matters now that the check runs
  every time the screen switches off.
- **A rescan keeps the displays it already has** when the same monitors are attached in the
  same places, so overlays, per-display settings and captured levels all survive. Plugging a
  monitor in or out still rebuilds everything, and **Rescan** always does a full re-probe.
- **The scrollbar is drawn in the theme** rather than left as Windows' grey one — the
  panel behind it still does the scrolling, so the wheel and keyboard behave as before.
- **The mouse wheel no longer moves a slider.** Scrolling with the pointer over one used to
  change the brightness; it now scrolls the page, as expected. Arrow keys still move sliders.
- **Dimly hands its memory back to Windows once you have gone** — a few seconds after the
  screen dims, and again the moment Windows switches the screen off, which are the two moments
  nothing can be waiting on it. And the window now repaints only when something has actually
  changed rather than once a second regardless, which halves what the app accumulates while it
  is open.
- Activity is read through **Raw Input instead of a low-level keyboard hook** — the API every
  keylogger reaches for, and the most heuristic-tripping thing a small unsigned utility can do.
  Dimly only ever looks at which *kind* of device sent an event, never at what was typed.

## Install

1. Download `Dimly.exe` below.
2. Put it anywhere you like.
3. Run it.

.NET Framework 4.8 is already part of Windows 10 and 11.

## Windows SmartScreen

The executable is unsigned, so SmartScreen shows **"Windows protected your PC"** the first
time. Choose **More info → Run anyway**. A code-signing certificate costs a few hundred pounds
a year, which is hard to justify for a free 194 KB utility.

A handful of antivirus engines flag it on machine-learning heuristics rather than signatures —
3 of 60 on VirusTotal, with every signature-based engine clean. The README explains what those
verdicts are and how to verify the build yourself.

## Verify your download

```powershell
Get-FileHash .\Dimly.exe -Algorithm SHA256
```

This checks that the file you downloaded is the file published here. Building from source will
*not* reproduce this hash — the C# 5 compiler stamps a build time and a fresh module GUID into
every build, so two builds of identical source differ in 46 bytes.

`Dimly.exe` — 198,144 bytes

```
SHA-256  296c17c33f9bd7f9a34cc215071398ddf864c086d695aa2214080c38dc9cfe70
```

---

Made by Aitha & AI.
