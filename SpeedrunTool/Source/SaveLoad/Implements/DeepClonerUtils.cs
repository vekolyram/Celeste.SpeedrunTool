using Celeste.Mod.SpeedrunTool.ModInterop;
using FMOD.Studio;
using Force.DeepCloner;
using Force.DeepCloner.Helpers;
using Microsoft.Xna.Framework.Graphics;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;
using NLua;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Celeste.Mod.SpeedrunTool.SaveLoad;

public static class DeepClonerUtils {
    [ThreadStatic] private static Stack<Component> backupComponents;
    [ThreadStatic] private static Stack<object> backupHashSet;
    [ThreadStatic] private static Dictionary<object, object> backupDict;

    // 只在 profiler 打开时统计: 每种类型被克隆了多少次, 用来找"快照里到底是谁占体积"
    [ThreadStatic] private static Dictionary<Type, int> cloneStats;

    internal static void ResetCloneStats() {
        if (!SaveLoadProfiler.Enabled) {
            return;
        }

        cloneStats ??= [];
        cloneStats.Clear();
    }

    internal static void DumpCloneStats(string title) {
        if (!SaveLoadProfiler.Enabled || cloneStats is null || cloneStats.Count == 0) {
            return;
        }

        long total = 0;
        foreach (KeyValuePair<Type, int> pair in cloneStats) {
            total += pair.Value;
        }

        System.Text.StringBuilder sb = new();
        int shown = 0;
        foreach (KeyValuePair<Type, int> pair in cloneStats.OrderByDescending(pair => pair.Value)) {
            if (shown++ >= 14) {
                break;
            }

            sb.Append($"{pair.Key.Name}x{pair.Value} ");
        }

        Logger.Info("SpeedrunTool/Profile", $"  cloneTypes({title}): total={total} | {sb}");
    }

    // 共用 DeepCloneState 可使多次 DeepClone 复用相同对象避免多次克隆同一对象
    private static DeepCloneState sharedDeepCloneState = new();

    // Clone 开始时，判断哪些类型是直接使用原对象而不 DeepClone 的
    // Before cloning, determine which types use the original object directly
    private static bool? KnownTypeProcessor(Type type) {
        if (
            // Celeste Singleton
            type == typeof(Celeste)
            || type == typeof(Settings)

            // Everest
            || type.IsSubclassOf(typeof(ModAsset))
            || type.IsSubclassOf(typeof(EverestModule))
            || type.IsSubclassOf(typeof(EverestModuleSettings))
            || type == typeof(EverestModuleMetadata)

            // Monocle
            || type == typeof(GraphicsDevice)
            || type == typeof(GraphicsDeviceManager)
            || type == typeof(Monocle.Commands)
            || type == typeof(BitTag)
            || type == typeof(Atlas)

            // 性能优化: MTexture 本质是不可变的 (图集坐标 + 名字), 但它是普通 class, 会被逐个深克隆。
            // 而每个 Sprite 的每个动画帧都是一个 MTexture → 装饰密集的房间 (SJ 大厅 2034 个实体)
            // 快照里可能有上百万个 MTexture 克隆, 实测占掉 166~451MB 的绝大部分。
            // 共享原对象后快照体积大幅下降。要退回旧行为: srt_tex off
            || (ModSettings.ShareMTexture && type == typeof(MTexture))

            // 应该是可以直接使用原对象的. 但还有待测试
            // TileGrid.Tiles
            || type == typeof(VirtualMap<MTexture>)
            // Level.SolidData / BgData
            || type == typeof(VirtualMap<char>)

            // 性能优化(最大的一条): 房间的"原始定义数据" —— 4.5 万个 EntityData、4.3 万个 DecalData、
            // 39 万条 Dictionary.Entry …… 实测一个 SJ 大厅快照 106 万个克隆对象里, 实体本身只占 ~1%,
            // 剩下几乎全是这份静态地图定义。
            //
            // 分两档, 默认只开第一档 (更安全):
            //  - ShareLevelData    : 共享 EntityData / DecalData 这些"条目对象"。LevelData 本身仍然克隆,
            //                        所以往 Session.LevelData.Entities 里**追加**实体的行为仍能被读档回滚
            //                        (只有"就地修改某个 EntityData 的字段"不会回滚, 这很少见)。
            //  - ShareLevelDataAll : 连 LevelData **和 MapData** 一起共享。
            //                        注意 MapData 才是大头 —— 快照里装的是 Session.MapData, 也就是
            //                        **整张地图所有房间**的定义 (45k EntityData 是全图的, 不是单房间的)。
            //                        随机房间类 Mod (CelesteRandomizer) 会 `new MapData(...)` 整张重建,
            //                        共享后读档不会把地图还原成旧版本, 对这类 Mod 反而更合适。
            || (ModSettings.ShareLevelDataAll && (type == typeof(LevelData) || type == typeof(MapData)))
            || (ModSettings.ShareLevelData && (type == typeof(EntityData) || type == typeof(DecalData)))

            // XNA GraphicsResource
            || type.IsSubclassOf(typeof(GraphicsResource))

            // NLua
            || type == typeof(Lua)
            || type == typeof(KeraLua.Lua)
            || type.IsSubclassOf(typeof(LuaBase))

            // MonoMod
            || type == typeof(ILHook)
            || type == typeof(Helpers.LegacyMonoMod.LegacyILHook)
            || typeof(IDetourBase).IsAssignableFrom(type)
            || type.GetInterfaces().Any(t => t.FullName is "Celeste.Mod.Helpers.LegacyMonoMod.ILegacyDetour")

            // CelesteNet
            || (type.Namespace?.StartsWith("Celeste.Mod.CelesteNet.") ?? false) && !type.IsSubclassOf(typeof(Entity))
        ) {
            return true;
        }

        if (SaveLoadInterop.CanReturnSameObject(type)) {
            return true;
        }


#if DEBUG
        if (Log_IDisposable) {
            if (typeof(IDisposable).IsAssignableFrom(Nullable.GetUnderlyingType(type) ?? type)) {
                if (type.IsCompilerGenerated()) {
                    Logger.Debug("SpeedrunTool/DeepClonerUtils", $"{type} is CompilerGenerated IDisposable, check if it could be cloned safely!");
                    // 比如由返回值 IEnumerator 的方法生成的类, 就是 IDisposable.
                    // 如果方法内部没有 using / try-finally, 那么就不持有资源, 不 Dispose 也没事
                }
                else {
                    Logger.Warn("SpeedrunTool/DeepClonerUtils", $"{type} is IDisposable and should not be cloned!");
                }
            }
        }
#endif

        return null;
    }

    // Clone 对象的字段前，判断哪些类型是直接使用原对象或者自行通过其它方法 clone
    // Before cloning object's field, determine which types are directly used by the original object
    private static object PreCloneProcessor(object sourceObj, DeepCloneState deepCloneState) {
        if (sourceObj == null) {
            return null;
        }

        // profiler 打开时统计每个类型被克隆的次数 (只在主线程, 预克隆在后台线程不统计)
        if (SaveLoadProfiler.Enabled && Thread.CurrentThread.IsMainThread()) {
            Type sourceType = sourceObj.GetType();
            cloneStats ??= [];
            cloneStats.TryGetValue(sourceType, out int count);
            cloneStats[sourceType] = count + 1;
        }

        lock (sourceObj) {
            if (sourceObj is VirtualAsset virtualAsset) {
                if (StateManager.Instance.State == State.Loading || !Thread.CurrentThread.IsMainThread()) {
                    // 预克隆的资源需要等待 LoadState 中移除实体之后才能判断是否需要 Reload，必须等待主线程中再操作
                    GraphicResourcesHandler.Add(virtualAsset);
                }
                return virtualAsset;
            }

            if (sourceObj is Scene) {
                if (sourceObj is Level) {
                    // 金草莓死亡或者 PageDown/Up 切换房间后等等改变 Level 实例的情况
                    // After golden strawberry deaths or changing rooms w/ Page Down / Up
                    if (Engine.Scene is Level level) {
                        return level;
                    }
                }

                return sourceObj;
            }

            // 稍后重新创建正在播放的 SoundSource 里的 EventInstance 实例
            if (sourceObj is SoundSource { Playing: true, instance: { } instance } source) {
                if (source.EventName.IsNullOrEmpty()) {
                    return null;
                }

                instance.NeedManualClone();
                instance.SaveTimelinePosition(instance.LoadTimelinePosition());

                return null;
            }

            if (sourceObj is CassetteBlockManager manager) {
                // isLevelMusic = true 时 sfx 自动等于 Audio.CurrentMusicEventInstance，无需重建
                if (manager.sfx is { } sfx && !manager.isLevelMusic) {
                    sfx.NeedManualClone();
                }

                if (manager.snapshot is { } snapshot) {
                    snapshot.NeedManualClone();
                }

                return null;
            }

            // 重新创建正在播放的 EventInstance 实例
            if (sourceObj is EventInstance eventInstance && eventInstance.IsNeedManualClone()) {
                EventInstance clonedEventInstance = eventInstance.Clone();

                bool isMainThread = Thread.CurrentThread.IsMainThread();
                if (StateManager.Instance.State == State.Saving && isMainThread) {
                    SaveLoadAction.ClonedEventInstancesWhenSave.Add(clonedEventInstance);
                }
                else if (!isMainThread) {
                    SaveLoadAction.ClonedEventInstancesWhenPreClone.Add(clonedEventInstance);
                }

                return clonedEventInstance;
            }

            // Fixes: 克隆 WeakReference 后 Target 没有一起被克隆的问题，修复 dynData.Weak 克隆不完整导致的一些报错
            // System.Reflection.TargetException: 非静态字段需要一个目标。
            // 在 System.Reflection.RtFieldInfo.CheckConsistency(Object target)
            // 在 System.Reflection.RtFieldInfo.InternalGetValue(Object obj, StackCrawlMark& stackMark)
            // 在 System.Reflection.RtFieldInfo.GetValue(Object obj)
            // 在 MonoMod.Utils.DynData`1.<>c__DisplayClass19_0.<.cctor>b__1(TTarget obj)
            // 在 MonoMod.Utils.DynData`1.get_Item(String name)
            // 在 Celeste.Mod.MaxHelpingHand.Entities.CustomizableRefill.<>c__DisplayClass0_0.<.ctor>b__0(Player player)
            if (sourceObj is WeakReference sourceWeak) {
                return new WeakReference(sourceWeak.Target.DeepClone(deepCloneState), sourceWeak.TrackResurrection);
            }

            Type type = sourceObj.GetType();

            // 手动克隆 WeakReference<T>
            if (type.IsWeakReference(out Type genericType)) {
                object[] parameters = { null };
                sourceObj.InvokeMethod("TryGetTarget", parameters);
                return type.GetConstructorInfo(genericType).Invoke(parameters.DeepClone(deepCloneState));
            }

            // 手动克隆 ConditionalWeakTable<TKey, TValue>
            // 之前好像不能正确支持 SMH+ v1.7.2 (尽管 SMH+ 自己内部写的也有问题)
            if (type.IsConditionalWeakTable(out Type weakTableKeyType, out Type weakTableValueType)) {
                // 目前已知需要克隆 ConditionalWeakTable 的有:
                // SkinModHelperPlus:   ConditionalWeakTable<Sprite, List<Object>>
                // Aqua:                ConditionalWeakTable<Entity, DataContainer>   . 其中 Key 包含了 **所有** 实体

                // 当 key 是 Level 时, 由于 SRT 克隆不同的 Level 对象时都会返回 Engine.Scene, 所以可能会出问题
                // 导致只能 TryAdd 而非 Add
                // https://discord.com/channels/403698615446536203/1520883580933374124/1521136713005662258
                FastReflectionHelper.FastInvoker addMethod = type.GetMethodDelegate("TryAdd");
                object weakTable = Activator.CreateInstance(type);

                // Entity-keyed CWTs are commonly used as per-entity dynamic
                // state. Only clone entries whose keys are part of the saved
                // object graph; stale entries from previous rooms otherwise
                // make a single save traverse and clone the entire history.
                bool onlyKnownEntityKeys = weakTableKeyType.IsSameOrSubclassOf(typeof(Entity));

                Type kvpType = typeof(KeyValuePair<,>).MakeGenericType(weakTableKeyType, weakTableValueType);
                FastReflectionHelper.FastInvoker keyGetter = kvpType.GetFieldInfo("key").GetFastInvoker();
                FastReflectionHelper.FastInvoker valueGetter = kvpType.GetFieldInfo("value").GetFastInvoker();
                object[] args = new object[2];
                foreach (object kvp in (IEnumerable)sourceObj) {
                    object sourceKey = keyGetter.Invoke(kvp, null);
                    object clonedKey = onlyKnownEntityKeys
                        ? deepCloneState.GetKnownRef(sourceKey)
                        : sourceKey.DeepClone(deepCloneState);
                    if (clonedKey == null) {
                        continue;
                    }
                    object clonedValue = valueGetter.Invoke(kvp, null).DeepClone(deepCloneState);
                    if (clonedKey != null && clonedValue != null) {
                        args[0] = clonedKey;
                        args[1] = clonedValue;
                        addMethod.Invoke(weakTable, args);
                    }
                }
                return weakTable;
            }

            if (sourceObj is Entity e) {
                DesyncRiskAnalyzer.Check(e);
            }

            return SaveLoadInterop.CustomDeepCloneObject(sourceObj);
        }
    }

    // Clone 对象的字段后，进行自定的处理
    // After cloning, perform custom processing
    private static object PostCloneProcessor(object sourceObj, object clonedObj, DeepCloneState deepCloneState) {
        if (sourceObj == null) {
            return null;
        }

        lock (sourceObj) {
            Type type = clonedObj.GetType();

            // 修复：DeepClone 后的 HashSet.Containes/Dictonary.ContainsKey(未重新 GetHashCode 的对象) 总是返回 False
            // 原因：没有重写 GetHashCode 方法 https://github.com/force-net/DeepCloner/issues/17#issuecomment-678650032
            // Fix: DeepClone's hashSet.Contains (ReferenceType) always returns false

            // 手动处理最常见的 HashSet<Component> 类型，避免使用反射以及判断类型
            if (clonedObj is HashSet<Component> hashSet) {
                backupComponents ??= new Stack<Component>();
                foreach (Component component in hashSet) {
                    if (component != null) {
                        backupComponents.Push(component);
                    }
                }

                hashSet.Clear();
                while (backupComponents.Count > 0) {
                    hashSet.Add(backupComponents.Pop());
                }
            }
            else if (type.IsHashSet(out Type hashSetElementType) && !hashSetElementType.IsSimple()) {
                IEnumerator enumerator = ((IEnumerable)clonedObj).GetEnumerator();
                backupHashSet ??= new Stack<object>();
                while (enumerator.MoveNext()) {
                    if (enumerator.Current is { } element) {
                        backupHashSet.Push(element);
                    }
                }

                if (backupHashSet.Count >= 0) {
                    clonedObj.InvokeMethod("Clear");
                    FastReflectionHelper.FastInvoker addDelegate = type.GetMethodDelegate("Add");
                    while (backupHashSet.Count > 0) {
                        addDelegate.Invoke(clonedObj, backupHashSet.Pop());
                    }
                }
            }
            else if (type.IsIDictionary(out Type dictKeyType, out Type _) && !dictKeyType.IsSimple() &&
                       clonedObj is IDictionary { Count: > 0 } clonedDict) {
                backupDict ??= [];
                backupDict.SetRange(clonedDict);
                clonedDict.Clear();
                clonedDict.SetRange(backupDict);
                backupDict.Clear();
            }

            // Clone dynData.Data
            if (type is { IsClass: true } objType && !DynDataUtils.IgnoreObjects.ContainsKey(sourceObj)) {
                bool cloned = false;

                // 对其类型及其逐层展开的根类型 T 检验 DynData<T> 里是否涉及到这个对象
                do {
                    if (DynDataUtils.NotExistDynData(objType, out object dataMap)) {
                        continue;
                    }

                    object[] parameters = [sourceObj, null];
                    if (false == (bool)dataMap.InvokeMethod("TryGetValue", parameters)) {
                        continue;
                    }

                    object sourceValue = parameters[1];
                    if (sourceValue.GetFieldValue("Data") is not Dictionary<string, object> data || data.Count == 0) {
                        continue;
                    }

                    dataMap.InvokeMethod("Add", clonedObj, sourceValue.DeepClone(deepCloneState));
                    cloned = true;
                } while ((objType = objType.BaseType) != null && objType.IsSameOrSubclassOf(typeof(object)));

                if (!cloned) {
                    DynDataUtils.IgnoreObjects.Add(clonedObj, null);
                }
            }

            // Clone DynamicData
            // 注意这个和 DynData 不是同一个东西
            if (DynamicData._DataMap.TryGetValue(sourceObj, out DynamicData._Data_ value) && value.Data.Count > 0) {
                DynamicData._DataMap.TryAdd(clonedObj, value.DeepClone(deepCloneState));
            }
        }

        return clonedObj;
    }


    [Load]
    private static void Config() {
        DeepCloner.SetKnownTypesProcessor(KnownTypeProcessor);
        DeepCloner.SetPreCloneProcessor(PreCloneProcessor);
        DeepCloner.SetPostCloneProcessor(PostCloneProcessor);
    }

    [Unload]
    private static void Clear() {
        DeepCloner.ClearKnownTypesProcessor();
        DeepCloner.ClearPreCloneProcessor();
        DeepCloner.ClearPostCloneProcessor();
    }

    private static void InitSharedDeepCloneState() {
        sharedDeepCloneState ??= new DeepCloneState();
    }

    internal static void ClearSharedDeepCloneState() {
        sharedDeepCloneState = null;
    }

    internal static void SetSharedDeepCloneState(DeepCloneState deepCloneState) {
        sharedDeepCloneState = deepCloneState;
    }

    public static T DeepCloneShared<T>(this T obj) {
        InitSharedDeepCloneState();
        return obj.DeepClone(sharedDeepCloneState);
    }

    public static TTo DeepCloneToShared<TFrom, TTo>(this TFrom objFrom, TTo objTo) where TTo : class, TFrom {
        InitSharedDeepCloneState();
        return objFrom.DeepCloneTo(objTo, sharedDeepCloneState);
    }

    public static TTo ShallowCloneToShared<TFrom, TTo>(this TFrom objFrom, TTo objTo) where TTo : class, TFrom {
        InitSharedDeepCloneState();
        return objFrom.ShallowCloneTo(objTo, sharedDeepCloneState);
    }
}
