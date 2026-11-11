# Changelog

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
