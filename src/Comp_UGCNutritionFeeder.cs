using RimWorld;
using Verse;

namespace GeneCompilerPatch
{
    /// <summary>
    /// 营养供给：营养池由原版 CompRefuelable 承载（XML patch 挂载），搬运补料、燃料条
    /// 与补料阈值均沿用原版行为；本 comp 只负责按饱食度缺口从营养池扣营养、喂饱舱内人员。
    /// </summary>
    public sealed class CompProperties_UGCNutritionFeeder : CompProperties
    {
        /// <summary>弥补 1 点饱食度需要消耗多少营养（默认 1：1）。</summary>
        public float nutritionPerFoodLevel = 1f;

        /// <summary>检查间隔（tick）。</summary>
        public int checkIntervalTicks = 250;

        public CompProperties_UGCNutritionFeeder()
        {
            compClass = typeof(Comp_UGCNutritionFeeder);
        }
    }

    public sealed class Comp_UGCNutritionFeeder : ThingComp
    {
        private CompRefuelable fuelComp;
        private CompPowerTrader powerComp;
        private Pawn lastFedPawn;
        private int lastFedTick = -99999;

        public CompProperties_UGCNutritionFeeder Props => (CompProperties_UGCNutritionFeeder)props;

        private CompRefuelable FuelComp
        {
            get
            {
                if (fuelComp == null)
                {
                    fuelComp = parent.GetComp<CompRefuelable>();
                }
                return fuelComp;
            }
        }

        private CompPowerTrader PowerComp
        {
            get
            {
                if (powerComp == null)
                {
                    powerComp = parent.GetComp<CompPowerTrader>();
                }
                return powerComp;
            }
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            fuelComp = parent.GetComp<CompRefuelable>();
            powerComp = parent.GetComp<CompPowerTrader>();
        }

        public override void CompTick()
        {
            base.CompTick();
            if (!parent.IsHashIntervalTick(Props.checkIntervalTicks)) return;

            Building_Enterable building = parent as Building_Enterable;
            Pawn occupant = UGCDefs.OccupantOf(building);
            if (occupant == null) return;

            Need_Food food = occupant.needs?.food;
            if (food == null) return;

            CompRefuelable fuel = FuelComp;
            if (fuel == null) return;

            CompPowerTrader power = PowerComp;
            if (power != null && !power.PowerOn) return;

            float missing = food.MaxLevel - food.CurLevel;
            if (missing <= 0.0001f) return;

            float cost = missing * Props.nutritionPerFoodLevel;
            if (cost <= 0f) return;

            if (fuel.Fuel < cost) return;

            fuel.ConsumeFuel(cost);
            food.CurLevel = food.MaxLevel;
            lastFedPawn = occupant;
            lastFedTick = Find.TickManager.TicksGame;
        }

        /// <summary>
        /// 营养池本身的「营养: x / y」由原版 CompRefuelable 面板负责打印，
        /// 这里只补充它给不出的状态信息。
        /// </summary>
        public override string CompInspectStringExtra()
        {
            Building_Enterable building = parent as Building_Enterable;
            Pawn occupant = UGCDefs.OccupantOf(building);
            if (occupant == null) return null;

            CompRefuelable fuel = FuelComp;
            if (fuel == null) return null;

            if (fuel.Fuel <= 0f)
            {
                return "UGCPatch_NoNutrition".Translate().ToString();
            }
            if (lastFedPawn != null && Find.TickManager.TicksGame - lastFedTick < 900)
            {
                return "UGCPatch_NutritionFeeding".Translate(lastFedPawn.Named("PAWN")).ToString();
            }
            return null;
        }
    }
}
