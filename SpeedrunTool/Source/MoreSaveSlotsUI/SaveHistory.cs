using Celeste.Mod.SpeedrunTool.SaveLoad;
using System.Collections.Generic;

namespace Celeste.Mod.SpeedrunTool.MoreSaveSlotsUI;

/// <summary>
/// 存档历史：一条 = 一份**独立的快照引用**，完全不复用 <see cref="SaveSlot"/>、也不切换当前槽。
/// <para>存档前把当前槽的快照"抽"出来（只是搬引用 + 换掉值字典），存档成功后压入历史环。
/// 读档时把某条历史"借"给当前槽加载一次，读完立刻还原，所以存档永远不会写进历史。</para>
/// </summary>
internal static class SaveHistory {
    internal const int MaxCount = 5;

    internal sealed class Entry {
        internal string Label;
        internal Level Level;
        internal SaveData SaveData;
        internal System.Collections.IEnumerator Transition;
        internal Dictionary<int, Dictionary<Type, Dictionary<string, object>>> Values;
        internal Snapshot Preview;
    }

    private static readonly List<Entry> entries = [];

    internal static int Count => System.Math.Clamp(ModSettings.SaveHistoryCount, 0, MaxCount);

    internal static bool Enabled => Count > 0;

    internal static IReadOnlyList<Entry> Entries => entries;

    /// <summary>存档前调用：把当前槽的快照抽成一条历史（返回 null 表示没有旧状态）。</summary>
    internal static Entry Capture(SaveSlot slot) {
        if (!Enabled || slot?.StateManager is not { IsSaved: true } sm) {
            return null;
        }

        Entry entry = new() {
            Label = sm.SlotDescription,
            Level = sm.savedLevel,
            SaveData = sm.savedSaveData,
            Transition = sm.savedTransitionRoutine,
            Values = slot.AllSavedValues,
            Preview = slot.Preview
        };

        // 让随后的 ClearStateImpl 清的是"新字典/空预览"，不会动到我们手里的这份
        slot.AllSavedValues = [];
        slot.Preview = null;
        Snapshot.SnapshotsDict.Remove(sm.SlotName);
        return entry;
    }

    /// <summary>存档成功后调用：压入历史环（满了丢最旧）。</summary>
    internal static void Push(Entry entry) {
        if (entry is null || !Enabled) {
            return;
        }

        entries.Insert(0, entry);
        while (entries.Count > Count) {
            Entry dropped = entries[entries.Count - 1];
            entries.RemoveAt(entries.Count - 1);
            dropped.Preview?.Tex?.Dispose();
            dropped.Preview = null;
        }
    }

    /// <summary>读档：把这条历史"借"给当前槽加载一次，然后还原。</summary>
    internal static bool Load(Entry entry) {
        if (entry?.Level is null || SaveSlotsManager.Slot is not { } slot) {
            return false;
        }

        StateManager sm = slot.StateManager;

        // 借出前备份当前槽自己的东西
        Level backupLevel = sm.savedLevel;
        SaveData backupSaveData = sm.savedSaveData;
        System.Collections.IEnumerator backupTransition = sm.savedTransitionRoutine;
        Dictionary<int, Dictionary<Type, Dictionary<string, object>>> backupValues = slot.AllSavedValues;

        sm.savedLevel = entry.Level;
        sm.savedSaveData = entry.SaveData;
        sm.savedTransitionRoutine = entry.Transition;
        slot.AllSavedValues = entry.Values;

        try {
            return SaveLoadHotkeys.LoadStateAndMessage();
        }
        finally {
            sm.savedLevel = backupLevel;
            sm.savedSaveData = backupSaveData;
            sm.savedTransitionRoutine = backupTransition;
            slot.AllSavedValues = backupValues;
        }
    }

    internal static void Clear() {
        foreach (Entry entry in entries) {
            entry.Preview?.Tex?.Dispose();
        }

        entries.Clear();
    }

    internal static string Label(int index) {
        return index >= 0 && index < entries.Count ? entries[index].Label ?? "" : "";
    }
}
