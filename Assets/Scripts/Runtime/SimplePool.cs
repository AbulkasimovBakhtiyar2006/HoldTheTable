using System;
using System.Collections.Generic;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>Minimal component pool. Get() activates, Release() deactivates. Owner: Agent B.</summary>
    public sealed class SimplePool<T> where T : Component
    {
        readonly Stack<T> free = new Stack<T>();
        readonly Func<T> factory;

        public int FreeCount => free.Count;

        public SimplePool(Func<T> factory, int prewarm = 0)
        {
            this.factory = factory;
            for (int i = 0; i < prewarm; i++)
            {
                var t = factory();
                if (t == null) break;
                t.gameObject.SetActive(false);
                free.Push(t);
            }
        }

        public T Get()
        {
            while (free.Count > 0)
            {
                var t = free.Pop();
                if (t != null) { t.gameObject.SetActive(true); return t; }
            }
            var n = factory();
            n.gameObject.SetActive(true);
            return n;
        }

        public void Release(T t)
        {
            if (t == null) return;
            t.gameObject.SetActive(false);
            free.Push(t);
        }

        public void Clear() => free.Clear();
    }
}
