using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal static class ShiftedClippers
    {
        private const float ClipSize = 500f;
        private static readonly MaterialPropertyBlock vertBlock = new MaterialPropertyBlock();
        private static readonly MaterialPropertyBlock horBlock = new MaterialPropertyBlock();

        public static void Draw(Map map, HorizonDomain domain, Material overrideMat = null)
        {
            if (!map.DrawMapClippers || domain.Limited)
            {
                return;
            }
            Material mat = overrideMat ?? map.MapEdgeMaterial;
            var size = new IntVec3(domain.W, 0, domain.H);
            int offset = domain.R;
            float alt = AltitudeLayer.WorldClipper.AltitudeFor();
            float half = ClipSize / 2f;

            var scale = new Vector3(ClipSize, 1f, size.z + 2f * offset);
            DrawPlane(mat, horBlock, scale, new Vector3(-offset - half, 0f, size.z / 2f), alt);
            DrawPlane(mat, horBlock, scale, new Vector3(size.x + offset + half, 0f, size.z / 2f), alt);

            scale = new Vector3(size.x + 2f * offset + 2f * ClipSize, 1f, ClipSize);
            DrawPlane(mat, vertBlock, scale, new Vector3(size.x / 2f, 0f, size.z + offset + half), alt);
            DrawPlane(mat, vertBlock, scale, new Vector3(size.x / 2f, 0f, -offset - half), alt);
        }

        private static void DrawPlane(Material mat, MaterialPropertyBlock block, Vector3 scale, Vector3 center, float alt)
        {
            block.SetVector(ShaderPropertyIDs.MainTextureScale, scale);
            block.SetVector(ShaderPropertyIDs.MainTextureOffset, center);
            Matrix4x4 matrix = default;
            matrix.SetTRS(center.WithYOffset(alt), Quaternion.identity, scale);
            Graphics.DrawMesh(MeshPool.plane10, matrix, mat, 0, null, 0, block);
        }
    }
}
