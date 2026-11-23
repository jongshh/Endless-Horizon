using System;
using Verse;

namespace EndlessHorizon.Api
{
    public static class HorizonView
    {
        public static bool SetExternalDrawing(Map map, string owner, bool external)
        {
            try { return map != null && ExtensionRegistry.OwnerAllowed(owner) && ExtensionMaps.SetExternal(map, owner, external); }
            catch (Exception ex) { ApiSupport.Failed(nameof(SetExternalDrawing), ex); return false; }
        }

        public static bool Draw(Map map, CellRect view)
        {
            try
            {
                if (map == null || ExtensionMaps.ExternalOwner(map) == null) return false;
                return ExtensionMaps.DrawMap?.Invoke(map, view) == true;
            }
            catch (Exception ex) { ApiSupport.Failed(nameof(Draw), ex); return false; }
        }

        public static Map DrawingMap => ExtensionMaps.DrawingMap;

        public static CellRect ViewOf(Map map)
        {
            try { return ExtensionMaps.ViewOf(map); }
            catch (Exception ex) { ApiSupport.Failed(nameof(ViewOf), ex); return CellRect.Empty; }
        }
    }
}
