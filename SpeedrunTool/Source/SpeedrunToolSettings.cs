using Celeste.Mod.SpeedrunTool.Message;
using Celeste.Mod.SpeedrunTool.Other;
using Celeste.Mod.SpeedrunTool.RoomTimer;
using Celeste.Mod.SpeedrunTool.SaveLoad;
using Microsoft.Xna.Framework.Input;
using System.Collections.Generic;
using System.Text;

namespace Celeste.Mod.SpeedrunTool;

[SettingName(DialogIds.SpeedrunTool)]
public class SpeedrunToolSettings : EverestModuleSettings {
    public static SpeedrunToolSettings Instance { get; private set; }

    public SpeedrunToolSettings() {
        Instance = this;
    }

    public void OnLoadSettings() {
        SetGcMemoryThreshold(Doubled_GcMemoryThreshold);
        StateManager.AllowSaveLoadWhenWaiting = SaveWhileWaiting;

        LogForDiagnostics();
    }

    private void LogForDiagnostics() {
        StringBuilder sb = new();
        sb.Append("GC Settings:");
        sb.Append($"\n - {nameof(GcAfterLoadState)}: {GcAfterLoadState}");
        if (GcAfterLoadState) {
            sb.Append($" | {nameof(StateManager.MemoryThreshold)}: {StateManager.MemoryThreshold} GB");
        }
        sb.Append($"\n - {nameof(GcAfterClearState)}: {GcAfterClearState}");
        sb.Append($"\n - {nameof(GcCompactLoh)}: {GcCompactLoh}");
        sb.Append($"\n - {nameof(GcBackgroundCollect)}: {GcBackgroundCollect}");
        sb.Append($"\n - {nameof(PreCloneEntities)}: {PreCloneEntities}");
        sb.Append($"\n - {nameof(IgnoreDecoEntities)}: {IgnoreDecoEntities}");
        sb.Append($"\n - {nameof(ShareMTexture)}: {ShareMTexture}");
        sb.Append($"\n - {nameof(ShareLevelData)}: {ShareLevelData}");
        sb.Append($"\n - {nameof(ShareLevelDataAll)}: {ShareLevelDataAll}");
        sb.Append($"\n - ProfileSaveLoad: {SaveLoadProfiler.Enabled}");
        sb.Append($"\n - {nameof(QueueSaveWhenBusy)}: {QueueSaveWhenBusy}, {nameof(SaveWhileWaiting)}: {SaveWhileWaiting}, {nameof(RotateSaveSlots)}: {RotateSaveSlots}({RotateSlotCount})");
        Logger.Info("SpeedrunTool", sb.ToString());
        // log it in LogLevel.Info, so we know more to diagnose when user gives us a log.txt
    }

    public bool Enabled { get; set; } = true;

    #region RoomTimer

    public RoomTimerType RoomTimerType { get; set; } = RoomTimerType.Off;
    public int NumberOfRooms { get; set; } = 1;
    public EndPoint.SpriteStyle EndPointStyle { get; set; } = EndPoint.SpriteStyle.Flag;
    public bool TimeSummitFlag { get; set; } = true;
    public bool TimeHeartCassette { get; set; } = true;
    public bool AutoResetRoomTimer { get; set; } = true;
    public bool DisplayRoomGold { get; set; } = true;
    public RoomTimerExportType RoomTimerExportType { get; set; } = RoomTimerExportType.Clipboard;

    #endregion

    #region State

    public bool AutoLoadStateAfterDeath { get; set; } = false;
    public bool AutoClearStateOnScreenTransition { get; set; } = false;
    public bool AutoClearStateOnSceneSwitch { get; set; } = true;
    public FreezeAfterLoadStateType FreezeAfterLoadStateType { get; set; } = FreezeAfterLoadStateType.On;
    public bool GcAfterLoadState { get; set; } = false;

    public int Doubled_GcMemoryThreshold = 5;

    public static void SetGcMemoryThreshold(int doubleNum) {
        StateManager.MemoryThreshold = doubleNum / 2f;
    }
    public bool GcAfterClearState { get; set; } = true;

    /// <summary>
    /// 回收时是否压缩 LOH。旧实现固定压缩, 在 2.5GB 堆上会带来 0.7~2.1s 的停顿;
    /// 关闭后只做 sweep, 通常快一个数量级。碎片化明显时再打开 (或用控制台 srt_gc on)。
    /// </summary>
    public bool GcCompactLoh { get; set; } = false;

    /// <summary>
    /// 内存未超阈值时, 回收改为后台非阻塞 GC (blocking:false), 主线程不再白卡 400~940ms。
    /// 超过阈值时仍然阻塞回收, 保证内存真的降下来。
    /// </summary>
    public bool GcBackgroundCollect { get; set; } = true;

    /// <summary>
    /// 存档后在后台预克隆一份实体图, 用于加速读档 (读档 writeback 3ms vs 30ms)。
    /// 代价: 内存翻倍 + GC 压力, 且下一次清档要等它跑完 (实测 305~413ms 尖峰)。
    /// </summary>
    public bool PreCloneEntities { get; set; } = true;

    /// <summary>
    /// 实验性: 把"纯装饰"实体 (无碰撞盒、类型没重写 Update/Added/Removed/Awake、组件全是图形类)
    /// 排除在快照之外。它们在存档前被摘下、读档后放回, 因此不会消失, 只是状态停在摘下那一刻。
    /// 大厅这类装饰密集的房间能显著减小快照体积。默认关闭。
    /// </summary>
    public bool IgnoreDecoEntities { get; set; } = false;

    /// <summary>
    /// 深克隆时让 MTexture 直接共享原对象 (它本质不可变: 图集坐标 + 名字)。
    /// 每个 Sprite 的每一帧都是一个 MTexture, 装饰密集的房间快照里可能有上百万个克隆,
    /// 实测占 166~451MB 的大部分。默认开启, 出问题可 srt_tex off 退回。
    /// </summary>
    public bool ShareMTexture { get; set; } = true;

    /// <summary>
    /// 深克隆时共享房间定义里的"条目对象"(EntityData / DecalData)。实测一个 SJ 大厅快照 106 万个
    /// 克隆对象里, 实体只占 ~1%, 其余几乎全是这些条目 + 它们内部的字典条目。
    /// LevelData 本身仍然克隆, 所以"运行时往 LevelData.Entities 追加实体"的行为仍能被读档回滚。
    /// 默认开启, 出问题可 srt_mapdata off 退回。
    /// </summary>
    public bool ShareLevelData { get; set; } = true;

    /// <summary>
    /// 更激进的一档: 连 LevelData 本身也共享 (最省, 但会动态修改 LevelData 的 Mod 受影响)。
    /// 用 srt_mapdata all 打开。
    /// </summary>
    public bool ShareLevelDataAll { get; set; } = false;
    /// <summary>
    /// 忙碌时（读档后的 wipe / 冻结窗口）按存档不报 Busy，而是排队，等空闲后自动补一次。
    /// 高速图里"读完立刻存"很常见。
    /// </summary>
    public bool QueueSaveWhenBusy { get; set; } = true;

    /// <summary>
    /// 允许在"读档后的等待状态"（State.Waiting，画面冻结等按键）里直接存档，而不是排队。
    /// </summary>
    public bool SaveWhileWaiting { get; set; } = false;

    /// <summary>
    /// 轮换存档：每次存档自动切到环里的下一个槽（1..RotateSlotCount），
    /// 旧快照留在上一个槽里，配合 NumPad9 的预览 UI 就相当于"存档历史"。
    /// </summary>
    public bool RotateSaveSlots { get; set; } = false;

    public int RotateSlotCount { get; set; } = 2;
    public bool SaveTimeAndDeaths { get; set; } = false;
    public bool SaveExtendedVariants { get; set; } = true;

    public bool SaveInLuaCutscene { get; set; } = false;
    public bool NoMessageAfterSaveLoad { get; set; } = false;
    public bool AllowStateDuringDeath { get; set; } = false;
    public bool ClearStateOnHotReload { get; set; } = true;

    #endregion

    #region DeathStatistics

    public bool DeathStatistics { get; set; } = false;
    public int MaxNumberOfDeathData { get; set; } = 2;

    #endregion

    #region MoreOptions
    public TeleportRoomCategory TeleportRoomCategory { get; set; } = TeleportRoomCategory.Any;
    public int RespawnSpeed { get; set; } = 1;
    public int RestartChapterSpeed { get; set; } = 1;
    public bool SkipRestartChapterScreenWipe { get; set; } = false;
    public bool AllowPauseDuringDeath { get; set; } = false;
    public bool MuteInBackground { get; set; } = false;
    public bool FixCoreRefillDashAfterTeleport { get; set; } = true;

    public bool EmoteModPULP { get; set; } = true;
    public PopupMessageStyle PopupMessageStyle { get; set; } = PopupMessageStyle.Tooltip;
    public SpeedrunType AreaCompleteEnableTimerType { get; set; } = SpeedrunType.Off;
    public bool Hotkeys { get; set; } = true;
    public bool UnlockCamera { get; set; } = true;

    #endregion

    #region HotkeyConfig

    public List<Keys> KeyboardToggleHotkeys { get; set; } = Hotkey.ToggleHotkeys.GetDefaultKeys();
    public List<Keys> KeyboardSaveState { get; set; } = Hotkey.SaveState.GetDefaultKeys();
    public List<Keys> KeyboardLoadState { get; set; } = Hotkey.LoadState.GetDefaultKeys();
    public List<Keys> KeyboardClearState { get; set; } = Hotkey.ClearState.GetDefaultKeys();
    public List<Keys> KeyboardClearAllState { get; set; } = Hotkey.ClearAllState.GetDefaultKeys();
    public List<Keys> KeyboardOpenDebugMap { get; set; } = Hotkey.OpenDebugMap.GetDefaultKeys();
    public List<Keys> KeyboardResetRoomTimerPb { get; set; } = Hotkey.ResetRoomTimerPb.GetDefaultKeys();
    public List<Keys> KeyboardSwitchRoomTimer { get; set; } = Hotkey.SwitchRoomTimer.GetDefaultKeys();
    public List<Keys> KeyboardIncreaseTimedRooms { get; set; } = Hotkey.IncreaseTimedRooms.GetDefaultKeys();
    public List<Keys> KeyboardDecreaseTimedRooms { get; set; } = Hotkey.DecreaseTimedRooms.GetDefaultKeys();
    public List<Keys> KeyboardSetEndPoint { get; set; } = Hotkey.SetEndPoint.GetDefaultKeys();
    public List<Keys> KeyboardSetAdditionalEndPoint { get; set; } = Hotkey.SetAdditionalEndPoint.GetDefaultKeys();
    public List<Keys> KeyboardCheckDeathStatistics { get; set; } = Hotkey.CheckDeathStatistics.GetDefaultKeys();
    public List<Keys> KeyboardTeleportToPreviousRoom { get; set; } = Hotkey.TeleportToPreviousRoom.GetDefaultKeys();
    public List<Keys> KeyboardTeleportToNextRoom { get; set; } = Hotkey.TeleportToNextRoom.GetDefaultKeys();
    public List<Keys> KeyboardSwitchAutoLoadState { get; set; } = Hotkey.SwitchAutoLoadState.GetDefaultKeys();
    public List<Keys> KeyboardSpawnTowerViewer { get; set; } = Hotkey.SpawnTowerViewer.GetDefaultKeys();
    public List<Keys> KeyboardToggleFullscreen { get; set; } = Hotkey.ToggleFullscreen.GetDefaultKeys();
    public List<Keys> KeyboardExportRoomTimes { get; set; } = Hotkey.ExportRoomTimes.GetDefaultKeys();
    public List<Keys> KeyboardSaveSlot1 { get; set; } = Hotkey.SaveSlot1.GetDefaultKeys();
    public List<Keys> KeyboardSaveSlot2 { get; set; } = Hotkey.SaveSlot2.GetDefaultKeys();
    public List<Keys> KeyboardSaveSlot3 { get; set; } = Hotkey.SaveSlot3.GetDefaultKeys();
    public List<Keys> KeyboardSaveSlot4 { get; set; } = Hotkey.SaveSlot4.GetDefaultKeys();
    public List<Keys> KeyboardSaveSlot5 { get; set; } = Hotkey.SaveSlot5.GetDefaultKeys();
    public List<Keys> KeyboardSaveSlot6 { get; set; } = Hotkey.SaveSlot6.GetDefaultKeys();
    public List<Keys> KeyboardSaveSlot7 { get; set; } = Hotkey.SaveSlot7.GetDefaultKeys();
    public List<Keys> KeyboardSaveSlot8 { get; set; } = Hotkey.SaveSlot8.GetDefaultKeys();

    public List<Keys> KeyboardSaveSlot9 { get; set; } = Hotkey.SaveSlot9.GetDefaultKeys();

    public List<Keys> KeyboardSwitchToNextSlot { get; set; } = Hotkey.SwitchToNextSlot.GetDefaultKeys();

    public List<Keys> KeyboardSwitchToPreviousSlot { get; set; } = Hotkey.SwitchToPreviousSlot.GetDefaultKeys();

    public List<Keys> KeyboardSaveToNextSlot { get; set; } = Hotkey.SaveToNextSlot.GetDefaultKeys();

    public List<Keys> KeyboardLoadFromLastSlot { get; set; } = Hotkey.LoadFromLastSlot.GetDefaultKeys();

    public List<Keys> KeyboardToggleSaveLoadUI { get; set; } = Hotkey.ToggleSaveLoadUI.GetDefaultKeys();

    public List<Buttons> ControllerToggleHotkeys { get; set; } = new List<Buttons>();
    public List<Buttons> ControllerSaveState { get; set; } = new List<Buttons>();
    public List<Buttons> ControllerLoadState { get; set; } = new List<Buttons>();
    public List<Buttons> ControllerClearState { get; set; } = new List<Buttons>();
    public List<Buttons> ControllerClearAllState { get; set; } = new List<Buttons>();
    public List<Buttons> ControllerOpenDebugMap { get; set; } = new List<Buttons>();
    public List<Buttons> ControllerResetRoomTimerPb { get; set; } = new List<Buttons>();
    public List<Buttons> ControllerSwitchRoomTimer { get; set; } = new List<Buttons>();
    public List<Buttons> ControllerIncreaseTimedRooms { get; set; } = new List<Buttons>();
    public List<Buttons> ControllerDecreaseTimedRooms { get; set; } = new List<Buttons>();
    public List<Buttons> ControllerSetEndPoint { get; set; } = new List<Buttons>();
    public List<Buttons> ControllerSetAdditionalEndPoint { get; set; } = new List<Buttons>();
    public List<Buttons> ControllerSetRoomIdEndPoint { get; set; } = new List<Buttons>();
    public List<Buttons> ControllerClearRoomIdEndPoint { get; set; } = new List<Buttons>();
    public List<Buttons> ControllerCheckDeathStatistics { get; set; } = new List<Buttons>();
    public List<Buttons> ControllerTeleportToPreviousRoom { get; set; } = new List<Buttons>();
    public List<Buttons> ControllerTeleportToNextRoom { get; set; } = new List<Buttons>();
    public List<Buttons> ControllerSwitchAutoLoadState { get; set; } = new List<Buttons>();
    public List<Buttons> ControllerSpawnTowerViewer { get; set; } = new List<Buttons>();
    public List<Buttons> ControllerToggleFullscreen { get; set; } = new List<Buttons>();
    public List<Buttons> ControllerExportRoomTimes { get; set; } = new List<Buttons>();
    public List<Buttons> ControllerSaveSlot1 { get; set; } = new List<Buttons>();

    public List<Buttons> ControllerSaveSlot2 { get; set; } = new List<Buttons>();

    public List<Buttons> ControllerSaveSlot3 { get; set; } = new List<Buttons>();

    public List<Buttons> ControllerSaveSlot4 { get; set; } = new List<Buttons>();

    public List<Buttons> ControllerSaveSlot5 { get; set; } = new List<Buttons>();

    public List<Buttons> ControllerSaveSlot6 { get; set; } = new List<Buttons>();

    public List<Buttons> ControllerSaveSlot7 { get; set; } = new List<Buttons>();

    public List<Buttons> ControllerSaveSlot8 { get; set; } = new List<Buttons>();

    public List<Buttons> ControllerSaveSlot9 { get; set; } = new List<Buttons>();

    public List<Buttons> ControllerSwitchToNextSlot { get; set; } = new List<Buttons>();

    public List<Buttons> ControllerSwitchToPreviousSlot { get; set; } = new List<Buttons>();

    public List<Buttons> ControllerSaveToNextSlot { get; set; } = new List<Buttons>();

    public List<Buttons> ControllerLoadFromLastSlot { get; set; } = new List<Buttons>();

    public List<Buttons> ControllerToggleSaveLoadUI { get; set; } = new List<Buttons>();


    #endregion HotkeyConfig
}

public enum FreezeAfterLoadStateType {
    Off,
    On,
    IgnoreHoldingKeys
}

public enum TeleportRoomCategory {
    Default,
    Any
}
