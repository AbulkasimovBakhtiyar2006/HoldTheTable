# QA_REPORT — HOLD THE TABLE

T0 = 2026-11-18 21:14:32 (Mac local; device logcat clock shows +1 h). Device: Quest 3S `340YC10G9L06GW` (metavr product panther). Unity 6000.6.9f1, Meta SDK 209.0.0, MRUK 209.0.0.
Final APK: `/Users/abulkasimovbakhtiyar/IdeaProjects/TableCiv/Builds/HoldTheTable.apk` — 76 301 100 B (< 150 MB).

## Build & static checks
| # | Check | Result | Evidence |
| --- | --- | --- | --- |
| G1a | Packages resolve (incl. MRUK 209.0.0), project compiles | PASS | import.log: 0 `error CS` |
| G1b | Minimal APK (rig + passthrough + MRUK) | PASS | scratchpad gate1/build_gate1.log `result=Succeeded errors=0`, 75 478 086 B |
| G1c | Minimal APK install + launch | PASS | logcat: OpenXR running, MRUK 1 room / 15 anchors, no Unity exceptions |
| C1 | Full project compiles, 0 errors | PASS | build.log, build2.log |
| C2 | SceneBuilder generates full scene, no missing types/fields | PASS | build2.log `[SceneBuilder] built …`, no MISSING lines |
| V1 | Validator | PASS | Logs/validator.txt (first run FAIL: 4 runtime-built scroll refs + CHANGELOG missing → fixed) |
| B1 | Final APK build | PASS | build2.log `result=Succeeded errors=0` |
| D1 | Four documents non-empty | PASS | CREDITS/CHANGELOG/SUBMISSION/SHOTLIST (Validator checks) |

## Device runtime (automated: install, launch, logcat, screenshot)
| # | Check | Result | Evidence |
| --- | --- | --- | --- |
| R1 | Build #1 runtime log | FAIL→fixed | NullReferenceException in TMP_Settings (TMP Essential Resources missing) on every UI label; fixed by adding `Assets/TextMesh Pro` |
| R2 | Final APK install + launch, no exceptions / missing refs | PASS | logcat 22:57 (device clock): only Unity's benign `AssetPackManager` class probe; no `Exception:` from game code |
| R3 | MRUK path: real surfaces registered | PASS | `[SurfaceRegistry]` floor 4.13×3.11 m y≈0, table 1.41×0.64 m y=0.78, furniture 1.74×0.91 m y=0.43; 2 obstacle volumes; phase `CivilizationSetup` |
| R4 | Passthrough visible, hands tracked, wrist HUD shown | PASS | device screenshot (real room + hand outline + HUD) |
| R5 | Forced fallback (MRUK disabled via `force_fallback` file) | PASS (startup) | `forceFallback=True`, `fallback table spawned at (0.09, 0.65, 0.42)`, no exceptions |
| R6 | Idle frame timing | PASS (idle only) | VrApi `FPS=72/72`, App≈0.65 ms, CPU&GPU≈2.8 ms, Stale=0 in steady state |
| R7 | BuildPhase 12 s countdown → Wave 1 auto start, monsters spawn & path | PASS (automated) | stress log: active grows 30→36 after countdown, no exceptions |
| R9 | Scroll + HUD spawn near the player on a head orbit (not on the floor) | PASS | build4; device screenshot 23:07: scroll right of view at eye level, HUD low-left with grab bar; no exceptions. Moving them by hand along the orbit: NOT RUN (needs headset) |
| R10 | Iteration 2 build (system keyboard, banner layers, corner spawns, rotation) | PASS (build) | build6.log `result=Succeeded errors=0`, Validator PASS, manifest has `oculus.software.overlay_keyboard` + `USE_SCENE`; installed. Runtime log NOT RUN: headset in STANDBY (proximity) |
| R11 | Iteration 3 build (climbing/ropes/ladders, free-floor portals, grab fix, clutter check, wall lines, economy, music+SFX) | PASS (build + startup) | build8.log `result=Succeeded errors=0`, Validator PASS (5 tray slots); device log 00:01: no exceptions, 3 surfaces, `[RoomCornerSpawns] portals=1 area=11.3m²`. Hands-only play of these features: NOT RUN |
| R8 | Batchmode exit code | NOTE | build3: `result=Succeeded` but Unity process exit 134 (abort during editor shutdown after build); APK valid and installed |

## Hands-only gameplay & acceptance tests (require a person in the headset)
| # | Check | Result | Note |
| --- | --- | --- | --- |
| H1 | Acceptance: civ poke → type `Heavenly Kingdom` → colors → draw emblem → place Town Hall → own emblem on waving banner → build defenses → start wave | NOT RUN | needs physical hands-only run |
| H2 | Town Hall on table and on floor/off-table; invalid support red ghost; far ray+pinch placement | NOT RUN | |
| H3 | Scroll move (pinch grab), Undo/Clear, tracking loss while drawing | NOT RUN | |
| H4 | Profile persisted across relaunch; RESTART keeps empire; New Empire | NOT RUN | |
| H5 | All 3 unit types, timber, 3 waves + Ram, win and loss, poke RESTART | NOT RUN | |
| H6 | Shockwave gesture, 2 charges, push/damage; upgrades; Quartermaster 6 s auto-skip | NOT RUN | |
| H7 | Fallback path played to EndScreen | NOT RUN | startup verified only (R5) |
| P1 | 30 active units sustained ≥ 60 fps | PASS (automated) | `stress_test` hook on device, MRUK surface, 30→36 active monsters (30 + auto-started wave 1) for 24 s: Unity fps 72.0 every second, worst frame 13.9 ms; VrApi FPS 72–73/72, Stale 0, App≈0.9 ms, CPU&GPU≈3.5 ms. Logs/device/stress_logcat.txt |
| A1 | OVRSpatialAnchor on founded hall | NOT RUN | logged at founding; needs H1 |

Not verified ≠ done: Definition of Done is NOT met until H1–H7 pass in the headset (P1 passed via the automated stress hook). The 4 `Exception:` lines in Logs/device/stress_logcat.txt are Unity's AssetPackManager probe and the system PackageIntegrityClient (pid 3920), not game code.
