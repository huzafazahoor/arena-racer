using System;
using System.IO;
using Fusion.Photon.Realtime;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Batch WebGL build for the portfolio site:
///   Unity.exe -batchmode -nographics -quit -projectPath . -buildTarget WebGL -executeMethod PortfolioBuild.BuildWebGL -outPath Builds/arena-racer
///
/// The Photon App ID is read from photon-appid.txt next to the project (git-ignored), written into
/// PhotonAppSettings only for the duration of the build, then blanked again so it never lands in the repo.
/// Without that file the build still works: the game starts straight in offline mode against bots.
/// </summary>
public static class PortfolioBuild
{
    const string AppSettingsPath = "Assets/Photon/Fusion/Resources/PhotonAppSettings.asset";
    const string FusionConfigPath = "Assets/Photon/Fusion/Resources/NetworkProjectConfig.fusion";

    public static void BuildWebGL()
    {
        string outPath = Argument("-outPath") ?? "Builds/arena-racer";

        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
        PlayerSettings.WebGL.decompressionFallback = true;  // works on hosts that don't send Content-Encoding (GitHub Pages)
        PlayerSettings.WebGL.nameFilesAsHashes = false;
        PlayerSettings.WebGL.dataCaching = true;
        PlayerSettings.WebGL.template = "APPLICATION:Minimal";
        PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly;

        // Make sure Fusion's prefab table includes every network prefab before building.
        AssetDatabase.ImportAsset(FusionConfigPath, ImportAssetOptions.ForceUpdate);

        var appSettings = AssetDatabase.LoadAssetAtPath<PhotonAppSettings>(AppSettingsPath);
        string appId = ReadAppId();
        SetAppId(appSettings, appId);
        Debug.Log(string.IsNullOrEmpty(appId)
            ? "PortfolioBuild: no photon-appid.txt, building OFFLINE-only (bots)."
            : "PortfolioBuild: Photon App ID found, building with online play.");

        BuildReport report;
        try
        {
            report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { "Assets/ArenaRacer/Scenes/Arena.unity" },
                locationPathName = outPath,
                target = BuildTarget.WebGL,
                options = BuildOptions.None,
            });
        }
        finally
        {
            SetAppId(appSettings, "");
        }

        Debug.Log($"PortfolioBuild: {report.summary.result}, {report.summary.totalSize / (1024f * 1024f):0.0} MB, " +
                  $"{report.summary.totalErrors} errors -> {outPath}");
        if (Application.isBatchMode) EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
    }

    static string ReadAppId()
    {
        string file = Path.Combine(Directory.GetCurrentDirectory(), "photon-appid.txt");
        return File.Exists(file) ? File.ReadAllText(file).Trim() : "";
    }

    static void SetAppId(PhotonAppSettings settings, string appId)
    {
        settings.AppSettings.AppIdFusion = appId;
        EditorUtility.SetDirty(settings);
        AssetDatabase.SaveAssets();
    }

    static string Argument(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }
}
