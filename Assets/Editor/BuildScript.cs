using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>The only BuildScript in the project. Batchmode:
/// Unity -quit -batchmode -projectPath . -executeMethod BuildScript.BuildApk -logFile build.log</summary>
public static class BuildScript
{
    public const string ScenePath = "Assets/Scenes/HoldTheTable.unity";
    public const string ApkPath = "Builds/HoldTheTable.apk";

    [MenuItem("HoldTheTable/Build APK")]
    public static void BuildApk()
    {
        bool batch = Application.isBatchMode;
        try
        {
            HoldTheTable.EditorTools.ProjectSetup.Apply();
            if (!File.Exists(ScenePath) || Environment.GetCommandLineArgs() is string[] a && Array.IndexOf(a, "-rebuildScene") >= 0)
                HoldTheTable.EditorTools.SceneBuilder.Build();
            var issues = HoldTheTable.EditorTools.Validator.Run();
            if (issues.Count > 0) Debug.LogWarning("[BuildScript] Validator reported " + issues.Count + " issue(s); see log above.");

            Directory.CreateDirectory("Builds");
            var opts = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = ApkPath,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None
            };
            EditorUserBuildSettings.buildAppBundle = false;
            var report = BuildPipeline.BuildPlayer(opts);
            var s = report.summary;
            Debug.Log($"[BuildScript] result={s.result} size={s.totalSize} errors={s.totalErrors} time={s.totalTime} path={Path.GetFullPath(ApkPath)}");
            if (batch) EditorApplication.Exit(s.result == BuildResult.Succeeded ? 0 : 1);
        }
        catch (Exception e)
        {
            Debug.LogError("[BuildScript] FAILED: " + e);
            if (batch) EditorApplication.Exit(2);
        }
    }
}
