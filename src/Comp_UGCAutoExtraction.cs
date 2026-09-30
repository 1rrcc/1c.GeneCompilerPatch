using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace GeneCompilerPatch
{
    public sealed class CompProperties_UGCAutoExtraction : CompProperties
    {
        /// <summary>新建建筑时的默认开关状态。</summary>
        public bool defaultEnabled = true;

        /// <summary>轮询间隔（tick），同时作为重试退避的基准。</summary>
        public int checkIntervalTicks = 250;

        public CompProperties_UGCAutoExtraction()
        {
            compClass = typeof(Comp_UGCAutoExtraction);
        }
    }

    /// <summary>
    /// 提取冷却结束后自动送人回舱：记住带 UGC_GeneExtractionCooldown 的舱内人员，
    /// 冷却结束时殖民者自己入舱（EnterBuilding），囚犯 / 倒地人员由搬运工搬入
    /// （UGC_CarryToUniversalGeneCompiler）。提取本身仍由玩家手动执行；建筑上有开关可关闭。
    /// </summary>
    public sealed class Comp_UGCAutoExtraction : ThingComp
    {
        private bool enabled = true;
        private Pawn lastPawn;
        private Pawn autoRequestedPawn;
        private int nextAttemptTick;
        private int attemptCount;

        public CompProperties_UGCAutoExtraction Props => (CompProperties_UGCAutoExtraction)props;

        private static bool iconResolved;
        private static Texture2D cachedIcon;

        /// <summary>优先使用本补丁自带图标，缺失时回退原版 InsertPawn。</summary>
        private static Texture2D Icon
        {
            get
            {
                if (!iconResolved)
                {
                    iconResolved = true;
                    cachedIcon = ContentFinder<Texture2D>.Get("UI/Gizmos/UGC_InsertPawn", false)
                        ?? ContentFinder<Texture2D>.Get("UI/Gizmos/InsertPawn", false);
                }
                return cachedIcon;
            }
        }

        public override void Initialize(CompProperties props)
        {
            base.Initialize(props);
            enabled = Props.defaultEnabled;
        }

        /// <summary>
        /// 记住待送回舱的小人。常规路径是 CompTick 发现「舱内有人且带冷却」时自行记录；
        /// 提取计时作业结束后会立即弹出小人，绕过该路径，故由计时器显式调用。
        /// </summary>
        public void RememberPawn(Pawn pawn)
        {
            if (pawn == null)
            {
                return;
            }
            lastPawn = pawn;
            attemptCount = 0;
            nextAttemptTick = 0;
        }

        // 1.6：ThingComp 不再暴露 virtual ExposeData()，存档逻辑写在 PostExposeData()。
        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref enabled, "ugcAutoExtractionEnabled", Props.defaultEnabled);
            Scribe_References.Look(ref lastPawn, "ugcAutoExtractionLastPawn");
            Scribe_Values.Look(ref nextAttemptTick, "ugcAutoExtractionNextTick", 0);
            Scribe_Values.Look(ref attemptCount, "ugcAutoExtractionAttempts", 0);
        }

        public override void CompTick()
        {
            base.CompTick();
            Building_Enterable building = parent as Building_Enterable;
            if (building == null) return;

            Pawn occupant = UGCDefs.OccupantOf(building);

            if (occupant != null)
            {
                // 带提取冷却 = 刚被提取过，记下等待回舱。
                if (UGCDefs.IsOnCooldown(occupant))
                {
                    lastPawn = occupant;
                    attemptCount = 0;
                }
                if (autoRequestedPawn != null && autoRequestedPawn == occupant)
                {
                    autoRequestedPawn = null;
                    Messages.Message(
                        "UGCPatch_AutoEntered".Translate(occupant.Named("PAWN")),
                        building,
                        MessageTypeDefOf.PositiveEvent,
                        false);
                }
                return;
            }

            if (!enabled) return;

            int interval = Mathf.Max(1, Props.checkIntervalTicks);
            int now = Find.TickManager.TicksGame;
            if (now < nextAttemptTick) return;

            if (building.SelectedPawn != null) return;

            Pawn candidate = lastPawn;
            if (candidate == null || candidate.Destroyed || candidate.Dead
                || !candidate.Spawned || candidate.Map != parent.Map)
            {
                lastPawn = null;
                return;
            }

            // 冷却未结束：静默等待，不计入失败。
            if (UGCDefs.IsOnCooldown(candidate)) return;

            if (!building.CanAcceptPawn(candidate).Accepted) return;

            building.SelectedPawn = candidate;
            if (!candidate.IsPrisonerOfColony && !candidate.Downed)
            {
                candidate.jobs?.TryTakeOrderedJob(
                    JobMaker.MakeJob(JobDefOf.EnterBuilding, building), JobTag.Misc);
            }
            autoRequestedPawn = candidate;

            // 退避重试：避免玩家手动取消后立刻又被重新安排。
            attemptCount++;
            nextAttemptTick = now + Mathf.Clamp(interval * (1 + attemptCount), interval, 6000);
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            Command_Toggle toggle = new Command_Toggle();
            toggle.defaultLabel = "UGCPatch_AutoExtraction".Translate().ToString()
                + ": " + (enabled ? "On".Translate() : "Off".Translate());
            toggle.defaultDesc = "UGCPatch_AutoExtractionDesc".Translate().ToString();
            toggle.icon = Icon;
            toggle.isActive = () => enabled;
            toggle.toggleAction = delegate
            {
                enabled = !enabled;
                if (!enabled)
                {
                    autoRequestedPawn = null;
                    attemptCount = 0;
                }
                else
                {
                    nextAttemptTick = 0;
                }
            };
            toggle.activateSound = SoundDefOf.Tick_Tiny;
            toggle.Order = -90f;
            yield return toggle;
        }

        public override string CompInspectStringExtra()
        {
            if (!enabled || lastPawn == null) return null;
            if (!lastPawn.Spawned || lastPawn.Dead || lastPawn.Destroyed) return null;
            if (!UGCDefs.IsOnCooldown(lastPawn)) return null;
            return "UGCPatch_AutoWaiting".Translate(lastPawn.Named("PAWN")).ToString();
        }
    }
}
