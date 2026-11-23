using System;
using System.Collections.Generic;
using Verse;

namespace EndlessHorizon
{
    internal static class ExtensionScopes
    {
        private sealed class Guarded : IDisposable
        {
            internal ScopeRegistration Scope;
            internal IDisposable Inner;

            public void Dispose()
            {
                IDisposable leaving = Inner;
                if (leaving == null) return;
                Inner = null;
                try { leaving.Dispose(); }
                catch (Exception ex) { ExtensionRegistry.Fail(Scope, ex); }
            }
        }

        private sealed class Several : IDisposable
        {
            internal readonly List<IDisposable> Entered = new List<IDisposable>();

            public void Dispose()
            {
                for (int i = Entered.Count - 1; i >= 0; i--) Entered[i].Dispose();
                Entered.Clear();
            }
        }

        internal static IDisposable Enter(Map map, string id)
        {
            ScopeRegistration scope = ExtensionRegistry.ScopeOf(id);
            if (scope == null || scope.Failed) return null;
            try
            {
                IDisposable inner = scope.Enter(map);
                return inner == null ? null : new Guarded { Scope = scope, Inner = inner };
            }
            catch (Exception ex)
            {
                ExtensionRegistry.Fail(scope, ex);
                return null;
            }
        }

        internal static IDisposable EnterAll(Map map, string[] ids)
        {
            if (ids.Length == 0) return null;
            if (ids.Length == 1) return Enter(map, ids[0]);
            var several = new Several();
            foreach (string id in ids)
            {
                IDisposable entered = Enter(map, id);
                if (entered != null) several.Entered.Add(entered);
            }
            return several;
        }
    }
}
