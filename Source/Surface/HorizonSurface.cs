using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal enum WeatherCover
    {
        Open,
        RoofedOutdoors,
        Indoors,
    }

    internal sealed partial class HorizonSurface
    {
        internal readonly Map Map;
        internal readonly HorizonDomain Domain;
        internal readonly int FogStart;
        private readonly HorizonField land;
        private OutsideGlow light;
        private FogMask fogMask;

        internal HorizonSurface(Map map, HorizonField outside, int fogStart)
        {
            Map = map;
            land = outside;
            Domain = outside.Domain;
            FogStart = fogStart;
        }

        internal HorizonSurface(Map map, HorizonDomain domain, int fogStart)
        {
            Map = map;
            Domain = domain;
            FogStart = fogStart;
        }

        internal bool Built => land != null;

        internal bool Answers(IntVec3 c) => Built ? Domain.InGrid(c) : Domain.Inside(c);

        internal int Ring => Domain.R;

        internal FogMask FogMask => fogMask ??= new FogMask(Map, Domain, FogStart);

        internal void SetLight(OutsideGlow value) => light = value;

        private MountainMask Mountains => land?.Mountains;

        internal TerrainDef Top(IntVec3 c) => Domain.Inside(c) ? Map.terrainGrid.TerrainAt(c) : land.TopAt(c);

        internal TerrainDef Ground(IntVec3 c) => Domain.Inside(c) ? Map.terrainGrid.BaseTerrainAt(c) : land.At(c);

        internal TerrainDef Floor(IntVec3 c) => Domain.Inside(c) ? Map.terrainGrid.FoundationAt(c) : land.RoadTopAt(c);

        internal bool HasFoundation(IntVec3 c) => Domain.Inside(c) ? Map.terrainGrid.FoundationAt(c) != null : land.BridgeAt(c);

        internal bool Polluted(IntVec3 c) => Domain.Inside(c) ? c.IsPolluted(Map) : land.PollutedAt(c);

        internal bool Frozen(IntVec3 c) => Domain.Inside(c)
            ? TerrainDefOf.ThinIce != null && Map.terrainGrid.TempTerrainAt(c) == TerrainDefOf.ThinIce
            : land.FrozenAt(c);

        internal TerrainDef Flooded(IntVec3 c)
        {
            if (Domain.Inside(c))
            {
                TerrainDef t = Map.terrainGrid.TempTerrainAt(c);
                return t != null && t.IsFlood ? t : null;
            }
            return land?.FloodAt(c);
        }

        internal bool Drowned(IntVec3 c, ThingDef plant)
        {
            if (Domain.Inside(c) || plant?.plant == null) return false;
            TerrainDef water = land?.FloodAt(c);
            if (water == null) return false;
            PlantProperties p = plant.plant;
            if (water.fertility < p.fertilityMin)
            {
                if (!p.destroyedByFlooding) return false;
                if (!p.completelyIgnoreFertility) return true;
            }
            HashSet<string> wild = p.WildTerrainTags;
            if (wild != null && wild.Count > 0 && (water.tags == null || !wild.Overlaps(water.tags))) return true;
            return false;
        }

        internal bool AnyWaterIn(CellRect rect) => land.AnyWaterIn(rect);

        internal bool IsRock(int x, int z)
        {
            if (Mountains != null) return Mountains.IsRock(x, z);
            return Domain.Inside(x, z) && Map.edificeGrid.InnerArray[Domain.MapIndex(x, z)]?.def.mineable == true;
        }

        internal bool OutsideRock(int x, int z) => Mountains != null && !Domain.Inside(x, z) && Mountains.IsRock(x, z);

        internal ThingDef PrintedRock(int x, int z) => Mountains?.RockDefAt(x, z);

        private ThingDef localRock;

        internal ThingDef Rock(IntVec3 c)
        {
            if (Domain.Inside(c))
            {
                ThingDef def = c.GetEdifice(Map)?.def;
                return def?.mineable == true && def.building != null && (def.building.isNaturalRock || def.building.isResourceRock) ? def : null;
            }
            if (!Domain.InGrid(c) || !OutsideRock(c.x, c.z)) return null;
            ThingDef printed = PrintedRock(c.x, c.z);
            if (printed != null) return printed;
            if (localRock == null)
            {
                foreach (ThingDef d in Find.World.NaturalRockTypesIn(Map.Tile))
                {
                    localRock = d;
                    break;
                }
            }
            return localRock;
        }

        internal ThingDef RoadBuildingAt(IntVec3 c) => Domain.Inside(c) ? null : land.RoadBuildingAt(c);

        internal RoofDef Roof(IntVec3 c)
        {
            if (Domain.Inside(c)) return Map.roofGrid.RoofAt(c);
            byte level = land.OutsideRoofAt(c);
            return level == 2 ? RoofDefOf.RoofRockThick : level == 1 ? RoofDefOf.RoofRockThin : null;
        }

        internal bool RoofShades(IntVec3 c)
        {
            if (OutsideRock(c.x, c.z)) return land.OutsideThickRoof(c);
            return land.InTunnel(c) || NaturalRoofOfSource(c);
        }

        private bool NaturalRoofOfSource(IntVec3 c)
        {
            int s = land.RawSampleAt(c);
            return s >= 0 && Map.roofGrid.RoofAt(s)?.isNatural == true;
        }

        internal bool Fogged(IntVec3 c) => Domain.Inside(c) ? Map.fogGrid.IsFogged(c) : land.OutsideFogged(c);

        internal bool Exposed(IntVec3 c)
        {
            if (Domain.Inside(c))
            {
                return Map.roofGrid.RoofAt(c) == null && !Map.fogGrid.IsFogged(c) && c.GetEdifice(Map)?.def.mineable != true;
            }
            if (OutsideRock(c.x, c.z) || land.InTunnel(c) || land.OutsideFogged(c)) return false;
            return !NaturalRoofOfSource(c);
        }

        internal WeatherCover Cover(IntVec3 c, out float overage)
        {
            overage = 0f;
            if (OutsideRock(c.x, c.z)) return WeatherCover.Indoors;
            byte tunnelRoof = land.TunnelRoof(c);
            if (tunnelRoof > 0)
            {
                if (tunnelRoof != 2) return WeatherCover.RoofedOutdoors;
                overage = 0.16f;
                return WeatherCover.Indoors;
            }
            int s = land.RawSampleAt(c);
            if (s < 0) return WeatherCover.Open;
            Building edifice = Map.edificeGrid.InnerArray[s];
            bool hide = land.OutsideFogged(c);
            RoofDef roof = Map.roofGrid.RoofAt(s);
            if (!hide && roof != null && roof.isNatural)
            {
                hide = edifice == null || edifice.def.Fillage != FillCategory.Full || edifice.def.size.x > 1 || edifice.def.size.z > 1
                    || edifice.def.holdsRoof || edifice.def.blockWeather;
            }
            if (!hide) return WeatherCover.Open;
            Room room = Map.cellIndices.IndexToCell(s).GetRoom(Map);
            if ((room == null || !room.ProperRoom) && (edifice == null || edifice.def.Fillage != FillCategory.Full) && (roof == null || !roof.isThickRoof))
                return WeatherCover.RoofedOutdoors;
            overage = edifice == null || (edifice.def.passability != Traversability.Impassable && !edifice.def.IsDoor) ? 0.16f : 0f;
            return WeatherCover.Indoors;
        }

        internal float Snow(int x, int z)
        {
            var c = new IntVec3(x, 0, z);
            if (Domain.Inside(c)) return Map.snowGrid.GetDepth(c);
            if (!Domain.InGrid(c)) c = Domain.ClampToGrid(c);
            TerrainDef t = land.TopAt(c);
            if (t == null || t.IsWater || t.IsFlood || land.InTunnel(c) || OutsideRock(c.x, c.z)) return 0f;
            int s = land.RawSampleAt(c);
            if (land.FrozenAt(c))
            {
                IntVec3 src = s < 0 ? IntVec3.Invalid : Map.cellIndices.IndexToCell(s);
                if (src.IsValid && TerrainDefOf.ThinIce != null && Map.terrainGrid.TempTerrainAt(src) == TerrainDefOf.ThinIce)
                    return Map.snowGrid.GetDepth(src);
                return IceSnow();
            }
            return s < 0 ? 0f : Map.snowGrid.GetDepth(Map.cellIndices.IndexToCell(s));
        }

        private int iceSnowFrame = -1;
        private float iceSnowDepth;

        private float IceSnow()
        {
            if (Time.frameCount == iceSnowFrame) return iceSnowDepth;
            iceSnowFrame = Time.frameCount;
            float ice = 0f, ground = 0f;
            int iceN = 0, groundN = 0;
            TerrainDef thin = TerrainDefOf.ThinIce;
            for (int z = 1; z < Domain.H; z += 3)
            {
                for (int x = 1; x < Domain.W; x += 3)
                {
                    if (!Domain.Inside(x, z)) continue;
                    var c = new IntVec3(x, 0, z);
                    float d = Map.snowGrid.GetDepth(c);
                    if (thin != null && Map.terrainGrid.TempTerrainAt(c) == thin)
                    {
                        ice += d;
                        iceN++;
                    }
                    else if (iceN == 0 && !Map.terrainGrid.TerrainAt(c).IsWater && !c.Roofed(Map))
                    {
                        ground += d;
                        groundN++;
                    }
                }
            }
            iceSnowDepth = iceN > 0 ? ice / iceN : groundN > 0 ? ground / groundN : 0f;
            return iceSnowDepth;
        }

        internal bool TryThingSource(IntVec3 c, out IntVec3 source)
        {
            int s = land.SampleAt(c);
            source = s < 0 ? IntVec3.Invalid : Map.cellIndices.IndexToCell(s);
            return s >= 0;
        }

        internal static bool ShownOutside(Thing thing) => OutsideSources.IsShown(thing);

        internal void PrintFailed(ThingDef def) => OutsideSources.PrintFailed(def);

        internal Plant PlantAt(IntVec3 c)
        {
            if (Domain.Inside(c)) return c.GetPlant(Map);
            if (OutsideRock(c.x, c.z) || !HorizonDebug.Things || !HorizonSettings.Current.things) return null;
            if (!TryThingSource(c, out IntVec3 source)) return null;
            Plant plant = source.GetPlant(Map);
            return plant != null && plant.Position == source && OutsideSources.IsShown(plant) && !Drowned(c, plant.def) ? plant : null;
        }

        internal Color32 Glow(int x, int z) => light?.At(x, z) ?? new Color32(0, 0, 0, 0);

        internal float GroundGlow(IntVec3 c)
        {
            if (Domain.Inside(c)) return Map.glowGrid.GroundGlowAt(c);
            float sky = land.OutsideRoofAt(c) > 0 ? 0f : Map.skyManager.CurSkyGlow;
            if (sky >= 1f) return sky;
            Color32 g = Glow(c.x, c.z);
            if (g.a == 1) return 1f;
            return Mathf.Max(sky, Mathf.Min(0.5f, Mathf.Max(Mathf.Max(g.r, g.g), g.b) / 255f * 3.6f));
        }
    }
}
