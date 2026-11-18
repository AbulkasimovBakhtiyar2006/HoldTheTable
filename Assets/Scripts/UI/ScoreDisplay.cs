using TMPro;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>Timber, hall HP and shockwave charge readouts. Only rewrites TMP text when values change.</summary>
    public class ScoreDisplay : MonoBehaviour
    {
        public TMP_Text timberText;
        public TMP_Text hallText;
        public TMP_Text chargesText;
        public TMP_Text goldText;
        public Color rateColor = new Color(0.68f, 0.78f, 0.50f);    // #ADC780 Muted Sage-Gold
        public Color healthyColor = new Color(0.95f, 0.89f, 0.76f); // #F2E3C2 Warm Vellum Cream
        public Color dangerColor = new Color(0.85f, 0.29f, 0.22f);  // #D94A38 Heraldic Vermilion Warning

        int lastTimber = int.MinValue, lastHp = int.MinValue, lastMax = int.MinValue, lastCharges = int.MinValue;
        int lastGold = int.MinValue, lastTimberRate = int.MinValue, lastGoldRate = int.MinValue;
        string rateHex;

        public void Bind(TMP_Text timber, TMP_Text hall, TMP_Text charges)
        {
            timberText = timber;
            hallText = hall;
            chargesText = charges;
            lastTimber = lastHp = lastMax = lastCharges = int.MinValue;
            lastGold = lastTimberRate = lastGoldRate = int.MinValue;
        }

        public void BindGold(TMP_Text gold)
        {
            goldText = gold;
            lastGold = lastGoldRate = int.MinValue;
        }

        /// <summary>Timber/gold amounts plus income per second (rates shown with one decimal, hidden when 0).</summary>
        public void RefreshEconomy(int timber, float timberRate, int gold, float goldRate)
        {
            int tr = Mathf.RoundToInt(timberRate * 10f), gr = Mathf.RoundToInt(goldRate * 10f);
            if ((timber != lastTimber || tr != lastTimberRate) && timberText != null)
            {
                lastTimber = timber; lastTimberRate = tr;
                timberText.text = WithRate(Strings.Format(Strings.Timber, timber), tr);
            }
            if ((gold != lastGold || gr != lastGoldRate) && goldText != null)
            {
                lastGold = gold; lastGoldRate = gr;
                goldText.text = WithRate(Strings.Format(Strings.Gold, gold), gr);
            }
        }

        string WithRate(string baseText, int rateTimes10)
        {
            if (rateTimes10 == 0) return baseText;
            if (rateHex == null) rateHex = ColorUtility.ToHtmlStringRGB(rateColor);
            string rate = Strings.Format(Strings.IncomeRate, (rateTimes10 / 10f).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
            return baseText + " <size=70%><color=#" + rateHex + ">" + rate + "</color></size>";
        }

        public void RefreshStatus(int hallHp, int hallMax, int charges)
        {
            if ((hallHp != lastHp || hallMax != lastMax) && hallText != null)
            {
                lastHp = hallHp;
                lastMax = hallMax;
                hallText.text = hallMax > 0 ? Strings.Format(Strings.KeepHp, hallHp, hallMax) : string.Empty;
                hallText.color = hallMax > 0 && hallHp <= hallMax / 3 ? dangerColor : healthyColor;
            }
            if (charges != lastCharges && chargesText != null)
            {
                lastCharges = charges;
                chargesText.text = Strings.Format(Strings.Charges, charges);
            }
        }

        public void Refresh(int timber, int hallHp, int hallMax, int charges)
        {
            if (timber != lastTimber && timberText != null)
            {
                lastTimber = timber;
                timberText.text = Strings.Format(Strings.Timber, timber);
            }
            if ((hallHp != lastHp || hallMax != lastMax) && hallText != null)
            {
                lastHp = hallHp;
                lastMax = hallMax;
                hallText.text = hallMax > 0 ? Strings.Format(Strings.KeepHp, hallHp, hallMax) : string.Empty;
                hallText.color = hallMax > 0 && hallHp <= hallMax / 3 ? dangerColor : healthyColor;
            }
            if (charges != lastCharges && chargesText != null)
            {
                lastCharges = charges;
                chargesText.text = Strings.Format(Strings.Charges, charges);
            }
        }
    }
}
