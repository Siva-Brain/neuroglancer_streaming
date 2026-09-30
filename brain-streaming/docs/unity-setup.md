# Unity client setup (the "next" client)

The browser client already proves the architecture. These are the Unity scripts
that speak the **identical protocol**, so the DGX server needs no changes. Plan
~1–2 hours the first time (Unity install + scene wiring).

## 1. Create the project

- Unity Hub → New Project → **3D (URP)** or **3D (Built-in)**, Unity **2021.3 LTS+**.
- Copy `unity/BrainStreaming/Assets/Scripts/` into your project's `Assets/`.

## 2. Add the JSON dependency

Window → Package Manager → **+** → *Add package by name* →
`com.unity.nuget.newtonsoft-json`. (Used by `BrainStreamClient` to parse
`/info`, `/chunks`, `/view`.)

## 3. Build the scene

Create `Assets/Scenes/BrainStream.unity` and add:

| GameObject | Component(s) | Settings |
|------------|--------------|----------|
| `Main Camera` | `BrainCameraController` | Target `(0,0,-10)`, Distance `360` |
| `Brain` (empty) | `BrainRenderer` | assign a URP/Standard `BaseMaterial` |
| `App` (empty) | `BrainStreamingApp` | `ServerUrl = http://<DGX_IP>:8000`; drag in `Brain` (Renderer), `Main Camera` (Cam), and the HUD |
| `App` | `BrainHud` | same GameObject as `BrainStreamingApp`; drag into its `Hud` field |

Point light + ambient recommended so LOD facets read well.

## 4. Run

Press **Play**. Expected (same as the browser):
- coarse LOD0 brain appears region-by-region,
- visible regions upgrade to LOD1 → LOD2,
- orbiting re-prioritizes the newly visible region,
- revisited regions come from `BrainChunkCache` (no re-download),
- HUD shows chunks/bytes/LOD/active/pending/hits/FPS.

Windows firewall: allow outbound to the DGX port (usually already allowed). The
DGX must allow inbound `8000/tcp`.

## 5. Class map (networking ⟂ rendering)

| Class | Role |
|-------|------|
| `BrainStreamClient` | networking only: `GetInfo/GetChunks/GetChunk/PostView`, `BRN1` parse |
| `BrainChunk` / `BrainChunkMeta` | decoded geometry / region metadata |
| `BrainChunkCache` | dedup + bytes + best-LOD tracking |
| `BrainRenderer` | Unity meshes, LOD replacement, display-mode branch |
| `BrainCameraController` | orbit camera (motion drives streaming) |
| `BrainStreamingApp` | orchestrator: LOD0 boot → camera-driven upgrades, concurrency, cancel |
| `BrainHud` | on-screen stats |

---

## 6. Sony ELF-SR2 (optional, do last)

The core prototype must work **without** the Sony SDK. To add it:

1. Install the **Sony Spatial Reality Display SDK for Unity** (from Sony's
   developer site) on the Windows machine with the ELF-SR2 attached.
2. Import the SDK package; add its **SR Display rig / camera** prefab to the
   scene (it replaces the normal `Main Camera` view path).
3. Parent the SR rig so it renders the same `Brain` object tree produced by
   `BrainRenderer` — the streaming layer is display-agnostic and needs no change.
4. Set `BrainRenderer.DisplayMode = SonySpatialReality`. Keep
   `BrainCameraController` driving the *logical* camera you send in `VIEW_UPDATE`
   (the SR rig provides the stereo/lightfield views; streaming still keys off one
   logical viewpoint).
5. Build to a Windows Standalone player; run on the ELF-SR2 host.

If the SDK is absent, leave `DisplayMode = NormalUnity` — everything else works.
