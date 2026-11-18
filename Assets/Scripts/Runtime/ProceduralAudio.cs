using UnityEngine;

namespace HoldTheTable
{
    // Append-only: never reorder existing values (other agents reference them).
    public enum Sfx
    {
        Click, Place, Deny, Hit, Death, Shockwave, WaveStart, Victory, Defeat, Pen,
        ScrollUnfurl, PageTurn, KeyTap, BannerFlap, Hammer, Arrow, Footsteps, PortalHum, Horn, Coin, Harvest,
        Rope, LadderBuild, Climb, Ram, Thud
    }

    /// <summary>
    /// Offline procedural sound effects: every clip is synthesized at startup (filtered noise, FM, Karplus-Strong,
    /// envelopes). Pooled 2D + 3D one-shot sources, looping sources (PortalHum, Footsteps) and an automatic soft
    /// march loop while monsters are active. Music ducking reads <see cref="LastLoudSfxTime"/>.
    /// </summary>
    public class ProceduralAudio : MonoBehaviour
    {
        public static ProceduralAudio Instance { get; private set; }

        [Range(0f, 1f)] public float volume = 0.7f;
        public int pool3D = 12;
        public int loopPool = 4;
        public bool autoMarch = true;

        public const int Rate = 22050;
        AudioClip[] clips;
        AudioSource source2D;
        AudioSource[] sources3D;
        AudioSource[] loops;
        AudioSource march;
        int next3D;
        float[] lastPlayed;
        uint noiseSeed = 22222u;

        /// <summary>Time.unscaledTime of the last non-trivial effect (UI ticks excluded); MusicPlayer ducks under it.</summary>
        public float LastLoudSfxTime { get; private set; } = -10f;

        // ---- null-safe static helpers for other agents' code
        public static void Fire(Sfx s) { if (Instance != null) Instance.Play(s); }
        public static void Fire(Sfx s, Vector3 pos) { if (Instance != null) Instance.Play(s, pos); }

        void Awake()
        {
            Instance = this;
            int n = System.Enum.GetValues(typeof(Sfx)).Length;
            clips = new AudioClip[n];
            lastPlayed = new float[n];
            for (int i = 0; i < n; i++) lastPlayed[i] = -10f;
            for (int i = 0; i < n; i++) clips[i] = Synth((Sfx)i);

            source2D = gameObject.AddComponent<AudioSource>();
            source2D.playOnAwake = false;
            source2D.spatialBlend = 0f;

            sources3D = new AudioSource[Mathf.Max(1, pool3D)];
            for (int i = 0; i < sources3D.Length; i++) sources3D[i] = MakeSource("Sfx3D_" + i, false);
            loops = new AudioSource[Mathf.Max(1, loopPool)];
            for (int i = 0; i < loops.Length; i++) loops[i] = MakeSource("SfxLoop_" + i, true);
            march = MakeSource("SfxMarch", true);
            march.clip = clips[(int)Sfx.Footsteps];
            march.volume = 0f;
        }

        AudioSource MakeSource(string name, bool loop)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var s = go.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.loop = loop;
            s.spatialBlend = 1f;
            s.rolloffMode = AudioRolloffMode.Linear;
            s.minDistance = 0.3f;
            s.maxDistance = 6f;
            s.dopplerLevel = 0f;
            return s;
        }

        void OnDestroy() { if (Instance == this) Instance = null; }

        public AudioClip GetClip(Sfx s) => clips[(int)s];

        public void Play(Sfx sfx)
        {
            int i = (int)sfx;
            if (!Throttle(sfx)) return;
            source2D.PlayOneShot(clips[i], volume * Gain(sfx));
            MarkLoud(sfx);
        }

        public void Play(Sfx sfx, Vector3 pos)
        {
            int i = (int)sfx;
            if (!Throttle(sfx)) return;
            var s = sources3D[next3D];
            next3D = (next3D + 1) % sources3D.Length;
            s.transform.position = pos;
            s.pitch = 0.94f + 0.12f * Rand01();
            s.PlayOneShot(clips[i], volume * Gain(sfx));
            MarkLoud(sfx);
        }

        /// <summary>Starts a looping 3D sound (e.g. PortalHum at a portal). Returns the source; pass it to StopLoop.</summary>
        public AudioSource PlayLoop(Sfx sfx, Vector3 pos, float vol = 1f)
        {
            AudioSource free = null;
            for (int i = 0; i < loops.Length; i++) if (!loops[i].isPlaying) { free = loops[i]; break; }
            if (free == null) return null;
            free.transform.position = pos;
            free.clip = clips[(int)sfx];
            free.volume = volume * vol * Gain(sfx);
            free.time = Random.value * free.clip.length * 0.9f;
            free.Play();
            return free;
        }

        public void StopLoop(AudioSource s)
        {
            if (s != null) s.Stop();
        }

        public void StopAllLoops()
        {
            for (int i = 0; i < loops.Length; i++) loops[i].Stop();
        }

        void Update()
        {
            if (!autoMarch) return;
            // soft marching loop at the centroid of (up to 12) active monsters, volume grows with their count
            var list = MonsterUnit.Active;
            int n = list.Count;
            float target = 0f;
            if (n > 0)
            {
                Vector3 c = Vector3.zero;
                int k = 0;
                for (int i = 0; i < n && k < 12; i++) { var m = list[i]; if (m == null) continue; c += m.transform.position; k++; }
                if (k > 0)
                {
                    march.transform.position = c / k;
                    target = volume * Mathf.Lerp(0.35f, 0.8f, Mathf.Clamp01(n / 16f));
                    if (!march.isPlaying) march.Play();
                    march.pitch = Mathf.Lerp(0.95f, 1.15f, Mathf.Clamp01(n / 16f));
                }
            }
            march.volume = Mathf.MoveTowards(march.volume, target, Time.deltaTime * 0.8f);
            if (target <= 0f && march.volume <= 0.001f && march.isPlaying) march.Stop();
        }

        static float Gain(Sfx s)
        {
            switch (s)
            {
                case Sfx.KeyTap: return 0.6f;
                case Sfx.Deny: return 0.45f;
                case Sfx.Pen: return 0.5f;
                case Sfx.Arrow: return 0.55f;
                case Sfx.Footsteps: return 0.6f;
                case Sfx.PortalHum: return 0.55f;
                case Sfx.Horn: return 1f;
                default: return 0.9f;
            }
        }

        static float MinInterval(Sfx s)
        {
            switch (s)
            {
                case Sfx.Hit: return 0.04f;
                case Sfx.Arrow: return 0.06f;
                case Sfx.KeyTap: return 0.02f;
                case Sfx.Pen: return 0.06f;
                case Sfx.Death: return 0.05f;
                case Sfx.Climb: return 0.15f;
                case Sfx.Thud: return 0.08f;
                case Sfx.Horn: return 1.0f;
                case Sfx.WaveStart: return 0.5f;
                case Sfx.BannerFlap: return 0.4f;
                default: return 0.035f;
            }
        }

        bool Throttle(Sfx s)
        {
            int i = (int)s;
            float t = Time.unscaledTime;
            if (t - lastPlayed[i] < MinInterval(s)) return false;
            lastPlayed[i] = t;
            return true;
        }

        void MarkLoud(Sfx s)
        {
            if (s == Sfx.KeyTap || s == Sfx.Click || s == Sfx.Pen || s == Sfx.Footsteps || s == Sfx.PortalHum || s == Sfx.PageTurn) return;
            LastLoudSfxTime = Time.unscaledTime;
        }

        float Rand01()
        {
            noiseSeed ^= noiseSeed << 13; noiseSeed ^= noiseSeed >> 17; noiseSeed ^= noiseSeed << 5;
            return (noiseSeed & 0xFFFFFF) / (float)0xFFFFFF;
        }

        // ------------------------------------------------------------------ synthesis

        static AudioClip Synth(Sfx sfx)
        {
            var d = Render(sfx, new AudioDsp.Rng(1234u + (uint)sfx * 7919u));
            AudioDsp.Normalize(d, sfx == Sfx.Footsteps || sfx == Sfx.PortalHum ? 0.6f : 0.85f);
            var clip = AudioClip.Create("Sfx_" + sfx, d.Length, 1, Rate, false);
            clip.SetData(d, 0);
            return clip;
        }

        static float[] Render(Sfx sfx, AudioDsp.Rng rng)
        {
            float[] d;
            switch (sfx)
            {
                case Sfx.Click:
                    d = AudioDsp.Buf(0.07f);
                    AudioDsp.Sine(d, 0f, 1800f, 1800f, 0.6f, 0.001f, 90f);
                    AudioDsp.Noise(d, rng, 0f, 0.01f, 0.25f, 9000f, 2000f, 0.0005f, 400f);
                    return d;
                case Sfx.Place:
                    d = AudioDsp.Buf(0.3f);
                    AudioDsp.Sine(d, 0f, 190f, 105f, 0.7f, 0.002f, 16f);
                    AudioDsp.Sine(d, 0f, 430f, 400f, 0.25f, 0.001f, 40f);
                    AudioDsp.Noise(d, rng, 0f, 0.2f, 0.45f, 1600f, 60f, 0.001f, 30f);
                    return d;
                case Sfx.Deny:
                    d = AudioDsp.Buf(0.3f);
                    AudioDsp.Square(d, 0f, 0.13f, 233f, 0.22f);
                    AudioDsp.Square(d, 0.14f, 0.15f, 175f, 0.22f);
                    AudioDsp.LowPass(d, 2500f);
                    return d;
                case Sfx.Hit:
                    d = AudioDsp.Buf(0.14f);
                    AudioDsp.Noise(d, rng, 0f, 0.14f, 0.6f, 3200f, 150f, 0.001f, 35f);
                    AudioDsp.Sine(d, 0f, 160f, 120f, 0.45f, 0.001f, 30f);
                    return d;
                case Sfx.Death:
                    d = AudioDsp.Buf(0.4f);
                    AudioDsp.Sine(d, 0f, 420f, 70f, 0.5f, 0.005f, 7f);
                    AudioDsp.Noise(d, rng, 0f, 0.35f, 0.4f, 1300f, 80f, 0.005f, 9f);
                    return d;
                case Sfx.Shockwave:
                    d = AudioDsp.Buf(1.0f);
                    AudioDsp.Sine(d, 0f, 95f, 32f, 0.8f, 0.01f, 3.5f);
                    AudioDsp.NoiseBand(d, rng, 0f, 1.0f, 0.8f, 2500f, 150f, 0.8f, 0.01f, 0.25f);
                    AudioDsp.Noise(d, rng, 0f, 0.05f, 0.4f, 8000f, 1500f, 0.001f, 60f);
                    return d;
                case Sfx.WaveStart:
                    // war drums: three low hits
                    d = AudioDsp.Buf(1.1f);
                    for (int i = 0; i < 3; i++)
                    {
                        float t = i * 0.28f;
                        AudioDsp.Sine(d, t, 85f, 48f, i == 2 ? 0.9f : 0.7f, 0.002f, 7f);
                        AudioDsp.Noise(d, rng, t, 0.25f, 0.35f, 700f, 40f, 0.001f, 18f);
                    }
                    return d;
                case Sfx.Victory:
                    d = AudioDsp.Buf(1.5f);
                    {
                        float[] notes = { 523.25f, 659.25f, 783.99f, 1046.5f };
                        for (int i = 0; i < 4; i++)
                            AudioDsp.FM(d, i * 0.16f, i == 3 ? 1.3f : 0.6f, notes[i], 2f, 1.6f, 0.35f, 0.005f, i == 3 ? 2.5f : 5f);
                    }
                    return d;
                case Sfx.Defeat:
                    d = AudioDsp.Buf(1.6f);
                    {
                        float[] notes = { 293.66f, 277.18f, 261.63f, 220f };
                        for (int i = 0; i < 4; i++) AudioDsp.Pluck(d, rng, i * 0.32f, i == 3 ? 1.2f : 0.5f, notes[i], 0.45f, 1.4f, 0.4f);
                        AudioDsp.Sine(d, 0f, 73.4f, 73.4f, 0.2f, 0.4f, 1.5f);
                    }
                    return d;
                case Sfx.Pen:
                    d = AudioDsp.Buf(0.06f);
                    AudioDsp.Noise(d, rng, 0f, 0.06f, 0.15f, 7000f, 2200f, 0.01f, 40f);
                    return d;
                case Sfx.ScrollUnfurl:
                    d = AudioDsp.Buf(0.95f);
                    AudioDsp.NoiseBand(d, rng, 0f, 0.9f, 0.35f, 1800f, 3200f, 0.9f, 0.15f, 0.35f);
                    AudioDsp.Crackle(d, rng, 0.05f, 0.85f, 70f, 0.25f, 5000f);
                    AudioDsp.Sine(d, 0f, 150f, 120f, 0.35f, 0.003f, 22f);   // wooden roller knock
                    AudioDsp.Sine(d, 0.78f, 170f, 140f, 0.3f, 0.003f, 24f);
                    return d;
                case Sfx.PageTurn:
                    d = AudioDsp.Buf(0.35f);
                    AudioDsp.NoiseBand(d, rng, 0f, 0.3f, 0.45f, 1300f, 4500f, 1.2f, 0.35f, 0.4f);
                    AudioDsp.Noise(d, rng, 0.26f, 0.05f, 0.25f, 9000f, 2500f, 0.001f, 80f);
                    return d;
                case Sfx.KeyTap:
                    d = AudioDsp.Buf(0.06f);
                    AudioDsp.Sine(d, 0f, 1250f, 1150f, 0.5f, 0.0005f, 110f);
                    AudioDsp.Sine(d, 0f, 2700f, 2700f, 0.15f, 0.0005f, 220f);
                    AudioDsp.Noise(d, rng, 0f, 0.006f, 0.3f, 7000f, 1500f, 0.0002f, 600f);
                    return d;
                case Sfx.BannerFlap:
                    d = AudioDsp.Buf(0.65f);
                    AudioDsp.Flap(d, rng, 0f, 0.6f, 6.5f, 0.6f, 900f);
                    return d;
                case Sfx.Hammer:
                    d = AudioDsp.Buf(0.55f);
                    for (int i = 0; i < 2; i++)
                    {
                        float t = i * 0.21f, a = i == 0 ? 1f : 0.8f;
                        AudioDsp.Sine(d, t, 523f, 523f, 0.35f * a, 0.0005f, 25f);
                        AudioDsp.Sine(d, t, 1382f, 1382f, 0.2f * a, 0.0005f, 45f);
                        AudioDsp.Sine(d, t, 2291f, 2291f, 0.12f * a, 0.0005f, 65f);
                        AudioDsp.Sine(d, t, 170f, 120f, 0.45f * a, 0.001f, 30f);
                        AudioDsp.Noise(d, rng, t, 0.03f, 0.5f * a, 6000f, 400f, 0.0003f, 160f);
                    }
                    return d;
                case Sfx.Arrow:
                    d = AudioDsp.Buf(0.42f);
                    AudioDsp.Pluck(d, rng, 0f, 0.1f, 196f, 0.35f, 0.4f, 0.06f);  // bow string twang
                    AudioDsp.NoiseBand(d, rng, 0.03f, 0.36f, 0.6f, 3200f, 800f, 0.5f, 0.2f, 0.45f); // whoosh
                    return d;
                case Sfx.Footsteps:
                    d = AudioDsp.Buf(1.0f);
                    for (int i = 0; i < 4; i++)
                    {
                        float t = i * 0.25f + (i % 2) * 0.012f, a = (i % 2 == 0) ? 1f : 0.8f;
                        AudioDsp.Noise(d, rng, t, 0.2f, 0.45f * a, 450f, 30f, 0.002f, 38f);
                        AudioDsp.Sine(d, t, 85f, 60f, 0.35f * a, 0.002f, 34f);
                        AudioDsp.Noise(d, rng, t + 0.01f, 0.05f, 0.08f * a, 4000f, 1200f, 0.002f, 70f); // gravel
                    }
                    AudioDsp.FoldTail(d, 0.15f); // seamless loop
                    return d;
                case Sfx.PortalHum:
                    d = AudioDsp.Buf(2.0f);
                    AudioDsp.Drone(d);
                    return d;
                case Sfx.Horn:
                    d = AudioDsp.Buf(1.9f);
                    AudioDsp.Brass(d, 0f, 1.75f, 146.83f, 220f, 0.55f);
                    AudioDsp.Brass(d, 0f, 1.75f, 73.42f, 110f, 0.25f);
                    return d;
                case Sfx.Coin:
                    d = AudioDsp.Buf(0.55f);
                    AudioDsp.FM(d, 0f, 0.45f, 1975.5f, 1.41f, 2.2f, 0.4f, 0.001f, 11f);
                    AudioDsp.FM(d, 0.075f, 0.45f, 2637f, 1.41f, 2.0f, 0.4f, 0.001f, 9f);
                    AudioDsp.Noise(d, rng, 0f, 0.01f, 0.2f, 9000f, 3000f, 0.0003f, 300f);
                    return d;
                case Sfx.Harvest:
                    d = AudioDsp.Buf(0.6f);
                    AudioDsp.NoiseBand(d, rng, 0f, 0.3f, 0.5f, 5000f, 1800f, 0.6f, 0.45f, 0.4f); // scythe swish
                    AudioDsp.Pluck(d, rng, 0.24f, 0.3f, 783.99f, 0.25f, 0.8f, 0.3f);              // gathered sheaf
                    AudioDsp.Pluck(d, rng, 0.30f, 0.3f, 1174.66f, 0.18f, 0.8f, 0.3f);
                    return d;
                case Sfx.Rope:
                    d = AudioDsp.Buf(0.75f);
                    AudioDsp.Creak(d, rng, 0f, 0.7f, 45f, 95f, 650f, 0.6f);
                    AudioDsp.NoiseBand(d, rng, 0f, 0.7f, 0.12f, 2500f, 2000f, 1f, 0.3f, 0.4f);
                    return d;
                case Sfx.LadderBuild:
                    d = AudioDsp.Buf(0.7f);
                    for (int i = 0; i < 3; i++) WoodKnock(d, rng, i * 0.19f, 1f - i * 0.1f, 1f + i * 0.06f);
                    return d;
                case Sfx.Climb:
                    d = AudioDsp.Buf(0.45f);
                    WoodKnock(d, rng, 0f, 0.6f, 1.2f);
                    WoodKnock(d, rng, 0.17f, 0.5f, 1.3f);
                    AudioDsp.Creak(d, rng, 0.05f, 0.35f, 60f, 80f, 900f, 0.25f);
                    return d;
                case Sfx.Ram:
                    d = AudioDsp.Buf(0.95f);
                    AudioDsp.Sine(d, 0f, 62f, 34f, 0.9f, 0.002f, 5.5f);
                    AudioDsp.Noise(d, rng, 0f, 0.6f, 0.6f, 600f, 25f, 0.001f, 9f);
                    AudioDsp.Noise(d, rng, 0f, 0.06f, 0.35f, 7000f, 1500f, 0.0005f, 55f); // wood crack
                    AudioDsp.Creak(d, rng, 0.25f, 0.5f, 35f, 55f, 400f, 0.25f);
                    return d;
                case Sfx.Thud:
                    d = AudioDsp.Buf(0.4f);
                    AudioDsp.Sine(d, 0f, 78f, 50f, 0.8f, 0.002f, 13f);
                    AudioDsp.Noise(d, rng, 0f, 0.3f, 0.5f, 900f, 40f, 0.001f, 24f);
                    AudioDsp.Noise(d, rng, 0.005f, 0.05f, 0.25f, 5000f, 900f, 0.001f, 70f); // stone crunch
                    return d;
            }
            return AudioDsp.Buf(0.05f);
        }

        static void WoodKnock(float[] d, AudioDsp.Rng rng, float t, float a, float pitch)
        {
            AudioDsp.Sine(d, t, 310f * pitch, 290f * pitch, 0.5f * a, 0.0005f, 40f);
            AudioDsp.Sine(d, t, 770f * pitch, 760f * pitch, 0.2f * a, 0.0005f, 65f);
            AudioDsp.Noise(d, rng, t, 0.04f, 0.3f * a, 2500f, 200f, 0.0003f, 90f);
        }
    }

    /// <summary>Tiny offline DSP toolkit (mono float buffers at ProceduralAudio.Rate). Thread-safe: no Unity API.</summary>
    public static class AudioDsp
    {
        public const int Rate = ProceduralAudio.Rate;
        const float TwoPi = 6.2831853f;

        public sealed class Rng
        {
            uint s;
            public Rng(uint seed) { s = seed == 0 ? 1u : seed; }
            public float Next01() { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return (s & 0xFFFFFF) / (float)0xFFFFFF; }
            public float NextSigned() => Next01() * 2f - 1f;
        }

        public static float[] Buf(float seconds) => new float[Mathf.CeilToInt(seconds * Rate)];
        static int Idx(float t) => Mathf.Max(0, Mathf.RoundToInt(t * Rate));
        static float Coef(float hz) => 1f - Mathf.Exp(-TwoPi * Mathf.Min(hz, Rate * 0.45f) / Rate);

        /// <summary>Sine with exponential pitch glide f0→f1 (over its decay life), linear attack, exp decay (1/s).</summary>
        public static void Sine(float[] d, float start, float f0, float f1, float amp, float attack, float decay)
        {
            int s0 = Idx(start);
            float ph = 0f;
            float life = Mathf.Min(8f / Mathf.Max(0.1f, decay), (d.Length - s0) / (float)Rate);
            for (int i = s0; i < d.Length; i++)
            {
                float t = (i - s0) / (float)Rate;
                float env = (t < attack ? t / attack : 1f) * Mathf.Exp(-t * decay);
                if (env < 1e-4f && t > attack) break;
                float f = f0 * Mathf.Pow(f1 / f0, Mathf.Clamp01(t / Mathf.Max(0.01f, life * 0.35f)));
                ph += TwoPi * f / Rate;
                if (ph > TwoPi) ph -= TwoPi;
                d[i] += Mathf.Sin(ph) * amp * env;
            }
        }

        /// <summary>Noise through one-pole low-pass (lp Hz) then high-pass (hp Hz), attack + exp decay.</summary>
        public static void Noise(float[] d, Rng r, float start, float dur, float amp, float lp, float hp, float attack, float decay)
        {
            int s0 = Idx(start), s1 = Mathf.Min(d.Length, s0 + Idx(dur) + Idx(5f / Mathf.Max(1f, decay)));
            float a = Coef(lp), b = Coef(hp), l = 0f, h = 0f;
            for (int i = s0; i < s1; i++)
            {
                float t = (i - s0) / (float)Rate;
                float env = (t < attack ? t / attack : 1f) * Mathf.Exp(-t * decay);
                l += (r.NextSigned() - l) * a;
                h += (l - h) * b;
                d[i] += (l - h) * amp * env;
            }
        }

        /// <summary>Band-pass (Chamberlin SVF) noise whose center sweeps fc0→fc1; envelope rises for 'attackFrac' and falls over the last 'releaseFrac'.</summary>
        public static void NoiseBand(float[] d, Rng r, float start, float dur, float amp, float fc0, float fc1, float q, float attackFrac, float releaseFrac)
        {
            int s0 = Idx(start), n = Idx(dur);
            float low = 0f, band = 0f, damp = Mathf.Min(1.6f, 1f / Mathf.Max(0.3f, q));
            for (int k = 0; k < n && s0 + k < d.Length; k++)
            {
                float u = k / (float)n;
                float fc = fc0 * Mathf.Pow(fc1 / fc0, u);
                float f = 2f * Mathf.Sin(Mathf.PI * Mathf.Min(fc, Rate * 0.22f) / Rate);
                f = Mathf.Min(f, (2f - damp) * 0.85f); // Chamberlin SVF stability
                float x = r.NextSigned();
                low += f * band;
                float high = x - low - damp * band;
                band += f * high;
                d[s0 + k] += band * amp * Env(u, attackFrac, 1f - releaseFrac);
            }
        }

        /// <summary>Paper crackle: sparse random clicks, high-passed.</summary>
        public static void Crackle(float[] d, Rng r, float start, float dur, float perSecond, float amp, float hp)
        {
            int s0 = Idx(start), n = Idx(dur);
            float p = perSecond / Rate, hb = Coef(hp), h = 0f, burst = 0f;
            for (int k = 0; k < n && s0 + k < d.Length; k++)
            {
                if (r.Next01() < p) burst = 0.5f + r.Next01();
                float x = burst * r.NextSigned();
                burst *= 0.93f;
                h += (x - h) * hb;
                float u = k / (float)n;
                d[s0 + k] += (x - h) * amp * Env(u, 0.1f, 0.8f);
            }
        }

        /// <summary>Cloth flapping: low-passed noise amplitude-modulated by sharp pulses.</summary>
        public static void Flap(float[] d, Rng r, float start, float dur, float rateHz, float amp, float lp)
        {
            int s0 = Idx(start), n = Idx(dur);
            float a = Coef(lp), l = 0f;
            for (int k = 0; k < n && s0 + k < d.Length; k++)
            {
                float t = k / (float)Rate, u = k / (float)n;
                float pulse = Mathf.Pow(Mathf.Max(0f, Mathf.Sin(TwoPi * rateHz * t)), 4f);
                l += (r.NextSigned() - l) * a;
                d[s0 + k] += l * amp * (0.15f + pulse) * Env(u, 0.05f, 0.6f) * 2f;
            }
        }

        /// <summary>Wood/rope creak: impulse train at a wandering rate through a resonant band-pass.</summary>
        public static void Creak(float[] d, Rng r, float start, float dur, float hz0, float hz1, float res, float amp)
        {
            int s0 = Idx(start), n = Idx(dur);
            float low = 0f, band = 0f, damp = 0.12f;
            float f = 2f * Mathf.Sin(Mathf.PI * res / Rate);
            float ph = 0f;
            for (int k = 0; k < n && s0 + k < d.Length; k++)
            {
                float u = k / (float)n;
                float hz = Mathf.Lerp(hz0, hz1, 0.5f + 0.5f * Mathf.Sin(u * 7.3f)) * (0.9f + 0.2f * r.Next01());
                ph += hz / Rate;
                float x = 0f;
                if (ph >= 1f) { ph -= 1f; x = 1f; }
                low += f * band;
                float high = x - low - damp * band;
                band += f * high;
                d[s0 + k] += band * amp * 0.5f * Env(u, 0.15f, 0.7f);
            }
        }

        public static void Square(float[] d, float start, float dur, float f, float amp)
        {
            int s0 = Idx(start), n = Idx(dur);
            float ph = 0f;
            for (int k = 0; k < n && s0 + k < d.Length; k++)
            {
                ph += f / Rate; if (ph >= 1f) ph -= 1f;
                float u = k / (float)n;
                d[s0 + k] += (ph < 0.5f ? 1f : -1f) * amp * Env(u, 0.03f, 0.85f);
            }
        }

        /// <summary>2-operator FM bell/brass: carrier fc, modulator fc*ratio, index decays with the envelope.</summary>
        public static void FM(float[] d, float start, float dur, float fc, float ratio, float index, float amp, float attack, float decay)
        {
            int s0 = Idx(start), n = Mathf.Min(d.Length - s0, Idx(dur + 3f / Mathf.Max(0.5f, decay)));
            float pc = 0f, pm = 0f;
            for (int k = 0; k < n; k++)
            {
                float t = k / (float)Rate;
                float env = (t < attack ? t / attack : 1f) * Mathf.Exp(-t * decay);
                pm += TwoPi * fc * ratio / Rate;
                pc += TwoPi * fc / Rate;
                if (pm > TwoPi) pm -= TwoPi;
                if (pc > TwoPi) pc -= TwoPi;
                d[s0 + k] += Mathf.Sin(pc + index * env * Mathf.Sin(pm)) * amp * env;
            }
        }

        /// <summary>War horn: FM brass with a lip glide f0→f1 in the first 0.3 s, swell, vibrato and soft low-pass.</summary>
        public static void Brass(float[] d, float start, float dur, float f0, float f1, float amp)
        {
            int s0 = Idx(start), n = Idx(dur);
            float pc = 0f, pm = 0f, l = 0f, a = Coef(1800f);
            for (int k = 0; k < n && s0 + k < d.Length; k++)
            {
                float t = k / (float)Rate, u = k / (float)n;
                float glide = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((t - 0.12f) / 0.25f));
                float f = Mathf.Lerp(f0, f1, glide) * (1f + 0.006f * Mathf.Sin(TwoPi * 5.2f * t) * Mathf.Clamp01(t - 0.5f));
                float env = Env(u, 0.12f, 0.8f);
                pm += TwoPi * f / Rate; pc += TwoPi * f / Rate;
                if (pm > TwoPi) pm -= TwoPi;
                if (pc > TwoPi) pc -= TwoPi;
                float x = Mathf.Sin(pc + (0.8f + 2.6f * env) * Mathf.Sin(pm));
                l += (x - l) * a;
                d[s0 + k] += l * amp * env;
            }
        }

        /// <summary>Portal drone: 2 s seamless loop (all partials complete whole cycles).</summary>
        public static void Drone(float[] d)
        {
            float[] f = { 55f, 82.5f, 110.5f, 165f, 220f };
            float[] a = { 0.35f, 0.16f, 0.22f, 0.08f, 0.05f };
            for (int i = 0; i < d.Length; i++)
            {
                float t = i / (float)Rate;
                float lfo = 0.75f + 0.25f * Mathf.Sin(TwoPi * 0.5f * t);
                float s = 0f;
                for (int k = 0; k < f.Length; k++) s += Mathf.Sin(TwoPi * f[k] * t) * a[k];
                s += 0.06f * Mathf.Sin(TwoPi * 440f * t + 2f * Mathf.Sin(TwoPi * 3f * t)); // shimmer
                d[i] += s * lfo;
            }
        }

        /// <summary>Karplus-Strong plucked string with fractional delay (exact tuning). T60s in seconds; damped after 'dur'.</summary>
        public static void Pluck(float[] d, Rng r, float start, float dur, float freq, float amp, float t60, float release, float brightness = 0.6f)
        {
            int s0 = Idx(start);
            if (s0 >= d.Length) return;
            float delay = Rate / freq - 0.5f;
            int size = (int)delay + 4;
            var buf = new float[size];
            // excitation: noise smoothed according to brightness
            float sm = 0f, smA = Mathf.Lerp(0.15f, 0.95f, brightness);
            for (int i = 0; i < size; i++) { sm += (r.NextSigned() - sm) * smA; buf[i] = sm; }
            float mean = 0f; for (int i = 0; i < size; i++) mean += buf[i]; mean /= size;
            for (int i = 0; i < size; i++) buf[i] -= mean;
            // the loop gain is applied once per trip around the delay line, i.e. once per period
            float gHold = Mathf.Pow(0.001f, 1f / (Mathf.Max(0.05f, t60) * freq));
            float gRel = Mathf.Pow(0.001f, 1f / (Mathf.Max(0.02f, release) * freq));
            int nHold = Idx(dur), nTotal = nHold + Idx(release * 1.2f);
            int w = 0;
            for (int k = 0; k < nTotal && s0 + k < d.Length; k++)
            {
                float rp = w - delay; if (rp < 0f) rp += size;
                int i0 = (int)rp; float fr = rp - i0;
                int i1 = (i0 + 1) % size, im = (i0 - 1 + size) % size;
                float a0 = buf[i0] + (buf[i1] - buf[i0]) * fr;       // y[n-D]
                float a1 = buf[im] + (buf[i0] - buf[im]) * fr;       // y[n-D-1]
                float g = k < nHold ? gHold : gRel;
                float y = 0.5f * (a0 + a1) * g;
                buf[w] = y;
                w++; if (w >= size) w = 0;
                float atk = k < 30 ? k / 30f : 1f;
                d[s0 + k] += y * amp * atk;
            }
        }

        public static void LowPass(float[] d, float hz)
        {
            float a = Coef(hz), l = 0f;
            for (int i = 0; i < d.Length; i++) { l += (d[i] - l) * a; d[i] = l; }
        }

        /// <summary>Adds the last 'seconds' onto the start and fades them out at the end, for click-free looping.</summary>
        public static void FoldTail(float[] d, float seconds)
        {
            int n = Mathf.Min(d.Length / 2, Idx(seconds));
            for (int i = 0; i < n; i++)
            {
                float w = i / (float)n;
                d[i] += d[d.Length - n + i] * (1f - w);
                d[d.Length - n + i] *= w;
            }
        }

        public static void Normalize(float[] d, float peak)
        {
            float m = 1e-6f;
            for (int i = 0; i < d.Length; i++) { float v = d[i] < 0 ? -d[i] : d[i]; if (v > m) m = v; }
            float g = peak / m;
            for (int i = 0; i < d.Length; i++) d[i] *= g;
        }

        public static float Env(float u, float attack, float releaseStart)
        {
            if (u < attack) return attack > 0f ? u / attack : 1f;
            if (u > releaseStart) return Mathf.Clamp01((1f - u) / Mathf.Max(1e-4f, 1f - releaseStart));
            return 1f;
        }
    }
}
