# Drawing

Hand-tracked VR drawing with Meta Quest. No tool panels.

## Run

Requires **Unity 6000.3.5f1** and a **Meta Quest connected through Quest Link**, with hand tracking enabled.

```bash
git clone https://github.com/Janbinco/VRDraw_raw.git
```

1. Add the downloaded `VRDraw_raw` folder to Unity Hub and open it.
2. Wait for Unity to download dependencies and finish importing.
3. Open `Assets/Scenes/DrawingScene.unity`.
4. Connect the headset and click **Play**.

## Draw

- **Draw:** hold your left thumb and index fingertip together, then move your right index fingertip.
- **Pause:** release the left index pinch.
- **Erase:** hold your left thumb and middle fingertip together, then move your right index fingertip over the drawing.

Quest frame drops can make curves appear less smooth.

## Save

Drawing and session data are saved automatically while drawing. Clicking **Stop** in Unity saves any remaining data.

Each session is stored in a timestamp-named folder:

```text
Assets/Data/<timestamp>/
```

- `drawing.csv` / `drawing.json`: drawing points.
- `drawing.obj`: 3D drawing lines.
- `events.jsonl`: movement and gesture records.
- `metadata.json` / `session.json`: session information.

Recordings stay local and are excluded from Git.
