# Devpost "Additional info" — HOLD THE TABLE

Fields marked **[YOU]** are personal/legal and must be filled by you. Everything else is ready to paste.

---

**Submitter Type** — [YOU] Individual (if you build alone) / Team / Organization.

**Country of residence** — [YOU]

**Email of your developer Organization / Meta VR Start membership** — [YOU] the email of the Meta developer account that is in the Start program.

**Team members (Name, Email, Role)** — [YOU] Leave empty if solo. Otherwise e.g.:
`<Name> — <email> — Lead developer, design, Unity/XR integration`

**Team / Organization name** — [YOU] the name to show as "HOLD THE TABLE created by ___".

**Track** — Gaming

**Division** — New

> The Unity project was created from scratch on 2026-11-18 (inside the window that began Sep 24, 2026). Only project settings were copied from your earlier Quest project; no code or assets. Do not pick "Adapted".

**Adapted / Significantly Updated summary** — leave empty (not applicable).

**Build Path** — Unity

**Release channel URL** — [YOU] After uploading `Builds/HoldTheTable.apk` to a release channel named exactly `Competition` in the Meta VR Developer Dashboard, create an Invite URL and paste it here.

**Meta VR capabilities used** (tick whichever of these the form lists):
- Hand Tracking
- Interaction SDK (hand grab, poke, ray, poke/ray canvases)
- Passthrough / Mixed Reality
- Mixed Reality Utility Kit (MRUK) / Scene understanding (room surfaces)
- Spatial Anchors — only if you confirm it works in the headset (QA: NOT RUN yet)

---

**Describe how Hand Interactions are implemented into your experience.**

HOLD THE TABLE is hands-only from first launch to restart; no controller is ever needed. It is built on the Meta XR Interaction SDK with hand tracking over passthrough.

- Poke: the Imperial Scroll's large buttons — choosing one of three civilizations, colors, "Found Empire", RESTART and "New Empire" — are pressed with a fingertip on poke-enabled world-space canvases. The empire name is typed on a hand-poke keyboard built into the scroll.
- Finger drawing: the player draws their own banner emblem with the tip of the index finger. Touching the canvas starts a stroke and lifting ends it; there are Undo, Clear and two brush sizes. If tracking is lost, the stroke ends cleanly. That same drawing then waves on a cloth banner beside the Town Hall.
- Grab and place: pinch the scroll's roller to move it. Pinch the miniature Town Hall to pull it out of the scroll, then release it onto any real surface found by MRUK (table, sideboard, floor). A green or red ghost shows whether the footprint is supported and clear. Surfaces out of reach are targeted with a hand ray and a pinch. Walls, archer towers and barricades are pinched from a tray and placed the same way, and each one extends the settlement's build area.
- Gesture: sweeping both palms outward releases a shockwave (two charges) that damages attackers and pushes them back, sometimes off the table's edge.
- UI: the combat HUD is attached to the wrist, so it stays readable within arm's reach in a seated, two-foot play space.

**What makes you excited about the future of VR?** (optional, ≤150 words)

Mixed reality turns the room you already live in into the game board. A coffee table becomes a plateau and a shelf becomes a cliff. When hands replace controllers, nothing stands between the idea and the action: you pinch a tower and set it down, or you draw your flag with a fingertip. That makes VR something you can share with anyone in the room, not only people who learned a gamepad. I'm excited for experiences that remember your space: an empire still waiting on your shelf tomorrow, or a friend sitting across the table and laying siege to it. Short, seated sessions that fit a bus ride, plus persistent spaces you return to, are how VR becomes part of everyday life rather than an event.

**Target launch date** — [YOU] e.g. `Q2 2027` (or whatever you plan).

---

**Checkboxes** — [YOU] tick only if true: Start program member; age of majority; eligible jurisdiction; not an employee of the sponsor, its affiliates or any government entity.
