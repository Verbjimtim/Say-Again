# Changelog

## v0.2.1

- The **AFK** button has moved to the Say Again page, in the space under ▼ (it was on the VSCS Setup page).
- AFK marks the Say Again list: a thin line labelled **Away from Keyboard 0402Z** where AFK was turned on and **Back at Keyboard 0431Z** where it was turned off (UTC), so you can see which calls came in while you were away. The lines can't be selected, and ▲/▼ and the replay keys step straight past them. A line shows only with a call above and below it, so it goes once the calls next to it expire.
- **Duck Discord** (Settings → Media, off by default): lowers Discord's volume while the radio is busy, the same times Pause Media pauses (receiving, transmitting, a hotline/coldline open, a replay or a handoff tone playing). **Duck By** sets how much: 10-100% in tens (50% by default; 100% silences it). Discord comes back after the **Resume Media** delay, or 2 seconds when that's set to Never. It works on Discord, Discord PTB and Canary through the Windows volume mixer, and puts the volume back when vatSys closes.
- The status lines under Settings now show one fact per line (receivers, your Tx, coord, open lines) and wrap instead of running off the right edge of the VSCS.

## v0.2.0

- **AFK** button on the VSCS Setup page, right of Handoff Alert. While it's on:
  - Calls and coord made while you're away aren't deleted; they're kept until the usual Keep Calls / Keep Coord time after you turn AFK off. Calls from before AFK expire as normal, the 200-call cap still applies, and disconnecting still wipes everything.
  - vatSys's **Request Break** (Tools menu) is turned on, as soon as you're connected as ATC if you aren't yet. Turning AFK off turns Request Break off; turning Request Break off yourself turns AFK off.
  - AFK is always off when vatSys starts.
- **Handoff Alert**, merged in from Matt's Handoff Alert plugin (with his blessing; thanks Matt). Off by default; turn it on with the **Handoff Alert** button on the VSCS Setup page (where Matt's plugin had it) or in Settings → **Alerts**.
  - A handoff to you plays a rising two-tone, repeated until you accept it. **Repeat** (Alerts tab) sets how often: every 3-10 seconds (5 by default) or Off (once).
  - When someone accepts your handoff you hear a falling two-tone; if they refuse it, a flat tone.
  - Tones play through vatSys's alert output, at vatSys's alert volume (Windows' default output if that isn't available). They're made in memory; nothing is read from or written to disk.
  - With Pause Media on, a handoff tone pauses your media too.
  - Flight plans and tracks are now read on vatSys's UI thread, not a background thread.
  - If Matt's separate Handoff Alert plugin is still installed, Say Again says so once (you'd hear every tone twice). Nothing is switched off.
  - Settings are now in three pages: **Replay**, **Media** and **Alerts**.
- Say Again's VSCS buttons (Say Again, Settings, ▲, ▶, ▼) match vatSys's own: the VSCS background colour (#8C9696 in the Australia profile) when unpressed, and the darker pressed colour (#829292) only while pressed or active. Spotted by Matt.
- Your own transmissions are always recorded: everything from your mic while PTT is held on a transmit frequency goes in the list, however quiet or short. (Before, very quiet transmissions were dropped as silence and presses under 250 ms were dropped.)
- The Settings status line counts your own transmissions ("your Tx: kept/presses"), noting presses with no transmit frequency, or "mic not hooked" if the mic can't be listened to.
- Pause Media also pauses while a Say Again replay is playing (not while it's paused).
- **Resume Media** setting: how long after the radio goes quiet paused media resumes, 1-5 seconds (2 by default), or **Never** (it stays paused for you to resume yourself).
- Settings split into pages, **Replay** (Keep Calls, Keep Coord, Volume, Speed, Stop On New, Highlight Selected Pilot) and **Media** (Pause Media, Resume Media), with each slider's value beside it on one line. The status lines sit at the bottom and no longer spill off.

## v0.1.6

- **Pause Media** (Settings, off by default): pauses Spotify, browser tabs and anything else in Windows' media controls while the radio is busy (receiving, transmitting or a hotline/coldline open), and resumes them 2 seconds after it goes quiet. Only what was playing is paused, and only that is resumed, so anything you started or stopped yourself in the meantime is left alone. Say Again replays don't count as busy.
- Fixed the Say Again list showing no rows until clicked. Seen live: with few calls (all fitting on the page), the list's scroll origin ended up a row above its top and Windows drew nothing; it couldn't be scrolled back because there was nothing to scroll. The list now rebuilds itself when that happens. It also no longer sends Windows a tiny scroll correction every second (the header's height and the first row's position differ by a pixel or two, which it was treating as being off).

## v0.1.5

- Silent line calls are logged: a hotline/coldline that opened with nobody speaking gets its own row (who it was with, Hotline/Coldline, "silent" as its length). There's nothing to play, so the coord key and up/down skip it. Unanswered ringing isn't logged.
- Line calls list who the call was with (the controller on the line, even if they never spoke), not "You". "You" appears only when nobody else is known.

## v0.1.4

- Fixed coord recording only your side of a line call after vatSys's coord connection was rebuilt (e.g. by the MumbleReconnect plugin): the plugin stayed on the old connection and heard no other controllers, and coord replays had nowhere to play. It now follows the connection when it changes.
- Line calls use vatSys's own hotline/coldline state: a call is one clip from when the line opens until it closes (ringing doesn't count), pauses included at their real length. Line audio still arriving is the backup. Calls with pauses over 4 s used to be split into several clips. Very long calls split at 5 minutes.
- The Freq column (and the VSCS status button) shows **Hotline** or **Coldline** for a line call, and the clip lists who the line was with even if they never spoke.
- Fixed the other controller missing from line calls when the frequency was busy: quiet line speech (vatSys sends the raw mic) overlapping frequency traffic was taken for vatSys's faint frequency loopback. Loopback is now judged against the overlapping frequency call's own level (the real loopback is 1.4% of it), using speech level rather than an average that pauses drag down. Quieter line speech is picked up too.
- A line with no controller attached in vatSys (seen on a coldline) falls back to the line's name for the clip's callsign.
- Settings status shows the lines vatSys reports (e.g. `ML-ELW_CTR hotline open`).

## v0.1.3

- Selecting a pilot no longer filters the Say Again list (an empty list was confusing): their calls are highlighted instead, and every call stays listed. **Highlight Selected Pilot** in Settings turns the highlight off.

## v0.1.1

- Replay Last and Replay Coord now always play the most recent call of their kind (your own transmissions skipped); pressing again repeats it from the start instead of stepping back. Older calls are on the Say Again page.
- Fixed the Say Again list sometimes showing no calls: it could end up scrolled out of range. It now scrolls to fixed positions and corrects itself on every refresh.

## v0.1.0

First public release.

- Keeps the last 1-5 minutes of received AFV frequency calls in memory, one clip per transmission per frequency, tagged with callsign and frequency (blocked calls marked).
- Keeps the last 1-10 minutes of hotline/coldline coordination: each line call is one clip with both sides mixed as you heard them.
- Your own voice too: real frequency transmissions (PTT with a frequency set to Transmit) and your side of open lines.
- Frequency and coord kept apart: line copies of PTT transmissions and faint frequency audio on lines are dropped.
- **Replay Last** and **Replay Coord** keys in vatSys's Keyboard settings; tap again to step back.
- **Say Again** page on the VSCS (fifth menu button under Group): browse calls, play/pause, next/previous, progress bar, filter to the selected aircraft, Last Tx call-out, settings (retention, volume, speed 1-2x, stop on new call).
- Recorded at 8 kHz, compressed in memory (about 1 KB/s). Never written to disk; wiped on expiry, disconnect and shutdown.
