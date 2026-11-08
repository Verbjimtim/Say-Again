# Changelog

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
