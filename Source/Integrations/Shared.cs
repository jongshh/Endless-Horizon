using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using EndlessHorizon.Api;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{

    internal static class IntegrationLog
    {
        internal const string Prefix = "[Endless Horizon] ";
    }

    internal sealed class FieldShape
    {
        internal int W, H, Ring;
        internal CellRect Bounds;
        private bool[] playable;
        private int[] depth;
        private Dictionary<(int, int), (int, int)> nearestEdge;

        internal static FieldShape Of(Map map)
        {
            CellRect bounds = HorizonSpace.Bounds(map);
            int ring = -bounds.minX;
            return new FieldShape { Bounds = bounds, Ring = ring, W = bounds.Width - 2 * ring, H = bounds.Height - 2 * ring };
        }

        internal bool Rectangle => playable == null;

        internal IEnumerable<bool> ReadSteps(Map map)
        {
            var read = new bool[W * H];
            bool every = true;
            for (int z = 0; z < H; z++)
            {
                for (int x = 0; x < W; x++)
                    every &= read[z * W + x] = HorizonSpace.IsPlayable(map, new IntVec3(x, 0, z));
                if ((z & 15) == 15) yield return false;
            }
            if (every) yield break;
            var far = new int[read.Length];
            int At(int x, int z) => x < 0 || z < 0 || x >= W || z >= H ? 0 : far[z * W + x];
            for (int z = 0; z < H; z++)
                for (int x = 0; x < W; x++)
                    far[z * W + x] = !read[z * W + x] ? 0 : 1 + Math.Min(Math.Min(At(x - 1, z), At(x, z - 1)), Math.Min(At(x - 1, z - 1), At(x + 1, z - 1)));
            yield return false;
            for (int z = H - 1; z >= 0; z--)
                for (int x = W - 1; x >= 0; x--)
                {
                    int i = z * W + x;
                    if (far[i] == 0) continue;
                    far[i] = Math.Min(far[i], 1 + Math.Min(Math.Min(At(x + 1, z), At(x, z + 1)), Math.Min(At(x + 1, z + 1), At(x - 1, z + 1))));
                }
            depth = far;
            nearestEdge = new Dictionary<(int, int), (int, int)>();
            playable = read;
        }

        internal bool InMap(int x, int z) => x >= 0 && z >= 0 && x < W && z < H && (playable == null || playable[z * W + x]);

        internal bool OnMap(int x, int z) => playable == null
            ? x >= 0 && z >= 0 && x <= W && z <= H
            : InMap(x - 1, z - 1) || InMap(x, z - 1) || InMap(x - 1, z) || InMap(x, z);

        internal bool IsEdge(int x, int z) => playable == null
            ? x >= 0 && x <= W && z >= 0 && z <= H && (x == 0 || x == W || z == 0 || z == H)
            : OnMap(x, z) && !(InMap(x - 1, z - 1) && InMap(x, z - 1) && InMap(x - 1, z) && InMap(x, z));

        internal int Inward(int x, int z)
        {
            if (playable == null) return Math.Min(Math.Min(x, W - x), Math.Min(z, H - z));
            int At(int cx, int cz) => InMap(cx, cz) ? depth[cz * W + cx] : 0;
            return Math.Min(Math.Min(At(x - 1, z - 1), At(x, z - 1)), Math.Min(At(x - 1, z), At(x, z)));
        }

        internal bool TryNearestEdge(int x, int z, int reach, out int ex, out int ez)
        {
            if (!nearestEdge.TryGetValue((x, z), out (int x, int z) found))
            {
                found = (int.MinValue, 0);
                for (int step = 0; step <= reach && found.x == int.MinValue; step++)
                {
                    long best = long.MaxValue;
                    for (int dz = -step; dz <= step; dz++)
                        for (int dx = -step; dx <= step; dx++)
                        {
                            if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != step || !IsEdge(x + dx, z + dz)) continue;
                            long d = dx * dx + dz * dz;
                            if (d >= best) continue;
                            best = d;
                            found = (x + dx, z + dz);
                        }
                }
                nearestEdge[(x, z)] = found;
            }
            ex = found.x;
            ez = found.z;
            return found.x != int.MinValue;
        }

        internal IntVec3 ClampToBounds(int x, int z) =>
            new IntVec3(Mathf.Clamp(x, Bounds.minX, Bounds.maxX), 0, Mathf.Clamp(z, Bounds.minZ, Bounds.maxZ));
    }

    internal sealed class StepResult<T> where T : class
    {
        private sealed class Held
        {
            internal T Current, Pending;
            internal bool HasPending;
        }

        private readonly ConditionalWeakTable<Map, Held> held = new ConditionalWeakTable<Map, Held>();
        private readonly Action<T> free;

        internal StepResult(Action<T> free = null) => this.free = free;

        internal void Made(Map map, T value)
        {
            Held h = held.GetOrCreateValue(map);
            if (h.HasPending && h.Pending != null && h.Pending != h.Current && h.Pending != value) free?.Invoke(h.Pending);
            h.Pending = value;
            h.HasPending = true;
        }

        internal void Shown(Map map)
        {
            if (!held.TryGetValue(map, out Held h) || !h.HasPending) return;
            if (h.Current != null && h.Current != h.Pending) free?.Invoke(h.Current);
            h.Current = h.Pending;
            h.Pending = null;
            h.HasPending = false;
        }

        internal void Disposed(Map map)
        {
            if (!held.TryGetValue(map, out Held h)) return;
            if (h.Current != null) free?.Invoke(h.Current);
            if (h.Pending != null && h.Pending != h.Current) free?.Invoke(h.Pending);
            held.Remove(map);
        }

        internal T Current(Map map) => map != null && held.TryGetValue(map, out Held h) ? h.Current : null;

        internal T Building(Map map)
        {
            if (map == null || !held.TryGetValue(map, out Held h)) return null;
            return h.HasPending && HorizonInfo.IsBuilding(map) ? h.Pending : h.Current;
        }
    }

    internal static class IntegrationWater
    {
        internal static bool Within(Map map, CellRect area, int radius, Func<TerrainDef, bool> water)
        {
            foreach (IntVec3 c in area.ExpandedBy(radius))
            {
                TerrainDef t = HorizonWorld.BaseTerrainAt(map, c);
                if (t != null && water(t)) return true;
            }
            return false;
        }

        internal static bool AnyPlayable(Map map, CellRect rect)
        {
            foreach (IntVec3 c in rect)
                if (HorizonSpace.IsPlayable(map, c)) return true;
            return false;
        }

        internal static float[] Distance(int w, int h, Func<int, bool> source, float cap)
        {
            var d = new float[w * h];
            for (int i = 0; i < d.Length; i++) d[i] = source(i) ? 0f : cap;
            const float D1 = 1f, D2 = 1.4142f;
            for (int z = 0; z < h; z++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = z * w + x;
                    float v = d[i];
                    if (x > 0) v = Mathf.Min(v, d[i - 1] + D1);
                    if (z > 0)
                    {
                        v = Mathf.Min(v, d[i - w] + D1);
                        if (x > 0) v = Mathf.Min(v, d[i - w - 1] + D2);
                        if (x < w - 1) v = Mathf.Min(v, d[i - w + 1] + D2);
                    }
                    d[i] = v;
                }
            }
            for (int z = h - 1; z >= 0; z--)
            {
                for (int x = w - 1; x >= 0; x--)
                {
                    int i = z * w + x;
                    float v = d[i];
                    if (x < w - 1) v = Mathf.Min(v, d[i + 1] + D1);
                    if (z < h - 1)
                    {
                        v = Mathf.Min(v, d[i + w] + D1);
                        if (x < w - 1) v = Mathf.Min(v, d[i + w + 1] + D2);
                        if (x > 0) v = Mathf.Min(v, d[i + w - 1] + D2);
                    }
                    d[i] = v;
                }
            }
            return d;
        }
    }

    internal static class OptionRedraws
    {
        private static readonly List<string> owners = new List<string>();
        private static AccessTools.FieldRef<MapDrawer, Map> drawerMap;

        internal static void Follow(Harmony harmony, string owner)
        {
            try
            {
                if (owners.Contains(owner)) return;
                if (drawerMap == null)
                {
                    MethodInfo target = AccessTools.Method(typeof(MapDrawer), nameof(MapDrawer.RegenerateEverythingNow))
                        ?? throw new MissingMethodException("MapDrawer.RegenerateEverythingNow");
                    AccessTools.FieldRef<MapDrawer, Map> field = AccessTools.FieldRefAccess<MapDrawer, Map>("map");
                    harmony.Patch(target, postfix: new HarmonyMethod(typeof(OptionRedraws), nameof(Postfix)));
                    drawerMap = field;
                }
                owners.Add(owner);
            }
            catch (Exception ex)
            {
                Log.Error(IntegrationLog.Prefix + "Compatibility layers will not follow the other mods' option changes until the map is reloaded. " + ex);
            }
        }

        public static void Postfix(MapDrawer __instance)
        {
            try
            {
                Map map = drawerMap(__instance);
                if (map == null || !HorizonInfo.IsBuilt(map)) return;
                foreach (string owner in owners) HorizonInfo.RequestRefresh(map, owner, HorizonSpace.Bounds(map));
            }
            catch (Exception ex)
            {
                Log.ErrorOnce(IntegrationLog.Prefix + "Marking compatibility layers for a rebuild failed. " + ex, 0x45485244);
            }
        }
    }
}
