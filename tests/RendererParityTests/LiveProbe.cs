using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Runtime.InteropServices;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(RendererParityProbe), "ClonZones parity probe", "1.0.0", "arceus")]
[assembly: MelonGame("srylain Inc.", "Clone Hero")]

// Diagnostic mod only. Never install this in a performance trial.
public sealed class RendererParityProbe : MelonMod
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticFields = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    // Fixed-width outputs from staging/sustain-shuffle/five-color-golden.json; this probe does not reimplement the hash.
    private static readonly int[] GoldenSeeds = { 0, 1, -1, int.MaxValue, int.MinValue, 0x12345678 };
    private static readonly int[][] GoldenColors =
    {
        new[] { 4, 1, 2, 3, 1 },
        new[] { 2, 3, 4, 2, 3 },
        new[] { 5, 3, 5, 1, 2 },
        new[] { 5, 3, 2, 5, 3 },
        new[] { 4, 3, 1, 4, 2 },
        new[] { 4, 3, 5, 3, 3 }
    };
    private bool _ran;
    private int _comparisons, _frames, _sceneFrames;
    private long _repeatAt;
    public override void OnSceneWasInitialized(int index, string name)
    {
        if (_ran || name != "Gameplay") return;
        _ran = true;
        RunChecks("cold");
        _repeatAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 30L;
    }

    public override void OnUpdate()
    {
        if (_repeatAt == 0 || Stopwatch.GetTimestamp() < _repeatAt) return;
        _repeatAt = 0;
        RunChecks("warm");
    }

    private void RunChecks(string stage)
    {
        _comparisons = 0; _frames = 0; _sceneFrames = 0;
        string output = Environment.GetEnvironmentVariable("CLONZONES_PARITY_OUTPUT") + "." + stage + ".json";
        try
        {
            Assembly baseline = Assembly.LoadFrom(Environment.GetEnvironmentVariable("CLONZONES_PARITY_BASELINE"));
            Assembly candidate = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "ClonZones");
            CheckHud(baseline, candidate);
            CheckBounds(baseline, candidate, "Gh3HudMesh");
            CheckBounds(baseline, candidate, "Gh3HighwayMesh");
            CheckWorld(baseline, candidate);
            CheckHighway(baseline, candidate);
            CheckHighwayLength(candidate);
            CheckSidebarAnchor(candidate);
            CheckScene(baseline, candidate);
            if (stage == "warm")
            {
                CheckSustainRecordMask(candidate);
                CheckHeadColor(candidate);
            }
            File.WriteAllText(output, JsonSerializer.Serialize(new { Passed = true, Stage = stage, Comparisons = _comparisons, Frames = _frames, SceneFrames = _sceneFrames }));
            LoggerInstance.Msg($"PARITY PASS {stage}: {_comparisons} scalar bit comparisons, {_frames} HUD frames, {_sceneFrames} scene frames.");
        }
        catch (Exception ex)
        {
            File.WriteAllText(output, JsonSerializer.Serialize(new { Passed = false, Error = ex.ToString(), Comparisons = _comparisons }));
            LoggerInstance.Error("PARITY FAILED: " + ex);
        }
    }

    private static object Field(object o, string name)
    {
        if (o == null) throw new NullReferenceException($"Cannot read {name} from null.");
        Type type = o.GetType();
        FieldInfo field = type.GetField(name, Fields);
        if (field != null) return field.GetValue(o);
        PropertyInfo property = type.GetProperty(name, Fields);
        if (property != null) return property.GetValue(o);
        throw new MissingMemberException(type.FullName, name);
    }
    private static void Set(object o, string name, object value)
    {
        if (o == null) throw new NullReferenceException($"Cannot write {name} on null.");
        Type type = o.GetType();
        FieldInfo field = type.GetField(name, Fields);
        if (field != null) { field.SetValue(o, value); return; }
        PropertyInfo property = type.GetProperty(name, Fields);
        if (property != null) { property.SetValue(o, value); return; }
        throw new MissingMemberException(type.FullName, name);
    }
    private static object StaticField(Type type, string name)
    {
        FieldInfo field = type.GetField(name, StaticFields);
        if (field != null) return field.GetValue(null);
        PropertyInfo property = type.GetProperty(name, StaticFields);
        if (property != null) return property.GetValue(null);
        throw new MissingMemberException(type.FullName, name);
    }
    private static void StaticSet(Type type, string name, object value)
    {
        FieldInfo field = type.GetField(name, StaticFields);
        if (field != null) { field.SetValue(null, value); return; }
        PropertyInfo property = type.GetProperty(name, StaticFields);
        if (property != null) { property.SetValue(null, value); return; }
        throw new MissingMemberException(type.FullName, name);
    }
    private static object Call(object o, string name, params object[] args)
    {
        MethodInfo method = o.GetType().GetMethod(name, Fields);
        int supplied = args.Length, count = method.GetParameters().Length;
        if (supplied < count)
        {
            Array.Resize(ref args, count);
            Array.Fill(args, Type.Missing, supplied, count - supplied);
        }
        return method.Invoke(o, args);
    }
    private void Equal(int expected, int actual, string label)
    {
        _comparisons++;
        if (expected != actual) throw new InvalidOperationException($"{label}: {expected:X8} != {actual:X8}");
    }
    private void Equal(float expected, float actual, string label) => Equal(BitConverter.SingleToInt32Bits(expected), BitConverter.SingleToInt32Bits(actual), label);
    private void Equal(Vector3 a, Vector3 b, string label)
    {
        Equal(a.x,b.x,label+".x"); Equal(a.y,b.y,label+".y"); Equal(a.z,b.z,label+".z");
    }
    private static Type MeshType(Assembly a, string name) => a.GetType("ClonZones." + name, true);
    private static object NewHud(Assembly a, Camera camera, bool additive)
    {
        object mesh = Activator.CreateInstance(MeshType(a,"Gh3HudMesh"), Fields, null, new object[] {
            "parity-only", Texture2D.whiteTexture, Shader.Find("Sprites/Default"), camera,
            0, 0, 0, 3000, 128, additive ? Shader.Find("Legacy Shaders/Particles/Additive") : null
        }, null);
        ((GameObject)Field(mesh,"_object")).SetActive(false);
        return mesh;
    }

    private static IntPtr Pointer(object o)
    {
        object value = Field(o, "Pointer");
        if (value is IntPtr pointer) return pointer;
        throw new InvalidOperationException($"Native owner pointer has unexpected type {value?.GetType().FullName ?? "<null>"}.");
    }

    private static object LiveSustainNotes(Type patchType)
    {
        object players = StaticField(patchType, "Players");
        if (players == null)
            throw new InvalidOperationException("[sustain-shuffle] native setup blocker: Gh3SustainPatch.Players is null; enter Gameplay after GuitarNoteRenderer.Start.");
        PropertyInfo valuesProperty = players.GetType().GetProperty("Values", Fields);
        System.Collections.IEnumerable values = valuesProperty?.GetValue(players) as System.Collections.IEnumerable;
        if (values == null && players is System.Collections.IDictionary dictionary)
            values = dictionary.Values;
        if (values == null)
            throw new InvalidOperationException($"[sustain-shuffle] native setup blocker: cannot enumerate {players.GetType().FullName}.Values.");
        foreach (object player in values)
        {
            if (player == null || (bool)Field(player, "_disposed")) continue;
            object notes = Field(player, "_notes");
            if (notes == null || Pointer(notes) == IntPtr.Zero) continue;
            return notes;
        }
        throw new InvalidOperationException("[sustain-shuffle] native setup blocker: no live Gh3SustainPatch owner. Run the probe in Gameplay after GuitarNoteRenderer.Start; do not replace this with a synthetic owner.");
    }

    private static object NewSustainFixture(Type patchType, object notes, out Array closed, out Array open)
    {
        Type slotType = patchType.GetNestedType("Slot", BindingFlags.NonPublic);
        if (slotType == null)
            throw new MissingMemberException(patchType.FullName, "Slot");
        closed = Array.CreateInstance(slotType, 5);
        open = Array.CreateInstance(slotType, 1);
        for (int i = 0; i < closed.Length; i++) closed.SetValue(Activator.CreateInstance(slotType, true), i);
        open.SetValue(Activator.CreateInstance(slotType, true), 0);
        object patch = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(patchType);
        Set(patch, "_notes", notes);
        Set(patch, "_closed", closed);
        Set(patch, "_open", open);
        Set(patch, "_identitySamples", 6);
        return patch;
    }


    private static void AssertIdentity(object slot, int expectedLane, int expectedVariant, string label)
    {
        if (!(bool)Field(slot, "HasIdentity"))
            throw new InvalidOperationException($"{label}: RecordMask did not mark the slot.");
        int lane = (int)Field(slot, "Lane");
        int variant = (int)Field(slot, "Variant");
        if (lane != expectedLane || variant != expectedVariant)
            throw new InvalidOperationException($"{label}: lane/variant {lane}/{variant}, expected {expectedLane}/{expectedVariant}.");
    }

    private static void InvokeRecord(MethodInfo method, object patch, IntPtr note, ushort mask, bool dead, bool starPower,
        int expectedClosed, int expectedOpen, string label)
    {
        object[] args = { note, mask, dead, starPower, 0, 0 };
        try { method.Invoke(patch, args); }
        catch (TargetInvocationException error)
        {
            Exception cause = error.InnerException ?? error;
            throw new InvalidOperationException($"{label}: RecordMask threw {cause.Message}", cause);
        }
        if ((int)args[4] != expectedClosed || (int)args[5] != expectedOpen)
            throw new InvalidOperationException($"{label}: counts {(int)args[4]}/{(int)args[5]}, expected {expectedClosed}/{expectedOpen}.");
    }

    private static void CheckClosedRecord(MethodInfo method, object patch, object slot, IntPtr note,
        int physicalLane, bool dead, bool starPower, int expectedVariant, string label)
    {
        InvokeRecord(method, patch, note, (ushort)(1 << physicalLane), dead, starPower, 1, 0, label);
        AssertIdentity(slot, physicalLane - 1, expectedVariant, label);
    }

    private static void CheckOpenRecord(MethodInfo method, object patch, object slot, IntPtr note, bool dead,
        bool starPower, int expectedVariant, string label)
    {
        InvokeRecord(method, patch, note, 1, dead, starPower, 0, 1, label);
        AssertIdentity(slot, -1, expectedVariant, label);
    }

    private static void CheckMixedRecord(MethodInfo method, object patch, object closedSlot, object openSlot,
        IntPtr note, int physicalLane, int expectedVariant, string label)
    {
        InvokeRecord(method, patch, note, (ushort)(1 | (1 << physicalLane)), false, false, 1, 1, label);
        AssertIdentity(openSlot, -1, 7, label + " open");
        AssertIdentity(closedSlot, physicalLane - 1, expectedVariant, label + " closed");
    }

    private static int InvokeHeadColor(MethodInfo method, int lane, int nativeColor, IntPtr note, string label)
    {
        try { return (int)method.Invoke(null, new object[] { lane, nativeColor, note }); }
        catch (TargetInvocationException error)
        {
            Exception cause = error.InnerException ?? error;
            throw new InvalidOperationException($"{label}: ResolveHeadColor threw {cause.Message}", cause);
        }
    }

    private void CheckHeadColor(Assembly candidate)
    {
        Type headType = MeshType(candidate, "GuitarNoteHeadPatch");
        MethodInfo resolver = headType.GetMethod("ResolveHeadColor", StaticFields);
        if (resolver == null)
            throw new MissingMethodException(headType.FullName, "ResolveHeadColor");
        FieldInfo shuffleField = headType.GetField("_shuffleHeads", StaticFields);
        if (shuffleField == null)
            throw new MissingMemberException(headType.FullName, "_shuffleHeads");
        bool originalShuffle = (bool)shuffleField.GetValue(null);
        IntPtr note = Marshal.AllocHGlobal(0x24);
        try
        {
            for (int seedIndex = 0; seedIndex < GoldenSeeds.Length; seedIndex++)
            {
                int seed = GoldenSeeds[seedIndex];
                Marshal.WriteInt32(note, 0x20, seed);
                StaticSet(headType, "_shuffleHeads", true);
                int open = InvokeHeadColor(resolver, 0, 99, note, $"head seed {seed} active open");
                if (open != 0)
                    throw new InvalidOperationException($"head seed {seed} active open: {open}, expected physical open color 0.");
                for (int physicalLane = 1; physicalLane <= 5; physicalLane++)
                {
                    int actual = InvokeHeadColor(resolver, physicalLane, 99 + physicalLane, note,
                        $"head seed {seed} active lane {physicalLane}");
                    int expected = GoldenColors[seedIndex][physicalLane - 1];
                    if (actual != expected)
                        throw new InvalidOperationException($"head seed {seed} active lane {physicalLane}: {actual}, expected {expected}.");
                }

                StaticSet(headType, "_shuffleHeads", false);
                for (int lane = 0; lane <= 5; lane++)
                {
                    int nativeColor = 99 + lane;
                    int actual = InvokeHeadColor(resolver, lane, nativeColor, note,
                        $"head seed {seed} inactive lane {lane}");
                    if (actual != nativeColor)
                        throw new InvalidOperationException($"head seed {seed} inactive lane {lane}: {actual}, expected native {nativeColor}.");
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(note);
            StaticSet(headType, "_shuffleHeads", originalShuffle);
        }
    }

    private void CheckSustainRecordMask(Assembly candidate)
    {
        Type patchType = MeshType(candidate, "Gh3SustainPatch");
        object notes = LiveSustainNotes(patchType);
        MethodInfo record = patchType.GetMethod("RecordMask", Fields);
        if (record == null)
            throw new MissingMethodException(patchType.FullName, "RecordMask");
        Array closed, open;
        object patch;
        try { patch = NewSustainFixture(patchType, notes, out closed, out open); }
        catch (Exception error)
        {
            throw new InvalidOperationException("[sustain-shuffle] synthetic Slot[] setup blocker: the live IL2CPP owner was found, but the managed Slot fixture could not be attached. Keep the warm Gameplay probe and update this reflection boundary rather than substituting a selection mock.", error);
        }
        object closedSlot = closed.GetValue(0), openSlot = open.GetValue(0);
        bool originalShuffle = (bool)Field(notes, "field_Protected_Boolean_1");
        IntPtr nativeNote = Marshal.AllocHGlobal(0x24);
        try
        {
            for (int seedIndex = 0; seedIndex < GoldenSeeds.Length; seedIndex++)
            {
                int seed = GoldenSeeds[seedIndex];
                Marshal.WriteInt32(nativeNote, 0x20, seed);
                Set(notes, "field_Protected_Boolean_1", false);
                for (int physicalLane = 1; physicalLane <= 5; physicalLane++)
                    CheckClosedRecord(record, patch, closedSlot, nativeNote, physicalLane, false, false,
                        physicalLane - 1, $"sustain seed {seed} unshuffled closed lane {physicalLane}");
                CheckOpenRecord(record, patch, openSlot, nativeNote, false, false, 7,
                    $"sustain seed {seed} unshuffled open");

                Set(notes, "field_Protected_Boolean_1", true);
                for (int physicalLane = 1; physicalLane <= 5; physicalLane++)
                    CheckClosedRecord(record, patch, closedSlot, nativeNote, physicalLane, false, false,
                        GoldenColors[seedIndex][physicalLane - 1] - 1,
                        $"sustain seed {seed} shuffled closed lane {physicalLane}");
                CheckOpenRecord(record, patch, openSlot, nativeNote, false, false, 7,
                    $"sustain seed {seed} shuffled open");
                CheckMixedRecord(record, patch, closedSlot, openSlot, nativeNote, 5,
                    GoldenColors[seedIndex][4] - 1, $"sustain seed {seed} shuffled open chord");

                CheckClosedRecord(record, patch, closedSlot, nativeNote, 2, false, true, 5,
                    $"sustain seed {seed} closed SP");
                CheckClosedRecord(record, patch, closedSlot, nativeNote, 3, true, false, 6,
                    $"sustain seed {seed} closed dead");
                CheckClosedRecord(record, patch, closedSlot, nativeNote, 4, true, true, 6,
                    $"sustain seed {seed} closed dead beats SP");
                CheckOpenRecord(record, patch, openSlot, nativeNote, false, true, 8,
                    $"sustain seed {seed} open SP");
                CheckOpenRecord(record, patch, openSlot, nativeNote, true, false, 9,
                    $"sustain seed {seed} open dead");
                CheckOpenRecord(record, patch, openSlot, nativeNote, true, true, 9,
                    $"sustain seed {seed} open dead beats SP");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(nativeNote);
            Set(notes, "field_Protected_Boolean_1", originalShuffle);
        }
    }

    private void CheckWorld(Assembly baseline, Assembly candidate)
    {
        object a = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(MeshType(baseline, "Gh3HighwayBridge"));
        object b = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(MeshType(candidate, "Gh3HighwayBridge"));
        var rng = new System.Random(73182);
        Matrix4x4 perspective = Matrix4x4.Perspective(55f, 16f / 9f, .3f, 1000f).inverse;
        for (int fixture = 0; fixture < 4096; fixture++)
        {
            object matrix = fixture % 3 == 0 ? Matrix4x4.identity : perspective;
            if (fixture % 3 == 2)
                for (int row = 0; row < 4; row++)
                    for (int column = 0; column < 4; column++)
                        Set(matrix, "m" + row + column, (float)(rng.NextDouble() * 4 - 2));
            if (fixture == 0) matrix = default(Matrix4x4);
            Rect rect = new Rect((float)(rng.NextDouble() * 200), (float)(rng.NextDouble() * 100),
                (float)(rng.NextDouble() * 2800 + 200), (float)(rng.NextDouble() * 1400 + 100));
            foreach (string field in new[] { "_screenX", "_screenY", "_scaleX", "_scaleY", "_depth" })
            {
                float value = (float)(rng.NextDouble() * 4 - 2);
                Set(a, field, value); Set(b, field, value);
            }
            Set(a, "_rect", rect); Set(b, "_rect", rect);
            Set(a, "_screenToWorld", matrix); Set(b, "_screenToWorld", matrix);
            float x = (float)(rng.NextDouble() * 2400 - 600);
            float y = (float)(rng.NextDouble() * 1400 - 350);
            Equal((Vector3)Call(a, "World", x, y), (Vector3)Call(b, "World", x, y), "world " + fixture);
        }
    }

    private void CheckHud(Assembly baseline, Assembly candidate)
    {
        var cameraObject = new GameObject("parity-camera");
        Camera camera = cameraObject.AddComponent<Camera>();
        camera.enabled = false;
        camera.nearClipPlane = 0.3f;
        try
        {
            foreach (bool additive in new[] { false, true })
            {
                object a = NewHud(baseline,camera,additive), b = NewHud(candidate,camera,additive);
                try
                {
                    var rng = new System.Random(73181);
                    for (int frame = 0; frame < 96; frame++)
                    {
                        // alternating unchanged and changed viewports defends the height cache.
                        if ((frame & 1) == 0)
                        {
                            camera.pixelRect = new Rect(frame % 3 * 17, frame % 5 * 11, 640 + frame * 13, 480 + frame * 7);
                            camera.fieldOfView = 35 + frame % 60;
                            camera.orthographic = frame % 4 == 0;
                            camera.orthographicSize = 2 + frame * 0.01f;
                        }
                        Equal((bool)Call(a,"RefreshViewport") ? 1 : 0, (bool)Call(b,"RefreshViewport") ? 1 : 0, "viewport changed");
                        Call(a,"Begin"); Call(b,"Begin");
                        int quads = frame % 65;
                        for (int q = 0; q < quads; q++)
                        {
                            Vector2[] points = new Vector2[4];
                            for (int p = 0; p < 4; p++) points[p] = new Vector2((float)(rng.NextDouble()*2000-300),(float)(rng.NextDouble()*1200-200));
                            float u0=(float)rng.NextDouble(),v0=(float)rng.NextDouble(),u1=(float)rng.NextDouble(),v1=(float)rng.NextDouble();
                            Color32 color = new Color32((byte)rng.Next(256),(byte)rng.Next(256),(byte)rng.Next(256),(byte)rng.Next(256));
                            int blend = additive ? q % 2 : 0;
                            foreach (object mesh in new[] {a,b})
                            {
                                Type blendType = mesh.GetType().Assembly.GetType("ClonZones.Gh3HudBlend");
                                Call(mesh,"Quad",points[0],points[1],points[2],points[3],u0,v0,u1,v1,color,Enum.ToObject(blendType,blend));
                            }
                        }
                        CompareVertices(a,b);
                        Call(a,"Upload"); Call(b,"Upload");
                        Mesh ma=(Mesh)Field(a,"_mesh"), mb=(Mesh)Field(b,"_mesh");
                        Equal(ma.bounds.center,mb.bounds.center,"bounds center");
                        Equal(ma.bounds.extents,mb.bounds.extents,"bounds extents");
                        Equal(ma.subMeshCount,mb.subMeshCount,"submesh count");
                        var ra=(MeshRenderer)Field(a,"_renderer");var rb=(MeshRenderer)Field(b,"_renderer");
                        Equal(ra.enabled?1:0,rb.enabled?1:0,"renderer enabled");
                        var matsA=ra.sharedMaterials;var matsB=rb.sharedMaterials;
                        Equal(matsA.Length,matsB.Length,"material count");
                        for(int m=0;m<matsA.Length;m++)
                            if(matsA[m].shader.name!=matsB[m].shader.name||matsA[m].renderQueue!=matsB[m].renderQueue)
                                throw new InvalidOperationException("material order differs");
                        _frames++;
                    }
                }
                finally { ((IDisposable)a).Dispose(); ((IDisposable)b).Dispose(); }
            }
        }
        finally { UnityEngine.Object.Destroy(cameraObject); }
    }

    private void CheckHighway(Assembly baseline, Assembly candidate)
    {
        object Make(Assembly assembly, bool fade)
        {
            object bridge = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(MeshType(assembly, "Gh3HighwayBridge"));
            Set(bridge, "_screenToWorld", Matrix4x4.identity);
            Set(bridge, "_rect", new Rect(0, 0, 1920, 1080));
            Set(bridge, "_screenX", 960f); Set(bridge, "_screenY", 120f);
            Set(bridge, "_scaleX", 1f); Set(bridge, "_scaleY", .85f); Set(bridge, "_depth", .55f);
            bridge.GetType().GetProperty("TopY", Fields)?.SetValue(bridge, 305f);
            object mesh = Activator.CreateInstance(MeshType(assembly, "Gh3HighwayMesh"), Fields, null, new object[] {
                "parity-highway", Texture2D.whiteTexture, Shader.Find("Sprites/Default"), bridge,
                0, 0, 0, 3000, 128, fade, null
            }, null);
            ((GameObject)Field(mesh, "_object")).SetActive(false);
            return mesh;
        }
        foreach (bool fade in new[] { false, true })
        {
            object a = Make(baseline, fade), b = Make(candidate, fade);
            try
            {
                for (int frame = 0; frame < 64; frame++)
                {
                    Color32 c0 = new Color32((byte)(frame * 3), 137, 251, (byte)(frame * 4));
                    Color32 c1 = new Color32(233, (byte)(frame * 2), 13, 255);
                    foreach (object mesh in new[] { a, b })
                    {
                        Call(mesh, "Begin");
                        if (frame != 0)
                        {
                            float y = 250f + (frame % 16) * 40f;
                            Call(mesh, "Bar", y, 100f + frame, 12f, c0);
                            Call(mesh, "Strip", 500f + frame, y, -.25f + frame * .02f, -1f,
                                130f, 24f, frame % 2 == 0, c1, .8f, .9f);
                            Call(mesh, "WorldQuad", new Vector3(-1, 0, .5f), new Vector3(1, 0, .5f),
                                new Vector3(1, 2, .4f), new Vector3(-1, 2, .4f),
                                frame / 64f, 1f - frame / 64f, c0, c1, -.25f, 1.25f);
                        }
                    }
                    CompareVertices(a, b);
                    Call(a, "Upload"); Call(b, "Upload");
                    Equal(((MeshRenderer)Field(a, "_renderer")).enabled ? 1 : 0,
                        ((MeshRenderer)Field(b, "_renderer")).enabled ? 1 : 0, "highway enabled");
                    if ((int)Field(a, "_count") != 0)
                    {
                        Mesh ma = (Mesh)Field(a, "_mesh"), mb = (Mesh)Field(b, "_mesh");
                        Equal((int)ma.GetIndexCountImpl(0), (int)mb.GetIndexCountImpl(0), "highway index count");
                        Equal(ma.bounds.center, mb.bounds.center, "highway bounds center");
                        Equal(ma.bounds.extents, mb.bounds.extents, "highway bounds extents");
                    }
                }
            }
            finally { ((IDisposable)a).Dispose(); ((IDisposable)b).Dispose(); }
        }
    }

    private static void CheckHighwayLength(Assembly candidate)
    {
        object bridge = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(MeshType(candidate, "Gh3HighwayBridge"));
        Set(bridge, "_strike", 0f);
        foreach (float z in new[] { 0f, .378f, 1f, 4f, 7.87f, 13.379f })
        {
            Set(bridge, "_far", (double)7.87f);
            float shortY = (float)Call(bridge, "DepthY", z);
            Set(bridge, "_far", (double)13.379f);
            float longY = (float)Call(bridge, "DepthY", z);
            if (shortY != longY)
                throw new InvalidOperationException($"highway length moves depth {z}: {shortY} -> {longY}");
        }
        float top = (float)Call(bridge, "DepthY", 13.379f);
        if (top >= 305f)
            throw new InvalidOperationException("longer highway does not extend beyond the authored far edge");
        bridge.GetType().GetProperty("TopY", Fields).SetValue(bridge, top);
        foreach (float distance in new[] { -10f, 0f, 15f, 30f, 60f })
        {
            float actual = (float)Call(bridge, "AlphaAtY", top + distance);
            float expected = Math.Clamp(distance / 30f, 0f, 1f);
            if (Math.Abs(actual - expected) > .00001f)
                throw new InvalidOperationException($"length-dependent fade mismatch at {distance}: {actual} != {expected}");
        }
    }

    private static void CheckSidebarAnchor(Assembly candidate)
    {
        object bridge = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(MeshType(candidate, "Gh3HighwayBridge"));
        Set(bridge, "_screenToWorld", Matrix4x4.identity);
        Set(bridge, "_rect", new Rect(0, 0, 1920, 1080));
        Set(bridge, "_screenX", 960f); Set(bridge, "_screenY", 120f);
        Set(bridge, "_scaleX", 1f); Set(bridge, "_scaleY", 1f); Set(bridge, "_depth", .55f);
        object mesh = Activator.CreateInstance(MeshType(candidate, "Gh3HighwayMesh"), Fields, null, new object[] {
            "parity-npot-sidebar", Texture2D.whiteTexture, Shader.Find("Sprites/Default"), bridge,
            0, 0, 0, 3000, 1, false, null
        }, null);
        try
        {
            ((GameObject)Field(mesh, "_object")).SetActive(false);
            Vector3 NearCorner(float scale)
            {
                Call(mesh, "Begin");
                // support the first candidate's call contract so the regression fails on its moving base.
                if (mesh.GetType().GetMethod("Strip", Fields).GetParameters().Length == 10)
                    Call(mesh, "Strip", 336f, 742.5f, 176f, -350f, 400f * scale, 12f, false,
                        new Color32(255, 255, 255, 255), 1f, 400f / 512f);
                else
                    Call(mesh, "Strip", 336f, 742.5f, 176f, -350f, 400f, 12f, false,
                        new Color32(255, 255, 255, 255), 1f, 400f / 512f, scale);
                Array vertices = (Array)Field(mesh, "_vertices");
                Vector3 near = new Vector3(0f, float.PositiveInfinity, 0f);
                for (int i = 0; i < (int)Field(mesh, "_count"); i++)
                {
                    Vector3 position = (Vector3)Field(vertices.GetValue(i), "Position");
                    if (position.y < near.y) near = position;
                }
                return near;
            }
            Vector3 original = NearCorner(1f), extended = NearCorner(1.7f);
            if (original.x != extended.x || original.y != extended.y || original.z != extended.z)
                throw new InvalidOperationException($"NPOT sidebar near edge moves when extended: {original} -> {extended}");
        }
        finally { ((IDisposable)mesh).Dispose(); }
    }

    private void CheckScene(Assembly baseline, Assembly candidate)
    {
        string fontRoot = Path.Combine(Path.GetDirectoryName(candidate.Location), "ClonZones", "fallback", "hud", "gh3");
        object LoadFont(Assembly assembly, string name)
        {
            string metrics = File.ReadAllText(Path.Combine(fontRoot, name + ".font.txt"));
            string[] page = metrics.Split('\n').Single(line => line.StartsWith("page ")).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int width = int.Parse(page[1]), height = int.Parse(page[2]);
            object font = MeshType(assembly, "Gh3HudFont").GetMethod("Parse", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { name, metrics, width, height });
            object region = Activator.CreateInstance(MeshType(assembly, "Gh3HudRegion"), new object[] { width, height, 0f, 0f, 1f, 1f });
            Call(font, "AssignAtlas", region);
            return font;
        }
        object Sprite(object scene, Assembly assembly, string id, object parent)
        {
            object element = Call(scene, "Add", id, parent);
            object region = Activator.CreateInstance(MeshType(assembly, "Gh3HudRegion"), new object[] { 164, 96, .125f, .25f, .75f, .875f });
            Set(element, "Region", region); Set(element, "Dims", new Vector2(164, 96)); Set(element, "Just", new Vector2(0, 0));
            Call(element, "SetPos", new Vector2(23.5f, -17.25f));
            Call(element, "SetRgba", new Color32(91, 173, 247, 231));
            return element;
        }
        (object Root, object Panel, object Text, object Axis) Populate(object scene, Assembly assembly, object font)
        {
            object root = Call(scene, "CreateContainer", "root", null, new Vector2(640, 400), 0f, 1f, 0f);
            object panel = Sprite(scene, assembly, "panel", root);
            object text = Call(scene, "CreateText", "text", panel, font, "12,345", new Vector2(13.75f, 29.5f),
                new Vector2(0, 0), 1f, .9f, new Color32(251, 211, 83, 237), new Vector2(.83f, 1.17f), 13.37f);
            Set(text, "Shadow", true); Set(text, "ShadowOffset", new Vector2(.5f, -1.5f));
            Set(text, "ShadowRgba", new Color32(17, 31, 47, 191));
            object axis = Sprite(scene, assembly, "axis", null);
            Set(axis, "Blend", Enum.ToObject(MeshType(assembly, "Gh3HudBlend"), 1));
            return (root, panel, text, axis);
        }
        var cameraObject = new GameObject("parity-scene-camera");
        Camera camera = cameraObject.AddComponent<Camera>(); camera.enabled = false;
        camera.orthographic = true; camera.orthographicSize = 5f;
        object ma = NewHud(baseline, camera, true), mb = NewHud(candidate, camera, true);
        Assembly[] assemblies = { baseline, candidate };
        object[] scenes = assemblies.Select(a => Activator.CreateInstance(MeshType(a, "Gh3HudScene"), new object[] { null })).ToArray();
        var nodes = new (object Root, object Panel, object Text, object Axis)[2];
        float[] angles = { 0f, BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)), 1e-6f, 45f, -45f, 90f, 180f, 360f, 13.37f };
        try
        {
            for (int frame = 0; frame < 192; frame++)
            {
                camera.pixelRect = frame % 24 < 12 ? new Rect(0, 0, 1280, 720) : new Rect(17, 29, 1920, 1080);
                for (int side = 0; side < 2; side++)
                {
                    object scene = scenes[side]; Assembly assembly = assemblies[side];
                    if (frame % 64 == 0)
                    {
                        Call(scene, "Clear");
                        string name = new[] { "num_a9", "num_a7", "text_a6" }[frame / 64];
                        nodes[side] = Populate(scene, assembly, LoadFont(assembly, name));
                    }
                    if (frame % 64 == 47)
                    {
                        Call(scene, "Destroy", nodes[side].Axis);
                        nodes[side].Axis = Sprite(scene, assembly, "axis", null);
                    }
                    var state = nodes[side];
                    float angle = angles[(frame / 2) % angles.Length];
                    Call(state.Root, "SetRot", angle); Call(state.Axis, "SetRot", angle);
                    Set(state.Root, "Scale", new Vector2(.7f + frame % 5 * .1f, 1.1f - frame % 3 * .1f));
                    Set(state.Text, "FontSpacing", frame % 3 == 0 ? -1f : 5.5f);
                    ((char[])Field(state.Text, "Text"))[0] = frame % 2 == 0 ? '9' : '1';
                    if (frame % 32 == 1)
                    {
                        object morph = Activator.CreateInstance(MeshType(assembly, "Gh3Morph"));
                        Set(morph, "HasPos", true); Set(morph, "Pos", new Vector2(120 + frame, -15f + frame * .3f));
                        Set(morph, "HasScale", true); Set(morph, "Scale", new Vector2(.6f + frame * .01f, 1.4f - frame * .001f));
                        Set(morph, "HasRot", true); Set(morph, "Rot", angle + 23.5f);
                        Set(morph, "HasAlpha", true); Set(morph, "Alpha", .37f + frame % 3 * .2f);
                        Set(morph, "HasRgba", true); Set(morph, "Rgba", new Color32(83, 141, 219, 173));
                        Set(morph, "Time", .48f); Set(morph, "Motion", Enum.ToObject(MeshType(assembly, "Gh3Motion"), (frame / 32) % 4));
                        Call(state.Panel, "Morph", morph, frame * 16L);
                    }
                    Call(scene, "Tick", frame * 16L);
                    Set(state.Axis, "Z", frame % 17 < 8 ? 0f : 2f); Call(scene, "ZChanged");
                    object mesh = side == 0 ? ma : mb;
                    Call(mesh, "RefreshViewport"); Call(mesh, "Begin"); Call(scene, "Draw", mesh);
                }
                CompareVertices(ma, mb);
                Call(ma, "Upload"); Call(mb, "Upload");
                Mesh a = (Mesh)Field(ma, "_mesh"), b = (Mesh)Field(mb, "_mesh");
                Equal(a.subMeshCount, b.subMeshCount, "scene submesh count");
                Equal(a.bounds.center, b.bounds.center, "scene bounds center"); Equal(a.bounds.extents, b.bounds.extents, "scene bounds extents");
                _sceneFrames++;
            }
        }
        finally { ((IDisposable)ma).Dispose(); ((IDisposable)mb).Dispose(); UnityEngine.Object.Destroy(cameraObject); }
    }

    private void CompareVertices(object a, object b)
    {
        int count=(int)Field(a,"_count");Equal(count,(int)Field(b,"_count"),"active vertex count");
        Array va=(Array)Field(a,"_vertices"),vb=(Array)Field(b,"_vertices");
        for(int i=0;i<count;i++)
        {
            object x=va.GetValue(i),y=vb.GetValue(i);
            Equal((Vector3)Field(x,"Position"),(Vector3)Field(y,"Position"),"vertex "+i);
            Vector2 ux=(Vector2)Field(x,"UV"),uy=(Vector2)Field(y,"UV");
            Equal(ux.x,uy.x,"u");Equal(ux.y,uy.y,"v");
            Color32 cx=(Color32)Field(x,"Color"),cy=(Color32)Field(y,"Color");
            Equal(cx.r,cy.r,"r");Equal(cx.g,cy.g,"g");Equal(cx.b,cy.b,"b");Equal(cx.a,cy.a,"a");
        }
    }

    private void CheckBounds(Assembly baseline, Assembly candidate, string typeName)
    {
        // Run the actual private vertex buffers through the real native Mesh upload path.
        // A null bridge is enough: these fixtures do not call highway emission/projection.
        object Make(Assembly assembly)
        {
            if(typeName=="Gh3HudMesh") return NewHud(assembly,Camera.main,false);
            object mesh = Activator.CreateInstance(MeshType(assembly,typeName), Fields, null, new object[] {
                "parity-bounds", Texture2D.whiteTexture, Shader.Find("Sprites/Default"), null,
                0, 0, 0, 3000, 1, false, null
            }, null);
            ((GameObject)Field(mesh,"_object")).SetActive(false);
            return mesh;
        }
        object a=Make(baseline),b=Make(candidate);
        try
        {
            float[] values={0f,-0f,1f,-1f,float.Epsilon,-float.Epsilon,float.MaxValue,float.MinValue,float.PositiveInfinity,float.NegativeInfinity,
                BitConverter.Int32BitsToSingle(unchecked((int)0x7FC12345)),BitConverter.Int32BitsToSingle(unchecked((int)0xFFC54321))};
            foreach(float x in values) foreach(float y in values)
            {
                foreach(object mesh in new[]{a,b})
                {
                    Array vertices=(Array)Field(mesh,"_vertices");Type vertexType=vertices.GetType().GetElementType();
                    for(int i=0;i<3;i++)
                    {
                        object vertex=Activator.CreateInstance(vertexType);
                        Set(vertex,"Position",new Vector3(i==0?x:y,i==0?y:x,0));vertices.SetValue(vertex,i);
                    }
                    Set(mesh,"_count",3);Call(mesh,"Upload");
                }
                Bounds ba=((Mesh)Field(a,"_mesh")).bounds,bb=((Mesh)Field(b,"_mesh")).bounds;
                Equal(ba.center,bb.center,"edge bounds center");Equal(ba.extents,bb.extents,"edge bounds extents");
            }
        }
        finally {((IDisposable)a).Dispose();((IDisposable)b).Dispose();}
    }
}
