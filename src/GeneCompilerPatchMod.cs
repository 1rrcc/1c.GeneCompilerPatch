using UnityEngine;
using Verse;

namespace GeneCompilerPatch
{
    public sealed class GeneCompilerPatchMod : Mod
    {
        public static GeneCompilerPatchSettings Settings;

        public GeneCompilerPatchMod(ModContentPack content)
            : base(content)
        {
            Settings = GetSettings<GeneCompilerPatchSettings>();
        }

        public override string SettingsCategory()
        {
            return "UniversalGeneCompiler Patch";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Settings.DoWindowContents(inRect);
        }
    }
}
