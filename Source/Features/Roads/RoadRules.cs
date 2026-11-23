using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal sealed class RoadSurface
    {
        private sealed class Cell
        {
            internal TerrainDef Top, Under, Foundation;
            internal bool Cleared, Listed;
        }

        internal readonly int MinX, MinZ, Width, Height;
        private readonly Func<int, int, TerrainDef> initialTop, initialFoundation;
        private readonly TerrainDef defaultUnder;
        private readonly Dictionary<int, Cell> touched = new();
        internal readonly List<int> Changed = new();
        internal readonly List<(int x, int z, ThingDef def)> Things = new();
        private readonly Dictionary<ThingDef, List<(int x, int z)>> standing = new();

        internal RoadSurface(int minX, int minZ, int width, int height, TerrainDef defaultUnder,
            Func<int, int, TerrainDef> initialTop, Func<int, int, TerrainDef> initialFoundation)
        {
            MinX = minX; MinZ = minZ; Width = width; Height = height;
            this.defaultUnder = defaultUnder; this.initialTop = initialTop; this.initialFoundation = initialFoundation;
        }

        internal bool Covers(int x, int z) => x >= MinX && z >= MinZ && x < MinX + Width && z < MinZ + Height;
        internal int X(int i) => i % Width + MinX;
        internal int Z(int i) => i / Width + MinZ;
        private int Index(int x, int z) => (z - MinZ) * Width + x - MinX;

        private Cell Load(int x, int z)
        {
            int i = Index(x, z);
            if (!touched.TryGetValue(i, out Cell cell))
                touched.Add(i, cell = new Cell { Top = initialTop(x, z), Foundation = initialFoundation?.Invoke(x, z) });
            return cell;
        }

        private void List(Cell cell, int x, int z)
        {
            if (cell.Listed) return;
            cell.Listed = true;
            Changed.Add(Index(x, z));
        }

        internal TerrainDef TerrainAt(int x, int z)
        {
            Cell cell = Load(x, z);
            return cell.Under == null && cell.Foundation != null ? cell.Foundation : cell.Top;
        }

        internal TerrainDef BaseAt(int x, int z)
        {
            Cell cell = Load(x, z);
            return cell.Under ?? cell.Top;
        }

        internal TerrainDef FoundationAt(int x, int z) => Load(x, z).Foundation;

        internal bool ClearedAt(int x, int z) => Load(x, z).Cleared;

        internal void Clear(int x, int z)
        {
            Cell cell = Load(x, z);
            cell.Cleared = true;
            List(cell, x, z);
        }

        internal void Set(int x, int z, TerrainDef def)
        {
            if (def == null) return;
            Cell cell = Load(x, z);
            if (def.isFoundation)
            {
                if (cell.Under != null) return;
                cell.Foundation = def;
            }
            else
            {
                if (!def.layerable) cell.Under = null;
                else cell.Under ??= cell.Top != null && cell.Top.passability != Traversability.Impassable ? cell.Top : defaultUnder;
                cell.Top = def;
            }
            List(cell, x, z);
        }

        internal void Standing(ThingDef def, int x, int z)
        {
            if (!standing.TryGetValue(def, out List<(int, int)> at)) standing[def] = at = new List<(int, int)>();
            at.Add((x, z));
        }

        internal void PlaceThing(int x, int z, ThingDef def)
        {
            Things.Add((x, z, def));
            Standing(def, x, z);
        }

        internal bool ThingNear(ThingDef def, int x, int z, float spacing)
        {
            if (!standing.TryGetValue(def, out List<(int x, int z)> at)) return false;
            float max = spacing * spacing;
            foreach ((int tx, int tz) in at)
            {
                int dx = tx - x, dz = tz - z;
                if (dx * dx + dz * dz <= max) return true;
            }
            return false;
        }

        internal bool CanBuild(BuildableDef def, int x, int z)
        {
            TerrainDef terrain = TerrainAt(x, z);
            if (def is TerrainDef && terrain?.changeable != true) return false;
            TerrainAffordanceDef need = def.terrainAffordanceNeeded;
            if (need == null) return true;
            List<TerrainAffordanceDef> offered = (FoundationAt(x, z) ?? terrain)?.affordances;
            return offered != null && offered.Contains(need);
        }
    }

    internal sealed class RoadDistanceField
    {
        internal const float Radius = 10f;
        internal readonly int MinX, MinZ, Width, Height;
        private readonly float[] fromRoad, along;
        private readonly bool[] touched;
        private readonly int[] originX, originZ;

        internal RoadDistanceField(int minX, int minZ, int width, int height)
        {
            MinX = minX; MinZ = minZ; Width = width; Height = height;
            int n = width * height;
            fromRoad = new float[n]; along = new float[n]; touched = new bool[n];
            originX = new int[n]; originZ = new int[n];
        }

        internal void Fill(float cx, float cz, float alongPath)
        {
            int lastX = MinX + Width - 1, lastZ = MinZ + Height - 1;
            int x0 = Mathf.Clamp(Mathf.FloorToInt(cx - Radius), MinX, lastX), x1 = Mathf.Clamp(Mathf.FloorToInt(cx + Radius), MinX, lastX);
            int z0 = Mathf.Clamp(Mathf.FloorToInt(cz - Radius), MinZ, lastZ), z1 = Mathf.Clamp(Mathf.FloorToInt(cz + Radius), MinZ, lastZ);
            int ox = Mathf.Clamp(Mathf.FloorToInt(cx), MinX, lastX), oz = Mathf.Clamp(Mathf.FloorToInt(cz), MinZ, lastZ);
            for (int x = x0; x <= x1; x++)
            {
                float dx = (x + 0.5f - cx) * (x + 0.5f - cx);
                for (int z = z0; z <= z1; z++)
                {
                    float d = Mathf.Sqrt(dx + (z + 0.5f - cz) * (z + 0.5f - cz));
                    int i = (z - MinZ) * Width + x - MinX;
                    if (!touched[i] || d < fromRoad[i])
                    {
                        fromRoad[i] = d; along[i] = alongPath;
                        originX[i] = ox; originZ[i] = oz;
                    }
                    touched[i] = true;
                }
            }
        }

        internal bool TryGet(int x, int z, out float from, out float alongPath, out int ox, out int oz)
        {
            from = alongPath = 0f; ox = oz = 0;
            if (x < MinX || z < MinZ || x >= MinX + Width || z >= MinZ + Height) return false;
            int i = (z - MinZ) * Width + x - MinX;
            if (!touched[i]) return false;
            from = fromRoad[i]; alongPath = along[i]; ox = originX[i]; oz = originZ[i];
            return true;
        }
    }

    internal sealed class RoadRules
    {
        internal TerrainDef Flagstone = TerrainDefOf.FlagstoneSandstone, Bridge = TerrainDefOf.Bridge, Gravel = TerrainDefOf.Gravel;
        internal TerrainDef WaterDeep = TerrainDefOf.WaterDeep, WaterShallow = TerrainDefOf.WaterShallow;
        internal TerrainDef OceanDeep = TerrainDefOf.WaterOceanDeep, OceanShallow = TerrainDefOf.WaterOceanShallow;

        internal static bool Supported(RoadDefGenStep step) => step.GetType() == typeof(RoadDefGenStep_Place)
            || step.GetType() == typeof(RoadDefGenStep_Bulldoze) || step.GetType() == typeof(RoadDefGenStep_DryWithFallback);

        internal IEnumerable<bool> Apply(RoadDef road, RoadDistanceField field, RoadSurface surface, TerrainDef rock,
            Func<int, int, int, float> random, Func<int, int, bool> writable)
        {
            List<RoadDefGenStep> steps = road.roadGenSteps;
            if (steps == null) yield break;
            int done = 0;
            for (int x = field.MinX; x < field.MinX + field.Width; x++)
            {
                for (int z = field.MinZ; z < field.MinZ + field.Height; z++)
                {
                    if (!field.TryGet(x, z, out float from, out float along, out int ox, out int oz)) continue;
                    if (!surface.Covers(x, z) || writable != null && !writable(x, z)) continue;
                    int draw = 0;
                    float jittered = Mathf.Abs(from + random(x, z, draw++) - 0.5f);
                    foreach (RoadDefGenStep step in steps)
                    {
                        if (step.chancePerPositionCurve == null) continue;
                        float at = Mathf.LerpUnclamped(from, jittered, step.antialiasingMultiplier);
                        float chance = step.chancePerPositionCurve.Evaluate(at);
                        if (chance <= 0f) continue;
                        if (step.periodicSpacing != 0 && along / (float)step.periodicSpacing % 1f * (float)step.periodicSpacing >= 1f) continue;
                        if (random(x, z, draw++) < chance) Place(step, field, surface, rock, x, z, ox, oz);
                    }
                    if (++done % 128 == 0) yield return false;
                }
            }
        }

        private void Place(RoadDefGenStep step, RoadDistanceField field, RoadSurface s, TerrainDef rock, int x, int z, int ox, int oz)
        {
            if (!Supported(step)) return;
            if (step is RoadDefGenStep_DryWithFallback dry)
            {
                Dry(s, x, z, dry.fallback);
                return;
            }
            var placing = step as RoadDefGenStep_Place;
            if (placing != null)
            {
                if (placing.place == null) return;
                if (placing.onlyIfOriginAllows && !OriginAllows(placing, field, s, x, z)) return;
                if (!placing.suppressOnTerrainTag.NullOrEmpty() && s.TerrainAt(x, z)?.HasTag(placing.suppressOnTerrainTag) == true) return;
            }
            s.Clear(x, z);
            if (placing?.place is ThingDef building)
            {
                if (!s.CanBuild(building, x, z)) return;
                if (placing.proximitySpacing != 0 && s.ThingNear(building, x, z, placing.proximitySpacing)) return;
                Dry(s, x, z, Gravel);
                s.PlaceThing(x, z, building);
                return;
            }
            if (!(placing?.place is TerrainDef terrain)) return;

            TerrainDef before = s.TerrainAt(x, z);
            if (terrain == Flagstone && rock != null) terrain = rock;
            if (terrain.bridge)
            {
                if (before == WaterDeep) s.Set(x, z, WaterShallow);
                if (before == OceanDeep) s.Set(x, z, OceanShallow);
            }
            if (before == null || before.bridge || !s.CanBuild(terrain, x, z)) return;
            if (!terrain.bridge && Bridge != null && s.CanBuild(Bridge, x, z)) return;
            if (before.HasTag("Road") && !before.layerable) s.Set(x, z, Gravel);
            s.Set(x, z, terrain);
        }

        private static bool OriginAllows(RoadDefGenStep_Place step, RoadDistanceField field, RoadSurface s, int x, int z)
        {
            for (int d = 0; d < 4; d++)
            {
                IntVec3 o = GenAdj.CardinalDirections[d];
                int nx = x + o.x, nz = z + o.z;
                if (!s.Covers(nx, nz) || !field.TryGet(nx, nz, out float from, out _, out int ox, out int oz)) continue;
                if (step.chancePerPositionCurve.Evaluate(from) > 0f
                    && (s.CanBuild(step.place, nx, nz) || s.TerrainAt(nx, nz) == step.place)
                    && s.Covers(ox, oz) && (s.CanBuild(step.place, ox, oz) || s.TerrainAt(ox, oz) == step.place))
                    return true;
            }
            return false;
        }

        private static void Dry(RoadSurface s, int x, int z, TerrainDef fallback)
        {
            for (int guard = 0; guard < 16 && s.TerrainAt(x, z)?.driesTo != null; guard++) s.Set(x, z, s.TerrainAt(x, z).driesTo);
            TerrainDef t = s.TerrainAt(x, z);
            if (t != null && (t.passability == Traversability.Impassable || t.IsRiver)) s.Set(x, z, fallback);
        }
    }
}
