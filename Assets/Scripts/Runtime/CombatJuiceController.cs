using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>
    /// Self-bootstrapping combat feedback: pooled floating damage numbers, white hit-flash on monsters
    /// (MaterialPropertyBlock) and pooled fire/smoke quads on structures below 35% HP.
    /// Polls Health values at a throttled interval (no per-hit subscriptions needed), resets on GameDirector.GameReset.
    /// Visible text is numeric/symbolic only ("-3.5", "x"), so no Strings.cs keys are required.
    /// </summary>
    public class CombatJuiceController : MonoBehaviour
    {
        public static CombatJuiceController Instance { get; private set; }

        const int TextPoolSize = 24;
        const int FxPoolSize = 16;
        const float PollInterval = 0.05f;
        const float TextLife = 0.8f;
        const float TextRise = 0.07f;
        const float FlashTime = 0.1f;
        const float DamagedFraction = 0.35f;
        const float TextFontSize = 0.22f;

        class Track
        {
            public Health health;
            public Transform tr;
            public Renderer[] renderers;     // monsters only
            public float lastHp;
            public float flashT;
            public bool isMonster;
            public bool seenThisPoll;
            public int fxCount;
        }

        class FloatText
        {
            public Transform tr;
            public TextMeshPro tmp;
            public float t;
            public bool active;
            public Color color;
        }

        class FxQuad
        {
            public Transform tr;
            public MeshRenderer mr;
            public Track owner;
            public float phase;
            public bool smoke;
            public bool active;
            public Vector3 offset;
        }

        readonly Dictionary<Health, Track> tracks = new Dictionary<Health, Track>(64);
        readonly List<Health> stale = new List<Health>(16);
        readonly List<Track> flashing = new List<Track>(32);
        readonly FloatText[] texts = new FloatText[TextPoolSize];
        readonly FxQuad[] fx = new FxQuad[FxPoolSize];
        readonly MaterialPropertyBlock flashBlock = new MaterialPropertyBlock();
        readonly MaterialPropertyBlock fxBlock = new MaterialPropertyBlock();

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        Camera cam;
        Mesh quadMesh;
        Material fxMaterial;
        int nextText;
        float pollTimer;
        float fxAssignTimer;
        GameDirector subscribedTo;
        bool killedSubscribed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { Instance = null; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (Instance != null) return;
            var existing = FindFirstObjectByType<CombatJuiceController>();
            if (existing != null) { Instance = existing; return; }
            var go = new GameObject("CombatJuiceController");
            go.AddComponent<CombatJuiceController>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            BuildPools();
        }

        void OnEnable()
        {
            if (Instance != this) return;
            if (!killedSubscribed) { MonsterUnit.Killed += OnMonsterKilled; killedSubscribed = true; }
        }

        void OnDisable()
        {
            if (killedSubscribed) { MonsterUnit.Killed -= OnMonsterKilled; killedSubscribed = false; }
            if (subscribedTo != null) { subscribedTo.GameReset -= ResetAll; subscribedTo = null; }
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ------------------------------------------------------------ pools
        void BuildPools()
        {
            quadMesh = new Mesh { name = "JuiceQuad" };
            quadMesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f)
            };
            quadMesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
            quadMesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            quadMesh.RecalculateBounds();
            quadMesh.RecalculateNormals();
            fxMaterial = Mats.Transparent(new Color(1f, 1f, 1f, 1f));

            for (int i = 0; i < TextPoolSize; i++)
            {
                var go = new GameObject("JuiceText_" + i);
                go.transform.SetParent(transform, false);
                var t = go.AddComponent<TextMeshPro>();
                t.fontSize = TextFontSize;
                t.alignment = TextAlignmentOptions.Center;
                t.rectTransform.sizeDelta = new Vector2(0.2f, 0.06f);
                t.textWrappingMode = TextWrappingModes.NoWrap;
                t.raycastTarget = false;
                t.text = "";
                go.SetActive(false);
                texts[i] = new FloatText { tr = go.transform, tmp = t };
            }

            for (int i = 0; i < FxPoolSize; i++)
            {
                var go = new GameObject("JuiceFx_" + i);
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = quadMesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = fxMaterial;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                go.SetActive(false);
                fx[i] = new FxQuad { tr = go.transform, mr = mr };
            }
        }

        // ------------------------------------------------------------ reset
        void TrySubscribeDirector()
        {
            var gd = GameDirector.Instance;
            if (gd == subscribedTo) return;
            if (subscribedTo != null) subscribedTo.GameReset -= ResetAll;
            subscribedTo = gd;
            if (subscribedTo != null) subscribedTo.GameReset += ResetAll;
        }

        void ResetAll()
        {
            for (int i = 0; i < texts.Length; i++)
            {
                texts[i].active = false;
                texts[i].tr.gameObject.SetActive(false);
            }
            for (int i = 0; i < fx.Length; i++) ReleaseFx(fx[i]);
            for (int i = 0; i < flashing.Count; i++) ClearFlash(flashing[i]);
            flashing.Clear();
            tracks.Clear();
        }

        // ------------------------------------------------------------ polling
        void Update()
        {
            if (cam == null) cam = Camera.main;
            float dt = Time.deltaTime;

            pollTimer -= dt;
            if (pollTimer <= 0f)
            {
                pollTimer = PollInterval;
                TrySubscribeDirector();
                Poll();
            }

            UpdateFlashes(dt);
            UpdateTexts(dt);
            UpdateFx();
        }

        void Poll()
        {
            var monsters = MonsterUnit.Active;
            for (int i = 0; i < monsters.Count; i++)
            {
                var m = monsters[i];
                if (m == null || m.health == null || !m.IsAlive) continue;
                Observe(m.health, m.transform, true, m.AimPoint + Vector3.up * 0.02f);
            }

            var units = PlacedUnit.All;
            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u == null || u.health == null) continue;
                Observe(u.health, u.transform, false, u.transform.position + Vector3.up * 0.08f);
            }

            var hall = TownHall.Current;
            if (hall != null && hall.health != null)
                Observe(hall.health, hall.transform, false, hall.transform.position + Vector3.up * 0.14f);

            // fire/smoke assignment and stale pruning at a lower rate
            fxAssignTimer -= PollInterval;
            if (fxAssignTimer <= 0f)
            {
                fxAssignTimer = 0.25f;
                stale.Clear();
                foreach (var kv in tracks)
                {
                    var tr = kv.Value;
                    if (kv.Key == null || tr.tr == null) { stale.Add(kv.Key); continue; }
                    if (tr.isMonster) continue;
                    if (tr.fxCount == 0 && !kv.Key.IsDead && kv.Key.Fraction < DamagedFraction
                        && tr.tr.gameObject.activeInHierarchy)
                        AssignFx(tr);
                }
                for (int i = 0; i < stale.Count; i++)
                {
                    tracks.Remove(stale[i]);
                }
            }
        }

        void Observe(Health h, Transform tr, bool isMonster, Vector3 textPos)
        {
            if (!tracks.TryGetValue(h, out var t))
            {
                t = new Track { health = h, tr = tr, isMonster = isMonster, lastHp = h.Current };
                if (isMonster) t.renderers = tr.GetComponentsInChildren<Renderer>(true);
                tracks[h] = t;
                return;
            }
            float cur = h.Current;
            float delta = t.lastHp - cur;
            t.lastHp = cur;
            if (delta > 0.01f)
            {
                SpawnText(textPos, delta, isMonster ? new Color(1f, 0.92f, 0.35f, 1f) : new Color(1f, 0.35f, 0.3f, 1f), true);
                if (isMonster) StartFlash(t);
            }
        }

        void OnMonsterKilled(MonsterUnit m)
        {
            if (m == null) return;
            // death marker: white "x" rising from the monster
            SpawnMarker(m.AimPoint + Vector3.up * 0.03f, new Color(1f, 1f, 1f, 1f));
            if (m.health != null && tracks.TryGetValue(m.health, out var t)) { ClearFlash(t); flashing.Remove(t); }
        }

        // ------------------------------------------------------------ floating text
        FloatText NextText()
        {
            // prefer a free slot, otherwise recycle the oldest in round-robin order
            for (int k = 0; k < texts.Length; k++)
            {
                int idx = (nextText + k) % texts.Length;
                if (!texts[idx].active) { nextText = (idx + 1) % texts.Length; return texts[idx]; }
            }
            var f = texts[nextText];
            nextText = (nextText + 1) % texts.Length;
            return f;
        }

        void SpawnText(Vector3 pos, float amount, Color c, bool damage)
        {
            var f = NextText();
            f.tmp.SetText(damage ? "-{0:1}" : "+{0:1}", amount);
            Activate(f, pos, c);
        }

        void SpawnMarker(Vector3 pos, Color c)
        {
            var f = NextText();
            f.tmp.SetText("x");
            Activate(f, pos, c);
        }

        void Activate(FloatText f, Vector3 pos, Color c)
        {
            f.t = 0f;
            f.color = c;
            f.active = true;
            f.tmp.color = c;
            f.tr.position = pos;
            f.tr.gameObject.SetActive(true);
            Face(f.tr);
        }

        void Face(Transform t)
        {
            if (cam == null) return;
            Vector3 d = t.position - cam.transform.position;
            if (d.sqrMagnitude > 1e-6f) t.rotation = Quaternion.LookRotation(d, Vector3.up);
        }

        void UpdateTexts(float dt)
        {
            for (int i = 0; i < texts.Length; i++)
            {
                var f = texts[i];
                if (!f.active) continue;
                f.t += dt;
                float k = f.t / TextLife;
                if (k >= 1f)
                {
                    f.active = false;
                    f.tr.gameObject.SetActive(false);
                    continue;
                }
                f.tr.position += Vector3.up * (TextRise / TextLife * dt);
                Face(f.tr);
                float scale = k < 0.15f ? Mathf.Lerp(0.6f, 1.2f, k / 0.15f) : Mathf.Lerp(1.2f, 1f, (k - 0.15f) / 0.85f);
                f.tr.localScale = new Vector3(scale, scale, scale);
                Color c = f.color;
                c.a = k < 0.5f ? 1f : 1f - (k - 0.5f) * 2f;
                f.tmp.color = c;
            }
        }

        // ------------------------------------------------------------ hit flash
        void StartFlash(Track t)
        {
            if (t.renderers == null) return;
            if (t.flashT <= 0f)
            {
                flashBlock.Clear();
                flashBlock.SetColor(BaseColorId, Color.white);
                for (int i = 0; i < t.renderers.Length; i++)
                {
                    var r = t.renderers[i];
                    if (r != null) r.SetPropertyBlock(flashBlock);
                }
                flashing.Add(t);
            }
            t.flashT = FlashTime;
        }

        void ClearFlash(Track t)
        {
            t.flashT = 0f;
            if (t.renderers == null) return;
            for (int i = 0; i < t.renderers.Length; i++)
            {
                var r = t.renderers[i];
                if (r != null) r.SetPropertyBlock(null);
            }
        }

        void UpdateFlashes(float dt)
        {
            for (int i = flashing.Count - 1; i >= 0; i--)
            {
                var t = flashing[i];
                t.flashT -= dt;
                if (t.flashT <= 0f)
                {
                    ClearFlash(t);
                    flashing.RemoveAt(i);
                }
            }
        }

        // ------------------------------------------------------------ fire / smoke
        void AssignFx(Track owner)
        {
            // needs two free quads (fire + smoke)
            FxQuad a = FreeFx(), b = null;
            if (a == null) return;
            a.active = true; // reserve
            b = FreeFx();
            if (b == null) { a.active = false; return; }
            ActivateFx(a, owner, false, 0f);
            ActivateFx(b, owner, true, 0.5f);
            owner.fxCount = 2;
        }

        FxQuad FreeFx()
        {
            for (int i = 0; i < fx.Length; i++) if (!fx[i].active) return fx[i];
            return null;
        }

        void ActivateFx(FxQuad q, Track owner, bool smoke, float phase)
        {
            q.owner = owner;
            q.smoke = smoke;
            q.phase = phase + (owner.health.GetHashCode() & 7) * 0.11f;
            q.offset = new Vector3(((owner.health.GetHashCode() >> 3) & 3) * 0.008f - 0.012f, 0.05f, 0f);
            q.active = true;
            q.tr.gameObject.SetActive(true);
        }

        void ReleaseFx(FxQuad q)
        {
            if (q.owner != null) q.owner.fxCount = 0;
            q.owner = null;
            q.active = false;
            q.tr.gameObject.SetActive(false);
        }

        void UpdateFx()
        {
            float time = Time.time;
            for (int i = 0; i < fx.Length; i++)
            {
                var q = fx[i];
                if (!q.active) continue;
                var o = q.owner;
                if (o == null || o.health == null || o.tr == null || !o.tr.gameObject.activeInHierarchy
                    || o.health.IsDead || o.health.Fraction >= DamagedFraction)
                {
                    ReleaseFx(q);
                    continue;
                }
                float k = Mathf.Repeat(time * (q.smoke ? 0.55f : 1.1f) + q.phase, 1f);
                float rise = q.smoke ? 0.12f : 0.06f;
                Vector3 p = o.tr.position + q.offset + Vector3.up * (k * rise);
                p.x += Mathf.Sin((time + q.phase) * 7f) * 0.004f;
                q.tr.position = p;
                if (cam != null)
                {
                    Vector3 d = p - cam.transform.position;
                    if (d.sqrMagnitude > 1e-6f) q.tr.rotation = Quaternion.LookRotation(d, Vector3.up);
                }
                float s;
                Color c;
                if (q.smoke)
                {
                    s = Mathf.Lerp(0.02f, 0.06f, k);
                    c = new Color(0.25f, 0.25f, 0.27f, 0.55f * (1f - k));
                }
                else
                {
                    s = Mathf.Lerp(0.03f, 0.008f, k) * (0.85f + 0.15f * Mathf.Sin(time * 25f + q.phase * 9f));
                    c = Color.Lerp(new Color(1f, 0.85f, 0.2f, 0.9f), new Color(1f, 0.25f, 0.05f, 0f), k);
                }
                q.tr.localScale = new Vector3(s, s, s);
                fxBlock.Clear();
                fxBlock.SetColor(BaseColorId, c);
                q.mr.SetPropertyBlock(fxBlock);
            }
        }
    }
}
