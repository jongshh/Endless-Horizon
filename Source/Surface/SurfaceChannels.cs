using Verse;

namespace EndlessHorizon
{
    internal sealed partial class HorizonSurface
    {
        internal float Channel(ChannelKind kind, IntVec3 c)
        {
            switch (kind)
            {
                case ChannelKind.SpaceOutside: return Domain.Drawn(c) ? 1f : 0f;
                case ChannelKind.SpaceDistance: return Domain.Inside(c) ? 0f : Domain.Distance(c.x, c.z);
                case ChannelKind.Rock: return Rock(c) != null ? 1f : 0f;
                case ChannelKind.Roof:
                {
                    RoofDef roof = Roof(c);
                    return roof == null ? 0f : roof.isThickRoof ? 2f : 1f;
                }
                case ChannelKind.Fogged: return Fogged(c) ? 1f : 0f;
                case ChannelKind.Frozen: return Frozen(c) ? 1f : 0f;
                case ChannelKind.Polluted: return Polluted(c) ? 1f : 0f;
                case ChannelKind.Snow: return Snow(c.x, c.z);
                case ChannelKind.Exposed: return Exposed(c) ? 1f : 0f;
                case ChannelKind.Glow: return GroundGlow(c);
                case ChannelKind.FogOpacity: return Domain.Inside(c) ? 0f : FogMask.Opacity(c.x, c.z);
                case ChannelKind.WaterKind: return KindOf(Ground(c));
                case ChannelKind.WaterDeep: return DeepWater(c) ? 1f : 0f;
                case ChannelKind.WaterCovered: return CoveredWater(c) ? 1f : 0f;
                case ChannelKind.ShoreDistance: return ShoreDistance(c);
                case ChannelKind.FlowX: return Flow(c).x;
                case ChannelKind.FlowZ: return Flow(c).z;
                case ChannelKind.RiverIndex: return TryRiverPath(c, out int river, out _) ? river : -1f;
                case ChannelKind.RiverAlong: return TryRiverPath(c, out _, out float along) ? along : float.NaN;
                default: return float.NaN;
            }
        }
    }
}
