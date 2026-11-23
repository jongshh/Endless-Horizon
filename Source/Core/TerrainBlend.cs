using System;
using System.Collections.Generic;
using System.Reflection;
using Verse;

namespace EndlessHorizon
{
    internal static class TerrainBlend
    {
        private static readonly Dictionary<Type, (PropertyInfo baseP, PropertyInfo coverP)?> props = new Dictionary<Type, (PropertyInfo, PropertyInfo)?>();

        public static bool TryParts(TerrainDef t, out TerrainDef baseTerrain, out TerrainDef cover)
        {
            baseTerrain = cover = null;
            if (t == null) return false;
            Type type = t.GetType();
            if (type == typeof(TerrainDef)) return false;
            if (!props.TryGetValue(type, out var p))
            {
                PropertyInfo b = type.GetProperty("BaseTerrain", BindingFlags.Public | BindingFlags.Instance);
                PropertyInfo c = type.GetProperty("CoverTerrain", BindingFlags.Public | BindingFlags.Instance);
                p = b != null && c != null && b.PropertyType == typeof(TerrainDef) && c.PropertyType == typeof(TerrainDef) ? (b, c) : ((PropertyInfo, PropertyInfo)?)null;
                props[type] = p;
            }
            if (p == null) return false;
            baseTerrain = p.Value.baseP.GetValue(t) as TerrainDef;
            cover = p.Value.coverP.GetValue(t) as TerrainDef;
            return true;
        }

        public static bool Matches(TerrainDef actual, TerrainDef predicted)
        {
            if (actual == predicted) return true;
            return TryParts(actual, out TerrainDef b, out TerrainDef c) && (b == predicted || c == predicted);
        }
    }
}
