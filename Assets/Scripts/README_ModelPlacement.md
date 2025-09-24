Meta XR Model Placement - Setup & Usage

Overview

This folder contains four main scripts for a simple model-selection + placement MVP targeting Meta Quest 3 using the Meta XR All-in-One SDK (Interaction SDK + MRUK):

- `PanelUIBuilder.cs` - builds a small world-space panel attached to the left controller showing 5 model options.
- `ModelSelector.cs` - handles left-hand joystick and X/Y confirm input; updates the selected index and calls the `PlacementManager` to update the preview.
- `PlacementManager.cs` - manages the right-hand raycasting (MRUK preferred; Physics.Raycast fallback), preview instantiation (semi-transparent), and final placement on trigger.
- `RayVisualizer.cs` - simple LineRenderer-based visualizer that exposes `GetRay()` for `PlacementManager`.

Required Unity / Packages

- Unity: 2022.3.59f1 (targeted). The Meta XR All-in-One package minimum documented support is 2022.3.15f1; test for minor 2022.3.x differences.
- Meta XR All-in-One (UPM) - install via Unity Package Manager. Ensure at least the following packages are installed from the All-in-One package:
  - Interaction SDK
  - Mixed Reality Utility Kit (MRUK)
  - XR Runtime / Core (Meta XR Core)

To install via Package Manager (UI):
1. Window -> Package Manager
2. Click + -> Add package by name or git URL (follow Meta docs) or search in scoped registries if configured.

Notes:
- If you cannot reference some Meta-specific types in the scripts above, replace with the Interaction SDK equivalents. The code includes TODO comments where SDK-specific input hooks or MRUK calls should be inserted.
- In Editor, enable the Meta XR Simulator (part of the Interaction SDK or as separate package) to test controller input and ray interactions without a device.

Scene Setup (quick)

1. Create two empty GameObjects in your scene: `LeftControllerAnchor` and `RightControllerAnchor`. These can be provided by the XR Rig or Simulator prefabs from the Meta XR package.
2. Attach `PanelUIBuilder` and `ModelSelector` to `LeftControllerAnchor` (or a manager object). Assign your 5 model prefabs in the inspector (`modelPrefabs`).
3. Attach `PlacementManager` and `RayVisualizer` to `RightControllerAnchor`. Assign `rightController` to the `RightControllerAnchor` transform in `PlacementManager`.
4. Hook `PanelUIBuilder` reference into `ModelSelector`, and `PlacementManager` reference into `ModelSelector`.
5. Optionally, set `RayVisualizer` on `PlacementManager` so the manager uses the ray provided.

Editor Debug / Simulator

- `editorDebug` booleans are available on `ModelSelector` and `PlacementManager` to enable keyboard fallbacks for testing in Editor.
  - `ModelSelector`: W/A/S/D or arrow keys to move selection, X/Y or Return to confirm.
  - `PlacementManager`: Space bar to place the preview into the scene.
- To use the XR Simulator (recommended): install the Meta XR Simulator package and run the scene; bind the left/right controllers to the anchors and the script will use pose/forward vectors from those anchors.

MRUK & Quest 3 Notes

- When using MRUK or the MR Scene API on Quest 3, ensure your project has the necessary permissions (Passthrough, Scene Understanding) and that the MRUK runtime is initialized.
- The `PlacementManager` contains commented placeholders where MRUK "TryGetSceneHit" style calls should be used to obtain more accurate spatial hits. If MRUK types are available, replace the Physics.Raycast logic with MRUK calls and use Physics as fallback.

MRUK compilation flag and integration

- If you have MRUK installed and want the project to use MRUK's scene/space hit APIs, define a scripting symbol `MRUK_PRESENT` so the MRUK-specific code blocks compile.
  - Edit `Project Settings -> Player -> Other Settings -> Scripting Define Symbols` and add `MRUK_PRESENT` for your build target (e.g., Debug, Release) or define it per platform.
- After defining `MRUK_PRESENT`, open `Assets/Scripts/PlacementManager.cs` and replace the MRUK placeholder block with the concrete MRUK API available in your package. Example pseudocode to replace:

  // Example MRUK pseudocode:
  // MRUKHit mrukHit;
  // if (MRUK.Scene.TryGetSceneHit(ray, out mrukHit)) {
  //   hit = true;
  //   hitPosition = mrukHit.point;
  //   hitNormal = mrukHit.normal;
  // }

- The actual namespace/type names vary by MRUK version. Use the MRUK documentation or the package's provided samples to find the correct API.

Testing on Quest 3

1. Build Settings -> Android -> Set appropriate Player Settings for Oculus/Quest (XR Plug-in Management or Meta XR Core setup recommended).
2. Add required manifest entries and permissions for MRUK (if used).
3. Build and run on device. Verify left-hand panel selection updates preview on the right-hand ray and placement with the trigger.

Edge cases and suggestions

- Ensure your prefabs contain appropriate colliders for placement and interaction. The PlacementManager will add a default BoxCollider only if none exist.
- For complex models, create simplified preview meshes or LODs to reduce shader/material complexity in preview.
- Consider using the Interaction SDK's Ray Interactor component directly (wire up selection/hover callbacks) to get proper UI interaction and pointer events.

If you want, I can now adapt the input code to the Meta XR Interaction SDK specific APIs (e.g., XRController, XR Interaction Toolkit compatibility layers) or add an example XR Rig prefab wiring for the Meta XR Simulator.
