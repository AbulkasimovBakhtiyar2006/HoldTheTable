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

            UiKit.FramedPanel(content, "UpgradeFrame", Vector2.zero, new Vector2(900f, 420f),
                UiKit.WalnutFrame, UiKit.DarkPanel, UiKit.WalnutFrame, 3f);

            headerText = UiKit.Label(content, Strings.QuartermasterGreeting, new Vector2(0f, 168f), new Vector2(820f, 48f), UiKit.FontHeading);
            headerText.fontStyle = FontStyles.Bold;
            headerText.color = UiKit.GoldBright;

            for (int i = 0; i < 2; i++)
            {
                int idx = i;
                var card = UiKit.Button(content, string.Empty, new Vector2(i == 0 ? -212f : 212f, 20f), new Vector2(404f, 236f), () => Pick(idx), 30);
                UiKit.SetButtonColor(card, UiKit.ParchmentLight);
                var crt = (RectTransform)card.transform;
                var defaultLabel = card.GetComponentInChildren<TMP_Text>(true);
                if (defaultLabel != null) defaultLabel.gameObject.SetActive(false);

                var topStripe = UiKit.Panel(crt, new Vector2(0f, 100f), new Vector2(120f, 8f), UiKit.RubricCrimson);
                topStripe.GetComponent<Image>().raycastTarget = false;

                titles[i] = UiKit.Label(crt, string.Empty, new Vector2(0f, 48f), new Vector2(364f, 70f), UiKit.FontHeading - 2);
                titles[i].fontStyle = FontStyles.Bold;
                titles[i].color = UiKit.Ink;
                titles[i].enableAutoSizing = true; titles[i].fontSizeMin = 22; titles[i].fontSizeMax = UiKit.FontHeading - 2;

                descs[i] = UiKit.Label(crt, string.Empty, new Vector2(0f, -42f), new Vector2(364f, 110f), UiKit.FontCaption + 1);
                descs[i].color = UiKit.InkMuted;
                cards[i] = card;
            }

            // timer bar
            var barBg = UiKit.Panel(content, new Vector2(0f, -128f), new Vector2(828f, 12f), new Color(1f, 1f, 1f, 0.14f));
            barBg.GetComponent<Image>().raycastTarget = false;

            timerFill = UiKit.Panel(barBg, new Vector2(0f, 0f), new Vector2(828f, 12f), UiKit.GoldBright);
            timerFill.GetComponent<Image>().raycastTarget = false;
            timerFill.anchorMin = new Vector2(0f, 0.5f);
            timerFill.anchorMax = new Vector2(0f, 0.5f);
            timerFill.pivot = new Vector2(0f, 0.5f);
            timerFill.anchoredPosition = new Vector2(0f, 0f);

            skipText = UiKit.Label(content, string.Empty, new Vector2(0f, -168f), new Vector2(820f, 34f), UiKit.FontCaption);
            skipText.color = new Color(0.72f, 0.68f, 0.60f);
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
            timerFill.sizeDelta = new Vector2(828f, 12f);
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
            timerFill.sizeDelta = new Vector2(828f * (left / timerSeconds), 12f);
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
