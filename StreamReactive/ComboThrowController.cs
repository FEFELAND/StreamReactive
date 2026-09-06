using System;
using HarmonyLib;

namespace StreamReactive;

/// <summary>
/// Throws a block at the player when their combo breaks from at/above the
/// configured threshold. Combo is tracked the same way BSDataPuller does: it is
/// incremented on every good cut and reset on a miss / bad cut / bomb hit.
/// The guard capsule (Enable Throw) must be enabled for a block to be thrown.
///
/// References (verified against Beat Saber 1.40.8):
///   - ScoreController no longer exposes a combo event/property; combo is
///     derived from scoring elements, so we hook
///     BeatmapObjectExecutionRatingsRecorder.HandleScoringForNoteDidFinish
///     (good cuts) and BeatmapObjectManager.HandleNoteControllerNoteWasMissed
///     / HandleNoteControllerNoteWasCut (breaks). See BSDataPuller
///     (github.com/ReadieFur/BSDataPuller) for the same approach.
/// </summary>
internal static class ComboThrowController
{
    private static int _combo;

    // ColorType (BeatmapCore) None == a bomb / non-colored note, which does not
    // break the in-game combo when merely passed. Resolved once via reflection.
    private static readonly System.Type? ColorTypeEnum = System.Type.GetType("ColorType, BeatmapCore");
    private static readonly object? ColorTypeNone = ColorTypeEnum?.GetField("None")?.GetValue(null);

    internal static void ResetCombo() => _combo = 0;

    internal static void OnGoodCut()
    {
        if (!Plugin.IsInGame)
            return;
        _combo++;
    }

    // True for a real colored-note miss (breaks combo). Bombs that merely pass
    // the player fire the same miss event but must NOT break our counter.
    private static bool IsColorNoteMiss(NoteController noteController)
    {
        if (ColorTypeEnum == null || ColorTypeNone == null || noteController == null)
            return true;
        try
        {
            var noteData = noteController.GetType().GetProperty("noteData")?.GetValue(noteController);
            if (noteData == null)
                return true;
            var colorType = noteData.GetType().GetProperty("colorType")?.GetValue(noteData);
            return colorType == null || !colorType.Equals(ColorTypeNone);
        }
        catch
        {
            return true;
        }
    }

    internal static void OnBreak()
    {
        var prev = _combo;
        _combo = 0;

        var cfg = PluginConfig.Instance;
        if (cfg == null || !cfg.ComboThrowEnabled || !cfg.Enabled || !Plugin.IsInGame)
            return;

        // Protected map (Noodle/Vivify/WIP): suppress the auto-throw like all other
        // transient effects.
        if (Plugin.IsMapProtectionActive())
        {
            Plugin.Log.Debug("ComboThrow: skipped (map protection active).");
            return;
        }

        if (prev >= cfg.ComboThrowThreshold)
        {
            Plugin.Log.Debug($"ComboThrow: combo broke from {prev} (>= {cfg.ComboThrowThreshold}); throwing a block.");
            ProjectileThrower.Throw(1);
        }
    }

    // Increment the running combo on every good cut.
    [HarmonyPatch(typeof(BeatmapObjectExecutionRatingsRecorder), "HandleScoringForNoteDidFinish")]
    internal static class GoodCutPatch
    {
        private static void Postfix(ScoringElement scoringElement)
        {
            if (scoringElement is GoodCutScoringElement)
                OnGoodCut();
        }
    }

    // Misses reset the combo (and may trigger a throw). Bomb passes also fire
    // this event but are not real combo breaks, so they are ignored.
    [HarmonyPatch(typeof(BeatmapObjectManager), "HandleNoteControllerNoteWasMissed")]
    internal static class MissPatch
    {
        private static void Postfix(NoteController noteController)
        {
            if (noteController == null) return;
            if (!IsColorNoteMiss(noteController)) return;
            OnBreak();
        }
    }

    // Bad cuts and bomb hits (allIsOK == false) reset the combo too.
    [HarmonyPatch(typeof(BeatmapObjectManager), "HandleNoteControllerNoteWasCut")]
    internal static class CutPatch
    {
        private static void Postfix(NoteController noteController, NoteCutInfo noteCutInfo)
        {
            if (!noteCutInfo.allIsOK)
                OnBreak();
        }
    }
}
