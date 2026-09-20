# Shaders

`ScreenSaturation.shader` is the source of the full-screen colour effect (real desaturation and an
edge vignette). `nextbots.saturation` is that shader compiled into a Unity AssetBundle, and it is what
the mod actually loads: it is embedded in `NextBotsRagdoll.dll` as a resource, so there is nothing extra
to install.

It has to be built with the **same Unity version as the game** (6000.2.9f1 for Gorilla Tag when this was
written), or the bundle will not load.

## Rebuilding it

1. Make an empty Unity project on the game's Unity version and give it the Universal Render Pipeline
   package. (Gorilla Tag is 6000.2.9f1; a copy of URP 17.x is in any recent Unity 6 project.)
2. Put `ScreenSaturation.shader` in `Assets/Shaders/`.
3. Before building, assign a URP asset in Graphics settings and in every Quality level. **This step is
   not optional**: Unity's SRP shader stripper deletes any shader whose `RenderPipeline` tag does not
   match the project's active pipeline, and with none assigned it silently strips every variant and
   leaves an empty bundle that reports "not supported".
4. Build an AssetBundle named `nextbots.saturation` containing only that shader, for
   StandaloneWindows64, with the D3D11, D3D12 and Vulkan graphics APIs, and copy it here.

## Why the shader has its own `NB_STEREO` keyword

Single-pass instanced VR needs its own shader variant. Unity's built-in `STEREO_INSTANCING_ON` variant
is stripped from a bundle built in a project with no XR set up, whatever the settings say, so the shader
declares a keyword of its own, which cannot be stripped, and defines the built-in one from it. The mod
turns it on only when the game is really rendering in single-pass VR, and only if
`TrueDesaturationInVr` is on - it has not been tested on a headset.
