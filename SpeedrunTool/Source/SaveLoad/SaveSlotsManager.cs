using System.Collections.Generic;
using EventInstance = FMOD.Studio.EventInstance;

namespace Celeste.Mod.SpeedrunTool.SaveLoad;
internal static class SaveSlotsManager {
    public static IEnumerable<SaveSlot> SaveSlots => Dictionary.Values;

    public static Dictionary<string, SaveSlot> Dictionary = [];

    internal static SaveSlot Slot;

    internal static string SlotName { get; private set; } = GetSlotName(1);

    public static StateManager StateManagerInstance => Slot.StateManager;

    public static bool IsSaved(int index) {
        return IsSaved(GetSlotName(index));
    }

    public static bool IsSaved(string name) {
        if (Dictionary.TryGetValue(name, out SaveSlot slot)) {
            return slot.StateManager.IsSaved;
        }
        return false;
    }
    public static bool IsSaved() {
        return Slot.StateManager.IsSaved;
    }

    public static bool SwitchSlot(int index) {
        return SwitchSlot(GetSlotName(index));
    }

    /// <summary>
    /// Switch to the slot with the specified name. Will be automatically created if the slot does not exist.
    /// </summary>
    /// <returns> Success or not </returns>
    public static bool SwitchSlot(string name) {
        if (!IsAllFree()) {
            // even we are not actually switching slot (i.e name == SlotName), still return false
            // to ensure safety
            return false;
        }
        WaitUntilThreadSafe();
        if (name != SlotName) {
            Logger.Info("SpeedrunTool", $"Switch to [{name}]");
        }

        // execute even if name == SlotName, so the resulting slot will always be right (it's not necessarily created yet!)
        SlotName = name;
        if (Dictionary.TryGetValue(name, out SaveSlot slot)) {
            Slot = slot;
        }
        else {
            Slot = new SaveSlot(name);
            Dictionary.Add(name, Slot);
        }
        return true;
    }
    public static string GetSlotName(int index) {
        return index == 1 ? "Default Slot" : $"SaveSlot@{index}";
    }

    public static bool CannotSaveLoad() {
        if (Engine.Scene is not Level) {
            return true;
        }

        if (!IsAllFree()) {
            return true;
        }
        return false;
    }
    #region 排队存档 / 轮换存档

    // 忙碌（读档后的 wipe、冻结）时按下的存档请求，等空闲后自动补一次
    internal static bool PendingSave;

    // 轮换存档：每次存档自动切到环里的下一个槽，旧快照自然留在上一个槽里，
    // 配合已有的预览 UI（NumPad9，左右翻页、回车读档）就等于一个"存档历史"。
    private static int rotateIndex = 1;

    private static bool IsIdle() {
        foreach (SaveSlot slot in SaveSlots) {
            if (slot.StateManager.State != State.None) {
                return false;
            }
        }

        return true;
    }

    private static void AdvanceRotateSlot() {
        if (!ModSettings.RotateSaveSlots) {
            return;
        }

        int count = Math.Clamp(ModSettings.RotateSlotCount, 2, 4);
        rotateIndex = rotateIndex % count + 1;
        SwitchSlot(rotateIndex);
    }

    internal static void TryFlushPendingSave() {
        if (!PendingSave || Engine.Scene is not Level || !IsIdle()) {
            return;
        }

        PendingSave = false;
        if (SaveState(out string popup)) {
            Message.PopupMessageUtils.Show(popup, null);
        }
    }

    private static void LevelOnUpdate(On.Celeste.Level.orig_Update orig, Level self) {
        orig(self);
        TryFlushPendingSave();
    }

    #endregion
    public static bool SaveState(out string popup) {
        bool b;
        if (CannotSaveLoad()) {
            // 读档后的 wipe / 冻结窗口里按存档不再直接报 Busy：
            // 排队等空闲后自动补一次（高速图里"读完立刻存"很常见）
            if (ModSettings.QueueSaveWhenBusy && Engine.Scene is Level) {
                PendingSave = true;
                popup = "Save queued: will save as soon as SpeedrunTool is free.";
                Logger.Info("SpeedrunTool/SaveState", popup);
                return true;
            }

            b = false;
            popup = "Failed to Save: SpeedrunTool is Busy!";
        }
        else {
            AdvanceRotateSlot();
            b = StateManagerInstance.SaveStateImpl(false, out string message);
            popup = message;
            if (b) {
                MoreSaveSlotsUI.Snapshot.RequireCaptureSnapshot(SlotName);
            }
        }
        if (!b) {
            Logger.Verbose("SpeedrunTool/SaveState", popup);
        }

        return b;
    }


    public static bool LoadState(out string popup) {
        bool b;
        if (CannotSaveLoad()) {
            b = false;
            popup = "Failed to Save: SpeedrunTool is Busy!";
        }
        else {
            b = StateManagerInstance.LoadStateImpl(false, out string message);
            popup = message;
        }
        if (!b) {
            Logger.Verbose("SpeedrunTool/LoadState", popup);
        }

        return b;
    }


    /// <summary>
    /// Clear current save slot 
    /// </summary>
    public static bool ClearState() {
        return StateManagerInstance.ClearStateImpl(hasGc: true);
    }

    public static void ClearAll() {
        WaitUntilThreadSafe();
        bool anySaved = false;
        foreach (SaveSlot slot in Dictionary.Values) {
            anySaved = anySaved || slot.StateManager.IsSaved;
            Slot = slot; // 由于 clearState action 可能依赖于 slot, 所以这是必要的
            SlotName = Slot.Name; // Name 当然也要同步修改
            StateManagerInstance.ClearStateImpl(hasGc: false);
        }
        Dictionary = [];

        MoreSaveSlotsUI.Snapshot.ClearAll();

        SwitchSlot(1);
        if (anySaved) {
            StateManagerInstance.GcCollect(force: true);
        }
    }

    private static void HandleSwitchScene(On.Monocle.Scene.orig_Begin orig, Scene self) {
        orig(self);
        PendingSave = false; // 换房间后不能再补存到别的房间
        SaveSlot current = Slot;
        WaitUntilThreadSafe();
        foreach (SaveSlot slot in SaveSlots) {
            Slot = slot;
            SlotName = Slot.Name;
            slot.StateManager.HandleSwitchScene(self);
            slot.StateManager.preCloneTask?.Wait();
            // 因为涉及到存档槽切换, 干脆等一等
        }
        Slot = current;
        SlotName = current.Name;
    }

    /// <summary>
    /// When StateManager is busy (saving/loading), we shouldn't do anything
    /// </summary>
    public static bool IsFree(this StateManager manager) {
        return manager.State == State.None || manager.State == State.Waiting;
    }
    /// <summary>
    /// When any StateManager is busy (saving/loading), we shouldn't do anything
    /// </summary>
    public static bool IsAllFree() {
        foreach (SaveSlot slot in SaveSlots) {
            if (!IsFree(slot.StateManager)) {
                return false;
            }
        }
        return true;
    }

    private static void WaitUntilThreadSafe() {
        // preCloneTask 涉及到多线程, 而 DeepClonerUtils 会多次用到 StateManager.Instance, 这玩意线程不安全
        // 所以我们一定要等它结束了再说
        foreach (SaveSlot s in SaveSlots) {
            s.StateManager.preCloneTask?.Wait();
        }
        // 此外 clearState 等也有可能访问 StateManager.Instance
    }

    internal static void RequireReInit() {
        foreach (SaveSlot slot in SaveSlots) {
            slot.ValueDictionaryInitialized = false;
            slot.AllSavedValues = null;
        }
    }
    internal static void AfterAssetReload() {
        Dictionary = [];
        SwitchSlot(1);
    }

    [Load]
    private static void Load() {
        On.Monocle.Scene.Begin += HandleSwitchScene;
        On.Celeste.Level.Update += LevelOnUpdate;
    }

    [Unload]
    private static void Unload() {
        On.Monocle.Scene.Begin -= HandleSwitchScene;
        On.Celeste.Level.Update -= LevelOnUpdate;
    }

    #region Tas
    public static void OnTasDisableRun() {
        SwitchSlot(1);
    }
    public static bool SaveStateTas(string slot) {
        if (CannotSaveLoad()) {
            return false;
        }

        SwitchSlot(slot);
        bool result = StateManagerInstance.SaveStateImpl(true, out _);
        return result;
    }
    public static bool LoadStateTas(string slot) {
        if (CannotSaveLoad()) {
            return false;
        }

        SwitchSlot(slot);
        bool result = StateManagerInstance.LoadStateImpl(true, out _);
        return result;
    }
    public static void ClearStateTas(string slot) {
        if (SwitchSlot(slot)) {
            StateManagerInstance.ClearStateImpl(hasGc: true);
        }
    }
    #endregion
}

public class SaveSlot {
    public string Name;

    public StateManager StateManager;

    public bool ValueDictionaryInitialized = false;

    public Dictionary<int, Dictionary<Type, Dictionary<string, object>>> AllSavedValues = [];

    public readonly List<EventInstance> ClonedEventInstancesWhenSave = [];
    public readonly List<EventInstance> ClonedEventInstancesWhenPreClone = [];

    public SaveSlot(string name) {
        Name = name;
        ValueDictionaryInitialized = false;
        AllSavedValues = [];
        StateManager = new();
        StateManager.SlotName = Name;
        ClonedEventInstancesWhenSave = [];
        ClonedEventInstancesWhenPreClone = [];
    }
}