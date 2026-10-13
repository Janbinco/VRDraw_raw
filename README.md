# Drawing

A standalone Unity application for hand-tracked drawing with Meta Quest.

## Open and run

Requirements:

- Unity `6000.3.5f1` with Android Build Support.
- A Meta Quest device with hand tracking enabled, connected through Quest Link for Editor testing or configured as an Android build target.

Clone or download this repository, open its root folder in Unity Hub, then open `Assets/Scenes/DrawingScene.unity` and press Play. The scene is already the only enabled Build Settings scene. Unity restores package dependencies from `Packages/manifest.json`; generated `Library`, `Temp`, `Logs`, IDE files and local settings are intentionally not included in Git.

## Student flow

1. Launch `Assets/Scenes/DrawingScene.unity` on the Meta XR device.
2. Hold the **left index pinch** to draw (clutch).
3. Move the **right index fingertip** to create the stroke.
4. Release the left pinch to stop the current stroke.
5. Hold the **left middle-finger pinch** to erase around the right index fingertip. Erase takes priority if both left-hand pinches overlap.
6. Hold the **right controller B button for 2 seconds** (or press `Esc` in the Editor) to finish and save automatically.

There are no color, brush, part, erase, transform, or help panels during drawing. The brush is fixed to red at 80% opacity and 0.008 m width, while Sketch (Part 0) is fixed blue at 90% opacity. The background is fixed light bright purple (`#C9A6FF`).

## Identity and configuration

Defaults are in `Assets/Resources/DrawingConfig.json`. Identity can also be supplied without showing a setup panel:

- Desktop command line: `--participant P001 --condition clutch --demo drawing`
- URL query (for a future browser host): `?participant=P001&condition=clutch&demo=drawing`

Participant and condition IDs are supplied through the configuration, command line, or URL query before the session starts.

The public source repository intentionally contains no participant recordings, previous drawings, recovery scenes, imported samples, or nested Git history.

## Crash-safe data

In the Unity Editor, every session is written incrementally under the project:

`Assets/Data/<local timestamp>/`

For example: `Assets/Data/2026-10-12_23-30-00-123/`. Folder names use the computer's local time with milliseconds and no random suffix. Event timestamps remain UTC. `Assets/Data` stays excluded from Git.

Installed players, including Quest, have no writable project `Assets` folder, so they continue to use `Application.persistentDataPath/DrawingSessions/<local timestamp>/`.

Event batches are flushed on a single background task approximately every 0.25 seconds; clutch/erase transitions request the next batch immediately. Drawing snapshots are requested every 2 seconds. Only pose/geometry copying occurs on the drawing thread; event serialization, drawing export and disk writes run in the background. Pending snapshots are combined when storage is busy. Pause and normal shutdown wait for pending saves. Previously flushed events survive a process crash, but a crash or storage failure may lose pending data; there is no guaranteed maximum loss interval. Snapshot files are replaced atomically when supported.

## Sampling and performance

Version `0.1.2` uses configuration `drawing-config-3`. Drawing checks the current fingertip every rendered frame, after the hand SDK updates. A new visible point still requires 1 mm of movement; there is no time-based drawing throttle or synthetic interpolation. Invalid tracking ends the current segment instead of connecting across a tracking gap.

The app requests 90 Hz when the XR runtime advertises support (about 11.1 ms per frame, versus 13.9 ms at 72 Hz), without lowering a higher current rate. Quest Link/runtime settings can still determine the actual rate; a request is not a guarantee. `telemetryRateHz` controls ordinary telemetry only, not drawing.

To check a test run, compare `stroke_point.sampleIntervalSeconds`, `slow_frame.frameDeltaSeconds` and `autosave_complete` in `events.jsonl`. Events include `snapshotCopyMs` (main-thread copying), `saveWorkerMs` (last background write/export), `saveInProgress` and the observed `displayRefreshRateHz`. These distinguish frame stalls from normal point-distance filtering; background save overlap alone does not establish causation.

Files:

- `events.jsonl`: raw time-series events, stroke samples, clutch and erase changes, frame telemetry, controller pose/buttons, audio state and DSP time.
- `metadata.json`: participant/demo/condition IDs, versions, device/hands, brush/eraser/background config, XR refresh rate and estimated audio-buffer latency.
- `session.json`: recovery/finalization status and counts.
- `drawing.csv` and `drawing.json`: final polyline drawing.
- `drawing.obj`: portable 3D polyline model.
- `preview.png`: final XR framebuffer screenshot.

For a Quest build using the configured package ID, pull completed sessions with:

`adb pull /sdcard/Android/data/edu.handmade.drawing/files/DrawingSessions ./DrawingSessions`

## Important platform note

This is a native Unity + Meta XR application, not a WebXR site. It finishes and saves without displaying an additional post-session panel.

## Repository contents

Commit only these source-controlled items:

- `.gitattributes`, `.gitignore`, `README.md`
- `Assets/`
- `Packages/`
- `ProjectSettings/`

Upload this project folder, not its parent folder. Do not commit generated folders, builds, or experiment/session data; `.gitignore` excludes them. Keep the asset `.meta` files and `Packages/packages-lock.json` so references and dependencies remain reproducible. Use Git/GitHub Desktop for publishing; browser uploads and manually created archives do not apply `.gitignore` automatically, so omit `Assets/Data` and `Assets/Data.meta` yourself in those cases.

Before publishing, run **Drawing > Validate Project** in Unity or invoke `DrawingProjectValidation.ValidateBatch` in batch mode. Unity recreates local caches when the project is next opened.
