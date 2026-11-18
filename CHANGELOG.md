# CHANGELOG

## 2026-11-18 — initial one-session build (no prior project history; repository had no commits)

### Project & tooling
- Unity 6000.6.9f1 project created in this folder; Quest Android/IL2CPP/ARM64/Vulkan+GLES3/linear settings, OpenXR loader and Meta SDK 209.0.0 configuration seeded from the author's own ArenaClash project; added MR Utility Kit 209.0.0.
- `ProjectSetup` (idempotent Quest/passthrough/scene/anchor/hand config, always-included shaders, `HTT_MRUK` define), `SceneBuilder` (generates the whole scene), `Validator` (components, references, tray/civilization counts, no pre-placed Town Hall, documents), `BuildScript.BuildApk` (single build entry, batchmode capable).

### AR empire founding
- Imperial Scroll: procedural parchment with curled edges, wooden rollers, gold trim, wax seal, unfurl animation; pinch-grab to move; poke/ray canvas.
- Civilization selection (Celestial Dynasty / Verdant Clans / Iron Dominion), hand poke keyboard for a custom empire name (1–32 chars, suggested fallback), banner + emblem color swatches, fingertip emblem drawing with Undo/Clear/brush size, live banner preview, `Found Empire`, `New Empire`.
- Local offline profile (`profile.json`) restoring last civilization, name, colors and emblem.
- Town Hall token pulled from the scroll; near placement by hand grab, far placement by hand ray + pinch; green/red ghost with full-footprint support and obstacle checks across all MRUK horizontal surfaces (tables, floor, furniture) or the fallback table.
- Waving procedural banner (vertex-wave cloth pinned to the pole, double-sided, player emblem, empire name plaque).

### Siege gameplay
- Town Hall as keep (20 HP) with civilization-specific architecture; Wall / Archer Tower / Barricade with distinct per-civilization silhouettes, costs 10/15/8, HP, blocking, ranged arrows, slowing.
- Connected settlement build area (0.45 m around the hall, +0.20 m per building), max 12 defenses, overlap/gap checks.
- Timber economy (40 start, +6/s), 12 s build phases with skip, 3 waves (6/10/16) with a 4x-HP Ram, Catmull-Rom paths with separation steering from the local battlefield boundary, pooled monsters and arrows.
- Two-palm outward sweep shockwave (2 charges, 0.6 m damage + push), Quartermaster NPC with two usage-based upgrade cards (damage, HP, cost, extra charge) and 6 s auto-skip.
- Score (kills×10 + hall HP×25 + timber), Victory/Defeat EndScreen with poke RESTART keeping the empire and hall location.
- MRUK surface discovery with 3 s timeout to a procedural fallback table (forced via PlayerPrefs `htt_force_fallback`), session spatial anchor attempt.
- Wrist-attached diegetic HUD clamped to a 30° cone, procedural audio clips, pooled ring/burst VFX.

### QA tooling
- TextMesh Pro essential resources added (fixes runtime TMP_Settings NullReference found on device).
- Device QA hooks: `force_fallback` and `stress_test` files in the app's persistentDataPath (pushed via adb) force the fallback table or auto-found + 30-monster frame-timing run.
- Imperial Scroll and combat HUD now live on a personal orbit around the head (`OrbitAnchor`): spawn only after a valid head pose (fixes scroll appearing on the floor), lazily follow when the player turns, and can be pinch/grab-moved — on release the spot is projected back onto the orbit (radius 0.3–0.7 m). HUD has a grab bar.
- Building tray base hidden until the empire is founded (`TrayVisibility`).

### Iteration 2 (user feedback)
- Empire name typed with the Meta system overlay keyboard (`TouchScreenKeyboard`, `requiresSystemKeyboard` → manifest `oculus.software.overlay_keyboard`); scroll keyboard kept only as editor fallback.
- Layered heraldic banners: 18-color palette, 21 procedural patterns stacked up to 6 layers, 12 built-in preset banners (offline), drawn emblem on top; Presets / Patterns / Emblem tabs on the scroll.
- Enemies spawn from portals at the corners of the scanned MRUK room floor and advance slowly across the floor, climbing the edge of the surface that holds the Town Hall; fallback/no-scan keeps settlement-edge spawns.
- Pinch-and-circle gesture rotates any placed building or the Town Hall (`BuildingRotator`).
- Build-area field around the capital no longer drawn (placement rules unchanged).
- Larger scroll grab zones (both rollers + side edges).

### Iteration 3 (user feedback)
- Terrain-aware enemies: floor routes go around furniture or climb over it (climb/top/descend); first climber sets ropes for faster climbing; enemy Engineers (wave 2+) build long ladders to the Town Hall surface without crossing furniture; shockwave destroys ropes/ladders.
- Spawn portals only on free floor: away from furniture (0.3 m) and walls (0.6 m), ≥2 m from the Town Hall, 1 portal (2 in large rooms); swirling green vortex visual with sparks and floor glow, looping portal hum.
- Grab fix: manual pinch-drag fallback in GrabWatcher so the scroll, HUD and Town Hall token can always be taken by hand even when ISDK canvas poke/ray hover wins interactor priority.
- Placement blocked on small objects lying on surfaces (cup, laptop…) via downward raycasts into the MRUK global scene mesh.
- Wall line drawing: hold the wall token low over the surface and sweep — joined wall segments are built along the path.
- Economy: Gold resource, Farm (+1.5 timber/s) and Mine (+1 gold/s) with civilization-styled models, Archer Tower costs gold, separate economy limit (8), Harvest upgrade, 5-slot tray, HUD shows Gold and income rates.
- Music: public-domain classical pieces transcribed in code and synthesized offline (Pachelbel Canon in D, Bach/Petzold Minuet in G, Grieg In the Hall of the Mountain King for waves, Beethoven Ode to Joy on victory, Chopin Funeral March on defeat); 16 new procedural SFX (scroll, page, keys, hammer, arrows, march, portal hum, horn, coins, harvest, rope, ladder, climb, ram, hall thud, banner flap).

## Swarm sprint
- Fixed Unity 6 compile errors (obsolete GetInstanceID).
- Added CombatJuiceController (floating damage text, hit flash, fire/smoke), TacticalAdvisorController (threat lines, skippable countdown ring), WorldEventsController (Golden Age streak, ruin obelisk), HolographicRadar (wrist radar).
- GameDirector.AddTimber(float).

## Swarm round: Miracles, Warlords, Trophies, Chronicle, Wonders
- DivineMiraclesAndHeroController: grabbable Hero (Heroic Slam), Meteor Rain (two palms), Protective Dome (crossed wrists).
- WarlordAiPersonalityDirector: Saboteur / Breaker / Shaman named warlords with unique AI and healing totems.
- ImperialTrophiesAndAudioJuiceController: velvet trophy stand beside the scroll, persistent unlockable cups, procedural chimes.
- WarChronicleTracker: gold/timber income graph and extended stats on EndScreen (MonsterUnit.FellOffTable hook).
- WorldEventsController: Jade Bell, Yggdrasil, Tesla Tower wonders per civilization.

## Swarm round 2
- HarpoonerUnit + instant ropes (ClimbRoutes/WaveSpawner/MonsterUnit): Harpooner spawns first each wave, fires harpoon, rope deploys immediately; fallback rope if it dies.
- UnitScale: shared soldier size standard (BuildingVisuals/DefenderUnit/BarracksSpawner).
- HolographicRadar redesign: ornate bezel, glass dome, sweep, per-class blips, danger arrows.
- AntiAirTower: 6th tray slot, flak missiles vs flyers only.

## Swarm round 3: civ unit redesign
- Soldiers (spearman/swordsman/archer) redesigned per civilization (DefenderUnit/BuildingVisuals), meshes combined by material.
- Hero Champion per civ (celestial/verdant/iron) with plinths and animations.
- Peasants/workers per civ via CivUnitStyle.cs with tools and walk/work animation.
