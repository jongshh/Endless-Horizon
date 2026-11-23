using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace EndlessHorizon.Api
{
    public static class HorizonWorld
    {
        private static bool At(Map map, IntVec3 c, out HorizonSurface surface)
        {
            surface = ApiSupport.SurfaceOf(map);
            return surface != null && surface.Answers(c);
        }

        public static TerrainDef TerrainAt(Map map, IntVec3 c)
        {
            try { return At(map, c, out HorizonSurface s) ? s.Top(c) : null; }
            catch (Exception ex) { ApiSupport.Failed(nameof(TerrainAt), ex); return null; }
        }

        public static TerrainDef BaseTerrainAt(Map map, IntVec3 c)
        {
            try { return At(map, c, out HorizonSurface s) ? s.Ground(c) : null; }
            catch (Exception ex) { ApiSupport.Failed(nameof(BaseTerrainAt), ex); return null; }
        }

        public static ThingDef RockAt(Map map, IntVec3 c)
        {
            try { return At(map, c, out HorizonSurface s) ? s.Rock(c) : null; }
            catch (Exception ex) { ApiSupport.Failed(nameof(RockAt), ex); return null; }
        }

        public static RoofDef RoofAt(Map map, IntVec3 c)
        {
            try { return At(map, c, out HorizonSurface s) ? s.Roof(c) : null; }
            catch (Exception ex) { ApiSupport.Failed(nameof(RoofAt), ex); return null; }
        }

        public static bool FoggedAt(Map map, IntVec3 c)
        {
            try { return At(map, c, out HorizonSurface s) && s.Fogged(c); }
            catch (Exception ex) { ApiSupport.Failed(nameof(FoggedAt), ex); return false; }
        }

        public static Plant PlantAt(Map map, IntVec3 c)
        {
            try { return At(map, c, out HorizonSurface s) ? s.PlantAt(c) : null; }
            catch (Exception ex) { ApiSupport.Failed(nameof(PlantAt), ex); return null; }
        }

        public static Vector3 WaterFlowAt(Map map, IntVec3 c)
        {
            try { return At(map, c, out HorizonSurface s) ? s.Flow(c) : Vector3.zero; }
            catch (Exception ex) { ApiSupport.Failed(nameof(WaterFlowAt), ex); return Vector3.zero; }
        }

        public static int RiverCount(Map map)
        {
            try { return ApiSupport.SurfaceOf(map)?.RiverCount ?? 0; }
            catch (Exception ex) { ApiSupport.Failed(nameof(RiverCount), ex); return 0; }
        }

        public static bool TryGetRiver(Map map, int river, out Vector2 start, out Vector2 outward, out float width)
        {
            start = outward = Vector2.zero;
            width = 0f;
            try { return ApiSupport.SurfaceOf(map)?.TryRiver(river, out start, out outward, out width) == true; }
            catch (Exception ex) { ApiSupport.Failed(nameof(TryGetRiver), ex); return false; }
        }

        public static bool TryRiverPath(Map map, IntVec3 c, out int river, out float along)
        {
            river = -1;
            along = 0f;
            try { return At(map, c, out HorizonSurface s) && s.TryRiverPath(c, out river, out along); }
            catch (Exception ex) { ApiSupport.Failed(nameof(TryRiverPath), ex); return false; }
        }

        public static float Value(Map map, string channel, IntVec3 c)
        {
            try
            {
                if (channel == null || !At(map, c, out HorizonSurface s)) return float.NaN;
                ChannelKind kind = Vocabulary.KindOf(channel);
                if (kind != ChannelKind.None) return s.Channel(kind, c);
                ChannelRegistration registered = ExtensionRegistry.ChannelOf(channel);
                return registered == null || registered.Failed ? float.NaN : Read(registered, map, c);
            }
            catch (Exception ex) { ApiSupport.Failed(nameof(Value), ex); return float.NaN; }
        }

        private static float Read(ChannelRegistration registered, Map map, IntVec3 c)
        {
            try { return registered.Read(map, c); }
            catch (Exception ex)
            {
                ExtensionRegistry.Fail(registered, ex);
                return float.NaN;
            }
        }

        private static bool Fits(Map map, CellRect area, Array destination)
        {
            if (map == null || destination == null) return false;
            long width = (long)area.maxX - area.minX + 1, height = (long)area.maxZ - area.minZ + 1;
            return width > 0 && height > 0 && width * height <= destination.Length;
        }

        public static bool Copy(Map map, string channel, CellRect area, float[] destination)
        {
            try
            {
                if (channel == null || !Fits(map, area, destination)) return false;
                ChannelKind kind = Vocabulary.KindOf(channel);
                bool own = kind != ChannelKind.None;
                if (own && Vocabulary.IsTerrainChannel(channel)) return false;
                ChannelRegistration registered = own ? null : ExtensionRegistry.ChannelOf(channel);
                if (!own && (registered == null || registered.Failed)) return false;
                HorizonSurface s = ApiSupport.SurfaceOf(map);
                int i = 0;
                for (int z = area.minZ; z <= area.maxZ; z++)
                {
                    for (int x = area.minX; x <= area.maxX; x++)
                    {
                        var c = new IntVec3(x, 0, z);
                        destination[i++] = !s.Answers(c) ? float.NaN : own ? s.Channel(kind, c)
                            : registered.Failed ? float.NaN : Read(registered, map, c);
                    }
                }
                return true;
            }
            catch (Exception ex) { ApiSupport.Failed(nameof(Copy), ex); return false; }
        }

        public static bool CopyTerrain(Map map, string channel, CellRect area, TerrainDef[] destination)
        {
            try
            {
                if (channel == null || !Vocabulary.IsTerrainChannel(channel) || !Fits(map, area, destination)) return false;
                bool top = channel == Vocabulary.TerrainTop;
                HorizonSurface s = ApiSupport.SurfaceOf(map);
                int i = 0;
                for (int z = area.minZ; z <= area.maxZ; z++)
                {
                    for (int x = area.minX; x <= area.maxX; x++)
                    {
                        var c = new IntVec3(x, 0, z);
                        destination[i++] = !s.Answers(c) ? null : top ? s.Top(c) : s.Ground(c);
                    }
                }
                return true;
            }
            catch (Exception ex) { ApiSupport.Failed(nameof(CopyTerrain), ex); return false; }
        }
    }
}
