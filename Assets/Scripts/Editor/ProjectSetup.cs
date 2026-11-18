using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.Rendering;

namespace HoldTheTable.EditorTools
{
    /// <summary>Idempotent Quest/Android/XR configuration for HOLD THE TABLE (leader-owned).</summary>
    public static class ProjectSetup
    {
        [MenuItem("HoldTheTable/Apply Project Setup")]
        public static string Apply()
        {
            var sb = new StringBuilder();
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);

            var android = NamedBuildTarget.Android;
            PlayerSettings.companyName = "TableCiv";
            PlayerSettings.productName = "Hold The Table";
            PlayerSettings.SetApplicationIdentifier(android, "com.tableciv.holdthetable");
            PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan, GraphicsDeviceType.OpenGLES3 });

            // scripting define for MRUK-dependent code
            var defines = PlayerSettings.GetScriptingDefineSymbols(android);
            bool hasMruk = System.Type.GetType("Meta.XR.MRUtilityKit.MRUK, meta.xr.mrutilitykit") != null;
            if (hasMruk && !defines.Contains("HTT_MRUK"))
                PlayerSettings.SetScriptingDefineSymbols(android, string.IsNullOrEmpty(defines) ? "HTT_MRUK" : defines + ";HTT_MRUK");
            var sdef = PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.Standalone);
            if (hasMruk && !sdef.Contains("HTT_MRUK"))
                PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.Standalone, string.IsNullOrEmpty(sdef) ? "HTT_MRUK" : sdef + ";HTT_MRUK");
            sb.AppendLine("MRUK present=" + hasMruk);

            // Meta project config: hands, passthrough, scene, anchors
            var cfg = OVRProjectConfig.CachedProjectConfig;
            cfg.handTrackingSupport = OVRProjectConfig.HandTrackingSupport.ControllersAndHands;
            cfg.insightPassthroughSupport = OVRProjectConfig.FeatureSupport.Required;
            cfg.sceneSupport = OVRProjectConfig.FeatureSupport.Supported;
            cfg.anchorSupport = OVRProjectConfig.AnchorSupport.Enabled;
            cfg.requiresSystemKeyboard = true;
            OVRProjectConfig.CommitProjectConfig(cfg);

            // Always-included shaders (runtime Shader.Find in Mats)
            AddAlwaysIncluded("Universal Render Pipeline/Unlit");
            AddAlwaysIncluded("Universal Render Pipeline/Simple Lit");
            AddAlwaysIncluded("TextMeshPro/Distance Field");
            AddAlwaysIncluded("TextMeshPro/Mobile/Distance Field");

            // URP mobile: no shadows, no HDR / post
            foreach (var rp in new[] { GraphicsSettings.defaultRenderPipeline, QualitySettings.renderPipeline })
            {
                var urp = rp as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;
                if (urp == null) continue;
                urp.shadowDistance = 0f;
                urp.supportsHDR = false;
                urp.msaaSampleCount = 4;
                EditorUtility.SetDirty(urp);
            }
            QualitySettings.shadows = ShadowQuality.Disable;
            AssetDatabase.SaveAssets();
            return sb.ToString();
        }

        static void AddAlwaysIncluded(string shaderName)
        {
            var shader = Shader.Find(shaderName);
            if (shader == null) return;
            var gs = AssetDatabase.LoadAssetAtPath<Object>("ProjectSettings/GraphicsSettings.asset");
            var so = new SerializedObject(gs);
            var arr = so.FindProperty("m_AlwaysIncludedShaders");
            for (int i = 0; i < arr.arraySize; i++)
                if (arr.GetArrayElementAtIndex(i).objectReferenceValue == shader) return;
            arr.InsertArrayElementAtIndex(arr.arraySize);
            arr.GetArrayElementAtIndex(arr.arraySize - 1).objectReferenceValue = shader;
            so.ApplyModifiedProperties();
        }
    }
}
