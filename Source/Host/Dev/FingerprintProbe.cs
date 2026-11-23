#if EH_DEV
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal sealed class FingerprintSet
    {
        private readonly SortedDictionary<string, string> items = new SortedDictionary<string, string>(StringComparer.Ordinal);

        internal void Add(string name, FingerprintHash hash) => items[name] = hash.Text;

        internal void Count(string name, long count) => items[name] = "n" + count;

        internal string Text => string.Join(" ", items.Select(i => i.Key + "=" + i.Value));

        internal readonly SortedDictionary<string, int> Materials = new SortedDictionary<string, int>(StringComparer.Ordinal);
    }

    internal sealed class FingerprintHash
    {
        private static readonly Dictionary<string, int> texts = new Dictionary<string, int>();
        private static readonly Regex AtlasPage = new Regex(@"(TextureAtlas_[A-Za-z]+_(?:True|False))_-?\d+");
        private ulong value = 14695981039346656037UL;

        internal string Text => value.ToString("x16");

        internal void Int(int v)
        {
            unchecked { value = (value ^ (uint)v) * 1099511628211UL; }
        }

        internal void Long(ulong v)
        {
            unchecked { Int((int)v); Int((int)(v >> 32)); }
        }

        internal void Float(float v) => Int(v == 0f ? 0 : BitConverter.SingleToInt32Bits(v));

        internal void Bool(bool v) => Int(v ? 1 : 0);

        internal void String(string text) => Int(OfText(text));

        internal void Def(Def def) => String(def?.defName);

        internal void Colour(Color32 c) => Int(c.r | c.g << 8 | c.b << 16 | c.a << 24);

        internal void Vector(Vector3 v)
        {
            Float(v.x); Float(v.y); Float(v.z);
        }

        private static int OfText(string text)
        {
            if (text == null) return 0;
            if (texts.TryGetValue(text, out int known)) return known;
            unchecked
            {
                uint h = 2166136261;
                foreach (char c in text) h = (h ^ c) * 16777619u;
                return texts[text] = (int)h;
            }
        }

        internal bool Array(Array array)
        {
            switch (array)
            {
                case null: Int(-1); return true;
                case bool[] a: Int(a.Length); foreach (bool v in a) Bool(v); return true;
                case byte[] a: Int(a.Length); foreach (byte v in a) Int(v); return true;
                case int[] a: Int(a.Length); foreach (int v in a) Int(v); return true;
                case float[] a: Int(a.Length); foreach (float v in a) Float(v); return true;
                case double[] a: Int(a.Length); foreach (double v in a) Float((float)v); return true;
                case Def[] a: Int(a.Length); foreach (Def v in a) Def(v); return true;
                default: return false;
            }
        }

        internal void Mesh(Mesh mesh)
        {
            if (mesh == null)
            {
                Int(-1);
                return;
            }
            foreach (Vector3 v in mesh.vertices) Vector(v);
            foreach (int t in mesh.triangles) Int(t);
            foreach (Color32 c in mesh.colors32) Colour(c);
        }

        private static string Stable(string name) => name == null ? null : AtlasPage.Replace(name, "$1");

        internal static ulong Of(LayerSubMesh sm, bool animated = false)
        {
            var h = new FingerprintHash();
            Material m = sm.material;
            if (m != null)
            {
                h.String(Stable(m.name));
                h.String(m.shader?.name);
                h.Int(m.renderQueue);
                if (m.HasProperty("_MainTex"))
                {
                    h.String(Stable(m.mainTexture?.name));
                    if (!animated)
                    {
                        h.Float(m.mainTextureOffset.x); h.Float(m.mainTextureOffset.y);
                        h.Float(m.mainTextureScale.x); h.Float(m.mainTextureScale.y);
                    }
                }
                if (!animated && m.HasProperty("_Color")) h.Colour(m.color);
            }
            h.Int(sm.renderLayer);
            foreach (Vector3 v in sm.verts) h.Vector(v);
            foreach (int t in sm.tris) h.Int(t);
            foreach (Color32 c in sm.colors) h.Colour(c);
            foreach (Vector3 v in sm.uvs) h.Vector(v);
            foreach (Vector3 v in sm.uvsChannelTwo) h.Vector(v);
            foreach (Vector3 v in sm.pollution) h.Vector(v);
            return h.value;
        }

        internal static bool OnAtlas(LayerSubMesh sm) =>
            sm.material?.name?.IndexOf("TextureAtlas", StringComparison.Ordinal) >= 0;

        internal static string Identity(LayerSubMesh sm)
        {
            Material m = sm.material;
            if (m == null) return "none";
            string text = $"{m.name}|{m.shader?.name}|q{m.renderQueue}|l{sm.renderLayer}";
            if (m.HasProperty("_MainTex")) text += FormattableString.Invariant($"|{m.mainTexture?.name}|o{m.mainTextureOffset.x:R},{m.mainTextureOffset.y:R}");
            if (m.HasProperty("_Color"))
            {
                Color32 c = m.color;
                text += $"|c{c.r},{c.g},{c.b},{c.a}";
            }
            return text;
        }

        internal static void Triangles(LayerSubMesh sm, List<ulong> into)
        {
            bool coloured = sm.colors.Count == sm.verts.Count;
            if (sm.verts.Count % 4 == 0 && sm.material?.name?.IndexOf("TextureAtlas_Item_", StringComparison.Ordinal) >= 0)
            {
                Quads(sm, coloured, into);
                return;
            }
            for (int t = 0; t + 2 < sm.tris.Count; t += 3)
            {
                var h = new FingerprintHash();
                for (int k = 0; k < 3; k++)
                {
                    int v = sm.tris[t + k];
                    h.Vector(sm.verts[v]);
                    if (coloured) h.Colour(sm.colors[v]);
                }
                into.Add(h.value);
            }
        }

        private static void Quads(LayerSubMesh sm, bool coloured, List<ulong> into)
        {
            var corner = new ulong[4];
            var height = new int[4];
            for (int q = 0; q + 3 < sm.verts.Count; q += 4)
            {
                for (int k = 0; k < 4; k++)
                {
                    Vector3 v = sm.verts[q + k];
                    var h = new FingerprintHash();
                    h.Int(Mathf.RoundToInt(v.x * 4096f));
                    h.Int(Mathf.RoundToInt(v.z * 4096f));
                    if (coloured) h.Colour(sm.colors[q + k]);
                    corner[k] = h.value;
                    height[k] = Mathf.RoundToInt(v.y * 4096f);
                }
                System.Array.Sort(corner);
                System.Array.Sort(height);
                var quad = new FingerprintHash();
                foreach (ulong c in corner) quad.Long(c);
                foreach (int y in height) quad.Int(y);
                into.Add(quad.value);
            }
        }

        internal static ulong AtlasUvs(LayerSubMesh sm)
        {
            var h = new FingerprintHash();
            foreach (Vector3 v in sm.uvs) h.Vector(v);
            return h.value;
        }

        internal static ulong AtlasMaterial(LayerSubMesh sm)
        {
            var h = new FingerprintHash();
            h.String(Stable(Identity(sm)));
            return h.value;
        }

        internal void Meshes(List<ulong> hashes)
        {
            hashes.Sort();
            Int(hashes.Count);
            foreach (ulong h in hashes) Long(h);
        }
    }

    internal sealed partial class HorizonField
    {
        internal void Fingerprint(FingerprintSet set)
        {
            void Cells(string name, Array array)
            {
                var h = new FingerprintHash();
                h.Array(array);
                set.Add("source." + name, h);
            }
            Cells("terrain", cells);
            Cells("samples", samples);
            Cells("rawSamples", rawSamples);
            Cells("rock", genRock);
            Cells("rockDef", genRockDef);
            Cells("roof", genRoofLevel);
            GenerationSnapshot snapshot = Generator?.Snapshot;
            Cells("gen.elevation", snapshot?.Elevation);
            Cells("gen.fertility", snapshot?.Fertility);
            Cells("gen.caves", snapshot?.Caves);
            Cells("gen.biome", snapshot?.Biomes);
            Cells("gen.terrain", snapshot?.Terrain);
            Cells("gen.roof", snapshot?.Roofs);
            Cells("gen.flowAlpha", snapshot?.FlowAlpha);
            Cells("gen.flowBeta", snapshot?.FlowBeta);
            var mode = new FingerprintHash();
            mode.String(Generator != null ? "generator" : "mirror");
            set.Add("source.mode", mode);
            ModuleItems(set);
        }

        private void ModuleItems(FingerprintSet set)
        {
            var roads = new FingerprintHash();
            for (int z = -r; z < domain.H + r; z++)
                for (int x = -r; x < domain.W + r; x++)
                {
                    var c = new IntVec3(x, 0, z);
                    if (!domain.Outside(c)) continue;
                    roads.Def(RoadTopAt(c));
                    roads.Bool(RoadCleared(c));
                    roads.Int(TunnelRoof(c));
                }
            if (RoadBuildingCells > 0)
                for (int z = -r; z < domain.H + r; z++)
                    for (int x = -r; x < domain.W + r; x++)
                    {
                        var c = new IntVec3(x, 0, z);
                        if (domain.Outside(c)) roads.Def(RoadBuildingAt(c));
                    }
            roads.Int(RoadCells);
            roads.Int(BridgeCells);
            roads.Int(RoadCutCells);
            set.Add("module.roads", roads);
            var coast = new FingerprintHash();
            coast.String(CoastMode);
            coast.Int(CoastCells);
            coast.Int(CoastBoundaryCells);
            set.Add("module.coast", coast);
            var generation = new FingerprintHash();
            generation.String(OutsideMode);
            generation.String(RiverPaintReport);
            generation.Int(WildSubstitutions);
            generation.Int(RiverCells);
            set.Add("module.generation", generation);
        }
    }

    internal sealed partial class HorizonLighting
    {
        internal void Fingerprint(FingerprintHash h) => h.Mesh(mesh);
    }

    internal sealed partial class HorizonFog
    {
        internal void Fingerprint(FingerprintHash h)
        {
            h.Array(baseAlpha);
            h.Array(solid);
            h.Mesh(mesh);
        }
    }

    internal sealed partial class HorizonState
    {
        internal string BusyReason()
        {
            if (!Built) return "not built";
            if (Replacing || RebuildRequested) return "replacement";
            if (refreshWork != null || LiveBusy) return "refresh";
            if (seamQueue.Count + refreshQueue.Count + snowQueue.Count > 0) return "refresh queue";
            if (glowQueue.Count > 0) return "light queue";
            if (pendingSwaps.Count > 0) return "mesh swap";
            if (Field.PendingCoastSearch != null) return "coast search";
            string busy = extensions?.Busy();
            if (busy != null) return busy;
            return null;
        }

        internal void Fingerprint(FingerprintSet set)
        {
            Field.Fingerprint(set);
            SurfaceCells(set);
            Output(set);
            FingerprintCompat.Add(this, set);
            if (IntegrationList.Find("quietwonders")?.State == CompatState.Detected) Compat_QuietWonders.Fingerprint(this, set);
        }

        private void SurfaceCells(FingerprintSet set)
        {
            FingerprintHash top = new FingerprintHash(), ground = new FingerprintHash(), rock = new FingerprintHash(),
                roof = new FingerprintHash(), fog = new FingerprintHash(), region = new FingerprintHash(),
                dirty = new FingerprintHash(), ice = new FingerprintHash(), river = new FingerprintHash(),
                snow = new FingerprintHash(), glow = new FingerprintHash();
            for (int z = -RingWidth; z < map.Size.z + RingWidth; z++)
            {
                for (int x = -RingWidth; x < map.Size.x + RingWidth; x++)
                {
                    var c = new IntVec3(x, 0, z);
                    if (c.InBounds(map)) continue;
                    top.Def(Field.TopAt(c));
                    ground.Def(Field.At(c));
                    bool solid = Mountains?.IsRock(x, z) == true;
                    rock.Def(solid ? Mountains.RockDefAt(x, z) : null);
                    rock.Bool(solid);
                    roof.Bool(Field.OutsideThickRoof(c));
                    roof.Int(Field.TunnelRoof(c));
                    fog.Bool(Field.OutsideFogged(c));
                    region.Bool(Field.OutsideFogAt(c));
                    dirty.Bool(Field.PollutedAt(c));
                    ice.Bool(Field.FrozenAt(c));
                    int crossing = -1;
                    float along = 0f;
                    bool onRiver = Field.River != null && Field.River.TryPathAt(x, z, out crossing, out along);
                    river.Int(onRiver ? crossing : -1);
                    river.Float(onRiver ? along : 0f);
                    snow.Float(Surface.Snow(x, z));
                    glow.Colour(outsideGlow?.At(x, z) ?? default(Color32));
                }
            }
            set.Add("surface.top", top);
            set.Add("surface.base", ground);
            set.Add("surface.rock", rock);
            set.Add("surface.roof", roof);
            set.Add("surface.fogged", fog);
            set.Add("surface.fogRegion", region);
            set.Add("surface.polluted", dirty);
            set.Add("surface.frozen", ice);
            set.Add("surface.river", river);
            set.Add("surface.snow", snow);
            set.Add("surface.glow", glow);
        }

        private void Output(FingerprintSet set)
        {
            void Chunks(string name, List<HorizonChunk> chunks)
            {
                var found = new List<ulong>();
                foreach (HorizonChunk chunk in chunks)
                    foreach (LayerSubMesh sm in chunk.subMeshes)
                        if (sm.finalized && !sm.disabled && sm.verts.Count > 0) found.Add(FingerprintHash.Of(sm));
                var h = new FingerprintHash();
                h.Meshes(found);
                set.Add(name, h);
            }
            Chunks("output.terrain", terrainChunks);
            Chunks("output.depth", depthChunks);

            List<ulong> plain = new List<ulong>(), triangles = new List<ulong>(), atlasUv = new List<ulong>(),
                atlasMaterial = new List<ulong>(), weather = new List<ulong>(), fogOfWar = new List<ulong>();
            long thingVertices = 0;
            foreach (HorizonThingsChunk chunk in dynThings)
            {
                if (chunk == null) continue;
                foreach (LayerSubMesh sm in chunk.subMeshes)
                {
                    if (!sm.finalized || sm.disabled) continue;
                    if (sm.material == MatBases.IndoorMask || sm.material == MatBases.RoofedOutdoorMask) weather.Add(FingerprintHash.Of(sm));
                    else if (sm.material == MatBases.FogOfWar) fogOfWar.Add(FingerprintHash.Of(sm));
                    else
                    {
                        FingerprintHash.Triangles(sm, triangles);
                        if (FingerprintHash.OnAtlas(sm))
                        {
                            atlasUv.Add(FingerprintHash.AtlasUvs(sm));
                            atlasMaterial.Add(FingerprintHash.AtlasMaterial(sm));
                        }
                        else plain.Add(FingerprintHash.Of(sm));
                        string identity = FingerprintHash.Identity(sm);
                        set.Materials[identity] = (set.Materials.TryGetValue(identity, out int n) ? n : 0) + 1;
                        thingVertices += sm.verts.Count;
                    }
                }
            }
            void Meshes(string name, List<ulong> found)
            {
                var h = new FingerprintHash();
                h.Meshes(found);
                set.Add(name, h);
            }
            Meshes("output.things.geometry", triangles);
            Meshes("output.things.plain", plain);
            Meshes("output.things.atlasUv", atlasUv);
            Meshes("output.things.atlasMaterial", atlasMaterial);
            set.Count("output.thingVertices", thingVertices);
            Meshes("output.weatherMask", weather);
            Meshes("output.fogOfWar", fogOfWar);

            var light = new FingerprintHash();
            foreach ((CellRect rect, Mesh mesh) in lightChunks.OrderBy(l => l.rect.minX).ThenBy(l => l.rect.minZ)) light.Mesh(mesh);
            set.Add("output.lighting", light);
            var snow = new FingerprintHash();
            foreach (int i in Enumerable.Range(0, dynSnow.Count).OrderBy(i => dynRects[i].minX).ThenBy(i => dynRects[i].minZ))
                if (dynSnow[i] != null) snow.Mesh(dynSnow[i]);
            set.Add("output.snow", snow);
            var frame = new FingerprintHash();
            lighting?.Fingerprint(frame);
            set.Add("output.lightingFrame", frame);
            var fog = new FingerprintHash();
            Fog?.Fingerprint(fog);
            set.Add("output.fogMask", fog);
            var sky = new FingerprintHash();
            sky.Mesh(Compat_WeatherReimagined.SkyOf(map));
            set.Add("output.openSky", sky);
        }
    }

    internal static class FingerprintCompat
    {
        internal static void Add(HorizonState s, FingerprintSet set)
        {
            foreach (ExtensionRing.OwnerWork work in s.Extensions.Owners)
            {
                if (work.Layers.Count == 0) continue;
                string name = "compat." + work.Owner;
                var found = new List<ulong>();
                var layers = new HashSet<SectionLayer>();
                foreach (ExtensionRing.Strip strip in work.Shown)
                    if (layers.Add(strip.Layer))
                        foreach (LayerSubMesh sm in strip.Layer.subMeshes)
                            if (sm.finalized && sm.verts.Count > 0) found.Add(FingerprintHash.Of(sm, animated: true));
                var h = new FingerprintHash();
                h.Meshes(found);
                set.Add(name + ".meshes", h);
                set.Count(name + ".strips", layers.Count);
                if (work.Owner != "waves" || layers.Count == 0) continue;
                object kept = Compat_Waves.ShownShore(s.map)?.TrimToMap();
                if (kept == null) continue;
                foreach (FieldInfo f in kept.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public).Where(f => f.FieldType.IsArray))
                {
                    var values = new FingerprintHash();
                    if (values.Array(f.GetValue(kept) as Array)) set.Add(name + ".field." + f.Name, values);
                }
            }
        }
    }

    internal static partial class Compat_QuietWonders
    {
        internal static void Fingerprint(HorizonState s, FingerprintSet set)
        {
            FingerprintHash fog = new FingerprintHash(), roof = new FingerprintHash(), terrain = new FingerprintHash(),
                plant = new FingerprintHash(), flow = new FingerprintHash();
            FingerprintHash roofBefore = new FingerprintHash();
            Map map = s.map;
            try
            {
                if (!Enter(map)) return;
                Ctx k = ctx;
                for (int z = -k.L; z < k.H + k.L; z++)
                {
                    for (int x = -k.L; x < k.W + k.L; x++)
                    {
                        var c = new IntVec3(x, 0, z);
                        if (c.InBounds(map)) continue;
                        fog.Bool(W_Fogged(c, map));
                        roof.Bool(W_Roofed(c, map));
                        roofBefore.Bool(Rock(k, c) || s.Field.OutsideThickRoof(c) || s.Field.InTunnel(c));
                        terrain.Def(W_GetTerrain(c, map));
                        Plant p = W_GetPlant(c, map);
                        plant.Def(p?.def);
                        flow.Vector(FlowOut(k, c));
                    }
                }
                set.Add("compat.quietwonders.fogged", fog);
                set.Add("compat.quietwonders.roofed", roof);
                set.Materials["EH_LEGACY compat.quietwonders.roofed=" + roofBefore.Text] = 1;
                set.Add("compat.quietwonders.terrain", terrain);
                set.Add("compat.quietwonders.plant", plant);
                set.Add("compat.quietwonders.flow", flow);
            }
            finally
            {
                ExitFinalizer(null);
            }
        }
    }

    internal static class HorizonFingerprint
    {
        private const string Marker = EndlessHorizonMod.LogPrefix + "EH_FINGERPRINT ";
        private const string Detail = EndlessHorizonMod.LogPrefix + "EH_FINGERPRINT_DETAIL ";
        private const int StableFrames = 75;
        private const float TimeoutSeconds = 20f;

        private static Map waiting;
        private static float deadline;
        private static int idleSince;
        private static string lastBusy;

        [DebugAction("Endless Horizon", "Log fingerprint (when idle)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Request()
        {
            waiting = Find.CurrentMap;
            deadline = Time.realtimeSinceStartup + TimeoutSeconds;
            idleSince = Time.frameCount;
            lastBusy = "no frame was drawn";
        }

        internal static void OnFrame(Map map, HorizonState s)
        {
            if (waiting == null || map != waiting) return;
            string busy = Find.TickManager.Paused ? s.BusyReason() : "not paused";
            if (busy != null)
            {
                lastBusy = busy;
                idleSince = Time.frameCount;
            }
            else if (Time.frameCount - idleSince >= StableFrames)
            {
                waiting = null;
                Write(map, s);
                return;
            }
            if (Time.realtimeSinceStartup <= deadline) return;
            waiting = null;
            Log.Message(Marker + "failed: not idle: " + lastBusy);
        }

        private static void Write(Map map, HorizonState s)
        {
            try
            {
                var set = new FingerprintSet();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                s.Fingerprint(set);
                Log.ResetMessageCount();
                var lines = new StringBuilder();
                foreach (KeyValuePair<string, int> material in set.Materials)
                {
                    lines.Append(material.Value).Append(' ').Append(material.Key).Append('\n');
                    if (lines.Length < 4000) continue;
                    Log.Message(Detail + lines);
                    lines.Clear();
                }
                if (lines.Length > 0) Log.Message(Detail + lines);
                foreach (string line in MigrationProbe.Lines(s)) Log.Message(Detail + line + "\n");
                Log.Message($"{Marker}mvid={typeof(HorizonState).Module.ModuleVersionId} tick={Find.TickManager.TicksGame} ms={sw.ElapsedMilliseconds} |{set.Text}");
            }
            catch (Exception ex)
            {
                Log.Message(Marker + "failed: " + ex);
            }
        }
    }
}
#endif
