using System;
using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.XR;
using UnityEngine.XR.Management;

[DefaultExecutionOrder(-1000)]
[DisallowMultipleComponent]
public sealed class DrawingApp : MonoBehaviour
{
    private DrawingSessionConfig config;
    private FingerTrajectoryRenderer trajectoryRenderer;
    private DrawingClutchController clutchController;
    private DrawingExperimentRecorder recorder;
    private GameObject backgroundDome;
    private Material backgroundMaterial;
    private bool setupComplete;
    private bool ending;
    private bool sessionEnded;
    private bool observedActiveXr;
    private float xrInactiveSince = -1f;
    private float finishButtonHeldSeconds;
    private bool refreshRateChecked;
    private float nextRefreshRateCheck;
    private int refreshRateAttempts;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void CreateRuntime()
    {
        if (FindFirstObjectByType<DrawingApp>() != null) return;
        var host = new GameObject("Drawing Runtime");
        host.AddComponent<DrawingApp>();
    }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);
        config = DrawingSessionConfig.Load();
        EnableHandVisuals();
        ConfigureBackground();
    }

    private IEnumerator Start()
    {
        for (int attempt = 0; attempt < 300 && trajectoryRenderer == null; attempt++)
        {
            var renderers = FindObjectsByType<FingerTrajectoryRenderer>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (renderers.Length > 0) trajectoryRenderer = renderers[0];
            if (trajectoryRenderer == null) yield return null;
        }

        if (trajectoryRenderer == null)
        {
            Debug.LogError("[Drawing] FingerTrajectoryRenderer was not found. Session cannot start.");
            enabled = false;
            yield break;
        }

        ConfigureFixedBrush();
        ConfigureBackground();

        clutchController = gameObject.AddComponent<DrawingClutchController>();
        clutchController.Initialize(trajectoryRenderer);

        recorder = gameObject.AddComponent<DrawingExperimentRecorder>();
        recorder.Initialize(config, trajectoryRenderer, clutchController);
        setupComplete = true;
    }

    private static void EnableHandVisuals()
    {
        var hands = FindObjectsByType<OVRHand>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var hand in hands)
        {
            var mesh = hand.GetComponent<OVRMesh>();
            if (mesh != null) mesh.enabled = true;

            var meshRenderer = hand.GetComponent<OVRMeshRenderer>();
            if (meshRenderer != null) meshRenderer.enabled = true;

            var skinnedMeshRenderer = hand.GetComponent<SkinnedMeshRenderer>();
            if (skinnedMeshRenderer != null) skinnedMeshRenderer.enabled = true;
        }
    }

    private void ConfigureFixedBrush()
    {
        trajectoryRenderer.mode = FingerTrajectoryRenderer.TrackingMode.IndexTipOnly;
        trajectoryRenderer.width = config.strokeWidthMeters;
        trajectoryRenderer.minPointDistance = config.minPointDistanceMeters;
        trajectoryRenderer.eraseRadius = config.eraseRadiusMeters;
        trajectoryRenderer.colorBrightness = 1f;
        trajectoryRenderer.sketchAlpha = config.sketchAlpha;
        trajectoryRenderer.useFixedStrokeAppearance = true;
        trajectoryRenderer.fixedStrokeColor = new Color(1f, 0f, 0f, config.strokeAlpha);
        trajectoryRenderer.RefreshSketchAppearance();
        trajectoryRenderer.SwitchToPart(1);
        if (trajectoryRenderer.activePart != null)
            trajectoryRenderer.activePart.color = trajectoryRenderer.fixedStrokeColor;
    }

    private void ConfigureBackground()
    {
        Color background = config.BackgroundColor;
        RenderSettings.skybox = null;
        RenderSettings.fog = false;
        RenderSettings.fogColor = background;

        var cameras = FindObjectsByType<Camera>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var camera in cameras)
        {
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = background;
        }

        Camera xrCamera = Camera.main;
        if (xrCamera == null)
        {
            foreach (var camera in cameras)
            {
                if (!camera.isActiveAndEnabled) continue;
                xrCamera = camera;
                break;
            }
        }
        if (xrCamera == null && cameras.Length > 0)
            xrCamera = cameras[0];
        if (xrCamera == null) return;

        // Wait for the drawing renderer so we can reuse a shader that is
        // already referenced by an asset and therefore retained in builds.
        if (trajectoryRenderer == null || trajectoryRenderer.baseLineMaterial == null)
            return;

        // Meta's XR compositor does not consistently expose Camera background
        // colors. Render an inward-facing unlit sphere as real scene geometry
        // so the chosen color is visible in both eyes on-device.
        if (backgroundDome == null)
        {
            backgroundDome = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            backgroundDome.name = "Drawing Background";
            backgroundDome.layer = LayerMask.NameToLayer("Ignore Raycast");

            var collider = backgroundDome.GetComponent<Collider>();
            if (collider != null) Destroy(collider);

            var domeRenderer = backgroundDome.GetComponent<MeshRenderer>();
            backgroundMaterial = new Material(trajectoryRenderer.baseLineMaterial.shader)
            {
                name = "Drawing Background Material",
                renderQueue = 1000
            };
            domeRenderer.sharedMaterial = backgroundMaterial;
            domeRenderer.shadowCastingMode = ShadowCastingMode.Off;
            domeRenderer.receiveShadows = false;
            domeRenderer.lightProbeUsage = LightProbeUsage.Off;
            domeRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            domeRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            domeRenderer.sortingOrder = short.MinValue;
        }

        if (backgroundMaterial != null)
        {
            backgroundMaterial.color = background;
            if (backgroundMaterial.HasProperty("_BaseColor"))
                backgroundMaterial.SetColor("_BaseColor", background);
            if (backgroundMaterial.HasProperty("_Color"))
                backgroundMaterial.SetColor("_Color", background);
            if (backgroundMaterial.HasProperty("_EmissionColor"))
            {
                backgroundMaterial.EnableKeyword("_EMISSION");
                backgroundMaterial.SetColor("_EmissionColor", background);
            }
        }

        backgroundDome.transform.SetParent(xrCamera.transform, false);
        backgroundDome.transform.localPosition = Vector3.zero;
        backgroundDome.transform.localRotation = Quaternion.identity;
        backgroundDome.transform.localScale = new Vector3(-40f, 40f, 40f);
    }

    private void Update()
    {
        if (!setupComplete || ending || sessionEnded) return;

        bool xrActive = XRSettings.isDeviceActive;
        if (xrActive)
        {
            TryRequestRefreshRate();
            observedActiveXr = true;
            xrInactiveSince = -1f;
        }
        else if (observedActiveXr)
        {
            if (xrInactiveSince < 0f) xrInactiveSince = Time.unscaledTime;
            if (Time.unscaledTime - xrInactiveSince > 1f)
            {
                BeginEnding("xr_ended");
                return;
            }
        }

        if (Keyboard.current?.escapeKey.wasPressedThisFrame == true)
        {
            BeginEnding("keyboard_escape");
            return;
        }

        bool finishButtonHeld = false;
        try
        {
            finishButtonHeld = OVRInput.Get(
                OVRInput.Button.Two, OVRInput.Controller.RTouch);
        }
        catch
        {
            finishButtonHeld = false;
        }

        if (finishButtonHeld)
        {
            finishButtonHeldSeconds += Time.unscaledDeltaTime;
            if (finishButtonHeldSeconds >= config.finishButtonHoldSeconds)
                BeginEnding("right_b_hold");
        }
        else
        {
            finishButtonHeldSeconds = 0f;
        }
    }

    private void BeginEnding(string reason)
    {
        if (ending) return;
        ending = true;
        StartCoroutine(EndSession(reason));
    }

    private IEnumerator EndSession(string reason)
    {
        if (clutchController != null) clutchController.enabled = false;
        if (trajectoryRenderer != null) trajectoryRenderer.StopRecording();

        yield return new WaitForEndOfFrame();
        if (recorder != null && !string.IsNullOrEmpty(recorder.ScreenshotPath))
        {
            try
            {
                ScreenCapture.CaptureScreenshot(recorder.ScreenshotPath, 1);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[Drawing] Screenshot failed: {exception.Message}");
            }
        }

        yield return null;
        if (config.stopXrWhenSessionEnds)
            StopXrSubsystems();

        recorder?.FinalizeSession(reason);
        sessionEnded = true;
        ending = false;
        Debug.Log($"[Drawing] Session saved to {recorder?.SessionDirectory}");

        if (!Application.isEditor)
            Application.Quit();
    }

    private static void StopXrSubsystems()
    {
        try
        {
            var manager = XRGeneralSettings.Instance?.Manager;
            if (manager == null || manager.activeLoader == null) return;
            manager.StopSubsystems();
            manager.DeinitializeLoader();
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[Drawing] XR shutdown warning: {exception.Message}");
        }
    }

    private void TryRequestRefreshRate()
    {
        if (refreshRateChecked || Time.unscaledTime < nextRefreshRateCheck) return;
        nextRefreshRateCheck = Time.unscaledTime + 2f;
        // Some runtimes expose frequencies only after the XR session starts.
        if (++refreshRateAttempts >= 5) refreshRateChecked = true;
        if (OVRManager.display == null) return;
        try
        {
            float current = OVRManager.display.displayFrequency;
            var supported = OVRManager.display.displayFrequenciesAvailable;
            if (supported == null || supported.Length == 0) return;
            float selected = current;
            foreach (float rate in supported)
                if (rate > selected && rate <= config.preferredRefreshRateHz + 0.1f)
                    selected = rate;
            if (selected > current + 0.1f)
                OVRManager.display.displayFrequency = selected;
            refreshRateChecked = true;
            Debug.Log($"[Drawing] XR refresh request: {selected:F0} Hz (was {current:F0} Hz). Actual rate is logged in frame telemetry.");
        }
        catch (Exception exception)
        {
            refreshRateChecked = true;
            Debug.LogWarning($"[Drawing] XR refresh request unavailable: {exception.Message}");
        }
    }
}
