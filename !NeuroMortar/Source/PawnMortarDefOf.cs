using RimWorld;
using Verse;

namespace PawnMortar
{
    /// <summary>
    /// Def 引用。
    /// </summary>
    [DefOf]
    public static class PawnMortarDefOf
    {
        public static JobDef EnterPawnMortar;
        public static JobDef CarryToPawnMortar;

        static PawnMortarDefOf()
            => DefOfHelper.EnsureInitializedInCtor(typeof(PawnMortarDefOf));
    }
}