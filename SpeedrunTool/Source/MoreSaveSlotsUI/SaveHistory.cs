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
    internal static bool Enabled => Count > 0;

    private static readonly SaveSlot temp = new("@history/tmp");

    /// <summary>
    /// 存档前调用：把"即将被覆盖"的内容整体换到临时槽，当前槽变空（IsSaved=false）。
    /// 这样 SaveStateImpl 不会再走 ClearStateImpl，历史环也不会被提前改动；
    /// 存档失败时可以原样换回来。
    /// </summary>
    internal static SaveSlot? Begin(SaveSlot current) {
        Ensure();
        if (entries.Count == 0 || current.StateManager is not { IsSaved: true }) {
            return null;
        }

        Swap(temp, current);
        return temp;
    }

    /// <summary>存档成功后调用：旋转历史环，把临时槽里的旧内容放进 entries[0]。</summary>
    internal static void Commit(SaveSlot? holder) {
        if (holder is null || entries.Count == 0) {
            return;
        }

        for (int i = entries.Count - 1; i > 0; i--) {
            Swap(entries[i], entries[i - 1]);
        }

        Swap(entries[0], holder);

        // 被挤掉的那条现在在 holder 里，释放它的缩略图
        holder.Preview?.Tex?.Dispose();
        holder.Preview = null;
    }

    /// <summary>存档失败时调用：把内容换回当前槽。</summary>
    internal static void Rollback(SaveSlot? holder) {
        if (holder is not null) {
            Swap(holder, SaveSlotsManager.Slot);
        }
    }

    private static void Swap(SaveSlot a, SaveSlot b) {
        (a.StateManager, b.StateManager) = (b.StateManager, a.StateManager);
        (a.AllSavedValues, b.AllSavedValues) = (b.AllSavedValues, a.AllSavedValues);
        (a.ValueDictionaryInitialized, b.ValueDictionaryInitialized) = (b.ValueDictionaryInitialized, a.ValueDictionaryInitialized);
        (a.ClonedEventInstancesWhenSave, b.ClonedEventInstancesWhenSave) = (b.ClonedEventInstancesWhenSave, a.ClonedEventInstancesWhenSave);
        (a.ClonedEventInstancesWhenPreClone, b.ClonedEventInstancesWhenPreClone) =
            (b.ClonedEventInstancesWhenPreClone, a.ClonedEventInstancesWhenPreClone);
        (a.Preview, b.Preview) = (b.Preview, a.Preview);

        // StateManager 里的 SlotName 决定弹窗文案，跟着内容一起改
        if (a.StateManager is { } sa) {
            sa.SlotName = a.Name;
        }

        if (b.StateManager is { } sb) {
            sb.SlotName = b.Name;
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
