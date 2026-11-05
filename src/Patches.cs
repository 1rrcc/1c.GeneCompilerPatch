using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace GeneCompilerPatch
{
    /// <summary>
    /// 统一挂载 Harmony 补丁。
    /// 对 UniversalGeneCompiler 没有编译期依赖：目标类型 / 方法全部按名字反射解析，
    /// 单项解析失败只跳过该修复并记一条日志，不影响其余补丁加载。
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class PatchBootstrap
    {
        internal const string LibraryNamespace = "UniversalGeneCompilerLib";

        internal const string BuildingTypeName = LibraryNamespace + ".Buildings.Building_UniversalGeneCompiler";
        internal const string LayoutTypeName = LibraryNamespace + ".Rendering.DirectionalVisualLayout";
        internal const string OperationServiceTypeName = LibraryNamespace + ".Operations.GeneOperationService";
        internal const string DialogTypeName = LibraryNamespace + ".UI.Dialog_UniversalGeneCompiler";

        static PatchBootstrap()
        {
            Harmony harmony = new Harmony("1c.GeneCompilerPatch");

            TryPatch(harmony, BuildingTypeName, "get_PawnDrawOffset",
                typeof(Patch_PawnFacing), "PawnDrawOffsetPostfix", "舱内位置");
            TryPatch(harmony, BuildingTypeName, "get_HeldPawnBodyAngle",
                typeof(Patch_PawnFacing), "HeldPawnBodyAnglePostfix", "舱内朝向");
            TryPatch(harmony, BuildingTypeName, "GetArmMotionOffset",
                typeof(Patch_PawnFacing), "ArmMotionOffsetPostfix", "机械臂行程");

            TryPatch(harmony, OperationServiceTypeName, "TryExtract",
                typeof(Patch_ExtractionCooldown), "TryExtractPostfix", "提取冷却时长");

            TryPatch(harmony, OperationServiceTypeName, "GetInjectionPrice",
                typeof(Patch_ArchiteGenePrice), "GetInjectionPricePostfix", "超凡基因注入单价");

            TryPatch(harmony, OperationServiceTypeName, "TryExtract",
                typeof(Patch_ExtractionTimer), "TryExtractPrefix", "提取作业计时", prefix: true);
            TryPatch(harmony, BuildingTypeName, "EjectPawn",
                typeof(Patch_ExtractionTimer), "EjectPawnPrefix", "强制弹出确认", prefix: true);
            TryPatch(harmony, Patch_ExtractionTimer.DialogTypeName, "DrawFooter",
                typeof(Patch_ExtractionTimer), "DrawFooterPostfix", "控制台作业进度显示");

            Log.Message("[1c.GeneCompilerPatch] 已加载（南向贴图：" +
                (Patch_PawnFacing.HasSouthTexture ? "已提供，南向按 180° 精确变换" : "缺失，南向回退为与北向一致") + "）。");
        }

        private static void TryPatch(Harmony harmony, string typeName, string methodName,
            Type patchHost, string patchName, string label, bool prefix = false)
        {
            try
            {
                Type type = AccessTools.TypeByName(typeName);
                if (type == null)
                {
                    Log.Warning("[1c.GeneCompilerPatch] 找不到类型 " + typeName + "，" + label + "修复已跳过。");
                    return;
                }
                MethodBase target = AccessTools.Method(type, methodName);
                if (target == null)
                {
                    Log.Warning("[1c.GeneCompilerPatch] 找不到方法 " + typeName + "." + methodName + "，" + label + "修复已跳过。");
                    return;
                }
                MethodInfo patchMethod = AccessTools.Method(patchHost, patchName);
                if (patchMethod == null)
                {
                    Log.Warning("[1c.GeneCompilerPatch] 找不到 " + (prefix ? "prefix" : "postfix") + " 方法 " + patchName + "，" + label + "修复已跳过。");
                    return;
                }
                if (prefix)
                {
                    harmony.Patch(target, prefix: new HarmonyMethod(patchMethod));
                }
                else
                {
                    harmony.Patch(target, postfix: new HarmonyMethod(patchMethod));
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[1c.GeneCompilerPatch] " + label + " 修复挂载失败：" + ex);
            }
        }
    }

    /// <summary>
    /// 修复建筑转到南向后舱内人员的偏移与朝向。
    /// 原模组南向的 DirectionalVisualLayout 数值有误（偏移不是北向的 180° 反号、
    /// 身体角度照抄北向），且缺失 _south 贴图时 Graphic_Multi 会原样复用 _north。
    ///
    /// 本补丁补齐南向贴图（= 北向旋转 180°），并只在「南向 + 贴图存在」时对北向基准值
    /// 做严格 180° 变换（偏移取反、角度 +180°、机械臂行程取反），北 / 东 / 西一律不动。
    /// 北向基准读取原模组 DirectionalVisualLayout.Get(Rot4.North)，原模组调整数值时无需同步本补丁。
    /// </summary>
    internal static class Patch_PawnFacing
    {
        private const string SouthTexturePath =
            "Things/Building/UniversalGeneCompiler/UniversalGeneCompiler_south";

        private static bool southTextureChecked;
        private static bool southTextureExists;

        private static bool northLayoutResolved;
        private static object northLayout;
        private static FieldInfo northOffsetXField;
        private static FieldInfo northOffsetZField;
        private static FieldInfo northBodyAngleField;

        /// <summary>南向贴图是否存在（决定是否对南向做 180° 变换）。</summary>
        internal static bool HasSouthTexture
        {
            get
            {
                if (!southTextureChecked)
                {
                    southTextureChecked = true;
                    try
                    {
                        southTextureExists =
                            ContentFinder<Texture2D>.Get(SouthTexturePath, false) != null;
                    }
                    catch (Exception)
                    {
                        southTextureExists = false;
                    }
                }
                return southTextureExists;
            }
        }

        /// <summary>懒加载原模组的北向布局对象及需要读取的三个字段。</summary>
        private static bool TryResolveNorthLayout()
        {
            if (northLayoutResolved)
            {
                return northLayout != null;
            }
            northLayoutResolved = true;
            try
            {
                Type layoutType = AccessTools.TypeByName(PatchBootstrap.LayoutTypeName);
                if (layoutType == null)
                {
                    return false;
                }
                MethodInfo get = AccessTools.Method(layoutType, "Get", new[] { typeof(Rot4) });
                FieldInfo offsetX = AccessTools.Field(layoutType, "PawnOffsetX");
                FieldInfo offsetZ = AccessTools.Field(layoutType, "PawnOffsetZ");
                FieldInfo bodyAngle = AccessTools.Field(layoutType, "PawnBodyAngle");
                if (get == null || offsetX == null || offsetZ == null || bodyAngle == null)
                {
                    return false;
                }
                object layout = get.Invoke(null, new object[] { Rot4.North });
                if (layout == null)
                {
                    return false;
                }
                northOffsetXField = offsetX;
                northOffsetZField = offsetZ;
                northBodyAngleField = bodyAngle;
                northLayout = layout;
            }
            catch (Exception ex)
            {
                Log.Warning("[1c.GeneCompilerPatch] 读取原模组北向布局失败，南向回退为原行为：" + ex.Message);
                northLayout = null;
            }
            return northLayout != null;
        }

        private static bool IsSouth(object instance)
        {
            return instance is Thing thing && thing.Rotation == Rot4.South;
        }

        private static bool ShouldTransform(object instance)
        {
            return IsSouth(instance) && HasSouthTexture && TryResolveNorthLayout();
        }

        public static void PawnDrawOffsetPostfix(object __instance, ref Vector3 __result)
        {
            if (!ShouldTransform(__instance))
            {
                return;
            }
            float x = (float)northOffsetXField.GetValue(northLayout);
            float z = (float)northOffsetZField.GetValue(northLayout);
            __result = new Vector3(0f - x, __result.y, 0f - z);
        }

        public static void HeldPawnBodyAnglePostfix(object __instance, ref float __result)
        {
            if (!ShouldTransform(__instance))
            {
                return;
            }
            float angle = (float)northBodyAngleField.GetValue(northLayout);
            __result = Mathf.Repeat(angle + 180f, 360f);
        }

        public static void ArmMotionOffsetPostfix(object __instance, ref Vector2 __result)
        {
            if (!ShouldTransform(__instance))
            {
                return;
            }
            // 机械臂贴图同样旋转了 180°，行程同步取反才能继续对准舱位。
            __result = new Vector2(0f - __result.x, 0f - __result.y);
        }
    }

    /// <summary>
    /// 将定向提取冷却时长改为设置值。
    /// TryExtract 是唯一挂 UGC_GeneExtractionCooldown 的位置，在此修改可保证设置即时生效。
    /// </summary>
    internal static class Patch_ExtractionCooldown
    {
        public static void TryExtractPostfix(Building_Enterable __0, ref bool __result)
        {
            if (!__result)
            {
                return;
            }

            GeneCompilerPatchSettings settings = GeneCompilerPatchMod.Settings;
            if (settings == null || __0 == null)
            {
                return;
            }

            Pawn pawn = UGCDefs.OccupantOf(__0);
            HediffDef def = UGCDefs.GeneExtractionCooldown;
            if (pawn == null || pawn.health == null || def == null)
            {
                return;
            }

            Hediff hediff = pawn.health.hediffSet.GetFirstHediffOfDef(def, false);
            if (hediff == null)
            {
                return;
            }

            int ticks = settings.CooldownTicks;
            if (ticks <= 0)
            {
                // 0 = 不施加冷却。
                pawn.health.RemoveHediff(hediff);
                return;
            }

            HediffComp_Disappears disappears = hediff.TryGetComp<HediffComp_Disappears>();
            if (disappears != null)
            {
                disappears.SetDuration(ticks);
            }
        }
    }

    /// <summary>
    /// 把超凡基因注入的单价改为设置值。
    ///
    /// 原模组 GeneOperationService.GetInjectionPrice =
    ///   待注入基因的复杂度合计 × 300 + 待注入的超凡基因数 × 10000。
    /// 这里只替换「超凡基因数 × 10000」这一项：postfix 按原方法同一定义数出超凡基因个数，
    /// 把差额补进返回值，复杂度计费与「已拥有该基因则不收费」等规则全部保持原样。
    /// 付款（TryInject）、注入页报价、模板页报价都调用本方法，所以改这一处即可全局一致。
    /// </summary>
    internal static class Patch_ArchiteGenePrice
    {
        /// <summary>原模组 GeneOperationService.SilverPerArchiteGene。</summary>
        internal const int VanillaArchiteGeneSilver = 10000;

        private static bool resolved;
        private static MethodInfo genesRequiringInjectionMethod;

        public static void GetInjectionPricePostfix(ref int __result, object __0, IEnumerable<GeneDef> __1)
        {
            GeneCompilerPatchSettings settings = GeneCompilerPatchMod.Settings;
            if (settings == null)
            {
                return;
            }

            int unitPrice = settings.ArchiteGeneSilver;
            if (unitPrice == VanillaArchiteGeneSilver)
            {
                return; // 默认值：不介入，零开销。
            }

            int count = CountArchiteGenes(__0 as Pawn, __1);
            if (count <= 0)
            {
                return;
            }

            // 差额可正可负；调低后不允许低于 0，复杂度部分仍按原方法保留。
            __result = Mathf.Max(0, __result + count * (unitPrice - VanillaArchiteGeneSilver));
        }

        /// <summary>
        /// 数出「本次注入真正需要新增的超凡基因」个数。
        /// 优先调用原模组的私有方法 GetGenesRequiringInjection，保证口径与其完全一致；
        /// 反射不可用时退回到同样的筛选规则（排除小人已经拥有的基因）。
        /// </summary>
        private static int CountArchiteGenes(Pawn pawn, IEnumerable<GeneDef> genes)
        {
            if (genes == null)
            {
                return 0;
            }

            EnsureResolved();
            if (genesRequiringInjectionMethod != null)
            {
                try
                {
                    List<GeneDef> list =
                        genesRequiringInjectionMethod.Invoke(null, new object[] { pawn, genes }) as List<GeneDef>;
                    if (list != null)
                    {
                        return list.Count(g => g != null && g.biostatArc > 0);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning("[1c.GeneCompilerPatch] 读取原模组待注入基因列表失败，改按本地规则统计：" + ex.Message);
                    genesRequiringInjectionMethod = null;
                }
            }

            IEnumerable<GeneDef> remaining = genes.Where(g => g != null).Distinct();
            if (pawn != null && pawn.genes != null)
            {
                remaining = remaining.Where(g => !pawn.genes.HasEndogene(g) && !pawn.genes.HasXenogene(g));
            }
            return remaining.Count(g => g.biostatArc > 0);
        }

        private static void EnsureResolved()
        {
            if (resolved)
            {
                return;
            }
            resolved = true;

            try
            {
                Type opType = AccessTools.TypeByName(PatchBootstrap.OperationServiceTypeName);
                if (opType != null)
                {
                    genesRequiringInjectionMethod = AccessTools.Method(opType, "GetGenesRequiringInjection");
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[1c.GeneCompilerPatch] 超凡基因单价反射初始化失败：" + ex.Message);
            }
        }
    }

    /// <summary>
    /// 把瞬时提取改为计时作业。
    /// TryExtract 前缀：设置时长 > 0 且请求合法时启动 Comp_UGCExtractionTimer 并拦截调用，
    /// 到点由计时器再次调用本方法完成入库；时长为 0 或请求不合法时放行，
    /// 由原方法给出精确的拒绝原因。EjectPawn 前缀：作业进行中先弹确认窗，确认后才中断并弹出。
    /// </summary>
    internal static class Patch_ExtractionTimer
    {
        internal const string DialogTypeName = PatchBootstrap.DialogTypeName;

        private static bool resolved;

        private static MethodInfo tryExtractMethod;
        private static MethodInfo ejectPawnMethod;
        private static FieldInfo dialogMachineField;
        private static PropertyInfo powerOnProperty;
        private static PropertyInfo storedGenesProperty;
        private static PropertyInfo maxStoredGenesProperty;

        /// <summary>重入保护：计时器经反射发起的真实提取同样会走 prefix，用它放行。</summary>
        private static bool insideOriginalCall;

        private static void EnsureResolved()
        {
            if (resolved)
            {
                return;
            }
            resolved = true;

            try
            {
                Type opType = AccessTools.TypeByName(PatchBootstrap.OperationServiceTypeName);
                if (opType != null)
                {
                    tryExtractMethod = AccessTools.Method(opType, "TryExtract");
                }
                Type buildingType = AccessTools.TypeByName(PatchBootstrap.BuildingTypeName);
                if (buildingType != null)
                {
                    ejectPawnMethod = AccessTools.Method(buildingType, "EjectPawn");
                    powerOnProperty = AccessTools.Property(buildingType, "PowerOn");
                    storedGenesProperty = AccessTools.Property(buildingType, "StoredGenes");
                    maxStoredGenesProperty = AccessTools.Property(buildingType, "MaxStoredGeneCount");
                }
                Type dialogType = AccessTools.TypeByName(DialogTypeName);
                if (dialogType != null)
                {
                    dialogMachineField = AccessTools.Field(dialogType, "machine");
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[1c.GeneCompilerPatch] 提取作业计时器反射初始化失败：" + ex.Message);
            }
        }

        // ---------- 调用原模组的两件事 ----------

        /// <summary>调用原模组的 GeneOperationService.TryExtract（真正入库 + 挂冷却）。</summary>
        internal static bool TryRunOriginalExtract(Building_Enterable machine, GeneDef gene, out string rejection)
        {
            rejection = null;
            EnsureResolved();
            if (machine == null || gene == null || tryExtractMethod == null)
            {
                return false;
            }

            object[] args = { machine, gene, null };
            insideOriginalCall = true;
            try
            {
                return (bool)tryExtractMethod.Invoke(null, args);
            }
            catch (Exception ex)
            {
                Log.Warning("[1c.GeneCompilerPatch] 提取作业收尾时调用原模组 TryExtract 失败：" + ex);
                return false;
            }
            finally
            {
                rejection = args[2] as string;
                insideOriginalCall = false;
            }
        }

        /// <summary>调用原模组的 Building_UniversalGeneCompiler.EjectPawn。</summary>
        internal static void EjectPawn(Building_Enterable machine)
        {
            if (machine == null)
            {
                return;
            }
            EnsureResolved();
            if (ejectPawnMethod == null)
            {
                return;
            }
            try
            {
                ejectPawnMethod.Invoke(machine, null);
            }
            catch (Exception ex)
            {
                Log.Warning("[1c.GeneCompilerPatch] 自动弹出小人失败：" + ex);
            }
        }

        // ---------- Harmony 前缀 ----------

        /// <summary>
        /// GeneOperationService.TryExtract(Building_UniversalGeneCompiler machine, GeneDef geneDef, out string rejection)
        /// 参数用 __0/__1/__2 按位绑定，避免对原模组类型产生编译期依赖。
        /// </summary>
        public static bool TryExtractPrefix(object __0, GeneDef __1, ref string __2, ref bool __result)
        {
            // 计时器发起的真实提取：放行。
            if (insideOriginalCall)
            {
                return true;
            }

            GeneCompilerPatchSettings settings = GeneCompilerPatchMod.Settings;
            int ticks = settings != null ? settings.ExtractionTicks : 0;
            if (ticks <= 0)
            {
                return true; // 功能关闭 → 原版瞬时提取
            }

            Building_Enterable machine = __0 as Building_Enterable;
            GeneDef gene = __1;
            if (machine == null || gene == null)
            {
                return true; // 让原方法给出它自己的报错
            }

            Comp_UGCExtractionTimer comp = machine.GetComp<Comp_UGCExtractionTimer>();
            if (comp == null)
            {
                return true; // 未挂计时器 comp → 保持原行为
            }

            if (comp.Running)
            {
                __result = false;
                __2 = "UGCPatch_ExtractionBusy"
                    .Translate(gene.LabelCap.ToString(), comp.RemainingTimeText)
                    .ToString();
                return false;
            }

            if (!LooksExtractable(machine, gene))
            {
                // 请求不合法 → 放行，由原方法给出拒绝原因。
                return true;
            }

            // 拦截本次调用；提示已由 StartJob 发出，rejection 留空以免重复弹消息。
            comp.StartJob(gene, ticks);
            __result = false;
            __2 = null;
            return false;
        }

        /// <summary>作业进行中时改为先弹确认窗，确认后才中断作业并弹出；返回 false 拦下调用。</summary>
        public static bool EjectPawnPrefix(object __instance)
        {
            Building_Enterable machine = __instance as Building_Enterable;
            if (machine == null)
            {
                return true;
            }

            Comp_UGCExtractionTimer comp = machine.GetComp<Comp_UGCExtractionTimer>();
            if (comp == null || !comp.Running)
            {
                return true;
            }

            GeneDef gene = comp.PendingGene;
            Comp_UGCExtractionTimer capturedComp = comp;
            Building_Enterable capturedMachine = machine;

            string text = "UGCPatch_ForceEjectConfirm".Translate(
                gene != null ? gene.LabelCap.ToString() : string.Empty,
                comp.RemainingTimeText).ToString();

            Dialog_MessageBox dialog = Dialog_MessageBox.CreateConfirmation(
                text,
                delegate
                {
                    capturedComp.CancelJob();
                    EjectPawn(capturedMachine);
                },
                destructive: true,
                title: "UGCPatch_ForceEjectTitle".Translate().ToString(),
                layer: WindowLayer.Dialog);

            Find.WindowStack.Add(dialog);
            return false;
        }

        /// <summary>在控制台页脚中间绘制作业进度；纯显示，异常静默忽略。</summary>
        public static void DrawFooterPostfix(object __instance, Rect rect)
        {
            try
            {
                EnsureResolved();
                if (dialogMachineField == null || __instance == null)
                {
                    return;
                }

                Building_Enterable machine = dialogMachineField.GetValue(__instance) as Building_Enterable;
                if (machine == null)
                {
                    return;
                }
                Comp_UGCExtractionTimer comp = machine.GetComp<Comp_UGCExtractionTimer>();
                if (comp == null || !comp.Running)
                {
                    return;
                }

                float side = Mathf.Min(180f, (rect.width - 8f) / 2f);
                Rect mid = new Rect(rect.x + side + 6f, rect.y, rect.width - (side + 6f) * 2f, rect.height);
                if (mid.width < 70f)
                {
                    return;
                }

                GameFont prevFont = Text.Font;
                TextAnchor prevAnchor = Text.Anchor;
                Text.Font = GameFont.Tiny;
                Text.Anchor = TextAnchor.MiddleCenter;
                try
                {
                    if (comp.PausedByPowerCut)
                    {
                        Widgets.Label(mid,
                            "UGCPatch_ExtractionJobPausedShort"
                                .Translate(comp.RemainingTimeText).ToString());
                    }
                    else
                    {
                        Widgets.FillableBar(new Rect(mid.x, mid.y + 2f, mid.width, 12f), comp.Progress01);
                        Widgets.Label(
                            new Rect(mid.x, mid.y + 14f, mid.width, Mathf.Max(0f, mid.height - 14f)),
                            "UGCPatch_ExtractionJobShort".Translate(comp.RemainingTimeText).ToString());
                    }
                }
                finally
                {
                    Text.Font = prevFont;
                    Text.Anchor = prevAnchor;
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>复刻原版前置校验以决定拦截或放行；不确定时一律返回 false 交给原版。</summary>
        private static bool LooksExtractable(Building_Enterable machine, GeneDef gene)
        {
            try
            {
                Pawn pawn = UGCDefs.OccupantOf(machine);
                if (pawn == null || pawn.genes == null || pawn.health == null || pawn.health.hediffSet == null)
                {
                    return false;
                }

                EnsureResolved();

                // PowerOn 在原模组类型上，只能反射取；取不到则退回原版行为。
                if (powerOnProperty == null)
                {
                    return false;
                }
                if (!(powerOnProperty.GetValue(machine, null) is bool powerOn) || !powerOn)
                {
                    return false;
                }

                if (gene.biostatArc > 0)
                {
                    return false;
                }
                HediffDef cooldown = UGCDefs.GeneExtractionCooldown;
                if (cooldown != null && pawn.health.hediffSet.HasHediff(cooldown, false))
                {
                    return false;
                }
                if (!pawn.genes.GenesListForReading.Any(g => g != null && g.def == gene))
                {
                    return false;
                }

                if (storedGenesProperty != null)
                {
                    IList stored = storedGenesProperty.GetValue(machine, null) as IList;
                    if (stored != null)
                    {
                        if (stored.Contains(gene))
                        {
                            return false;
                        }
                        if (maxStoredGenesProperty != null)
                        {
                            int max = (int)maxStoredGenesProperty.GetValue(machine, null);
                            if (stored.Count >= max)
                            {
                                return false;
                            }
                        }
                    }
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
