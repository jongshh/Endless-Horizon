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
    internal sealed partial class HorizonState : IDisposable
    {
        public readonly Map map;
        public readonly int Generation;
        public readonly int RingWidth;
        public readonly int FogStart;

        public HorizonField Field;
        internal HorizonDomain Domain;
        internal HorizonSurface Surface;
        internal RockRender Rocks;
        internal FogTint Tint;
        private bool fogTintDue;
        private float fogTintNext;
        internal int FogTintRefreshes;
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
            NoteRebuildReason(reason);
        }

        internal string BuildReason { get; private set; } = "first build";
        private readonly Dictionary<string, int> rebuildReasons = new Dictionary<string, int>();

        private void NoteRebuildReason(string reason) => rebuildReasons[reason] = rebuildReasons.TryGetValue(reason, out int n) ? n + 1 : 1;

        private string TakeRebuildReasons()
        {
            if (rebuildReasons.Count == 0) return "unknown";
            string text = string.Join(", ", rebuildReasons.Select(r => r.Key + " x" + r.Value));
            rebuildReasons.Clear();
            return text;
        }

        internal HorizonState AdvanceReplacement()
        {
            if (replacementFrame == Time.frameCount) return null;
            replacementFrame = Time.frameCount;
            if (replacement == null)
            {
                if (!Built || !RebuildRequested || Time.realtimeSinceStartup < rebuildDue) return null;
                RebuildRequested = false;
                extensions?.HandOver();
                replacement = new HorizonState(map, Generation) { CoastSourceOverride = CoastSourceOverride, BuildReason = TakeRebuildReasons() };
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
        private RingDarkness darkness;
        private bool darknessFailed;
        public MountainMask Mountains;
        private readonly List<(CellRect rect, Mesh mesh)> lightChunks = new List<(CellRect, Mesh)>();
        private OutsideGlow outsideGlow;
        public OutsideGlow Glow => outsideGlow;
        private int glowSignature, nextGlowCheck;
        private float glowThingDist;
        public HorizonFog Fog;
        private bool built;
        public bool Built { get; private set; }
        private ExtensionRing extensions;
        internal ExtensionRing Extensions => extensions;

        private sealed class Change
        {
            internal CellRect Area;
            internal bool Ground, Roof;
        }

        private readonly Dictionary<(int x, int z), Change> changes = new Dictionary<(int x, int z), Change>();
        private bool snowChangedSince, glowChangedSince;

        private void RecordChange(IntVec3 loc, bool ground, bool roof)
        {
            (int, int) key = (FloorTo(loc.x, Section.Size), FloorTo(loc.z, Section.Size));
            if (!changes.TryGetValue(key, out Change change)) changes[key] = change = new Change { Area = CellRect.CenteredOn(loc, 1) };
            else change.Area = Union(change.Area, CellRect.CenteredOn(loc, 1));
            change.Ground |= ground;
            change.Roof |= roof;
        }

        private static CellRect Union(CellRect a, CellRect b) =>
            CellRect.FromLimits(Math.Min(a.minX, b.minX), Math.Min(a.minZ, b.minZ), Math.Max(a.maxX, b.maxX), Math.Max(a.maxZ, b.maxZ));

        private static CellRect Around(List<IntVec3> cells)
        {
            CellRect area = CellRect.SingleCell(cells[0]);
            foreach (IntVec3 c in cells) area = Union(area, CellRect.SingleCell(c));
            return area;
        }

        internal void MarkRoofChanged(IntVec3 loc)
        {
            if (built) RecordChange(loc, false, true);
        }

        private void PublishChanges()
        {
            if (pendingSwaps.Count > 0 || extensions == null) return;
            if (refreshWork == null && seamQueue.Count == 0)
            {
                if (changes.Count > 0)
                {
                    var settled = new List<Change>(changes.Values);
                    changes.Clear();
                    foreach (Change change in settled)
                    {
                        if (change.Ground)
                        {
                            foreach (string id in Vocabulary.GroundChannels) ExtensionMaps.Publish(map, id, change.Area);
                        }
                        else if (change.Roof)
                        {
                            ExtensionMaps.Publish(map, Vocabulary.Roof, change.Area);
                            ExtensionMaps.Publish(map, Vocabulary.Exposed, change.Area);
                        }
                        ExtensionMaps.Publish(map, Vocabulary.Fogged, change.Area);
                        if (!change.Ground && !change.Roof) ExtensionMaps.Publish(map, Vocabulary.Exposed, change.Area);
                    }
                }
                if (liveChanged.Count > 0 && Field != null)
                {
                    foreach (ILiveModule module in Field.Live)
                    {
                        if (!liveChanged.TryGetValue(module, out CellRect area)) continue;
                        liveChanged.Remove(module);
                        module.Announce(area, (id, where) => ExtensionMaps.Publish(map, id, where));
                    }
                }
            }
            if (snowChangedSince && refreshWork == null && snowQueue.Count == 0 && refreshQueue.Count == 0)
            {
                snowChangedSince = false;
                ExtensionMaps.Publish(map, Vocabulary.Snow, Domain.Bounds);
            }
            if (glowChangedSince && glowQueue.Count == 0)
            {
                glowChangedSince = false;
                ExtensionMaps.Publish(map, Vocabulary.Glow, Domain.Bounds);
            }
        }

        internal void RegionChanged()
        {
            if (!built) return;
            replacement?.Dispose();
            replacement = null;
            RebuildRequested = true;
            rebuildDue = Time.realtimeSinceStartup;
            HorizonPerf.Record("ring rebuild requested: region", 0);
            NoteRebuildReason("region");
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
            SurfaceTable.Show(map, Surface);
            extensions.Show(null);
            EdgeRock.RedrawEdgeRock(map, Domain);
        }

        private IEnumerable<bool> BuildSteps()
        {
            built = true;
            ExtensionRegistry.Close();
            RockPrint.Printed = RockPrint.Fallbacks = 0;
            var sw = Stopwatch.StartNew();
            Domain = HorizonDomain.Of(map, RingWidth);
            Field = new HorizonField(map, Domain);
            Surface = new HorizonSurface(map, Field, FogStart);
            buildPhase = "field";
            foreach (bool step in Field.BuildSteps(EndlessHorizonMod.Settings.useCoastNoise, CoastSourceOverride)) yield return step;
            yield return false;
            buildPhase = "mountains/tint";
            try
            {
                Mountains = Field.MakeMountains();
            }
            catch (Exception ex)
            {
                Mountains = null;
                Log.Error(EndlessHorizonMod.LogPrefix + "Mountain mask failed: " + ex);
            }
            yield return false;
            if (Mountains != null)
            {
                buildPhase = "fog enclosure";
                using (IEnumerator<bool> enclose = Mountains.EncloseSteps().GetEnumerator())
                {
                    while (true)
                    {
                        bool more;
                        try { more = enclose.MoveNext(); }
                        catch (Exception ex)
                        {
                            Log.Error(EndlessHorizonMod.LogPrefix + "Enclosing the outside fog failed; it may touch open ground. " + ex);
                            break;
                        }
                        if (!more) break;
                        yield return false;
                    }
                }
                Field.Mountains = Mountains;
                Rocks = new RockRender(Surface);
            }
            buildPhase = "fog tint";
            try { Tint = FogTint.Build(map, Domain); }
            catch (Exception ex) { Log.Error(EndlessHorizonMod.LogPrefix + "Fog tint failed: " + ex); }
            yield return false;
            buildPhase = "ice";
            Field.BuildIce();
            Field.BuildFlood();
            seasons = new SeasonWatch(map, Field);

            yield return false;
            buildPhase = "outside light";
            glowThingDist = FogRamp.CoveredFrom(FogStart, RingWidth, FogRamp.HiddenAlpha);
            try
            {
                glowSignature = OutsideGlow.Signature(map, Domain);
                outsideGlow = Field.MakeGlow(Mountains, glowThingDist);
                Surface.SetLight(outsideGlow);
            }
            catch (Exception ex)
            {
                outsideGlow = null;
                Log.ErrorOnce(EndlessHorizonMod.LogPrefix + "Outside light failed; the ring is lit by the sky only. " + ex, 0x45484C47);
            }
            yield return false;
            nextGlowCheck = Time.frameCount + 60;
            darkness = new RingDarkness(Surface, dynRects);
            buildPhase = "extension step";
            extensions = new ExtensionRing(Surface);
            foreach (bool step in extensions.RunStepsFirst()) yield return step;
            yield return false;
            int size = Section.Size;
            int minX = FloorTo(-RingWidth, size), minZ = FloorTo(-RingWidth, size);
            int maxX = Domain.W + RingWidth, maxZ = Domain.H + RingWidth;
            bool depth = EndlessHorizonMod.Settings.drawWaterDepth;
            for (int x = minX; x < maxX; x += size)
            {
                for (int z = minZ; z < maxZ; z += size)
                {
                    var rect = new CellRect(x, z, size, size);
                    if (Domain.Interior(rect))
                    {
                        continue;
                    }
                    var chunk = new HorizonChunk(Surface, rect, false);
                    buildPhase = "terrain chunk";
                    terrainChunks.Add(chunk);
                    chunk.Regenerate();
                    yield return false;
                    VertexCount += chunk.VertexCount;
                    HorizonThingsChunk things;
                    buildPhase = "things cell/stage";
                    using (var work = HorizonThings.BeginBuild(Surface, rect, Rocks, Tint))
                    {
                        while (work.Steps.MoveNext()) yield return false;
                        PrintedThings += work.Printed;
                        FogCells += work.FogCells;
                        things = work.Result;
                        dynThings.Add(things);
                    }
                    yield return false;
                    buildPhase = "light chunk";
                    Mesh lightMesh = RingLighting.BuildChunk(Surface, rect);
                    if (lightMesh != null)
                    {
                        lightChunks.Add((rect, lightMesh));
                    }
                    yield return false;
                    chunkByCorner.Add((rect.minX, rect.minZ), dynRects.Count);
                    dynRects.Add(rect);
                    DarknessWork(() => darkness.Add(dynRects.Count - 1), "build");
                    buildPhase = "snow chunk";
                    dynSnow.Add(EndlessHorizonMod.Settings.snow ? SnowLayer.BuildChunk(Surface, rect) : null);
                    yield return false;
                    if (things != null)
                    {
                        foreach (LayerSubMesh sm in things.subMeshes) VertexCount += sm.verts.Count;
                    }
                    if (depth && Field.AnyWaterIn(rect))
                    {
                        buildPhase = "depth chunk";
                        var depthChunk = new HorizonChunk(Surface, rect, true);
                        depthChunks.Add(depthChunk);
                        depthChunk.Regenerate();
                        if (!depthChunk.HasGeometry) { depthChunks.Remove(depthChunk); depthChunk.Dispose(); }
                        yield return false;
                    }
                }
            }
            ChunkCount = terrainChunks.Count(c => c.HasGeometry);
            yield return false;
            buildPhase = "overlays";
            lighting = new HorizonLighting(Domain);
            Fog = new HorizonFog(Surface);
            yield return false;
            buildPhase = "extension layer";
            foreach (bool step in extensions.BuildLayersFirst()) yield return step;
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
            RecordChange(loc, surfaceChanged, false);
            string reason = null;
            if (Field != null)
                foreach (ILiveModule module in Field.Live)
                {
                    string why = module.OnEdit(loc, surfaceChanged);
                    reason ??= why;
                }
            if (surfaceChanged && (replacement != null || reason != null)) RequestFullRebuild(reason ?? "boundary base terrain");
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

        internal void FogRegenerated()
        {
            if (Built && Tint != null && Tint.Reads) fogTintDue = true;
        }

        private void FogTintStep()
        {
            if (!fogTintDue || Time.realtimeSinceStartup < fogTintNext) return;
            fogTintDue = false;
            fogTintNext = Time.realtimeSinceStartup + 1f;
            var sw = Stopwatch.StartNew();
            if (FogTint.ReadSignature(map, Domain) == Tint.Signature)
            {
                HorizonPerf.Record("fog tint check", sw.Elapsed.TotalMilliseconds);
                return;
            }
            FogTint fresh = FogTint.Build(map, Domain);
            if (fresh != null && !fresh.SameAs(Tint))
            {
                Tint = fresh;
                foreach (HorizonThingsChunk c in dynThings)
                {
                    if (c != null) fresh.Recolour(c.subMeshes);
                }
                FogTintRefreshes++;
            }
            else if (fresh != null)
            {
                Tint = fresh;
            }
            HorizonPerf.Record("fog tint read", sw.Elapsed.TotalMilliseconds);
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
            foreach (ILiveModule module in Field.Live)
            {
                string why = module.RebuildReason(RebuildRequested);
                if (why != null) RequestFullRebuild(why);
            }
            if (seasons != null)
            {
                seasons.Check(force, out bool snowChanged, out bool thingsChanged, out bool iceChanged);
                if (iceChanged) Field.RequestIce();
                foreach (ILiveModule module in Field.Live)
                {
                    if (!module.Due()) continue;
                    if (module.Immediate)
                    {
                        var changed = new List<IntVec3>();
                        foreach (bool step in module.UpdateSteps(changed)) { }
                        Updated(module, changed);
                    }
                    else if (!liveWaiting.Contains(module)) liveWaiting.Add(module);
                }
                if (thingsChanged && refreshQueue.Count == 0)
                {
                    RequestRefresh();
                }
                else if (snowChanged)
                {
                    RequestSnowRefresh();
                }
                snowChangedSince |= snowChanged;
            }
            do
            {
                if (refreshWork == null)
                {
                    if (liveWaiting.Count > 0)
                    {
                        ILiveModule module = liveWaiting[0];
                        liveWaiting.RemoveAt(0);
                        refreshWork = RefreshLive(module).GetEnumerator();
                    }
                    else if (seamQueue.Count > 0)
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

        private readonly List<ILiveModule> liveWaiting = new List<ILiveModule>();
        private readonly Dictionary<ILiveModule, CellRect> liveChanged = new Dictionary<ILiveModule, CellRect>();

        private bool LiveBusy => liveWaiting.Count > 0 || Field?.Live.Any(m => m.Busy) == true;

        private IEnumerable<bool> RefreshLive(ILiveModule module)
        {
            refreshPhase = "refresh " + module.Name + " unit";
            var changed = new List<IntVec3>();
            foreach (bool step in module.UpdateSteps(changed)) yield return step;
            Updated(module, changed);
        }

        private void Updated(ILiveModule module, List<IntVec3> changed)
        {
            if (changed.Count == 0) return;
            CellRect area = Around(changed);
            liveChanged[module] = liveChanged.TryGetValue(module, out CellRect was) ? Union(was, area) : area;
            if (module.ChangesWater) Surface?.WaterChanged();
            QueueChunksAround(changed);
        }

        private void QueueChunksAround(List<IntVec3> changed)
        {
            foreach (IntVec3 c in changed)
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                        if (chunkByCorner.TryGetValue((FloorTo(c.x + dx, Section.Size), FloorTo(c.z + dz, Section.Size)), out int i) && seamQueued.Add(i))
                            seamQueue.Enqueue(i);
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
                    terrain = new HorizonChunk(Surface, rect, false);
                    terrain.Regenerate();
                    yield return false;
                    refreshPhase = "refresh depth unit";
                    if (EndlessHorizonMod.Settings.drawWaterDepth && (depthChunks.Exists(d => d.Rect == rect) || Field.AnyWaterIn(rect)))
                    {
                        depth = new HorizonChunk(Surface, rect, true);
                        depth.Regenerate();
                    }
                    yield return false;
                }
                using (var work = HorizonThings.BeginBuild(Surface, rect, Rocks, Tint))
                {
                    refreshPhase = "refresh things cell/stage";
                    while (work.Steps.MoveNext()) yield return false;
                    things = work.Result;
                }
                refreshPhase = "refresh snow unit";
                snow = EndlessHorizonMod.Settings.snow ? SnowLayer.BuildChunk(Surface, rect) : null;
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
                if (seam)
                {
                    SeamRebuilds++;
                    DarknessWork(() => darkness.MarkDirty(rect), "edge change");
                }
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
        private Dictionary<int, Color32> edgeGlowSeen;
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
                    int sig = OutsideGlow.Signature(map, Domain);
                    if (sig != glowSignature)
                    {
                        glowSignature = sig;
                        OutsideGlow old = outsideGlow;
                        outsideGlow = Field.MakeGlow(Mountains, glowThingDist, old);
                        Surface.SetLight(outsideGlow);
                        glowChangedSince = true;
                        QueueChangedLight(old, outsideGlow);
                    }
                }
                while (glowQueue.Count > 0)
                {
                    int index = glowQueue.Dequeue();
                    glowQueued.Remove(index);
                    Mesh fresh = RingLighting.BuildChunk(Surface, lightChunks[index].rect);
                    CellRect lit = lightChunks[index].rect;
                    DarknessWork(() => darkness.MarkDirty(lit), "light change");
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
                Log.ErrorOnce(EndlessHorizonMod.LogPrefix + "Updating the outside light failed; it stays as it was. " + ex, 0x45484C55);
            }
            finally
            {
                double ms = sw.Elapsed.TotalMilliseconds;
                if (ms > 0.2) HorizonPerf.Record("outside light (per frame)", ms);
            }
        }

        private void QueueChangedLight(OutsideGlow before, OutsideGlow after)
        {
            var now = new Dictionary<int, Color32>(Domain.Edge.Length);
            foreach (int i in Domain.Edge) now[i] = map.glowGrid.VisualGlowAt(i);
            bool Same(Color32 a, Color32 b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;
            for (int i = 0; i < lightChunks.Count; i++)
            {
                CellRect rect = lightChunks[i].rect.ExpandedBy(1);
                bool changed = edgeGlowSeen == null;
                foreach (IntVec3 c in rect)
                {
                    if (changed) break;
                    if (!Domain.Inside(c)) changed = !Same(before.At(c.x, c.z), after.At(c.x, c.z));
                    else if (Domain.OnEdge(c))
                    {
                        int k = Domain.MapIndex(c.x, c.z);
                        changed = !Same(edgeGlowSeen[k], now[k]);
                    }
                }
                if (changed && glowQueued.Add(i)) glowQueue.Enqueue(i);
            }
            edgeGlowSeen = now;
        }

        private void RebuildSnow(int i)
        {
            Mesh fresh = EndlessHorizonMod.Settings.snow ? SnowLayer.BuildChunk(Surface, dynRects[i]) : null;
            pendingSwaps.Add(() =>
            {
                Mesh old = dynSnow[i];
                dynSnow[i] = fresh;
                if (old != null) Bury(() => UnityEngine.Object.Destroy(old));
            });
        }

        public void Draw(CellRect shown)
        {
            EnsureBuilt();
            ExtensionMaps.SetView(map, shown);
            Map drawing = ExtensionMaps.DrawingMap;
            ExtensionMaps.DrawingMap = map;
            beforeRan.Clear();
            try
            {
                RunStage(Vocabulary.Before, shown);
                DrawCost.Lap(DrawCost.Before);
                DrawRing(shown);
            }
            finally
            {
                try { RunStage(Vocabulary.After, shown); }
                finally { ExtensionMaps.DrawingMap = drawing; }
                DrawCost.Lap(DrawCost.After);
            }
        }

        private readonly HashSet<string> beforeRan = new HashSet<string>(StringComparer.Ordinal);

        private void RunStage(string stage, CellRect shown)
        {
            List<DrawRegistration> draws = ExtensionRegistry.DrawsAt(stage);
            if (draws == null) return;
            bool after = stage == Vocabulary.After;
            for (int i = 0; i < draws.Count; i++)
            {
                DrawRegistration draw = draws[i];
                if (draw.Failed) continue;
                if (ExtensionRegistry.DrawsOff(draw.Owner) && !(after && beforeRan.Contains(draw.Owner))) continue;
                if (stage == Vocabulary.Before) beforeRan.Add(draw.Owner);
                try { draw.Draw(map, shown); }
                catch (Exception ex) { ExtensionRegistry.Fail(draw, ex); }
            }
        }

        private int workFrame = -1;

        private void DarknessWork(Action work, string what)
        {
            if (darkness == null || darknessFailed) return;
            try { work(); }
            catch (Exception ex)
            {
                darknessFailed = true;
                Log.ErrorOnce(EndlessHorizonMod.LogPrefix + "Outside unnatural darkness failed (" + what + "); the ring stays lit by its own lighting. " + ex, 0x45484B44);
                RingDarkness failed = darkness;
                darkness = null;
                try { failed.Dispose(); }
                catch (Exception freeing) { Log.ErrorOnce(EndlessHorizonMod.LogPrefix + "Freeing the outside darkness failed. " + freeing, 0x45484B46); }
            }
        }

        internal RingDarkness Darkness => darkness;

        private void DrawRing(CellRect shown)
        {
            CellRect view = shown.ExpandedBy(2);
            bool work = workFrame != Time.frameCount;
            workFrame = Time.frameCount;
            bool waterSurface = !ExtensionRegistry.IsReplaced(Vocabulary.WaterSurface);
            int drawn = 0, calls = 0;
            for (int i = 0; i < terrainChunks.Count; i++)
            {
                HorizonChunk c = terrainChunks[i];
                if (c.HasGeometry && c.Rect.Overlaps(view))
                {
                    c.DrawLayer(waterSurface);
                    drawn++;
                    calls += c.subMeshes.Count;
                }
            }
            DrawCost.Lap(DrawCost.Terrain);
            if (!ExtensionRegistry.IsReplaced(Vocabulary.WaterDepth))
            {
                for (int i = 0; i < depthChunks.Count; i++)
                {
                    HorizonChunk c = depthChunks[i];
                    if (c.Rect.Overlaps(view))
                    {
                        c.DrawLayer();
                        calls += c.subMeshes.Count;
                    }
                }
            }
            DrawCost.Lap(DrawCost.Depth);
            RunStage(Vocabulary.AfterTerrain, shown);
            DrawCost.Lap(DrawCost.AfterTerrain);
            if (work) extensions.Advance(Replacing);
            DrawCost.Lap(DrawCost.Advance);
            calls += extensions.DrawLayers(view);
            DrawCost.Lap(DrawCost.Layers);
            if (work)
            {
                if (!Replacing)
                {
                    RefreshStep(false);
                    FogTintStep();
                    GlowStep();
                    PublishChanges();
                }
                else SwapAndCollect();
                DarknessWork(() => darkness.Step(1.0), "update");
                DrawCost.Lap(DrawCost.Work);
                if (retiredWork != null)
                {
                    var cleanup = Stopwatch.StartNew();
                    do
                    {
                        if (!retiredWork.MoveNext()) { retiredWork.Dispose(); retiredWork = null; break; }
                    } while (cleanup.Elapsed.TotalMilliseconds < 1.0);
                    HorizonPerf.Record("ring retirement (per frame)", cleanup.Elapsed.TotalMilliseconds);
                }
            }
            DrawCost.Lap(DrawCost.Retire);
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
            DrawCost.Lap(DrawCost.Things);
            if (work && !Replacing && Field.PendingCoastSearch != null && Field.PendingCoastSearch.Step(2f))
            {
                Field.PendingCoastSearch = null;
                RequestFullRebuild("coast search completed");
            }
            DrawCost.Lap(DrawCost.Coast);
            RunStage(Vocabulary.BeforeLighting, shown);
            DrawCost.Lap(DrawCost.BeforeLighting);
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
            DarknessWork(() => calls += darkness.Draw(view), "draw");
            DrawCost.Lap(DrawCost.Lighting);
            RunStage(Vocabulary.BeforeFog, shown);
            DrawCost.Lap(DrawCost.BeforeFog);
            bool haze = s.fog && !HorizonDebug.HideFog;
            bool clouds = s.clouds && !HorizonDebug.HideClouds;
            bool fogShown = haze && Fog.Update(clouds, view);
            DrawCost.Lap(DrawCost.FogUpdate);
            if (fogShown)
            {
                Fog.Draw();
                calls++;
            }
            DrawCost.Lap(DrawCost.FogDraw);
            LastDrawnChunks = drawn;
            LastDrawCalls = calls;
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
            if (Surface != null) SurfaceTable.Hide(map, Surface);
            bool onScreen = wasBuilt && map != null && HorizonRegistry.Peek(map) == this;
            if (onScreen && !map.Disposed)
            {
                EdgeRock.RedrawEdgeRock(map, Domain);
            }
            if (onScreen) ExtensionMaps.Publish(map, Vocabulary.Disposed, Domain.Bounds);
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
            if (extensions != null)
            {
                foreach (bool step in extensions.DisposeSteps()) yield return step;
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
            darkness?.Dispose();
            darkness = null;
            Fog?.Dispose();
        }
    }
}
