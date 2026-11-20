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
        public Func<HorizonState, IOutsideCompatLayer> BuildOutside;
        public Func<Map, (int, int, int)> Revision;
    }

    public static class CompatRegistry
    {
        public static readonly List<CompatEntry> Entries = new List<CompatEntry>();

        public static void Init(Harmony harmony)
        {
            Add("RimWorld: Waves", "patrickschwarzer.waves", () => Compat_Waves.Enable(harmony), Compat_Waves.Build, Compat_Waves.Revision);
            Add("RimWorld: Living Waters", "patrickschwarzer.rivers", () => Compat_LivingWaters.Enable(harmony), Compat_LivingWaters.Build, Compat_LivingWaters.Revision);
            Add("Quiet Wonders", "choco.quietwonders", () => Compat_QuietWonders.Enable(harmony), null, detail: () => Compat_QuietWonders.Detail);
            if (Entries.Exists(e => e.State == CompatState.Detected && e.BuildOutside != null))
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

        private static void Add(string name, string packageId, Func<string> enable, Func<HorizonState, IOutsideCompatLayer> build,
            Func<Map, (int, int, int)> revision = null, Func<string> detail = null)
        {
            var entry = new CompatEntry { Name = name, PackageId = packageId, BuildOutside = build, Revision = revision };
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
            foreach (CompatEntry e in Entries)
            {
                if (e.State != CompatState.Detected || e.BuildOutside == null)
                {
                    continue;
                }
                try
                {
                    layers.Add(new RefreshingLayer(e, state));
                }
                catch (Exception ex)
                {
                    Fail(e, ex);
                }
            }
            return layers;
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
            private readonly CompatEntry entry;
            private readonly HorizonState state;
            private IOutsideCompatLayer current, pending;
            private bool hasPending;
            private (int, int, int) revision;
            private int seamRevision, nextCheck, pendingFrame;
            private readonly List<(int frame, IOutsideCompatLayer layer)> retired = new List<(int, IOutsideCompatLayer)>();

            public RefreshingLayer(CompatEntry entry, HorizonState state)
            {
                this.entry = entry;
                this.state = state;
                revision = entry.Revision?.Invoke(state.map) ?? default;
                current = entry.BuildOutside(state);
                seamRevision = state.CompatSeamRevision;
                nextCheck = Time.frameCount + 120;
            }

            public int Draw(CellRect view)
            {
                int frame = Time.frameCount;
                for (int i = retired.Count - 1; i >= 0; i--)
                    if (frame - retired[i].frame >= 2) { retired[i].layer?.Dispose(); retired.RemoveAt(i); }
                if (hasPending && frame > pendingFrame)
                {
                    retired.Add((frame, current));
                    current = pending;
                    pending = null;
                    hasPending = false;
                }
                if (entry.State != CompatState.Detected) return 0;
                if (frame >= nextCheck)
                {
                    nextCheck = frame + 120;
                    try
                    {
                        var latest = entry.Revision?.Invoke(state.map) ?? default;
                        if (latest != revision || seamRevision != state.CompatSeamRevision)
                        {
                            pending = entry.BuildOutside(state);
                            hasPending = true;
                            pendingFrame = frame;
                            revision = latest;
                            seamRevision = state.CompatSeamRevision;
                        }
                    }
                    catch (Exception ex) { Fail(entry, ex); return 0; }
                }
                try { return current?.Draw(view) ?? 0; }
                catch (Exception ex) { Fail(entry, ex); return 0; }
            }

            public void Dispose()
            {
                current?.Dispose();
                pending?.Dispose();
                foreach (var old in retired) old.layer?.Dispose();
                retired.Clear();
            }
        }
    }
}
