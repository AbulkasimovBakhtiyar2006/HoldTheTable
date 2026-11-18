using System;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>Hit points with damage/death events. Owner: Agent B.</summary>
    public class Health : MonoBehaviour
    {
        [SerializeField] float max = 10f;
        [SerializeField] float current = 10f;

        public float Max => max;
        public float Current => current;
        public bool IsDead => current <= 0f;
        public float Fraction => max > 0f ? Mathf.Clamp01(current / max) : 0f;

        public event Action<Health> Damaged;
        public event Action<Health> Died;

        public void Init(float maxHp)
        {
            max = Mathf.Max(0.01f, maxHp);
            current = max;
        }

        /// <summary>Change max HP (e.g. after an HP upgrade) keeping the current fraction.</summary>
        public void SetMaxKeepFraction(float newMax)
        {
            if (IsDead) { max = Mathf.Max(0.01f, newMax); return; }
            float f = Fraction;
            max = Mathf.Max(0.01f, newMax);
            current = Mathf.Max(0.01f, max * f);
        }

        public void Damage(float amount)
        {
            if (amount <= 0f || IsDead) return;
            current = Mathf.Max(0f, current - amount);
            Damaged?.Invoke(this);
            if (current <= 0f) Died?.Invoke(this);
        }

        public void Heal(float amount)
        {
            if (amount <= 0f || IsDead) return;
            current = Mathf.Min(max, current + amount);
        }

        public void ResetFull()
        {
            current = max;
        }
    }
}
