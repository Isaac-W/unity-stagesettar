# StageSettAR

Localize an AR headset against a map exported from star-mobile (STAR: a small landmark network plus the 3D landmark positions, optionally a Gaussian splat), and place the map's content into the real room. Built for Meta Quest 3 with AR Foundation, behind a `CameraSource` interface so other cameras can be added.

The **map root** moves, never the XR Origin or the camera. Headset tracking stays smooth; the map is placed (and slowly refined) in tracking space.

## Install

1. Create the project from the Unity Hub **Mixed Reality** template (Unity 6000.3, URP).
2. Package Manager ▸ **Add package from disk…** ▸ `package.json` of this folder (a git URL or tarball also works).
3. The **StageSettAR Setup** window opens (reopen it any time from **Tools ▸ StageSettAR ▸ Setup…**). Click each enabled button, top to bottom. Unity recompiles after the package install; new steps then appear on their own.
4. **Tools ▸ StageSettAR ▸ Import STAR Map…**, then press Play: the headset looks for the room (see Localizing) and the map appears once it is found.

### What the setup creates

- **StageSettAR Rig** (`Assets/StageSettAR/StageSettAR Rig.prefab`): a variant of the Mixed Reality template's `MR Interaction Setup` with passthrough, hands and controllers. The tutorial objects (Goal Manager, Object Spawner, contact spawn triggers) are removed or switched off.
- **No locomotion.** The user moves by walking. The locomotion objects stay in the hierarchy but are switched off, and the controllers can't switch them back on.
- **Head collider.** A `Head` child of the camera carries a trigger collider tagged **Player** (layer Ignore Raycast) on a kinematic rigidbody. A trigger zone only needs `other.CompareTag("Player")`.
- **Gaze source and screen effects.** The `Head` also carries a `GazeSource` (the one place that says where the user is looking), and a `Screen Effects` child of the camera holds a `Camera Fade`, a `Camera Circle Wipe` and a `Passthrough Fade`. A `Boundary Keeper` child keeps asking Quest to hide the boundary (needs `com.unity.xr.meta-openxr` 2.4 or newer). A `Localizing Screen` child is the panel that guides the user while the room is found (see Localizing). See "Toolkit" below.
- **Editor User Model.** A blue stand-in body with an orange visor shows where the user is and which way they face, so you can place content in the Scene view. The user's own camera never sees it, and it is left out of builds (`EditorOnly` tag).
- **Quest settings.** Camera image access is turned on so the localizer can read the camera. The Quest boundary is hidden while passthrough is on, so the whole room or site is usable. Students still need a clear, safe space.
- **File ▸ New Scene ▸ StageSettAR** starts a scene with the rig, a light and a global volume. Post-processing is off on the XR camera, as in the template, so the volume does nothing until you enable post-processing.
- **Tools ▸ StageSettAR ▸ Rebuild XR Rig** recreates the prefab.

No git is needed on the student machine. The splat packages are fetched without it: WebP through the OpenUPM registry (the setup adds a scoped registry to `Packages/manifest.json`), and UnitySplats as a zip from GitHub that is unpacked into `Packages/com.arloopa.unitysplats` (an embedded package). Until UnitySplats is present, the splat-related parts of this package are not compiled, so a fresh project opens without errors.

Advanced options (bottom of the Setup window):

- **Use a local UnitySplats folder…**: for a fork or a clone you already have. Pick the folder that contains `package.json` (name `com.arloopa.unitysplats`); it is added to the manifest as a `file:` dependency instead of downloading.
- **Keep downloaded UnitySplats out of version control** (default on): writes `Packages/com.arloopa.unitysplats/.gitignore` for git, and for Unity Version Control adds a marked `# StageSettAR begin` / `# StageSettAR end` block to the workspace's `ignore.conf` (only if that file already exists; it is never created). Turning it off removes both. Files that are already tracked are not untracked retroactively. A copy that contains its own `.git` is left alone. The setting is stored in `ProjectSettings/StageSettARSettings.asset`.

Quest needs AR Foundation 6.x and `com.unity.xr.meta-openxr` (the Mixed Reality template includes them); `com.unity.ai.inference` is pulled in automatically.

## Import a map

**Tools ▸ StageSettAR ▸ Import STAR Map…**, pick the exported zip. It creates a `StarMap` asset (model, landmarks, floor map), converts the splat, and builds a prefab: a root with a `StarLocalizer`, a `Splat` child, an empty `Content` child (put your virtual objects here; they live in the map frame), and, if the map has a floor image, a `Floor` quad (visible by default; switch it off or delete it if you do not want it, and re-importing will not bring it back). "Add to scene" instantiates it and wires an `ArFoundationCameraSource` if the scene has an `ARCameraManager`.

Splat rendering needs the Setup step **Splat rendering (URP)**. It checks every renderer of the pipeline asset in Graphics settings and of each quality level (the Mixed Reality template has one per level: High Fidelity, Balanced, Performant), so a headset or phone that uses a different quality level is covered too. **Configure** adds the package's splat feature (`StageSettARSplatURPFeature`) to each renderer and deactivates Gsplat's own `Gsplat URP Feature`, also if it was added by hand. Ours renders before opaque geometry and avoids a Vulkan problem seen on Quest, so the two must not both be active. A renderer where you switched our feature off is reported and left alone. Renderers that are read-only or inside a package cannot be changed; the step lists them, and you assign a renderer from `Assets/` in the quality level instead. The import window checks the Setup steps the splat depends on; while one is not done it shows what is missing with an **Open Setup** button and imports the map without the splat.

## Localizing

When the scene has a map, the user sees a small panel float a little below eye level, about a metre ahead. It follows their view softly and never covers the room, which stays visible all the time.

- **Finding the room... look around slowly**, while the Startup phase waits for a few confident fixes to agree (`startup.fixesToShow`). There is no progress bar: the panel fades away as soon as the map is placed and shown.
- **Lost tracking, look around**, if the device lost its tracking (or the headset was taken off). The panel stays until tracking is back.
- **Couldn't find this room. Are you in the right place?**, if it gives up. It tries again by itself after a few seconds.

The panel is the `Localizing Screen` object under the StageSettAR Rig (or add the component **StageSettAR ▸ Localizing Screen** to your own world-space canvas). It finds the `STAR Localizer` by itself and stays hidden in scenes without a map. The words, the **Retry After Seconds** and the timings are plain fields on it.

**Hooking up an intro.** The map stays hidden until the room is found (until `startup.fixesToShow` fixes agree, so it appears within a second or two; it may move a little while it is refined). Select the object with the `STAR Localizer` and, under **On First Localized**, press `+`, drag in what should happen, and pick its method. For example drag in a `Camera Fade` and pick `Clear`, an `Object Fade` and pick `FadeIn`, or a `Splat Reveal` and pick `Play`. **On First Localized** runs only once, the first time the room is found. **On Settled** runs when enough of the room has been seen for the placement to be trusted and the Late phase begins. For a map that appears at once, do nothing.

For more advanced use the same component has **On Localizing**, **On Localized** (every time, including after tracking came back and the placement was kept), **On Settled**, **On Lost**, **On Failed** (with a reason) and **On State Changed**.

## Scene setup (Quest 3)

- A normal AR Foundation rig: XR Origin with the camera carrying `ARCameraManager`, plus `ArFoundationCameraSource` on the same object. It needs an OpenXR setup with the Meta camera feature enabled.
- Both `android.permission.CAMERA` and `horizonos.permission.HEADSET_CAMERA` are needed; the bundled androidlib declares them in the manifest. They must be granted **before** the camera starts: a camera that opens without them does not retry, and passthrough images never arrive. So the build starts with a scene that only asks for them (`CameraPermissionGate`) and then loads the next build scene. Setup step "Camera permission scene" makes it and puts it first in the build. If a scene starts without the permission anyway, `ArFoundationCameraSource` asks itself and restarts the camera manager on the grant, but that may not be enough; `IsReady` stays false until images arrive.
- The source timestamps each frame with the camera pose at capture time and applies the lens offset from the head, so localizing while turning your head is fine.
- Drop the map prefab into the scene and leave `autoStart` on.

### Camera source settings (`ArFoundationCameraSource`)

The same component serves Quest and phones (ARCore / ARKit) through AR Foundation only. The basics sit at the top level; everything else is in collapsed groups.

**Lens mount** (`mount`). The camera sensor is not at the head camera: on Quest 3 the left RGB camera is about 5 cm left of and 2 cm below the AR Camera and looks about 11° down, which would be roughly 19 cm of error per metre of distance if ignored. The mount is a pose relative to the AR Camera in Unity camera-local axes (x right, y up, z forward), and has two fields:

| Setting | Default |
|---|---|
| `position` | (-0.047, -0.023, 0.043) m, measured on a Quest 3 (left camera) |
| `eulerAngles` | (10.94, 0.20, -0.15)°, i.e. looking 10.9° down |

If the platform ever provides the lens pose through AR Foundation, that is used instead (`TryGetProvidedMount`); AR Foundation 6 does not expose camera extrinsics, so today the fields above are what is used. On phones the camera transform already is the sensor, so the mount is identity there and the fields are ignored (the screen-orientation roll is still applied).

**Image** (`image`). `maxWidth` (default 0 = automatic) only matters for the processor-side colour conversion described below. `gpuYuvConversion` (default on) enables the raw upload path.

**Debug** (`debug`). `logDiagnostics` (default off) writes the following once at start, prefixed `[StageSettAR]`: every camera configuration (width, height, frame rate) and the chosen one; the CPU image format with plane count, row and pixel strides; the intrinsics (fx, fy, cx, cy, resolution) and fx/width; whether the YUV path or the RGBA fallback is used and at what size; and which lens mount was used (`provided`, `default/custom` or `phone (identity)`) with its values. Turn it on when a device behaves differently from another.

### How the camera image reaches the network

- **YUV path (preferred).** When the CPU image is YUV 4:2:0 (`AndroidYuv420_888` on ARCore, `IosYpCbCr420_8BiPlanarFullRange` on ARKit), the planes are uploaded as they are at the camera's resolution: luma as an R8 texture, chroma as an RG8 texture (iOS) or as two R8 textures (Android, which may be interleaved or planar; row and pixel strides are handled). `StageSettARPreprocess` converts them to RGB (BT.601 full range) inside the same pass that turns, scales and crops the image, so there is no CPU colour conversion and the output is the same sRGB-encoded 0..1 colour as the RGBA path. The shader's box filter handles the downscale, so the image is not resized on the processor.
- **RGBA fallback.** Any other format, a GPU without R8/RG8 textures, unusual strides, or any error in the upload (logged once as a warning) switches to the asynchronous RGBA32 conversion. Its size is automatic: the image is scaled so its focal length matches the map's (`StarLocalizer` tells the source through `CameraSource.TargetFocal`): width = ceil(imageWidth × mapFocal × `advanced.inputScale` / fx), rounded up to a multiple of 8, never larger than the image. Set `image.maxWidth` above 0 to override; it is then a plain cap.
- `TextureCameraSource` (Editor, webcam) keeps delivering RGB textures.

To carry the extra planes, `CameraFrame` has a `format` (`Rgb` by default, `YuvSemiPlanar`, `YuvPlanar`), the chroma textures `chroma` / `chromaV` with `chromaPixelStride`, and the image `width` / `height` when they differ from the luma texture (which keeps its row padding). A custom `CameraSource` can ignore all of this and deliver an RGB texture.

## StarLocalizer settings

The Inspector shows only the basics at first. Everything else sits in collapsed groups with sensible defaults; most projects never open them.

**Basics (top level)**

| Setting | Meaning |
|---|---|
| `map`, `cameraSource` | The STAR map to find and where camera frames come from. |
| `mapRoot` | The object that gets moved into place. Defaults to the localizer's own object. |
| `autoStart` | Start looking when the scene starts. Off: call `StartLocalization()` yourself. |
| `hideUntilLocalized` | Children of the map root stay inactive until the first localization. |
| events | `onLocalizing`, `onFirstLocalized`, `onLocalized`, `onSettled`, `onLost`, `onFailed`, `onStateChanged`. |

**Phases.** Timing is three phases, each with an `enabled` toggle and its own settings. The localizer starts in Startup, moves to Early once the map is placed, and to Late once Early has seen enough of the room. The current one is `CurrentPhase` (`Startup`, `Early`, `Late`).

**Startup** (`startup`): finding the room.

| Setting | Meaning |
|---|---|
| `enabled` | On (default). Off: the first confident fix places the map, with no agreement needed. |
| `hz` | Frames analysed per second (4). |
| `fixesToShow` | How many confident fixes (`gating.minConfidence`) must agree within `gating.agreementMeters` / `gating.agreementDegrees` before the map is placed and shown (3, minimum 1). They are fitted together; there is no turn or coverage requirement. The same wait applies after a tracking reset. Fixes that do not agree keep waiting. |
| `failAfterSeconds` | If Startup does not finish within this time (25 s; with Startup off, the first placement) the state becomes `Failed` and `onFailed("Not in this room…")` fires. |

**Early** (`early`): refining right after the placement.

| Setting | Meaning |
|---|---|
| `enabled` | On (default). Off: go straight to Late after Startup. |
| `intervalSeconds` | Seconds between analysed frames (0.5). |
| `minFixes`, `minTurnDeg`, `minMoveMeters` | Early ends once at least `minFixes` (4) accepted fixes are spread over a head turn of `minTurnDeg` (30°) or camera travel of `minMoveMeters` (0.5 m), whichever comes first. Travel is the extent of the camera positions on the floor (x and z); height is ignored. Then `onSettled` fires and Late begins. |
| `gateMeters` / `gateDegrees` | Every accepted fix refines the joint fit. A fix that would move the map more than this (0.9 m / 18°, plus 3 sigma of the two fixes) is rejected; 3 rejects in a row that agree with each other re-seed the alignment from them, which also restarts Early's confirmation. |

**Late** (`late`): checking now and then.

| Setting | Meaning |
|---|---|
| `enabled` | On (default). Off: nothing is inferred once Early ends, and the map keeps its placement. |
| `useInterval`, `intervalSeconds` | A check every 30 s. |
| `useMove`, `moveMeters` | A check once the camera moved more than 2 m since the last check. |
| `useTurn`, `turnDegrees` | A check once the head turned more than 60° since the last check. Each trigger can be switched off on its own. |
| `hz`, `burstSeconds` | A check analyses frames at `hz` (4) for at most `burstSeconds` (3 s), and ends sooner once 3 fixes that agree with each other have been collected. Nothing is inferred between checks. |
| `refitMeters`, `refitDegrees` | The placement is only refitted when at least 3 fixes that agree with each other put the map farther than this (0.05 m / 2°, plus at most 1 sigma of their own noise) from it. Those fixes are fitted together and the map glides there; otherwise the placement is left alone. Each check starts with no fixes left over from an earlier one. |

**Timing** (`timing`)

| Setting | Meaning |
|---|---|
| `glideSeconds` | A changed alignment is eased in over this long (0.5 s, about 95 % of the way). The easing is exponential, so it does not depend on the frame rate and a new target mid-glide just changes the direction. The root is recomputed every `LateUpdate` as `TrackingSpace.localToWorld * MapToTracking`, so it follows locomotion. |
| `smoothMoves` | On (default): every map move glides. Off: every move is applied at once. The first placement and a move after tracking was reset always snap. |
| `snapAboveMeters`, `snapAboveDegrees` | A map move longer than this (1 m) or a turn larger than this (20°), compared with what is shown now, is applied at once instead of gliding, since a slide that large would look like the room drifting. 0 turns a limit off. The move is measured where the user is (the map content at the head position; the landmark centroid if the head position is unknown), so a small turn seen from far from the map origin is not mistaken for a large move. |

**Gating** (`gating`)

| Setting | Meaning |
|---|---|
| `minConfidence` | Minimum PnP confidence (0..1) for a fix to count (0.5). |
| `agreementMeters` / `agreementDegrees` | Two fixes agree if the map lands within this of each other (0.1 m / 3°). |
| `trackingJumpMeters` | If the tracked camera (`CameraSource.TryGetCurrentPose`) jumps farther than this (1.5 m) from one rendered frame to the next, tracking was reset (see Lost below). A frame that was being analysed is dropped only if it was captured before the reset (`CameraFrame.timestamp` is on `Time.realtimeSinceStartupAsDouble`). A source whose `TryGetCurrentPose` returns false is checked on the poses of the frames it delivers instead, and gets no Late checks from moving or turning, only from the interval. Walking never triggers it. |

**Advanced** (`advanced`): `backend` (`GPUCompute`), `layersPerFrame` (10: network layers run per rendered frame, so no frame pays for the whole network; 0 runs it all at once), `inputScale` (1: 1 feeds the network the map's trained focal length; lower values, down to 0.25, shrink the whole image for speed but find fewer landmarks), `pnp` (pose solver) and `alignment` (`maxFrames` 30, `maxFrameAgeSeconds` 60, `huberPx`, `inlierPx`: the window and robust loss of the fit). **Debug** (`debug`): `fusion`: `Joint` (default) combines recent fixes with headset tracking, rejects outliers and glides the map into place; `Per Frame` places the map exactly where each single frame says, with no agreement check, gate, refit or glide, and Early never ends. It jitters and can jump to a wrong fix; use it to compare against `Joint`.

States: `Idle`, `Initializing` (the Startup phase before the first placement), `Localized`, `Lost`, `Failed`. Other API: `CurrentPhase`, `SecondsUntilGiveUp`, `CurrentAlignment`, `LastConfidence`; control with `StartLocalization()`, `Relocalize()`, `StopLocalization()`. The map is always placed at scale 1; the localizer does not estimate a scale.

**Lost.** The localizer never gives up on the room because of time or missing fixes: failing to get fixes in Early or Late only means nothing is refined, and the headset's own tracking carries the map. It goes `Lost` (`onLost`) only when the device has no tracking, `CameraSource.IsTracking` is false, or when the tracked camera jumps (tracking was reset). While `Lost` nothing is inferred.
- When tracking comes back after it was lost with the headset on, or after a jump, the old placement cannot be trusted: the map is hidden (`hideUntilLocalized`) and the localizer goes back to Startup (state `Initializing`).
- If tracking was lost only because the headset was taken off or the app was paused or lost focus (`IsWorn` false), the placement is kept and the localizer carries on in the phase it was in (Early if Early had not finished, else Late) with state `Localized` and `onLocalized`.
  The jump baseline is forgotten then, so carrying the headset away is not a jump, and a check runs at once (in Early, the next frame is taken at once), so a changed space is caught by a refit.
- `ArFoundationCameraSource` reports tracking from `ARSession.state`: it counts as lost only after the session was seen tracking and then left that state, and a `notTrackingReason` of None, Initializing or Relocalizing counts as a brief drop, not a loss. This needs checking on Quest meta-openxr. It reports `IsWorn` false while the app is paused or unfocused (taking a Quest off moves the OpenXR session out of focus, so this needs no Meta SDK; on a phone it is the app going to the background) or while the head device's `CommonUsages.userPresence` is false. A platform without a presence signal counts as present. Other sources keep the default `true`; a custom `CameraSource` can override `IsTracking` and `IsWorn`.

How fixes are used: each frame gives a camera pose in the map; combined with where the headset says the camera was, that implies a map-to-tracking alignment (yaw + translation; gravity is shared). The current alignment is also passed to PnP as a prior, and an `AlignmentSolver` refits a window of recent frames, which averages noise down without trusting drifting tracking over long times.

## Editor testing

Add `TextureCameraSource` (webcam or a still image plus `ManualIntrinsics`) to an object, assign it as the localizer's `cameraSource`. The pose of that source is whatever you give it, so a still photo with a known pose is a good smoke test. Select the localizer to see landmarks as gizmos.

## Testing a map against a scan

A `.starscan` (star-web-demo's capture file, or the same files unpacked in a folder) holds camera images with ground-truth poses in the map's world, so a map can be tested without a headset.

**Tools ▸ StageSettAR ▸ Test Map Against Scan…** takes a `StarMap`, a scan, a backend, the PnP settings and a list of image scales (default 0.25, 0.5, 0.75, 1). For each scale it runs the detector and `StarPnp` on the held-out frames (`test.txt`, else every 10th frame; frames without a trustworthy pose are skipped) and reports recall within 5 cm / 5° and 10 cm / 10°, median error, mean detections and inliers, failed solves and milliseconds per frame, next to the holdout figures stored in the map. A failed solve counts as an infinite error. It is the same evaluation star-web-demo ran when it trained the map, but through the Unity detector, so it also checks the preprocessing and the scale sweep shows how far the image can shrink before accuracy drops. **Copy as CSV** puts the table on the clipboard.

To watch the real `StarLocalizer` on a scan in Play mode, use `StarScanCameraSource` instead of a webcam: it replays the scan's frames at a set rate with the ground-truth pose as the camera pose. Keep the map root at the identity relative to the tracking space; a correct localization then leaves it there, and `TryGetMapRootError` gives the offset.

## Coordinate frames

The export is the WebXR capture world: right-handed, y up, metres. The Unity map frame is that mirrored in z, `(x, y, -z)`; `StarMap.landmarksExport` keeps the landmarks verbatim in the export frame (as in `landmarks.json`), and PnP uses them directly with OpenCV cameras. Unity map-frame positions come only from `StarMap.LandmarkUnity(i)` / `LandmarksUnity`; the splat importer applies the same flip, so the splat sits at identity under the map root. All conversions live in `Conventions`. `topdownBounds` is a Unity-frame XZ rectangle.

## Quest caveat

The lens mount defaults are measured on one Quest 3 (see "Camera source settings"). If the map sits consistently off in the direction you look, check the mount first.

star-mobile maps are usually trained only at rotation 1 (portrait phone images). The Quest camera is landscape, so frames are rotated and padded to fit the network, which leaves fewer landmarks visible and lowers confidence. Prefer maps whose `rotations` include 0 (check the `StarMap` asset).

## Extending

Implement `CameraSource` (`IsReady`, optional `TrackingSpace`, `TryGetFrame`). It must deliver an upright texture, intrinsics for that texture's resolution, a strictly increasing `id`, and the sensor pose at capture time relative to `TrackingSpace` (y up). The rest of the pipeline is source-agnostic.

## Toolkit (no-code components)

Ready-made pieces for adding behaviour without writing code. They find the user's camera and head themselves, act on their own object unless you point them elsewhere, and are hooked together with the usual UnityEvent lists in the Inspector. Everything is under **GameObject > StageSettAR** (right-click in the Hierarchy works too) or **Component > StageSettAR**. New objects appear about 1.5 m in front of the Scene view camera, under the map's `Content` object if the scene has a map, or under whatever you have selected.

### Menu items

| Item | What you get |
|---|---|
| **Trigger Zone** | A 2 m box that fires **On Enter** / **On Exit** when the user's head walks in or out. Drawn in the Scene view; resize it with the Box Collider. |
| **Gaze Target** | A sphere that fires **On Enter** / **On Exit** when the user looks at it (after a quarter-second hold). Put it on or around the thing to look at, or add **On Gaze Action** to any object that has a collider. |
| **Info Panel** | A world-space text panel (TextMeshPro) that turns to face the user. Edit the `Text` child. |
| **Info Panel (Follows You)** | The same, but it drifts along in front of the user (Tag Along). |
| **Path Follower** | A closed route with a sphere that travels it. Select the `Path` object and drag the points in the Scene view; swap the sphere for your own model. |
| **Timed Sequence** | An Experience Sequence with two timed steps (see below). |
| **Video Panel...** | Opens Add Video. |

TextMeshPro needs its essentials once: if the panel text is blank, use **Window > TextMeshPro > Import TMP Essential Resources**.

### Videos

**Tools > StageSettAR > Add Video...** (also **GameObject > StageSettAR > Video Panel...**): choose a video file, or drop one on the window, or pick a video already in the project. The file is copied to `Assets/StageSettAR/Videos` and set to transcode to H.264 on Android (Quest), capped at 2560 px wide. Then a screen with the video's exact proportions is added to the scene, with spatial sound. Choose when it plays: **When Gazed At** (pauses on looking away), **When Nearby** (a play zone of the radius you pick), **On Start**, or **Manual** (call `Play` on the Video Player from an event). You can also right-click a video clip in the Project window and choose **StageSettAR > Create Video Panel**. The first Android build transcodes, which can take a while for long videos.

### The Player tag and trigger zones

The user's head is the `Head` object under the camera, tagged **Player**, with a small trigger sphere. **On Trigger Action** reacts only to objects with the tag in its **Trigger Tag Name** field (default `Player`; leave it empty to react to anything). It fires On Enter when the first matching collider arrives and On Exit when the last leaves. It also reports solid collisions, which replaces the old Collide Action (one of the two objects needs a Rigidbody). Scenes that do not use the StageSettAR rig need a camera tagged `MainCamera` and an object tagged `Player` that has a collider and a Rigidbody.

### Gaze

Gaze needs no setup: **On Gaze Action**, **Panel Look At** (turn to face you, always or only when looked at) and **Panel Dynamic Tilt** (lean toward where you look) all read the line from the `GazeSource` on the rig's Head, falling back to the main camera. A solid object in front blocks the gaze; triggers do not, and the Ignore Raycast layer never does. A sphere gaze (the default) forgives aim by `Gaze Radius` metres; set the shape to Ray for precise targets. **Fire Exit On Start** puts the scene in its not-yet-looked-at state when it loads, so the thing that On Enter reveals starts hidden.

### Sequences

**Experience Sequence** replaces chains of timers. List steps top to bottom; each step waits for something, then runs its **Then** event:

- **Nothing**: runs straight away.
- **Seconds**: waits the given time.
- **User In Zone**: waits until the user is inside the Trigger Zone you drag into **Target**.
- **User Looks At**: waits until the user looks at the Gaze Target you drag into **Target**.
- **Continue**: waits until some event calls the sequence's `Continue()`.

It starts by itself (**Play On Start**), or call `Play()` from another event such as the map's On Localized. **On Finished** fires after the last step, and **Loop** starts it over. **Timed Action** is the single "after N seconds, do this" version.

### Fades and other helpers

- **Camera Fade** (fade to a colour and back) and **Camera Circle Wipe** (iris in and out): `Cover()` / `Clear()` from an event, or **Start Covered** to open a scene from black. They are on the rig's `Screen Effects`; adding one elsewhere also works.
- **Passthrough Fade**: `FadeToVirtual()` takes passthrough to a solid colour, `FadeToPassthrough()` back. Quest only hides the boundary while passthrough shows, so it may reappear when fully virtual.
- **Object Fade**: fades an object and everything under it (renderers, UI, splat shells) with `FadeIn()` / `FadeOut()`, without touching materials.
- **Scene Switcher**: next / previous / by-name scene, restart or quit, with a Camera Fade first. Scenes must be in the build settings.
- **Tag Along**, **Do On Angle**, **Path Follower**, **Video To Material** are the pieces the menu items are built from; each can be added on its own.


## Panoramic shells and reveals

Needs UnitySplats (installed by the Setup window). Everything below is under `Add Component > StageSettAR > Splats`.

**Shells.** Far-away splats are the least detailed and most expensive part of a splat scene. Baking replaces everything beyond `Start Radius` with a few inside-out spheres ("shells") carrying a photo of that far field, taken from where the viewer will stand. With `Splat Shell Reprojection` each shell also stores a depth map, so the picture still shifts correctly when you move your head.

**How to bake.**
1. Put an empty object where the viewer's head will be and assign it as `Capture Origin` (otherwise the splat's own position is used; the inspector warns about this).
2. Select the splat and choose `Tools > StageSettAR > Bake Panoramic Shells`. This adds `Splat Shell Reprojection` with sensible defaults and bakes. Tweak the settings and press `Re-bake Panoramic Shells` on the component to try again; `Revert Bake` removes the shells.
3. Results are written to `Assets/StageSettAR/Shells/<splat name>/`. Stay out of play mode while baking; the scene is briefly rearranged and put back afterwards.

For depth the bake temporarily swaps in capture versions of UnitySplats' shaders. If your UnitySplats is not 2.0 the inspector says so and the bake produces colour-only shells (no reprojection).

**Reveals.** All of them find the splat on their own object, parents or children, and have `Play`, `Show`, `Hide`, `Stop` and an `onComplete` event.
- `Splat Reveal` grows the splat in from dots (or shrinks it away) by animating its downscale factor. Plays on start by default.
- `Splat Shell Reveal` fades the baked shells in or out, nearest first by default. Starts hidden.
- `Splat Events` raises `onUploaded` and `onFirstRender`, handy for starting a reveal only once the splat is really on screen.

Baking from the importer: tick **Bake panoramic shells** (needs the map added to the open scene). It runs the same bake as the menu, on the splat in your scene, so the shells belong to that scene object, not the prefab. Bake from where people will stand: see How to bake above.

**Splat LOD (Gradient).** `Splat LOD (Gradient)` thins splats out with distance from a centre (a curve, with options to keep large or opaque splats longest). It works on the CPU through UnitySplats' active ranges, so it replaces the renderer's ordinary cutouts while active, and recomputes when you change a setting or move the centre. `Tools > StageSettAR > Splat Counts` shows uploaded/visible splat counts for the selected splat; the same panel is in the LOD inspector.
