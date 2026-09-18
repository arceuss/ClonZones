# GH3 PC HUD implementation report

Branch `feature/gh3-hud`, continuing the working `feature/gh3-renderer` tree (tracked
modifications and untracked highway sources preserved; nothing reset).

## Runtime and deployment

Installed Clone Hero v1.1.0.6142-final, Unity 2022.3.62f2, MelonLoader 0.8.0-ci.2526,
generated `Il2CppCloneHero.dll` sha256 `7a8ab18c…` (matches the checkout's bindings).

Build: `dotnet build v11hotfix.sln -c Release` from `ClonZones`. The post-build copy now
targets `$(DeployDir)` (default `$(GamePath)/Mods`); `-p:DeployDir=<folder>` stages
without touching the installed mod. Unit tests: `dotnet run -c Release --project
tests/Gh3HudTests` (94 checks, plain .NET, Unity value types stubbed).

Assets: every HUD file is looked up per file in `<theme>/Hud/` first, then
`Mods/ClonZones/fallback/hud/`. The fallback folder holds the 44 decoded original PNGs
from the continuation pack (untouched: full canvas, transparent padding, straight alpha)
plus the three fonts as editable pairs `num_a9|num_a7|text_a6 .png + .font.txt`, exported
from the original `.fnt.xen` containers with `tools/gh3_font_export.py` (stdlib Python,
one-off preparation; the mod never runs Python). A theme overrides any single file by
placing it under its own `Hud/`.

Preferences (`MelonPreferences.cfg` `[ClonZonesHud]`): `Enabled` (default true),
`Diagnostics` (periodic snapshot log, default false).

## Files

- `Gh3HudTypes.cs`, `Gh3HudAssets.cs`: atlas region/bitmap types; loads the images and
  fonts once and packs them into one 2048² RGBA atlas with 2-texel edge-extended gutters
  (one material, one mesh, so GH3's z order can interleave textures and glyphs).
- `Gh3HudFont.cs`: editable font pair parser; native measure/emit rules.
- `Gh3HudLayout.cs`: `career_hud_2d_elements` (53 declarations) and builder defaults.
- `Gh3HudElement.cs`: ScreenElement channels (pos/alpha/scale/rot/rgba), shared ms timer,
  easing, replacement rules.
- `Gh3HudScheduler.cs`: bounded script scheduler (`Wait seconds/gameframe`, blocking `:DoMorph`,
  `spawnscriptnow`, `KillSpawnedScript name/id`).
- `Gh3HudScene.cs`: element tree, native compose, z-sorted sprite/text emission with shadow pass.
- `Gh3HudMesh.cs`: single MeshRenderer child of the player camera, authored 1280x720 →
  `min(W/1280,H/720)` centred fit → camera-local via the camera's real projection matrix.
- `Gh3HudRules.cs`: pure native presentation rules (dial, milestones, lamps, palette, needle, bands, score fit).
- `Gh3HudStateBridge.cs`: per-frame plain snapshot of CH state.
- `Gh3HudController.cs`, `Gh3HudScripts.cs`, `Gh3HudSpMeter.cs`: attach/lifecycle, native
  `UpdateScoreFastPerFrame` port, script ports, six-bulb meter.
- `Gh3HudVisibility.cs`: exact CH renderer leaves hidden with `forceRenderingOff`, restored on dispose.
- `Gh3HudDiagnostics.cs`: optional snapshot log. `Core.cs`: load/install/tick/clear hooks.
  `ClonZonesProfiler.cs`: `gh3HudUpdate` scope. `v11hotfix.csproj`: compile items, DeployDir.

Attachment mirrors the highway: `BeatRenderer.Start` postfix (single-player five-fret
guitar with an engine present), `BeatRenderer.OnDisable` prefix and every Core scene
callback dispose. Update runs in `OnLateUpdate` after the highway and sustain passes.

## Evidence

Classes: **[V]** instruction-level, **[D]** decompiled only, **[S]** script text,
**[I]** inferred, **[A]** deliberate adaptation, **[U]** unverified.

### Native 2D updater (gh3.exe 1.31 database `gh3.i64`, work copies only)

`UpdateScoreFastInit 0x421BC0`, `UpdateScoreFastPerFrame 0x42F6D0`, streak driver
`0x42FFFC–0x43036C`, `sub_422BC0`, milestone/digit block `0x422E8A–0x4230E6`,
`UpdateSPMeter 0x4230F0`, `UpdateScoreAndStreak 0x423F10`, `KillPulsateStarPowerBulbs 0x42C480`,
`RockMeterChanging 0x42F400`, `StarPowerOn 0x42EEF0`, `UpdateNixie 0x419780`.

- Score text: `%d`, no commas [V]; when longer than 5 characters `scale = (175/width, 1.0)` [D];
  which width the native reads (measured vs scaled) is [U]; the port uses the unscaled measure.
- Nixie: alpha-only selection over `{1,2,3,4,6,8}`, a-side without SP, b-side with SP [V].
  `UpdateNixie` only invalidates the driver's caches [V]; the activation flash is the script.
- Lamps: `v44 = s>30?10 : s>20?s-20 : s>10?s-10 : s`, `full = trunc(v44/2)`, half on odd,
  all off at `v44<=1` [V]. Palette on multiplier change: SP → blue, 1/2 base, 3 green,
  4 purple [V].
- Counter: shown at streak ≥ 25, hidden below [V]; flip dial `%1000→4, %100→3, %10≠0→1, else 2` [V];
  four drums always written, thousands = full quotient, icon alpha = (thousands==0) [V].
- Milestones: 50, then every 100 [V]. Star Power Ready is script-spawned at amount ≥ 50 [S].
- Rock meter: needle `(1-h)·(-84)+42` degrees [V constants], bands at 14 and 31.5 [V],
  layer alphas per band [D], red-flash script at ≤ -31.5 [D].
- Six bulbs (`FINDINGS2`): unit 16.666667 [V]; tube child scale `(0.8·small_bulb_scale, 3·f)` as
  multipliers of authored dims [V]; full-child alpha `f` [V]; `old_alpha` tag written on rising
  edges only [V]; timers armed at 0 ms [V]; instant set, no smoothing [D].
- `KillPulsateStarPowerBulbs` restore values come from frame temps [U]; the port restores the
  full child to its `old_alpha` tag and the tube child to its authored 1.0 [A].

### ScreenElement morphs (`FINDINGS`, `FINDINGS2`)

- Easing: `ease_in = t²`, `ease_out = 2t−t²`, `smooth = 3t²−2t³`, linear default [V];
  `gentle` has no mapping and leaves the timer mode unchanged [V]; omitted `time` = 0 ms = snap [V].
- Per-channel guards on `target == new` [V]; `relative` pos adds to the pending target [V];
  one timer per element, re-armed by every morph [V]; ms timer domain [V].
- Compose: child pos scaled then rotated by the parent, alpha and scale multiply, rotation adds,
  `+rot_angle` clockwise on the y-down screen [V]. `just` in −1..1 space [D]; anchor at
  `((just+1)/2)·dims`, shared scale/rotation pivot [I, consistent with the compose's
  `(p+1)·0.5` form]. Final alpha = rgba.a × world alpha with a 1e-4 cutoff [D].
- z: float `z_priority` feeds a depth-buffered backend [V]; direction and tie order [U].
  The port draws ascending z with construction order for ties; the visible result matches the
  artwork's intent (body over nixie, icon over digits, text over flash).
- **Deviation [A]**: the recovered shared setup leaves an unnamed channel's start stale, and
  the tick lerps every channel, which re-fades the streak text's alpha on every pulse. The
  obfuscated `vtable+44` wrapper preceding the shared setup was not recoverable; the port
  snapshots every start at morph setup, which removes the per-pulse dip. A real GH3 capture
  is needed to settle which is original.
- Clock [A]: morph and Wait time is unscaled wall time, frozen while `isPaused`.

### Fonts (`font/FINDINGS`)

Container format from loader `sub_5F3510` cross-checked against the file bytes [V];
glyph rects equal the atlas ink bounds [V]. Measure `sum(advance + spacing)` including the
trailing spacing, space via `spaceWidth`, height = lineheight [D]; emitter quad top
`(lineheight − (h − yoff) − yorigin)·sy + 1.25` [D]; shadow = second pass at
`pos + shadow_offs` (unscaled) with `shadow_rgba·alpha` [D]. `SetFontNonProportionalNumbers`
is only applied to the debug font (`guitar.q:939`) so `num_a7 '1'` keeps its 10 px advance [S].
Text positions truncate to integer pixels in authored space [A: native truncates in
backbuffer space].

### Clone Hero bindings (`ch/FINDINGS`, GameAssembly.dll IDA database)

Engine (`BasePlayer.engine`): combo `field_Public_Int32_1` (0x88), effective multiplier
`field_Protected_Int32_0` (0x48, doubled under SP), SP active `field_Public_Boolean_0`
(0x42), health `field_Protected_Single_0` (0x70, 0..1, no-fail runs negative), total score
`prop_Int32_2`, SP 0..1 `prop_Single_0`, SP raw/max/threshold `field_Public_Int64_0/2/3`
(observed 14400 max, 7200 ready) [V + live]. Readiness is derived as
`!active && raw >= threshold` because `prop_Boolean_1` stayed false at sp=0.567 in a live run.
Practice/seek: song-time regression or a score drop resets the presentation epoch [A].

## Verification

- Compiled: Release build, zero warnings/errors.
- Unit tested (94 checks): easing tables, snap/gentle/relative/guard semantics, scheduler
  waits and kills, all `Gh3HudRules` tables, font measure against the recovered widths
  (`'0'=28`, `'1000000'=196`, `'50 Note Streak!'=361`, `'Star Power Ready'=393`), compose
  (0.7 parent scale, clockwise rotation, alpha chain, z ties), text justification/shadow, destroy.
- In-game (CLI bot preview `--song … --player Guitar,Expert --profile kae`, and two human
  runs by the user): attach, first-frame hide of 50 CH leaves, entrance, score 0→6 digits with
  the 175 px fit, lamps/palettes, counter show at 25, flips, drum carries to 1237 with the icon
  off, "300/1200 Note Streak!" with glowburst, SP ready (blue tubes, pulse, lightning, text),
  activation to x8/blue lamps/8b nixie, drain, needle bands, song end → `EndOfSong` scene with
  clean dispose/restore, no errors in the MelonLoader log.
- Profiled (dense chart, ~1000 FPS): `gh3HudUpdate` 0.15–0.23 ms/frame avg, max ≈1 ms,
  0–2.5 bytes/call (event-time allocations amortised); highway 0.04, sustains 0.01–0.12.
- Captures: `C:/Users/arceus/AppData/Local/Temp/clonzones-hud/{run2,run4,burst/f1,endofsong}.png`.

Not done: no original GH3 capture comparison (GH3 PC is not runnable here), so animation
parity is source/native-derived, not measured; no 720p/1440p/non-16:9 runs (viewport policy
is the neutral centred fit, GH3's own non-16:9 policy [U]); sound cues not implemented (no
cue assets); pause was exercised by the user only; practice seek not exercised in-game.

## Known differences and open items

- Blend/colour: Sprites/Default straight alpha in Gamma space; GH3's material blend for the
  glowburst/lightning art [U].
- z tie order and the exact sprite corner emission are inferred (see above).
- The stale-start replay deviation and the wall-clock choice are adaptations.
- `hud_move_note_scorebar` chains an async ease_in with a blocking relative morph exactly as
  scripted; the visible rebound is therefore the 0.1 s morphs, not the 0.167 s one.
- Leaderboard mode: the sweep found no extra HUD component instances in the runs so far; the
  user's leaderboard run reported the remaining widgets gone.
- A HUD position reset re-creates CH's combo leaves; ownership is re-asserted once a second.
- Sustain SP classification (pre-existing `Gh3SustainPatch`) keys on the profile's SP tint;
  under an all-white colour profile every sustain reads as SP. Not a HUD change; needs a
  sustain-root → note-flag association to fix.

## Stability follow-up (commit after 03dac12)

Root causes fixed, from the prepared audit and confirmed in source:

- **Animation clock gated on the script queue** (`_sched.Tick` only when `Count > 0`): async
  morphs launched by an already-finished script (counter exit, digit flip return, entrance's
  final return, flash fade) froze. The clock now advances every unpaused HUD frame
  (`AdvanceClock`), poses are sampled before scripts resume, and `Morph` samples the running
  morph at the new timestamp before snapshotting starts.
- **Cosmetic invalidation manufactured note events**: `UpdateNixie` cleared the streak cache,
  so the SP flash could re-fire a 50/100 milestone and a flip. The multiplier art is now
  re-evaluated on a streak change or invalidation (native gate kept); counter, lamps,
  milestones and digits need a real streak change.
- **Epoch reset left old timers**: restart/seek/engine swap now cancels scripts (disposing
  iterators so `finally` cleanup runs), rebuilds the element tree, and hydrates the current
  CH state without synthesising hits or announcements.
- **Ready-notification lock**: readiness is rechecked after waiting behind a streak banner and
  the flag is released in `finally` (early exit, kill, reset).
- **CH leaf suppression**: cached `forceRenderingOff` re-asserted every frame (paused too),
  dynamic font/multiplier arrays checked directly, rediscovery on right-click and a 1 s
  fallback; extra ComboColor/SPBar/HealthContainer/StarProgress instances swept once.
- **Projection-only camera changes** rebuild the mesh (field compare of the matrix).

Tests: `tests/Gh3HudRuntimeTests` (real controller/scripts/scheduler/scene with a
synthetic host): 0/16 on the audited base export, 18/18 on the candidate (two added:
Nixie switches only after the flash's `UpdateNixie`; a milestone re-fires on a new
streak). Pure suite 94/94. Release build clean.

In-game (this build): bot entrance settles at the authored positions; song end teardown
clean. Human acceptance (miss exit, restarts, right-click reset, notification contention)
was run by the user; see the session outcome.
