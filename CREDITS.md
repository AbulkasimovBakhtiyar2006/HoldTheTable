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

## Civilization selection art (Assets/Resources/CivArt/)
Wikimedia Commons, all CC0 1.0 (http://creativecommons.org/publicdomain/zero/1.0/); downscaled to 512 px and recompressed.
- celestial.jpg: "Temple of Heaven-1" by Balon Greyjoy, https://commons.wikimedia.org/wiki/File:20200110_Temple_of_Heaven-1.jpg
- verdant.jpg: "Borgund stave church (17162)" by KenSwe72, https://commons.wikimedia.org/wiki/File:Borgund_stave_church_(17162).jpg
- iron.jpg: "Burg Eltz 5" by Ahrtaler2, https://commons.wikimedia.org/wiki/File:Burg_Eltz_5.jpg
