# CREDITS — HOLD THE TABLE

All game art, meshes, materials, UI and audio are generated procedurally in code at edit/run time (Unity primitives, generated meshes, `AudioClip.Create`). No Asset Store content, no .fbx, no third-party art or sound files.

Versions below are read from the installed `package.json` files in `Library/PackageCache`; licenses from each package's `LICENSE.md`.

| Component | Version | License |
| --- | --- | --- |
| Unity Editor | 6000.6.9f1 | Unity Terms of Service |
| Meta XR Core SDK (`com.meta.xr.sdk.core`) | 209.0.0 | Oculus SDK License Agreement (developers.meta.com/horizon/licenses/oculussdk) |
| Meta XR Interaction SDK (`com.meta.xr.sdk.interaction`) | 209.0.0 | Oculus SDK License Agreement |
| Meta XR Interaction SDK OVR Integration (`com.meta.xr.sdk.interaction.ovr`) | 209.0.0 | Oculus SDK License Agreement |
| Meta MR Utility Kit (`com.meta.xr.mrutilitykit`) | 209.0.0 | Oculus SDK License Agreement |
| Unity OpenXR Plugin (`com.unity.xr.openxr`) | 1.19.0 | Unity Companion License |
| XR Plug-in Management (`com.unity.xr.management`) | 4.7.0 | Unity Companion License |
| Universal Render Pipeline (`com.unity.render-pipelines.universal`) | 17.6.0 | Unity Companion License |
| Unity UI incl. TextMeshPro (`com.unity.ugui`) | 2.6.0 | Unity Companion License |
| Input System (`com.unity.inputsystem`) | 1.20.1 | Unity Companion License |

Notes:
- `com.meta.xr.sdk.all` and the standalone `com.unity.textmeshpro` package were not used: the project uses the individual Meta packages above (same SDK release line) and TextMeshPro as shipped inside `com.unity.ugui` 2.x in Unity 6.
- Project settings and XR loader configuration were copied from the author's own local project `ArenaClash` as a starting configuration; no code or assets from it are included.
- Default TextMeshPro font asset (LiberationSans SDF) ships with Unity UI / TextMeshPro essentials.
