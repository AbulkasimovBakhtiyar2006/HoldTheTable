using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HoldTheTable
{
    /// <summary>Two big poke-able upgrade cards with a 6 s visual timer. Auto-skip logic lives in QuartermasterAgent.</summary>
    public class UpgradeChoice : MonoBehaviour
    {
        public static UpgradeChoice Instance { get; private set; }

        public Canvas canvas;
        public float timerSeconds = 6f;
        [Tooltip("Place the canvas in front of the player on Show (disable if another script positions it).")]
        public bool autoPlaceInFront = true;
        public float distance = 0.5f;
        public float below = 0.15f;

        RectTransform content;
        Button[] cards = new Button[2];
        TMP_Text[] titles = new TMP_Text[2];
        TMP_Text[] descs = new TMP_Text[2];
        TMP_Text headerText, skipText;
        RectTransform timerFill;
        Action<int> onPick;
        float shownAt;
        int lastSecs = -1;
        bool built;

        public bool IsShowing => content != null && content.gameObject.activeSelf;

        void Awake()
        {
            Instance = this;
            Build();
            if (content != null) content.gameObject.SetActive(false);
        }

        void OnDestroy() { if (Instance == this) Instance = null; }

        void Build()
        {
            if (built) return;
            if (canvas == null) canvas = GetComponentInChildren<Canvas>(true);
            if (canvas == null) { Debug.LogError("[UpgradeChoice] canvas not assigned"); return; }
            built = true;
            var root = (RectTransform)canvas.transform;
            content = UiKit.Rect(root, "UpgradeContent", Vector2.zero, new Vector2(900f, 420f));

            // Carved walnut & antique brass frame (fits inside 900x420 with clean margins)
            UiKit.FramedPanel(content, "UpgradeFrame", Vector2.zero, new Vector2(900f, 420f),
                UiKit.WalnutFrame, UiKit.DarkPanel, UiKit.Gold, 8f, true);

            // Royal crimson header banner
            var headerRibbon = UiKit.Panel(content, new Vector2(0f, 168f), new Vector2(840f, 44f), UiKit.RubricCrimson);
            headerRibbon.name = "HeaderRibbon";
            headerRibbon.GetComponent<Image>().raycastTarget = false;
            var hRim = headerRibbon.gameObject.AddComponent<Outline>();
            hRim.effectColor = UiKit.Gold;
            hRim.effectDistance = new Vector2(1.5f, -1.5f);

            headerText = UiKit.Label(content, Strings.QuartermasterGreeting, new Vector2(0f, 168f), new Vector2(810f, 40f), 30);
            headerText.fontStyle = FontStyles.Bold | FontStyles.SmallCaps;
            headerText.color = UiKit.GoldBright;

            for (int i = 0; i < 2; i++)
            {
                int idx = i;
                var card = UiKit.Button(content, string.Empty, new Vector2(i == 0 ? -212f : 212f, 12f), new Vector2(404f, 244f), () => Pick(idx), 30);
                UiKit.SetButtonColor(card, UiKit.Parchment);
                var crt = (RectTransform)card.transform;
                // remove the default label created by UiKit (empty), use title + description instead
                var defaultLabel = card.GetComponentInChildren<TMP_Text>(true);
                if (defaultLabel != null) defaultLabel.gameObject.SetActive(false);

                // Inner vellum frame + corner brass studs + crimson decree stripe
                var innerWell = UiKit.Panel(crt, Vector2.zero, new Vector2(388f, 228f), UiKit.ParchmentLight);
                innerWell.name = "CardInner";
                innerWell.GetComponent<Image>().raycastTarget = false;
                var innerRim = innerWell.gameObject.AddComponent<Outline>();
                innerRim.effectColor = UiKit.BronzeShadow;
                innerRim.effectDistance = new Vector2(1.5f, -1.5f);
                UiKit.CornerGems(crt, 12f, 7f, UiKit.Gold);

                var topStripe = UiKit.Panel(crt, new Vector2(0f, 98f), new Vector2(364f, 6f), UiKit.RubricCrimson);
                topStripe.GetComponent<Image>().raycastTarget = false;

                titles[i] = UiKit.Label(crt, string.Empty, new Vector2(0f, 54f), new Vector2(364f, 64f), 32);
                titles[i].fontStyle = FontStyles.Bold;
                titles[i].color = UiKit.RubricCrimson;

                UiKit.Divider(crt, new Vector2(0f, 12f), 330f, true);

                descs[i] = UiKit.Label(crt, string.Empty, new Vector2(0f, -48f), new Vector2(364f, 100f), 25);
                descs[i].color = UiKit.Ink;
                cards[i] = card;
            }

            // Timer bar framed inside the lower panel (y = -138, well inside -202 inner bottom border)
            var barBg = UiKit.Panel(content, new Vector2(0f, -138f), new Vector2(828f, 18f), UiKit.WalnutFrame);
            barBg.GetComponent<Image>().raycastTarget = false;
            var barRim = barBg.gameObject.AddComponent<Outline>();
            barRim.effectColor = UiKit.BronzeShadow;
            barRim.effectDistance = new Vector2(1.5f, -1.5f);

            timerFill = UiKit.Panel(barBg, new Vector2(2f, 0f), new Vector2(824f, 14f), UiKit.GoldBright);
            timerFill.GetComponent<Image>().raycastTarget = false;
            timerFill.anchorMin = new Vector2(0f, 0.5f);
            timerFill.anchorMax = new Vector2(0f, 0.5f);
            timerFill.pivot = new Vector2(0f, 0.5f);
            timerFill.anchoredPosition = new Vector2(2f, 0f);

            skipText = UiKit.Label(content, string.Empty, new Vector2(0f, -172f), new Vector2(820f, 28f), 21);
            skipText.fontStyle = FontStyles.Italic;
            skipText.color = UiKit.Parchment;
        }

        public void Show(UpgradeOption a, UpgradeOption b, Action<int> onPick)
        {
            Build();
            if (content == null) return;
            this.onPick = onPick;
            titles[0].text = a.title;
            descs[0].text = a.description;
            titles[1].text = b.title;
            descs[1].text = b.description;
            cards[0].interactable = cards[1].interactable = true;
            shownAt = Time.time;
            lastSecs = -1;
            timerFill.sizeDelta = new Vector2(824f, 14f);
            if (autoPlaceInFront) PlaceInFront();
            content.gameObject.SetActive(true);
        }

        public void Hide()
        {
            onPick = null;
            if (content != null) content.gameObject.SetActive(false);
        }

        void Pick(int i)
        {
            var cb = onPick;
            Hide();
            cb?.Invoke(i);
        }

        void Update()
        {
            if (content == null || !content.gameObject.activeSelf) return;
            float left = Mathf.Max(0f, timerSeconds - (Time.time - shownAt));
            timerFill.sizeDelta = new Vector2(824f * (left / timerSeconds), 14f);
            int secs = Mathf.CeilToInt(left);
            if (secs != lastSecs)
            {
                lastSecs = secs;
                skipText.text = Strings.Format(Strings.QuartermasterSkip, secs);
            }
        }

        void PlaceInFront()
        {
            var cam = Camera.main;
            if (cam == null) return;
            var ct = cam.transform;
            Vector3 f = ct.forward; f.y = 0f;
            if (f.sqrMagnitude < 1e-4f) f = Vector3.forward;
            f.Normalize();
            Vector3 pos = ct.position + f * distance + Vector3.down * below;
            // move the root (canvas + ISDK poke/ray surfaces created under it by the leader) as one unit
            transform.SetPositionAndRotation(pos, Quaternion.LookRotation(pos - ct.position, Vector3.up));
        }
    }
}
