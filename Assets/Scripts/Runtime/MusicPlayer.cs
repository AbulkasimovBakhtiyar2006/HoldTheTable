using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>
    /// Offline background music: real public-domain classical compositions transcribed note-by-note below and
    /// synthesized at runtime (Karplus-Strong harpsichord / pizzicato + soft string pad + Schroeder reverb).
    /// Rendering runs on a worker thread; AudioClips are created on the main thread when ready. Loops are seamless
    /// (reverb tail folded onto the loop start) and track changes crossfade. Ducks under loud SFX.
    ///   Setup / placement : J. Pachelbel — Canon in D (ground bass + three canonic violins)
    ///   Build / upgrade   : C. Petzold (formerly attr. J. S. Bach) — Minuet in G major, BWV Anh. 114
    ///   Wave              : E. Grieg — In the Hall of the Mountain King (Peer Gynt), accelerating
    ///   Victory           : L. van Beethoven — "Ode to Joy" theme (Symphony No. 9), final phrase + cadence
    ///   Defeat            : F. Chopin — Funeral March (Sonata No. 2, Op. 35) opening, B-flat minor
    /// </summary>
    public class MusicPlayer : MonoBehaviour
    {
        public static MusicPlayer Instance { get; private set; }

        public enum Track { Canon, Minuet, MountainKing, Victory, Defeat }

        [Range(0f, 1f)] public float volume = 0.25f;
        public float duckTo = 0.55f;
        public float crossfadeSeconds = 1.5f;
        public bool followGamePhase = true;

        const int Rate = AudioDsp.Rate;
        const int TrackCount = 5;

        readonly AudioClip[] clips = new AudioClip[TrackCount];
        readonly float[][] rendered = new float[TrackCount][];
        readonly object gate = new object();
        AudioSource[] src = new AudioSource[2];
        int active;
        Track? current, wanted;
        float fade = 1f;
        float duck = 1f;
        GamePhase lastPhase = (GamePhase)(-1);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (Instance != null) return;
            var go = new GameObject("MusicPlayer");
            DontDestroyOnLoad(go);
            go.AddComponent<MusicPlayer>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
            for (int i = 0; i < 2; i++)
            {
                var s = gameObject.AddComponent<AudioSource>();
                s.playOnAwake = false;
                s.spatialBlend = 0f;
                s.loop = true;
                s.volume = 0f;
                s.priority = 0;
                src[i] = s;
            }
            // render in the order they are needed: setup music first
            Task.Run(() =>
            {
                Track[] order = { Track.Canon, Track.Minuet, Track.MountainKing, Track.Victory, Track.Defeat };
                for (int i = 0; i < order.Length; i++)
                {
                    float[] data;
                    try { data = Compose(order[i]); }
                    catch (Exception) { data = new float[Rate]; }
                    lock (gate) rendered[(int)order[i]] = data;
                }
            });
        }

        void OnDestroy() { if (Instance == this) Instance = null; }

        /// <summary>Request a track (crossfades). Victory/Defeat play once.</summary>
        public void Play(Track t)
        {
            wanted = t;
        }

        public void Stop()
        {
            wanted = null;
            current = null;
        }

        void Update()
        {
            // finish clips rendered by the worker (AudioClip API is main-thread only)
            for (int i = 0; i < TrackCount; i++)
            {
                if (clips[i] != null) continue;
                float[] data;
                lock (gate) data = rendered[i];
                if (data == null) continue;
                var clip = AudioClip.Create("Music_" + (Track)i, data.Length, 1, Rate, false);
                clip.SetData(data, 0);
                clips[i] = clip;
                lock (gate) rendered[i] = null;
                break; // at most one upload per frame
            }

            if (followGamePhase) FollowPhase();

            // start / crossfade
            if (wanted.HasValue && (!current.HasValue || current.Value != wanted.Value))
            {
                var clip = clips[(int)wanted.Value];
                if (clip != null)
                {
                    active = 1 - active;
                    var s = src[active];
                    s.clip = clip;
                    s.loop = wanted.Value != Track.Victory && wanted.Value != Track.Defeat;
                    s.volume = 0f;
                    s.Play();
                    current = wanted;
                    fade = 0f;
                }
            }

            fade = Mathf.MoveTowards(fade, 1f, Time.unscaledDeltaTime / Mathf.Max(0.05f, crossfadeSeconds));
            var pa = ProceduralAudio.Instance;
            bool loud = pa != null && Time.unscaledTime - pa.LastLoudSfxTime < 0.35f;
            duck = Mathf.MoveTowards(duck, loud ? duckTo : 1f, Time.unscaledDeltaTime * (loud ? 6f : 1.2f));
            float v = volume * duck;
            var a = src[active];
            var b = src[1 - active];
            a.volume = current.HasValue ? v * fade : Mathf.MoveTowards(a.volume, 0f, Time.unscaledDeltaTime * 0.5f);
            b.volume = v * (1f - fade) * (b.isPlaying ? 1f : 0f);
            if (fade >= 1f && b.isPlaying) b.Stop();
        }

        void FollowPhase()
        {
            var gd = GameDirector.Instance;
            if (gd == null) return;
            var p = gd.Phase;
            if (p == lastPhase) return;
            lastPhase = p;
            switch (p)
            {
                case GamePhase.CivilizationSetup:
                case GamePhase.HeadquartersPlacement: Play(Track.Canon); break;
                case GamePhase.BuildPhase:
                case GamePhase.Upgrade: Play(Track.Minuet); break;
                case GamePhase.Wave: Play(Track.MountainKing); break;
                case GamePhase.Victory: Play(Track.Victory); break;
                case GamePhase.Defeat: Play(Track.Defeat); break;
            }
        }

        // ================================================================== score → audio (worker thread, no Unity API)

        struct Note { public float t, dur; public int midi; public float vel; public int inst; }
        const int Harpsichord = 0, Pizz = 1, Pad = 2;

        static float[] Compose(Track t)
        {
            var notes = new List<Note>(512);
            float length; bool loop = true;
            switch (t)
            {
                case Track.Canon: length = Canon(notes); break;
                case Track.Minuet: length = Minuet(notes); break;
                case Track.MountainKing: length = MountainKing(notes); break;
                case Track.Victory: length = Victory(notes); loop = false; break;
                default: length = Defeat(notes); loop = false; break;
            }
            return Render(notes, length, loop, (uint)t * 977u + 17u);
        }

        // ---- Pachelbel, Canon in D (D major, 4/4). Ground bass + three violins entering 2 bars apart (a true canon).
        static float Canon(List<Note> n)
        {
            const float bpm = 66f;
            const string ground = "D3:1 A2:1 B2:1 F#2:1 G2:1 D2:1 G2:1 A2:1";
            // violin line (bars 3–8 of the original): crotchet descents, then the first quaver variation
            const string violin =
                "F#5:1 E5:1 D5:1 C#5:1 B4:1 A4:1 B4:1 C#5:1 " +
                "D5:1 C#5:1 B4:1 A4:1 G4:1 F#4:1 G4:1 E4:1 " +
                "D4:.5 F#4:.5 A4:.5 G4:.5 F#4:.5 D4:.5 F#4:.5 E4:.5 " +
                "D4:.5 B3:.5 D4:.5 A4:.5 G4:.5 B4:.5 A4:.5 G4:.5";
            float beat = 60f / bpm;
            for (int c = 0; c < 6; c++)
            {
                Voice(n, ground, c * 8 * beat, bpm, Pizz, 0.75f, 0);
                Voice(n, ground, c * 8 * beat, bpm, Pad, 0.22f, 12);
            }
            Voice(n, violin, 8 * beat, bpm, Harpsichord, 0.5f, 0);
            Voice(n, violin, 16 * beat, bpm, Harpsichord, 0.42f, 0);
            Voice(n, violin, 24 * beat, bpm, Harpsichord, 0.38f, 0);
            return 48 * beat;
        }

        // ---- Minuet in G major, BWV Anh. 114 (3/4): melody + bass, parts A and B.
        static float Minuet(List<Note> n)
        {
            const float bpm = 116f;
            const string melodyA =
                "D5:1 G4:.5 A4:.5 B4:.5 C5:.5 | D5:1 G4:1 G4:1 | E5:1 C5:.5 D5:.5 E5:.5 F#5:.5 | G5:1 G4:1 G4:1 | " +
                "C5:1 D5:.5 C5:.5 B4:.5 A4:.5 | B4:1 C5:.5 B4:.5 A4:.5 G4:.5 | F#4:1 G4:.5 A4:.5 B4:.5 G4:.5 | A4:3 | " +
                "D5:1 G4:.5 A4:.5 B4:.5 C5:.5 | D5:1 G4:1 G4:1 | E5:1 C5:.5 D5:.5 E5:.5 F#5:.5 | G5:1 G4:1 G4:1 | " +
                "C5:1 D5:.5 C5:.5 B4:.5 A4:.5 | B4:1 C5:.5 B4:.5 A4:.5 G4:.5 | A4:1 B4:.5 A4:.5 G4:.5 F#4:.5 | G4:3";
            const string bassA =
                "G3+B3+D4:2 A3:1 | B3:3 | C4:3 | B3:3 | A3:3 | G3:3 | D4:1 B3:1 G3:1 | D4:1 D3:1 C4:1 | " +
                "B3:2 A3:1 | G3:1 B3:1 G3:1 | C4:3 | B3:1 C4:.5 B3:.5 A3:.5 G3:.5 | A3:2 F#3:1 | G3:2 B3:1 | C4:1 D4:1 D3:1 | G3:2 G2:1";
            const string melodyB =
                "B5:1 G5:.5 A5:.5 B5:.5 G5:.5 | A5:1 D5:.5 E5:.5 F#5:.5 D5:.5 | G5:1 E5:.5 F#5:.5 G5:.5 D5:.5 | C#5:1 B4:.5 C#5:.5 A4:1 | " +
                "A4:.5 B4:.5 C#5:.5 D5:.5 E5:.5 F#5:.5 | G5:1 F#5:1 E5:1 | F#5:1 A4:1 C#5:1 | D5:3 | " +
                "D5:1 G4:.5 F#4:.5 G4:1 | E5:1 G4:.5 F#4:.5 G4:1 | D5:1 C5:1 B4:1 | A4:.5 G4:.5 F#4:.5 G4:.5 A4:1 | " +
                "D4:.5 E4:.5 F#4:.5 G4:.5 A4:.5 B4:.5 | C5:1 B4:1 A4:1 | B4:.5 D5:.5 G4:1 F#4:1 | G4:3";
            const string bassB =
                "G3:3 | F#3:3 | E3:2 G3:1 | A3:2 A2:1 | A3:3 | B3:1 D4:1 C#4:1 | D4:1 F#3:1 A3:1 | D4:1 D3:1 C4:1 | " +
                "B3:3 | C4:3 | B3:1 A3:1 G3:1 | D4:2 F#3:1 | G3:3 | C3:2 D3:1 | D3:3 | G2:3";
            float beat = 60f / bpm;
            float tB = Voice(n, melodyA, 0f, bpm, Harpsichord, 0.55f, 0);
            Voice(n, bassA, 0f, bpm, Harpsichord, 0.45f, 0);
            Voice(n, bassA, 0f, bpm, Pad, 0.12f, 0);
            float end = Voice(n, melodyB, tB, bpm, Harpsichord, 0.55f, 0);
            Voice(n, bassB, tB, bpm, Harpsichord, 0.45f, 0);
            Voice(n, bassB, tB, bpm, Pad, 0.12f, 0);
            return Mathf.Max(end, 96 * beat);
        }

        // ---- Grieg, In the Hall of the Mountain King (B minor, 4/4): theme repeated, rising register, accelerating.
        static float MountainKing(List<Note> n)
        {
            const string theme =
                "B2:.5 C#3:.5 D3:.5 E3:.5 F#3:.5 D3:.5 F#3:1 | F3:.5 C#3:.5 F3:1 E3:.5 C3:.5 E3:1 | " +
                "B2:.5 C#3:.5 D3:.5 E3:.5 F#3:.5 D3:.5 F#3:.5 B3:.5 | A3:.5 F#3:.5 D3:.5 F#3:.5 A3:2";
            const string bass = "B1:1 F#2:1 B1:1 F#2:1 | C#2:1 G#2:1 C2:1 G2:1 | B1:1 F#2:1 B1:1 F#2:1 | F#1:1 C#2:1 F#1:2";
            float[] bpms = { 104f, 118f, 134f, 152f, 172f, 192f };
            float t = 0f;
            for (int c = 0; c < bpms.Length; c++)
            {
                float bpm = bpms[c];
                int oct = c < 2 ? 0 : (c < 4 ? 12 : 24);
                for (int r = 0; r < 2; r++)
                {
                    float start = t;
                    Voice(n, theme, start, bpm, Pizz, 0.6f, oct);
                    if (c >= 3) Voice(n, theme, start, bpm, Harpsichord, 0.35f, oct - 12);
                    if (c >= 1) Voice(n, bass, start, bpm, Pizz, 0.5f, 0);
                    if (c >= 4) Voice(n, bass, start, bpm, Pad, 0.18f, 12);
                    t = start + 16f * 60f / bpm;
                }
            }
            return t;
        }

        // ---- Beethoven, "Ode to Joy" (D major): final phrase of the theme + broad D major cadence.
        static float Victory(List<Note> n)
        {
            const float bpm = 112f;
            const string melody =
                "E4:1 E4:1 F#4:1 D4:1 | E4:1 F#4:.5 G4:.5 F#4:1 D4:1 | E4:1 F#4:.5 G4:.5 F#4:1 E4:1 | D4:1 E4:1 A3:2 | " +
                "F#4:1 F#4:1 G4:1 A4:1 | A4:1 G4:1 F#4:1 E4:1 | D4:1 D4:1 E4:1 F#4:1 | E4:1.5 D4:.5 D4:2 | D4+F#4+A4+D5:4";
            const string bass =
                "A2:2 D3:2 | A2:2 D3:2 | A2:4 | D3:2 A2:2 | D3:4 | A2:2 D3:2 | D3:2 A2:2 | A2:2 D3:2 | D2+A2+D3:4";
            float end = Voice(n, melody, 0f, bpm, Harpsichord, 0.6f, 12);
            Voice(n, melody, 0f, bpm, Pad, 0.15f, 0);
            Voice(n, bass, 0f, bpm, Harpsichord, 0.45f, 0);
            Voice(n, bass, 0f, bpm, Pad, 0.18f, 12);
            return end + 2.5f;
        }

        // ---- Chopin, Funeral March (B-flat minor) opening: dotted-rhythm melody over the tolling bass.
        static float Defeat(List<Note> n)
        {
            const float bpm = 52f;
            const string melody =
                "Bb3:1 Bb3:.75 Bb3:.25 Bb3:2 | Db4:1 C4:.75 C4:.25 Bb3:.75 Bb3:.25 A3:.75 Bb3:.25 | Bb3:4";
            const string bass =
                "Bb1+F2:1 Db3+F3:1 Bb1+F2:1 Db3+F3:1 | Bb1+F2:1 Gb3+Bb3:1 Bb1+F2:1 Db3+F3:1 | Bb1+F2+Db3:4";
            float end = Voice(n, melody, 0f, bpm, Harpsichord, 0.55f, 0);
            Voice(n, melody, 0f, bpm, Pad, 0.18f, 0);
            Voice(n, bass, 0f, bpm, Pizz, 0.5f, 0);
            Voice(n, bass, 0f, bpm, Pad, 0.14f, 0);
            return end + 2.5f;
        }

        /// <summary>Parses "C#5:.5 R:1 G3+B3+D4:2 | ..." (duration in beats, '|' bar lines ignored); returns end time.</summary>
        static float Voice(List<Note> n, string seq, float start, float bpm, int inst, float vel, int transpose)
        {
            float beat = 60f / bpm, t = start;
            var tokens = seq.Split(' ');
            for (int i = 0; i < tokens.Length; i++)
            {
                string tok = tokens[i];
                if (tok.Length == 0 || tok == "|") continue;
                int colon = tok.IndexOf(':');
                float beats = colon > 0 ? float.Parse(tok.Substring(colon + 1), System.Globalization.CultureInfo.InvariantCulture) : 1f;
                string pitches = colon > 0 ? tok.Substring(0, colon) : tok;
                float dur = beats * beat;
                if (pitches != "R")
                {
                    var parts = pitches.Split('+');
                    for (int p = 0; p < parts.Length; p++)
                    {
                        int m = Midi(parts[p]) + transpose;
                        // slight humanisation of chord roll for plucked instruments
                        float roll = inst == Pad ? 0f : p * 0.018f;
                        n.Add(new Note { t = t + roll, dur = dur, midi = m, vel = vel * (parts.Length > 1 ? 0.8f : 1f), inst = inst });
                    }
                }
                t += dur;
            }
            return t;
        }

        static int Midi(string s)
        {
            int pc;
            switch (s[0])
            {
                case 'C': pc = 0; break; case 'D': pc = 2; break; case 'E': pc = 4; break; case 'F': pc = 5; break;
                case 'G': pc = 7; break; case 'A': pc = 9; break; default: pc = 11; break;
            }
            int i = 1;
            if (i < s.Length && s[i] == '#') { pc++; i++; }
            else if (i < s.Length && s[i] == 'b') { pc--; i++; }
            int oct = int.Parse(s.Substring(i), System.Globalization.CultureInfo.InvariantCulture);
            return 12 * (oct + 1) + pc;
        }

        static float Hz(int midi) => 440f * Mathf.Pow(2f, (midi - 69) / 12f);

        static float[] Render(List<Note> notes, float length, bool loop, uint seed)
        {
            const float tail = 2.5f;
            int len = Mathf.CeilToInt(length * Rate);
            var d = new float[len + Mathf.CeilToInt(tail * Rate)];
            var rng = new AudioDsp.Rng(seed);
            for (int i = 0; i < notes.Count; i++)
            {
                var nt = notes[i];
                float f = Hz(nt.midi);
                switch (nt.inst)
                {
                    case Harpsichord:
                        AudioDsp.Pluck(d, rng, nt.t, nt.dur * 0.95f, f, nt.vel, Mathf.Lerp(3.2f, 1.2f, Mathf.InverseLerp(40, 84, nt.midi)), 0.18f, 0.85f);
                        // second "rank" an octave up, quiet, gives the harpsichord its jangle
                        AudioDsp.Pluck(d, rng, nt.t + 0.004f, nt.dur * 0.9f, f * 2f, nt.vel * 0.18f, 1.0f, 0.12f, 0.9f);
                        break;
                    case Pizz:
                        AudioDsp.Pluck(d, rng, nt.t, Mathf.Min(nt.dur, 0.35f), f, nt.vel, 0.9f, 0.12f, 0.35f);
                        break;
                    default:
                        PadNote(d, nt.t, nt.dur, f, nt.vel);
                        break;
                }
            }
            Reverb(d, 0.2f);
            if (loop)
            {
                // fold everything that rings past the loop point back onto the start → seamless loop
                for (int i = len; i < d.Length; i++) d[i - len] += d[i];
            }
            var outBuf = new float[loop ? len : d.Length];
            Array.Copy(d, outBuf, outBuf.Length);
            AudioDsp.Normalize(outBuf, 0.85f);
            for (int i = 0; i < outBuf.Length; i++) { float x = outBuf[i]; outBuf[i] = x / (1f + Mathf.Abs(x) * 0.3f); }
            if (!loop) { int fl = Mathf.Min(outBuf.Length, Rate / 2); for (int i = 0; i < fl; i++) outBuf[outBuf.Length - 1 - i] *= i / (float)fl; }
            return outBuf;
        }

        /// <summary>Soft string pad: three detuned saws, low-passed, slow bow attack and release.</summary>
        static void PadNote(float[] d, float start, float dur, float f, float vel)
        {
            int s0 = Mathf.RoundToInt(start * Rate);
            int n = Mathf.RoundToInt((dur + 0.45f) * Rate);
            float p1 = 0f, p2 = 0.33f, p3 = 0.66f, l1 = 0f, l2 = 0f;
            float a = 1f - Mathf.Exp(-6.2831853f * Mathf.Min(1400f, f * 4f) / Rate);
            float atk = 0.18f, rel = 0.45f;
            for (int k = 0; k < n && s0 + k < d.Length; k++)
            {
                float t = k / (float)Rate;
                p1 += f * 0.997f / Rate; if (p1 >= 1f) p1 -= 1f;
                p2 += f / Rate; if (p2 >= 1f) p2 -= 1f;
                p3 += f * 1.003f / Rate; if (p3 >= 1f) p3 -= 1f;
                float x = (p1 + p2 + p3) * (2f / 3f) - 1f;
                l1 += (x - l1) * a;
                l2 += (l1 - l2) * a;
                float env = t < atk ? t / atk : (t > dur ? Mathf.Max(0f, 1f - (t - dur) / rel) : 1f);
                env *= 1f + 0.03f * Mathf.Sin(6.2831853f * 5f * t);
                d[s0 + k] += l2 * vel * env * 0.6f;
            }
        }

        /// <summary>Mono Schroeder reverb: 4 damped combs + 2 allpasses (delays scaled for 22.05 kHz).</summary>
        static void Reverb(float[] d, float wet)
        {
            int[] cd = { 558, 594, 638, 678 };
            int[] ad = { 278, 220 };
            var cb = new float[4][]; var ci = new int[4]; var cl = new float[4];
            for (int k = 0; k < 4; k++) cb[k] = new float[cd[k]];
            var ab = new float[2][]; var ai = new int[2];
            for (int k = 0; k < 2; k++) ab[k] = new float[ad[k]];
            const float fb = 0.8f, damp = 0.3f;
            for (int i = 0; i < d.Length; i++)
            {
                float x = d[i] * 0.25f, y = 0f;
                for (int k = 0; k < 4; k++)
                {
                    float o = cb[k][ci[k]];
                    cl[k] = o * (1f - damp) + cl[k] * damp;
                    cb[k][ci[k]] = x + cl[k] * fb;
                    if (++ci[k] >= cd[k]) ci[k] = 0;
                    y += o;
                }
                for (int k = 0; k < 2; k++)
                {
                    float o = ab[k][ai[k]];
                    float v = y + o * 0.5f;
                    ab[k][ai[k]] = v;
                    y = o - v * 0.5f;
                    if (++ai[k] >= ad[k]) ai[k] = 0;
                }
                d[i] = d[i] * (1f - wet * 0.5f) + y * wet;
            }
        }
    }
}
