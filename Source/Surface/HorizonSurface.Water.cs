using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal sealed partial class HorizonSurface
    {
        internal const int Land = 0, StillWater = 1, MovingWater = 2, Sea = 3, Marsh = 4;

        internal static int KindOf(TerrainDef t)
        {
            if (t == null || !t.IsWater) return Land;
            if (t.IsOcean) return Sea;
            if (t.waterDepthShader == null) return Marsh;
            return t.IsRiver ? MovingWater : StillWater;
        }

        internal bool DeepWater(IntVec3 c)
        {
            TerrainDef t = Ground(c);
            return t != null && t.IsWater && t.passability == Traversability.Impassable;
        }

        internal bool CoveredWater(IntVec3 c)
        {
            TerrainDef t = Ground(c);
            if (t == null || !t.IsWater) return false;
            return Domain.Inside(c) ? Map.terrainGrid.TerrainAt(c) != t && Map.terrainGrid.TempTerrainAt(c) == null : land.BridgeAt(c);
        }

        private const float ShoreLimit = 32f;
        private float[] shore;
        private int shoreVersion = -1;

        internal int WaterVersion { get; private set; }

        internal void WaterChanged() => WaterVersion++;

        internal float ShoreDistance(IntVec3 c)
        {
            if (!Domain.InGrid(c)) return float.NaN;
            int version = Built ? WaterVersion : Time.frameCount;
            if (shore == null || shoreVersion != version)
            {
                shoreVersion = version;
                var water = new bool[Domain.Cells];
                for (int z = -Ring; z < Domain.H + Ring; z++)
                    for (int x = -Ring; x < Domain.W + Ring; x++)
                                            {
                        var at = new IntVec3(x, 0, z);
                        if (Answers(at)) water[Domain.Index(x, z)] = Ground(at)?.IsWater == true;
                    }
                float[] toLand = DistanceField.Chamfer(Domain.Stride, Domain.Rows, i => !water[i], ShoreLimit + 0.5f);
                float[] toWater = DistanceField.Chamfer(Domain.Stride, Domain.Rows, i => water[i], ShoreLimit + 0.5f);
                shore = new float[water.Length];
                for (int i = 0; i < shore.Length; i++)
                    shore[i] = Mathf.Clamp(water[i] ? toLand[i] - 0.5f : 0.5f - toWater[i], -ShoreLimit, ShoreLimit);
            }
            return shore[Domain.Index(c.x, c.z)];
        }

        internal int RiverCount => land?.River?.Crossings.Count ?? 0;

        internal bool TryRiver(int river, out Vector2 start, out Vector2 outward, out float width)
        {
            start = outward = Vector2.zero;
            width = 0f;
            if (river < 0 || river >= RiverCount) return false;
            RiverCrossing crossing = land.River.Crossings[river];
            start = crossing.start;
            outward = crossing.outDir;
            width = crossing.width;
            return true;
        }

        internal bool TryRiverPath(IntVec3 c, out int river, out float along)
        {
            river = -1;
            along = 0f;
            return land.River != null && !Domain.Inside(c) && land.River.TryPathAt(c.x, c.z, out river, out along);
        }

        private float[] flowSigns;

        internal Vector3 Flow(IntVec3 c)
        {
            if (Domain.Inside(c)) return Map.waterInfo?.GetWaterMovement(c.ToVector3Shifted()) ?? Vector3.zero;
            GenerationSnapshot snapshot = land.Generator?.Snapshot;
            if (snapshot?.OwnsFlow == true && snapshot.Covers(c.x, c.z)) return snapshot.WaterMovementAt(c.x, c.z);
            RiverExtension river = land.River;
            if (river == null || !river.TryPathAt(c.x, c.z, out int index, out float _) || index < 0 || index >= river.Crossings.Count) return Vector3.zero;
            flowSigns ??= FlowSigns(river.Crossings);
            Vector2 d = river.Crossings[index].outDir.normalized;
            return new Vector3(d.x, 0f, d.y) * flowSigns[index];
        }

        private float[] FlowSigns(List<RiverCrossing> crossings)
        {
            var signs = new float[crossings.Count];
            for (int i = 0; i < signs.Length; i++)
            {
                RiverCrossing cr = crossings[i];
                Vector2 inward = -cr.outDir.normalized;
                IntVec3 cell = Domain.Nearest(new IntVec3(Mathf.FloorToInt(cr.start.x + inward.x * 1.5f), 0, Mathf.FloorToInt(cr.start.y + inward.y * 1.5f)));
                Vector3 m = Map.waterInfo != null ? Map.waterInfo.GetWaterMovement(cell.ToVector3Shifted()) : Vector3.zero;
                float dot = m.x * cr.outDir.x + m.z * cr.outDir.y;
                signs[i] = m.sqrMagnitude < 1e-6f ? 0f : Mathf.Sign(dot) * m.magnitude;
            }
            return signs;
        }
    }
}
