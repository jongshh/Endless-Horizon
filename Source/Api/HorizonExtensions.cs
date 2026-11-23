using System;
using System.Collections.Generic;
using Verse;

namespace EndlessHorizon.Api
{
    public static class HorizonExtensions
    {
        public static bool RegisterLayer(string owner, Type sectionLayerType, string[] scopes,
                                         Func<Map, CellRect, bool> wanted, bool joinMapEdge, bool callDrawLayer)
        {
            try { return ExtensionRegistry.AddLayer(owner, sectionLayerType, scopes, wanted, joinMapEdge, callDrawLayer); }
            catch (Exception ex) { ApiSupport.Failed(nameof(RegisterLayer), ex); return false; }
        }

        public static bool RegisterBuildStep(string owner, string id, Func<Map, IEnumerable<bool>> build, string[] dependsOn)
        {
            try { return ExtensionRegistry.AddStep(owner, id, build, dependsOn); }
            catch (Exception ex) { ApiSupport.Failed(nameof(RegisterBuildStep), ex); return false; }
        }

        public static bool RegisterChannel(string owner, string id, Func<Map, IntVec3, float> read)
        {
            try { return ExtensionRegistry.AddChannel(owner, id, read); }
            catch (Exception ex) { ApiSupport.Failed(nameof(RegisterChannel), ex); return false; }
        }

        public static bool RegisterScope(string owner, string id, Func<Map, IDisposable> enter)
        {
            try { return ExtensionRegistry.AddScope(owner, id, enter); }
            catch (Exception ex) { ApiSupport.Failed(nameof(RegisterScope), ex); return false; }
        }

        public static IDisposable Enter(Map map, string scope)
        {
            try { return map == null ? null : ExtensionScopes.Enter(map, scope); }
            catch (Exception ex) { ApiSupport.Failed(nameof(Enter), ex); return null; }
        }

        public static bool RegisterDraw(string owner, string stage, Action<Map, CellRect> draw)
        {
            try { return ExtensionRegistry.AddDraw(owner, stage, draw); }
            catch (Exception ex) { ApiSupport.Failed(nameof(RegisterDraw), ex); return false; }
        }

        public static bool Replace(string owner, string id)
        {
            try { return ExtensionRegistry.Replace(owner, id); }
            catch (Exception ex) { ApiSupport.Failed(nameof(Replace), ex); return false; }
        }

        public static string StateOf(string id)
        {
            try { return ExtensionRegistry.StateOf(id); }
            catch (Exception ex) { ApiSupport.Failed(nameof(StateOf), ex); return "absent"; }
        }
    }
}
