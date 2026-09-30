using RimWorld;
using UnityEngine;
using Verse;

namespace GeneCompilerPatch
{
    /// <summary>补丁设置：定向提取冷却时长与提取作业时长。</summary>
    public sealed class GeneCompilerPatchSettings : ModSettings
    {
        public const float DefaultCooldownDays = 1f;
        public const float MinCooldownDays = 0f;
        public const float MaxCooldownDays = 7f;

        public const float DefaultExtractionHours = 6f;
        public const float MinExtractionHours = 0f;
        public const float MaxExtractionHours = 24f;

        /// <summary>定向提取冷却，单位「天」。0 = 不施加冷却。</summary>
        public float cooldownDays = DefaultCooldownDays;

        /// <summary>提取作业计时，单位「游戏内小时」。0 = 关闭计时（恢复原版瞬时提取）。</summary>
        public float extractionHours = DefaultExtractionHours;

        public static float ClampDays(float value)
        {
            return Mathf.Clamp(value, MinCooldownDays, MaxCooldownDays);
        }

        public static float ClampExtractionHours(float value)
        {
            return Mathf.Clamp(value, MinExtractionHours, MaxExtractionHours);
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

        public void ResetToDefaults()
        {
            cooldownDays = DefaultCooldownDays;
            extractionHours = DefaultExtractionHours;
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref cooldownDays, "cooldownDays", DefaultCooldownDays);
            Scribe_Values.Look(ref extractionHours, "extractionHours", DefaultExtractionHours);
            cooldownDays = ClampDays(cooldownDays);
            extractionHours = ClampExtractionHours(extractionHours);
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
