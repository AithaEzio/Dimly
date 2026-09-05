# Changelog

## 1.1 — 4 September 2026

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
- **Smart restore**, on by default. When Windows switches the screen off on its own timeout,
  the display that comes back is not the display that went away: handles taken beforehand are
  dead, and a monitor still powering up answers with values it contradicts a second later.

  Windows reports the screen as on the moment it asks for it, which is several seconds before
  a monitor has actually left power save. So Smart restore waits: the monitors are left alone
  to settle, then asked - gently, until they answer - and only then are the displays looked
  over and the brightness handed back. The dim is held through all of it, even if you are
  already moving the mouse, so the brightness returns once and correctly rather than being
  written into a monitor that is still dark. A display that never answers is given up on after
  fifteen seconds rather than left holding the screen dark.

  It only ever runs after the screen has been switched off; every other wake follows the
  ordinary path, switch on or not. The Displays page reports what Windows is actually set to
  do, since a machine never set to switch the screen off will never see this run.
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

- **Half the size of 1.0, despite everything above**: 374 KB to 194 KB. The icon was embedded
  twice — once as the Win32 resource Windows uses for the file, and again as a managed
  resource for the app to read — so Dimly now reads the Win32 copy it already carries. The
  large icon frames are also PNG-compressed, which halved the icon itself; a single 128px
  uncompressed frame was 66 KB.
- Activity is read through **Raw Input instead of a low-level keyboard hook**.
  `SetWindowsHookEx(WH_KEYBOARD_LL)` is the API every keylogger reaches for, and installing one
  across the machine is the most heuristic-tripping thing a small unsigned utility can do.
  Dimly now has Windows post notifications to one private window, and only ever looks at which
  kind of device sent them — never at what was typed.
- **Re-establishing the displays is around sixty times faster** - 62 ms down to under 1 ms per
  pass on a single monitor, and it no longer grows with the number of them. Nearly all of that
  was one question put to each monitor over DDC/CI: what scale do you use? It is the same
  monitor, so it is the same scale, and the answer was already known. Whether the new handle
  works is settled by the write that follows, which is read back and retried until the display
  agrees - a question beforehand proved nothing that write does not. This matters because the
  check now runs every single time the screen switches off.
- **A rescan keeps the displays it already has** when the same monitors are still attached in
  the same places. Nothing is torn down and rebuilt, so the overlay windows survive, every
  per-display setting and captured level survives, and the Displays page is not redrawn under
  the user. Plugging a monitor in or out still rebuilds everything, and the **Rescan** button
  always does a full re-probe so a display that had gone quiet can be found again.
- **The scrollbar is drawn in the theme.** Windows' own bar cannot be coloured, and one grey
  strip in a window where every other control is drawn by hand looked like a mistake. The panel
  behind it still does the scrolling - wheel, keyboard and touchpad all behave exactly as
  before - and its bar is simply kept out of sight.
- **The mouse wheel no longer moves a slider.** Scrolling with the pointer over one changed
  the brightness when all that was meant was to scroll the page - and these pages do scroll.
  The wheel now reaches the page behind, as it should. Arrow keys, Home and End still move a
  slider for anyone working without a mouse.
- **Dimly hands its memory back to Windows once you have gone.** A tray application spends
  nearly all of its life asleep, holding pages it touched on the way in and will not touch
  again until somebody comes back — and Windows only reclaims those under memory pressure, so
  the figure in Task Manager climbs all evening and stays climbed. They are now given up a few
  seconds after the screen dims, and again the moment Windows switches the screen off: the two
  moments when nothing can possibly be waiting on them. Measured over a ten-minute away
  period, the working set holds around 1–3 MB instead of climbing past 30 MB, and the pages
  come back from memory well inside the time a single brightness write already takes.
- **The window only repaints when something has actually changed.** The status line was redrawn
  once a second whether or not it still said the same thing — and "Paused", "Dimmed" and "Media
  playing" sit still for minutes at a time. The live brightness readouts did the same on every
  reading. Over twenty-five dim-and-restore cycles with the window open, that halved the memory
  the app accumulates, with nothing different on screen.
- Closing the window explains, once, that Dimly is still in the tray. Silently vanishing is
  the most common way a tray app loses a user.
- Rescanning displays says that it is scanning, and a button that cannot be pressed now looks
  like one.
- The rules are worded consistently, and the settings window is taller to fit them.

### Fixed

- **The scrollbar ignored the mouse wheel.** Scrolling with the pointer anywhere on the page
  worked; scrolling with it over the scrollbar itself did nothing at all, because the panel
  that does the scrolling is not the bar's parent and the message died there. The one strip of
  the window a person is most likely to aim at is now the one it was least likely to work on.
- **A window dragged off the edge of the desktop could not be brought back.** Dimly's window
  has no title bar and is dragged by its own caption strip, so one pushed off-screen — or left
  on a monitor that was later unplugged — was unreachable by any means Windows offers, and the
  app looked as though it would no longer open. Opening it now puts a window with nothing left
  to grab hold of back in the middle of the screen.
- **Quitting or signing out during a display check left the screens dimmed.** A check in
  progress holds every brightness write back, and on the way out there is no later to wait
  for. The hold is now let go of before the final restore, so the displays are handed back
  whatever the app was in the middle of.
- **A display that failed on the way in could leave Dimly running with its clock stopped** —
  in the tray, apparently fine, never dimming, and with nothing at all to say why. The first
  scan of the hardware can no longer stop the engine from starting.
- **A monitor that dropped a reading made the live brightness flicker.** Monitors answer over
  a slow serial link and miss queries — one here answers about four times in ten — and the
  Displays page blanked the reading to "not reported" on every single miss, then showed the
  number again a second and a half later. The last thing a display said now stands until it
  has gone properly quiet.
- **Smart restore could hold the screen dark for the rest of the session.** Windows announces a
  display change twice the instant the screen comes back on - and that announcement made Dimly
  rebuild its display list, which cancelled the check that was in progress. The cancelled path
  never let go of the hold it had placed on the screen, and that hold stops every decision the
  app makes: the brightness never came back, the app said "Checking displays" forever, and
  anything done afterwards - including **Rescan** - re-applied the stale dimmed state, which is
  the brighten-then-dim loop. The hold is now released whatever happens to the check, routine
  work no longer interrupts one, nothing is written to a monitor that is still waking, and a
  check that somehow outlives its own timeout is let go of by the next tick.
- **Both monitors were written off as uncontrollable by a single screen timeout.** Windows dims
  every monitor and then switches it off, and through that a monitor accepts nothing. Dimly
  read those refusals as "this display has no working brightness control", gave up on it, and
  covered it with a black overlay for the rest of the session - so the screen came back dark
  with the backlight sitting at full, and only **Rescan** undid it. Refusals from a screen
  Windows is powering down are no longer treated as evidence about the display, and Dimly now
  leaves the displays completely alone from the moment Windows says it is switching them off
  until it says they are back.
- **A monitor could be lost for good once the screen had been switched off and on.** Windows
  announces a display change four times around a single screen timeout - twice on the way into
  the power-off and twice on the way out - and every one of them invalidates the handle that
  identifies a display. Dimly captured that handle once and asked through it forever after, so
  from the first power cycle onward it could not read or write that monitor at all: the screen
  came back at the away level and stayed there, restores failed silently, and only pressing
  **Rescan** brought it back. Handles are now re-taken against the display as Windows has just
  enumerated it.
- **Rebuilding the display list while the screen was dark wrote every monitor off.** Those same
  announcements arrive while the monitors are asleep, and a monitor asleep answers nothing - so
  each was judged uncontrollable and covered with a black overlay instead. A change that
  arrives while the screen is off is now acted on when it comes back, which is the first moment
  the answer means anything.
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
