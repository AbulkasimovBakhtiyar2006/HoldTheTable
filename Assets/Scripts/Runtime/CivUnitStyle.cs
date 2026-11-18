using UnityEngine;

namespace HoldTheTable
{
    public enum CivTool { None, Hoe, Pickaxe, Hammer }

    /// <summary>
    /// Shared per-civilization look for civ-owned non-combat walkers (peasants, workers).
    /// Humanoid proportions come from UnitScale (same height/head as foot soldiers); clothing, headgear, tools,
    /// aprons and banner-colored accents differ per ArchitectureStyle. All materials are shared via Mats.Lit.
    /// Owner: living-civ visuals agent.
    /// </summary>
    public static class CivUnitStyle
    {
        /// <summary>Scale of a worker relative to a foot soldier (slightly smaller, never taller).</summary>
        public const float WorkerHeightMul = 0.92f;

        /// <summary>Cached transforms of a built humanoid; animate with no allocations.</summary>
        public sealed class Rig
        {
            public Transform armL, armR, legL, legR, toolRoot, hoe, pick, hammer;
            public CivTool tool;

            public void SetTool(CivTool t)
            {
                tool = t;
                if (hoe != null) hoe.gameObject.SetActive(t == CivTool.Hoe);
                if (pick != null) pick.gameObject.SetActive(t == CivTool.Pickaxe);
                if (hammer != null) hammer.gameObject.SetActive(t == CivTool.Hammer);
            }

            public void ShowTool(bool show)
            {
                if (toolRoot != null && toolRoot.gameObject.activeSelf != show) toolRoot.gameObject.SetActive(show);
            }

            static void Rot(Transform t, float x, float z = 0f)
            {
                if (t != null) t.localRotation = Quaternion.Euler(x, 0f, z);
            }

            /// <summary>Walking: legs and arms counter-swing. carrying = both arms hold cargo in front.</summary>
            public void Walk(float phase, float amp, bool carrying)
            {
                float s = Mathf.Sin(phase) * 32f * amp;
                Rot(legL, s);
                Rot(legR, -s);
                if (carrying)
                {
                    Rot(armL, -62f, -6f);
                    Rot(armR, -62f, 6f);
                }
                else
                {
                    Rot(armL, -s * 0.8f);
                    Rot(armR, -28f + s * 0.25f);
                }
            }

            /// <summary>Work swing: t = seconds into the work cycle, cycle = seconds per swing.</summary>
            public void Work(float t, float cycle)
            {
                float u = Mathf.Repeat(t / cycle, 1f);
                float s = u < 0.6f ? u / 0.6f : 1f - (u - 0.6f) / 0.4f;
                s = s * s * (3f - 2f * s);
                Rot(armR, Mathf.Lerp(-40f, -170f, s));
                Rot(armL, Mathf.Lerp(-30f, -120f, s), 8f);
                Rot(legL, 0f);
                Rot(legR, 0f);
            }

            public void Panic(float phase)
            {
                float s = Mathf.Sin(phase) * 18f;
                Rot(armL, -165f + s, -14f);
                Rot(armR, -165f - s, 14f);
                Rot(legL, s * 1.6f);
                Rot(legR, -s * 1.6f);
            }

            public void Idle()
            {
                Rot(armL, 0f);
                Rot(armR, -28f);
                Rot(legL, 0f);
                Rot(legR, 0f);
            }
        }

        static GameObject Child(Transform parent, string name, Vector3 pos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            return go;
        }

        /// <summary>
        /// Builds a full humanoid (legs, torso, arms, head, civ clothing/headgear, three toggleable tools) under parent.
        /// Feet at local y=0, crown near UnitScale.HumanHeight * WorkerHeightMul (hat tips may exceed slightly).
        /// </summary>
        public static Rig BuildWorker(Transform parent, ArchitectureStyle style, BuildingVisuals.Palette pal)
        {
            var rig = new Rig();
            var root = parent;
            var k = new BuildingVisuals.Kit(root);

            Color skin = new Color(0.93f, 0.77f, 0.62f);
            Color eye = new Color(0.1f, 0.1f, 0.1f);
            Color leather = new Color(0.33f, 0.21f, 0.12f);
            Color wood = new Color(0.46f, 0.30f, 0.15f);
            Color iron = new Color(0.55f, 0.57f, 0.60f);
            Color straw = new Color(0.88f, 0.76f, 0.40f);

            Color tunic, trousers, sleeve;
            switch (style)
            {
                case ArchitectureStyle.TieredRoofs:
                    tunic = Color.Lerp(pal.main, new Color(0.45f, 0.58f, 0.62f), 0.55f); // pale indigo-blue robe
                    trousers = new Color(0.86f, 0.84f, 0.76f);
                    sleeve = tunic;
                    break;
                case ArchitectureStyle.Timber:
                    tunic = new Color(0.50f, 0.40f, 0.26f); // undyed wool
                    trousers = new Color(0.30f, 0.26f, 0.20f);
                    sleeve = new Color(0.62f, 0.58f, 0.48f);
                    break;
                default:
                    tunic = new Color(0.66f, 0.62f, 0.52f); // linen gambeson
                    trousers = new Color(0.36f, 0.34f, 0.32f);
                    sleeve = tunic;
                    break;
            }

            // Legs: hip pivots at y 0.0145 so boots touch the ground.
            for (int side = 0; side < 2; side++)
            {
                float x = side == 0 ? -0.0030f : 0.0030f;
                var pivot = Child(root, side == 0 ? "LegL" : "LegR", new Vector3(x, 0.0145f, 0f)).transform;
                var lk = new BuildingVisuals.Kit(pivot);
                lk.CylRot(new Vector3(0f, -0.0055f, 0f), 0.0019f, 0.011f, trousers, Vector3.zero);
                lk.Ellipsoid(new Vector3(0f, -0.0126f, 0.0012f), new Vector3(0.0042f, 0.0036f, 0.0075f), style == ArchitectureStyle.TieredRoofs ? pal.dark : leather);
                if (side == 0) rig.legL = pivot; else rig.legR = pivot;
            }

            // Torso (garment silhouette differs per civ)
            switch (style)
            {
                case ArchitectureStyle.TieredRoofs:
                    // Long robe with flared skirt, banner sash with hanging tail, wide sleeves
                    k.Cyl(new Vector3(0f, 0.0085f, 0f), 0.0072f, 0.0075f, tunic);
                    k.Ellipsoid(new Vector3(0f, 0.0215f, 0f), new Vector3(0.0150f, 0.0150f, 0.0098f), tunic);
                    k.Cyl(new Vector3(0f, 0.0162f, 0f), 0.0070f, 0.0026f, pal.banner);
                    k.Box(new Vector3(0.0030f, 0.0120f, 0.0068f), new Vector3(0.0028f, 0.0105f, 0.0013f), pal.banner);
                    k.Box(new Vector3(0f, 0.0262f, 0.0036f), new Vector3(0.0050f, 0.0016f, 0.0034f), pal.trim); // collar trim
                    break;
                case ArchitectureStyle.Timber:
                    // Short wool tunic, big leather apron, fur-trimmed shoulders, banner-color belt
                    k.Cyl(new Vector3(0f, 0.0105f, 0f), 0.0064f, 0.0098f, tunic);
                    k.Ellipsoid(new Vector3(0f, 0.0218f, 0f), new Vector3(0.0158f, 0.0140f, 0.0102f), tunic);
                    k.Box(new Vector3(0f, 0.0160f, 0.0062f), new Vector3(0.0092f, 0.0130f, 0.0014f), leather); // apron
                    k.Cyl(new Vector3(0f, 0.0158f, 0f), 0.0068f, 0.0020f, pal.banner);
                    k.Cyl(new Vector3(0f, 0.0250f, 0f), 0.0070f, 0.0026f, new Color(0.80f, 0.74f, 0.62f)); // fur collar
                    break;
                default:
                    // Gambeson with banner-color tabard + trim stripe and iron-buckled belt
                    k.Cyl(new Vector3(0f, 0.0105f, 0f), 0.0063f, 0.0100f, tunic);
                    k.Ellipsoid(new Vector3(0f, 0.0218f, 0f), new Vector3(0.0158f, 0.0140f, 0.0100f), tunic);
                    k.Box(new Vector3(0f, 0.0175f, 0.0058f), new Vector3(0.0092f, 0.0160f, 0.0013f), pal.banner);
                    k.Box(new Vector3(0f, 0.0175f, -0.0058f), new Vector3(0.0092f, 0.0160f, 0.0013f), pal.banner);
                    k.Box(new Vector3(0f, 0.0185f, 0.0066f), new Vector3(0.0024f, 0.0120f, 0.0006f), pal.trim);
                    k.Cyl(new Vector3(0f, 0.0148f, 0f), 0.0066f, 0.0018f, pal.dark);
                    k.Box(new Vector3(0f, 0.0157f, 0.0068f), new Vector3(0.0024f, 0.0020f, 0.0008f), iron);
                    break;
            }

            // Arms with shoulder pivots; right hand holds the tool pivot.
            for (int side = 0; side < 2; side++)
            {
                float x = side == 0 ? -0.0088f : 0.0088f;
                var pivot = Child(root, side == 0 ? "ArmL" : "ArmR", new Vector3(x, 0.0255f, 0f)).transform;
                var ak = new BuildingVisuals.Kit(pivot);
                float r = style == ArchitectureStyle.TieredRoofs ? 0.0023f : 0.0017f;
                ak.CylRot(new Vector3(0f, -0.0050f, 0f), r, 0.0105f, sleeve, Vector3.zero);
                ak.Ball(new Vector3(0f, -0.0108f, 0f), 0.0030f, skin);
                if (side == 0) rig.armL = pivot; else rig.armR = pivot;
            }

            // Tools in right hand: handle points along tool-local +Y, rotated 160 deg so the head leads forward-down.
            var toolRoot = Child(rig.armR, "ToolRoot", new Vector3(0f, -0.0108f, 0f)).transform;
            toolRoot.localRotation = Quaternion.Euler(160f, 0f, 0f);
            rig.toolRoot = toolRoot;

            var hoe = Child(toolRoot, "Hoe", Vector3.zero).transform;
            var hk = new BuildingVisuals.Kit(hoe);
            hk.CylRot(new Vector3(0f, 0.0100f, 0f), 0.0007f, 0.0270f, wood, Vector3.zero);
            hk.Box(new Vector3(0f, 0.0235f, 0.0018f), new Vector3(0.0070f, 0.0044f, 0.0010f), iron, new Vector3(35f, 0f, 0f));
            rig.hoe = hoe;

            var pick = Child(toolRoot, "Pickaxe", Vector3.zero).transform;
            var pk = new BuildingVisuals.Kit(pick);
            pk.CylRot(new Vector3(0f, 0.0100f, 0f), 0.0007f, 0.0270f, wood, Vector3.zero);
            pk.Box(new Vector3(0f, 0.0228f, 0f), new Vector3(0.0150f, 0.0016f, 0.0016f), iron);
            pk.Box(new Vector3(-0.0078f, 0.0221f, 0f), new Vector3(0.0030f, 0.0014f, 0.0014f), iron, new Vector3(0f, 0f, -30f));
            pk.Box(new Vector3(0.0078f, 0.0221f, 0f), new Vector3(0.0030f, 0.0014f, 0.0014f), iron, new Vector3(0f, 0f, 30f));
            rig.pick = pick;

            var hammer = Child(toolRoot, "Hammer", Vector3.zero).transform;
            var mk = new BuildingVisuals.Kit(hammer);
            mk.CylRot(new Vector3(0f, 0.0070f, 0f), 0.0007f, 0.0200f, wood, Vector3.zero);
            mk.Box(new Vector3(0f, 0.0178f, 0f), new Vector3(0.0072f, 0.0040f, 0.0040f), iron);
            rig.hammer = hammer;
            rig.SetTool(CivTool.Hoe);

            // Head (UnitScale proportions)
            float headD = UnitScale.HeadDiameter;
            float headY = 0.0305f;
            k.Ball(new Vector3(0f, headY, 0f), headD, skin);
            k.Ball(new Vector3(-0.0022f, headY + 0.0004f, 0.0050f), 0.0019f, eye);
            k.Ball(new Vector3(0.0022f, headY + 0.0004f, 0.0050f), 0.0019f, eye);

            // Headgear
            switch (style)
            {
                case ArchitectureStyle.TieredRoofs:
                    k.Ellipsoid(new Vector3(0f, headY + 0.0020f, -0.0014f), new Vector3(0.0116f, 0.0100f, 0.0108f), new Color(0.10f, 0.08f, 0.08f)); // hair
                    k.Cone(new Vector3(0f, headY + 0.0030f, 0f), 0.0112f, 0.0080f, straw);                 // wide conical straw hat
                    k.Ball(new Vector3(0f, headY + 0.0112f, 0f), 0.0020f, pal.banner);                      // banner-colored finial
                    k.Box(new Vector3(0f, headY - 0.0012f, 0.0054f), new Vector3(0.0070f, 0.0008f, 0.0008f), pal.dark); // chin cord
                    break;
                case ArchitectureStyle.Timber:
                    k.Ellipsoid(new Vector3(0f, headY + 0.0018f, -0.0012f), new Vector3(0.0128f, 0.0108f, 0.0134f), pal.banner); // hood
                    k.Ellipsoid(new Vector3(0f, headY - 0.0040f, -0.0072f), new Vector3(0.0070f, 0.0090f, 0.0050f), pal.banner); // hood tail
                    k.Ellipsoid(new Vector3(0f, headY - 0.0048f, 0.0034f), new Vector3(0.0070f, 0.0058f, 0.0042f), new Color(0.38f, 0.26f, 0.14f)); // beard
                    break;
                default:
                    k.Ellipsoid(new Vector3(0f, headY + 0.0004f, -0.0009f), new Vector3(0.0126f, 0.0124f, 0.0124f), new Color(0.58f, 0.60f, 0.62f)); // mail coif
                    k.Ball(new Vector3(0f, headY - 0.0003f, 0.0034f), 0.0082f, skin);                            // face opening
                    k.Ball(new Vector3(-0.0020f, headY + 0.0004f, 0.0071f), 0.0017f, eye);
                    k.Ball(new Vector3(0.0020f, headY + 0.0004f, 0.0071f), 0.0017f, eye);
                    k.Cyl(new Vector3(0f, headY + 0.0042f, 0f), 0.0063f, 0.0028f, iron);                          // arming cap
                    k.Cyl(new Vector3(0f, headY + 0.0040f, 0f), 0.0079f, 0.0008f, pal.banner);                    // banner brim band
                    break;
            }

            return rig;
        }
    }
}
