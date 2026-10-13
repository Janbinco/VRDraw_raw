using System;
using System.Linq;
using Oculus.Interaction.PoseDetection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[InitializeOnLoad]
public static class DrawingProjectValidation
{
    private const string ScenePath = "Assets/Scenes/DrawingScene.unity";

    static DrawingProjectValidation()
    {
        EditorApplication.delayCall += OpenDrawingSceneOnFirstLaunch;
    }

    private static void OpenDrawingSceneOnFirstLaunch()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;

        var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (!string.IsNullOrEmpty(activeScene.path) || activeScene.isDirty) return;

        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        Debug.Log("[Drawing] Opened DrawingScene automatically.");
    }

    [MenuItem("Drawing/Validate Project")]
    public static void Run()
    {
        var enabledScenes = EditorBuildSettings.scenes
            .Where(scene => scene.enabled)
            .Select(scene => scene.path)
            .ToArray();
        Require(enabledScenes.Length == 1 && enabledScenes[0] == ScenePath,
            "DrawingScene must be the only enabled build scene.");

        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        Require(activeScene.rootCount == 1,
            "DrawingScene must contain only the drawing XR rig root.");
        foreach (var root in activeScene.GetRootGameObjects())
        {
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                Require(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transform.gameObject) == 0,
                    $"Missing script found on {transform.gameObject.name}.");
            }
        }
        var renderer = UnityEngine.Object.FindObjectsByType<FingerTrajectoryRenderer>(
            FindObjectsInactive.Include, FindObjectsSortMode.None)
            .FirstOrDefault(component => component.gameObject.scene == activeScene);
        Require(renderer != null, "FingerTrajectoryRenderer is missing from DrawingScene.");
        Require(renderer.GetComponent<OVRSkeleton>() != null,
            "The drawing renderer must share a GameObject with the right-hand OVRSkeleton.");
        Require(renderer.baseLineMaterial != null, "The drawing line material is not assigned.");

        var hands = UnityEngine.Object.FindObjectsByType<OVRHand>(
            FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Where(hand => hand.gameObject.scene == activeScene &&
                hand.GetComponent<SkinnedMeshRenderer>() != null)
            .ToArray();
        Require(hands.Length == 2,
            $"DrawingScene must contain exactly two rendered OVR hands; found {hands.Length}: " +
            string.Join(" | ", hands.Select(hand =>
                $"{hand.transform.parent?.name}/{hand.name}")));
        foreach (var hand in hands)
        {
            Require(hand.GetComponent<OVRMesh>()?.enabled == true,
                $"OVRMesh must be enabled on {hand.name}.");
            Require(hand.GetComponent<OVRMeshRenderer>()?.enabled == true,
                $"OVRMeshRenderer must be enabled on {hand.name}.");
            Require(hand.GetComponent<SkinnedMeshRenderer>()?.enabled == true,
                $"SkinnedMeshRenderer must be enabled on {hand.name}.");
        }

        var handProviders = UnityEngine.Object.FindObjectsByType<FingerFeatureStateProvider>(
            FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Where(provider => provider.gameObject.scene == activeScene)
            .ToArray();
        Require(handProviders.Length >= 2,
            "The scene must contain the left/right FingerFeatureStateProvider pair.");
        foreach (var provider in handProviders)
        {
            RequireReference(provider, "_hand");
        }

        var sceneBehaviours = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(
                FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Where(component => component != null && component.gameObject.scene == activeScene)
            .ToArray();
        foreach (var provider in sceneBehaviours.Where(component =>
                     component.GetType().Name == "TransformFeatureStateProvider"))
        {
            RequireReference(provider, "_hand");
            RequireReference(provider, "_hmd");
        }
        foreach (var provider in sceneBehaviours.Where(component =>
                     component.GetType().Name == "JointsRadiusFeature"))
        {
            RequireReference(provider, "_hand");
        }
        foreach (var provider in sceneBehaviours.Where(component =>
                     component.GetType().Name == "CenterEyeOffset"))
        {
            RequireReference(provider, "_hmd");
        }

        Require(UnityEngine.Object.FindObjectsByType<Canvas>(
            FindObjectsInactive.Include, FindObjectsSortMode.None).Length == 0,
            "DrawingScene must not contain legacy Canvas panels.");
        Require(!UnityEngine.Object.FindObjectsByType<MonoBehaviour>(
            FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Any(component => component != null &&
                component.GetType().Name == "FirstPersonLocomotor"),
            "DrawingScene must not contain locomotion components.");

        var config = Resources.Load<TextAsset>("DrawingConfig");
        Require(config != null && !string.IsNullOrWhiteSpace(config.text),
            "Resources/DrawingConfig.json is missing or empty.");
        var parsed = JsonUtility.FromJson<DrawingSessionConfig>(config.text);
        Require(parsed != null && Mathf.Approximately(parsed.strokeAlpha, 0.8f),
            "DrawingConfig must specify 80% stroke opacity.");
        Require(Mathf.Approximately(parsed.strokeWidthMeters, 0.008f),
            "DrawingConfig must keep the fixed 0.008 m brush width.");
        Require(Mathf.Approximately(parsed.minPointDistanceMeters, 0.001f),
            "DrawingConfig must keep the 0.001 m point spacing.");
        Require(Mathf.Approximately(parsed.sketchAlpha, 0.9f),
            "DrawingConfig must specify 90% sketch opacity.");
        Require(parsed.BackgroundColor != Color.white,
            "DrawingConfig background must not be white.");

        Debug.Log(
            $"[Drawing Validation] PASS: scene={ScenePath}, hands={hands.Length}, " +
            $"handProviders={handProviders.Length}, " +
            $"renderer={renderer.name}, material={renderer.baseLineMaterial.name}");
    }

    public static void ValidateBatch()
    {
        try
        {
            Run();
            EditorApplication.Exit(0);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorApplication.Exit(1);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("[Drawing Validation] " + message);
    }

    private static void RequireReference(MonoBehaviour component, string propertyName)
    {
        var property = new SerializedObject(component).FindProperty(propertyName);
        Require(property != null && property.objectReferenceValue != null,
            $"{component.gameObject.name}/{component.GetType().Name} is missing {propertyName}.");
    }
}
