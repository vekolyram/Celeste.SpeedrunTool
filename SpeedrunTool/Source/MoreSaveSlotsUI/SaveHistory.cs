using Celeste.Mod.SpeedrunTool.SaveLoad;
using Microsoft.Xna.Framework.Input;
using System.Collections.Generic;

namespace Celeste.Mod.SpeedrunTool.MoreSaveSlotsUI;

/// <summary>
/// 存档历史（时间轴）。
/// <para>每次存档前，把"即将被覆盖"的那份快照整体挪进历史环，最旧的一条被丢弃。
/// 移动是**引用交换**（O(1)），不重新克隆，所以不会额外占用克隆时间。</para>
/// <para>每条历史 = 一个 <see cref="SaveSlot"/>（含自己的 StateManager / 补丁值字典 / 预览图），
/// 内存代价就是 N 份快照本身。</para>
/// </summary>
internal static class SaveHistory {
    internal const int MaxCount = 5;

    private static readonly List<SaveSlot> entries = [];

    internal static int Count => System.Math.Clamp(ModSettings.SaveHistoryCount, 0, MaxCount);

    internal static IReadOnlyList<SaveSlot> Entries {
        get {
            Ensure();
            return entries;
        }
    }

    private static string NameOf(int index) => "@history/" + index;

    internal static void Ensure() {
        int count = Count;
        while (entries.Count < count) {
            string name = NameOf(entries.Count);
            SaveSlot slot = new(name);
            entries.Add(slot);
            // 必须注册进 SaveSlotsManager，否则 SwitchSlot 会另建一个空槽，历史条目就读不回来
            SaveSlotsManager.Dictionary[name] = slot;
        }

        while (entries.Count > count) {
            SaveSlot removed = entries[entries.Count - 1];
            entries.RemoveAt(entries.Count - 1);
            SaveSlotsManager.Dictionary.Remove(removed.Name);
        }
    }

    /// <summary>把即将被覆盖的快照推进历史（在 SaveStateImpl 清掉它之前调用）。</summary>
    internal static void Push(SaveSlot current) {
        Ensure();
        if (entries.Count == 0 || current.StateManager is not { IsSaved: true }) {
            return;
        }

        // 整体下移一位，最旧的落到 current 上（随后会被 ClearStateImpl 清掉）
        for (int i = entries.Count - 1; i > 0; i--) {
            Swap(entries[i], entries[i - 1]);
        }

        Swap(entries[0], current);
    }

    private static void Swap(SaveSlot a, SaveSlot b) {
        (a.StateManager, b.StateManager) = (b.StateManager, a.StateManager);
        (a.AllSavedValues, b.AllSavedValues) = (b.AllSavedValues, a.AllSavedValues);
        (a.ValueDictionaryInitialized, b.ValueDictionaryInitialized) = (b.ValueDictionaryInitialized, a.ValueDictionaryInitialized);
        (a.ClonedEventInstancesWhenSave, b.ClonedEventInstancesWhenSave) = (b.ClonedEventInstancesWhenSave, a.ClonedEventInstancesWhenSave);
        (a.ClonedEventInstancesWhenPreClone, b.ClonedEventInstancesWhenPreClone) =
            (b.ClonedEventInstancesWhenPreClone, a.ClonedEventInstancesWhenPreClone);

        // StateManager 里的 SlotName 决定弹窗文案和预览图归属，跟着内容一起改
        if (a.StateManager is { } sa) {
            MovePreview(sa.SlotName, a.Name);
            sa.SlotName = a.Name;
        }

        if (b.StateManager is { } sb) {
            MovePreview(sb.SlotName, b.Name);
            sb.SlotName = b.Name;
        }
    }

    private static void MovePreview(string from, string to) {
        if (from == to || string.IsNullOrEmpty(from)) {
            return;
        }

        Dictionary<string, Snapshot> dict = Snapshot.SnapshotsDict;
        if (dict.TryGetValue(from, out Snapshot snap)) {
            dict.Remove(from);
            snap.Name = to;
            dict[to] = snap;
        }
    }

    /// <summary>历史第 index 条的描述（房间 + 帧号），没有就返回空串。</summary>
    internal static string Label(int index) {
        IReadOnlyList<SaveSlot> list = Entries;
        if (index < 0 || index >= list.Count || list[index].StateManager is not { IsSaved: true } sm) {
            return "";
        }

        return string.IsNullOrWhiteSpace(sm.SlotDescription) ? sm.SlotName : sm.SlotDescription;
    }
}
