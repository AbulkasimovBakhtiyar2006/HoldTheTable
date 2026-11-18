using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HoldTheTable
{
    /// <summary>
    /// Three big poke cards (one per CivilizationCatalog entry) with name, description and a small architectural motif.
    /// Selecting writes CivilizationProfile.Current.civId (suggested name replaced only if the name was still a suggestion).
    /// </summary>
    public class CivilizationSelector : MonoBehaviour
    {
        public event Action<CivilizationDefinition> Selected;

        Button[] cards;

        Image[] cardImages;
        bool built;

        static readonly Color CardNormal = new Color(0.985f, 0.973f, 0.945f, 1f); // UiKit.ParchmentLight
        static readonly Color CardSelected = Color.white;

        public void Build(RectTransform area)
        {
            if (built) { Refresh(); return; }
            built = true;
            var defs = CivilizationCatalog.All;
            int n = defs.Count;
            cards = new Button[n];
            
            cardImages = new Image[n];

            float h = area.rect.height > 10f ? area.rect.height : 700f;
            float w = area.rect.width > 10f ? Mathf.Min(area.rect.width, 670f) : 670f;
            float cardH = Mathf.Min(210f, (h - (n - 1) * 24f) / n);
            float y0 = (n - 1) * (cardH + 24f) * 0.5f;

            for (int i = 0; i < n; i++)
            {
                var def = defs[i];
                var card = UiKit.Button(area, string.Empty, new Vector2(0f, y0 - i * (cardH + 24f)), new Vector2(w, cardH), () => Select(def), 30);
                var crt = (RectTransform)card.transform;
                var defaultLabel = card.GetComponentInChildren<TMP_Text>(true);
                if (defaultLabel != null) defaultLabel.gameObject.SetActive(false);
                cardImages[i] = (Image)card.targetGraphic;
                UiKit.SetSelected(card, false);

                // illustration tile on the left: real photo (Resources/CivArt) in a thin gold frame, procedural motif as fallback
                float motifW = 160f, motifH = cardH - 28f;
                float motifX = -w * 0.5f + 14f + motifW * 0.5f;
                var tex = LoadArt(def);
                var tile = UiKit.Panel(crt, new Vector2(motifX, 0f), new Vector2(motifW, motifH), tex != null ? UiKit.Gold : UiKit.DarkPanel);
                tile.name = "MotifTile";
                tile.GetComponent<Image>().raycastTarget = false;
                if (tex != null) BuildPhoto(tile, tex, def, motifW - 6f, motifH - 6f);
                else BuildMotif(tile, def, motifH);

                float textLeft = motifX + motifW * 0.5f + 22f;
                float textW = w * 0.5f - 20f - textLeft;
                float textX = textLeft + textW * 0.5f;

                var name = UiKit.Label(crt, def.DisplayName, new Vector2(textX, cardH * 0.20f), new Vector2(textW, 46f), UiKit.FontHeading - 4, TextAlignmentOptions.Left);
                name.fontStyle = FontStyles.Bold;
                name.color = UiKit.Ink;
                name.enableAutoSizing = true; name.fontSizeMin = 24; name.fontSizeMax = UiKit.FontHeading - 4;
                name.textWrappingMode = TextWrappingModes.NoWrap;
                var desc = UiKit.Label(crt, def.Description, new Vector2(textX, -cardH * 0.17f), new Vector2(textW, cardH * 0.50f), UiKit.FontCaption, TextAlignmentOptions.TopLeft);
                desc.color = UiKit.InkMuted;

                // civ colour dot (replaces the old edge ribbon)
                var dot = UiKit.Panel(crt, new Vector2(w * 0.5f - 30f, cardH * 0.5f - 30f), new Vector2(20f, 20f), def.primary);
                dot.GetComponent<Image>().raycastTarget = false;
                cards[i] = card;
            }
            Refresh();
        }

        void Select(CivilizationDefinition def)
        {
            var p = CivilizationProfile.Current;
            p.SelectCivilization(def.id);
            CivilizationProfile.NotifyChanged();
            Refresh();
            Selected?.Invoke(def);
        }

        public void Refresh()
        {
            if (cards == null) return;
            string cur = CivilizationProfile.Current.civId;
            var defs = CivilizationCatalog.All;
            for (int i = 0; i < cards.Length; i++)
            {
                bool on = defs[i].id == cur;
                cardImages[i].color = on ? CardSelected : CardNormal;
                UiKit.SetSelected(cards[i], on);
            }
        }

        static Texture2D LoadArt(CivilizationDefinition def)
        {
            string key;
            switch (def.style)
            {
                case ArchitectureStyle.TieredRoofs: key = "celestial"; break;
                case ArchitectureStyle.Timber: key = "verdant"; break;
                default: key = "iron"; break;
            }
            try { return Resources.Load<Texture2D>("CivArt/" + key); }
            catch (Exception) { return null; }
        }

        /// <summary>Photo cropped (cover-fit via uvRect) to the frame, inset inside the gold border.</summary>
        static void BuildPhoto(RectTransform frame, Texture2D tex, CivilizationDefinition def, float w, float h)
        {
            var go = new GameObject("Photo", typeof(RectTransform));
            go.layer = frame.gameObject.layer;
            var rt = (RectTransform)go.transform;
            rt.SetParent(frame, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(w, h);
            float frameAspect = w / h, texAspect = (float)tex.width / tex.height;
            float uw = 1f, uh = 1f;
            if (texAspect > frameAspect) uw = frameAspect / texAspect; else uh = texAspect / frameAspect;
            float fx = def.style == ArchitectureStyle.TieredRoofs || def.style == ArchitectureStyle.Timber ? 0.5f : 0.72f; // keep the castle in frame
            float fy = def.style == ArchitectureStyle.Timber ? 0.55f : (def.style == ArchitectureStyle.TieredRoofs ? 0.45f : 0.5f);
            float ux = Mathf.Clamp01(fx - uw * 0.5f); ux = Mathf.Min(ux, 1f - uw);
            float uy = Mathf.Clamp01(fy - uh * 0.5f); uy = Mathf.Min(uy, 1f - uh);
            var raw = go.AddComponent<RawImage>();
            raw.texture = tex;
            raw.uvRect = new Rect(ux, uy, uw, uh);
            raw.raycastTarget = false;
        }

        /// <summary>Detailed heraldic architectural crest: tiered pagoda eaves, timber great-hall, or battlemented keep.</summary>
        static void BuildMotif(RectTransform box, CivilizationDefinition def, float size)
        {
            Color a = def.primary, b = def.accent;
            Color wood = new Color(0.50f, 0.32f, 0.16f, 1f);
            Color darkWood = new Color(0.24f, 0.14f, 0.08f, 1f);
            Color stone = new Color(0.58f, 0.56f, 0.52f, 1f);
            Color darkStone = new Color(0.32f, 0.30f, 0.28f, 1f);
            Color gold = UiKit.Gold;
            float s = size / 180f;

            switch (def.style)
            {
                case ArchitectureStyle.TieredRoofs:
                    // 3 curved pagoda tiers with lacquered bracket pillars and golden imperial finial
                    for (int t = 0; t < 3; t++)
                    {
                        float y = (-42f + t * 44f) * s;
                        float roofW = (118f - t * 26f) * s;
                        // bracket pillar core
                        P(box, new Vector2(0f, y - 12f * s), new Vector2(roofW * 0.5f, 22f * s), darkWood);
                        // main curved eave
                        P(box, new Vector2(0f, y + 4f * s), new Vector2(roofW, 10f * s), a);
                        // swept eave tip highlights
                        P(box, new Vector2(-roofW * 0.48f, y + 8f * s), new Vector2(10f * s, 6f * s), b);
                        P(box, new Vector2(roofW * 0.48f, y + 8f * s), new Vector2(10f * s, 6f * s), b);
                    }
                    // spire / finial
                    P(box, new Vector2(0f, 84f * s), new Vector2(6f * s, 22f * s), gold);
                    P(box, new Vector2(0f, 96f * s), new Vector2(12f * s, 12f * s), gold);
                    break;

                case ArchitectureStyle.Timber:
                    // Norse/Anglo-Saxon great hall with cross-gables, timber posts and golden shield boss
                    P(box, new Vector2(0f, -28f * s), new Vector2(110f * s, 64f * s), wood);
                    for (int k = -2; k <= 2; k++)
                        P(box, new Vector2(k * 22f * s, -28f * s), new Vector2(5f * s, 64f * s), darkWood);
                    // steep pitched thatch / timber roof
                    for (int r = 0; r < 4; r++)
                        P(box, new Vector2(0f, (12f + r * 16f) * s), new Vector2((124f - r * 28f) * s, 13f * s), a);
                    // crossed dragon beams / gables
                    P(box, new Vector2(-16f * s, 74f * s), new Vector2(7f * s, 26f * s), darkWood);
                    P(box, new Vector2(16f * s, 74f * s), new Vector2(7f * s, 26f * s), darkWood);
                    // arched hall door
                    P(box, new Vector2(0f, -44f * s), new Vector2(24f * s, 32f * s), darkWood);
                    // heraldic boss above door
                    P(box, new Vector2(0f, -12f * s), new Vector2(16f * s, 16f * s), b);
                    break;

                default:
                    // Battlemented medieval stone keep with twin flanking towers, crenels and portcullis
                    P(box, new Vector2(0f, -32f * s), new Vector2(80f * s, 56f * s), stone);
                    // twin flanking towers
                    for (int side = -1; side <= 1; side += 2)
                    {
                        float tx = side * 44f * s;
                        P(box, new Vector2(tx, -4f * s), new Vector2(30f * s, 112f * s), stone);
                        // tower battlements
                        for (int c = -1; c <= 1; c++)
                            P(box, new Vector2(tx + c * 10f * s, 56f * s), new Vector2(7f * s, 12f * s), stone);
                        // arrow slit
                        P(box, new Vector2(tx, 22f * s), new Vector2(5f * s, 16f * s), darkStone);
                        // tower roof cap / pennon
                        P(box, new Vector2(tx, 66f * s), new Vector2(4f * s, 16f * s), darkWood);
                        P(box, new Vector2(tx + side * 8f * s, 70f * s), new Vector2(12f * s, 8f * s), a);
                    }
                    // central keep crenelations
                    for (int c = -1; c <= 1; c++)
                        P(box, new Vector2(c * 16f * s, 2f * s), new Vector2(8f * s, 10f * s), stone);
                    // arched gateway with iron grating
                    P(box, new Vector2(0f, -44f * s), new Vector2(26f * s, 32f * s), darkStone);
                    P(box, new Vector2(0f, 16f * s), new Vector2(32f * s, 22f * s), b);
                    break;
            }
        }

        static void P(RectTransform parent, Vector2 pos, Vector2 size, Color c)
        {
            var go = new GameObject("Motif", typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            var img = go.AddComponent<Image>();
            img.sprite = UiKit.WhiteSprite;
            img.color = c;
            img.raycastTarget = false;
        }
    }
}
