# SUBMISSION — HOLD THE TABLE

**Tagline (≤140):** Found your own empire anywhere in your real room — name it, draw its banner with a fingertip, then hold it against the siege, hands only.

## Description

HOLD THE TABLE is a seated, hands-only mixed-reality empire-building and siege-defense game for Meta Quest 3 and Quest 3S. Your real room stays visible through passthrough; the empire lives inside it.

**Inspiration.** Every living room already has terrain: a coffee table is a plateau, a shelf is a cliff, the floor is a plain. We wanted the joy of a tabletop war game without the box, the pieces or the setup — and we wanted the player to feel that the kingdom on the table is *theirs*, not a preset faction.

**How it plays.** An Imperial Scroll unfurls beside you: parchment, wooden rollers, gold trim, a wax seal. Pinch its roller to move it; poke its large buttons with a fingertip. Choose one of three fictional civilizations — Celestial Dynasty (tiered roofs), Verdant Clans (timber halls) or Iron Dominion (stone towers) — which shapes the architecture of everything you build. Type your own empire name on the scroll's hand keyboard (say, "Heavenly Kingdom"), pick banner and symbol colors, and draw your emblem with your index finger, with undo, clear and two brush sizes. Poke "Found Empire", pull the miniature Town Hall out of the scroll and place it on any suitable surface: a table, a sideboard, the floor. A green or red ghost shows whether the whole footprint is supported and clear; distant spots can be picked with a hand ray and a pinch. The Town Hall rises beside a waving banner carrying your drawing and your empire's name.

The settlement grows outward from the Town Hall: every wall, archer tower and barricade you pinch from the tray extends the connected build area, while real edges and gaps limit it. Timber accumulates over time. Three waves march in from the local battlefield edge on curved paths; the last brings an armored Ram. Sweep both palms outward to release a shockwave (two charges) that damages and shoves attackers, sometimes off the table. Between waves a tiny Quartermaster offers two upgrades based on what you built; ignore him and he leaves after six seconds. Victory or defeat shows your score and a big RESTART button; your empire and Town Hall location are kept, or start a New Empire from the scroll.

**How we built it.** Unity 6 and URP, Meta XR Core SDK, Meta Interaction SDK (hand grab, poke, ray, poke/ray canvases) and Mixed Reality Utility Kit room surfaces, with a procedural virtual table fallback. Every mesh, material, sound and UI element is generated in code; an editor script generates the scene and a validator checks it. Monsters follow Catmull-Rom splines with separation steering; the banner is a light vertex-wave cloth pinned to its pole. Everything runs offline. It was built in one session by a lead developer coordinating parallel AI coding agents.

**Future plans.** Settlements on several surfaces of one room, colocated two-player sieges, persistent anchors so empires wait on your shelf, and more civilizations.

**Disclosure.** Base samples: none — no Meta or Unity sample scenes or sample assets are included; the project was created from scratch in this session (Unity project settings copied from the author's own earlier Quest project as a configuration starting point). Licenses: Meta XR SDKs under the Oculus SDK License; Unity packages under the Unity Companion License (see CREDITS.md). Added in this project: all gameplay, UI, procedural art, audio, scene generation, validator and build tooling.
