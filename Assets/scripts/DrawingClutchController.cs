using System;
using Oculus.Interaction.Input;
using Oculus.Interaction.PoseDetection;
using UnityEngine;

[DefaultExecutionOrder(-800)]
[DisallowMultipleComponent]
public sealed class DrawingClutchController : MonoBehaviour
{
    public FingerTrajectoryRenderer trajectoryRenderer;
    public bool ClutchHeld { get; private set; }
    public bool EraseHeld { get; private set; }
    public bool LeftHandTracked => leftHand != null && leftHand.IsConnected && leftHand.IsTrackedDataValid;
    public bool LeftHandHighConfidence => LeftHandTracked && leftHand.IsHighConfidence;
    public Transform RightIndexTip { get; private set; }

    public event Action<bool, double> ClutchChanged;
    public event Action<bool, double> EraseChanged;

    private IHand leftHand;
    private int nextSetupFrame;

    public void Initialize(FingerTrajectoryRenderer rendererTarget)
    {
        trajectoryRenderer = rendererTarget;
        TrySetupInputs();
    }

    // Read gestures after the SDK has updated this frame's hand data.
    private void LateUpdate()
    {
        if (leftHand == null && Time.frameCount >= nextSetupFrame)
        {
            TrySetupInputs();
            nextSetupFrame = Time.frameCount + 60;
        }

        if (RightIndexTip == null)
            TryFindRightIndexTip();

        bool indexPinching = LeftHandHighConfidence &&
            leftHand.GetFingerIsPinching(HandFinger.Index);
        bool middlePinching = LeftHandHighConfidence &&
            leftHand.GetFingerIsPinching(HandFinger.Middle);

        // The middle-finger pinch owns erase. Giving it priority prevents a
        // noisy or overlapping index pinch from drawing while erasing.
        SetErase(middlePinching);
        SetClutch(indexPinching && !middlePinching);
    }

    public bool TryGetRightTipPose(out Vector3 position, out Quaternion rotation)
    {
        if (RightIndexTip == null) TryFindRightIndexTip();
        if (RightIndexTip == null)
        {
            position = default;
            rotation = Quaternion.identity;
            return false;
        }

        position = RightIndexTip.position;
        rotation = RightIndexTip.rotation;
        return true;
    }

    private void SetClutch(bool held)
    {
        if (ClutchHeld == held) return;

        ClutchHeld = held;
        if (trajectoryRenderer != null)
        {
            if (held) trajectoryRenderer.StartRecording();
            else trajectoryRenderer.StopRecording();
        }

        ClutchChanged?.Invoke(held, Time.realtimeSinceStartupAsDouble);
    }

    private void SetErase(bool held)
    {
        if (EraseHeld == held) return;

        EraseHeld = held;
        if (trajectoryRenderer != null)
        {
            if (RightIndexTip != null)
                trajectoryRenderer.erasePoint = RightIndexTip;

            if (held) trajectoryRenderer.BeginErase(this);
            else trajectoryRenderer.EndErase(this);
        }

        EraseChanged?.Invoke(held, Time.realtimeSinceStartupAsDouble);
    }

    private void TrySetupInputs()
    {
        var providers = FindObjectsByType<FingerFeatureStateProvider>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        foreach (var provider in providers)
        {
            if (provider == null || !provider.isActiveAndEnabled || provider.Hand == null)
                continue;
            if (provider.Hand.Handedness != Handedness.Left)
                continue;

            leftHand = provider.Hand;
            break;
        }

        TryFindRightIndexTip();
    }

    private void TryFindRightIndexTip()
    {
        if (trajectoryRenderer == null) return;
        var skeleton = trajectoryRenderer.skeleton;
        if (skeleton == null) skeleton = trajectoryRenderer.GetComponent<OVRSkeleton>();
        if (skeleton == null || skeleton.Bones == null) return;

        foreach (var bone in skeleton.Bones)
        {
            if (bone?.Transform == null) continue;
            if (bone.Transform.name.IndexOf("IndexTip", StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            RightIndexTip = bone.Transform;
            return;
        }
    }

    private void OnDisable()
    {
        SetErase(false);
        SetClutch(false);
    }
}
