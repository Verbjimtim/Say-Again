# Plan: bring Handoff Alert into Say Again

Draft for review with Matt. Source: [mattk04/vatsys-handoff-alert](https://github.com/mattk04/vatsys-handoff-alert) v1.1.0.

## Goal

Say Again plays Matt's handoff tones. It plays them in the same situations and with the same sounds as Handoff Alert v1.1.0:

- **Handoff in:** a rising two-tone, repeated until you accept it.
- **Your handoff accepted:** a falling two-tone.
- **Your handoff refused:** a flat tone.

The aims are to:

- add a settings page and keep the quick toggle on the VSCS Setup page;
- save the settings;
- make the code safe to run on vatSys's UI thread;
- add tests.

Handoff Alert users move over without getting double tones.

## Before we start

- [ ] **Licence.** Matt has agreed to the merge; still to get an explicit OK that his code is under MIT (Say Again's licence). A one-line message is enough.
- [x] **Credit.** Matt is in the README credits and the CHANGELOG. Copyright line: only if he wants it.
- [ ] **How we work.** Either Matt becomes a collaborator on Verbjimtim/Say-Again and works through PRs, or he sends changes and they get folded in.
- [x] **Decisions:** see "Decisions" below.

## Design

### 1. `HandoffTracker` (new, `src/AFVReplay/Handoff/HandoffTracker.cs`)

This is plain logic with no vatSys calls, so it can be unit tested.

- **Input:** `Update(IEnumerable<TrackSnapshot> tracks, DateTime now)`, where each `TrackSnapshot` is:
  - `Key`: the callsign, used only in memory;
  - `HandoffIn`: the track is in `HMIStates.HandoverIn`;
  - `HandoffOut`: the track is in `HMIStates.HandoverOut`;
  - `TrackedByMe`.
- **Output:** a list of events: `Incoming`, `Accepted`, `Refused` and `Repeat`.
- **Rules,** the same as v1.1.0:
  - **Incoming:** a track becomes `HandoffIn`.
  - **Repeat:** fires every *N* seconds while any track is still `HandoffIn`. It stops when none are left, or when the slider is set to Off.
  - **Accepted:** a track you had in `HandoffOut` is no longer tracked by you.
  - **Accepted, for a plan that disappears:** what happens when a plan you had handed off vanishes is still open (see decision 3).
  - **Refused:** comes from `NotifyRefused()`. vatSys has no handoff-refused event, so the refusal is detected from the error message, as Matt does (see §3).
- **Not handled yet:** having several handoffs in at once still gives a single repeat stream, as now.

### 2. `HandoffMonitor` (new, the vatSys side)

- **Thread:** reads `FDP2.GetFDRs` and `MMI.FindTrack` on the **UI thread**, using a WinForms timer every 250 ms. v1.1.0 does this from a `System.Timers.Timer` on the thread pool. vatSys's flight-plan and track lists aren't safe to read from there, which risks rare, random crashes.
- **Events:** polling is enough. We drop the per-FDR `PropertyChanged` subscriptions and the `FDRsChanged` resubscribe, so there's one way in and no double triggers.
- **Flow:** builds the snapshots, calls `HandoffTracker.Update`, and hands the events to the audio player.
- **Off:** when the setting is off, the timer still keeps the tracker's state current but plays nothing. Turning it on mid-handoff then alerts straight away, the same as v1.1.0.

### 3. Refused handoffs

- Subscribe to `Errors.ErrorsChanged`, as v1.1.0 does. A message starting `"Handoff refused by "` calls `tracker.NotifyRefused()`.
- This is fragile: it breaks if vatSys rewords the message. We'll note it in the code and test it against the current vatSys version.

### 4. `AlertPlayer` (ported from `HandoffAudioPlayer`)

- **vatSys alert output first:** `vatsys.Audio.Instance.PlayAlertSound(CachedSound, false)`. The reflection lookups move into `VatSysBridge` next to the existing ones, with checks at startup so a vatSys update fails clearly.
- **Fallback:** Windows default output through `SoundPlayer`, as now.
- **Sounds:** the generated tones stay as they are: 880→1175 Hz in, 1175→880 Hz accepted, 440/440 Hz refused, with harmonics.
- **Custom sounds:** `HandoffAlert.wav`, `HandoffAccepted.wav` and `HandoffRejected.wav` beside the DLL still override them.
- **Temp file:** v1.1.0 writes the generated tone to a temp `.wav` for a moment because `CachedSound` loads from a file. We'll check whether `CachedSound` can be built from memory. If it can't, we keep the temp file. It's harmless because it isn't radio audio, but it's deleted straight away.
- **Volume:** an Alert Volume slider, if we can scale samples before building the `CachedSound`.

### 5. Settings (`ReplaySettings`)

| Setting | Default | Notes |
|---|---|---|
| `HandoffAlert` | off | The main switch; the Setup-page button changes the same value |
| `HandoffAcceptedTone` | on | |
| `HandoffRefusedTone` | on | |
| `HandoffRepeatSeconds` | 5 | 3–10, plus Off (alert once) |
| `AlertVolumePercent` | 100 | Only if §4 allows it |

These are saved in `settings.xml` with the rest, so the switch no longer resets to off on every start.

### 6. UI

- **New Alerts tab:** Settings gets a third tab next to Replay and Media (`Tab("Alerts", 2)`). It holds:
  - toggles for Handoff Alert, Accepted Tone and Refused Tone;
  - the Repeat slider, shown as `Off`, `3 s` … `10 s`;
  - the Alert Volume slider.
- **Setup-page button:**
  - "Handoff Alert" on the VSCS **Setup** page, in the same place as v1.1.0 (row 1, menu column + 4).
  - It's drawn with Say Again's `VscsButton`, so it gets the right #829292 colour and bevel. It shows pressed while the alert is on.
  - It uses the existing `VscsLayout` and `VatSysBridge.IsVscsSetupPage`, and the injector timer `VscsInjector` already runs, rather than a second 50 ms poll.
  - Check it doesn't sit on top of anything on Setup across profiles, and when the frequency count pushes the menu column over.
- **Status line:** add a short `handoff alerts on/off` part to the status line.

### 7. Running alongside the old plugin

- At startup, if an assembly named `HandoffAlertPlugin` is loaded, Say Again's handoff alerts stay off. Its Setup button and the Alerts page then say "Handoff Alert plugin installed: remove it to use this".
- Matt archives his repo, with a README pointing to Say Again.

### 8. Privacy and logging

- Callsigns are only held in memory and are never logged. Diag messages say "handoff in", not "QFA123 handoff in".
- No radio audio is involved.

## Tests (`tests/AFVReplay.Tests`)

`HandoffTrackerFollowsHandoffs` uses a fake clock, the same approach as `MediaPausesWhileRadioBusy`. It covers:

- **Incoming:** fires once, repeats every 5 s, stops when accepted; a second incoming while one is pending doesn't start a second repeat stream.
- **Accepted:** an out-handoff that you then stop tracking fires; leaving `HandoffOut` while still tracking (withdrawn) doesn't.
- **Plan disappears:** follows whatever decision 3 settles.
- **Refused:** `NotifyRefused` fires; the error-message matcher accepts `"Handoff refused by ML-BIK_CTR"`; other errors are ignored.
- **Repeat Off:** alerts once only.
- **Old plugin installed:** detecting it keeps the alerts off.
- **Settings:** defaults and `Normalise` limits.

The offline preview (`--preview-vscs`) also captures the Alerts tab and the Setup page with the button.

## Steps

1. Licence and credit agreed (Matt).
2. `HandoffTracker` and its tests.
3. `AlertPlayer` and the `VatSysBridge` lookups for alert sound.
4. `HandoffMonitor` and the error hook, wired into `ReplayPlugin`.
5. Settings, the Alerts tab and the Setup-page button.
6. Detecting the old plugin.
7. README, CHANGELOG and credits.
8. Live test in vatSys with Matt, on Sweatbox or in a quiet session:
   - a handoff in, an accept, a refusal;
   - a handoff out that is accepted, then a handoff out that is refused;
   - with and without Handoff Alert v1.1.0 installed.
9. Release as v0.2.0, the first release with a new feature area.

## Decisions (agreed 2026-11-21)

Matt agreed to the merge and to this plan. These decisions override anything earlier in this doc:

1. **Pause Media:** a handoff tone **does** pause media while it plays, like radio traffic.
2. **Name:** stays Say Again.
3. **Disappearing plan:** a plan you were handing off that disappears plays **nothing**.
4. **Repeat:** a Repeat slider in the Alerts tab: Off, or every 3-10 s, 5 s by default.
5. **Switch:** **both** the Setup-page button (in Matt's spot and size) and the Alerts tab, sharing one setting. Off by default.
6. **Volume:** **no** Alert Volume slider. The tones follow vatSys's alert volume.
7. **Tone toggles:** **no** separate Accepted and Refused toggles. Handoff Alert switches all three tones.
8. **Custom sounds:** **tones only**. No `.wav` files are read. The tones are built in memory as a vatSys `CachedSound`, so no temp file is needed either.
9. **Old plugin:** detect it and **warn once**. Nothing in Say Again is switched off.
10. **Status line:** no handoff part, so the status stays two lines and doesn't clip.
11. **Release:** one **v0.2.0** with everything unreleased since v0.1.6.

## Status

- Built: `Handoff/HandoffTracker.cs`, `Handoff/HandoffMonitor.cs`, `Handoff/AlertPlayer.cs`, the bridge additions, settings, the Alerts tab and the Setup-page button.
- 4 new tests; all 42 pass.
- Still to do: the live test in vatSys with Matt (step 8), then the v0.2.0 release.

## Not using vatSysToolbox

The toolbox doesn't make this merge easier:

- **Messaging:** its main feature is plugin-to-plugin messaging, but after the merge Handoff Alert lives inside Say Again's own DLL, so nothing needs to talk across plugins.
- **No overlap:** it doesn't touch the parts this work needs, which are flight plans and tracks, vatSys alert audio and VSCS buttons.
- **UI:** its UI helpers are WPF, and Say Again is WinForms built from vatSys's controls.
- **Cost:** using it means a GitHub token for every build, ILRepack, and a much larger DLL.

It's worth revisiting only for the separate idea of Eurocat Menus buttons driving Say Again.
