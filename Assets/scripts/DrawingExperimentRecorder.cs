using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Stopwatch = System.Diagnostics.Stopwatch;
using UnityEngine;
using UnityEngine.XR;

[DefaultExecutionOrder(-700)]
[DisallowMultipleComponent]
public sealed class DrawingExperimentRecorder : MonoBehaviour
{
    [Serializable]
    private sealed class SessionMetadata
    {
        public string sessionId;
        public string participantId;
        public string demoId;
        public string conditionId;
        public string softwareVersion;
        public string configVersion;
        public string unityVersion;
        public string startedUtc;
        public string updatedUtc;
        public string deviceName;
        public string deviceModel;
        public string deviceType;
        public string operatingSystem;
        public string graphicsDevice;
        public string xrDevice;
        public string clutchHand = "left";
        public string eraseGesture = "left middle-finger pinch";
        public string eraseSource = "right index fingertip";
        public string drawingHand = "right";
        public string drawingSource = "right index fingertip";
        public float configuredStrokeWidthMeters;
        public float configuredMinPointDistanceMeters;
        public float requestedRefreshRateHz;
        public float configuredStrokeAlpha;
        public float configuredSketchAlpha;
        public float configuredEraseRadiusMeters;
        public string configuredBackgroundColor;
        public float displayRefreshRateHz;
        public int audioSampleRate;
        public int audioDspBufferLength;
        public int audioDspBufferCount;
        public float estimatedAudioBufferLatencyMs;
        public string dataFormat = "drawing-session-v1";
    }

    [Serializable]
    private sealed class SessionSnapshot
    {
        public SessionMetadata metadata;
        public bool finalized;
        public string finalizeReason;
        public int strokeCount;
        public int strokePointCount;
        public int eventCount;
        public int renderedSegmentCount;
        public int renderedPointCount;
        public string eventsFile = "events.jsonl";
        public string drawingCsv = "drawing.csv";
        public string drawingJson = "drawing.json";
        public string drawingModel = "drawing.obj";
        public string screenshot = "preview.png";
    }

    [Serializable]
    private sealed class LogEvent
    {
        public string type;
        public string utc;
        public double sessionTime;
        public int frame;
        public int strokeId;
        public bool clutchHeld;
        public bool eraseHeld;
        public bool leftHandTracked;
        public bool leftHandHighConfidence;
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 velocity;
        public float speed;
        public float frameDeltaSeconds;
        public float fps;
        public double sampleIntervalSeconds;
        public bool rightHandTracked;
        public bool saveInProgress;
        public double snapshotCopyMs;
        public double saveWorkerMs;
        public float displayRefreshRateHz;
        public int pendingEventCount;
        public Vector3 rightControllerLocalPosition;
        public Quaternion rightControllerLocalRotation;
        public int rightControllerButtons;
        public double audioDspTime;
        public string audioSources;
        public string note;
    }

    [Serializable]
    private sealed class DrawingFile
    {
        public string format = "drawing-v1";
        public string exportedUtc;
        public StrokeFile[] strokes;
    }

    [Serializable]
    private sealed class StrokeFile
    {
        public int strokeIndex;
        public int partId;
        public string source;
        public Vector3[] points;
    }

    public string SessionDirectory { get; private set; }
    public string ScreenshotPath => string.IsNullOrEmpty(SessionDirectory)
        ? string.Empty
        : Path.Combine(SessionDirectory, "preview.png");
    public string ParticipantId => config?.participantId ?? string.Empty;
    public string ConditionId => config?.conditionId ?? string.Empty;
    public string DemoId => config?.demoId ?? string.Empty;
    public bool Finalized => finalized;

    private DrawingSessionConfig config;
    private FingerTrajectoryRenderer trajectoryRenderer;
    private DrawingClutchController clutchController;
    private SessionMetadata metadata;
    private FileStream eventStream;
    private double sessionStartedRealtime;
    private float nextTelemetryTime;
    private float nextAutoSaveTime;
    private float nextAudioSampleTime;
    private int strokeId;
    private int strokePointCount;
    private int eventCount;
    private bool hasPreviousStrokePoint;
    private Vector3 previousStrokePosition;
    private double previousStrokeTime;
    private bool finalized;
    private string finalizeReason = "";
    private bool closing;
    // Only the worker touches the open streams after initialization. Main-thread
    // objects/poses are copied before dispatch; at most one save can be in flight.
    private readonly List<LogEvent> pendingEvents = new List<LogEvent>(256);
    private Task<double> saveTask;
    private LogEvent[] savingEvents;
    private bool savingSnapshot;
    private bool snapshotRequested;
    private float nextEventFlushTime;
    private double lastSnapshotCopyMs;
    private double lastSaveWorkerMs;
    private float measuredRefreshRate;
    private bool saveErrorReported;
    private AudioSource[] cachedAudioSources;
    private float nextAudioDiscoveryTime;

    public void Initialize(
        DrawingSessionConfig sessionConfig,
        FingerTrajectoryRenderer rendererTarget,
        DrawingClutchController clutch)
    {
        config = sessionConfig;
        trajectoryRenderer = rendererTarget;
        clutchController = clutch;
        BeginSession();
    }

    private void BeginSession()
    {
        sessionStartedRealtime = Time.realtimeSinceStartupAsDouble;
#if UNITY_EDITOR
        string dataRoot = Path.Combine(Application.dataPath, "Data");
#else
        // Installed players (especially Quest) do not have a writable Assets folder.
        string dataRoot = Path.Combine(Application.persistentDataPath, "DrawingSessions");
#endif
        SessionDirectory = CreateSessionDirectory(dataRoot, DateTime.Now);
        string sessionId = Path.GetFileName(SessionDirectory);

        string eventPath = Path.Combine(SessionDirectory, "events.jsonl");
        eventStream = new FileStream(
            eventPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            64 * 1024,
            FileOptions.None);

        AudioSettings.GetDSPBufferSize(out int bufferLength, out int bufferCount);
        metadata = new SessionMetadata
        {
            sessionId = sessionId,
            participantId = config.participantId,
            demoId = config.demoId,
            conditionId = config.conditionId,
            softwareVersion = Application.version,
            configVersion = config.configVersion,
            unityVersion = Application.unityVersion,
            startedUtc = UtcNow(),
            updatedUtc = UtcNow(),
            deviceName = SystemInfo.deviceName,
            deviceModel = SystemInfo.deviceModel,
            deviceType = SystemInfo.deviceType.ToString(),
            operatingSystem = SystemInfo.operatingSystem,
            graphicsDevice = SystemInfo.graphicsDeviceName,
            xrDevice = XRSettings.loadedDeviceName,
            configuredStrokeWidthMeters = config.strokeWidthMeters,
            configuredMinPointDistanceMeters = config.minPointDistanceMeters,
            requestedRefreshRateHz = config.preferredRefreshRateHz,
            configuredStrokeAlpha = config.strokeAlpha,
            configuredSketchAlpha = config.sketchAlpha,
            configuredEraseRadiusMeters = config.eraseRadiusMeters,
            configuredBackgroundColor = ColorUtility.ToHtmlStringRGB(config.BackgroundColor),
            displayRefreshRateHz = ReadDisplayRefreshRate(),
            audioSampleRate = AudioSettings.outputSampleRate,
            audioDspBufferLength = bufferLength,
            audioDspBufferCount = bufferCount,
            estimatedAudioBufferLatencyMs = AudioSettings.outputSampleRate > 0
                ? 1000f * bufferLength * bufferCount / AudioSettings.outputSampleRate
                : 0f
        };
        measuredRefreshRate = metadata.displayRefreshRateHz;

        if (clutchController != null)
        {
            clutchController.ClutchChanged += HandleClutchChanged;
            clutchController.EraseChanged += HandleEraseChanged;
        }

        Log(new LogEvent { type = "session_start", note = "Autosave enabled" });
        SnapshotNow(false);
        nextTelemetryTime = Time.unscaledTime;
        nextAudioSampleTime = Time.unscaledTime;
        nextAutoSaveTime = Time.unscaledTime + config.autoSaveIntervalSeconds;
        Debug.Log($"[Drawing] Session data: {SessionDirectory}");
    }

    private static string CreateSessionDirectory(string dataRoot, DateTime localTime)
    {
        // Milliseconds keep the name timestamp-only, including rapid test restarts.
        string directory;
        do
        {
            directory = Path.Combine(dataRoot,
                localTime.ToString("yyyy-MM-dd_HH-mm-ss-fff", CultureInfo.InvariantCulture));
            localTime = localTime.AddMilliseconds(1);
        }
        while (Directory.Exists(directory) || File.Exists(directory));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private void LateUpdate()
    {
        if (eventStream == null || config == null || finalized || closing) return;

        ObserveSave(false);

        float telemetryInterval = 1f / config.telemetryRateHz;
        if (Time.unscaledTime >= nextTelemetryTime)
        {
            RecordFrameTelemetry(Time.unscaledTime >= nextAudioSampleTime);
            nextTelemetryTime = Time.unscaledTime + telemetryInterval;
        }

        if (clutchController != null && clutchController.ClutchHeld)
            RecordStrokePoint();

        // Record *every* slow frame, even between the lower-rate telemetry frames.
        float frameBudget = measuredRefreshRate > 0f ? 1f / measuredRefreshRate : 1f / 72f;
        if (Time.unscaledDeltaTime > frameBudget * 1.5f)
            Log(new LogEvent { type = "slow_frame", frameDeltaSeconds = Time.unscaledDeltaTime,
                clutchHeld = clutchController != null && clutchController.ClutchHeld });

        if (Time.unscaledTime >= nextAutoSaveTime)
        {
            SnapshotNow(false);
            nextAutoSaveTime = Time.unscaledTime + config.autoSaveIntervalSeconds;
        }
        if (snapshotRequested || Time.unscaledTime >= nextEventFlushTime)
            DispatchSave();
    }

    public void UpdateIdentity(string participantId, string conditionId)
    {
        config.participantId = (participantId ?? string.Empty).Trim();
        config.conditionId = (conditionId ?? string.Empty).Trim();
        metadata.participantId = config.participantId;
        metadata.conditionId = config.conditionId;
        Log(new LogEvent
        {
            type = "identity_update",
            note = $"participant={config.participantId};condition={config.conditionId}"
        });
        SnapshotNow(finalized);
    }

    public void FinalizeSession(string reason)
    {
        if (!finalized)
        {
            finalized = true;
            finalizeReason = string.IsNullOrWhiteSpace(reason) ? "completed" : reason;
            Log(new LogEvent { type = "session_end", note = finalizeReason });
        }

        SnapshotNow(true);
    }

    public void SnapshotNow(bool isFinal)
    {
        if (metadata == null || eventStream == null) return;
        snapshotRequested = true;
        DispatchSave();
        if (isFinal) DrainSaves();
    }

    private void DispatchSave()
    {
        if (!ObserveSave(false) || eventStream == null) return;
        if (!snapshotRequested && pendingEvents.Count == 0) return;
        var copyTimer = Stopwatch.StartNew();
        DrawingFile drawing = null;
        string metadataJson = null;
        string snapshotJson = null;
        savingSnapshot = snapshotRequested;
        if (savingSnapshot)
        {
            drawing = CaptureDrawing();
            metadata.updatedUtc = UtcNow();
            metadata.participantId = config.participantId;
            metadata.conditionId = config.conditionId;
            metadata.displayRefreshRateHz = measuredRefreshRate;
            int renderedPointCount = 0;
            foreach (var stroke in drawing.strokes) renderedPointCount += stroke.points.Length;
            var snapshot = new SessionSnapshot
            {
                metadata = metadata,
                finalized = finalized,
                finalizeReason = finalizeReason,
                strokeCount = strokeId,
                strokePointCount = strokePointCount,
                eventCount = eventCount,
                renderedSegmentCount = drawing.strokes.Length,
                renderedPointCount = renderedPointCount
            };

            // Small headers are frozen here; worker never observes live metadata.
            metadataJson = JsonUtility.ToJson(metadata, true);
            snapshotJson = JsonUtility.ToJson(snapshot, true);
            snapshotRequested = false;
        }
        lastSnapshotCopyMs = copyTimer.Elapsed.TotalMilliseconds;
        savingEvents = pendingEvents.ToArray();
        pendingEvents.Clear();
        var batch = savingEvents;
        string directory = SessionDirectory;
        saveTask = Task.Run(() =>
        {
            var timer = Stopwatch.StartNew();
            // Roll back a partially written event batch before retrying it.
            long eventStart = eventStream.Position;
            try
            {
                var lines = new StringBuilder();
                foreach (var item in batch) lines.AppendLine(JsonUtility.ToJson(item));
                byte[] bytes = Encoding.UTF8.GetBytes(lines.ToString());
                eventStream.Write(bytes, 0, bytes.Length);
                eventStream.Flush(true);
            }
            catch
            {
                eventStream.SetLength(eventStart);
                eventStream.Position = eventStart;
                throw;
            }
            // Events already persisted must not be replayed if only export fails.
            savingEvents = Array.Empty<LogEvent>();
            if (drawing != null)
            {
                ExportDrawing(directory, drawing);
                WriteTextAtomic(Path.Combine(directory, "metadata.json"), metadataJson);
                WriteTextAtomic(Path.Combine(directory, "session.json"), snapshotJson);
            }
            return timer.Elapsed.TotalMilliseconds;
        });
        nextEventFlushTime = Time.unscaledTime + 0.25f;
    }

    private bool ObserveSave(bool wait)
    {
        if (saveTask == null) return true;
        if (!wait && !saveTask.IsCompleted) return false;
        try
        {
            lastSaveWorkerMs = saveTask.GetAwaiter().GetResult();
            saveErrorReported = false;
        }
        catch (Exception exception)
        {
            pendingEvents.InsertRange(0, savingEvents);
            snapshotRequested |= savingSnapshot;
            if (!saveErrorReported)
                Debug.LogError($"[Drawing] Save failed; pending data will be retried: {exception}");
            saveErrorReported = true;
        }
        saveTask = null;
        savingEvents = null;
        if (savingSnapshot)
            Log(new LogEvent { type = "autosave_complete", note = saveErrorReported ? "failed" : "ok" });
        return true;
    }

    private void DrainSaves()
    {
        // Blocking is restricted to pause/end/quit, never normal drawing.
        ObserveSave(true);
        DispatchSave();
        ObserveSave(true);
        // Include the final autosave result event as well.
        if (!saveErrorReported && pendingEvents.Count > 0)
        {
            DispatchSave();
            ObserveSave(true);
        }
    }

    private void HandleClutchChanged(bool held, double realtime)
    {
        if (held)
        {
            strokeId++;
            hasPreviousStrokePoint = false;
        }
        else
        {
            hasPreviousStrokePoint = false;
        }

        Log(new LogEvent
        {
            type = held ? "clutch_down" : "clutch_up",
            clutchHeld = held,
            strokeId = strokeId,
            leftHandTracked = clutchController != null && clutchController.LeftHandTracked,
            leftHandHighConfidence = clutchController != null && clutchController.LeftHandHighConfidence
        });

        nextEventFlushTime = 0f;
    }

    private void HandleEraseChanged(bool held, double realtime)
    {
        var logEvent = new LogEvent
        {
            type = held ? "erase_down" : "erase_up",
            eraseHeld = held,
            clutchHeld = clutchController != null && clutchController.ClutchHeld,
            strokeId = strokeId,
            leftHandTracked = clutchController != null && clutchController.LeftHandTracked,
            leftHandHighConfidence = clutchController != null && clutchController.LeftHandHighConfidence
        };

        if (clutchController != null &&
            clutchController.TryGetRightTipPose(out Vector3 position, out Quaternion rotation))
        {
            logEvent.position = position;
            logEvent.rotation = rotation;
        }

        Log(logEvent);
        nextEventFlushTime = 0f;
    }

    private void RecordStrokePoint()
    {
        if (trajectoryRenderer == null || trajectoryRenderer.skeleton == null ||
            !trajectoryRenderer.skeleton.IsDataValid ||
            !clutchController.TryGetRightTipPose(out Vector3 position, out Quaternion rotation))
        {
            hasPreviousStrokePoint = false;
            return;
        }

        double now = Time.realtimeSinceStartupAsDouble;
        Vector3 velocity = Vector3.zero;
        if (hasPreviousStrokePoint)
        {
            double elapsed = Math.Max(0.000001, now - previousStrokeTime);
            velocity = (position - previousStrokePosition) / (float)elapsed;
        }

        Log(new LogEvent
        {
            type = "stroke_point",
            clutchHeld = true,
            strokeId = strokeId,
            position = position,
            rotation = rotation,
            velocity = velocity,
            speed = velocity.magnitude,
            sampleIntervalSeconds = hasPreviousStrokePoint ? now - previousStrokeTime : 0,
            frameDeltaSeconds = Time.unscaledDeltaTime,
            leftHandTracked = clutchController.LeftHandTracked,
            leftHandHighConfidence = clutchController.LeftHandHighConfidence
        });

        strokePointCount++;
        previousStrokePosition = position;
        previousStrokeTime = now;
        hasPreviousStrokePoint = true;
    }

    private void RecordFrameTelemetry(bool includeAudio)
    {
        if (includeAudio) measuredRefreshRate = ReadDisplayRefreshRate();
        var logEvent = new LogEvent
        {
            type = "frame",
            clutchHeld = clutchController != null && clutchController.ClutchHeld,
            eraseHeld = clutchController != null && clutchController.EraseHeld,
            strokeId = strokeId,
            leftHandTracked = clutchController != null && clutchController.LeftHandTracked,
            leftHandHighConfidence = clutchController != null && clutchController.LeftHandHighConfidence,
            frameDeltaSeconds = Time.unscaledDeltaTime,
            fps = Time.unscaledDeltaTime > 0f ? 1f / Time.unscaledDeltaTime : 0f,
            rightControllerButtons = ReadRightControllerButtons(),
            audioDspTime = AudioSettings.dspTime
        };

        try
        {
            logEvent.rightControllerLocalPosition =
                OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch);
            logEvent.rightControllerLocalRotation =
                OVRInput.GetLocalControllerRotation(OVRInput.Controller.RTouch);
        }
        catch
        {
            logEvent.rightControllerLocalRotation = Quaternion.identity;
        }

        if (includeAudio)
        {
            logEvent.audioSources = ReadAudioSources();
            nextAudioSampleTime = Time.unscaledTime + 1f;
        }

        Log(logEvent);
    }

    private static int ReadRightControllerButtons()
    {
        int mask = 0;
        try
        {
            const OVRInput.Controller controller = OVRInput.Controller.RTouch;
            if (OVRInput.Get(OVRInput.Button.One, controller)) mask |= 1;
            if (OVRInput.Get(OVRInput.Button.Two, controller)) mask |= 2;
            if (OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, controller)) mask |= 4;
            if (OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, controller)) mask |= 8;
            if (OVRInput.Get(OVRInput.Button.PrimaryThumbstick, controller)) mask |= 16;
            if (OVRInput.Get(OVRInput.Button.Start, controller)) mask |= 32;
        }
        catch
        {
            return 0;
        }
        return mask;
    }

    private string ReadAudioSources()
    {
        if (cachedAudioSources == null || Time.unscaledTime >= nextAudioDiscoveryTime)
        {
            cachedAudioSources = FindObjectsByType<AudioSource>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            nextAudioDiscoveryTime = Time.unscaledTime + 5f;
        }
        var text = new StringBuilder();
        foreach (var source in cachedAudioSources)
        {
            if (source == null) continue;
            if (text.Length > 0) text.Append('|');
            string resourceName = source.resource != null ? source.resource.name : "";
            float playbackTime = source.clip != null ? source.time : 0f;
            text.Append(source.gameObject.name.Replace("|", "_"));
            text.Append(';').Append(resourceName.Replace("|", "_"));
            text.Append(";playing=").Append(source.isPlaying ? "1" : "0");
            text.Append(";time=").Append(playbackTime.ToString("R", CultureInfo.InvariantCulture));
            text.Append(";volume=").Append(source.volume.ToString("R", CultureInfo.InvariantCulture));
            text.Append(";pitch=").Append(source.pitch.ToString("R", CultureInfo.InvariantCulture));
            text.Append(";spatialBlend=").Append(source.spatialBlend.ToString("R", CultureInfo.InvariantCulture));
        }
        return text.ToString();
    }

    private void Log(LogEvent logEvent)
    {
        if (eventStream == null || logEvent == null) return;
        logEvent.utc = UtcNow();
        logEvent.sessionTime = Time.realtimeSinceStartupAsDouble - sessionStartedRealtime;
        logEvent.frame = Time.frameCount;
        logEvent.rightHandTracked = trajectoryRenderer != null && trajectoryRenderer.skeleton != null &&
            trajectoryRenderer.skeleton.IsDataValid;
        logEvent.saveInProgress = saveTask != null && !saveTask.IsCompleted;
        logEvent.snapshotCopyMs = lastSnapshotCopyMs;
        logEvent.saveWorkerMs = lastSaveWorkerMs;
        logEvent.displayRefreshRateHz = measuredRefreshRate;
        logEvent.pendingEventCount = pendingEvents.Count;
        pendingEvents.Add(logEvent);
        eventCount++;
    }

    private DrawingFile CaptureDrawing()
    {
        var strokeFiles = new List<StrokeFile>();
        int strokeIndex = 0;
        if (trajectoryRenderer == null)
            return new DrawingFile { exportedUtc = UtcNow(), strokes = Array.Empty<StrokeFile>() };
        foreach (var part in trajectoryRenderer.GetAllParts())
        {
            if (part == null || part.segments == null) continue;
            foreach (var segment in part.segments)
            {
                if (segment == null || segment.points == null || segment.points.Count == 0)
                    continue;

                strokeIndex++;
                var points = segment.points.ToArray();
                strokeFiles.Add(new StrokeFile
                {
                    strokeIndex = strokeIndex,
                    partId = part.id,
                    source = segment.key ?? "right_index_tip",
                    points = points
                });
            }
        }
        return new DrawingFile { exportedUtc = UtcNow(), strokes = strokeFiles.ToArray() };
    }

    private static void ExportDrawing(string directory, DrawingFile drawingFile)
    {
        var csv = new StringBuilder();
        var obj = new StringBuilder();
        csv.AppendLine("stroke_index,part_id,source,point_index,x,y,z");
        obj.AppendLine("# Drawing polyline export");
        int vertexIndex = 1;
        foreach (var stroke in drawingFile.strokes)
        {
            var points = stroke.points;
            int firstVertex = vertexIndex;
            for (int pointIndex = 0; pointIndex < points.Length; pointIndex++)
            {
                Vector3 point = points[pointIndex];
                csv.Append(stroke.strokeIndex).Append(',').Append(stroke.partId).Append(',')
                    .Append(EscapeCsv(stroke.source)).Append(',').Append(pointIndex).Append(',')
                    .Append(point.x.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(point.y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(point.z.ToString("R", CultureInfo.InvariantCulture)).AppendLine();
                obj.Append("v ")
                    .Append(point.x.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                    .Append(point.y.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                    .Append(point.z.ToString("R", CultureInfo.InvariantCulture)).AppendLine();
                vertexIndex++;
            }

            if (points.Length >= 2)
            {
                obj.Append("l");
                for (int index = 0; index < points.Length; index++)
                    obj.Append(' ').Append(firstVertex + index);
                obj.AppendLine();
            }
        }
        WriteTextAtomic(Path.Combine(directory, "drawing.csv"), csv.ToString());
        WriteTextAtomic(
            Path.Combine(directory, "drawing.json"),
            JsonUtility.ToJson(drawingFile, true));
        WriteTextAtomic(Path.Combine(directory, "drawing.obj"), obj.ToString());
    }

    private static void WriteTextAtomic(string path, string contents)
    {
        string temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, contents, new UTF8Encoding(false));
        if (!File.Exists(path))
        {
            File.Move(temporaryPath, path);
            return;
        }

        try
        {
            string backupPath = path + ".bak";
            File.Replace(temporaryPath, path, backupPath, true);
            if (File.Exists(backupPath)) File.Delete(backupPath);
        }
        catch (PlatformNotSupportedException)
        {
            File.Copy(temporaryPath, path, true);
            File.Delete(temporaryPath);
        }
        catch (IOException)
        {
            File.Copy(temporaryPath, path, true);
            File.Delete(temporaryPath);
        }
    }

    private static string EscapeCsv(string value)
    {
        value ??= string.Empty;
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static float ReadDisplayRefreshRate()
    {
        var displays = new List<XRDisplaySubsystem>();
        SubsystemManager.GetSubsystems(displays);
        foreach (var display in displays)
        {
            if (display != null && display.TryGetDisplayRefreshRate(out float refreshRate))
                return refreshRate;
        }
        return 0f;
    }

    private static string UtcNow()
    {
        return DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    }

    private void OnApplicationPause(bool paused)
    {
        if (!paused || eventStream == null) return;
        SnapshotNow(finalized);
        DrainSaves();
    }

    private void OnApplicationQuit()
    {
        if (closing) return;
        closing = true;
        FinalizeSession("application_quit");
        DrainSaves();
        eventStream?.Dispose();
        eventStream = null;
    }

    private void OnDestroy()
    {
        if (clutchController != null)
        {
            clutchController.ClutchChanged -= HandleClutchChanged;
            clutchController.EraseChanged -= HandleEraseChanged;
        }
        if (eventStream == null) return;
        SnapshotNow(finalized);
        DrainSaves();
        eventStream?.Dispose();
        eventStream = null;
    }
}
