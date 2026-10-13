using System;
using UnityEngine;

[Serializable]
public sealed class DrawingSessionConfig
{
    public string configVersion = "drawing-config-3";
    public string demoId = "drawing";
    public string conditionId = "clutch";
    public string participantId = "";
    public float strokeWidthMeters = 0.008f;
    [Min(0.0001f)] public float minPointDistanceMeters = 0.001f;
    [Range(0f, 1f)] public float strokeAlpha = 0.8f;
    [Range(0f, 1f)] public float sketchAlpha = 0.9f;
    [Min(0.001f)] public float eraseRadiusMeters = 0.05f;
    [Range(0f, 1f)] public float backgroundRed = 0.788f;
    [Range(0f, 1f)] public float backgroundGreen = 0.651f;
    [Range(0f, 1f)] public float backgroundBlue = 1f;
    [Min(0.25f)] public float autoSaveIntervalSeconds = 2f;
    [Range(10f, 120f)] public float telemetryRateHz = 30f;
    [Range(72f, 120f)] public float preferredRefreshRateHz = 90f;
    [Min(0.5f)] public float finishButtonHoldSeconds = 2f;
    public bool stopXrWhenSessionEnds = true;

    public Color BackgroundColor => new Color(
        backgroundRed, backgroundGreen, backgroundBlue, 1f);

    public static DrawingSessionConfig Load()
    {
        var config = new DrawingSessionConfig();
        var asset = Resources.Load<TextAsset>("DrawingConfig");
        if (asset != null && !string.IsNullOrWhiteSpace(asset.text))
        {
            try
            {
                JsonUtility.FromJsonOverwrite(asset.text, config);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[Drawing] Could not parse DrawingConfig.json: {exception.Message}");
            }
        }

        ApplyCommandLine(config);
        ApplyUrlQuery(config);
        config.strokeAlpha = Mathf.Clamp01(config.strokeAlpha);
        config.sketchAlpha = Mathf.Clamp01(config.sketchAlpha);
        config.strokeWidthMeters = Mathf.Max(0.0001f, config.strokeWidthMeters);
        config.minPointDistanceMeters = Mathf.Max(0.0001f, config.minPointDistanceMeters);
        config.eraseRadiusMeters = Mathf.Max(0.001f, config.eraseRadiusMeters);
        config.backgroundRed = Mathf.Clamp01(config.backgroundRed);
        config.backgroundGreen = Mathf.Clamp01(config.backgroundGreen);
        config.backgroundBlue = Mathf.Clamp01(config.backgroundBlue);
        config.autoSaveIntervalSeconds = Mathf.Max(0.25f, config.autoSaveIntervalSeconds);
        config.telemetryRateHz = Mathf.Clamp(config.telemetryRateHz, 10f, 120f);
        config.preferredRefreshRateHz = Mathf.Clamp(config.preferredRefreshRateHz, 72f, 120f);
        return config;
    }

    private static void ApplyCommandLine(DrawingSessionConfig config)
    {
        var args = Environment.GetCommandLineArgs();
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (args[index] == "--participant") config.participantId = args[index + 1];
            if (args[index] == "--condition") config.conditionId = args[index + 1];
            if (args[index] == "--demo") config.demoId = args[index + 1];
        }
    }

    private static void ApplyUrlQuery(DrawingSessionConfig config)
    {
        if (string.IsNullOrWhiteSpace(Application.absoluteURL)) return;

        try
        {
            var uri = new Uri(Application.absoluteURL);
            var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
            foreach (var pair in query)
            {
                var parts = pair.Split('=', 2);
                if (parts.Length != 2) continue;
                string key = Uri.UnescapeDataString(parts[0]);
                string value = Uri.UnescapeDataString(parts[1]);
                if (key == "participant") config.participantId = value;
                if (key == "condition") config.conditionId = value;
                if (key == "demo") config.demoId = value;
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[Drawing] Could not parse URL parameters: {exception.Message}");
        }
    }
}
