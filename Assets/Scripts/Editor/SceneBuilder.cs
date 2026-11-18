using System.Collections.Generic;
using Oculus.Interaction.Editor.QuickActions;
using Oculus.Interaction.OVR.Editor.QuickActions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace HoldTheTable.EditorTools
{
    /// <summary>Generates the whole HOLD THE TABLE scene. Re-running rebuilds a fresh scene (no duplicates).</summary>
    public static class SceneBuilder
    {
        static readonly List<string> log = new List<string>();

        [MenuItem("HoldTheTable/Build Scene")]
        public static void Build()
        {
            log.Clear();
            ProjectSetup.Apply();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.62f, 0.6f, 0.58f);
            var sun = new GameObject("Key Light").AddComponent<Light>();
            sun.type = LightType.Directional; sun.shadows = LightShadows.None; sun.intensity = 1.0f;
            sun.transform.rotation = Quaternion.Euler(50, -30, 0);

            // ---------- XR rig (OVR + ISDK hands, passthrough) ----------
            OVRQuickActionsAPI.AddOVRInteractionRig();
            var rig = Object.FindFirstObjectByType<OVRCameraRig>();
            foreach (var t in rig.GetComponentsInChildren<Transform>(true))
                if (t.name.Contains("Locomot")) t.gameObject.SetActive(false);
            var mgr = rig.GetComponent<OVRManager>();
            mgr.isInsightPassthroughEnabled = true;
            var so = new SerializedObject(mgr);
            var origin = so.FindProperty("_trackingOriginType");
            if (origin != null) origin.enumValueIndex = (int)OVRManager.TrackingOrigin.FloorLevel;
            so.ApplyModifiedProperties();
            var pt = rig.gameObject.GetComponent<OVRPassthroughLayer>() ?? rig.gameObject.AddComponent<OVRPassthroughLayer>();
            pt.textureOpacity = 1f; // OVRPassthroughLayer always composites as an underlay in this SDK version
            foreach (var cam in rig.GetComponentsInChildren<Camera>(true))
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0, 0, 0, 0);
                cam.nearClipPlane = 0.02f; cam.farClipPlane = 50f;
                var ucd = cam.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
                if (ucd != null) { ucd.renderPostProcessing = false; ucd.renderShadows = false; }
            }
            var eye = rig.centerEyeAnchor ? rig.centerEyeAnchor.gameObject : rig.GetComponentInChildren<Camera>(true).gameObject;
            eye.tag = "MainCamera";
            if (!eye.GetComponent<AudioListener>()) eye.AddComponent<AudioListener>();

            // ---------- MRUK ----------
            var mrukPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Packages/com.meta.xr.mrutilitykit/Core/Tools/MRUK.prefab");
            if (mrukPrefab != null) PrefabUtility.InstantiatePrefab(mrukPrefab);
            else log.Add("MRUK prefab not found");

            // ---------- systems ----------
            var sys = new GameObject("Systems");
            Add(sys, "HandRig");
            Add(sys, "SurfaceRegistry");
            Add(sys, "TableAnchorBootstrap");
            Add(sys, "UpgradeState");
            Add(sys, "ScoreKeeper");
            Add(sys, "SettlementBuildArea");
            Add(sys, "BuildingFactory");
            Add(sys, "ProceduralAudio");
            Add(sys, "VfxPool");
            Add(sys, "ShockwaveAbility");
            Add(sys, "WaveSpawner");
            Add(sys, "QuartermasterAgent");
            var foundation = Add(sys, "EmpireFoundationController");
            Add(sys, "GameDirector");
            Add(sys, "StressTestHook");
            Add(sys, "BuildingRotator");
            Add(sys, "RoomCornerSpawns");
            Add(sys, "ClimbRoutes");

            // ---------- Imperial Scroll (world-space, grabbable) ----------
            var scroll = new GameObject("ImperialScroll");
            scroll.transform.position = new Vector3(0.25f, 1.0f, 0.45f);
            var scrollCanvas = MakeCanvas("ScrollCanvas", scroll.transform, new Vector3(0, 0, -0.004f), new Vector2(800, 1000), 0.0005f);
            var handle = new GameObject("GrabHandle");
            handle.transform.SetParent(scroll.transform, false);
            handle.transform.localPosition = new Vector3(0, 0.29f, 0);
            // generous grab zones: both rollers + both side edges (poke on the canvas is unaffected: it uses ISDK surfaces)
            var hc = handle.AddComponent<BoxCollider>(); hc.size = new Vector3(0.52f, 0.10f, 0.10f);
            var bc = scroll.AddComponent<BoxCollider>(); bc.center = new Vector3(0, -0.29f, 0); bc.size = new Vector3(0.52f, 0.10f, 0.10f);
            var lc = scroll.AddComponent<BoxCollider>(); lc.center = new Vector3(-0.235f, 0, 0); lc.size = new Vector3(0.06f, 0.52f, 0.08f);
            var rc = scroll.AddComponent<BoxCollider>(); rc.center = new Vector3(0.235f, 0, 0); rc.size = new Vector3(0.06f, 0.52f, 0.08f);
            Grab(scroll);
            Add(scroll, "GrabWatcher");
            var scrollOrbit = Add(scroll, "OrbitAnchor");
            SetFloat(scrollOrbit, "yaw", 22f); SetFloat(scrollOrbit, "pitch", -12f); SetFloat(scrollOrbit, "radius", 0.55f);
            var scrollComp = Add(scroll, "ImperialScroll");
            Set(scrollComp, "canvas", scrollCanvas);
            Set(scrollComp, "grabHandle", handle.transform);
            HandCanvas(scrollCanvas);

            // ---------- Town Hall token (grabbable, shown only during placement) ----------
            var token = new GameObject("TownHallToken");
            token.transform.position = new Vector3(0.25f, 0.7f, 0.45f);
            var tc = token.AddComponent<BoxCollider>(); tc.size = new Vector3(0.09f, 0.11f, 0.09f); tc.center = new Vector3(0, 0.05f, 0);
            Grab(token);
            Add(token, "GrabWatcher");
            Set(foundation, "hqToken", token);

            // ---------- Tray with 3 slots ----------
            var tray = new GameObject("Tray");
            tray.transform.position = new Vector3(0, 0.75f, 0.35f);
            var trayBase = GameObject.CreatePrimitive(PrimitiveType.Cube);
            trayBase.name = "TrayBase";
            Object.DestroyImmediate(trayBase.GetComponent<Collider>());
            trayBase.transform.SetParent(tray.transform, false);
            trayBase.transform.localScale = new Vector3(0.36f, 0.012f, 0.08f);
            trayBase.GetComponent<MeshRenderer>().sharedMaterial = TrayMaterial();
            string[] types = { "Wall", "ArcherTower", "Barricade", "Farm", "Mine" };
            for (int i = 0; i < types.Length; i++)
            {
                var slot = new GameObject("Slot_" + types[i]);
                slot.transform.SetParent(tray.transform, false);
                slot.transform.localPosition = new Vector3(-0.14f + 0.07f * i, 0.008f, 0);
                var tok = new GameObject("Token_" + types[i]);
                tok.transform.SetParent(slot.transform, false);
                var c = tok.AddComponent<BoxCollider>(); c.size = new Vector3(0.05f, 0.06f, 0.05f); c.center = new Vector3(0, 0.03f, 0);
                Grab(tok);
                Add(tok, "GrabWatcher");
                var ts = Add(slot, "TraySlot");
                SetEnum(ts, "type", i);
                Set(ts, "token", tok);
            }
            Set(foundation, "trayRoot", tray.transform);
            Set(Add(tray, "TrayVisibility"), "baseRenderer", trayBase.GetComponent<MeshRenderer>());
            tray.SetActive(true);

            // ---------- HUD / Upgrade / End canvases ----------
            var hudRoot = new GameObject("DiegeticHud");
            var hudCanvas = MakeCanvas("HudCanvas", hudRoot.transform, Vector3.zero, new Vector2(400, 260), 0.0005f);
            var hud = Add(hudRoot, "DiegeticHud");
            Set(hud, "canvas", hudCanvas);
            HandCanvas(hudCanvas);
            // grab bar above the HUD: pinch/grab it to move the HUD along the player's orbit
            var hudBar = GameObject.CreatePrimitive(PrimitiveType.Cube);
            hudBar.name = "HudGrabBar";
            Object.DestroyImmediate(hudBar.GetComponent<Collider>());
            hudBar.transform.SetParent(hudRoot.transform, false);
            hudBar.transform.localPosition = new Vector3(0, 0.078f, 0.004f);
            hudBar.transform.localScale = new Vector3(0.12f, 0.012f, 0.012f);
            hudBar.GetComponent<MeshRenderer>().sharedMaterial = TrayMaterial();
            var hudCol = hudRoot.AddComponent<BoxCollider>();
            hudCol.center = new Vector3(0, 0.078f, 0.004f); hudCol.size = new Vector3(0.14f, 0.035f, 0.035f);
            Grab(hudRoot);
            Add(hudRoot, "GrabWatcher");
            var hudOrbit = Add(hudRoot, "OrbitAnchor");
            SetFloat(hudOrbit, "yaw", -24f); SetFloat(hudOrbit, "pitch", -20f); SetFloat(hudOrbit, "radius", 0.42f);

            var upRoot = new GameObject("UpgradeChoice");
            upRoot.transform.position = new Vector3(0, 1.1f, 0.6f);
            var upCanvas = MakeCanvas("UpgradeCanvas", upRoot.transform, Vector3.zero, new Vector2(900, 420), 0.0006f);
            Set(Add(upRoot, "UpgradeChoice"), "canvas", upCanvas);
            HandCanvas(upCanvas);

            var endRoot = new GameObject("EndScreen");
            endRoot.transform.position = new Vector3(0, 1.2f, 0.6f);
            var endCanvas = MakeCanvas("EndCanvas", endRoot.transform, Vector3.zero, new Vector2(800, 500), 0.0006f);
            Set(Add(endRoot, "EndScreen"), "canvas", endCanvas);
            HandCanvas(endCanvas);

            System.IO.Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, BuildScript.ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(BuildScript.ScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log("[SceneBuilder] built " + BuildScript.ScenePath + (log.Count > 0 ? "\n  " + string.Join("\n  ", log) : ""));
        }

        static Material TrayMaterial()
        {
            const string path = "Assets/Scenes/TrayWood.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m != null) return m;
            m = new Material(Shader.Find("Universal Render Pipeline/Simple Lit"));
            m.SetColor("_BaseColor", new Color(0.45f, 0.3f, 0.18f));
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        static void Grab(GameObject go)
        {
            QuickActionsAPI.AddGrabInteraction(go);
            var rb = go.GetComponent<Rigidbody>();
            if (rb != null) { rb.isKinematic = true; rb.useGravity = false; }
        }

        static void HandCanvas(Canvas c)
        {
            QuickActionsAPI.AddPokeCanvasInteraction(c.gameObject);
            QuickActionsAPI.AddRayCanvasInteraction(c.gameObject);
        }

        static Canvas MakeCanvas(string name, Transform parent, Vector3 localPos, Vector2 size, float scale)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            go.transform.SetParent(parent, false);
            var c = go.GetComponent<Canvas>();
            c.renderMode = RenderMode.WorldSpace;
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = size;
            rt.localScale = Vector3.one * scale;
            rt.localPosition = localPos;
            rt.localRotation = Quaternion.identity;
            return c;
        }

        static Component Add(GameObject go, string typeName)
        {
            var t = Validator.FindType("HoldTheTable." + typeName);
            if (t == null) { log.Add("MISSING TYPE " + typeName); Debug.LogError("[SceneBuilder] missing type " + typeName); return null; }
            return go.GetComponent(t) ?? go.AddComponent(t);
        }

        static void Set(Component c, string field, Object value)
        {
            if (c == null) return;
            var so = new SerializedObject(c);
            var p = so.FindProperty(field);
            if (p == null) { log.Add($"no field {c.GetType().Name}.{field}"); Debug.LogError($"[SceneBuilder] no field {c.GetType().Name}.{field}"); return; }
            p.objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void SetFloat(Component c, string field, float value)
        {
            if (c == null) return;
            var so = new SerializedObject(c);
            var p = so.FindProperty(field);
            if (p == null) { log.Add($"no field {c.GetType().Name}.{field}"); Debug.LogError($"[SceneBuilder] no field {c.GetType().Name}.{field}"); return; }
            p.floatValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void SetEnum(Component c, string field, int value)
        {
            if (c == null) return;
            var so = new SerializedObject(c);
            var p = so.FindProperty(field);
            if (p == null) { log.Add($"no field {c.GetType().Name}.{field}"); return; }
            p.enumValueIndex = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
