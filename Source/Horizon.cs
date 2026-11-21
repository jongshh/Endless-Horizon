using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using RimWorld;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    public static class HorizonRegistry
    {
        private static readonly ConditionalWeakTable<Map, HorizonState> states = new ConditionalWeakTable<Map, HorizonState>();
        private static int generation;

        public static bool IsActive(Map map)
        {
            if (map == null || EndlessHorizonMod.Settings == null || !EndlessHorizonMod.Settings.enabled)
            {
                return false;
            }
            if (!map.DrawMapClippers || map.IsPocketMap || map.generatorDef == null || map.generatorDef.isUnderground)
            {
                return false;
            }
            return !(map.TileInfo?.Layer?.Def?.isSpace ?? false);
        }

        public static HorizonState Get(Map map)
        {
            if (!states.TryGetValue(map, out HorizonState state) || state.Generation != generation)
            {
                state?.Dispose();
                state = new HorizonState(map, generation);
                states.AddOrUpdate(map, state);
            }
            HorizonState ready = state.AdvanceReplacement();
            if (ready != null)
            {
                states.AddOrUpdate(map, ready);
                ready.retiredWork = state.DisposeSteps().GetEnumerator();
                EdgeRock.RedrawEdgeRock(map);
                state = ready;
            }
            return state;
        }

        public static HorizonState Peek(Map map)
        {
            return map != null && states.TryGetValue(map, out HorizonState state) ? state : null;
        }

        public static void Invalidate(Map map)
        {
            if (map != null && states.TryGetValue(map, out HorizonState state))
            {
                state.Dispose();
                states.Remove(map);
            }
        }

        public static void InvalidateAll()
        {
            generation++;
        }
    }

    public sealed class HorizonState : IDisposable
    {
        public readonly Map map;
        public readonly int Generation;
        public readonly int RingWidth;
        public readonly int FogStart;

        public HorizonField Field;
        public CoastNoise.Source CoastSourceOverride;
        public bool RebuildRequested;
        private HorizonState replacement;
        internal IEnumerator<bool> retiredWork;
        internal bool Replacing => replacement != null;
        private IEnumerator<bool> initialWork;
        private string buildPhase = "field";
        private readonly HashSet<IntVec3> replacementEdits = new HashSet<IntVec3>();
        private float rebuildDue;
        private int replacementFrame = -1;

        private void RequestFullRebuild(string reason)
        {
            RebuildRequested = true;
            rebuildDue = Time.realtimeSinceStartup + 0.35f;
            HorizonPerf.Record("ring rebuild requested: " + reason, 0);
        }

        internal HorizonState AdvanceReplacement()
        {
            if (replacementFrame == Time.frameCount) return null;
            replacementFrame = Time.frameCount;
            if (replacement == null)
            {
                if (!Built || !RebuildRequested || Time.realtimeSinceStartup < rebuildDue) return null;
                RebuildRequested = false;
                replacement = new HorizonState(map, Generation) { CoastSourceOverride = CoastSourceOverride };
                replacement.initialWork = replacement.BuildSteps().GetEnumerator();
            }
            var budget = Stopwatch.StartNew();
            try
            {
                do
                {
                    long started = Stopwatch.GetTimestamp();
                    bool more = replacement.initialWork.MoveNext();
                    double unitMs = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                    if (unitMs > 0.2) HorizonPerf.Record("ring replacement unit: " + replacement.buildPhase, unitMs);
                    if (!more)
                    {
                        HorizonState ready = replacement;
                        ready.initialWork.Dispose();
                        ready.initialWork = null;
                        ready.RebuildRequested = RebuildRequested;
                        ready.rebuildDue = rebuildDue;
                        foreach (IntVec3 loc in replacementEdits) ready.MarkSeamDirty(loc, false);
                        replacementEdits.Clear();
                        replacement = null;
                        return ready;
                    }
                } while (budget.Elapsed.TotalMilliseconds < 2.0);
                return null;
            }
            catch
            {
                replacement.Dispose();
                replacement = null;
                throw;
            }
            finally { HorizonPerf.Record("ring replacement (per frame)", budget.Elapsed.TotalMilliseconds); }
        }
        private readonly List<HorizonChunk> terrainChunks = new List<HorizonChunk>();
        private readonly List<HorizonChunk> depthChunks = new List<HorizonChunk>();
        private readonly List<CellRect> dynRects = new List<CellRect>();
        private readonly Dictionary<(int x, int z), int> chunkByCorner = new Dictionary<(int, int), int>();
        private readonly List<HorizonThingsChunk> dynThings = new List<HorizonThingsChunk>();
        private readonly List<Mesh> dynSnow = new List<Mesh>();
        private readonly Queue<int> refreshQueue = new Queue<int>();
        private readonly Queue<int> snowQueue = new Queue<int>();
        private readonly Queue<int> seamQueue = new Queue<int>();
        private readonly HashSet<int> seamQueued = new HashSet<int>();
        public int SeamRebuilds;
        private SeasonWatch seasons;
        public int Refreshes;
        public int PrintedThings;
        public int FogCells;
        private HorizonLighting lighting;
        public MountainMask Mountains;
        private readonly List<(CellRect rect, Mesh mesh)> lightChunks = new List<(CellRect, Mesh)>();
        private OutsideGlow outsideGlow;
        public OutsideGlow Glow => outsideGlow;
        private int glowSignature, nextGlowCheck;
        private float glowThingDist;
        public HorizonFog Fog;
        public Mesh OpenSky;
        private bool built;
        public bool Built { get; private set; }
        private readonly List<IOutsideCompatLayer> compatLayers = new List<IOutsideCompatLayer>();
        internal int CompatSeamRevision;
        private readonly List<IntVec3> compatEvents = new List<IntVec3>();
        internal int CompatEventFirstSerial { get; private set; }
        internal int CompatEventSerial => CompatEventFirstSerial + compatEvents.Count;
        internal IntVec3 CompatEventAt(int serial) => compatEvents[serial - CompatEventFirstSerial];

        private void AddCompatEvent(IntVec3 loc)
        {
            compatEvents.Add(loc);
            if (compatEvents.Count > 1024)
            {
                compatEvents.RemoveRange(0, 512);
                CompatEventFirstSerial += 512;
            }
        }

        public long BuildMs;
        public int ChunkCount;
        public int VertexCount;
        public int LastDrawnChunks;
        public int LastDrawCalls;

        public HorizonState(Map map, int generation)
        {
            this.map = map;
            Generation = generation;
            HorizonSettings s = EndlessHorizonMod.Settings;
            RingWidth = s.ringWidth;
            FogStart = s.fogStart;
        }

        public void EnsureBuilt()
        {
            if (built)
            {
                return;
            }
            using var work = BuildSteps().GetEnumerator();
            while (work.MoveNext()) { }
            EdgeRock.RedrawEdgeRock(map);
        }

        private IEnumerable<bool> BuildSteps()
        {
            built = true;
            RockPrint.Printed = RockPrint.Fallbacks = 0;
            var sw = Stopwatch.StartNew();
            Field = new HorizonField(map, RingWidth);
            buildPhase = "field";
            foreach (bool step in Field.BuildSteps(EndlessHorizonMod.Settings.useCoastNoise, CoastSourceOverride)) yield return step;
            yield return false;
            buildPhase = "mountains/tint";
            try
            {
                Mountains = new MountainMask(map, Field);
                Field.Mountains = Mountains;
                Field.FogTint = FogTint.Build(map);
            }
            catch (Exception ex)
            {
                Mountains = null;
                Log.Warning(EndlessHorizonMod.LogPrefix + "Mountain mask failed: " + ex);
            }
            yield return false;
            buildPhase = "ice";
            Field.BuildIce();
            seasons = new SeasonWatch(map, Field);

            yield return false;
            buildPhase = "outside light";
            glowThingDist = HorizonFog.CoveredFrom(FogStart, RingWidth, HorizonThings.HiddenAlpha);
            try
            {
                glowSignature = OutsideGlow.Signature(map);
                outsideGlow = new OutsideGlow(map, Field, Mountains, glowThingDist);
            }
            catch (Exception ex)
            {
                outsideGlow = null;
                Log.WarningOnce(EndlessHorizonMod.LogPrefix + "Outside light failed; the ring is lit by the sky only. " + ex, 0x45484C47);
            }
            yield return false;
            nextGlowCheck = Time.frameCount + 60;
            int size = Section.Size;
            int minX = FloorTo(-RingWidth, size), minZ = FloorTo(-RingWidth, size);
            int maxX = map.Size.x + RingWidth, maxZ = map.Size.z + RingWidth;
            CellRect inner = CellRect.WholeMap(map);
            bool depth = EndlessHorizonMod.Settings.drawWaterDepth;
            for (int x = minX; x < maxX; x += size)
            {
                for (int z = minZ; z < maxZ; z += size)
                {
                    var rect = new CellRect(x, z, size, size);
                    if (rect.minX > 0 && rect.minZ > 0 && rect.maxX < map.Size.x - 1 && rect.maxZ < map.Size.z - 1)
                    {
                        continue;
                    }
                    var chunk = new HorizonChunk(map, rect, this, false);
                    buildPhase = "terrain chunk";
                    terrainChunks.Add(chunk);
                    chunk.Regenerate();
                    yield return false;
                    VertexCount += chunk.VertexCount;
                    HorizonThingsChunk things;
                    buildPhase = "things cell/stage";
                    using (var work = HorizonThings.BeginBuild(map, rect, Field, FogStart, Mountains))
                    {
                        while (work.Steps.MoveNext()) yield return false;
                        PrintedThings += work.Printed;
                        FogCells += work.FogCells;
                        things = work.Result;
                        dynThings.Add(things);
                    }
                    yield return false;
                    buildPhase = "light chunk";
                    Mesh lightMesh = RingLighting.BuildChunk(map, rect, Field, Mountains, FogStart, outsideGlow);
                    if (lightMesh != null)
                    {
                        lightChunks.Add((rect, lightMesh));
                    }
                    yield return false;
                    chunkByCorner.Add((rect.minX, rect.minZ), dynRects.Count);
                    dynRects.Add(rect);
                    buildPhase = "snow chunk";
                    dynSnow.Add(EndlessHorizonMod.Settings.snow ? SnowLayer.BuildChunk(map, rect, Field, Mountains) : null);
                    yield return false;
                    if (things != null)
                    {
                        foreach (LayerSubMesh sm in things.subMeshes) VertexCount += sm.verts.Count;
                    }
                    if (depth && Field.AnyWaterIn(rect))
                    {
                        buildPhase = "depth chunk";
                        var depthChunk = new HorizonChunk(map, rect, this, true);
                        depthChunks.Add(depthChunk);
                        depthChunk.Regenerate();
                        if (!depthChunk.HasGeometry) { depthChunks.Remove(depthChunk); depthChunk.Dispose(); }
                        yield return false;
                    }
                }
            }
            ChunkCount = terrainChunks.Count(c => c.HasGeometry);
            if (Compat_WeatherReimagined.Installed)
            {
                try
                {
                    OpenSky = OpenSkyMesh.Build(map, Field, Mountains);
                }
                catch (Exception ex)
                {
                    Log.WarningOnce(EndlessHorizonMod.LogPrefix + "Open-sky mesh for weather overlays failed: " + ex, 0x4548534B);
                }
            }
            yield return false;
            buildPhase = "overlays";
            lighting = new HorizonLighting(map, RingWidth);
            Fog = new HorizonFog(map, RingWidth, FogStart);
            yield return false;
            buildPhase = "compat field/strip";
            foreach (bool step in CompatRegistry.BuildOutsideSteps(this, compatLayers)) yield return step;
            BuildMs = sw.ElapsedMilliseconds;
            Built = true;
            HorizonProbe.OnBuilt(this);
        }

        private static int FloorTo(int v, int step) => (int)Math.Floor(v / (double)step) * step;

        public void RequestRefresh()
        {
            refreshQueue.Clear();
            snowQueue.Clear();
            for (int i = 0; i < dynRects.Count; i++)
            {
                refreshQueue.Enqueue(i);
            }
            Refreshes++;
        }

        public void MarkSeamDirty(IntVec3 loc, bool surfaceChanged = true)
        {
            if (!built)
            {
                return;
            }
            if (replacement != null) replacementEdits.Add(loc);
            if (surfaceChanged)
            {
                AddCompatEvent(loc);
                if (replacement != null || Field?.BoundaryBaseChanged(loc) == true) RequestFullRebuild("boundary base terrain");
            }
            Field?.InvalidateOutsideFog();
            CellRect around = CellRect.CenteredOn(loc, 3);
            for (int x = FloorTo(around.minX, Section.Size); x <= around.maxX; x += Section.Size)
                for (int z = FloorTo(around.minZ, Section.Size); z <= around.maxZ; z += Section.Size)
                    if (chunkByCorner.TryGetValue((x, z), out int i) && seamQueued.Add(i)) seamQueue.Enqueue(i);
        }

        private void RequestSnowRefresh()
        {
            if (snowQueue.Count > 0)
            {
                return;
            }
            for (int i = 0; i < dynRects.Count; i++)
            {
                snowQueue.Enqueue(i);
            }
        }

        private readonly List<Action> pendingSwaps = new List<Action>();
        private readonly List<(int frame, Action dispose)> graveyard = new List<(int, Action)>();

        private void Bury(Action dispose) => graveyard.Add((Time.frameCount, dispose));

        private void SwapAndCollect()
        {
            if (pendingSwaps.Count > 0)
            {
                foreach (Action swap in pendingSwaps)
                {
                    swap();
                }
                pendingSwaps.Clear();
            }
            for (int i = graveyard.Count - 1; i >= 0; i--)
            {
                if (Time.frameCount - graveyard[i].frame >= 2)
                {
                    graveyard[i].dispose();
                    graveyard.RemoveAt(i);
                }
            }
        }

        private void RefreshStep(bool force)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                RefreshStepInner(force);
            }
            finally
            {
                double ms = sw.Elapsed.TotalMilliseconds;
                if (ms > 0.2) HorizonPerf.Record("seam/season rebuild (per frame)", ms);
            }
        }

        private const double RefreshFrameBudgetMs = 2.0;
        private IEnumerator<bool> refreshWork;
        private string refreshPhase = "refresh idle";

        private void RefreshStepInner(bool force)
        {
            var budget = Stopwatch.StartNew();
            SwapAndCollect();
            if (seasons != null)
            {
                seasons.Check(force, out bool snowChanged, out bool thingsChanged, out bool iceChanged);
                if (iceChanged && Field.Ice != null)
                {
                    List<IntVec3> changed = Field.Ice.Recalculate();
                    if (changed.Count > 0)
                    {
                        CompatSeamRevision++;
                        var affected = new HashSet<int>();
                        foreach (IntVec3 c in changed)
                            for (int dz = -1; dz <= 1; dz++)
                                for (int dx = -1; dx <= 1; dx++)
                                    if (chunkByCorner.TryGetValue((FloorTo(c.x + dx, Section.Size), FloorTo(c.z + dz, Section.Size)), out int i)) affected.Add(i);
                        foreach (int i in affected)
                        {
                            if (seamQueued.Add(i)) seamQueue.Enqueue(i);
                        }
                    }
                }
                if (thingsChanged && refreshQueue.Count == 0)
                {
                    RequestRefresh();
                }
                else if (snowChanged)
                {
                    RequestSnowRefresh();
                }
            }
            do
            {
                if (refreshWork == null)
                {
                    if (seamQueue.Count > 0)
                    {
                        int i = seamQueue.Dequeue();
                        seamQueued.Remove(i);
                        refreshWork = RebuildDynamicChunk(i, true).GetEnumerator();
                    }
                    else if (refreshQueue.Count > 0)
                        refreshWork = RebuildDynamicChunk(refreshQueue.Dequeue(), false).GetEnumerator();
                    else if (snowQueue.Count > 0)
                        refreshWork = RebuildSnowStep(snowQueue.Dequeue()).GetEnumerator();
                    else break;
                }
                long unit = Stopwatch.GetTimestamp();
                bool more;
                try { more = refreshWork.MoveNext(); }
                catch
                {
                    refreshWork.Dispose();
                    refreshWork = null;
                    throw;
                }
                finally
                {
                    double ms = (Stopwatch.GetTimestamp() - unit) * 1000.0 / Stopwatch.Frequency;
                    if (ms > 0.2) HorizonPerf.Record(refreshPhase, ms);
                }
                if (!more)
                {
                    refreshWork.Dispose();
                    refreshWork = null;
                }
            } while (budget.Elapsed.TotalMilliseconds < RefreshFrameBudgetMs);
        }

        private IEnumerable<bool> RebuildSnowStep(int i)
        {
            refreshPhase = "refresh snow unit";
            RebuildSnow(i);
            yield break;
        }

        private IEnumerable<bool> RebuildDynamicChunk(int i, bool seam)
        {
            CellRect rect = dynRects[i];
            HorizonChunk terrain = null, depth = null;
            HorizonThingsChunk things = null;
            Mesh snow = null;
            bool published = false;
            try
            {
                if (seam)
                {
                    refreshPhase = "refresh terrain unit";
                    terrain = new HorizonChunk(map, rect, this, false);
                    terrain.Regenerate();
                    yield return false;
                    refreshPhase = "refresh depth unit";
                    if (EndlessHorizonMod.Settings.drawWaterDepth && (depthChunks.Exists(d => d.Rect == rect) || Field.AnyWaterIn(rect)))
                    {
                        depth = new HorizonChunk(map, rect, this, true);
                        depth.Regenerate();
                    }
                    yield return false;
                }
                using (var work = HorizonThings.BeginBuild(map, rect, Field, FogStart, Mountains))
                {
                    refreshPhase = "refresh things cell/stage";
                    while (work.Steps.MoveNext()) yield return false;
                    things = work.Result;
                }
                refreshPhase = "refresh snow unit";
                snow = EndlessHorizonMod.Settings.snow ? SnowLayer.BuildChunk(map, rect, Field, Mountains) : null;
                yield return false;
                pendingSwaps.Add(() =>
                {
                    if (terrain != null)
                    {
                        HorizonChunk old = terrainChunks[i];
                        terrainChunks[i] = terrain;
                        Bury(old.Dispose);
                    }
                    if (depth != null)
                    {
                        int depthIndex = depthChunks.FindIndex(d => d.Rect == rect);
                        if (depthIndex >= 0)
                        {
                            HorizonChunk old = depthChunks[depthIndex];
                            depthChunks[depthIndex] = depth;
                            Bury(old.Dispose);
                        }
                        else depthChunks.Add(depth);
                    }
                    HorizonThingsChunk oldThings = dynThings[i];
                    dynThings[i] = things;
                    if (oldThings != null) Bury(oldThings.Dispose);
                    Mesh oldSnow = dynSnow[i];
                    dynSnow[i] = snow;
                    if (oldSnow != null) Bury(() => UnityEngine.Object.Destroy(oldSnow));
                });
                published = true;
                if (seam) SeamRebuilds++;
            }
            finally
            {
                if (!published)
                {
                    terrain?.Dispose();
                    depth?.Dispose();
                    things?.Dispose();
                    if (snow != null) UnityEngine.Object.Destroy(snow);
                }
            }
        }

        private readonly Queue<int> glowQueue = new Queue<int>();
        private readonly HashSet<int> glowQueued = new HashSet<int>();
        private Color32[] edgeGlowSeen;
        private const double GlowFrameBudgetMs = 2.0;

        private void GlowStep()
        {
            if (outsideGlow == null) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (Time.frameCount >= nextGlowCheck)
                {
                    nextGlowCheck = Time.frameCount + 60;
                    int sig = OutsideGlow.Signature(map);
                    if (sig != glowSignature)
                    {
                        glowSignature = sig;
                        OutsideGlow old = outsideGlow;
                        outsideGlow = new OutsideGlow(map, Field, Mountains, glowThingDist, old);
                        QueueChangedLight(old, outsideGlow);
                    }
                }
                while (glowQueue.Count > 0)
                {
                    int index = glowQueue.Dequeue();
                    glowQueued.Remove(index);
                    Mesh fresh = RingLighting.BuildChunk(map, lightChunks[index].rect, Field, Mountains, FogStart, outsideGlow);
                    pendingSwaps.Add(() =>
                    {
                        Mesh old = lightChunks[index].mesh;
                        lightChunks[index] = (lightChunks[index].rect, fresh ?? old);
                        if (fresh != null && old != null) Bury(() => UnityEngine.Object.Destroy(old));
                    });
                    if (sw.Elapsed.TotalMilliseconds >= GlowFrameBudgetMs) break;
                }
            }
            catch (Exception ex)
            {
                outsideGlow = null;
                Log.WarningOnce(EndlessHorizonMod.LogPrefix + "Updating the outside light failed; it stays as it was. " + ex, 0x45484C55);
            }
            finally
            {
                double ms = sw.Elapsed.TotalMilliseconds;
                if (ms > 0.2) HorizonPerf.Record("outside light (per frame)", ms);
            }
        }

        private void QueueChangedLight(OutsideGlow before, OutsideGlow after)
        {
            int w = map.Size.x, h = map.Size.z;
            int Perimeter(int x, int z) => z == 0 ? x : z == h - 1 ? w + x : x == 0 ? 2 * w + z : 2 * w + h + z;
            var now = new Color32[2 * w + 2 * h];
            for (int x = 0; x < w; x++)
            {
                now[Perimeter(x, 0)] = map.glowGrid.VisualGlowAt(map.cellIndices.CellToIndex(x, 0));
                now[Perimeter(x, h - 1)] = map.glowGrid.VisualGlowAt(map.cellIndices.CellToIndex(x, h - 1));
            }
            for (int z = 1; z < h - 1; z++)
            {
                now[Perimeter(0, z)] = map.glowGrid.VisualGlowAt(map.cellIndices.CellToIndex(0, z));
                now[Perimeter(w - 1, z)] = map.glowGrid.VisualGlowAt(map.cellIndices.CellToIndex(w - 1, z));
            }
            bool Same(Color32 a, Color32 b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;
            for (int i = 0; i < lightChunks.Count; i++)
            {
                CellRect rect = lightChunks[i].rect.ExpandedBy(1);
                bool changed = edgeGlowSeen == null;
                foreach (IntVec3 c in rect)
                {
                    if (changed) break;
                    bool inside = c.x >= 0 && c.z >= 0 && c.x < w && c.z < h;
                    if (!inside) changed = !Same(before.At(c.x, c.z), after.At(c.x, c.z));
                    else if (c.x == 0 || c.z == 0 || c.x == w - 1 || c.z == h - 1)
                    {
                        int k = Perimeter(c.x, c.z);
                        changed = !Same(edgeGlowSeen[k], now[k]);
                    }
                }
                if (changed && glowQueued.Add(i)) glowQueue.Enqueue(i);
            }
            edgeGlowSeen = now;
        }

        private void RebuildSnow(int i)
        {
            Mesh fresh = EndlessHorizonMod.Settings.snow ? SnowLayer.BuildChunk(map, dynRects[i], Field, Mountains) : null;
            pendingSwaps.Add(() =>
            {
                Mesh old = dynSnow[i];
                dynSnow[i] = fresh;
                if (old != null) Bury(() => UnityEngine.Object.Destroy(old));
            });
        }

        public void Draw()
        {
            EnsureBuilt();
            CellRect view = Find.CameraDriver.CurrentViewRect.ExpandedBy(2);
            int drawn = 0, calls = 0;
            for (int i = 0; i < terrainChunks.Count; i++)
            {
                HorizonChunk c = terrainChunks[i];
                if (c.HasGeometry && c.Rect.Overlaps(view))
                {
                    c.DrawLayer();
                    drawn++;
                    calls += c.subMeshes.Count;
                }
            }
            for (int i = 0; i < depthChunks.Count; i++)
            {
                HorizonChunk c = depthChunks[i];
                if (c.Rect.Overlaps(view))
                {
                    c.DrawLayer();
                    calls += c.subMeshes.Count;
                }
            }
            for (int i = 0; i < compatLayers.Count; i++)
            {
                calls += compatLayers[i].Draw(view);
            }
            if (!Replacing)
            {
                RefreshStep(false);
                GlowStep();
            }
            else SwapAndCollect();
            if (retiredWork != null)
            {
                var cleanup = Stopwatch.StartNew();
                do
                {
                    if (!retiredWork.MoveNext()) { retiredWork.Dispose(); retiredWork = null; break; }
                } while (cleanup.Elapsed.TotalMilliseconds < 1.0);
                HorizonPerf.Record("ring retirement (per frame)", cleanup.Elapsed.TotalMilliseconds);
            }
            CellRect thingView = view.ExpandedBy(3);
            for (int i = 0; i < dynRects.Count; i++)
            {
                if (!dynRects[i].Overlaps(thingView))
                {
                    continue;
                }
                if (dynSnow[i] != null)
                {
                    Graphics.DrawMesh(dynSnow[i], Matrix4x4.identity, MatBases.Snow, 0);
                    calls++;
                }
                if (dynThings[i] != null)
                {
                    calls += dynThings[i].Draw();
                }
            }
            if (!Replacing && Field.PendingCoastSearch != null && Field.PendingCoastSearch.Step(2f))
            {
                Field.PendingCoastSearch = null;
                RequestFullRebuild("coast search completed");
            }
            HorizonSettings s = EndlessHorizonMod.Settings;
            if (s.lighting && !HorizonDebug.HideLighting)
            {
                lighting.Draw();
                calls++;
                for (int i = 0; i < lightChunks.Count; i++)
                {
                    if (lightChunks[i].rect.Overlaps(view))
                    {
                        Graphics.DrawMesh(lightChunks[i].mesh, Matrix4x4.identity, MatBases.LightOverlay, 0);
                        calls++;
                    }
                }
            }
            bool haze = s.fog && !HorizonDebug.HideFog;
            bool clouds = s.clouds && !HorizonDebug.HideClouds;
            if (haze)
            {
                Fog.Update(clouds);
                Fog.Draw();
                calls++;
            }
            LastDrawnChunks = drawn;
            LastDrawCalls = calls;
        }

        public float AlphaAt(float x, float z)
        {
            return x >= -RingWidth && z >= -RingWidth && x <= map.Size.x + RingWidth && z <= map.Size.z + RingWidth ? 1f : 0f;
        }

        public void Dispose()
        {
            foreach (bool step in DisposeSteps()) { }
        }

        internal IEnumerable<bool> DisposeSteps()
        {
            if (retiredWork != null)
            {
                while (retiredWork.MoveNext()) yield return false;
                retiredWork.Dispose();
                retiredWork = null;
            }
            initialWork?.Dispose();
            initialWork = null;
            replacement?.Dispose();
            replacement = null;
            refreshWork?.Dispose();
            refreshWork = null;
            bool wasBuilt = Built;
            Built = false;
            if (wasBuilt && !map.Disposed && HorizonRegistry.Peek(map) == this)
            {
                EdgeRock.RedrawEdgeRock(map);
            }
            foreach (Action swap in pendingSwaps)
            {
                swap();
                yield return false;
            }
            pendingSwaps.Clear();
            foreach ((int _, Action dispose) in graveyard)
            {
                dispose();
                yield return false;
            }
            graveyard.Clear();
            foreach (IOutsideCompatLayer layer in compatLayers)
            {
                layer.Dispose();
                yield return false;
            }
            compatLayers.Clear();
            if (OpenSky != null)
            {
                UnityEngine.Object.Destroy(OpenSky);
                OpenSky = null;
            }
            seamQueue.Clear();
            seamQueued.Clear();
            foreach (HorizonChunk c in terrainChunks)
            {
                c.Dispose();
                yield return false;
            }
            foreach (HorizonChunk c in depthChunks)
            {
                c.Dispose();
                yield return false;
            }
            foreach (HorizonThingsChunk c in dynThings)
            {
                c?.Dispose();
                yield return false;
            }
            foreach (Mesh m in dynSnow)
            {
                if (m != null) UnityEngine.Object.Destroy(m);
                yield return false;
            }
            terrainChunks.Clear();
            depthChunks.Clear();
            dynRects.Clear();
            dynThings.Clear();
            dynSnow.Clear();
            refreshQueue.Clear();
            snowQueue.Clear();
            lighting?.Dispose();
            foreach ((CellRect _, Mesh mesh) in lightChunks)
            {
                UnityEngine.Object.Destroy(mesh);
                yield return false;
            }
            lightChunks.Clear();
            Fog?.Dispose();
        }
    }

    public sealed partial class HorizonField
    {
        private readonly Map map;
        private readonly int r;
        public int RingWidth => r;
        private readonly int width, height;
        private readonly TerrainDef[] cells;
        private readonly int[] samples;
        private readonly int[] rawSamples;

        public string CoastMode = "none";
        public string CoastOrigin = "none";
        public int CoastCells;
        public RiverExtension River;
        public int RiverCells;
        private readonly bool[] fogSnapshot;
        public CoastSearch PendingCoastSearch;
        private static bool coastWorkerLogged;
        public CoastFallback CoastFallback;
        private bool[] nearRiver;

        public HorizonField(Map map, int ringWidth)
        {
            this.map = map;
            r = ringWidth;
            width = map.Size.x + 2 * r;
            height = map.Size.z + 2 * r;
            cells = new TerrainDef[width * height];
            samples = new int[width * height];
            rawSamples = new int[width * height];
            fogSnapshot = new bool[map.cellIndices.NumGridCells];
            for (int i = 0; i < fogSnapshot.Length; i++)
            {
                fogSnapshot[i] = map.fogGrid.IsFogged(i);
            }
            for (int i = 0; i < samples.Length; i++) samples[i] = rawSamples[i] = -1;
        }

        private int Index(int x, int z) => (z + r) * width + (x + r);

        public bool Covers(IntVec3 c) => c.x >= -r && c.z >= -r && c.x < map.Size.x + r && c.z < map.Size.z + r;

        public TerrainDef At(IntVec3 c) => Covers(c) ? cells[Index(c.x, c.z)] : null;

        public bool AnyWaterIn(CellRect rect)
        {
            foreach (IntVec3 c in rect)
            {
                TerrainDef t = c.InBounds(map) ? null : At(c);
                if (t != null && t.IsWater)
                {
                    return true;
                }
            }
            return false;
        }

        public void Build(bool useCoast, CoastNoise.Source overrideSource)
        {
            foreach (bool step in BuildSteps(useCoast, overrideSource)) { }
        }

        internal IEnumerable<bool> BuildSteps(bool useCoast, CoastNoise.Source overrideSource)
        {
            CoastNoise.Source coast = null;
            if (useCoast)
            {
                coast = overrideSource ?? CoastNoise.Captured(map);
                TileMutatorWorker_Coast worker = CoastNoise.CoastWorkerOf(map);
                if (coast == null && worker != null)
                {
                    CoastSearch search = CoastSearch.For(map, worker);
                    if (search.Done && search.Result != null)
                    {
                        coast = search.Result;
                        CoastOrigin = search.Report;
                    }
                    else
                    {
                        PendingCoastSearch = search.Done ? null : search;
                        CoastFallback = CoastFallback.TryCreate(map);
                        CoastOrigin = (search.Done ? search.Report : "searching") + " → " + (CoastFallback?.Report ?? "no fallback");
                    }
                }
                else
                {
                    CoastOrigin = coast?.origin ?? "none";
                }
            }
            bool delta = map.TileInfo?.Mutators?.Any(m => m.defName.Contains("Delta")) ?? false;
            try
            {
                River = new RiverExtension(map, r);
                River.Build(delta);
            }
            catch (Exception ex)
            {
                River = null;
                Log.Warning(EndlessHorizonMod.LogPrefix + "River extension failed: " + ex);
            }
            yield return false;
            ChooseGenerator();
            yield return false;
            bool workerMode = true;
            for (int z = -r; z < map.Size.z + r; z++)
            {
                IDisposable scope = coast != null ? new CoastNoise.Scope(coast) : null;
                try
                {
                    for (int x = -r; x < map.Size.x + r; x++)
                    {
                        var c = new IntVec3(x, 0, z);
                        if (c.InBounds(map))
                        {
                            continue;
                        }
                        TerrainDef edge = OutsideTerrain(c, out int sample, out int rawSample);
                        TerrainDef t = edge;
                        if (coast != null)
                        {
                            TerrainDef ct = null;
                            if (workerMode)
                            {
                                try
                                {
                                    ct = CoastNoise.WorkerTerrainAt(coast, c, map);
                                }
                                catch (Exception ex)
                                {
                                    workerMode = false;
                                    CoastMode = "threshold (worker threw: " + ex.GetType().Name + ")";
                                    if (!coastWorkerLogged)
                                    {
                                        coastWorkerLogged = true;
                                        Log.Message(EndlessHorizonMod.LogPrefix + "Coast worker failed for an outside cell; using the threshold fallback (logged once per session). " + ex);
                                    }
                                }
                            }
                            if (!workerMode)
                            {
                                ct = ThresholdTerrain(coast, c);
                            }
                            if (ct != null)
                            {
                                t = ct;
                                CoastCells++;
                            }
                            else if (MirroredSea(x, z, edge))
                            {
                                t = MapGenUtility.BeachTerrainAt(Clamp(c), map) ?? TerrainDefOf.Sand;
                            }
                        }
                        else if (CoastFallback != null)
                        {
                            float dist = Mathf.Max(Mathf.Max(-x, x - map.Size.x + 1), Mathf.Max(-z, z - map.Size.z + 1));
                            bool mirroredSea = MirroredSea(x, z, edge);
                            float sea = CoastFallback.SeaScore(c, dist, mirroredSea);
                            IntVec3 near = Clamp(c);
                            if (sea > 0.1f) t = MapGenUtility.DeepOceanWaterTerrainAt(near, map);
                            else if (sea > 0f) t = MapGenUtility.ShallowOceanWaterTerrainAt(near, map);
                            else if (sea > -0.08f) t = MapGenUtility.BeachTerrainAt(near, map) ?? TerrainDefOf.Sand;
                            else if (edge.IsOcean) t = Dominant(near);
                            if (t != edge && t.IsWater) CoastCells++;
                        }
                        if (River != null && !(t.IsOcean || (coast != null && t.IsWater && !edge.IsWater)))
                        {
                            TerrainDef rt = River.TerrainAt(c, t, Clamp(c));
                            if (rt != null)
                            {
                                t = rt;
                                RiverCells++;
                            }
                        }
                        cells[Index(x, z)] = t;
                        samples[Index(x, z)] = t == edge ? sample : -1;
                        rawSamples[Index(x, z)] = t.IsOcean && !edge.IsOcean ? -1 : rawSample;
                        if (t.IsWater && samples[Index(x, z)] < 0)
                        {
                            int ws = WaterSource(c, t);
                            samples[Index(x, z)] = ws;
                            if (rawSamples[Index(x, z)] < 0) rawSamples[Index(x, z)] = ws;
                        }
                    }
                }
                finally { scope?.Dispose(); }
                yield return false;
            }
            if (coast != null && workerMode)
            {
                CoastMode = "worker";
            }
            if (Generator != null)
            {
                ApplyRiverPaint();
            }
            yield return false;
            ApplyCoastBoundary(coast);
            yield return false;
            CaptureBaseBoundary();
        }

        private bool MirroredSea(int x, int z, TerrainDef edge)
        {
            int w = map.Size.x, h = map.Size.z;
            float dist = Mathf.Max(Mathf.Max(-x, x - w + 1), Mathf.Max(-z, z - h + 1));
            if (dist >= 14f) return edge.IsOcean;
            return map.terrainGrid.BaseTerrainAt(new IntVec3(Mirror(x, w), 0, Mirror(z, h)))?.IsOcean ?? false;
        }

        private TerrainDef ThresholdTerrain(CoastNoise.Source coast, IntVec3 c)
        {
            float v = CoastNoise.Value(coast, c);
            IntVec3 near = Clamp(c);
            if (v < 0.4f)
            {
                return MapGenUtility.DeepOceanWaterTerrainAt(near, map);
            }
            if (v < 0.5f)
            {
                return MapGenUtility.ShallowOceanWaterTerrainAt(near, map);
            }
            if (v < 0.6f)
            {
                return MapGenUtility.BeachTerrainAt(near, map);
            }
            return null;
        }

        private IntVec3 Clamp(IntVec3 c)
        {
            return new IntVec3(Mathf.Clamp(c.x, 0, map.Size.x - 1), 0, Mathf.Clamp(c.z, 0, map.Size.z - 1));
        }

        private Verse.Noise.Perlin warpX, warpZ;
        private readonly Dictionary<int, TerrainDef> dominantCache = new Dictionary<int, TerrainDef>();

        private static bool Usable(TerrainDef t) => t != null && t.natural && !t.bridge && !t.dontRender;

        public int SampleAt(IntVec3 c) => Covers(c) ? samples[Index(c.x, c.z)] : -1;

        public bool SourceFogged(int index) => index >= 0 && index < fogSnapshot.Length && fogSnapshot[index];

        public int RawSampleAt(IntVec3 c) => Covers(c) ? rawSamples[Index(c.x, c.z)] : -1;

        private Verse.Noise.Perlin macroX, macroZ;
        private bool[] developed;
        public int WildSubstitutions;

        private bool Developed(int index)
        {
            if (developed == null)
            {
                int n = map.cellIndices.NumGridCells;
                developed = new bool[n];
                Area home = map.areaManager.Home;
                for (int i = 0; i < n; i++)
                {
                    IntVec3 c = map.cellIndices.IndexToCell(i);
                    bool dev = home != null && home[i];
                    if (!dev)
                    {
                        Building b = map.edificeGrid.InnerArray[i];
                        dev = b != null && !b.def.mineable;
                    }
                    if (!dev)
                    {
                        TerrainDef top = map.terrainGrid.TerrainAt(i);
                        dev = top != map.terrainGrid.BaseTerrainAt(c) || (top != null && !top.natural);
                    }
                    if (!dev)
                    {
                        RoofDef roof = map.roofGrid.RoofAt(i);
                        dev = roof != null && !roof.isNatural;
                    }
                    if (!dev)
                    {
                        dev = map.zoneManager.ZoneAt(c) != null;
                    }
                    if (!dev)
                    {
                        List<Thing> things = map.thingGrid.ThingsListAtFast(i);
                        for (int k = 0; k < things.Count && !dev; k++)
                        {
                            dev = things[k] is Plant p && p.sown;
                        }
                    }
                    developed[i] = dev;
                }
            }
            return developed[index];
        }

        private static readonly IntVec2[] WildOffsets =
        {
            new IntVec2(40, 0), new IntVec2(-40, 0), new IntVec2(0, 40), new IntVec2(0, -40),
            new IntVec2(80, 0), new IntVec2(-80, 0), new IntVec2(0, 80), new IntVec2(0, -80),
            new IntVec2(40, 40), new IntVec2(-40, 40), new IntVec2(40, -40), new IntVec2(-40, -40),
            new IntVec2(120, 0), new IntVec2(-120, 0), new IntVec2(0, 120), new IntVec2(0, -120),
        };

        private bool TryWild(ref int sx, ref int sz)
        {
            int w = map.Size.x, h = map.Size.z;
            foreach (IntVec2 o in WildOffsets)
            {
                int x = sx + o.x, z = sz + o.z;
                if (x < 0 || z < 0 || x >= w || z >= h)
                {
                    continue;
                }
                if (!Developed(map.cellIndices.CellToIndex(x, z)))
                {
                    sx = x;
                    sz = z;
                    return true;
                }
            }
            return false;
        }

        private TerrainDef OutsideTerrain(IntVec3 c, out int sample, out int rawSample)
        {
            if (warpX == null)
            {
                int seed = Gen.HashCombineInt(Find.World.info.Seed, map.Tile.GetHashCode()) ^ 0x45484F52;
                warpX = new Verse.Noise.Perlin(0.045, 2.0, 0.5, 3, seed, Verse.Noise.QualityMode.Medium);
                warpZ = new Verse.Noise.Perlin(0.045, 2.0, 0.5, 3, seed + 1, Verse.Noise.QualityMode.Medium);
                macroX = new Verse.Noise.Perlin(0.012, 2.0, 0.5, 2, seed + 2, Verse.Noise.QualityMode.Medium);
                macroZ = new Verse.Noise.Perlin(0.012, 2.0, 0.5, 2, seed + 3, Verse.Noise.QualityMode.Medium);
            }
            if (Generator != null)
            {
                TerrainDef g = GeneratorTerrain(c, out sample, out rawSample);
                if (g != null)
                {
                    return g;
                }
            }
            int w = map.Size.x, h = map.Size.z;
            int dx = c.x < 0 ? -c.x : (c.x >= w ? c.x - w + 1 : 0);
            int dz = c.z < 0 ? -c.z : (c.z >= h ? c.z - h + 1 : 0);
            float d = Math.Max(dx, dz);
            float amp = Mathf.Min(12f, d * 0.4f);
            float macro = Mathf.Min(30f, d * 0.75f);
            float fx = c.x + (float)warpX.GetValue(c.x, 0, c.z) * amp + (float)macroX.GetValue(c.x, 0, c.z) * macro;
            float fz = c.z + (float)warpZ.GetValue(c.x, 0, c.z) * amp + (float)macroZ.GetValue(c.x, 0, c.z) * macro;
            int sx = Mirror(Mathf.RoundToInt(fx), w);
            int sz = Mirror(Mathf.RoundToInt(fz), h);
            if (Developed(map.cellIndices.CellToIndex(sx, sz)))
            {
                if (!TryWild(ref sx, ref sz))
                {
                    sample = rawSample = -1;
                    return Dominant(Clamp(c));
                }
                WildSubstitutions++;
            }
            TerrainDef t = map.terrainGrid.BaseTerrainAt(new IntVec3(sx, 0, sz));
            sample = map.cellIndices.CellToIndex(sx, sz);
            rawSample = sample;
            if (!Usable(t) || t.IsRiver || NearRiver(sx, sz))
            {
                t = Dominant(Clamp(c));
                sample = -1;
            }
            return t;
        }

        private bool NearRiver(int x, int z)
        {
            if (nearRiver == null)
            {
                int w = map.Size.x, h = map.Size.z;
                nearRiver = new bool[w * h];
                int radius = Mathf.Clamp((map.Biome?.riverbankSizeRange.max ?? 4) + 2, 3, 10);
                for (int cz = 0; cz < h; cz++)
                {
                    for (int cx = 0; cx < w; cx++)
                    {
                        TerrainDef t = map.terrainGrid.BaseTerrainAt(new IntVec3(cx, 0, cz));
                        if (t == null || !t.IsRiver)
                        {
                            continue;
                        }
                        for (int dz = -radius; dz <= radius; dz++)
                        {
                            int zz = cz + dz;
                            if (zz < 0 || zz >= h) continue;
                            for (int dx = -radius; dx <= radius; dx++)
                            {
                                int xx = cx + dx;
                                if (xx >= 0 && xx < w) nearRiver[zz * w + xx] = true;
                            }
                        }
                    }
                }
            }
            return nearRiver[z * map.Size.x + x];
        }

        private static int Mirror(int v, int size)
        {
            if (v < 0) v = -v - 1;
            else if (v >= size) v = 2 * size - v - 1;
            return Mathf.Clamp(v, 0, size - 1);
        }

        private TerrainDef Dominant(IntVec3 edgeCell)
        {
            int key = map.cellIndices.CellToIndex(edgeCell);
            if (dominantCache.TryGetValue(key, out TerrainDef cached))
            {
                return cached;
            }
            var counts = new Dictionary<TerrainDef, int>();
            foreach (IntVec3 n in CellRect.CenteredOn(edgeCell, 10).ClipInsideMap(map))
            {
                TerrainDef t = map.terrainGrid.BaseTerrainAt(n);
                if (Usable(t) && !t.IsWater && !NearRiver(n.x, n.z) && !Developed(map.cellIndices.CellToIndex(n)))
                {
                    counts.TryGetValue(t, out int k);
                    counts[t] = k + 1;
                }
            }
            TerrainDef best = null;
            int bestCount = 0;
            foreach (KeyValuePair<TerrainDef, int> kv in counts)
            {
                if (kv.Value > bestCount)
                {
                    best = kv.Key;
                    bestCount = kv.Value;
                }
            }
            best ??= map.Biome?.TerrainForAffordance(TerrainAffordanceDefOf.Light) ?? TerrainDefOf.Soil;
            dominantCache[key] = best;
            return best;
        }

        private TerrainDef EdgeTerrain(IntVec3 outside)
        {
            IntVec3 c = Clamp(outside);
            IntVec3 inward = new IntVec3(Math.Sign(map.Size.x / 2 - c.x), 0, Math.Sign(map.Size.z / 2 - c.z));
            TerrainGrid grid = map.terrainGrid;
            for (int k = 0; k < 12; k++)
            {
                TerrainDef t = grid.BaseTerrainAt(c);
                if (t != null && t.natural && !t.bridge && !t.dontRender)
                {
                    return t;
                }
                IntVec3 next = c + inward;
                if (!next.InBounds(map))
                {
                    break;
                }
                c = next;
            }
            return map.Biome?.TerrainForAffordance(TerrainAffordanceDefOf.Light) ?? TerrainDefOf.Soil;
        }
    }

    public sealed class HorizonChunk : IDisposable
    {
        public readonly CellRect Rect;
        private readonly Map map;
        private readonly HorizonState state;
        private readonly bool depthPass;
        public readonly List<LayerSubMesh> subMeshes = new List<LayerSubMesh>();
        public int VertexCount;
        public bool HasGeometry;

        public HorizonChunk(Map map, CellRect rect, HorizonState state, bool depthPass)
        {
            this.map = map;
            Rect = rect;
            this.state = state;
            this.depthPass = depthPass;
        }

        private Map Map => map;

        private LayerSubMesh GetSubMesh(Material material)
        {
            if (material == null)
            {
                return null;
            }
            for (int i = 0; i < subMeshes.Count; i++)
            {
                if (subMeshes[i].material == material)
                {
                    return subMeshes[i];
                }
            }
            var sm = new LayerSubMesh(new Mesh(), material);
            subMeshes.Add(sm);
            return sm;
        }

        public void DrawLayer()
        {
            for (int i = 0; i < subMeshes.Count; i++)
            {
                LayerSubMesh sm = subMeshes[i];
                if (sm.finalized && !sm.disabled)
                {
                    Graphics.DrawMesh(sm.mesh, Matrix4x4.identity, sm.material, sm.renderLayer);
                }
            }
        }

        public void Dispose()
        {
            foreach (LayerSubMesh sm in subMeshes)
            {
                UnityEngine.Object.Destroy(sm.mesh);
            }
            subMeshes.Clear();
        }

        private CellTerrain TerrainAt(IntVec3 c, bool asNeighbor)
        {
            Map m = Map;
            if (c.InBounds(m))
            {
                var ct = new CellTerrain(m.terrainGrid.TerrainAt(c), c.IsPolluted(m), m.snowGrid.GetDepth(c), c.GetSandDepth(m), m.terrainGrid.ColorAt(c));
                if (asNeighbor)
                {
                    Thing edifice = c.GetEdifice(m);
                    if (edifice != null && edifice.def.coversFloor)
                    {
                        ct.def = TerrainDefOf.Underwall;
                    }
                }
                return ct;
            }
            return new CellTerrain(state.Field.TopAt(c), false, 0f, 0f, null);
        }

        private Material MaterialFor(CellTerrain ct)
        {
            if (ct.def == null)
            {
                return null;
            }
            if (depthPass)
            {
                return ct.def.waterDepthMaterial;
            }
            bool polluted = ct.polluted && ct.snowCoverage < 0.4f && ct.sandCoverage < 0.4f && ct.def.graphicPolluted != BaseContent.BadGraphic;
            return Map.terrainGrid.GetMaterial(ct.def, polluted, ct.color);
        }

        public static bool SeamBlend = true;

        private void AddSeamFans(CellTerrain[] around, HashSet<CellTerrain> blends, bool[] fanOn)
        {
            if (!SeamBlend)
            {
                return;
            }
            Map m = Map;
            int w = m.Size.x, h = m.Size.z;
            foreach (IntVec3 c in Rect)
            {
                if (!c.InBounds(m) || (c.x != 0 && c.z != 0 && c.x != w - 1 && c.z != h - 1))
                {
                    continue;
                }
                CellTerrain self = TerrainAt(c, false);
                if (self.def == null || m.terrainGrid.FoundationAt(c) != null)
                {
                    continue;
                }
                blends.Clear();
                var outside = new bool[8];
                for (int i = 0; i < 8; i++)
                {
                    IntVec3 nc = c + GenAdj.AdjacentCellsAroundBottom[i];
                    outside[i] = !nc.InBounds(m) && state.Field.Covers(nc);
                    if (nc.InBounds(m) || outside[i])
                    {
                        around[i] = TerrainAt(nc, true);
                        if (around[i].def == null)
                        {
                            around[i] = self;
                        }
                    }
                    else
                    {
                        around[i] = self;
                    }
                    CellTerrain other = around[i];
                    if (outside[i] && !other.Equals(self) && other.def.edgeType != TerrainDef.TerrainEdgeType.Hard
                        && other.def.renderPrecedence >= self.def.renderPrecedence)
                    {
                        blends.Add(other);
                    }
                }
                foreach (CellTerrain blend in blends)
                {
                    LayerSubMesh fm = GetSubMesh(MaterialFor(blend));
                    if (fm == null)
                    {
                        continue;
                    }
                    for (int j = 0; j < 8; j++)
                    {
                        fanOn[j] = false;
                    }
                    for (int k = 0; k < 8; k++)
                    {
                        if (!outside[k] || !around[k].Equals(blend))
                        {
                            continue;
                        }
                        if (k % 2 == 0)
                        {
                            fanOn[(k + 7) % 8] = true;
                            fanOn[k] = true;
                            fanOn[(k + 1) % 8] = true;
                        }
                        else
                        {
                            fanOn[k] = true;
                        }
                    }
                    int n = fm.verts.Count;
                    fm.verts.Add(new Vector3(c.x + 0.5f, 0f, c.z));
                    fm.verts.Add(new Vector3(c.x, 0f, c.z));
                    fm.verts.Add(new Vector3(c.x, 0f, c.z + 0.5f));
                    fm.verts.Add(new Vector3(c.x, 0f, c.z + 1));
                    fm.verts.Add(new Vector3(c.x + 0.5f, 0f, c.z + 1));
                    fm.verts.Add(new Vector3(c.x + 1, 0f, c.z + 1));
                    fm.verts.Add(new Vector3(c.x + 1, 0f, c.z + 0.5f));
                    fm.verts.Add(new Vector3(c.x + 1, 0f, c.z));
                    fm.verts.Add(new Vector3(c.x + 0.5f, 0f, c.z + 0.5f));
                    for (int l = 0; l < 8; l++)
                    {
                        fm.colors.Add(WithAlpha(fanOn[l], 1f));
                    }
                    fm.colors.Add(WithAlpha(false, 1f));
                    for (int t = 0; t < 8; t++)
                    {
                        fm.tris.Add(n + t);
                        fm.tris.Add(n + (t + 1) % 8);
                        fm.tris.Add(n + 8);
                    }
                }
            }
        }

        private static Color32 WithAlpha(bool on, float alpha)
        {
            return new Color32(255, 255, 255, on ? (byte)Mathf.RoundToInt(alpha * 255f) : (byte)0);
        }

        public void Regenerate()
        {
            Dispose();
            Map m = Map;
            float y = AltitudeLayer.Terrain.AltitudeFor();
            var around = new CellTerrain[8];
            var blends = new HashSet<CellTerrain>();
            var fanOn = new bool[8];
            foreach (IntVec3 c in Rect)
            {
                if (c.InBounds(m) || !state.Field.Covers(c))
                {
                    continue;
                }
                float a00 = state.AlphaAt(c.x, c.z), a01 = state.AlphaAt(c.x, c.z + 1), a11 = state.AlphaAt(c.x + 1, c.z + 1), a10 = state.AlphaAt(c.x + 1, c.z);
                if (a00 <= 0f && a01 <= 0f && a11 <= 0f && a10 <= 0f)
                {
                    continue;
                }
                CellTerrain self = TerrainAt(c, false);
                if (self.def == null)
                {
                    continue;
                }
                LayerSubMesh sm = GetSubMesh(self.def.dontRender ? (depthPass ? null : MatBases.ShadowMask) : MaterialFor(self));
                if (sm != null)
                {
                    int n = sm.verts.Count;
                    sm.verts.Add(new Vector3(c.x, y, c.z));
                    sm.verts.Add(new Vector3(c.x, y, c.z + 1));
                    sm.verts.Add(new Vector3(c.x + 1, y, c.z + 1));
                    sm.verts.Add(new Vector3(c.x + 1, y, c.z));
                    sm.colors.Add(WithAlpha(true, a00));
                    sm.colors.Add(WithAlpha(true, a01));
                    sm.colors.Add(WithAlpha(true, a11));
                    sm.colors.Add(WithAlpha(true, a10));
                    sm.tris.Add(n); sm.tris.Add(n + 1); sm.tris.Add(n + 2);
                    sm.tris.Add(n); sm.tris.Add(n + 2); sm.tris.Add(n + 3);
                }

                blends.Clear();
                for (int i = 0; i < 8; i++)
                {
                    IntVec3 nc = c + GenAdj.AdjacentCellsAroundBottom[i];
                    if (!nc.InBounds(m) && !state.Field.Covers(nc))
                    {
                        around[i] = self;
                        continue;
                    }
                    CellTerrain other = TerrainAt(nc, true);
                    if (other.def == null)
                    {
                        around[i] = self;
                        continue;
                    }
                    around[i] = other;
                    bool foundationHere = false;
                    bool foundationThere = nc.InBounds(m) && m.terrainGrid.FoundationAt(nc) != null;
                    if (!other.Equals(self) && other.def.edgeType != TerrainDef.TerrainEdgeType.Hard && !foundationHere && !foundationThere
                        && other.def.renderPrecedence >= self.def.renderPrecedence)
                    {
                        blends.Add(other);
                    }
                }
                float midAlpha = state.AlphaAt(c.x + 0.5f, c.z + 0.5f);
                foreach (CellTerrain blend in blends)
                {
                    LayerSubMesh fm = GetSubMesh(MaterialFor(blend));
                    if (fm == null)
                    {
                        continue;
                    }
                    int n = fm.verts.Count;
                    fm.verts.Add(new Vector3(c.x + 0.5f, 0f, c.z));
                    fm.verts.Add(new Vector3(c.x, 0f, c.z));
                    fm.verts.Add(new Vector3(c.x, 0f, c.z + 0.5f));
                    fm.verts.Add(new Vector3(c.x, 0f, c.z + 1));
                    fm.verts.Add(new Vector3(c.x + 0.5f, 0f, c.z + 1));
                    fm.verts.Add(new Vector3(c.x + 1, 0f, c.z + 1));
                    fm.verts.Add(new Vector3(c.x + 1, 0f, c.z + 0.5f));
                    fm.verts.Add(new Vector3(c.x + 1, 0f, c.z));
                    fm.verts.Add(new Vector3(c.x + 0.5f, 0f, c.z + 0.5f));
                    for (int j = 0; j < 8; j++)
                    {
                        fanOn[j] = false;
                    }
                    for (int k = 0; k < 8; k++)
                    {
                        if (!around[k].Equals(blend))
                        {
                            continue;
                        }
                        if (k % 2 == 0)
                        {
                            fanOn[(k + 7) % 8] = true;
                            fanOn[k] = true;
                            fanOn[(k + 1) % 8] = true;
                        }
                        else
                        {
                            fanOn[k] = true;
                        }
                    }
                    for (int l = 0; l < 8; l++)
                    {
                        Vector3 v = fm.verts[n + l];
                        fm.colors.Add(WithAlpha(fanOn[l], state.AlphaAt(v.x, v.z)));
                    }
                    fm.colors.Add(WithAlpha(false, midAlpha));
                    for (int t = 0; t < 8; t++)
                    {
                        fm.tris.Add(n + t);
                        fm.tris.Add(n + (t + 1) % 8);
                        fm.tris.Add(n + 8);
                    }
                }
            }
            AddSeamFans(around, blends, fanOn);
            VertexCount = 0;
            foreach (LayerSubMesh sm in subMeshes)
            {
                VertexCount += sm.verts.Count;
            }
            HasGeometry = VertexCount > 0;
            subMeshes.RemoveAll(sm => sm.verts.Count == 0);
            Bounds bounds = Rect.ExpandedBy(2).ToBounds();
            foreach (LayerSubMesh sm in subMeshes)
            {
                sm.FinalizeMesh(MeshParts.All);
                sm.mesh.bounds = bounds;
            }
            if (depthPass)
            {
                int layer = SubcameraDefOf.WaterDepth.LayerId;
                foreach (LayerSubMesh sm in subMeshes)
                {
                    sm.renderLayer = layer;
                }
            }
        }
    }
}
