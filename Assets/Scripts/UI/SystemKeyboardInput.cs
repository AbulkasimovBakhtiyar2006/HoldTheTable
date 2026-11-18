using System;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>
    /// Opens the Meta Horizon OS system (overlay) keyboard via TouchScreenKeyboard and streams the text back.
    /// Requires OVRProjectConfig.requiresSystemKeyboard (set by ProjectSetup). Works with hand-tracking poke.
    /// </summary>
    public class SystemKeyboardInput : MonoBehaviour
    {
        TouchScreenKeyboard kb;
        Action<string> onChanged, onDone;
        string last;
        int maxLength = 32;

        public static bool Available => !Application.isEditor && TouchScreenKeyboard.isSupported;
        public bool IsOpen => kb != null;

        public void Open(string current, int max, Action<string> changed, Action<string> done)
        {
            maxLength = max;
            onChanged = changed; onDone = done;
            if (kb != null) { kb.active = false; kb = null; }
            kb = TouchScreenKeyboard.Open(current ?? string.Empty, TouchScreenKeyboardType.Default, false, false, false, false, string.Empty, max);
            last = current;
            if (kb != null) kb.characterLimit = max;
        }

        public void Close()
        {
            if (kb == null) return;
            kb.active = false;
            kb = null;
        }

        void Update()
        {
            if (kb == null) return;
            string t = kb.text ?? string.Empty;
            if (t.Length > maxLength) t = t.Substring(0, maxLength);
            if (t != last) { last = t; onChanged?.Invoke(t); }
            var s = kb.status;
            if (s == TouchScreenKeyboard.Status.Done)
            {
                kb = null;
                onDone?.Invoke(t);
            }
            else if (s == TouchScreenKeyboard.Status.Canceled || s == TouchScreenKeyboard.Status.LostFocus)
            {
                kb = null; // keep whatever was typed so far
            }
        }

        void OnDisable() => Close();
    }
}
