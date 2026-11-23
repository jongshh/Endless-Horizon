using System;
using System.Collections.Generic;

namespace EndlessHorizon
{
    internal enum ChannelKind
    {
        None,
        TerrainTop,
        TerrainBase,
        SpaceOutside,
        SpaceDistance,
        Rock,
        Roof,
        Fogged,
        Frozen,
        Polluted,
        Snow,
        Exposed,
        Glow,
        FogOpacity,
        WaterKind,
        WaterDeep,
        WaterCovered,
        ShoreDistance,
        FlowX,
        FlowZ,
        RiverIndex,
        RiverAlong,
    }

    internal static class Vocabulary
    {
        internal const string TerrainTop = "terrain.top", TerrainBase = "terrain.base";
        internal const string SpaceOutside = "space.outside", SpaceDistance = "space.distance";
        internal const string Rock = "surface.rock", Roof = "surface.roof", Fogged = "surface.fogged", Frozen = "surface.frozen",
            Polluted = "surface.polluted", Snow = "surface.snow";
        internal const string Exposed = "weather.exposed", Glow = "light.glow", FogOpacity = "fog.opacity";
        internal const string WaterKind = "water.kind", WaterDeep = "water.deep", WaterCovered = "water.covered",
            ShoreDistance = "water.shoreDistance", FlowX = "water.flow.x", FlowZ = "water.flow.z";
        internal const string RiverIndex = "river.index", RiverAlong = "river.along";

        internal static readonly string[] Channels =
        {
            TerrainTop, TerrainBase, SpaceOutside, SpaceDistance, Rock, Roof, Fogged, Frozen, Polluted, Snow,
            Exposed, Glow, FogOpacity, WaterKind, WaterDeep, WaterCovered, ShoreDistance, FlowX, FlowZ, RiverIndex, RiverAlong,
        };

        internal static readonly string[] IntegrationChannels =
        {
            "waves.kind", "waves.shoreDistance", "waves.calm", "waves.reach", "waves.clearance",
            "livingwaters.kind", "livingwaters.flavor", "livingwaters.shoreDistance", "livingwaters.depth", "livingwaters.seaDistance",
            "livingwaters.along", "livingwaters.marshDistance", "livingwaters.marshReach", "livingwaters.lake",
        };

        internal static readonly string[] Scopes = { "waves.shore", "livingwaters.water" };

        internal static readonly string[] BuiltInOwners = { "waves", "livingwaters", "quietwonders", "weatherreimagined", "realisticrain" };

        internal static readonly string[] Namespaces = { "terrain", "space", "surface", "weather", "light", "fog", "water", "river" };

        internal const string WaterSurface = "water.surface", WaterDepth = "water.depth";

        internal static readonly string[] Replacements =
        {
            "waves", "livingwaters", "quietwonders", "weatherreimagined", "realisticrain", WaterSurface, WaterDepth,
        };

        internal const string Before = "before", AfterTerrain = "afterTerrain", BeforeLighting = "beforeLighting", BeforeFog = "beforeFog", After = "after";

        internal static readonly string[] Stages = { Before, AfterTerrain, BeforeLighting, BeforeFog, After };

        internal const string Built = "built", Disposed = "disposed", SettingsChanged = "settings";

        internal static readonly string[] Events = { Built, Disposed, SettingsChanged };

        internal static readonly string[] GroundChannels =
        {
            TerrainTop, TerrainBase, Rock, Roof, Exposed, WaterKind, WaterDeep, WaterCovered, ShoreDistance,
        };

        private static readonly Dictionary<string, ChannelKind> kinds = new Dictionary<string, ChannelKind>(StringComparer.Ordinal)
        {
            [TerrainTop] = ChannelKind.TerrainTop,
            [TerrainBase] = ChannelKind.TerrainBase,
            [SpaceOutside] = ChannelKind.SpaceOutside,
            [SpaceDistance] = ChannelKind.SpaceDistance,
            [Rock] = ChannelKind.Rock,
            [Roof] = ChannelKind.Roof,
            [Fogged] = ChannelKind.Fogged,
            [Frozen] = ChannelKind.Frozen,
            [Polluted] = ChannelKind.Polluted,
            [Snow] = ChannelKind.Snow,
            [Exposed] = ChannelKind.Exposed,
            [Glow] = ChannelKind.Glow,
            [FogOpacity] = ChannelKind.FogOpacity,
            [WaterKind] = ChannelKind.WaterKind,
            [WaterDeep] = ChannelKind.WaterDeep,
            [WaterCovered] = ChannelKind.WaterCovered,
            [ShoreDistance] = ChannelKind.ShoreDistance,
            [FlowX] = ChannelKind.FlowX,
            [FlowZ] = ChannelKind.FlowZ,
            [RiverIndex] = ChannelKind.RiverIndex,
            [RiverAlong] = ChannelKind.RiverAlong,
        };

        internal static ChannelKind KindOf(string id) => id != null && kinds.TryGetValue(id, out ChannelKind kind) ? kind : ChannelKind.None;

        internal static bool IsChannel(string id) => KindOf(id) != ChannelKind.None;

        internal static bool IsTerrainChannel(string id) => id == TerrainTop || id == TerrainBase;
    }
}
