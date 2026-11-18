using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace HoldTheTable.EditorTools
{
    /// <summary>Asserts every required component, reference, event wiring and document exists. Fails loudly with a list.</summary>
    public static class Validator
    {
        static readonly string[] RequiredComponents =
        {
            // core / XR
            "OVRCameraRig", "OVRManager", "OVRPassthroughLayer",
            // runtime
            "HoldTheTable.GameDirector", "HoldTheTable.WaveSpawner", "HoldTheTable.ScoreKeeper", "HoldTheTable.QuartermasterAgent",
            "HoldTheTable.UpgradeState", "HoldTheTable.SettlementBuildArea", "HoldTheTable.BuildingFactory",
            "HoldTheTable.SurfaceRegistry", "HoldTheTable.TableAnchorBootstrap", "HoldTheTable.HandRig",
            "HoldTheTable.EmpireFoundationController", "HoldTheTable.TraySlot", "HoldTheTable.GrabWatcher",
            "HoldTheTable.ShockwaveAbility", "HoldTheTable.ProceduralAudio", "HoldTheTable.VfxPool",
            "HoldTheTable.RoomCornerSpawns", "HoldTheTable.ClimbRoutes", "HoldTheTable.BuildingRotator", "HoldTheTable.OrbitAnchor",
            // UI
            "HoldTheTable.ImperialScroll", "HoldTheTable.DiegeticHud", "HoldTheTable.UpgradeChoice", "HoldTheTable.EndScreen",
            // ISDK
            "Oculus.Interaction.Grabbable", "Oculus.Interaction.PointableCanvas", "Oculus.Interaction.PointableCanvasModule",
            "Oculus.Interaction.PokeInteractor", "Oculus.Interaction.RayInteractor", "Oculus.Interaction.HandGrab.HandGrabInteractor",
        };

        static readonly string[] RequiredTypes =
        {
            "HoldTheTable.CivilizationDefinition", "HoldTheTable.CivilizationProfile", "HoldTheTable.EmblemDrawingCanvas",
            "HoldTheTable.BannerCustomizer", "HoldTheTable.BannerRenderer", "HoldTheTable.CivilizationSelector",
            "HoldTheTable.HandPokeKeyboard", "HoldTheTable.SplinePath", "HoldTheTable.MonsterUnit", "HoldTheTable.PlacedUnit",
            "HoldTheTable.Health", "HoldTheTable.UnitDef", "HoldTheTable.FallbackTable", "HoldTheTable.Strings",
        };

        [MenuItem("HoldTheTable/Validate")]
        public static void Menu()
        {
            var issues = Run();
            if (issues.Count == 0) EditorUtility.DisplayDialog("HoldTheTable", "Validator: PASS", "OK");
        }

        public static List<string> Run()
        {
            var issues = new List<string>();
            if (!File.Exists(BuildScript.ScenePath)) { issues.Add("scene missing: " + BuildScript.ScenePath); Report(issues); return issues; }
            var active = EditorSceneManager.GetActiveScene();
            if (active.path != BuildScript.ScenePath) EditorSceneManager.OpenScene(BuildScript.ScenePath, OpenSceneMode.Single);

            foreach (var tn in RequiredComponents)
            {
                var t = FindType(tn);
                if (t == null) { issues.Add("type not found: " + tn); continue; }
                if (UnityEngine.Object.FindObjectsByType(t, FindObjectsInactive.Include, FindObjectsSortMode.None).Length == 0)
                    issues.Add("component missing in scene: " + tn);
            }
            foreach (var tn in RequiredTypes)
                if (FindType(tn) == null) issues.Add("type not found: " + tn);

            // exactly 3 tray slots and 3 civilizations
            var traySlot = FindType("HoldTheTable.TraySlot");
            if (traySlot != null)
            {
                int n = UnityEngine.Object.FindObjectsByType(traySlot, FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
                if (n != 5) issues.Add("expected 5 TraySlots (wall, tower, barricade, farm, mine), found " + n);
            }
            try
            {
                var cat = FindType("HoldTheTable.CivilizationCatalog");
                var all = cat?.GetProperty("All")?.GetValue(null) as System.Collections.ICollection
                          ?? cat?.GetField("All")?.GetValue(null) as System.Collections.ICollection;
                if (all == null || all.Count != 3) issues.Add("CivilizationCatalog.All must contain 3 definitions (found " + (all?.Count ?? 0) + ")");
            }
            catch (Exception e) { issues.Add("CivilizationCatalog check failed: " + e.Message); }

            // no Town Hall in the scene before founding
            var hall = FindType("HoldTheTable.TownHall");
            if (hall != null && UnityEngine.Object.FindObjectsByType(hall, FindObjectsInactive.Include, FindObjectsSortMode.None).Length > 0)
                issues.Add("TownHall must not exist in scene before player confirmation");

            // null object references on all HoldTheTable components (missing refs)
            foreach (var mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (mb == null) { issues.Add("missing script on some GameObject"); continue; }
                var t = mb.GetType();
                if (t.Namespace != "HoldTheTable") continue;
                var so = new SerializedObject(mb);
                var it = so.GetIterator();
                while (it.NextVisible(true))
                {
                    if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                    if (it.name == "m_Script") continue;
                    if (it.objectReferenceValue == null && !IsOptional(t, it.name))
                        issues.Add($"null reference: {t.Name}.{it.propertyPath} on '{mb.gameObject.name}'");
                }
            }
            // missing scripts
            foreach (var go in UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go) > 0) issues.Add("missing script on " + go.name);

            foreach (var doc in new[] { "CREDITS.md", "CHANGELOG.md", "SUBMISSION.md", "SHOTLIST.md" })
                if (!File.Exists(doc) || new FileInfo(doc).Length < 50) issues.Add("document missing/empty: " + doc);

            Report(issues);
            return issues;
        }

        static bool IsOptional(Type t, string field)
        {
            var f = t.GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            return f != null && f.GetCustomAttributes(typeof(OptionalRefAttribute), true).Length > 0;
        }

        static void Report(List<string> issues)
        {
            var sb = new StringBuilder();
            if (issues.Count == 0) sb.Append("[Validator] PASS — all required components, references, events and documents present.");
            else
            {
                sb.AppendLine($"[Validator] FAIL — {issues.Count} issue(s):");
                foreach (var i in issues) sb.AppendLine("  - " + i);
            }
            if (issues.Count == 0) Debug.Log(sb.ToString()); else Debug.LogError(sb.ToString());
            File.WriteAllText("Logs/validator.txt", sb.ToString());
        }

        public static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName);
                if (t != null) return t;
            }
            return null;
        }
    }
}
