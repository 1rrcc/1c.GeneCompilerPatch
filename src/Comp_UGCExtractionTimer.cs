using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace GeneCompilerPatch
{
    public sealed class CompProperties_UGCExtractionTimer : CompProperties
    {
        public CompProperties_UGCExtractionTimer()
        {
            compClass = typeof(Comp_UGCExtractionTimer);
        }
    }

    /// <summary>
    /// 提取计时作业：把瞬时提取改为耗时作业，计时归零后调用原模组 TryExtract 入库并自动弹出小人。
    /// 进度显示在建筑上方、检查面板与控制台页脚；断电暂停不推进计时（与原版基因提取器同语义）。
    /// 时长来自模组设置，设 0 关闭本功能；是否拦截原调用由 Patches.cs 的 Harmony 前缀决定。
    /// </summary>
    public sealed class Comp_UGCExtractionTimer : ThingComp
    {
        private GeneDef pendingGene;
        private int ticksRemaining;
        private int totalTicks;

        // 进度条 effecter，不存档。
        [Unsaved(false)]
        private Effecter progressBar;

        // 绘制失败后不再重试，避免每 tick 刷日志。
        [Unsaved(false)]
        private bool progressBarBroken;

        [Unsaved(false)]
        private CompPowerTrader powerComp;

        /// <summary>是否有一份提取作业正在计时。</summary>
        public bool Running => pendingGene != null && ticksRemaining > 0;

        /// <summary>作业还在，但因为断电暂停了（与原版基因提取器同语义：断电不推进计时）。</summary>
        public bool PausedByPowerCut
        {
            get
            {
                if (!Running || powerComp == null)
                {
                    return false;
                }
                return !powerComp.PowerOn;
            }
        }

        public GeneDef PendingGene => pendingGene;

        /// <summary>剩余 tick（已钳制为 >= 0）。</summary>
        public int TicksRemaining => ticksRemaining > 0 ? ticksRemaining : 0;

        public int TotalTicks => totalTicks;

        /// <summary>完成度（0..1）。</summary>
        public float Progress01
        {
            get
            {
                if (totalTicks <= 0)
                {
                    return 0f;
                }
                return Mathf.Clamp01(1f - (float)ticksRemaining / (float)totalTicks);
            }
        }

        public string RemainingTimeText => TicksRemaining.ToStringTicksToPeriod();

        /// <summary>启动作业并发出开始提示；拦截方因此把原调用方的 rejection 留空，避免重复弹消息。</summary>
        public void StartJob(GeneDef gene, int ticks)
        {
            if (gene == null || ticks <= 0)
            {
                return;
            }
            CancelJob();
            pendingGene = gene;
            totalTicks = ticks;
            ticksRemaining = ticks;

            Messages.Message(
                "UGCPatch_ExtractionJobStarted".Translate(gene.LabelCap.ToString(), RemainingTimeText),
                parent,
                MessageTypeDefOf.NeutralEvent,
                false);
        }

        /// <summary>中断作业（不弹出小人、不入库）。</summary>
        public void CancelJob()
        {
            pendingGene = null;
            ticksRemaining = 0;
            totalTicks = 0;
            CleanupProgressBar();
        }

        public override void CompTick()
        {
            base.CompTick();

            if (!Running)
            {
                CleanupProgressBar();
                return;
            }

            Building_Enterable machine = parent as Building_Enterable;
            // 小人不在舱内（被取走 / 建筑被拆）→ 作业作废。
            if (UGCDefs.OccupantOf(machine) == null)
            {
                CancelJob();
                Messages.Message(
                    "UGCPatch_ExtractionJobAborted".Translate(),
                    parent,
                    MessageTypeDefOf.NeutralEvent,
                    false);
                return;
            }

            // 断电暂停：不推进计时、不画进度条。
            if (PausedByPowerCut)
            {
                CleanupProgressBar();
                return;
            }

            TickProgressBar();

            ticksRemaining--;
            if (ticksRemaining <= 0)
            {
                FinishJob();
            }
        }

        /// <summary>计时归零：真正执行提取，然后自动弹出小人。</summary>
        private void FinishJob()
        {
            GeneDef gene = pendingGene;
            Building_Enterable machine = parent as Building_Enterable;
            Pawn occupant = UGCDefs.OccupantOf(machine);
            string pawnLabel = occupant != null ? occupant.LabelShortCap.ToString() : string.Empty;

            CancelJob();

            if (machine == null || gene == null)
            {
                return;
            }

            string rejection;
            bool extracted = Patch_ExtractionTimer.TryRunOriginalExtract(machine, gene, out rejection);

            if (extracted)
            {
                Messages.Message(
                    "UGCPatch_ExtractionJobDone".Translate(gene.LabelCap.ToString(), pawnLabel),
                    parent,
                    MessageTypeDefOf.PositiveEvent,
                    false);

                // 人即将被弹出，显式交给自动入舱 comp，衔接冷却结束后的自动送回。
                if (occupant != null)
                {
                    Comp_UGCAutoExtraction autoComp = parent.GetComp<Comp_UGCAutoExtraction>();
                    if (autoComp != null)
                    {
                        autoComp.RememberPawn(occupant);
                    }
                }

                Patch_ExtractionTimer.EjectPawn(machine);
            }
            else
            {
                // 入库失败（如存储已满）：保留舱内小人以便重试。
                Messages.Message(
                    string.IsNullOrEmpty(rejection)
                        ? "UGCPatch_ExtractionJobFailed".Translate().ToString()
                        : rejection,
                    parent,
                    MessageTypeDefOf.RejectInput,
                    false);
            }
        }

        // ---------- 进度条 ----------

        private void TickProgressBar()
        {
            if (progressBarBroken)
            {
                return;
            }
            try
            {
                if (progressBar == null)
                {
                    progressBar = EffecterDefOf.ProgressBarAlwaysVisible.Spawn();
                }
                if (progressBar == null)
                {
                    progressBarBroken = true;
                    return;
                }

                Rot4 rot = parent.Rotation;
                TargetInfo target = new TargetInfo(
                    parent.Position + IntVec3.North.RotatedBy(rot), parent.Map, false);
                progressBar.EffectTick(target, TargetInfo.Invalid);

                SubEffecter_ProgressBar sub = progressBar.children.Count > 0
                    ? progressBar.children[0] as SubEffecter_ProgressBar
                    : null;
                MoteProgressBar mote = sub != null ? sub.mote : null;
                if (mote != null)
                {
                    mote.progress = Progress01;
                    mote.offsetZ = (rot == Rot4.North) ? 0.5f : -0.5f;
                }
            }
            catch (Exception ex)
            {
                progressBarBroken = true;
                Log.Warning("[1c.GeneCompilerPatch] 提取作业进度条绘制失败（不影响作业本身）：" + ex.Message);
            }
        }

        private void CleanupProgressBar()
        {
            if (progressBar == null)
            {
                return;
            }
            try
            {
                progressBar.Cleanup();
            }
            catch (Exception)
            {
            }
            progressBar = null;
        }

        // ---------- 面板 / 存档 / 生命周期 ----------

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            powerComp = parent.GetComp<CompPowerTrader>();
        }

        public override string CompInspectStringExtra()
        {
            if (!Running)
            {
                return null;
            }
            if (PausedByPowerCut)
            {
                return "UGCPatch_ExtractionJobPaused"
                    .Translate(pendingGene.LabelCap.ToString(), RemainingTimeText)
                    .ToString();
            }
            return "UGCPatch_ExtractionJobRunning"
                .Translate(pendingGene.LabelCap.ToString(), RemainingTimeText)
                .ToString();
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Defs.Look(ref pendingGene, "ugcExtractionPendingGene");
            Scribe_Values.Look(ref ticksRemaining, "ugcExtractionTicksRemaining", 0);
            Scribe_Values.Look(ref totalTicks, "ugcExtractionTotalTicks", 0);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (pendingGene == null || ticksRemaining <= 0)
                {
                    pendingGene = null;
                    ticksRemaining = 0;
                    totalTicks = 0;
                }
                else if (totalTicks < ticksRemaining)
                {
                    totalTicks = ticksRemaining;
                }
            }
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            CancelJob();
        }
    }
}
