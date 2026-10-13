# Drawing

A Unity application for drawing in VR with Meta Quest hand tracking. Hold a left-hand pinch to draw with your right index fingertip, or use a left middle-finger pinch to erase. The drawing space has no tool panels, and session data is saved automatically.

This repository contains the Unity source project, not a standalone executable or a browser/WebXR application.

## Requirements

- **Unity 6000.3.5f1**, installed through Unity Hub.
- A **Meta Quest headset** with hand tracking enabled.
- For Editor testing: a PC configured for **Meta Quest Link**, with hand tracking available to the Unity XR session.
- An internet connection for the first import and package downloads.
- For building a standalone Quest app: Unity **Android Build Support**, including the Android SDK, NDK, and OpenJDK.

Meta XR, OpenXR, and Input System dependencies are declared in `Packages/manifest.json` and `Packages/packages-lock.json`. Unity restores them during import. Do not copy another project's `Library` folder or install a second copy of the Meta SDK under `Assets`.

## Quick start

### 1. Download the project

For the first download, use `git clone`:

```bash
git clone https://github.com/Janbinco/VRDraw_raw.git
cd VRDraw_raw
```

Alternatively, download and extract the repository ZIP from GitHub.

### 2. Open it in Unity

1. In Unity Hub, add the **VRDraw_raw folder** containing `Assets`, `Packages`, and `ProjectSettings`.
2. Open it with **Unity 6000.3.5f1**.
3. Wait for package downloads, asset import, and script compilation to finish. The first import can take several minutes.
4. Open `Assets/Scenes/DrawingScene.unity` if it is not already open. It is the project's only enabled build scene.

No machine-specific path changes are required. Unity recreates its local caches automatically.

### 3. Start drawing

1. Connect the headset through Quest Link and make sure hand tracking is available.
2. Click **Play** in Unity.
3. Put on the headset and keep both hands visible to its tracking cameras.
4. Hold the left index-finger pinch and move your right index fingertip to draw.

### Updating an existing clone

Stop Play Mode, save your work, close Unity, and run this command from the project folder:

```bash
git pull --ff-only
```

Then reopen the project. `git pull` downloads updates; it does not launch the application. A ZIP download is not a Git clone and cannot be updated with `git pull`.

## Controls

| Action | Input |
| --- | --- |
| Draw | Hold the **left thumb and index fingertip together**, then move the **right index fingertip**. |
| Stop drawing | Release the left index pinch. |
| Erase | Hold the **left thumb and middle fingertip together**, then move the right index fingertip over existing strokes. |
| Finish in the Editor | Press **Esc** while the Game view has keyboard focus. |
| Finish using a controller | Hold the **right controller B button for 2 seconds**, when controller input is available. |

Erase takes priority if the left index and middle pinches overlap. The right fingertip supplies the drawing and erasing position; it does not need to pinch.

Use Esc or B to follow the normal finish flow, which requests a screenshot and saves the session. Clicking Unity's Stop button directly does not run the screenshot flow. A standalone player exits after finishing; in the Editor, stop Play Mode before starting another session.

## Configuration

Edit `Assets/Resources/DrawingConfig.json` **before starting a session**. No in-VR configuration panel is shown.

Current defaults:

- Light bright purple background (`#C9A6FF`).
- Fixed red drawing strokes, 80% opacity and 8 mm width.
- Blue Sketch (Part 0) appearance at 90% opacity; the default drawing session starts in Part 1.
- 1 mm minimum distance between added drawing points.
- 5 cm eraser radius.
- Drawing snapshots every 2 seconds.
- A preferred XR refresh rate of 90 Hz, when supported by the runtime.

Set `participantId`, `demoId`, and `conditionId` in the configuration for an experiment. The participant ID is blank by default. Desktop players also accept:

```text
--participant P001 --condition clutch --demo drawing
```

Do not commit real participant identifiers or recordings to a public repository.

## Saved data

### Location

In the **Unity Editor**, recordings are saved inside the project:

```text
Assets/Data/<local timestamp>/
```

Example folder name: `2026-10-13_14-30-00-123`. Each session gets a separate folder named using local time with milliseconds and no random suffix. Event timestamps inside the files use UTC.

In an **installed player**, including a Quest build, recordings are saved under:

```text
Application.persistentDataPath/DrawingSessions/<local timestamp>/
```

The Unity Console prints the resolved location as `[Drawing] Session data: ...`. Paths are determined at runtime and do not depend on the original developer's computer.

For a Quest build with the configured package ID, retrieve sessions using Android debugging tools:

```bash
adb pull /sdcard/Android/data/edu.handmade.drawing/files/DrawingSessions ./DrawingSessions
```

### Files

| File | Contents |
| --- | --- |
| `drawing.json` / `drawing.csv` | Current or final drawing geometry, grouped by stroke and part. |
| `drawing.obj` | 3D polyline export; this is not a solid mesh. |
| `events.jsonl` | Raw fingertip samples, timing and velocity, clutch/erase events, controller pose/buttons, frame diagnostics, and audio state. |
| `metadata.json` | Participant/demo/condition IDs, software/configuration versions, device and hand information, drawing settings, and audio-buffer latency estimates. |
| `session.json` | Session status, finalization reason, counts, and output filenames. |
| `preview.png` | Screenshot, when successfully captured through the normal finish flow. It may be absent after stopping Play Mode directly or an unexpected exit. |

### Autosave and recovery

Event serialization, drawing export, and disk writes run on a single background task. Event batches are normally flushed about every 0.25 seconds, and drawing snapshots are requested every 2 seconds. The drawing thread copies geometry for each snapshot without waiting for the normal background write to finish.

Pause and normal shutdown wait for pending saves. Previously flushed data can remain available after an unexpected exit, but a crash, power loss, or storage failure can still lose pending data. There is no guaranteed maximum loss interval or automatic session-resume interface.

## Sampling and performance

Drawing checks the fingertip **every rendered frame**, after the hand SDK updates. A new visible point is added when movement exceeds the configured minimum distance. There is no fixed time-based drawing throttle or synthetic curve interpolation. Invalid tracking ends the current segment instead of connecting across a tracking gap.

The app requests a higher refresh rate up to the configured preference when the XR runtime advertises support, without lowering an already higher rate. A 90 Hz request is not a guarantee. `telemetryRateHz` controls ordinary diagnostic logging, not drawing-point sampling.

To investigate stalls, inspect `stroke_point.sampleIntervalSeconds`, `slow_frame.frameDeltaSeconds`, and `autosave_complete` in `events.jsonl`. The fields `snapshotCopyMs`, `saveWorkerMs`, and `saveInProgress` describe snapshot preparation and background-save activity. A reported display refresh rate of zero means it was unavailable. Audio latency is a buffer-based estimate, not a measured end-to-end acoustic latency.

## Troubleshooting

- **Hands are missing:** confirm Quest Link and hand tracking are available to the Editor, keep both hands visible, and check the Console for XR errors. A desktop mouse cannot replace tracked hands.
- **Drawing does not start:** open `DrawingScene`, wait for compilation, and hold the left index pinch while the right hand is tracked.
- **Scene references appear incorrect:** run **Drawing > Validate Project** in Unity. It checks the scene, hand components, configuration, and required references.
- **A screenshot is missing:** finish with Esc or the controller B-button hold instead of stopping Play Mode directly. Screenshot capture is not guaranteed after an unexpected exit.
- **The first import fails:** check the internet connection and Package Manager errors, and use the pinned Unity version before changing package versions.

## Repository contents

The repository includes `Assets`, `Packages`, `ProjectSettings`, this README, and Git configuration files. Keep asset `.meta` files and the package lock file: they preserve references and dependency resolution.

Generated caches, builds, local settings, and `Assets/Data` are excluded by `.gitignore`. Manual browser uploads and hand-made archives do not apply `.gitignore` automatically, so exclude recordings yourself when using those methods.

Current application version: **0.1.2**. Configuration version: **drawing-config-3**. Local project validation has passed with Unity 6000.3.5f1; first-time setup on a separate clean computer has not yet been verified.
