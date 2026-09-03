# Dimly v1.1

A single 187 KB executable. No installer, no runtime to fetch, no service, nothing written
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
  leaves a monitor awake and stuck dim.
- **Three themes**, and a tray icon that stays out of the way.
- **Costs almost nothing to run:** 5.4 MB and 0 ms of CPU across 40 seconds idle. Closing the
  settings window hands its memory back — 45 MB down to under 4 MB.

## New in 1.1

- **Auto restore**, per display, on by default — see the warning above.
- **The Displays page is now a brightness control centre**: live brightness per monitor, a
  slider that moves it, the dim switch, Auto restore, the restore level, and
  **Use current brightness**.
- **A restore level per display**, replacing the single shared one.
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
- A monitor that dropped a single DDC/CI query was treated as having no answer.
- Launching Dimly while it was already running did nothing instead of showing the window.
- The "Dimly is still running" tray hint never appeared.
- A maximised window was mistaken for a fullscreen one, which blocked dimming indefinitely.
- Two internal deadlines were measured from zero rather than the system clock, which would have
  stopped restore retries and the Displays page's live readings after 24.9 days of uptime.

Full detail in [CHANGELOG.md](CHANGELOG.md).

## Changed

- **Half the size of 1.0 despite all of the above:** 374 KB → 187 KB.
- **The scrollbar is drawn in the theme** rather than left as Windows' grey one — the
  panel behind it still does the scrolling, so the wheel and keyboard behave as before.
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
a year, which is hard to justify for a free 187 KB utility.

A handful of antivirus engines flag it on machine-learning heuristics rather than signatures —
3 of 60 on VirusTotal, with every signature-based engine clean. The README explains what those
verdicts are and how to verify the build yourself.

## Verify your download

```powershell
Get-FileHash .\Dimly.exe -Algorithm SHA256
```

`Dimly.exe` — 190,976 bytes

```
SHA-256  b0fe46d2c5035d8ad05b2e7310739e283675753c6b5c3a06f2cc47e836d003fa
```

---

Made by Aitha & AI.
