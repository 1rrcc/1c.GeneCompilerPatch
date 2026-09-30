using RimWorld;
using Verse;

namespace GeneCompilerPatch
{
    /// <summary>按 defName 取 UniversalGeneCompiler 的 def，避免对其程序集的编译期依赖。</summary>
    internal static class UGCDefs
    {
        private static bool lookedUp;
        private static HediffDef geneExtractionCooldown;

        internal static HediffDef GeneExtractionCooldown
        {
            get
            {
                if (!lookedUp)
                {
                    lookedUp = true;
                    geneExtractionCooldown = DefDatabase<HediffDef>.GetNamedSilentFail("UGC_GeneExtractionCooldown");
                }
                return geneExtractionCooldown;
            }
        }

        internal static bool IsOnCooldown(Pawn pawn)
        {
            HediffDef def = GeneExtractionCooldown;
            if (def == null || pawn == null || pawn.health == null || pawn.health.hediffSet == null)
            {
                return false;
            }
            return pawn.health.hediffSet.HasHediff(def, false);
        }

        /// <summary>
        /// 取舱内人员。通用基因编译器继承自 Building_Enterable，内部容器是公开的。
        /// </summary>
        internal static Pawn OccupantOf(Building_Enterable building)
        {
            if (building == null || building.innerContainer == null || building.innerContainer.Count <= 0)
            {
                return null;
            }
            return building.innerContainer[0] as Pawn;
        }
    }
}
