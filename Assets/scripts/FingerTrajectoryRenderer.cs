using System.Collections.Generic;
using System.Linq;
using UnityEngine;

[DefaultExecutionOrder(-750)]
public class FingerTrajectoryRenderer : MonoBehaviour
{
    [Header("Required")]
    public Material baseLineMaterial;

    [Header("Line Settings")]
    public float width = 0.005f;
    public float minPointDistance = 0.001f;
    [Tooltip("Multiply line color brightness (>1 = HDR bloom if post-processing enabled)")]
    public float colorBrightness = 1.5f;

    [Header("Fixed Stroke Appearance")]
    [Tooltip("When enabled, every new line uses fixedStrokeColor instead of part colors.")]
    public bool useFixedStrokeAppearance = false;
    public Color fixedStrokeColor = new Color(1f, 0f, 0f, 0.8f);

    [Header("Erase")]
    public float eraseRadius = 0.05f;
    public Transform erasePoint; // auto-assigned to right hand IndexTip

    [Header("Sketch")]
    [Range(0f, 1f)] public float sketchAlpha = 0.4f;

    [Header("Debug")]
    public bool logFoundTipNames = false;

    public bool IsRecording { get; private set; } = false;
    public bool IsDrawEnabled { get; private set; } = false;
    public bool IsEraseMode { get; private set; } = false;
    public bool IsErasing => eraseHeld;
    public bool IsInputSuppressed => inputSuppressors.Count > 0;
    public bool CanUndo => activePart != null &&
        (activePart.undoStack.Count > 0 || HasPendingUndo(activePart));
    public bool CanTransformActivePart => PartHasPoints(activePart);
    public bool IsPartTransformInProgress => transformPart != null;
    public Part TransformPart => transformPart;
    public OVRSkeleton skeleton;

    public enum TrackingMode { IndexTipOnly, Fingertips, AllJoints }
    public TrackingMode mode = TrackingMode.IndexTipOnly;

    // Maximum Part id (0 = sketch, 1..MaxParts = drawing parts)
    public const int MaxParts = 20;

    // ── Part colors ──────────────────────────────────────────────────────────
    private static readonly Color[] PartColors =
    {
        new Color(1.00f,0.30f,0.22f), // 1  coral
        new Color(0.40f,0.10f,0.88f), // 2  violet
        new Color(0.56f,0.90f,0.00f), // 3  lime
        new Color(0.00f,0.84f,0.90f), // 4  turquoise
        new Color(0.78f,0.52f,0.94f), // 5  orchid
        new Color(0.72f,0.54f,0.00f), // 6  gold
        new Color(0.96f,0.52f,0.72f), // 7  rose pink
        new Color(0.00f,0.45f,0.82f), // 8  dark cyan
        new Color(0.90f,0.00f,0.74f), // 9  magenta
        new Color(1.00f,0.93f,0.00f), // 10 yellow
        new Color(0.65f,0.00f,0.16f), // 11 crimson
        new Color(0.18f,0.86f,0.28f), // 12 spring green
        new Color(0.42f,0.76f,0.98f), // 13 light blue
        new Color(0.95f,0.48f,0.00f), // 14 orange
        new Color(0.00f,0.78f,0.40f), // 15 emerald
        new Color(1.00f,0.76f,0.60f), // 16 peach
        new Color(0.07f,0.10f,0.40f), // 17 navy
        new Color(0.58f,0.30f,0.08f), // 18 sienna
        new Color(0.00f,0.36f,0.28f), // 19 dark teal
        new Color(0.55f,0.55f,0.55f), // 20 silver
    };

    public static Color GetPartColor(int partId)
    {
        if (partId < 1 || partId > MaxParts || partId > PartColors.Length)
            return SketchColor;

        return PartColors[partId - 1];
    }

    // ── Data structures ──────────────────────────────────────────────────────
    public class Segment
    {
        public string key;
        public List<Vector3> points = new();
        public LineRenderer lr;
    }

    public interface IUndoAction { }

    // Both draw and erase undo work the same way: restore full snapshot
    public class UndoAction : IUndoAction
    {
        public List<(string key, List<Vector3> points)> snapshot = new();
    }

    public class Part
    {
        public int id;
        public string name = "";
        public string description = "";
        public Color color;
        public bool isVisible = true;
        public bool isSketch = false;
        public bool isLocked = false;   // sketch becomes locked after first S
        public GameObject container;
        public List<Segment> segments = new();
        public Stack<IUndoAction> undoStack = new();
    }

    public static readonly Color SketchColor = new Color(0.05f, 0.45f, 1f);

    public string shapeName = "";
    public string shapeDescription = "";

    // ── State ─────────────────────────────────────────────────────────────────
    public List<Part> parts { get; private set; } = new();
    public Part activePart { get; private set; }

    private readonly Dictionary<string, Transform> tipT = new();
    private readonly Dictionary<string, Vector3> lastPos = new();
    private List<string> activeKeys = new();

    // current recording
    private List<Segment> currentDrawSegs = null;
    private UndoAction pendingDraw = null;
    private Part drawPart = null;

    // erase
    private bool eraseHeld = false;
    private UndoAction pendingErase = null;
    private Part erasePart = null;
    private bool eraseChanged = false;
    private readonly HashSet<object> eraseOwners = new();
    private readonly object legacyEraseOwner = new();

    // Suppression is owner-based so one UI/controller cannot accidentally
    // resume input while another suppressor is still active.
    private readonly HashSet<object> inputSuppressors = new();
    private readonly object legacyInputSuppressor = new();

    // A transform session owns immutable source arrays and reusable output
    // arrays. Every preview frame is recalculated from the source data so the
    // result cannot accumulate floating-point drift.
    private sealed class TransformSegmentState
    {
        public Segment segment;
        public Vector3[] sourcePoints;
        public Vector3[] outputPoints;
    }

    private readonly List<TransformSegmentState> transformSegments = new();
    private Part transformPart;
    private UndoAction pendingTransform;
    private Vector3 transformPivot;
    private Vector3 transformTranslation;
    private Quaternion transformRotation = Quaternion.identity;
    private float transformScale = 1f;
    private bool transformHasPreview;

    // ── Unity ─────────────────────────────────────────────────────────────────
    void Start()
    {
        skeleton = GetComponent<OVRSkeleton>();
        if (!skeleton)
            Debug.LogError("FingerTrajectoryRenderer: OVRSkeleton not found.");

        mode = TrackingMode.IndexTipOnly;
        // Do not overwrite a drawing restored earlier by another component's
        // Start(). A fresh session still begins with Scaffold (Part 0).
        if (parts.Count == 0 && activePart == null)
            SwitchToPart(0);
    }

    // SDK Update -> clutch LateUpdate -> drawing LateUpdate -> recorder LateUpdate.
    void LateUpdate()
    {
        if (!skeleton || !skeleton.IsInitialized || !skeleton.IsDataValid)
        {
            // Commit the partial stroke at the tracking boundary. When tracking
            // returns, a toggled-on Draw starts a fresh stroke instead of joining
            // two unrelated hand positions.
            EndRecordingInternal();
            return;
        }
        if (tipT.Count == 0) InitializeTips();

        if (IsDrawEnabled && !IsInputSuppressed && !eraseHeld && !IsRecording)
            BeginRecordingInternal();

        if (IsRecording && currentDrawSegs != null)
            RecordFrame();

        if (eraseHeld && erasePoint != null)
            EraseFrame(erasePoint.position);
    }

    // ── Parts ─────────────────────────────────────────────────────────────────
    public void SwitchToPart(int id)
    {
        if (id < 0 || id > MaxParts)
        {
            Debug.LogWarning($"[Part] Ignored out-of-range Part {id}. Valid range is 0-{MaxParts}.");
            return;
        }

        if (activePart != null && activePart.id == id)
            return;

        StopOperationsForContextChange();

        var existing = parts.FirstOrDefault(p => p.id == id);
        if (existing != null)
        {
            activePart = existing;
            Debug.Log($"[Part] Switched to Part {id}");
            return;
        }

        // Create new part
        bool sketch = (id == 0);
        var part = new Part
        {
            id = id,
            isSketch = sketch,
            color = sketch ? SketchColor : GetPartColor(id),
            container = new GameObject(sketch ? "Sketch" : $"Part_{id}")
        };
        part.container.transform.SetParent(transform, worldPositionStays: false);
        parts.Add(part);
        activePart = part;
        Debug.Log($"[Part] Created Part {id}");
    }

    // ── Recording ─────────────────────────────────────────────────────────────
    public void StartRecording()
    {
        IsDrawEnabled = true;
        BeginRecordingInternal();
    }

    public void StopRecording()
    {
        IsDrawEnabled = false;
        EndRecordingInternal();
    }

    public void ToggleRecording()
    {
        if (IsDrawEnabled) StopRecording();
        else StartRecording();
    }

    public void SetInputSuppressed(bool suppressed)
    {
        SetInputSuppressed(legacyInputSuppressor, suppressed);
    }

    public void SetInputSuppressed(object owner, bool suppressed)
    {
        if (owner == null) return;

        bool wasSuppressed = IsInputSuppressed;
        if (suppressed) inputSuppressors.Add(owner);
        else inputSuppressors.Remove(owner);

        if (wasSuppressed == IsInputSuppressed) return;

        if (IsInputSuppressed)
        {
            EndRecordingInternal();
            EndAllEraseInputs(resumeDraw: false);
        }
        else if (IsDrawEnabled)
        {
            BeginRecordingInternal();
        }
    }

    private void BeginRecordingInternal()
    {
        if (IsRecording || !IsDrawEnabled || IsInputSuppressed || eraseHeld || activePart == null)
            return;
        if (skeleton == null || !skeleton.IsInitialized || !skeleton.IsDataValid)
            return;

        if (tipT.Count == 0)
            InitializeTips();
        if (activeKeys.Count == 0)
            return;

        IsRecording = true;
        lastPos.Clear();
        drawPart = activePart;
        pendingDraw = CaptureSnapshot(drawPart);

        currentDrawSegs = new List<Segment>();
        foreach (var key in activeKeys)
        {
            var seg = new Segment { key = key };
            seg.lr = CreateLineRenderer(key, drawPart);
            drawPart.segments.Add(seg);
            currentDrawSegs.Add(seg);
        }
        Debug.Log("[Record] START");
    }

    private void EndRecordingInternal()
    {
        if (!IsRecording) return;

        IsRecording = false;
        bool changed = HasVisibleCurrentDraw();

        if (currentDrawSegs != null && drawPart != null)
        {
            foreach (var emptySegment in currentDrawSegs.Where(seg => seg.points.Count < 2).ToList())
            {
                drawPart.segments.Remove(emptySegment);
                if (emptySegment.lr != null) Destroy(emptySegment.lr.gameObject);
            }
        }

        if (changed && pendingDraw != null && drawPart != null)
            drawPart.undoStack.Push(pendingDraw);

        currentDrawSegs = null;
        pendingDraw = null;
        drawPart = null;
        lastPos.Clear();
        Debug.Log("[Record] STOP");
    }

    private void RecordFrame()
    {
        foreach (var key in activeKeys)
        {
            if (!tipT.TryGetValue(key, out var t) || t == null) continue;

            var seg = currentDrawSegs?.FirstOrDefault(s => s.key == key);
            if (seg == null || seg.lr == null) continue;

            Vector3 p = t.position;
            if (!lastPos.TryGetValue(key, out var prev) || Vector3.Distance(prev, p) > minPointDistance)
            {
                seg.points.Add(p);
                seg.lr.positionCount = seg.points.Count;
                seg.lr.SetPosition(seg.points.Count - 1, p);
                lastPos[key] = p;
            }
        }
    }

    // ── Undo ──────────────────────────────────────────────────────────────────
    public void Undo()
    {
        if (!CanUndo) return;

        StopOperationsForContextChange();
        if (activePart == null || activePart.undoStack.Count == 0) return;

        var action = activePart.undoStack.Pop() as UndoAction;
        if (action == null) return;

        foreach (var seg in activePart.segments)
            if (seg.lr != null) Destroy(seg.lr.gameObject);
        activePart.segments.Clear();

        foreach (var (key, points) in action.snapshot)
        {
            var seg = new Segment { key = key, points = new List<Vector3>(points) };
            seg.lr = CreateLineRenderer(key, activePart);
            RebuildLineRenderer(seg);
            activePart.segments.Add(seg);
        }
        Debug.Log("[Undo]");
    }

    // -- Part transform -------------------------------------------------------
    // These methods deliberately keep Segment.points in world space. This
    // preserves the existing erase, save/load, and undo behavior while a
    // controller supplies a lightweight CG transform for the live preview.
    public bool TryBeginActivePartTransform(out Vector3 pivot)
    {
        pivot = default;
        if (IsPartTransformInProgress || !PartHasPoints(activePart))
            return false;

        // Transform is a top-level operation and must never overlap a partial
        // draw or erase operation. Disabling Draw also prevents it resuming
        // when the transform controller later releases its suppression token.
        StopOperationsForContextChange(disableDraw: true);

        var part = activePart;
        if (!PartHasPoints(part))
            return false;

        bool hasBounds = false;
        Vector3 boundsMin = default;
        Vector3 boundsMax = default;

        transformSegments.Clear();
        for (int segmentIndex = 0; segmentIndex < part.segments.Count; segmentIndex++)
        {
            var segment = part.segments[segmentIndex];
            if (segment == null || segment.points == null || segment.points.Count == 0)
                continue;

            int pointCount = segment.points.Count;
            var sourcePoints = new Vector3[pointCount];
            var outputPoints = new Vector3[pointCount];
            segment.points.CopyTo(sourcePoints);

            for (int pointIndex = 0; pointIndex < pointCount; pointIndex++)
            {
                Vector3 point = sourcePoints[pointIndex];
                if (!hasBounds)
                {
                    boundsMin = point;
                    boundsMax = point;
                    hasBounds = true;
                }
                else
                {
                    boundsMin = Vector3.Min(boundsMin, point);
                    boundsMax = Vector3.Max(boundsMax, point);
                }
            }

            transformSegments.Add(new TransformSegmentState
            {
                segment = segment,
                sourcePoints = sourcePoints,
                outputPoints = outputPoints
            });
        }

        if (!hasBounds)
        {
            transformSegments.Clear();
            return false;
        }

        transformPart = part;
        pendingTransform = CaptureSnapshot(part);
        transformPivot = (boundsMin + boundsMax) * 0.5f;
        transformTranslation = Vector3.zero;
        transformRotation = Quaternion.identity;
        transformScale = 1f;
        transformHasPreview = false;
        pivot = transformPivot;
        return true;
    }

    public bool ApplyActivePartTransform(
        Vector3 translation, Quaternion rotation, float uniformScale)
    {
        if (!IsPartTransformInProgress || !IsFinite(translation) ||
            !IsFinite(rotation) || !IsFinite(uniformScale) || uniformScale <= 0f)
        {
            return false;
        }

        rotation = rotation.normalized;

        // Validate the session before changing any point. A concurrent edit is
        // unexpected, but failing atomically lets the controller cancel safely.
        for (int segmentIndex = 0; segmentIndex < transformSegments.Count; segmentIndex++)
        {
            var state = transformSegments[segmentIndex];
            if (state.segment == null || state.segment.points == null ||
                state.segment.points.Count != state.sourcePoints.Length)
            {
                return false;
            }
        }

        for (int segmentIndex = 0; segmentIndex < transformSegments.Count; segmentIndex++)
        {
            var state = transformSegments[segmentIndex];
            var segment = state.segment;
            int pointCount = state.sourcePoints.Length;

            for (int pointIndex = 0; pointIndex < pointCount; pointIndex++)
            {
                Vector3 point = transformPivot + translation +
                    rotation * ((state.sourcePoints[pointIndex] - transformPivot) * uniformScale);
                state.outputPoints[pointIndex] = point;
                segment.points[pointIndex] = point;
            }

            if (segment.lr != null)
            {
                segment.lr.positionCount = pointCount;
                segment.lr.SetPositions(state.outputPoints);
            }
        }

        transformTranslation = translation;
        transformRotation = rotation;
        transformScale = uniformScale;
        transformHasPreview = true;
        return true;
    }

    public bool CommitActivePartTransform()
    {
        if (!IsPartTransformInProgress)
            return false;

        bool changed = TransformResultIsMeaningful();
        if (changed && pendingTransform != null)
            transformPart.undoStack.Push(pendingTransform);
        else
            RestorePartTransformSource();

        ClearPartTransformState();
        if (changed)
            Debug.Log("[Transform] Commit");
        return changed;
    }

    public void CancelActivePartTransform()
    {
        if (!IsPartTransformInProgress)
            return;

        RestorePartTransformSource();
        ClearPartTransformState();
        Debug.Log("[Transform] Cancel");
    }

    private void RestorePartTransformSource()
    {
        for (int segmentIndex = 0; segmentIndex < transformSegments.Count; segmentIndex++)
        {
            var state = transformSegments[segmentIndex];
            var segment = state.segment;
            if (segment == null || segment.points == null)
                continue;

            segment.points.Clear();
            segment.points.AddRange(state.sourcePoints);
            if (segment.lr != null)
            {
                segment.lr.positionCount = state.sourcePoints.Length;
                segment.lr.SetPositions(state.sourcePoints);
            }
        }
    }

    // ── Erase ─────────────────────────────────────────────────────────────────
    public void SetEraseMode(bool on)
    {
        IsEraseMode = on;
        if (!on && eraseHeld) EndAllEraseInputs(resumeDraw: true);
        Debug.Log($"[Erase] Mode {(on ? "ON" : "OFF")}");
    }

    public void ToggleEraseMode() => SetEraseMode(!IsEraseMode);

    public void BeginErase()
    {
        BeginErase(legacyEraseOwner);
    }

    public bool BeginErase(object owner)
    {
        if (owner == null || activePart == null || IsInputSuppressed) return false;
        if (!eraseOwners.Add(owner)) return true;

        // Multiple hold sources share one erase operation. It is committed only
        // after the last source releases its own token.
        if (eraseHeld) return true;

        // Erase temporarily pauses a toggled-on draw state. Releasing erase
        // resumes drawing as a new stroke unless another input suppresses it.
        EndRecordingInternal();
        eraseHeld = true;
        erasePart = activePart;
        pendingErase = CaptureSnapshot(erasePart);
        eraseChanged = false;

        Debug.Log("[Erase] Begin");
        return true;
    }

    public bool IsEraseOwnerActive(object owner)
    {
        return owner != null && eraseOwners.Contains(owner);
    }

    public void EndErase()
    {
        EndErase(legacyEraseOwner);
    }

    public void EndErase(object owner)
    {
        if (owner == null || !eraseOwners.Remove(owner)) return;
        if (eraseOwners.Count == 0)
            EndEraseInternal(resumeDraw: true);
    }

    private void EndEraseInternal(bool resumeDraw)
    {
        if (!eraseHeld) return;

        eraseHeld = false;
        if (eraseChanged && pendingErase != null && erasePart != null)
            erasePart.undoStack.Push(pendingErase);

        pendingErase = null;
        erasePart = null;
        eraseChanged = false;
        Debug.Log("[Erase] End");

        if (resumeDraw && IsDrawEnabled && !IsInputSuppressed)
            BeginRecordingInternal();
    }

    private void EndAllEraseInputs(bool resumeDraw)
    {
        eraseOwners.Clear();
        EndEraseInternal(resumeDraw);
    }

    private void EraseFrame(Vector3 center)
    {
        if (erasePart == null) return;

        var toAdd = new List<Segment>();
        var toRemove = new List<Segment>();

        foreach (var seg in erasePart.segments)
        {
            bool modified = false;
            // Mark points inside sphere
            var keep = seg.points.Select(p =>
            {
                bool inside = Vector3.Distance(p, center) <= eraseRadius;
                if (inside) modified = true;
                return (p, inside);
            }).ToList();

            if (!modified) continue;

            toRemove.Add(seg);
            if (seg.lr != null) Destroy(seg.lr.gameObject);

            // Split into sub-segments at gaps
            var run = new List<Vector3>();
            foreach (var (p, erased) in keep)
            {
                if (!erased)
                {
                    run.Add(p);
                }
                else if (run.Count >= 2)
                {
                    toAdd.Add(BuildSegment(seg.key, run, erasePart));
                    run = new List<Vector3>();
                }
                else if (erased)
                {
                    run.Clear();
                }
            }
            if (run.Count >= 2)
                toAdd.Add(BuildSegment(seg.key, run, erasePart));
        }

        if (toRemove.Count == 0) return;

        eraseChanged = true;
        foreach (var s in toRemove) erasePart.segments.Remove(s);
        erasePart.segments.AddRange(toAdd);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private Segment BuildSegment(string key, List<Vector3> points, Part part)
    {
        var seg = new Segment { key = key, points = points };
        seg.lr = CreateLineRenderer(key, part);
        RebuildLineRenderer(seg);
        return seg;
    }

    private LineRenderer CreateLineRenderer(string key, Part part)
    {
        var go = new GameObject($"Seg_{key}");
        go.transform.SetParent(part.container.transform, worldPositionStays: false);

        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.widthMultiplier = width;
        lr.positionCount = 0;

        Material mat;
        if (baseLineMaterial != null)
            mat = new Material(baseLineMaterial);
        else
            mat = new Material(Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default"));

        var c = part.isSketch
            ? SketchColor
            : (useFixedStrokeAppearance ? fixedStrokeColor : part.color);
        float alpha = part.isSketch
            ? sketchAlpha
            : (useFixedStrokeAppearance ? fixedStrokeColor.a : 1f);
        mat.color = new Color(c.r * colorBrightness, c.g * colorBrightness, c.b * colorBrightness, alpha);
        lr.material = mat;
        return lr;
    }

    public void RefreshSketchAppearance()
    {
        Color color = new Color(
            SketchColor.r * colorBrightness,
            SketchColor.g * colorBrightness,
            SketchColor.b * colorBrightness,
            sketchAlpha);

        foreach (var part in parts.Where(candidate => candidate != null && candidate.isSketch))
        {
            part.color = SketchColor;
            foreach (var segment in part.segments)
            {
                if (segment?.lr == null) continue;
                Material material = segment.lr.material;
                material.color = color;
                if (material.HasProperty("_BaseColor"))
                    material.SetColor("_BaseColor", color);
                if (material.HasProperty("_Color"))
                    material.SetColor("_Color", color);
            }
        }
    }

    private void RebuildLineRenderer(Segment seg)
    {
        seg.lr.positionCount = seg.points.Count;
        for (int i = 0; i < seg.points.Count; i++)
            seg.lr.SetPosition(i, seg.points[i]);
    }

    // ── Tracking modes ────────────────────────────────────────────────────────
    public void SetMode(TrackingMode newMode)
    {
        if (mode == newMode) return;

        StopOperationsForContextChange(disableDraw: true);
        mode = newMode;
        tipT.Clear();
        lastPos.Clear();
        activeKeys.Clear();
        Debug.Log($"[Mode] {newMode}");
    }

    private void InitializeTips()
    {
        activeKeys.Clear();
        var boneTransforms = skeleton.Bones
            .Where(b => b != null && b.Transform != null)
            .Select(b => b.Transform).ToList();

        if (mode == TrackingMode.IndexTipOnly)
        {
            var t = boneTransforms.FirstOrDefault(x =>
                x.name.IndexOf("IndexTip", System.StringComparison.OrdinalIgnoreCase) >= 0);
            if (t != null) { tipT["IndexTip"] = t; activeKeys.Add("IndexTip"); TryAssignErasePoint(t); }
            return;
        }

        if (mode == TrackingMode.Fingertips)
        {
            string[] keys = { "IndexTip", "MiddleTip", "RingTip", "PinkyTip" };
            foreach (var key in keys)
            {
                var search = key == "PinkyTip" ? new[] { "PinkyTip", "LittleTip" } : new[] { key };
                foreach (var s in search)
                {
                    var t = boneTransforms.FirstOrDefault(x =>
                        x.name.IndexOf(s, System.StringComparison.OrdinalIgnoreCase) >= 0);
                    if (t != null)
                    {
                        tipT[key] = t;
                        activeKeys.Add(key);
                        if (key == "IndexTip") TryAssignErasePoint(t);
                        break;
                    }
                }
            }
            return;
        }

        // AllJoints — use IndexTip for erasePoint
        var indexT = boneTransforms.FirstOrDefault(x =>
            x.name.IndexOf("IndexTip", System.StringComparison.OrdinalIgnoreCase) >= 0);
        if (indexT != null) TryAssignErasePoint(indexT);

        string[] wanted =
        {
            "Index2","Index3","IndexTip","Middle2","Middle3","MiddleTip",
            "Ring2","Ring3","RingTip","Pinky2","Pinky3","PinkyTip",
            "IndexIntermediate","IndexDistal","MiddleIntermediate","MiddleDistal",
            "RingIntermediate","RingDistal","LittleIntermediate","LittleDistal","LittleTip",
        };
        foreach (var bone in skeleton.Bones)
        {
            if (bone?.Transform == null) continue;
            string bn = bone.Transform.name;
            if (wanted.Any(w => bn.IndexOf(w, System.StringComparison.OrdinalIgnoreCase) >= 0))
            {
                if (!tipT.ContainsKey(bn)) { tipT[bn] = bone.Transform; activeKeys.Add(bn); }
            }
        }
    }

    private void TryAssignErasePoint(Transform t)
    {
        if (erasePoint == null) erasePoint = t;
    }

    // ── Save data accessors ───────────────────────────────────────────────────
    public Part GetSketch() => parts.FirstOrDefault(p => p.isSketch);
    public List<Part> GetNonSketchParts() => parts.Where(p => !p.isSketch).ToList();
    public List<Part> GetAllParts() => parts;

    public void LockSketch()
    {
        var sketch = GetSketch();
        if (sketch != null) sketch.isLocked = true;
        Debug.Log("[Sketch] Locked");
    }

    // ── Load / restore ────────────────────────────────────────────────────────
    public void ClearAll()
    {
        StopOperationsForContextChange();
        foreach (var part in parts)
        {
            foreach (var seg in part.segments)
                if (seg.lr != null) Destroy(seg.lr.gameObject);
            if (part.container != null) Destroy(part.container);
        }
        parts.Clear();
        activePart = null;
        shapeName = "";
        shapeDescription = "";
        Debug.Log("[Load] Cleared all parts.");
    }

    public void RestorePart(int id, string name, string desc, Color color, List<List<Vector3>> segmentGroups)
    {
        // Skip if already restored (e.g. sketch loaded twice)
        if (parts.Any(p => p.id == id)) return;

        bool sketch = (id == 0);
        var part = new Part
        {
            id = id,
            name = name,
            description = desc,
            isSketch = sketch,
            isLocked = sketch,
            color = sketch ? SketchColor : color,
            container = new GameObject(sketch ? "Sketch" : $"Part_{id}")
        };
        part.container.transform.SetParent(transform, worldPositionStays: false);
        parts.Add(part);

        foreach (var points in segmentGroups)
        {
            if (points.Count < 2) continue;
            var seg = BuildSegment("loaded", points, part);
            part.segments.Add(seg);
        }
        Debug.Log($"[Load] Restored Part {id}: {segmentGroups.Count} segments");
    }

    private UndoAction CaptureSnapshot(Part part)
    {
        var snapshot = new UndoAction();
        if (part == null) return snapshot;

        foreach (var seg in part.segments)
            snapshot.snapshot.Add((seg.key, new List<Vector3>(seg.points)));
        return snapshot;
    }

    private static bool PartHasPoints(Part part)
    {
        if (part == null || part.segments == null)
            return false;

        for (int i = 0; i < part.segments.Count; i++)
        {
            var segment = part.segments[i];
            if (segment != null && segment.points != null && segment.points.Count > 0)
                return true;
        }
        return false;
    }

    private bool TransformResultIsMeaningful()
    {
        if (!transformHasPreview)
            return false;

        // Numerical guards only: precision gain should remain continuously
        // responsive, without a perceptual transform dead zone on release.
        const float positionEpsilon = 0.00001f;
        const float positionEpsilonSquared = positionEpsilon * positionEpsilon;
        const float scaleEpsilon = 0.00001f;
        const float rotationEpsilonDegrees = 0.001f;
        return transformTranslation.sqrMagnitude > positionEpsilonSquared ||
            Mathf.Abs(transformScale - 1f) > scaleEpsilon ||
            Quaternion.Angle(Quaternion.identity, transformRotation) > rotationEpsilonDegrees;
    }

    private void ClearPartTransformState()
    {
        transformSegments.Clear();
        transformPart = null;
        pendingTransform = null;
        transformPivot = default;
        transformTranslation = Vector3.zero;
        transformRotation = Quaternion.identity;
        transformScale = 1f;
        transformHasPreview = false;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static bool IsFinite(Vector3 value)
    {
        return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }

    private static bool IsFinite(Quaternion value)
    {
        return IsFinite(value.x) && IsFinite(value.y) &&
            IsFinite(value.z) && IsFinite(value.w);
    }

    private bool HasPendingUndo(Part part)
    {
        if (part == null) return false;

        bool pendingVisibleDraw = IsRecording && drawPart == part && HasVisibleCurrentDraw();
        bool pendingChangedErase = eraseHeld && erasePart == part && eraseChanged;
        bool pendingChangedTransform = transformPart == part && TransformResultIsMeaningful();
        return pendingVisibleDraw || pendingChangedErase || pendingChangedTransform;
    }

    private bool HasVisibleCurrentDraw()
    {
        return currentDrawSegs != null && currentDrawSegs.Any(seg => seg.points.Count >= 2);
    }

    private void StopOperationsForContextChange(bool disableDraw = false)
    {
        if (disableDraw) IsDrawEnabled = false;
        EndRecordingInternal();
        EndAllEraseInputs(resumeDraw: false);
        CommitActivePartTransform();
    }
}
