using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    public enum CompatState
    {
        NotInstalled,
        Detected,
        Unrecognized,
        Failed,
    }

    public interface IOutsideCompatLayer : IDisposable
    {
        int Draw(CellRect view);
    }

    public sealed class CompatEntry
    {
        public string Name;
        public string PackageId;
        public CompatState State;
        public string Reason = "";
        public Func<HorizonState, IOutsideCompatLayer, List<CellRect>, ICompatBuild> BeginBuild;
        public Func<HorizonState, ICompatWatch> Watch;
    }

    public static class CompatRegistry
    {
        public static readonly List<CompatEntry> Entries = new List<CompatEntry>();

        public static void Init(Harmony harmony)
        {
            Add("RimWorld: Waves", "patrickschwarzer.waves", () => Compat_Waves.Enable(harmony), Compat_Waves.Begin, Compat_Waves.Watch);
            Add("RimWorld: Living Waters", "patrickschwarzer.rivers", () => Compat_LivingWaters.Enable(harmony), Compat_LivingWaters.Begin, Compat_LivingWaters.Watch);
            Add("Quiet Wonders", "choco.quietwonders", () => Compat_QuietWonders.Enable(harmony), null, detail: () => Compat_QuietWonders.Detail);
            if (Entries.Exists(e => e.State == CompatState.Detected && e.BeginBuild != null))
            {
                PatchRedraw(harmony);
            }
        }

        private static AccessTools.FieldRef<MapDrawer, Map> drawerMap;

        private static void PatchRedraw(Harmony harmony)
        {
            try
            {
                drawerMap = AccessTools.FieldRefAccess<MapDrawer, Map>("map");
                MethodInfo target = AccessTools.Method(typeof(MapDrawer), nameof(MapDrawer.RegenerateEverythingNow));
                if (target == null) throw new MissingMethodException("MapDrawer.RegenerateEverythingNow");
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(CompatRegistry), nameof(RedrawPostfix)));
            }
            catch (Exception ex)
            {
                Log.Warning(EndlessHorizonMod.LogPrefix + "Compatibility layers will not follow the other mods' option changes until the map is reloaded. " + ex);
            }
        }

        public static void RedrawPostfix(MapDrawer __instance)
        {
            try
            {
                Map map = drawerMap(__instance);
                HorizonState s = map != null ? HorizonRegistry.Peek(map) : null;
                if (s != null) s.CompatSeamRevision++;
            }
            catch (Exception ex)
            {
                Log.WarningOnce(EndlessHorizonMod.LogPrefix + "Marking compatibility layers for a rebuild failed. " + ex, 0x45485244);
            }
        }

        private static void Add(string name, string packageId, Func<string> enable, Func<HorizonState, IOutsideCompatLayer, List<CellRect>, ICompatBuild> build,
            Func<HorizonState, ICompatWatch> watch = null, Func<string> detail = null)
        {
            var entry = new CompatEntry { Name = name, PackageId = packageId, BeginBuild = build, Watch = watch };
            Entries.Add(entry);
            if (!ModsConfig.IsActive(packageId))
            {
                entry.State = CompatState.NotInstalled;
                return;
            }
            try
            {
                string reason = enable();
                entry.State = reason == null ? CompatState.Detected : CompatState.Unrecognized;
                entry.Reason = reason ?? (detail?.Invoke() ?? "");
            }
            catch (Exception ex)
            {
                entry.State = CompatState.Unrecognized;
                entry.Reason = ex.GetType().Name;
                Log.Warning(EndlessHorizonMod.LogPrefix + "Compatibility with " + name + " could not be set up and stays off. " + ex);
            }
            Log.Message(EndlessHorizonMod.LogPrefix + "compat " + name + ": " + entry.State + (entry.Reason.Length > 0 ? " (" + entry.Reason + ")" : ""));
        }

        public static List<IOutsideCompatLayer> BuildOutside(HorizonState state)
        {
            var layers = new List<IOutsideCompatLayer>();
            foreach (bool step in BuildOutsideSteps(state, layers)) { }
            return layers;
        }

        internal static IEnumerable<bool> BuildOutsideSteps(HorizonState state, List<IOutsideCompatLayer> layers)
        {
            foreach (CompatEntry e in Entries)
            {
                if (e.State != CompatState.Detected || e.BeginBuild == null) continue;
                RefreshingLayer layer = null;
                try { layer = new RefreshingLayer(e, state); }
                catch (Exception ex) { Fail(e, ex); }
                if (layer == null) continue;
                layers.Add(layer);
                while (true)
                {
                    bool done;
                    try { done = layer.StepInitial(); }
                    catch (Exception ex) { layer.Dispose(); layers.Remove(layer); Fail(e, ex); break; }
                    if (done) break;
                    yield return false;
                }
                yield return false;
            }
        }

        public static void Fail(CompatEntry e, Exception ex)
        {
            e.State = CompatState.Failed;
            e.Reason = ex.GetType().Name;
            Log.Warning(EndlessHorizonMod.LogPrefix + "Compatibility with " + e.Name + " failed and is off for this session. " + ex);
        }

        public static CompatEntry Find(string packageId) => Entries.Find(e => e.PackageId == packageId);

        private sealed class RefreshingLayer : IOutsideCompatLayer
        {
            private const float QuietSeconds = 2.5f, QuickSeconds = 0.5f;
            private const double FrameBudgetMs = 2.0;
            private const int PollFrames = 15;
            private readonly CompatEntry entry;
            private readonly HorizonState state;
            private readonly ICompatWatch watch;
            private IOutsideCompatLayer current;
            private ICompatBuild building;
            private bool hasReady;
            private IOutsideCompatLayer ready;
            private HashSet<SectionLayer> readyReused;
            private int readyFrame;
            private int seamRevision, eventSerial, nextPoll;
            private bool dirty, dirtyGlobal;
            private float due;
            private readonly List<CellRect> dirtyAreas = new List<CellRect>();
            private readonly List<(int frame, IOutsideCompatLayer layer, HashSet<SectionLayer> keep)> retired = new List<(int, IOutsideCompatLayer, HashSet<SectionLayer>)>();

            public RefreshingLayer(CompatEntry entry, HorizonState state)
            {
                this.entry = entry;
                this.state = state;
                watch = entry.Watch?.Invoke(state);
                seamRevision = state.CompatSeamRevision;
                eventSerial = state.CompatEventSerial;
                building = entry.BeginBuild(state, null, null);
            }

            internal bool StepInitial()
            {
                if (!building.Step(0)) return false;
                current = building.Result;
                HorizonPerf.Record("compat " + entry.Name + " first build", building.SpentMs);
                building = null;
                var ignore = new List<CellRect>();
                bool ignored = false;
                watch?.Poll(state, ignore, ref ignored);
                nextPoll = Time.frameCount + PollFrames;
                return true;
            }

            public int Draw(CellRect view)
            {
                int frame = Time.frameCount;
                for (int i = retired.Count - 1; i >= 0; i--)
                {
                    if (frame - retired[i].frame < 2) continue;
                    Free(retired[i].layer, retired[i].keep);
                    retired.RemoveAt(i);
                }
                if (hasReady && frame > readyFrame)
                {
                    retired.Add((frame, current, readyReused));
                    current = ready;
                    ready = null;
                    readyReused = null;
                    hasReady = false;
                }
                if (entry.State != CompatState.Detected) return 0;
                try
                {
                    if (!state.Replacing && frame >= nextPoll)
                    {
                        nextPoll = frame + PollFrames;
                        Poll();
                    }
                    if (!state.Replacing && building != null)
                    {
                        if (building.Step(FrameBudgetMs))
                        {
                            HorizonPerf.Record("compat " + entry.Name + " rebuild total", building.SpentMs);
                            HorizonPerf.Record("compat " + entry.Name + " rebuild longest frame", building.LongestStepMs);
                            ready = building.Result;
                            readyReused = building.Reused;
                            readyFrame = frame;
                            hasReady = true;
                            building = null;
                        }
                    }
                    else if (!state.Replacing && dirty && !hasReady && Time.realtimeSinceStartup >= due)
                    {
                        building = entry.BeginBuild(state, current, dirtyGlobal ? null : new List<CellRect>(dirtyAreas));
                        dirty = dirtyGlobal = false;
                        dirtyAreas.Clear();
                    }
                }
                catch (Exception ex)
                {
                    building?.Abort();
                    building = null;
                    Fail(entry, ex);
                    return 0;
                }
                try { return current?.Draw(view) ?? 0; }
                catch (Exception ex) { Fail(entry, ex); return 0; }
            }

            private void Poll()
            {
                float now = Time.realtimeSinceStartup;
                if (seamRevision != state.CompatSeamRevision)
                {
                    seamRevision = state.CompatSeamRevision;
                    Mark(true, null, now + QuickSeconds);
                }
                if (eventSerial < state.CompatEventFirstSerial)
                {
                    eventSerial = state.CompatEventSerial;
                    Mark(true, null, now + QuietSeconds);
                }
                for (; eventSerial < state.CompatEventSerial; eventSerial++)
                {
                    IntVec3 loc = state.CompatEventAt(eventSerial);
                    if (watch == null || watch.Relevant(state, loc)) Mark(false, CellRect.CenteredOn(loc, 1), now + QuietSeconds);
                }
                if (watch != null)
                {
                    var areas = new List<CellRect>();
                    bool global = false;
                    watch.Poll(state, areas, ref global);
                    if (global) Mark(true, null, now + QuietSeconds);
                    foreach (CellRect a in areas) Mark(false, a, now + QuietSeconds);
                }
            }

            private void Mark(bool global, CellRect? area, float until)
            {
                if (!dirty) due = until;
                else due = Math.Max(due, until);
                dirty = true;
                dirtyGlobal |= global;
                if (area.HasValue) dirtyAreas.Add(area.Value);
            }

            private static void Free(IOutsideCompatLayer layer, HashSet<SectionLayer> keep)
            {
                if (layer is StripLayer strips) strips.DisposeExcept(keep);
                else layer?.Dispose();
            }

            public void Dispose()
            {
                building?.Abort();
                building = null;
                Free(current, hasReady ? readyReused : null);
                if (hasReady) Free(ready, null);
                foreach (var old in retired) Free(old.layer, old.keep);
                retired.Clear();
            }
        }
    }
}
