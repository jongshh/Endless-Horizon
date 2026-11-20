using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    public sealed class FogTint
    {
        private static readonly AccessTools.FieldRef<MapDrawer, Section[,]> sectionsRef =
            AccessTools.FieldRefAccess<MapDrawer, Section[,]>("sections");

        private const int Band = 3;

        private readonly int w, h;
        private readonly Color32?[] cellColor;
        private Color32 mean = new Color32(255, 255, 255, 255);
        public bool Tinted;
        private string reason = "vanilla";

        private static bool FogLayerPatchedByOthers(out string owners)
        {
            owners = "";
            MethodInfo regen = AccessTools.Method(typeof(SectionLayer_FogOfWar), nameof(SectionLayer_FogOfWar.Regenerate));
            Patches info = regen == null ? null : Harmony.GetPatchInfo(regen);
            if (info == null) return false;
            var list = info.Postfixes.Concat(info.Transpilers).Concat(info.Prefixes)
                .Select(p => p.owner).Where(o => o != "jongsh.endlesshorizon").Distinct().ToList();
            owners = string.Join(",", list);
            return list.Count > 0;
        }

        private FogTint(Map map)
        {
            w = map.Size.x;
            h = map.Size.z;
            cellColor = new Color32?[w * h];
            if (!FogLayerPatchedByOthers(out string owners))
            {
                return;
            }
            reason = "patched by " + owners;
            Section[,] sections = sectionsRef(map.mapDrawer);
            if (sections == null)
            {
                return;
            }
            var sum = new Vector3[w * h];
            var count = new int[w * h];
            Vector3 all = Vector3.zero;
            long allCount = 0;
            foreach (Section section in sections)
            {
                if (section == null) continue;
                SectionLayer layer = section.GetLayer(typeof(SectionLayer_FogOfWar));
                if (layer != null)
                {

                    foreach (LayerSubMesh sm in layer.subMeshes)
                    {
                        if (sm.material != MatBases.FogOfWar || sm.disabled) continue;
                        List<Vector3> verts = sm.verts;
                        List<Color32> colors = sm.colors;
                        int n = Mathf.Min(verts.Count, colors.Count);
                        for (int i = 0; i < n; i++)
                        {
                            Color32 c = colors[i];
                            if (c.r == 0 && c.g == 0 && c.b == 0) continue;
                            if (c.a > 0)
                            {
                                all += new Vector3(c.r, c.g, c.b);
                                allCount++;
                            }
                            int x = Mathf.Clamp(Mathf.FloorToInt(verts[i].x), 0, w - 1);
                            int z = Mathf.Clamp(Mathf.FloorToInt(verts[i].z), 0, h - 1);
                            if (x >= Band && z >= Band && x < w - Band && z < h - Band) continue;
                            int k = z * w + x;
                            sum[k] += new Vector3(c.r, c.g, c.b);
                            count[k]++;
                        }
                    }
                }
            }
            if (allCount > 0)
            {
                Vector3 m = all / allCount;
                mean = new Color32((byte)Mathf.RoundToInt(m.x), (byte)Mathf.RoundToInt(m.y), (byte)Mathf.RoundToInt(m.z), 255);
            }
            var queue = new Queue<int>();
            for (int k = 0; k < sum.Length; k++)
            {
                if (count[k] == 0) continue;
                Vector3 v = sum[k] / count[k];
                var c = new Color32((byte)Mathf.RoundToInt(v.x), (byte)Mathf.RoundToInt(v.y), (byte)Mathf.RoundToInt(v.z), 255);
                cellColor[k] = c;
                queue.Enqueue(k);
                if (c.r < 250 || c.g < 250 || c.b < 250) Tinted = true;
            }
            if (mean.r < 250 || mean.g < 250 || mean.b < 250) Tinted = true;
            while (queue.Count > 0)
            {
                int k = queue.Dequeue();
                int x = k % w, z = k / w;
                for (int d = 0; d < 4; d++)
                {
                    int xx = x + (d == 0 ? 1 : d == 1 ? -1 : 0), zz = z + (d == 2 ? 1 : d == 3 ? -1 : 0);
                    if (xx < 0 || zz < 0 || xx >= w || zz >= h) continue;
                    if (xx >= Band && zz >= Band && xx < w - Band && zz < h - Band) continue;
                    int j = zz * w + xx;
                    if (cellColor[j].HasValue) continue;
                    cellColor[j] = cellColor[k];
                    queue.Enqueue(j);
                }
            }
        }

        public static FogTint Build(Map map)
        {
            try
            {
                return new FogTint(map);
            }
            catch (System.Exception ex)
            {
                Log.WarningOnce(EndlessHorizonMod.LogPrefix + "Reading the map fog colour failed; outside fog stays vanilla white. " + ex, 0x45484654);
                return null;
            }
        }

        public string Report => Tinted ? $"tinted (mean {mean.r},{mean.g},{mean.b}; {reason})" : reason;

        public Color32 At(int x, int z)
        {
            if (!Tinted)
            {
                return new Color32(255, 255, 255, 255);
            }
            int cx = Mathf.Clamp(x, 0, w - 1), cz = Mathf.Clamp(z, 0, h - 1);
            return cellColor[cz * w + cx] ?? mean;
        }
    }
}
