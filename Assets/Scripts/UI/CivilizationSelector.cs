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

        static readonly Color CardNormal = new Color(0.98f, 0.93f, 0.80f, 1f);
        static readonly Color CardSelected = new Color(1f, 0.84f, 0.50f, 1f);

        public void Build(RectTransform area)
        {
            if (built) { Refresh(); return; }
            built = true;
            var defs = CivilizationCatalog.All;
            int n = defs.Count;
            cards = new Button[n];
            rims = new Outline[n];
            cardImages = new Image[n];

            float h = area.rect.height > 10f ? area.rect.height : 720f;
            float w = area.rect.width > 10f ? area.rect.width : 760f;
            float cardH = Mathf.Min(220f, (h - (n - 1) * 16f) / n);
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

                // motif icon on the left, tinted with the civilization colors
                var icon = UiKit.Panel(crt, new Vector2(-w * 0.5f + 95f, 0f), new Vector2(150f, cardH - 30f), new Color(0.15f, 0.10f, 0.06f, 0.15f));
                icon.GetComponent<Image>().raycastTarget = false;
                BuildMotif(icon, def, cardH - 30f);

                var name = UiKit.Label(crt, def.DisplayName, new Vector2(85f, cardH * 0.22f), new Vector2(w - 210f, 60f), 42, TextAlignmentOptions.Left);
                name.fontStyle = FontStyles.Bold;
                name.color = UiKit.Ink;
                var desc = UiKit.Label(crt, def.Description, new Vector2(85f, -cardH * 0.17f), new Vector2(w - 210f, cardH * 0.55f), 26, TextAlignmentOptions.TopLeft);
                desc.color = new Color(0.32f, 0.20f, 0.10f, 1f);

                // accent stripe
                var stripe = UiKit.Panel(crt, new Vector2(w * 0.5f - 10f, 0f), new Vector2(12f, cardH - 20f), def.primary);
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
                    rims[i].effectColor = on ? new Color(0.75f, 0.45f, 0.05f, 1f) : UiKit.Gold;
                    rims[i].effectDistance = on ? new Vector2(8f, -8f) : new Vector2(3f, -3f);
                }
            }
        }

        /// <summary>Tiny flat drawing of the architectural motif: tiered roofs, timber hall or stone towers.</summary>
        static void BuildMotif(RectTransform box, CivilizationDefinition def, float size)
        {
            Color a = def.primary, b = def.accent;
            Color wood = new Color(0.45f, 0.28f, 0.12f, 1f);
            Color stone = new Color(0.55f, 0.56f, 0.58f, 1f);
            float s = size / 180f;
            switch (def.style)
            {
                case ArchitectureStyle.TieredRoofs:
                    for (int t = 0; t < 3; t++)
                    {
                        float y = (-50f + t * 45f) * s;
                        float roofW = (130f - t * 30f) * s;
                        P(box, new Vector2(0f, y - 14f * s), new Vector2(roofW * 0.6f, 26f * s), b);
                        P(box, new Vector2(0f, y + 6f * s), new Vector2(roofW, 12f * s), a);
                    }
                    P(box, new Vector2(0f, 82f * s), new Vector2(6f * s, 18f * s), b);
                    break;
                case ArchitectureStyle.Timber:
                    P(box, new Vector2(0f, -30f * s), new Vector2(120f * s, 70f * s), wood);
                    for (int k = -2; k <= 2; k++) P(box, new Vector2(k * 24f * s, -30f * s), new Vector2(5f * s, 70f * s), new Color(0.30f, 0.18f, 0.08f, 1f));
                    for (int r = 0; r < 4; r++) P(box, new Vector2(0f, (14f + r * 16f) * s), new Vector2((130f - r * 32f) * s, 14f * s), a);
                    P(box, new Vector2(0f, -48f * s), new Vector2(26f * s, 34f * s), b);
                    break;
                default:
                    P(box, new Vector2(0f, -35f * s), new Vector2(90f * s, 60f * s), stone);
                    for (int side = -1; side <= 1; side += 2)
                    {
                        P(box, new Vector2(side * 50f * s, -5f * s), new Vector2(34f * s, 120f * s), stone);
                        for (int c = -1; c <= 1; c++) P(box, new Vector2(side * 50f * s + c * 12f * s, 60f * s), new Vector2(8f * s, 12f * s), stone);
                        P(box, new Vector2(side * 50f * s, 20f * s), new Vector2(8f * s, 18f * s), new Color(0.1f, 0.1f, 0.12f, 1f));
                    }
                    P(box, new Vector2(0f, -50f * s), new Vector2(28f * s, 30f * s), b);
                    P(box, new Vector2(0f, 10f * s), new Vector2(40f * s, 22f * s), a);
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
