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
        Outline[] rims;
        Image[] cardImages;
        bool built;

        static readonly Color CardNormal = new Color(0.93f, 0.87f, 0.75f, 1f);   // UiKit.Parchment
        static readonly Color CardSelected = new Color(0.97f, 0.94f, 0.86f, 1f); // UiKit.ParchmentLight

        public void Build(RectTransform area)
        {
            if (built) { Refresh(); return; }
            built = true;
            var defs = CivilizationCatalog.All;
            int n = defs.Count;
            cards = new Button[n];
            rims = new Outline[n];
            cardImages = new Image[n];

            float h = area.rect.height > 10f ? area.rect.height : 700f;
            float w = area.rect.width > 10f ? Mathf.Min(area.rect.width, 670f) : 670f;
            float cardH = Mathf.Min(210f, (h - (n - 1) * 16f) / n);
            float y0 = (n - 1) * (cardH + 16f) * 0.5f;

            for (int i = 0; i < n; i++)
            {
                var def = defs[i];
                var card = UiKit.Button(area, string.Empty, new Vector2(0f, y0 - i * (cardH + 16f)), new Vector2(w, cardH), () => Select(def), 30);
                var crt = (RectTransform)card.transform;
                var defaultLabel = card.GetComponentInChildren<TMP_Text>(true);
                if (defaultLabel != null) defaultLabel.gameObject.SetActive(false);
                cardImages[i] = (Image)card.targetGraphic;
                rims[i] = card.GetComponent<Outline>();
                if (rims[i] != null)
                {
                    rims[i].effectColor = UiKit.Gold;
                    rims[i].effectDistance = new Vector2(2f, -2f);
                }

                // Recessed dark walnut motif box on the left with antique brass frame
                float motifW = 132f, motifH = cardH - 24f;
                float motifX = -w * 0.5f + 16f + motifW * 0.5f;
                var motifFrame = UiKit.FramedPanel(crt, "MotifFrame", new Vector2(motifX, 0f), new Vector2(motifW, motifH),
                    UiKit.WalnutFrame, UiKit.DarkPanel, UiKit.Gold, 3f, true);
                motifFrame.GetComponent<Image>().raycastTarget = false;
                BuildMotif(motifFrame, def, motifH);

                // Vertical ornamental divider rule
                float divX = motifX + motifW * 0.5f + 12f;
                var vRule = UiKit.Panel(crt, new Vector2(divX, 0f), new Vector2(2f, cardH - 30f), UiKit.BronzeShadow);
                vRule.GetComponent<Image>().raycastTarget = false;

                float textX = divX + 16f + (w * 0.5f - 24f - (divX + 16f)) * 0.5f;
                float textW = w * 0.5f - 26f - (divX + 16f);

                var name = UiKit.Label(crt, def.DisplayName, new Vector2(textX, cardH * 0.22f), new Vector2(textW, 50f), 38, TextAlignmentOptions.Left);
                name.fontStyle = FontStyles.Bold | FontStyles.SmallCaps;
                name.color = UiKit.Ink;
                var desc = UiKit.Label(crt, def.Description, new Vector2(textX, -cardH * 0.16f), new Vector2(textW, cardH * 0.54f), 24, TextAlignmentOptions.TopLeft);
                desc.color = UiKit.InkMuted;

                // Right edge heraldic ribbon
                var stripe = UiKit.Panel(crt, new Vector2(w * 0.5f - 8f, 0f), new Vector2(8f, cardH - 16f), def.primary);
                stripe.GetComponent<Image>().raycastTarget = false;
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
                if (rims[i] != null)
                {
                    rims[i].effectColor = on ? UiKit.GoldBright : UiKit.Gold;
                    rims[i].effectDistance = on ? new Vector2(3f, -3f) : new Vector2(1.5f, -1.5f);
                }
            }
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
