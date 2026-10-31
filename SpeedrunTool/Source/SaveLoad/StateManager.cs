using Celeste.Mod.SpeedrunTool.ModInterop;
using Celeste.Mod.SpeedrunTool.Utils;
using Force.DeepCloner;
using Force.DeepCloner.Helpers;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using EventInstance = FMOD.Studio.EventInstance;

namespace Celeste.Mod.SpeedrunTool.SaveLoad;

public sealed class StateManager {
    public static StateManager Instance => SaveSlotsManager.StateManagerInstance;
    internal StateManager() { }

    internal static bool AllowSaveLoadWhenWaiting = false;

    public bool IsSaved => savedLevel != null;
    public State State { get; private set; } = State.None;
    public bool SavedByTas { get; private set; }
    public bool LoadByTas { get; private set; }
    public bool ClearBeforeSave { get; private set; }
    public Level SavedLevel => savedLevel;
    private Level savedLevel;
    private SaveData savedSaveData;
    internal Task<DeepCloneState> preCloneTask;
    private FreezeType freezeType;
    private Process celesteProcess;
    private int savedTasCycleGroupCounter;
    private WeakReference<IEnumerator> transitionRoutine;
    private IEnumerator savedTransitionRoutine;

    public string SlotName;
    public string SlotDescription = "";
    public string FullSlotDescription => string.IsNullOrWhiteSpace(SlotDescription) ? $"[{SlotName}]" : $"[{SlotName}],  {SlotDescription}";
    private static string GenerateSlotDescription() {
        string levelInfo = Engine.Scene.GetSession() is { } session ? $"'{session.Area.SID} [{session.Level}]'" : "";
        string frames = Engine.Scene is not null ? "(frame: " + (int)Math.Round(Engine.Scene.RawTimeActive / 0.0166667) + ")" : "";
        return levelInfo + frames;
    }
    private enum FreezeType {
        None,
        Save,
        Load
    }

    private readonly HashSet<EventInstance> playingEventInstances = [];


    #region Hook

    // manually call this to ensure it's first called
    internal static void Load() {
        On.Celeste.PlayerDeadBody.End += AutoLoadStateWhenDeath;
        IL.Celeste.Level.TransitionRoutine += IL_LevelTransitionRoutine;
        On.Celeste.Level.TransitionRoutine += On_LevelTransitionRoutine;
        On.Celeste.Level.End += LevelOnEnd;
    }

    internal static void Unload() {
        On.Celeste.PlayerDeadBody.End -= AutoLoadStateWhenDeath;
        IL.Celeste.Level.TransitionRoutine -= IL_LevelTransitionRoutine;
        On.Celeste.Level.TransitionRoutine -= On_LevelTransitionRoutine;
        On.Celeste.Level.End -= LevelOnEnd;
    }

    private static void IL_LevelTransitionRoutine(ILContext context) {
        ILCursor cursor = new(context);
        if (cursor.TryGotoNext(MoveType.After, ins => ins.OpCode == OpCodes.Newobj && ins.Operand.ToString().Contains("Level/<TransitionRoutine>"))) {
            cursor.EmitDelegate(SaveTransitionRoutine);
        }
    }

    private static IEnumerator SaveTransitionRoutine(IEnumerator enumerator) {
        Instance.transitionRoutine = new WeakReference<IEnumerator>(enumerator);
        return enumerator;
    }

    private static IEnumerator On_LevelTransitionRoutine(On.Celeste.Level.orig_TransitionRoutine orig, Level self, LevelData next, Vector2 direction) {
        IEnumerator enumerator = orig(self, next, direction);
        while (enumerator.MoveNext()) {
            yield return enumerator.Current;
        }

        Instance.transitionRoutine = null;
    }

    private static void LevelOnEnd(On.Celeste.Level.orig_End orig, Level self) {
        orig(self);
        Instance.transitionRoutine = null;
        DesyncRiskAnalyzer.CheckAll = true;
    }



    private static readonly Lazy<PropertyInfo> InGameOverworldHelperIsOpen = new(
        () => ModUtils.GetType("CollabUtils2", "Celeste.Mod.CollabUtils2.UI.InGameOverworldHelper")?.GetPropertyInfo("IsOpen")
    );

    internal void HandleSwitchScene(Scene self) {
        if (!IsSaved) {
            return;
        }

        // v3.28 新功能: 切换 Scene 时不再自动删除 SaveState.
        // 由于我们还保存了 Celeste.SaveData.Instance, 因此我们的多存档甚至可以存储 不同的原版存档文件上的不同章节
        // todo: 目前这个功能还有问题, 比如资源有时候好像没有正确加载

        if (ModSettings.AutoClearStateOnSceneSwitch) {
            if (self is Overworld && !SavedByTas && InGameOverworldHelperIsOpen.Value?.GetValue(null) as bool? != true
                || self.GetSession() is { } session && session.Area != savedLevel.Session.Area) {
                ClearStateImpl(hasGc: true);
            }
        }

        // 重启章节 Level 实例变更，所以之前预克隆的实体作废，需要重新克隆
        // 因为 preClone 期间若字段为 Level, 并且 Engine.Scene 也确实是 level, 则会直接把字段设为 level
        if (self is Level) {
            State = State.None;
            PreCloneSavedEntities();
        }
    }

    private static void AutoLoadStateWhenDeath(On.Celeste.PlayerDeadBody.orig_End orig, PlayerDeadBody self) {
        if (ModSettings.Enabled
            && ModSettings.AutoLoadStateAfterDeath
            && Instance.IsSaved
            && !TasUtils.Running
            && !Instance.SavedByTas
            && !self.finished
            && Engine.Scene is Level level
            && level.Entities.FindFirst<PlayerSeeker>() == null
           ) {
            level.OnEndOfFrame += () => {
                if (Instance.IsSaved) {
                    Instance.LoadStateImpl(false, out _);
                }
                else {
                    level.DoScreenWipe(wipeIn: false, self.DeathAction ?? level.Reload);
                }
            };
            self.RemoveSelf();
        }
        else {
            orig(self);
        }
    }

    #endregion Hook
    internal bool SaveStateImpl(bool tas, out string popup) {
        if (Engine.Scene is not Level level) {
            popup = "Cannot Save: Scene is not Level!";
            return false;
        }

        if (!IsAllowSave(level, tas, out string reason)) {
            popup = reason;
            return false;
        }

        // 不允许在春游图打开章节面板时存档
        if (InGameOverworldHelperIsOpen.Value?.GetValue(null) as bool? == true) {
            popup = "Cannot Save while ChapterPanel is Open!";
            return false;
        }

        bool shouldDesyncCheck = DesyncRiskAnalyzer.GetAndRefreshIfDesyncCheck();
        if (shouldDesyncCheck && DesyncRiskAnalyzer.OnBeforeSaveState(level, out string reason2)) {
            popup = reason2;
            return false;
        }

        SaveLoadProfiler.Begin();

        if (IsSaved) {
            ClearBeforeSave = true;
            ClearStateImpl(hasGc: false);
            ClearBeforeSave = false;
        }
        SaveLoadProfiler.Mark("clearOld");
#if DEBUG
        DebugTool.MemoryTracker.CheckFragmentation("Save Start");
        DebugTool.MemoryTracker.GcSample before = DebugTool.MemoryTracker.TakeGcSample();
        Stopwatch sw = Stopwatch.StartNew();
#endif

        SaveLoadAction.InitSlots();
        SaveLoadProfiler.Mark("initSlots");
        State = State.Saving;
        SavedByTas = tas;
        SaveLoadAction.OnBeforeSaveState(level);
        SaveLoadProfiler.Mark("beforeSave");
        DeepClonerUtils.ResetCloneStats();
        level.DeepCloneToShared(savedLevel = (Level)RuntimeHelpers.GetUninitializedObject(typeof(Level)));
        SaveLoadProfiler.Mark("cloneLevel");
        DeepClonerUtils.DumpCloneStats("level");
        savedSaveData = SaveData.Instance.DeepCloneShared();
        savedTasCycleGroupCounter = TasUtils.GroupCounter;
        savedTransitionRoutine = transitionRoutine?.TryGetTarget(out IEnumerator enumerator) == true ? enumerator.DeepCloneShared() : null;
        SaveLoadProfiler.Mark("cloneExtra");
        SaveLoadAction.OnSaveState(level);
        SaveLoadProfiler.Mark("saveActions");
        DeepClonerUtils.ClearSharedDeepCloneState();

        if (shouldDesyncCheck && DesyncRiskAnalyzer.OnAfterSaveState(out string reason3)) {
            popup = reason3;
            ClearStateImpl(hasGc: false);
            return false;
        }

        PreCloneSavedEntities();
        SaveLoadProfiler.Mark("preCloneSpawn");

        if (tas) {
            State = State.None;
        }
        else {
            FreezeGame(FreezeType.Save);
            if (TasUtils.HideGamePlay) {
                DoScreenWipe(level);
            }
            else {
                level.Add(new WaitingEntity());
            }
        }

        popup = $"Save to [{SlotName}]";
        SlotDescription = GenerateSlotDescription();
        Logger.Info("SpeedrunTool/SaveState", $"Save to {FullSlotDescription}");

#if DEBUG
        if (InGame_Profiling) {
            Logger.Debug("SpeedrunTool", $"{"Save",20}: {sw.ElapsedMilliseconds,7} ms");
            Logger.Debug("SpeedrunTool", $"{"Allocated",20}: {DebugTool.MemoryTracker.FormatDiff(before, DebugTool.MemoryTracker.TakeGcSample(), simplified: false)}");
            Logger.Debug("SpeedrunTool", $"{"ProcessMemoryUsage",20}: {Process.GetCurrentProcess().PrivateMemorySize64 / 1024.0 / 1024.0 / 1024.0,7:F2} GB");
        }
        DebugTool.MemoryTracker.CheckFragmentation("Save End");
        if (Log_WhenSaving) {
            SaveLoadAction.LogSavedValues(level: savedLevel);
        }
#endif

        SaveLoadProfiler.End("Save");

        return true;
    }


    internal bool LoadStateImpl(bool tas, out string popup) {
        popup = "";
        if (Engine.Scene is not Level level) {
            popup = "Cannot Load: Scene is not Level!";
            return false;
        }

        if (!IsAllowLoad(level, tas, out string reason)) {
            popup = reason;
            return false;
        }

        if (tas && !SavedByTas) {
            popup = "Cannot Load a state saved by TAS!";
            return false;
        }

#if DEBUG
        if (Log_WhenLoading) {
            SaveLoadAction.LogSavedValues(level: savedLevel);
        }
        DebugTool.MemoryTracker.CheckFragmentation("Load Start");
        DebugTool.MemoryTracker.GcSample before = DebugTool.MemoryTracker.TakeGcSample();
        Stopwatch sw = Stopwatch.StartNew();
#endif

        LoadByTas = tas;
        State = State.Loading;

        SaveLoadProfiler.Begin();

        SaveLoadAction.OnBeforeLoadState(level);
        SaveLoadProfiler.Mark("beforeLoad");

        // 性能优化: 不再 `preCloneTask?.Result` 阻塞等后台预克隆。
        // 实测有一次读档在 attachPreClone 上卡了 2323ms —— 玩家存完档立刻读档时,
        // 后台那份克隆还没跑完, 主线程就干等它。现在只在"已经跑完"时才复用,
        // 否则直接现克隆 (普通房间 ~30ms), 绝不会白等。
        if (preCloneTask is { IsCompletedSuccessfully: true } readyTask) {
            DeepClonerUtils.SetSharedDeepCloneState(readyTask.Result);
        }
        else {
            preCloneTask = null;
            DeepClonerUtils.ClearSharedDeepCloneState();
        }

        SaveLoadProfiler.Mark("attachPreClone");

        UpdateTimeAndDeaths(level);
        UnloadLevel(level);
        SaveLoadProfiler.Mark("unload");

        Tracker.Refresh(savedLevel); // 不能在 OnLoadState 里做. 否则多次读档会需要反复 Refresh Tracker
        savedLevel.DeepCloneToShared(level);
        SaveData.Instance = savedSaveData.DeepCloneShared();
        if (savedTransitionRoutine != null) {
            transitionRoutine = new WeakReference<IEnumerator>(savedTransitionRoutine.DeepCloneShared());
        }
        SaveLoadProfiler.Mark("writeback");

        RestoreAudio_1(level);
        RestoreCassetteBlockManager1(level);
        SaveLoadAction.OnLoadState(level);
        SaveLoadProfiler.Mark("loadActions");

        PreCloneSavedEntities();
        SaveLoadProfiler.Mark("preCloneSpawn");

        if (tas) {
            LoadStateComplete(level);
        }
        else {
            // restore cycle hitbox color
            RestoreLevelTime(level);
            FreezeGame(FreezeType.Load);
            DoScreenWipe(level);
        }

        if (!tas && ModSettings.GcAfterLoadState) {
            GcCollect(force: false);
        }

        popup = $"Load from [{SlotName}]";
        Logger.Info("SpeedrunTool/LoadState", $"Load from {FullSlotDescription}");

#if DEBUG
        if (InGame_Profiling) {
            Logger.Debug("SpeedrunTool", $"{"Load",20}: {sw.ElapsedMilliseconds,7} ms");
            Logger.Debug("SpeedrunTool", $"{"Allocated",20}: {DebugTool.MemoryTracker.FormatDiff(before, DebugTool.MemoryTracker.TakeGcSample(), simplified: false)}");
            Logger.Debug("SpeedrunTool", $"{"ProcessMemoryUsage",20}: {Process.GetCurrentProcess().PrivateMemorySize64 / 1024.0 / 1024.0 / 1024.0,7:F2} GB");
        }
        DebugTool.MemoryTracker.CheckFragmentation("Load End");
#endif

        SaveLoadProfiler.End("Load");

        return true;
    }

    internal bool ClearStateImpl(bool hasGc = true) {
        preCloneTask?.Wait();

        // fix: 读档冻结时被TAS清除状态后无法解除冻结
        if (State == State.Waiting && Engine.Scene is Level level) {
            OutOfFreeze(level);
        }

        playingEventInstances.Clear();
        bool doSomething = savedLevel is not null;
        if (!doSomething) {
            hasGc = false;
        }
        savedLevel = null;
        savedSaveData = null;
        preCloneTask = null;
        savedTransitionRoutine = null;
        celesteProcess?.Dispose();
        celesteProcess = null;
        SaveLoadAction.OnClearState(ClearBeforeSave);
        State = State.None;
        MoreSaveSlotsUI.Snapshot.RemoveSnapshot(SlotName);
        // 2025.10.08 fix: clear 之后读档更加卡顿 (这个问题在老版本好像也有, 之前在这里压根不 Gc)
        // 2025.10.19: 不过似乎不是每个人都喜欢卡顿一下, 姑且先做成可选项, 使得用户可以保留之前的体验
        if (hasGc && ModSettings.GcAfterClearState) {
            GcCollect(force: true);
        }
        if (doSomething) {
            Logger.Info("SpeedrunTool/ClearState", $"Clear {FullSlotDescription}");
        }
        SlotDescription = "";
        return doSomething;
    }

    internal static float MemoryThreshold = 2.5f; // GB

    internal void GcCollect(bool force = false) {
        // 阈值判断用"托管堆"而不是"进程私有内存"。
        // 实测: 进程私有内存 ~5.2~5.8GB (含 GPU/原生分配), 而托管堆只有 ~1.1~1.2GB,
        // 拿私有内存去比 2.5GB 阈值 → 永远超阈值 → 永远走阻塞回收, 后台回收分支成了死代码。
        double heapGb = GC.GetTotalMemory(false) / (1024.0 * 1024 * 1024);

        if (celesteProcess == null) {
            celesteProcess = Process.GetCurrentProcess();
        }
        else {
            celesteProcess.Refresh();
        }

        double privateGb = celesteProcess.PrivateMemorySize64 / (1024.0 * 1024 * 1024);
        bool overThreshold = heapGb > MemoryThreshold;

        if (!force && !overThreshold) {
            return;
        }

        // 性能优化 2: 堆没超阈值时, "强制"回收改成后台非阻塞 (blocking:false)。
        // 实测阻塞式全量 GC 在 1.1~1.2GB 托管堆上要 361~801ms, 而每次清档都会触发
        // (AutoClearStateOnScreenTransition 打开时每个过屏都清), 主线程白卡半秒以上。
        if (!overThreshold && ModSettings.GcBackgroundCollect) {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false, compacting: false);
            Logger.Info("SpeedrunTool", $"GC requested in background (heap={heapGb:0.00}GB, private={privateGb:0.00}GB).");
            return;
        }

        Logger.Info("SpeedrunTool", $"Blocking GC (heap={heapGb:0.00}GB{(overThreshold ? $" > {MemoryThreshold:0.00}GB" : ", background off")}, private={privateGb:0.00}GB).");
        GcCollectCore(ModSettings.GcCompactLoh);
    }

    private static void GcCollectCore(bool compact) {
        // 以现在卡顿一些为代价, 保证之后游戏流程中尽量不卡顿 (后者更致命)
        // 作为推论, 我们不应该放在其他线程执行此事
        //
        // 性能优化 (原实现固定 "CompactOnce + GC.Collect() + WaitForPendingFinalizers"):
        //  - LOH 压缩需要搬移大对象, 在 2.5GB 堆上实测占掉 0.7~2.1s 停顿的大头; 而 SRT 的快照是
        //    "大块短命对象", 回收它们不需要压缩. 现在默认只 sweep (compacting:false),
        //    需要恢复旧行为时打开 ModSettings.GcCompactLoh 或控制台 `srt_gc on`.
        //  - 去掉 WaitForPendingFinalizers: SRT 已手动 ReleaseSnapshot / Dispose, 再等一轮终结器
        //    只会把停顿拉长一个 finalizer 周期.
        //  - 用 GC.Collect(generation, mode, blocking, compacting) 明确表达意图, 避免 GCSettings
        //    的全局副作用残留到后续 GC.
#if DEBUG
        DebugTool.MemoryTracker.CheckFragmentation("GC Start");
#endif
        Stopwatch sw = Stopwatch.StartNew();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: compact);
        Logger.Info("SpeedrunTool", $"GC latency: {sw.ElapsedMilliseconds}ms (compact={compact}).");
#if DEBUG
        DebugTool.MemoryTracker.CheckFragmentation("GC End");
#endif
    }

    // 触发 Entity.Removed, 从而释放资源，停止正在播放的声音, 解除 hook 等
    // 零分配改写: 原实现每次读档都要 [.. GetEntities<Player>(), .. level.Entities] 建两个 List
    // 再 .Distinct() 建一个 HashSet + 迭代器。现在复用一块静态缓冲, 并用"先 Player 后其它"两趟遍历
    // 代替去重 (Player 一定在 level.Entities 里, 所以不需要 HashSet)。
    [ThreadStatic] private static List<Entity> unloadBuffer;
    [ThreadStatic] private static List<Component> unloadSoundBuffer;

    private static void UnloadLevel(Level level) {
        List<Entity> buffer = unloadBuffer ??= new List<Entity>(512);
        buffer.Clear();
        buffer.AddRange(level.Entities); // 快照: Removed() 里的 RemoveSelf 会改 level.Entities, 不能直接枚举

        // Player 必须最早移除，不然在处理 player.triggersInside 字段时会产生空指针异常
        for (int i = 0; i < buffer.Count; i++) {
            if (buffer[i] is Player) {
                RemoveOneEntity(buffer[i], level);
            }
        }

        for (int i = 0; i < buffer.Count; i++) {
            if (buffer[i] is not Player) {
                RemoveOneEntity(buffer[i], level);
            }
        }

        // 移除剩下声音组件
        // 零分配改写: GetComponentsCopy<T>() 每次都会新建 List; RemoveSelf 会改 Tracker 的内部列表,
        // 所以这里用一个复用的静态缓冲做快照。
        List<Component> soundBuffer = unloadSoundBuffer ??= new List<Component>(64);
        soundBuffer.Clear();
        if (level.Tracker.Components.TryGetValue(typeof(SoundSource), out List<Component> soundSources)) {
            soundBuffer.AddRange(soundSources);
        }

        foreach (Component component in soundBuffer) {
            component.RemoveSelf();
        }

        soundBuffer.Clear();
    }

    private static void RemoveOneEntity(Entity entity, Level level) {
        // TODO: 确定到底哪些 Global entity 可以移除
        // 这里知道的是 MirrorSurfaces 被移除会导致一些 VirtualRenderTarget Dispose
        // 于是如果在存档 1 存档, 在存档 2 清除, 再读档 1, 会崩溃
        if (entity.TagCheck(Tags.Global) && !GlobalEntitiesToRemove.Contains(entity.GetType())) {
            return;
        }

        try {
            entity.Removed(level); // 触发各式各样的副作用
            // Scene.TagLists / Scene.Tracker / Engine.Pooler 本身都将被克隆出来, 大部分情况下无需处理. 同理也不用 UpdateLists
            // 但是小部分情况下, Removed 副作用会依赖于 Tracker 等的内部状态 (例如实体带 hook, 只有移除到最后一个才解除 hook)
            // 所以我们还是做整套流程
            level.TagLists.EntityRemoved(entity);
            level.Tracker.EntityRemoved(entity);
            Engine.Pooler.EntityRemoved(entity);
        }
        catch (NullReferenceException) {
            // ignore https://discord.com/channels/403698615446536203/954507384183738438/954507384183738438
        }
    }

    private static readonly HashSet<Type> GlobalEntitiesToRemove = [typeof(CassetteBlockManager)];

    [Initialize]
    private static void InitGlobalEntitiesToRemove() {
        if (ModUtils.GetType("SpirialisHelper", "Celeste.Mod.Spirialis.TimeController") is { } timeControllerType) {
            GlobalEntitiesToRemove.Add(timeControllerType);
        }
    }

    private void UpdateTimeAndDeaths(Level level) {
        if (SavedByTas || ModSettings.SaveTimeAndDeaths) {
            return;
        }

        Session session = level.Session;
        Session savedSession = savedLevel.Session;
        AreaKey areaKey = session.Area;

        // 零分配改写: 原实现把 Session 和 SaveData 各深克隆一份, 只是为了在覆写前"读旧值"
        // (而 SaveData 的深克隆包含全部章节的 Areas_Safe, 每次读档都是白白一大坨分配)。
        // 这里先把旧值取到局部变量, 再原地写回, 语义完全等价。
        // 另外原实现里 clonedSaveData.Time / TimePlayed 赋值后从未写回, 属死代码, 一并删除。
        long oldTime = savedSession.Time;
        int oldDeaths = savedSession.Deaths;
        int oldDeathsInCurrentLevel = savedSession.DeathsInCurrentLevel;

        savedSession.Time = Math.Max(session.Time, oldTime);

        // 修复：切屏时存档，若干秒后读档游戏会误以为卡死自动重生

        // 2025.08.25: 这里我们不能用 level.transition, 因为有可能 TransitionRoutine 已经被 hook 过
        // 使得这个 coroutine 的类型不一样, 这里写的反射获取 <playerStuck> 就不对了
        // 不过理论上多写一些代码去解开 Coroutine 的嵌套应该也行...
        // 这个 bug 被 DJ 在 2023.10.07 修复了, 不过我把多存档合并进来的时候可能不小心丢失了这个提交
        // 总之我们采用 DJ 的写法
        if (savedTransitionRoutine?.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .FirstOrDefault(info => info.Name.StartsWith("<playerStuck>")) is { } playerStuck) {
            playerStuck.SetValue(savedTransitionRoutine, TimeSpan.FromTicks(session.Time));
            playerStuck.SetValue(savedTransitionRoutine.DeepCloneShared(), TimeSpan.FromTicks(session.Time));
        }

        int increaseDeath = level.IsPlayerDead() ? 0 : 1;
        savedSession.Deaths = Math.Max(session.Deaths + increaseDeath, oldDeaths);
        savedSession.DeathsInCurrentLevel = Math.Max(session.DeathsInCurrentLevel + increaseDeath, oldDeathsInCurrentLevel);
        savedSaveData.TotalDeaths = SaveData.Instance.TotalDeaths + increaseDeath;
        savedSaveData.Areas_Safe[areaKey.ID].Modes[(int)areaKey.Mode].Deaths =
            SaveData.Instance.Areas_Safe[areaKey.ID].Modes[(int)areaKey.Mode].Deaths + increaseDeath;
    }

    private void LoadStateComplete(Level level) {
        RestoreLevelTime(level);
        RestoreAudio_2();
        RestoreCassetteBlockManager2(level);
        DeepClonerUtils.ClearSharedDeepCloneState();
        State = State.None;
    }

    private void RestoreLevelTime(Level level) {
        level.TimeActive = savedLevel.TimeActive;
        level.RawTimeActive = savedLevel.RawTimeActive;
    }

    private void RestoreCycleGroupCounter() {
        TasUtils.GroupCounter = savedTasCycleGroupCounter;
    }

    // 收集需要继续播放的声音
    private void RestoreAudio_1(Level level) {
        playingEventInstances.Clear();

        // 零分配改写: 原实现 level.Entities.SelectMany(entity => entity.Components.ToArray())
        // 会给每个实体分配一个数组 + 一层 LINQ 迭代器; 这里直接双层 foreach
        // (level.Entities.entities 是 List<Entity>, 用结构体枚举器, 只读遍历不分配)。
        foreach (Entity entity in level.Entities.entities) {
            foreach (Component component in entity.Components) {
                if (component is SoundSource { Playing: true, instance: { } eventInstance }) {
                    playingEventInstances.Add(eventInstance);
                }
            }
        }
    }

    // 等 ScreenWipe 完毕再开始播放
    private void RestoreAudio_2() {
        foreach (EventInstance instance in playingEventInstances) {
            instance.start();
        }

        playingEventInstances.Clear();
    }

    // 分两步的原因是更早的停止音乐，听起来更舒服更好一点
    // 第一步：停止播放主音乐
    private static void RestoreCassetteBlockManager1(Level level) {
        if (level.Tracker.GetEntity<CassetteBlockManager>() is { } manager) {
            manager.snapshot?.start();
        }
    }

    // 第二步：播放节奏音乐
    // https://discord.com/channels/403698615446536203/429775260108324865/1422017531098566776
    // 理论上, 在 Cassette cycle 图中 LoadState 后暂停会引入额外的读图优势, 但这显然不是我们的问题.
    private static void RestoreCassetteBlockManager2(Level level) {
        if (level.Tracker.GetEntity<CassetteBlockManager>() is { } manager) {
            if (manager.sfx is { } sfx && !manager.isLevelMusic && manager.leadBeats <= 0) {
                sfx.start();
            }
        }
    }

    private void PreCloneSavedEntities() {
        // 性能优化 3: 预克隆的收益是"读档时少一次全图克隆"(实测 writeback 3ms vs 无预克隆时 ~30ms),
        // 代价是每次存档后都要在后台再克隆一整份(内存翻倍 + GC 压力), 而且下一次存档/切槽时
        // ClearStateImpl 里的 preCloneTask.Wait() 会被它阻塞(实测 305~413ms 的 clearOld 尖峰)。
        // 关掉后: 读档 +约 30ms, 但内存与 GC 压力减半。默认保持原行为, 可用设置/控制台切换。
        if (!ModSettings.PreCloneEntities) {
            preCloneTask = null;
            return;
        }

        if (IsSaved) {
            SaveLoadAction.OnPreCloneEntities();
            preCloneTask = Task.Run(() => {
                DeepCloneState deepCloneState = new();
                savedLevel.Entities.DeepClone(deepCloneState);
                savedLevel.RendererList.DeepClone(deepCloneState);
                savedSaveData.DeepClone(deepCloneState);
                return deepCloneState;
            });
        }
    }

    private bool IsAllowSave(Level level, bool tas, out string failReason) {
        // 正常游玩时禁止死亡或者跳过过场时存档，TAS 则无以上限制
        // 跳过过场时的黑屏与读档后加的黑屏冲突，会导致一直卡在跳过过场的过程中
        failReason = "";
        if (State == State.Saving || State == State.Loading) {
            failReason = "Failed to Save: SpeedrunTool is Busy!";
            return false;
        }
        if (State == State.Waiting && !AllowSaveLoadWhenWaiting) {
            failReason = $"Just Saved/Loaded!";
            return false;
        }
        if (tas) {
            return true;
        }
        if (level.Paused) {
            failReason = "Cannot Save while Level is Paused!";
            return false;
        }
        if (!ModSettings.AllowStateDuringDeath && level.IsPlayerDead()) {
            failReason = "Cannot Save while Player is Dead!";
            return false;
        }
        if (level.SkippingCutscene) {
            failReason = "Cannot Save while Skipping Cutscene!";
            return false;
        }
        return true;
    }

    private bool IsAllowLoad(Level level, bool tas, out string failReason) {
        failReason = "";
        if (!IsSaved) {
            failReason = DialogIds.NotSavedStateTooltip.DialogClean() + $" [{SlotName}]";
            return false;
        }
        if (State == State.Loading || State == State.Saving) {
            failReason = "Failed to Load: SpeedrunTool is Busy!";
            return false;
        }
        if (State == State.Waiting && !AllowSaveLoadWhenWaiting) {
            failReason = "Just Saved/Loaded!";
            return false;
        }
        if (!tas && level.Paused) {
            failReason = "Cannot Save while Level is Paused!";
        }
        return true;
    }

    private void FreezeGame(FreezeType freeze) {
        freezeType = freeze;
    }

    private void DoScreenWipe(Level level) {
        level.DoScreenWipe(true, () => {
            if (ModSettings.FreezeAfterLoadStateType != FreezeAfterLoadStateType.Off) {
                State = State.Waiting;
            }
            else {
                OutOfFreeze(level);
            }
        });
    }

    internal void OutOfFreeze(Level level) {
        if (freezeType == FreezeType.Save || savedLevel == null) {
            if (savedLevel != null) {
                RestoreLevelTime(level);
            }

            State = State.None;
        }
        else {
            LoadStateComplete(level);
        }

        // when freeze by SRT, the CycleGroupCounter still updates, so we restore it when out of freeze
        RestoreCycleGroupCounter();

        freezeType = FreezeType.None;
    }

    private class WaitingEntity : Entity {
        private bool waitOneFrame = true;

        public override void Render() {
            if (waitOneFrame) {
                waitOneFrame = false;
                return;
            }

            Level level = SceneAs<Level>();
            Instance.DoScreenWipe(level);
            RemoveSelf();
        }
    }
}

public enum State {
    None,
    Saving,
    Loading,
    Waiting,
}
