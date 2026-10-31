using Celeste.Mod.SpeedrunTool.RoomTimer;
using Celeste.Mod.SpeedrunTool.SaveLoad;

namespace Celeste.Mod.SpeedrunTool.ModInterop;
internal class CommandExport {

    [Command("srt_exportroomtimes", "export room timer data in csv format to clipboard or a file in gamePath/SRTool_RoomTimeExports folder (SpeedrunTool)")]
    public static void CmdExportRoomTimes() => RoomTimerManager.CmdExportRoomTimes();

    [Command("switch_slot", "Switch to another SRT save slot")]
    public static bool CmdSwitchSlot(string name) => SaveSlotsManager.SwitchSlot(name);

    [Command("srt_profile", "Toggle SpeedrunTool save/load profiling, logs per-phase timings. usage: srt_profile on | off (SpeedrunTool)")]
    public static void CmdProfile(string arg) {
        SaveLoadProfiler.Enabled = !string.Equals(arg, "off", StringComparison.OrdinalIgnoreCase);
        Logger.Info("SpeedrunTool/Profile", $"Profiling {(SaveLoadProfiler.Enabled ? "ON" : "OFF")}.");
    }

    [Command("srt_gc", "Toggle LOH compaction when SpeedrunTool collects garbage (off = faster, less stall). usage: srt_gc on | off (SpeedrunTool)")]
    public static void CmdGc(string arg) {
        ModSettings.GcCompactLoh = !string.Equals(arg, "off", StringComparison.OrdinalIgnoreCase);
        Logger.Info("SpeedrunTool", $"GcCompactLoh = {ModSettings.GcCompactLoh}.");
    }

    [Command("srt_preclone", "Toggle background pre-clone after saving (off = less memory/GC pressure, load ~+30ms). usage: srt_preclone on | off (SpeedrunTool)")]
    public static void CmdPreClone(string arg) {
        ModSettings.PreCloneEntities = !string.Equals(arg, "off", StringComparison.OrdinalIgnoreCase);
        Logger.Info("SpeedrunTool", $"PreCloneEntities = {ModSettings.PreCloneEntities}.");
    }

    [Command("srt_bggc", "Toggle non-blocking background GC below the memory threshold. usage: srt_bggc on | off (SpeedrunTool)")]
    public static void CmdBackgroundGc(string arg) {
        ModSettings.GcBackgroundCollect = !string.Equals(arg, "off", StringComparison.OrdinalIgnoreCase);
        Logger.Info("SpeedrunTool", $"GcBackgroundCollect = {ModSettings.GcBackgroundCollect}.");
    }

    [Command("srt_deco", "EXPERIMENTAL: skip pure-decoration entities when cloning (smaller snapshot in deco-heavy rooms). usage: srt_deco on | off (SpeedrunTool)")]
    public static void CmdDeco(string arg) {
        ModSettings.IgnoreDecoEntities = !string.Equals(arg, "off", StringComparison.OrdinalIgnoreCase);
        Logger.Info("SpeedrunTool", $"IgnoreDecoEntities = {ModSettings.IgnoreDecoEntities}.");
    }
    [Command("srt_tex", "Share MTexture instances instead of deep-cloning them (huge snapshot size win). usage: srt_tex on | off (SpeedrunTool)")]
    public static void CmdShareMTexture(string arg) {
        ModSettings.ShareMTexture = !string.Equals(arg, "off", StringComparison.OrdinalIgnoreCase);
        Logger.Info("SpeedrunTool", $"ShareMTexture = {ModSettings.ShareMTexture}.");
    }
        [Command("srt_mapdata", "Share room definition entries (EntityData/DecalData) instead of cloning them. usage: srt_mapdata on | off | all (SpeedrunTool)")]
    public static void CmdShareLevelData(string arg) {
        if (string.Equals(arg, "all", StringComparison.OrdinalIgnoreCase)) {
            ModSettings.ShareLevelData = true;
            ModSettings.ShareLevelDataAll = true;
        }
        else if (string.Equals(arg, "off", StringComparison.OrdinalIgnoreCase)) {
            ModSettings.ShareLevelData = false;
            ModSettings.ShareLevelDataAll = false;
        }
        else {
            ModSettings.ShareLevelData = true;
            ModSettings.ShareLevelDataAll = false;
        }

        Logger.Info("SpeedrunTool", $"ShareLevelData = {ModSettings.ShareLevelData}, ShareLevelDataAll = {ModSettings.ShareLevelDataAll}.");
    }}
