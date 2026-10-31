using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace Celeste.Mod.SpeedrunTool.SaveLoad;

/// <summary>
/// Save/Load 分段计时 + 分段分配统计。
/// <para>默认关闭：关闭时每次调用只多一次 bool 判断，没有 Stopwatch、没有分配。</para>
/// <para>用控制台命令 <c>srt_profile on</c> 打开后，每次存档/读档会往日志打一行分段数据：</para>
/// <para><c>Save: clearOld=0.35ms/12KB initSlots=0.02ms/0KB ... | total=41.5ms | alloc=2.3MB | heap=1830.2MB</c></para>
/// <para>分配量用 <see cref="GC.GetAllocatedBytesForCurrentThread"/> 取差值，只统计主线程
/// (后台预克隆跑在别的线程，不计入)。</para>
/// </summary>
internal static class SaveLoadProfiler {
    public static bool Enabled;

    private static readonly Stopwatch Watch = new();
    private static readonly List<(string Name, long Ticks, long Alloc)> Marks = [];

    public static void Begin() {
        if (!Enabled) {
            return;
        }

        Marks.Clear();
        Watch.Restart();
        // 起点：作为第一次 Mark 的基准
        Marks.Add(("", 0, GC.GetAllocatedBytesForCurrentThread()));
    }

    public static void Mark(string name) {
        if (!Enabled) {
            return;
        }

        Marks.Add((name, Watch.ElapsedTicks, GC.GetAllocatedBytesForCurrentThread()));
    }

    public static void End(string title) {
        if (!Enabled) {
            return;
        }

        long totalTicks = Watch.ElapsedTicks;
        long totalAlloc = GC.GetAllocatedBytesForCurrentThread() - Marks[0].Alloc;

        StringBuilder sb = new();
        long prevTicks = 0;
        long prevAlloc = Marks[0].Alloc;

        for (int i = 1; i < Marks.Count; i++) {
            (string name, long ticks, long alloc) = Marks[i];
            sb.Append($"{name}={ToMs(ticks - prevTicks):0.00}ms/{ToKb(alloc - prevAlloc):0}KB ");
            prevTicks = ticks;
            prevAlloc = alloc;
        }

        sb.Append($"rest={ToMs(totalTicks - prevTicks):0.00}ms ");
        sb.Append($"| total={ToMs(totalTicks):0.0}ms");
        sb.Append($" | alloc={totalAlloc / 1024.0 / 1024.0:0.0}MB");
        sb.Append($" | heap={GC.GetTotalMemory(false) / 1024.0 / 1024.0:0.0}MB");

        Logger.Info("SpeedrunTool/Profile", $"{title}: {sb}");
        Marks.Clear();
    }

    private static double ToMs(long ticks) {
        return ticks * 1000.0 / Stopwatch.Frequency;
    }

    private static double ToKb(long bytes) {
        return bytes / 1024.0;
    }
}
