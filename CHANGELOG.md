# Changelog

## 1.1 — 3 September 2026

> ### Please read before updating: Auto restore is new, and it is on
>
> Dimly 1.0 always read a display's brightness before dimming it and put that exact value
> back. **1.1 does not do that by default.** Every display now has an **Auto restore** switch,
> on out of the box, which means:
>
> - Dimly **never reads** the display before dimming. It dims straight to your away level.
> - When you come back, the display is put at the **Restore to** level *you* chose for it —
>   not at whatever it happened to be showing beforehand.
> - **That level starts at 100%.** So on a screen you keep at 75%, the first dim-and-return
>   will leave you at 100% until you set it.
>
> **Set it once, per display:** open **Displays**, put the screen where you like it, and press
> **Use current brightness**. That copies the level into **Restore to**.
>
> **Prefer how 1.0 behaved?** Turn **Auto restore** off for that display. Dimly then reads the
> brightness before dimming and puts back exactly what it found, using **If restoring fails**
> only when that cannot be done.
>
> Auto restore exists because reading a monitor is the fragile part: it is slow, monitors drop
> queries, and a display that has been switched off answers with stale values. Not asking is
> the most reliable way to dim and return.

### Added

- **Auto restore, per display, on by default.** Dimly dims straight to the away level and
  brings the display back to the level chosen for it, without ever asking the display how
  bright it is. Switch it off for a display and 1.0's behaviour returns. Two consequences
  worth knowing: because nothing is read there is no honest place to fade *from*, so the dim
  is written in one go rather than faded (guessing would flash the screen brighter before
  dimming it); and for the same reason a display already darker than the away level is taken
  to the away level rather than left alone.
- **The Displays page is a brightness control centre.** One card per monitor holding
  everything about it:
  - **Current brightness (Realtime)** — what the screen is set to now, on a slider that moves
    it. Re-read every second and a half while the page is open, so it follows the monitor even
    when its own buttons or another program change it. Setting a level by hand counts as you
    being at the desk, so it releases a dim rather than fighting it.
  - **Whether Dimly dims this display.**
  - **Auto restore**, and the **Restore to** level it uses, with **Use current brightness** to
    copy what is on screen into it.
  - A monitor Dimly cannot reach says so plainly, and one that will not report its level says
    that too rather than showing a made-up number.
- **A restore level per display**, replacing the single shared one — which is kept as the
  default for any display not given its own.
- **Never dim while media is playing.** Dimly asks the Windows audio engine whether anything
  is actually coming out of the speakers, so a video in a browser tab, VLC, PotPlayer or a
  music player all hold the countdown. Pausing genuinely stops the stream, and the countdown
  then starts from that moment rather than dimming on the spot. Any sound counts, music
  included.
- **Ignore devices that keep the PC awake.** Windows' idle clock is reset by any HID report at
  all, so a game controller with drifting analogue sticks pins it at zero forever and nothing
  — not the screen saver, not the display timeout, not Dimly — can ever fire. With this on,
  Dimly counts real keyboard, mouse and gamepad use instead. Off by default.
- The executable carries proper version information, so Explorer's tooltip and **Properties →
  Details** show the version, description and publisher.
- `build.ps1` prints a SHA-256 of the build, for publishing alongside a release.

### Changed

- **Half the size of 1.0, despite everything above**: 374 KB to 184 KB. The icon was embedded
  twice — once as the Win32 resource Windows uses for the file, and again as a managed
  resource for the app to read — so Dimly now reads the Win32 copy it already carries. The
  large icon frames are also PNG-compressed, which halved the icon itself; a single 128px
  uncompressed frame was 66 KB.
- Activity is read through **Raw Input instead of a low-level keyboard hook**.
  `SetWindowsHookEx(WH_KEYBOARD_LL)` is the API every keylogger reaches for, and installing one
  across the machine is the most heuristic-tripping thing a small unsigned utility can do.
  Dimly now has Windows post notifications to one private window, and only ever looks at which
  kind of device sent them — never at what was typed.
- Closing the window explains, once, that Dimly is still in the tray. Silently vanishing is
  the most common way a tray app loses a user.
- Rescanning displays says that it is scanning, and a button that cannot be pressed now looks
  like one.
- The rules are worded consistently, and the settings window is taller to fit them.

### Fixed

- **A screen switched off by Windows came back dim and stayed dim.** Not sleep — the display
  timeout, after which the only warning Dimly gets is a power notification it was not
  listening for. The monitor handle it holds survives the power-off in name only: it accepts
  writes, ignores them, and answers a read with the value it was given. So the restore made
  the moment the mouse moves is confirmed and let go of while the panel is still powering up,
  and the screen comes back at the dimmed level until the monitor's own buttons are used.
  Dimly now listens for the display coming back, takes every monitor handle again, and puts
  the level it last set back on the record so the restore has to prove itself afresh.
- **A screen left dim after the display slept.** A monitor woken from power-off accepts a
  brightness command, confirms it when read back, and then finishes its own start-up by
  reloading the brightness it had — quietly undoing the restore seconds later. Dimly noticed
  nothing, and the *next* dim read the dimmed screen and captured that as the level to return
  to, so the display was stuck dim for good: quitting and dimming by hand both "restored" it
  to dim. A restored brightness is now watched over for a few seconds and put back if the
  display drifts off it, a restore the display refuses is remembered and offered again rather
  than thrown away, and the captured level is only let go of once the display agrees with it.
- **The Displays page could stop the dim it was reporting on.** With that page open, the app
  would say "Dimmed" while every screen stayed bright. Refreshing the levels shown there
  cancelled whatever the engine had in flight — and the engine had queued the dim a moment
  earlier. A reading is not a change of intent, so it waits its turn instead of cancelling.
- **The level shown on the Displays page could be left behind.** A reading was cancelled by
  whatever the engine did next, so its answer never arrived and the page went on showing an
  older level — a screen restored to 75% still reading as dimmed, for instance. A reading
  changes nothing and is now never cancelled.
- **A monitor that dropped a single DDC/CI query was treated as having no answer.** Monitors
  answer over a slow serial link and miss one now and then, so a reading is retried before it
  counts as a refusal — which also stops a display being needlessly written off to the overlay.
- **Launching Dimly while it was already running did nothing.** It is meant to bring the
  window back, which is what a desktop or Start-menu shortcut does when the app is already in
  the tray. The running copy was signalled by a broadcast, and broadcasts are not delivered to
  the kind of window it listens on — never shown, and parked off-screen. The second copy now
  finds that window by name and posts to it directly.
- **The "Dimly is still running" hint never appeared.** It hung off the window's
  VisibleChanged event, which is not raised for a hide made from inside the closing event, so
  the one message explaining where the app went was never shown.
- **A rescan of the displays could outlive the page that asked for it.** The button made a
  fresh timer each time and left it behind; one started just before the window closed would
  still fire, on a page that no longer existed.
- **Two deadlines were measured from zero rather than from the clock**, which is not a reading
  from it. Windows' tick count turns negative after 24.9 days of uptime, and for that whole
  stretch "now minus zero" reads as negative - so a display that refused its restore would
  never have been offered it again, and the levels on the Displays page would have stopped
  updating. Both now start from the clock and wrap with it.
- **A maximised window was mistaken for a fullscreen one**, which made *Never dim over a
  fullscreen app* block dimming indefinitely. A maximised window overhangs its monitor by the
  invisible resize border, and when the taskbar is set to auto-hide the work area is the whole
  monitor — so an ordinary maximised browser matched the old test exactly. Dimly now tells the
  two apart by window placement and frame.
- The away-brightness gauge drew the `%` hard against the number, which collided for narrow
  readings such as `1%`, and sat directly beneath the card heading with no space between them.

### Known issues

- Some antivirus engines flag the unsigned executable. On VirusTotal it scores 3 of 60, and all
  three are machine-learning verdicts rather than signatures — Microsoft's `Wacatac.B!ml`,
  Trapmine's `Suspicious.low.ml.score`, and SecureAge, an ML-only engine. Every signature-based
  engine returns clean. See **Windows says it does not recognise this app** in the README.

## 1.0

First release.
