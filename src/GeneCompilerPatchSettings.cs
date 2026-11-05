using RimWorld;
using UnityEngine;
using Verse;

namespace GeneCompilerPatch
{
    /// <summary>补丁设置：定向提取冷却时长、提取作业时长与超凡基因注入单价。</summary>
    public sealed class GeneCompilerPatchSettings : ModSettings
    {
        public const float DefaultCooldownDays = 1f;
        public const float MinCooldownDays = 0f;
        public const float MaxCooldownDays = 7f;

        public const float DefaultExtractionHours = 6f;
        public const float MinExtractionHours = 0f;
        public const float MaxExtractionHours = 24f;

        public const int DefaultArchiteGeneSilver = 10000;
        public const int MinArchiteGeneSilver = 0;
        public const int MaxArchiteGeneSilver = 100000;

        /// <summary>滑块步进：白银单价按 100 取整。</summary>
        private const float ArchiteGeneSilverStep = 100f;

        /// <summary>定向提取冷却，单位「天」。0 = 不施加冷却。</summary>
        public float cooldownDays = DefaultCooldownDays;

        /// <summary>提取作业计时，单位「游戏内小时」。0 = 关闭计时（恢复原版瞬时提取）。</summary>
        public float extractionHours = DefaultExtractionHours;

        /// <summary>注入每个超凡基因额外收取的白银。原模组为 10000；0 = 不额外收费。</summary>
        public int architeGeneSilver = DefaultArchiteGeneSilver;

        public static float ClampDays(float value)
        {
            return Mathf.Clamp(value, MinCooldownDays, MaxCooldownDays);
        }

        public static float ClampExtractionHours(float value)
        {
            return Mathf.Clamp(value, MinExtractionHours, MaxExtractionHours);
        }

        public static int ClampArchiteGeneSilver(float value)
        {
            return Mathf.Clamp(Mathf.RoundToInt(value), MinArchiteGeneSilver, MaxArchiteGeneSilver);
        }

        /// <summary>取整到滑块步进，避免拖动时出现 1037 这类零碎数值。</summary>
        public static int SnapArchiteGeneSilver(float value)
        {
            return ClampArchiteGeneSilver(Mathf.Round(value / ArchiteGeneSilverStep) * ArchiteGeneSilverStep);
        }

        /// <summary>冷却对应的 tick 数；0 表示不施加冷却（直接移除该 hediff）。</summary>
        public int CooldownTicks
        {
            get
            {
                float days = ClampDays(cooldownDays);
                if (days <= 0f)
                {
                    return 0;
                }
                return Mathf.Max(1, Mathf.RoundToInt(days * GenDate.TicksPerDay));
            }
        }

        /// <summary>提取作业对应的 tick 数；0 表示关闭计时器，走原版瞬时提取。</summary>
        public int ExtractionTicks
        {
            get
            {
                float hours = ClampExtractionHours(extractionHours);
                if (hours <= 0f)
                {
                    return 0;
                }
                return Mathf.Max(1, Mathf.RoundToInt(hours * GenDate.TicksPerHour));
            }
        }

        /// <summary>超凡基因注入单价（已钳制），供 Harmony 补丁读取。</summary>
        public int ArchiteGeneSilver
        {
            get { return ClampArchiteGeneSilver(architeGeneSilver); }
        }

        public void ResetToDefaults()
        {
            cooldownDays = DefaultCooldownDays;
            extractionHours = DefaultExtractionHours;
            architeGeneSilver = DefaultArchiteGeneSilver;
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref cooldownDays, "cooldownDays", DefaultCooldownDays);
            Scribe_Values.Look(ref extractionHours, "extractionHours", DefaultExtractionHours);
            Scribe_Values.Look(ref architeGeneSilver, "architeGeneSilver", DefaultArchiteGeneSilver);
            cooldownDays = ClampDays(cooldownDays);
            extractionHours = ClampExtractionHours(extractionHours);
            architeGeneSilver = ClampArchiteGeneSilver(architeGeneSilver);
        }

        public void DoWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);

            // ===== 定向提取冷却 =====
            GameFont prevFont = Text.Font;
            Text.Font = GameFont.Medium;
            listing.Label("UGCPatch_SettingsHeader".Translate());
            Text.Font = prevFont;
            listing.GapLine(6f);

            listing.Label("UGCPatch_CooldownLabel".Translate().ToString()
                + ": " + "UGCPatch_DaysValue".Translate(ClampDays(cooldownDays).ToString("0.##")));
            listing.Gap(2f);

            float slider = listing.Slider(ClampDays(cooldownDays), MinCooldownDays, MaxCooldownDays);
            cooldownDays = ClampDays(slider);
            listing.Gap(2f);
            listing.Label("UGCPatch_CooldownHint".Translate());
            listing.Gap(12f);

            // ===== 提取作业计时 =====
            Text.Font = GameFont.Medium;
            listing.Label("UGCPatch_ExtractionHeader".Translate());
            Text.Font = prevFont;
            listing.GapLine(6f);

            listing.Label("UGCPatch_ExtractionHoursLabel".Translate().ToString()
                + ": " + "UGCPatch_HoursValue".Translate(ClampExtractionHours(extractionHours).ToString("0.##")));
            listing.Gap(2f);

            float hoursSlider = listing.Slider(
                ClampExtractionHours(extractionHours), MinExtractionHours, MaxExtractionHours);
            extractionHours = ClampExtractionHours(hoursSlider);
            listing.Gap(2f);
            listing.Label("UGCPatch_ExtractionHoursHint".Translate());
            listing.Gap(12f);

            // ===== 超凡基因注入单价 =====
            Text.Font = GameFont.Medium;
            listing.Label("UGCPatch_ArchiteSilverHeader".Translate());
            Text.Font = prevFont;
            listing.GapLine(6f);

            listing.Label("UGCPatch_ArchiteSilverLabel".Translate().ToString()
                + ": " + "UGCPatch_SilverValue".Translate(ArchiteGeneSilver.ToString()).ToString());
            listing.Gap(2f);

            float silverSlider = listing.Slider(
                ArchiteGeneSilver, MinArchiteGeneSilver, MaxArchiteGeneSilver);
            architeGeneSilver = SnapArchiteGeneSilver(silverSlider);
            listing.Gap(2f);
            listing.Label("UGCPatch_ArchiteSilverHint".Translate());
            listing.Gap(12f);

            // ===== 恢复默认 =====
            if (listing.ButtonText("UGCPatch_ResetDefaults".Translate().ToString()))
            {
                ResetToDefaults();
                Messages.Message("UGCPatch_ResetDone".Translate(), MessageTypeDefOf.TaskCompletion, false);
            }
            listing.Gap(4f);
            listing.Label("UGCPatch_ResetDefaultsDesc".Translate());

            listing.End();
        }
    }
}
