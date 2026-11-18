using System;
using TMPro;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>
    /// Offline procedural NPC at the settlement edge near the player. Appears in the Upgrade phase, says one line,
    /// offers two upgrades based on the most used unit types and auto-skips after 6 s. Owner: Agent C.
    /// </summary>
    public class QuartermasterAgent : MonoBehaviour
    {
        public const float AutoSkipSeconds = 6f;

        static QuartermasterAgent instance;
        static bool quitting;
        public static QuartermasterAgent Instance
        {
            get
            {
                if (instance == null && !quitting && Application.isPlaying)
                    instance = new GameObject("QuartermasterAgent").AddComponent<QuartermasterAgent>();
                return instance;
            }
        }

        [Tooltip("Optional: visual root; built procedurally when empty.")]
        [OptionalRef] public Transform visualRoot;
        [OptionalRef] public TextMeshPro speech;

        readonly UpgradeOption[] offered = new UpgradeOption[2];
        Action onDone;
        bool active;
        float remaining;
        int lastShownSecond = -1;
        Transform body;
        Camera cam;

        public bool IsActive => active;
        public float Remaining => remaining;
        public UpgradeOption OfferA => offered[0];
        public UpgradeOption OfferB => offered[1];

        void Awake()
        {
            if (instance != null && instance != this) { Destroy(this); return; }
            instance = this;
            if (visualRoot == null) BuildVisual();
            visualRoot.gameObject.SetActive(false);
        }

        void OnApplicationQuit() => quitting = true;
        void OnDestroy() { if (instance == this) instance = null; }

        void BuildVisual()
        {
            var root = new GameObject("QuartermasterVisual").transform;
            root.SetParent(transform, false);
            visualRoot = root;
            body = new GameObject("Body").transform;
            body.SetParent(root, false);
            // ~9 cm tall little steward: cloak body, head, wide hat, satchel.
            Prim(PrimitiveType.Capsule, body, new Vector3(0f, 0.03f, 0f), new Vector3(0.035f, 0.03f, 0.03f), new Color(0.35f, 0.22f, 0.12f));
            Prim(PrimitiveType.Sphere, body, new Vector3(0f, 0.068f, 0f), new Vector3(0.026f, 0.026f, 0.026f), new Color(0.93f, 0.76f, 0.6f));
            Prim(PrimitiveType.Cylinder, body, new Vector3(0f, 0.08f, 0f), new Vector3(0.05f, 0.0025f, 0.05f), new Color(0.2f, 0.14f, 0.08f));
            Prim(PrimitiveType.Cylinder, body, new Vector3(0f, 0.088f, 0f), new Vector3(0.024f, 0.008f, 0.024f), new Color(0.2f, 0.14f, 0.08f));
            Prim(PrimitiveType.Sphere, body, new Vector3(0.006f, 0.071f, 0.012f), new Vector3(0.005f, 0.005f, 0.005f), new Color(0.1f, 0.1f, 0.1f));
            Prim(PrimitiveType.Sphere, body, new Vector3(-0.006f, 0.071f, 0.012f), new Vector3(0.005f, 0.005f, 0.005f), new Color(0.1f, 0.1f, 0.1f));
            Prim(PrimitiveType.Cube, body, new Vector3(0.02f, 0.025f, 0.006f), new Vector3(0.014f, 0.016f, 0.01f), new Color(0.75f, 0.55f, 0.2f));
            // Small crate of supplies beside him.
            Prim(PrimitiveType.Cube, root, new Vector3(-0.035f, 0.01f, 0f), new Vector3(0.022f, 0.02f, 0.022f), new Color(0.55f, 0.38f, 0.18f));

            if (speech == null)
            {
                var tgo = new GameObject("Speech");
                tgo.transform.SetParent(root, false);
                tgo.transform.localPosition = new Vector3(0f, 0.14f, 0f);
                speech = tgo.AddComponent<TextMeshPro>();
                speech.rectTransform.sizeDelta = new Vector2(0.32f, 0.08f);
                speech.fontSize = 0.16f;
                speech.alignment = TextAlignmentOptions.Center;
                speech.textWrappingMode = TextWrappingModes.Normal;
                speech.color = new Color(1f, 0.95f, 0.8f);
                speech.outlineWidth = 0.2f;
                speech.outlineColor = new Color32(30, 20, 10, 255);
            }
        }

        static void Prim(PrimitiveType t, Transform parent, Vector3 pos, Vector3 scale, Color c)
        {
            var go = GameObject.CreatePrimitive(t);
            var col = go.GetComponent<Collider>();
            if (col != null) DestroyImmediate(col);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = Mats.Lit(c);
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        /// <summary>Starts the upgrade interaction; onFinished is invoked exactly once (pick or auto-skip).</summary>
        public void Begin(Action onFinished)
        {
            if (active) Finish(false);
            onDone = onFinished;
            active = true;
            remaining = AutoSkipSeconds;
            lastShownSecond = -1;
            ChooseOptions();
            PlaceAtSettlementEdge();
            visualRoot.gameObject.SetActive(true);
            UpdateSpeech();
            if (UpgradeChoice.Instance != null) UpgradeChoice.Instance.Show(offered[0], offered[1], OnPick);
            if (ProceduralAudio.Instance != null) ProceduralAudio.Instance.Play(Sfx.Click, visualRoot.position);
        }

        /// <summary>Hides without invoking the callback (restart / new empire / game end).</summary>
        public void Cancel()
        {
            if (!active && (visualRoot == null || !visualRoot.gameObject.activeSelf)) return;
            active = false;
            onDone = null;
            if (visualRoot != null) visualRoot.gameObject.SetActive(false);
            if (UpgradeChoice.Instance != null) UpgradeChoice.Instance.Hide();
        }

        void OnPick(int index)
        {
            if (!active) return;
            var o = offered[Mathf.Clamp(index, 0, 1)];
            if (o != null && UpgradeState.Instance != null) UpgradeState.Instance.Apply(o);
            if (ProceduralAudio.Instance != null) ProceduralAudio.Instance.Play(Sfx.Place);
            Finish(true);
        }

        void Finish(bool invoke)
        {
            active = false;
            if (visualRoot != null) visualRoot.gameObject.SetActive(false);
            if (UpgradeChoice.Instance != null) UpgradeChoice.Instance.Hide();
            var cb = onDone;
            onDone = null;
            if (invoke) cb?.Invoke();
        }

        void Update()
        {
            if (!active) return;
            remaining -= Time.deltaTime;
            if (remaining <= 0f) { Finish(true); return; }
            UpdateSpeech();
            if (body != null) body.localPosition = new Vector3(0f, Mathf.Abs(Mathf.Sin(Time.time * 6f)) * 0.004f, 0f);
            if (cam == null) cam = Camera.main;
            if (cam != null && speech != null)
            {
                Vector3 d = speech.transform.position - cam.transform.position;
                if (d.sqrMagnitude > 1e-4f) speech.transform.rotation = Quaternion.LookRotation(d, Vector3.up);
            }
        }

        void UpdateSpeech()
        {
            if (speech == null) return;
            int sec = Mathf.CeilToInt(Mathf.Max(0f, remaining));
            if (sec == lastShownSecond) return;
            lastShownSecond = sec;
            speech.text = Strings.QuartermasterGreeting + "\n<size=70%>" + string.Format(Strings.QuartermasterSkip, sec) + "</size>";
        }

        void ChooseOptions()
        {
            var us = UpgradeState.Instance;
            UnitType t1 = UnitType.ArcherTower, t2 = UnitType.Wall;
            int c1 = 0, c2 = 0;
            if (us != null) us.MostUsed(out t1, out c1, out t2, out c2);
            var area = SettlementBuildArea.Instance;
            bool hasEconomy = area != null && area.IsFounded && area.EconomyCount > 0;
            if (c1 <= 0)
            {
                offered[0] = UpgradeOption.Create(UpgradeKind.ShockwaveCharge, UnitType.ArcherTower);
                offered[1] = hasEconomy
                    ? UpgradeOption.Create(UpgradeKind.Harvest, UnitType.ArcherTower)
                    : UpgradeOption.Create(RandomKind(UnitType.ArcherTower, UpgradeKind.ShockwaveCharge), UnitType.ArcherTower);
            }
            else if (c2 <= 0)
            {
                offered[0] = UpgradeOption.Create(RandomKind(t1, UpgradeKind.ShockwaveCharge), t1);
                offered[1] = UpgradeOption.Create(hasEconomy ? UpgradeKind.Harvest : UpgradeKind.ShockwaveCharge, t1);
            }
            else
            {
                offered[0] = UpgradeOption.Create(RandomKind(t1, UpgradeKind.ShockwaveCharge), t1);
                offered[1] = UpgradeOption.Create(RandomKind(t2, UpgradeKind.ShockwaveCharge), t2);
            }
        }

        static readonly UpgradeKind[] unitKinds = { UpgradeKind.Damage, UpgradeKind.Hp, UpgradeKind.Cost };

        static UpgradeKind RandomKind(UnitType t, UpgradeKind exclude)
        {
            for (int tries = 0; tries < 8; tries++)
            {
                var k = unitKinds[UnityEngine.Random.Range(0, unitKinds.Length)];
                if (k != exclude && UpgradeState.IsValid(k, t)) return k;
            }
            return UpgradeKind.Hp;
        }

        /// <summary>Stand at the settlement edge on the player's side, slightly to the side, always on supported surface.</summary>
        void PlaceAtSettlementEdge()
        {
            var gd = GameDirector.Instance;
            if (gd == null || gd.Hall == null) return;
            var s = gd.Surface;
            Vector3 hall = gd.Hall.transform.position;
            Vector3 up = s != null ? s.Pose.up : Vector3.up;
            if (cam == null) cam = Camera.main;
            Vector3 toCam = cam != null ? Vector3.ProjectOnPlane(cam.transform.position - hall, up) : Vector3.back;
            if (toCam.sqrMagnitude < 1e-4f) toCam = Vector3.back;
            toCam.Normalize();
            Vector3 side = Vector3.Cross(up, toCam).normalized;
            Vector3 p = hall + toCam * 0.25f - side * 0.18f;
            if (s != null)
            {
                p = s.ProjectOnto(p);
                if (!s.IsPointSupported(p))
                {
                    Vector3 e = s.ClosestEdgePoint(p);
                    Vector3 inward = Vector3.ProjectOnPlane(hall - e, up);
                    p = s.ProjectOnto(e + (inward.sqrMagnitude > 1e-6f ? inward.normalized * 0.04f : Vector3.zero));
                }
            }
            visualRoot.position = p;
            visualRoot.rotation = Quaternion.LookRotation(toCam, up);
        }
    }
}
