using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace EndlessHorizon
{
    internal sealed class GenerationMetrics
    {
        internal const int ErrorFloor = 16;
        internal sealed class Group
        {
            internal int Cells,TerrainErrors,BiomeErrors,WaterErrors,RockErrors;
            internal int Judged,JudgedTerrain,JudgedWater,JudgedRock;
        }
        internal readonly Dictionary<string,Group> Groups=new();
        internal int BiomeErrors;
        private static readonly HashSet<string> positionGroups=new() { "west","east","south","north","corners" };
        internal static int Limit(int cells,float share) => System.Math.Max(ErrorFloor,(int)(cells*share));
        internal bool Acceptable => Groups.Count>0 && !Rejections().Any();
        internal IEnumerable<string> Rejections()
        {
            foreach (var pair in Groups.OrderBy(p=>p.Key))
            {
                if (positionGroups.Contains(pair.Key)) continue;
                Group g=pair.Value;
                if (g.BiomeErrors>Limit(g.Cells,0.05f)) yield return $"{pair.Key} biome {g.BiomeErrors}/{g.Cells}";
                if (g.JudgedWater>Limit(g.Judged,0.05f)) yield return $"{pair.Key} water {g.JudgedWater}/{g.Judged}";
                if (g.JudgedTerrain>Limit(g.Judged,0.2f)) yield return $"{pair.Key} terrain {g.JudgedTerrain}/{g.Judged}";
                if (g.JudgedRock>Limit(g.Judged,0.1f)) yield return $"{pair.Key} rock {g.JudgedRock}/{g.Judged}";
            }
        }
        internal bool Exact => Groups.TryGetValue("all",out Group g) && g.TerrainErrors==0 && g.BiomeErrors==0 && g.WaterErrors==0 && g.RockErrors==0;
        private static string Water(TerrainDef t) => t==null?"missing":t.IsOcean?"ocean":t.IsRiver?"river":t.IsWater?"freshwater":"land";
        internal void Record(int x,int z,int w,int h,BiomeDef actualBiome,BiomeDef predictedBiome,TerrainDef actual,TerrainDef predicted,bool terrainEqual,bool rockEqual,bool external=false)
        {
            bool biomeEqual=actualBiome==predictedBiome;
            if(!biomeEqual) BiomeErrors++;
            var keys=new List<string> { "all", "biome:"+(actualBiome?.defName??"missing"),"water:"+Water(actual) };
            if(x<3) keys.Add("west"); if(x>=w-3) keys.Add("east");
            if(z<3) keys.Add("south"); if(z>=h-3) keys.Add("north");
            if((x<3||x>=w-3)&&(z<3||z>=h-3)) keys.Add("corners");
            foreach(string key in keys)
            {
                if(!Groups.TryGetValue(key,out Group g)) Groups.Add(key,g=new Group());
                g.Cells++; if(!terrainEqual) g.TerrainErrors++; if(!biomeEqual) g.BiomeErrors++;
                bool waterEqual=Water(actual)==Water(predicted);
                if(!waterEqual) g.WaterErrors++; if(!rockEqual) g.RockErrors++;
                if(external) continue;
                g.Judged++;
                if(!terrainEqual) g.JudgedTerrain++; if(!waterEqual) g.JudgedWater++; if(!rockEqual) g.JudgedRock++;
            }
        }
        internal string Summary() => string.Join("; ",Groups.OrderBy(p=>p.Key).Select(p=>$"{p.Key} n={p.Value.Cells} errors terrain/biome/water/rock={p.Value.TerrainErrors}/{p.Value.BiomeErrors}/{p.Value.WaterErrors}/{p.Value.RockErrors} external={p.Value.Cells-p.Value.Judged}"));
    }
}
