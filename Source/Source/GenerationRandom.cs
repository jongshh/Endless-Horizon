using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace EndlessHorizon
{
    internal sealed class GenerationRandom
    {
        private static readonly PropertyInfo compressed = AccessTools.Property(typeof(Rand), "StateCompressed");
        private object state;
        internal GenerationRandom(int seed)
        {
            Rand.PushState(seed);
            try { state = compressed.GetValue(null); }
            finally { Rand.PopState(); }
        }
        internal IDisposable Enter() => new Slice(this);
        private sealed class Slice : IDisposable
        {
            private GenerationRandom owner;
            internal Slice(GenerationRandom owner)
            {
                this.owner = owner; Rand.PushState();
                try { compressed.SetValue(null, owner.state); }
                catch { Rand.PopState(); throw; }
            }
            public void Dispose()
            {
                if (owner == null) return;
                try { owner.state = compressed.GetValue(null); }
                finally { Rand.PopState(); owner = null; }
            }
        }
    }
}
