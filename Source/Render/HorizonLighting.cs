using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal sealed partial class HorizonLighting
    {
        private readonly Mesh mesh;

        public HorizonLighting(HorizonDomain domain)
        {
            mesh = FrameMesh.Make(domain, domain.R, FrameMesh.Far, AltitudeLayer.LightingOverlay.AltitudeFor(), new Color32(0, 0, 0, 0));
        }

        public void Draw()
        {
            Graphics.DrawMesh(mesh, Matrix4x4.identity, MatBases.LightOverlay, 0);
        }

        public void Dispose() => Object.Destroy(mesh);
    }
}
