# GH3 renderer implementation report

## Runtime and deployment

Implemented for the installed Clone Hero v1.1.0.6142 build, Unity 2022.3.62f2, using its generated MelonLoader IL2CPP bindings. The project remains .NET 6 and uses the existing configured `GamePath` and post-build copy target.

Build: `dotnet build v11hotfix.sln -c Release` from `ClonZones`.

Output: `bin/Release/net6.0/ClonZones.dll`, copied to `C:/Games/CloneHero/Mods/ClonZones.dll`.

Assets remain under the folder selected by `Mods/ClonZones/current.txt`. Testing used the already-installed Rainbow Zones (Restored) assets. No replacement asset pack was installed. Restart the game to reload asset-bank changes. The user's HUD placement was left unchanged.

## Files and ownership

- `HighwaySpriteBank.cs`: loads the three fretbar weights, lane string, and sidebar textures once. Missing individual assets warn and retain the corresponding native layer.
- `Gh3HighwayLayout.cs`: static row table, row inversion, lane positions, widths, and localized fade.
- `Gh3HighwayBridge.cs`: maps GH3 virtual coordinates onto the actual CH camera and field, anchored to CH's fret positions and visible-note limits.
- `Gh3BeatTimeline.cs`: preserves native bar weights and pulse events; synthesizes small eighth bars only at tempos at or below 180 BPM, anchored to measure ticks.
- `Gh3HighwayMesh.cs`: reusable vertex/index buffers, localized fade clipping, mirrored strips, and body/glow quads.
- `Gh3HighwayRenderer.cs`: owns field objects, native beatline substitutions, strings, sidebars, and synthetic eighths.
- `Gh3HighwayBacking.cs`: projects the existing selected neck art while preserving its material/property block and scrolling ownership.
- `Gh3NoteProjection.cs`: projects existing pooled heads. It does not create duplicate notes or change hit timing.
- `Gh3SustainBank.cs`, `Gh3SustainPatch.cs`, `Gh3WhammyHistory.cs`: authored sustain bodies, generated glow textures, pooled ribbon meshes, and CH whammy-input history sampling.
- `Gh3HighwayDiagnostics.cs`: optional field inventory behind the existing disabled-by-default diagnostic switch.
- `Core.cs`: setup, late-frame updates, and scene teardown. `GuitarNoteHeadPatch.cs` supplies hit timestamps through the existing native state hooks. `NoteHeadSpriteBank.cs` supplies projection metrics and corrected sprite classification/pivots.
- `ClonZonesProfiler.cs`: adds highway, note-projection, and sustain update timings plus managed bytes/call. `v11hotfix.csproj` explicitly includes the added sources.

Runtime attachment uses `BeatRenderer.Start` and `GuitarNoteRenderer.Start`; `BeatRenderer.OnDisable` and Core scene callbacks release field ownership. The existing native note SetupState/ApplyNewState bridge remains responsible for state substitution. Component references and native pools are cached at attachment, not rediscovered per frame.

## Sources and constants

Primary layout references: `NotClon/src/renderer.cpp`, `NotClon/src/gh3_tables.h`, and `gh3stuff/neversoft-script-library/gh3/pc/base_1.31/scripts/guitar/`.

The virtual layout is 1280x720: center X 640, playline Y 655, top Y 305, height 350, widths 160 to 512, and a 30-pixel top fade band. The row table has 1152 intervals; row 1024 reaches the playline. Perspective factors are 1.000886 and 1.001541. Fretbar scale runs from 0.15 to 0.48. Sidebar anchors remain `(336,742.5)` and `(944,742.5)`, with directions `(176,-350)` and `(-176,-350)`, and authored X scale 0.3. The first native beat pulse is white; the second is cyan-tinted, red channel 192. Synthetic eighths do not advance the pulse.

Installed CH timing takes precedence over older CH source. Its visible time window is `(noteZPosFarLimit-strikeLine)/noteSpeed`; the measured speed-7 window was approximately 1.1243 seconds. No GH3 fixed 1.5-second window was imposed.

### Note travel and SP taps

CH flattens pooled note-container Z scale to zero. A head cannot be placed on the GH3 camera plane through that singular parent transform. Projection now temporarily restores a nonzero parent Z scale and restores the original value when releasing the slot. In the timing capture, 15 projected samples had maximum virtual-row error below 0.00014 pixels. This fixes the earlier double-projection/compressed-travel behavior without changing CH's clock.

Gem seating follows NotClon's requested justifications 0.68/0.35, giving normalized pivots 0.16/0.325. Activated-SP taps were accidentally classified as phrase stars by an unqualified `tap_starpower` substring, receiving the 1.3 scale. The lane-specific match now requires `/tap_starpower`; active phrase keys retain their explicit classification. Live loaded metrics: normal and activated-SP taps 128x64, phrase taps 166.4x83.2.

### Rainbow sidebar correction

The original Rainbow package's 400x512 sidebar DDS decodes identically to the installed PNG. Both TEX dimension fields specify 400x512. Its global material scene is byte-identical to stock, so neither a material change nor a leftover 32-pixel logical width explains the discrepancy.

GH3's texture call at `0x5F8A57` uses `D3DXCreateTextureFromFileInMemoryEx`, default dimensions, and `D3DX_FILTER_NONE`. An isolated call through the matching `d3dx9_35.dll` produced a 512x512 texture with transparent right padding. The sprite keeps its original logical dimensions. ClonZones now emits only the source-covered part of that logical quad, including mirrored coverage, instead of allocating a padded texture. It does not move the anchor or invent a new angle/width constant.

In an unbridged 1280x720 comparison, the corrected left-edge runs matched the supplied GH3 image at five sampled rows. The live CH bridge still uses CH's actual camera/field extents; it is not a promise of identical absolute pixels to a separate GH3 viewport. The user accepted the corrected sidebar appearance.

### Rainbow sustain glow correction

Native reference: the supplied GH3 database, whose recorded input executable MD5 is `e12fe1bd71ef5877d5662a43f30bbf2a`. The database records `F:/Games/Guitar Hero III/gh3.exe` as its input path. Its stored SHA-256 was unavailable.

Shader reference: `gh3stuff/DATA/FXFILES/MaterialLibrary.bin.xen`, SHA-256 `b4828db406cbc16c18409d380cce40c47dcbd148937a0b9a4dcb3767564d7e90`. Extracted glow VS685 at file offset `0x30B300` and PS686 at `0x30B9F8` were disassembled with D3DX9_35. The Nvidia/ATI glow programs and SM2.0 equation agree.

The native glow uses a fixed RGB sample at UV `(0.9,0.9)`, a smoothstepped cross-strip profile, body alpha, tip V, squared vertex/fade alpha, and alpha scale 0.85. Native instructions confirm source-alpha/one blending, slope -0.3, width multiplier 3.5, and tip stretch 1.4. The installed Unity additive shader was independently extracted from `globalgamemanagers.assets`, shader path ID 5, and its D3D11 programs disassembled. Its factor of two cancels the existing RGBA 0.5 tint. Installed settings are Gamma, with soft particles and fog disabled. Increasing that tint would not correct the source discrepancy.

The actual defect was CPU texture sampling: `GetPixelBilinear` did not use the GPU's half-texel convention. Rainbow's fixed sample lies beside transparent texels. The old bake produced red-lane RGB approximately `(0.200,0.030,0.031)` instead of the GPU sample `(0.700,0.110,0.113)`. The bake now bilinearly samples the already-read pixel array at `u*width-0.5`, with the corresponding flipped V coordinate. Live generated RGB became `(0.702,0.110,0.114)`, within 8-bit rounding. Glow alpha, blending, width, and whammy behavior were not boosted.

`Skate5` was inspected as engine-family context. No leaked implementation was copied; the correction is derived from the matching GH3 bytecode and measured Unity behavior.

## Verification

- Release builds succeeded with zero project warnings/errors and copied the DLL to the configured installation.
- Actual gameplay exercised dense `F:/vsm` notes and held sustains in `F:/3999ascent`, keeping Rainbow selected. Corrected glow was visually inspected in a 1280x720 game capture.
- A temporary executable compiled the real layout/timeline source and passed eight checks: width endpoints, fade bounds, all 1153 row round-trips, 180/181 BPM boundary, eighth de-duplication/weight, exact pulse boundaries, a tempo transition, and visible-pool sizing. The temporary project was removed. Its .NET 6 SDK end-of-support warnings were separate from the clean mod build.
- A temporary in-game scene probe performed two Gameplay reloads, left for Main Menu, and re-entered Gameplay. Custom GameObject counts were `22,22,22,0,22`; the successful run logged PASS without renderer errors. This exercised real SceneManager transitions, not a simulated disposal call. It was not a manual pause-menu restart test. The probe was removed afterward. An earlier probe attempt used a stripped generic Unity inventory overload; the successful probe used the native non-generic overload.
- On the Ascent sustain workload, the last eight captured steady-state profiling windows reported highway-update averages of 0.0332-0.0394 ms, nested note projection 0.0112-0.0151 ms, and sustain updates 0.3451-0.4200 ms. All three reported zero managed bytes/call in those windows. Initial attachment/JIT windows had allocations and larger maxima. These counters do not measure native heap allocations or GPU time. Profiling was restored to disabled.

## Limits and deliberate differences

Support was verified on this single-player installed build. The field controller explicitly retains the native multiplayer field rather than claiming an unverified multiplayer layout. Other builds, multiplayer sustain behavior, and alternate graphics backends were not validated.

CH retains its note timing, selected backing, gameplay state, and whammy input. The bridge fits its actual camera and playline rather than forcing an absolute GH3 screen rectangle. Fades remain localized, but the mesh/vertex fade and finite-resolution baked glow are approximations of GH3's per-pixel shader behavior. The baked bottom-row V factor differs from the continuous endpoint by about 1.6% for a 32-pixel sheet. The native glow's separate Y gate is not reproduced as a custom shader. Missing-asset fallback branches were inspected but a separate clean/missing-assets installation was not launched. No claim is made that every theme or speed setting was exhaustively tested.

## Evidence locations

Local evidence directory: `C:/Users/arceus/AppData/Local/Temp/clonzones-analysis-xnu56rxr/`.

- `timing-fixed.log`, `timing-fixed.png`: corrected note travel.
- `gem-seating-fixed.log`, `gem-seating-fixed.png`: seating and projection.
- `rainbow-d3dx9-35-upload.png`: native DDS padding experiment.
- `rainbow-sidebar-fixed.png`, `rainbow-sidebar-fixed.log`: sidebar correction.
- `ascent-rainbow-before.png`, `ascent-rainbow-before.log`: weak-glow baseline.
- `ascent-rainbow-glow-fixed.png`, `ascent-rainbow-glow-fixed.log`: corrected live glow and SP-tap metrics.
- `ascent-profile.log`: measured renderer timings/allocations.
- `scene-reload-smoke.log`: successful scene transitions and object counts.
- `ch-additive-3518.asm.txt`: installed Unity additive pixel shader.

Extracted GH3 shader programs and disassembly: `C:/Users/arceus/AppData/Local/Temp/gh3glow_a9o41j4q/`.

Final deployed-build smoke: `ascent-rainbow-final.png` and `ascent-rainbow-final.log` in the evidence directory. Rainbow selection and held-sustain rendering were confirmed, with zero `[ERROR]` entries, no temporary scene-probe output, and profiling disabled. Built and deployed DLL SHA-256 both equal `9a5f5b62b660b1ab7d28e6974e4436adb6de08af45467c8f87cda1a17232fff7`.
