using Celeste.Mod.SpeedrunTool.DeathStatistics;
using Celeste.Mod.SpeedrunTool.RoomTimer;
using Celeste.Mod.SpeedrunTool.SaveLoad.ThirdPartySupport;
using Celeste.Mod.SpeedrunTool.SaveLoad.Utils;
using Celeste.Mod.SpeedrunTool.Utils;
using FMOD.Studio;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Celeste.Mod.SpeedrunTool.SaveLoad;

public sealed class SaveLoadAction {

    public delegate void SlAction(Dictionary<Type, Dictionary<string, object>> savedValues, Level level);

    // 核心的一些 SaveLoadAction 在第一次 SL 时才初始化，避免通过安装依赖功能解除禁用的 Mod 被忽略.

    private static bool internalActionInitialized = false;

    private static bool needInitializeDictionaryId = false;
    private static bool slotInitialized {
        get => SaveSlotsManager.Slot.ValueDictionaryInitialized;
        set {
            SaveSlotsManager.Slot.ValueDictionaryInitialized = value;
        }
    }
    public static List<EventInstance> ClonedEventInstancesWhenSave => SaveSlotsManager.Slot.ClonedEventInstancesWhenSave;
    public static List<EventInstance> ClonedEventInstancesWhenPreClone => SaveSlotsManager.Slot.ClonedEventInstancesWhenPreClone;

    // only actions, no values stored. Share among all save slots
    private static List<SaveLoadAction> SharedActions = [];

    // values, belong to each save slot

    internal static Dictionary<int, Dictionary<Type, Dictionary<string, object>>> AllSavedValues {
        get => SaveSlotsManager.Slot.AllSavedValues;
        set {
            SaveSlotsManager.Slot.AllSavedValues = value;
        }
    }

    internal static Dictionary<int, Dictionary<Type, Dictionary<string, object>>> InitValueDictionary() {
        Dictionary<int, Dictionary<Type, Dictionary<string, object>>> dict = [];
        foreach (SaveLoadAction saveLoadAction in SharedActions) {
            dict[saveLoadAction.dictionaryId] = [];
        }
        return dict;
    }

    private static Dictionary<Type, FieldInfo[]> simpleStaticFields;
    private static Dictionary<Type, FieldInfo[]> modModuleFields;

#if DEBUG
    public string ActionDescription = "";
#endif

    // Release 版也能用的轻量名字 (注册时的调用方类型名), 只给 profiler 用
    internal string debugName;

    private int dictionaryId;

    private readonly Action clearState;
    private readonly Action<Level> beforeSaveState;
    private readonly Action<Level> beforeLoadState;
    private readonly Action preCloneEntities;
    internal readonly SlAction loadState;
    private readonly SlAction saveState;
    private int executeOrder;

    private static int? contextOrder = null;
    private const int Order_InternalCoreAction = 0;
    private const int Order_InternalOtherAction = 100;
    private const int Order_ExternalAction = 10000;
    private const int Order_ExecuteAtLast = 1000000;

    public SaveLoadAction(SlAction saveState = null, SlAction loadState = null, Action clearState = null,
        Action<Level> beforeSaveState = null, Action preCloneEntities = null) {
        this.saveState = saveState;
        this.loadState = loadState;
        this.clearState = clearState;
        this.beforeSaveState = beforeSaveState;
        this.preCloneEntities = preCloneEntities;
        needInitializeDictionaryId = true;
    }

    public SaveLoadAction(SlAction saveState, SlAction loadState, Action clearState,
        Action<Level> beforeSaveState, Action<Level> beforeLoadState, Action preCloneEntities) {
        this.saveState = saveState;
        this.loadState = loadState;
        this.clearState = clearState;
        this.beforeSaveState = beforeSaveState;
        this.beforeLoadState = beforeLoadState;
        this.preCloneEntities = preCloneEntities;
        needInitializeDictionaryId = true;
    }

    // used by CelesteTAS
    [Obsolete("crash on macOS if speedrun tool is not installed, use SafeAdd() instead")]
    public static void Add(SaveLoadAction saveLoadAction) {
        SharedActions.Add(saveLoadAction);
    }

    // ReSharper disable once MemberCanBePrivate.Global
    // ReSharper disable once UnusedMethodReturnValue.Global

    internal static SaveLoadAction InternalSafeAdd(Action<Dictionary<Type, Dictionary<string, object>>, Level> saveState = null,
        Action<Dictionary<Type, Dictionary<string, object>>, Level> loadState = null, Action clearState = null,
        Action<Level> beforeSaveState = null, Action<Level> beforeLoadState = null, Action preCloneEntities = null, int skipFrames = 2) {
        if (internalActionInitialized) {
            throw new Exception("Internal SaveLoad actions are added after expected time!");
        }
        SaveLoadAction saveLoadAction = new(CreateSlAction(saveState), CreateSlAction(loadState), clearState, beforeSaveState, beforeLoadState, preCloneEntities);
        saveLoadAction.executeOrder = contextOrder.GetValueOrDefault(Order_InternalOtherAction);
        saveLoadAction.debugName = NameFromDelegate(saveLoadAction) ?? CaptureCallerName(skipFrames + 1);
#if DEBUG
        AddDebugDescription(saveLoadAction, internalCall: true, skipFrame: skipFrames);
#endif
        SharedActions.Add(saveLoadAction);
        return saveLoadAction;
    }

    private static SlAction CreateSlAction(Action<Dictionary<Type, Dictionary<string, object>>, Level> action) {
        return (SlAction)action?.Method.CreateDelegate(typeof(SlAction), action.Target);
    }

    /// <summary>
    /// For third party mods
    /// </summary>
    public static object SafeAdd(Action<Dictionary<Type, Dictionary<string, object>>, Level> saveState,
        Action<Dictionary<Type, Dictionary<string, object>>, Level> loadState, Action clearState,
        Action<Level> beforeSaveState, Action<Level> beforeLoadState, Action preCloneEntities = null) {
        SaveLoadAction saveLoadAction = new(CreateSlAction(saveState), CreateSlAction(loadState), clearState, beforeSaveState, beforeLoadState, preCloneEntities);
        saveLoadAction.executeOrder = Order_ExternalAction;
        saveLoadAction.debugName = "<<External>> " + (NameFromDelegate(saveLoadAction) ?? CaptureCallerName(4));
#if DEBUG
        AddDebugDescription(saveLoadAction, internalCall: false, skipFrame: 3);
#endif
        SharedActions.Add(saveLoadAction);
        return saveLoadAction;
    }

    /// <summary>
    /// 从委托本身推断名字, 比 StackTrace 准得多: 委托的 Target 是编译器生成的闭包/单例,
    /// 它的 DeclaringType 就是注册者所在的类; 方法名形如 &lt;Load&gt;b__3_0, 尖括号里是真实方法名。
    /// </summary>
    private static string NameFromDelegate(SaveLoadAction action) {
        try {
            SlAction handler = action.saveState ?? action.loadState;
            if (handler is null) {
                return null;
            }

            Type targetType = handler.Target?.GetType();
            string typeName = targetType?.DeclaringType?.Name ?? targetType?.Name;

            string methodName = handler.Method.Name;
            int open = methodName.IndexOf('<');
            int close = methodName.IndexOf('>');
            if (open == 0 && close > 1) {
                methodName = methodName.Substring(1, close - 1); // <SupportSimpleStaticFields>b__12_0 -> SupportSimpleStaticFields
            }

            if (typeName is null || typeName.StartsWith("<>")) {
                return methodName;
            }

            return $"{typeName}.{methodName}";
        }
        catch {
            return null;
        }
    }

    /// <summary>
    /// 取注册处的调用方 "类型名.方法名", 给 profiler 用。一次性成本(每个 action 一次 StackTrace)。
    /// </summary>
    private static string CaptureCallerName(int skipFrames) {
        try {
            System.Reflection.MethodBase method = new System.Diagnostics.StackTrace(skipFrames).GetFrame(0)?.GetMethod();
            if (method is null) {
                return "<unknown>";
            }

            string typeName = method.DeclaringType?.Name ?? "<unknown>";
            // 编译器为 lambda 生成的类名 (<>c / <>c__DisplayClass) 没有信息量, 只保留方法名
            return typeName.StartsWith("<>") ? method.Name : $"{typeName}.{method.Name}";
        }
        catch {
            return "<unknown>";
        }
    }

#if DEBUG
    private static void AddDebugDescription(SaveLoadAction action, bool internalCall, int skipFrame) {
        System.Diagnostics.StackFrame stackFrame = new System.Diagnostics.StackTrace(skipFrame).GetFrame(0);
        if (stackFrame.GetMethod() is { } method) {
            if (internalCall) {
                action.ActionDescription = $"{method.DeclaringType?.FullName?.
                    Replace("Celeste.Mod.SpeedrunTool.SaveLoad.", "")?.
                    Replace("Celeste.Mod.SpeedrunTool.", "") ?? "<UnknownType>"} @ {method.Name ?? "<UnknownMethod>"}";
            }
            else {
                action.ActionDescription = $"<<External>> {method.DeclaringType?.FullName ?? "<UnknownType>"} @ {method.Name ?? "<UnknownMethod>"}";
            }
        }
        else {
            action.ActionDescription = "<BadStackFrame>";
        }
    }
#endif

    /// <summary>
    /// For third party mods
    /// </summary>
    public static bool Remove(SaveLoadAction saveLoadAction) {
        bool b = SharedActions.Remove(saveLoadAction);
        if (b) {
            needInitializeDictionaryId = true;
        }
        return b;
    }

    internal static void Remove(Func<SaveLoadAction, bool> predicate) {
        List<SaveLoadAction> toRemove = [];
        foreach (SaveLoadAction action in SharedActions) {
            if (predicate(action)) {
                toRemove.Add(action);
            }
        }
        if (toRemove is not null) {
            foreach (SaveLoadAction action in toRemove) {
                Remove(action);
            }
        }
    }

    internal static void OnSaveState(Level level) {
        Dictionary<int, Dictionary<Type, Dictionary<string, object>>> dict = AllSavedValues;
#if DEBUG
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (SaveLoadAction saveLoadAction in SharedActions) {
            sw.Restart();
            var before = DebugTool.MemoryTracker.TakeGcSample();
            saveLoadAction.saveState?.Invoke(dict[saveLoadAction.dictionaryId], level);
            if (sw.ElapsedMilliseconds > 10 && EachAction_Profilling) {
                Logger.Debug($"SpeedrunTool/Save",
                    $"{sw.ElapsedMilliseconds,3} ms, " +
                    $"{DebugTool.MemoryTracker.FormatDiff(before, DebugTool.MemoryTracker.TakeGcSample(), simplified: true)},    " +
                    $"[{saveLoadAction.dictionaryId}] {saveLoadAction.ActionDescription}");
            }
        }
#else
        if (SaveLoadProfiler.Enabled) {
            ProfiledInvoke(save: true, dict, level);
            return;
        }

        foreach (SaveLoadAction saveLoadAction in SharedActions) {
            saveLoadAction.saveState?.Invoke(dict[saveLoadAction.dictionaryId], level);
        }
#endif
    }

    internal static void OnLoadState(Level level) {
        Dictionary<int, Dictionary<Type, Dictionary<string, object>>> dict = AllSavedValues;
#if DEBUG
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (SaveLoadAction saveLoadAction in SharedActions) {
            sw.Restart();
            var before = DebugTool.MemoryTracker.TakeGcSample();
            saveLoadAction.loadState?.Invoke(dict[saveLoadAction.dictionaryId], level);
            if (sw.ElapsedMilliseconds > 10 && EachAction_Profilling) {
                Logger.Debug($"SpeedrunTool/Load",
                    $"{sw.ElapsedMilliseconds,3} ms, " +
                    $"{DebugTool.MemoryTracker.FormatDiff(before, DebugTool.MemoryTracker.TakeGcSample(), simplified: true)},    " +
                    $"[{saveLoadAction.dictionaryId}] {saveLoadAction.ActionDescription}");
            }
        }
#else
        if (SaveLoadProfiler.Enabled) {
            ProfiledInvoke(save: false, dict, level);
            return;
        }

        foreach (SaveLoadAction saveLoadAction in SharedActions) {
            saveLoadAction.loadState?.Invoke(dict[saveLoadAction.dictionaryId], level);
        }
#endif
    }

    /// <summary>
    /// 只在 <see cref="SaveLoadProfiler.Enabled"/> 时使用: 逐个 action 计时, 打印耗时 ≥0.5ms 的,
    /// 用来点名"到底是哪个补丁把 loadActions 拖到 60ms"。
    /// </summary>
    private static void ProfiledInvoke(bool save, Dictionary<int, Dictionary<Type, Dictionary<string, object>>> dict, Level level) {
        System.Diagnostics.Stopwatch sw = new();

        foreach (SaveLoadAction saveLoadAction in SharedActions) {
            SlAction handler = save ? saveLoadAction.saveState : saveLoadAction.loadState;
            if (handler == null) {
                continue;
            }

            sw.Restart();
            handler.Invoke(dict[saveLoadAction.dictionaryId], level);
            double ms = sw.Elapsed.TotalMilliseconds;

            if (ms >= 0.5) {
                Logger.Info("SpeedrunTool/Profile",
                    $"  {(save ? "save" : "load")} [{saveLoadAction.dictionaryId,2}] {saveLoadAction.debugName ?? "<unknown>",-40} {ms,7:0.00} ms");
            }
        }
    }

    internal static void OnClearState(bool clearBeforeSave = false) {
        if (clearBeforeSave) {
            Dictionary<int, Dictionary<Type, Dictionary<string, object>>> dict = AllSavedValues;
            foreach (SaveLoadAction saveLoadAction in SharedActions) {
                foreach (KeyValuePair<Type, Dictionary<string, object>> pair in dict[saveLoadAction.dictionaryId]) {
                    pair.Value.Clear();
                    // avoid creating new Dictionary instances
                }
            }
            slotInitialized = true;
        }
        else {
            AllSavedValues = null;
            slotInitialized = false;
        }
        foreach (SaveLoadAction saveLoadAction in SharedActions) {
            saveLoadAction.clearState?.Invoke();
        }
    }

    internal static void OnBeforeSaveState(Level level) {
        foreach (SaveLoadAction saveLoadAction in SharedActions) {
            saveLoadAction.beforeSaveState?.Invoke(level);
        }
    }

    internal static void OnBeforeLoadState(Level level) {
        foreach (SaveLoadAction saveLoadAction in SharedActions) {
            saveLoadAction.beforeLoadState?.Invoke(level);
        }
    }

    internal static void OnPreCloneEntities() {
        foreach (SaveLoadAction saveLoadAction in SharedActions) {
            saveLoadAction.preCloneEntities?.Invoke();
        }
    }

    public static void SaveStaticMemberValues(Dictionary<Type, Dictionary<string, object>> values, Type type, params string[] memberNames) {
        if (type == null) {
            return;
        }

        if (!values.TryGetValue(type, out Dictionary<string, object> dict)) {
            values[type] = dict = [];
        }

        foreach (string memberName in memberNames) {
            if (type.GetMember(memberName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) is { } memberInfos) {
                if (memberInfos.Length == 0) {
                    $"SaveStaticMemberValues: No member found for type {type.FullName} and member name {memberName}".Log(LogLevel.Verbose);
                    continue;
                }

                if (memberInfos.First().IsField()) {
                    dict[memberName] = type.GetFieldValue(memberName).DeepCloneShared();
                }
                else {
                    dict[memberName] = type.GetPropertyValue(memberName).DeepCloneShared();
                }
            }
        }
    }
    public static void LoadStaticMemberValues(Dictionary<Type, Dictionary<string, object>> values) {
        // 使用时总是 (多个 SaveStaticMemberValues + 一个 LoadStaticMemberValues) 捆绑成一个 SaveLoadAction 使用
        // 由于每个 SaveLoadAction 都有自己独立的字典, 因此这边的读取实际上并不会多读什么
        // type 和 memberNames 自动地在这个双层字典中可以读出来, 不需要我们提供
        foreach (KeyValuePair<Type, Dictionary<string, object>> pair in values) {
            foreach (string memberName in pair.Value.Keys) {
                Type type = pair.Key;
                if (type.GetMember(memberName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) is { } memberInfos) {
                    if (memberInfos.Length == 0) {
                        $"LoadStaticMemberValues: No member found for type {type.FullName} and member name {memberName}".Log(LogLevel.Verbose);
                        continue;
                    }

                    if (memberInfos.First().IsField()) {
                        type.SetFieldValue(memberName, pair.Value[memberName].DeepCloneShared());
                    }
                    else {
                        type.SetPropertyValue(memberName, pair.Value[memberName].DeepCloneShared());
                    }
                }
            }
        }
    }


    [Unload]
    private static void Unload() {
        SharedActions.Clear();
    }


    private static void InitActions() {
        if (internalActionInitialized) {
            return;
        }

        contextOrder = Order_InternalCoreAction;
        InitFields();
        SupportSimpleStaticFields();
        SupportModModuleFields();
        FixSaveLoadIcon();
        BetterCasualPlay();
        SupportExternalMember();
        SupportCalcRandom();
        SupportSettings();
        SupportMInput();
        SupportInput();
        SupportAudioMusic();
        FixVertexLight();
        MuteAudioUtils.AddAction();
        ExternalAction();

        // mod support
        SupportModSessionAndSaveData();
        ThirdParty.Support();

        // 放最后，确保收集了所有克隆的 VirtualAssets 与 EventInstance
        contextOrder = Order_ExecuteAtLast;
        GraphicResourcesHandler.AddSaveLoadAction();
        ReleaseEventInstances();
        contextOrder = null;

        internalActionInitialized = true;
        needInitializeDictionaryId = true;
    }

    public static void InitSlots() {
        // most mods might add saveload actions before InitActions is executed
        InitActions();
        // but some might add during gameplay (why would you do that?)

        // anyway, we rebuild the dictionary
        if (needInitializeDictionaryId) {
            SharedActions = [.. SharedActions.OrderBy(x => x.executeOrder)]; // it's a stable sort
            int i = 0;
            foreach (SaveLoadAction action in SharedActions) {
                i++;
                action.dictionaryId = i;
            }
#if DEBUG
            if (Log_AllActions) {
                System.Text.StringBuilder sb = new();
                sb.Append("\n===== Initializing SharedActions... =====\n");
                foreach (SaveLoadAction action in SharedActions) {
                    sb.Append($"--- [{action.dictionaryId}] {action.ActionDescription} ---\n");
                }
                Logger.Debug("SpeedrunTool", sb.ToString());
            }
#endif

            SaveSlotsManager.RequireReInit();
            needInitializeDictionaryId = false;
        }

        if (slotInitialized) {
            return;
        }

        AllSavedValues = InitValueDictionary();
        slotInitialized = true;
    }

#if DEBUG
#pragma warning disable IDE0060 // 删除未使用的参数
    internal static void LogSavedValues(Level level) {
        System.Text.StringBuilder sb = new();

        foreach (SaveLoadAction slAction in SharedActions) {
            sb.Append($"\n======  {slAction.ActionDescription}  ======\n");
            int count = 1;
            foreach (KeyValuePair<Type, Dictionary<string, object>> pair in AllSavedValues[slAction.dictionaryId]) {
                sb.Append($"---  Type {count.ToString().PadRight(3)} =   {pair.Key.FullName}  ---\n");
                foreach (KeyValuePair<string, object> pair2 in pair.Value) {
                    sb.Append($" ├  {PadToMultiple(pair2.Key, 32)} | {pair2.Value}\n");
                }
                sb.Append('\n');
                count++;
            }
        }

        if (Log_SavedLevelEntities) {
            sb.Append("\n======  Saved Level (Part) ======");
            sb.Append("\n[0] Level\n");
            AppendInstanceField(typeof(Level), level);
            int index = 0;
            foreach (KeyValuePair<Type, List<Entity>> entity in level.Tracker.Entities) {
                if (entity.Value.Count == 0) {
                    continue;
                }
                index++;
                sb.Append($"\n[{index}] {entity.Key}    x {entity.Value.Count}\n");
                AppendInstanceField(entity.Key, entity.Value.First());
            }
        }

        Logger.Log(LogLevel.Debug, "SpeedrunTool", sb.ToString());

        void AppendInstanceField(Type type, object obj) {
            // modified from DeepClonerMsilGenerator.GenerateClonerInternal
            List<FieldInfo> list = [];
            Type tp = type;
            do {
                // don't do anything with this dark magic!
                if (tp == typeof(ContextBoundObject)) {
                    break;
                }

                list.AddRange(tp.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly));
                tp = tp.BaseType;
            } while (tp != null);
            if (obj is not null) {
                foreach (FieldInfo fi in list) {
                    sb.Append($" ├  {PadToMultiple(fi.Name, 32)} | {PadToMultiple(fi.GetValue(obj), 32)} | {(Force.DeepCloner.Helpers.DeepClonerSafeTypes.CanReturnSameObject(fi.FieldType) ? "" : "Complex")}\n");
                }
            }
            else {
                foreach (FieldInfo fi in list) {
                    sb.Append($" ├  {PadToMultiple(fi.Name, 32)} | {PadToMultiple(fi.FieldType.Name, 32)} | {(Force.DeepCloner.Helpers.DeepClonerSafeTypes.CanReturnSameObject(fi.FieldType) ? "" : "Complex")}\n");
                }
            }
        }

        string PadToMultiple(object obj, int b) {
            string str = obj?.ToString() ?? " ";
            int length = str.Length;
            int targetLength = ((length - 1) / b + 1) * b;
            return str.PadRight(targetLength);
        }
    }
#pragma warning restore IDE0060 // 删除未使用的参数
#endif

    private static void InitFields() {
        simpleStaticFields = [];
        modModuleFields = [];

        IEnumerable<Type> types = ModUtils.GetAllTypes().Where(type =>
            !type.IsGenericType
            && type.FullName != null
            && !type.FullName.StartsWith("Celeste.Mod.SpeedrunTool")
            && !type.IsSubclassOf(typeof(Oui))
            && type.IsSameOrSubclassOf(typeof(Entity)) || type.IsSameOrSubclassOf(typeof(Component)) ||
            type.IsSameOrSubclassOf(typeof(Renderer)));

        foreach (Type type in types) {
            try {
                FieldInfo[] fieldInfos = [.. type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(info => {
                        Type fieldType = info.FieldType; // might throw exception
                        return !info.IsConst() && fieldType.IsSimpleClass(genericType =>
                            fieldType == type
                            || fieldType == typeof(Level)
                            || fieldType == typeof(Player)
                            || fieldType == typeof(MTexture)
                            || fieldType == typeof(CrystalStaticSpinner)
                            || fieldType == typeof(Solid)
                            || fieldType.IsSubclassOf(typeof(Renderer))
                            || fieldType.IsSubclassOf(typeof(VirtualAsset))
                            || genericType == type
                            || genericType == typeof(Level)
                            || genericType == typeof(Player)
                            || genericType == typeof(MTexture)
                            || genericType == typeof(CrystalStaticSpinner)
                            || genericType == typeof(Solid)
                            || genericType.IsSubclassOf(typeof(Renderer))
                            || genericType.IsSubclassOf(typeof(VirtualAsset))
                        );
                    })];

                if (fieldInfos.Length == 0) {
                    continue;
                }

                simpleStaticFields[type] = fieldInfos;
            }
            catch {
                /* possibly a FileNotFoundException
                 * GetTypesSafe() just checks if the type can be loaded, and if the FullyQualifiedName, declaringType, baseType of the type is safe, etc.
                 * but doesn't check if each memberInfo is "safe"
                 * only method calls or something which use the "dangerous" memberInfo will immediately lead to an Exception
                 * 
                 * e.g: when you enable KyfexHelper, but don't enable auspicioushelper
                 * then KyfexHelper.KevinFace.tcComponent will give you a surprise
                 */
                continue;
            }
        }

        InitModuleFields();
        InitExtendedVariantsFields();
        FilterStaticFields();
    }

    private static void InitModuleFields() {
        foreach (EverestModule everestModule in Everest.Modules) {
            Type type = everestModule.GetType();
            List<FieldInfo> staticFields = [];
            List<FieldInfo> instanceFields = [];

            FieldInfo[] fieldInfos = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            foreach (FieldInfo fieldInfo in fieldInfos.Where(info => {
                Type fieldType = info.FieldType;
                return !info.IsInitOnly &&
                       (fieldType == typeof(Level) ||
                        fieldType == typeof(Session) ||
                        fieldType.IsSameOrSubclassOf(typeof(Entity)) && !fieldType.IsSubclassOf(typeof(Oui)) ||
                        fieldType == typeof(Vector2));
            })) {
                if (fieldInfo.IsStatic) {
                    staticFields.Add(fieldInfo);
                }
                else {
                    instanceFields.Add(fieldInfo);
                }
            }

            if (staticFields.Count > 0) {
                simpleStaticFields[type] = [.. staticFields];
            }

            if (instanceFields.Count > 0) {
                modModuleFields[type] = [.. instanceFields];
            }
        }
    }

    private static void InitExtendedVariantsFields() {
        if (ModUtils.GetType("ExtendedVariantMode", "ExtendedVariants.Variants.AbstractExtendedVariant") is { } variantType) {
            foreach (Type type in variantType.Assembly.GetTypesSafe().Where(type => type.IsSubclassOf(variantType))) {
                FieldInfo[] fieldInfos = [.. type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Where(info => !info.IsConst() && info.FieldType.IsSimpleClass())];

                if (fieldInfos.Length == 0) {
                    continue;
                }

                simpleStaticFields[type] = fieldInfos;
            }
        }
    }


    private static void FilterStaticFields() {
        // 过滤掉非法的字段
        // 例如未安装 DJMapHelper 时 ExtendedVariantsMode 的 AutoDestroyingReverseOshiroModder.stateMachine
        // 不止会有 TargetInvocationException, 此外 LeniencyHelper 还会有未绑定泛型参数造成的异常
        foreach (Type type in simpleStaticFields.Keys.ToArray()) {
            FieldInfo[] fieldInfos = [.. simpleStaticFields[type].Where(info => {
                try {
                    info.GetValue(null);
                    return true;
                }
                catch {
                    return false;
                }
            })];

            if (fieldInfos.Length > 0) {
                simpleStaticFields[type] = fieldInfos;
            }
            else {
                simpleStaticFields.Remove(type);
            }
        }
    }

    private static void SupportSimpleStaticFields() {
        InternalSafeAdd(
            (dictionary, _) => {
                foreach (Type type in simpleStaticFields.Keys) {
                    FieldInfo[] fieldInfos = simpleStaticFields[type];
                    // Logger.Debug("SpeedrunTool", "\n\n" + string.Join("\n", fieldInfos.Select(info => type.FullName + " " + info.Name + " " + info.FieldType)));
                    Dictionary<string, object> values = [];

                    foreach (FieldInfo fieldInfo in fieldInfos) {
                        values[fieldInfo.Name] = fieldInfo.GetValue(null);
                    }

                    dictionary[type] = values.DeepCloneShared();
                }
            },
            (dictionary, _) => {
                // 性能优化: 原实现先把整个 dictionary 深克隆一遍再读, 每次读档要多分配
                // (类型数 + 字段数) 个字典与条目; 语义上只需要"每个值克隆一份再写回".
                foreach (KeyValuePair<Type, Dictionary<string, object>> typePair in dictionary) {
                    Type type = typePair.Key;
                    foreach (KeyValuePair<string, object> pair in typePair.Value) {
                        type.SetFieldValue(pair.Key, pair.Value.DeepCloneShared());
                    }
                }
            }
        );
    }

    private static void SupportModModuleFields() {
        InternalSafeAdd(
            (savedValues, _) => {
                foreach (EverestModule module in Everest.Modules) {
                    Dictionary<string, object> dict = [];
                    Type moduleType = module.GetType();
                    if (modModuleFields.TryGetValue(moduleType, out FieldInfo[] moduleFields)) {
                        foreach (FieldInfo fieldInfo in moduleFields) {
                            dict[fieldInfo.Name] = fieldInfo.GetValue(module);
                        }

                        savedValues[moduleType] = dict.DeepCloneShared();
                    }
                }
            },
            (savedValues, _) => {
                // 性能优化: 同上, 只克隆要写回的值, 不再克隆整本字典
                foreach (EverestModule module in Everest.Modules) {
                    if (savedValues.TryGetValue(module.GetType(), out Dictionary<string, object> dict)) {
                        foreach (KeyValuePair<string, object> pair in dict) {
                            module.SetFieldValue(pair.Key, pair.Value.DeepCloneShared());
                        }
                    }
                }
            }
        );
    }

    private static void FixSaveLoadIcon() {
        InternalSafeAdd(loadState: (_, _) => {
            // 修复右下角存档图标残留
            if (!UserIO.savingInternal) {
                SaveLoadIcon.Hide();
            }
        });
    }

    private static void BetterCasualPlay() {
        InternalSafeAdd(beforeSaveState: level => {
            level.Session.SetFlag("SpeedrunTool_Reset_unpauseTimer", false);
            if (StateManager.Instance.SavedByTas) {
                return;
            }

            // 移除冻结帧，移除暂停黑屏
            Engine.FreezeTimer = 0f;
            level.HudRenderer.BackgroundFade = 0f;

            // 移除暂停帧
            if (level.GetFieldValue<float>("unpauseTimer") > 0f || !level.Paused && level.wasPaused) {
                level.Session.SetFlag("SpeedrunTool_Reset_unpauseTimer");

                if (level.GetFieldValue<float>("unpauseTimer") > 0f) {
                    level.SetFieldValue("unpauseTimer", 0f);
                }

                level.wasPaused = false;
                level.EndPauseEffects();
            }
        });
    }

    private static void SupportExternalMember() {
        InternalSafeAdd(
            (savedValues, _) => {
#pragma warning disable CS0618 // 类型或成员已过时
                SaveStaticMemberValues(savedValues, typeof(Engine),
                    nameof(Engine.DashAssistFreeze),
                    nameof(Engine.DashAssistFreezePress),
                    nameof(Engine.DeltaTime),
                    nameof(Engine.FrameCounter),
                    nameof(Engine.FreezeTimer),
                    nameof(Engine.RawDeltaTime),
                    nameof(Engine.TimeRate),
                    nameof(Engine.TimeRateB),
                    nameof(Engine.Pooler)
                );
#pragma warning restore CS0618 // 类型或成员已过时
                SaveStaticMemberValues(savedValues, typeof(Glitch), nameof(Glitch.Value));
                SaveStaticMemberValues(savedValues, typeof(Distort), nameof(Distort.Anxiety), nameof(Distort.GameRate));
                SaveStaticMemberValues(savedValues, typeof(ScreenWipe), nameof(ScreenWipe.WipeColor));
                SaveStaticMemberValues(savedValues, typeof(Audio), nameof(Audio.currentCamera));
                // Fixed: Game crashes after save PandorasBoxUtils.DustSpriteColorController
                SaveStaticMemberValues(savedValues, typeof(DustStyles), nameof(DustStyles.Styles));
            },
            (savedValues, _) => LoadStaticMemberValues(savedValues)
        );
    }

    private static void SupportCalcRandom() {
        InternalSafeAdd(
            (savedValues, _) => SaveStaticMemberValues(savedValues, typeof(Calc),
                nameof(Calc.Random), nameof(Calc.randomStack)),
            (savedValues, _) => LoadStaticMemberValues(savedValues)
        );
    }

    private static void SupportSettings() {
        InternalSafeAdd(
            (savedValues, _) => {
                if (Settings.Instance is { } settings) {
                    Dictionary<string, object> dict = new() {
                        [nameof(Settings.GrabMode)] = settings.GrabMode,
                        [nameof(Settings.CrouchDashMode)] = settings.CrouchDashMode
                    };
                    savedValues[typeof(Settings)] = dict;
                }
            },
            (savedValues, _) => {
                if (Settings.Instance is { } settings && savedValues.TryGetValue(typeof(Settings), out Dictionary<string, object> dict)) {
                    settings.GrabMode = (GrabModes)dict[nameof(Settings.GrabMode)];
                    settings.CrouchDashMode = (CrouchDashModes)dict[nameof(Settings.CrouchDashMode)];

                }
            }
        );
    }

    private static void SupportMInput() {
        InternalSafeAdd(
            (savedValues, _) => SaveStaticMemberValues(savedValues, typeof(MInput),
                nameof(MInput.Active), nameof(MInput.Disabled), nameof(MInput.Keyboard), nameof(MInput.Mouse), nameof(MInput.GamePads)),
            (savedValues, _) => {
                LoadStaticMemberValues(savedValues);

                // 关闭手柄震动
                MInput.GamePads[Input.Gamepad].Rumble(0f, 0f);
            }
        );
    }

    // Fix https://github.com/DemoJameson/CelesteSpeedrunTool/issues/19
    private static void SupportInput() {
        Type inputType = typeof(Input);
        InternalSafeAdd(
            (savedValues, _) => {
                SaveStaticMemberValues(savedValues, typeof(MInput), nameof(MInput.VirtualInputs));

                Dictionary<string, object> inputDict = [];
                foreach (FieldInfo fieldInfo in typeof(Input).GetFields(BindingFlags.Public | BindingFlags.Static).Where(info =>
                             info.FieldType.IsSameOrSubclassOf(typeof(VirtualInput)))) {
                    inputDict[fieldInfo.Name] = fieldInfo.GetValue(null);
                }

                inputDict["grabToggle"] = Input.grabToggle;
                inputDict["LastAim"] = Input.LastAim;

                savedValues[inputType] = inputDict.DeepCloneShared();

                foreach (EverestModule everestModule in Everest.Modules) {
                    if (everestModule.Metadata?.Name == "CelesteTAS" || everestModule == SpeedrunToolModule.Instance) {
                        continue;
                    }

                    if (everestModule.GetPropertyValue("SettingsType") is not Type settingsType) {
                        continue;
                    }

                    if (everestModule.GetPropertyValue("_Settings") is not { } settingsInstance) {
                        continue;
                    }

                    Dictionary<string, object> settingsDict = [];
                    foreach (PropertyInfo propertyInfo in settingsType.GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
                        if (propertyInfo.PropertyType != typeof(ButtonBinding)) {
                            continue;
                        }

                        settingsDict[propertyInfo.Name] = propertyInfo.GetValue(settingsInstance);
                    }

                    if (settingsDict.Count == 0) {
                        continue;
                    }

                    savedValues[settingsType] = settingsDict.DeepCloneShared();
                }
            }, (savedValues, _) => {
                // 性能优化: 不再 `savedValues = savedValues.DeepCloneShared()` 把整本字典(含所有
                // Mod 的 ButtonBinding 字典)克隆一遍, 改为在使用处按值克隆, 语义等价。

                if (StateManager.Instance.LoadByTas) {
                    MInput.VirtualInputs = (List<VirtualInput>)savedValues[typeof(MInput)][nameof(MInput.VirtualInputs)].DeepCloneShared();
                }

                Dictionary<string, object> dictionary = savedValues[inputType];
                foreach (KeyValuePair<string, object> inputPair in dictionary) {
                    string fieldName = inputPair.Key;
                    object virtualInput = inputPair.Value.DeepCloneShared();

                    if (StateManager.Instance.LoadByTas) {
                        inputType.SetFieldValue(fieldName, virtualInput);
                    }
                    else {
                        if (fieldName == "grabToggle") {
                            Input.grabToggle = (bool)virtualInput;
                        }
                        else if (fieldName == "LastAim") {
                            Input.LastAim = (Vector2)virtualInput;
                        }
                        else {
                            object fieldValue = inputType.GetFieldValue(fieldName);
                            if (fieldValue is VirtualJoystick virtualJoystick &&
                                virtualInput is VirtualJoystick savedVirtualJoystick) {
                                virtualJoystick.InvertedX = savedVirtualJoystick.InvertedX;
                                virtualJoystick.InvertedY = savedVirtualJoystick.InvertedY;
                            }
                            else if (fieldValue is VirtualIntegerAxis virtualIntegerAxis &&
                                       virtualInput is VirtualIntegerAxis savedVirtualIntegerAxis) {
                                virtualIntegerAxis.Inverted = savedVirtualIntegerAxis.Inverted;
                            }
                        }
                    }
                }

                if (StateManager.Instance.LoadByTas) {
                    foreach (EverestModule everestModule in Everest.Modules) {
                        if (everestModule.Metadata?.Name == "CelesteTAS" || everestModule == SpeedrunToolModule.Instance) {
                            continue;
                        }

                        if (everestModule.GetPropertyValue("SettingsType") is not Type settingsType) {
                            continue;
                        }

                        if (!savedValues.TryGetValue(settingsType, out Dictionary<string, object> settingsDict)) {
                            continue;
                        }

                        if (everestModule.GetPropertyValue("_Settings") is not { } settingsInstance) {
                            continue;
                        }

                        foreach (string propertyName in settingsDict.Keys) {
                            settingsInstance.SetPropertyValue(propertyName, settingsDict[propertyName].DeepCloneShared());
                        }
                    }
                }
            }
        );
    }

    private static void ExternalAction() {
        InternalSafeAdd(
            saveState: (_, level) => {
                IgnoreSaveLoadComponent.ReAddAll(level);
                EndPoint.AllReadyForTime();
            },
            loadState: (_, level) => {
                RoomTimerManager.ResetTime();
                DeathStatisticsManager.Clear();
                IgnoreSaveLoadComponent.ReAddAll(level);
                EndPoint.AllReadyForTime();
            },
            clearState: () => {
                RoomTimerManager.ClearPbTimes(!StateManager.Instance.ClearBeforeSave);
                DeepClonerUtils.ClearSharedDeepCloneState();
                DynDataUtils.ClearCached();
            },
            beforeSaveState: level => {
                RoomTimerManager.ClearPbTimes(false);
                DeepClonerUtils.ClearSharedDeepCloneState();
                DynDataUtils.ClearCached();

                // 实验性: 给"纯装饰"实体挂 IgnoreSaveLoadComponent, 让下面的 RemoveAll 把它们摘出去,
                // 从而不进快照 (读档后由 ReAddAll 放回)。默认关闭。
                DecoIgnoreUtils.Mark(level);

                IgnoreSaveLoadComponent.RemoveAll(level);
                ClearBeforeSaveComponent.RemoveAll(level);

                // 冲刺残影方向错误，干脆移除
                // TODO: 正确 SL 残影
                TrailManager.Clear();

                if (ModUtils.IsInstalled("CelesteNet.Client")) {
                    Type ghostEmoteWheelType = ModUtils.GetType("CelesteNet.Client", "Celeste.Mod.CelesteNet.Client.Entities.GhostEmoteWheel");
                    IEnumerable<Entity> entities =
                        level.Entities.Where(entity => entity.GetType().FullName?.StartsWith("Celeste.Mod.CelesteNet.") == true);
                    foreach (Entity entity in entities) {
                        if (ghostEmoteWheelType != null && entity.GetType() == ghostEmoteWheelType && entity.GetFieldValue<bool>("timeRateSet")) {
                            // Normally GhostEmoteWheel in CelesteNet does this when it gets closed again
                            Engine.TimeRate = 1f;
                            ghostEmoteWheelType = null;
                        }

                        entity.RemoveSelf();
                    }
                }
            },
            beforeLoadState: IgnoreSaveLoadComponent.RemoveAll
        );
    }

    private static void SupportModSessionAndSaveData() {
        InternalSafeAdd(
            (savedValues, _) => {
                foreach (EverestModule module in Everest.Modules.Where(module => module.GetType().Name != "NullModule")) {
                    savedValues[module.GetType()] = new Dictionary<string, object> {
                        {"_Session", module._Session},
                        {"_SaveData", module._SaveData},
                    }.DeepCloneShared();
                }
            },
            (savedValues, _) => {
                // 性能优化: 只克隆要写回的两个值, 不再克隆整本字典
                foreach (EverestModule module in Everest.Modules.Where(module => module.GetType().Name != "NullModule")) {
                    if (savedValues.TryGetValue(module.GetType(), out Dictionary<string, object> dictionary)) {
                        module._Session = dictionary["_Session"]?.DeepCloneShared() as EverestModuleSession;
                        module._SaveData = dictionary["_SaveData"]?.DeepCloneShared() as EverestModuleSaveData;
                    }
                }
            }
        );
    }

    private static void SupportAudioMusic() {
        InternalSafeAdd(
            (savedValues, level) => {
                Dictionary<string, object> saved = new() {
                    {"currentMusicEvent", Audio.GetEventName(Audio.CurrentMusicEventInstance)},
                    {"currentMusicEventParameters", Audio.CurrentMusicEventInstance.GetSavedParameterValues()},
                    {"CurrentAmbienceEventInstance", Audio.GetEventName(Audio.CurrentAmbienceEventInstance)},
                    {"CurrentAmbienceEventInstanceParameters", Audio.CurrentAmbienceEventInstance.GetSavedParameterValues()},
                    {"currentAltMusicEvent", Audio.GetEventName(Audio.currentAltMusicEvent)},
                    {"currentAltMusicEventParameters", Audio.currentAltMusicEvent.GetSavedParameterValues()},
                    {"MusicUnderwater", Audio.MusicUnderwater},
                    {"PauseMusic", Audio.PauseMusic},
                    {"PauseGameplaySfx", Audio.PauseGameplaySfx},
                };

                // EndPauseEffects() 之后立即读取该值依然为 true，所以需要加上这个判断
                if (level.Session.GetFlag("SpeedrunTool_Reset_unpauseTimer")) {
                    saved["PauseMusic"] = false;
                    saved["PauseGameplaySfx"] = false;
                }

                savedValues[typeof(Audio)] = saved.DeepCloneShared();
            },
            (savedValues, level) => {
                Dictionary<string, object> saved = savedValues[typeof(Audio)];

                Audio.SetMusic(saved["currentMusicEvent"] as string);
                Audio.CurrentMusicEventInstance?.CopyParametersFrom(saved["currentMusicEventParameters"] as ConcurrentDictionary<string, float>);

                Audio.SetAmbience(saved["CurrentAmbienceEventInstance"] as string);
                Audio.CurrentAmbienceEventInstance?.CopyParametersFrom(
                    saved["CurrentAmbienceEventInstanceParameters"] as ConcurrentDictionary<string, float>);

                Audio.SetAltMusic(saved["currentAltMusicEvent"] as string);
                Audio.currentAltMusicEvent?.CopyParametersFrom(saved["currentAltMusicEventParameters"] as ConcurrentDictionary<string, float>);

                Audio.MusicUnderwater = (bool)saved["MusicUnderwater"];
                Audio.PauseMusic = (bool)saved["PauseMusic"];
                Audio.PauseGameplaySfx = (bool)saved["PauseGameplaySfx"];
                if (!level.Paused && Level._PauseSnapshot != null) {
                    Audio.ReleaseSnapshot(Level._PauseSnapshot);
                    Level.PauseSnapshot = null;
                }
            }
        );
    }

    // fix: 5A黑暗房间读档后灯光问题
    private static void FixVertexLight() {
        InternalSafeAdd(loadState: (_, level) => {
            VertexLight[] lights = level.Lighting.lights;
            for (int i = 0; i < lights.Length; i++) {
                if (lights[i] is { } light) {
                    light.Index = -1;
                    lights[i] = null;
                }
            }
        });
    }

    private static void ReleaseEventInstances() {
        InternalSafeAdd(
            clearState: () => {
                foreach (EventInstance eventInstance in ClonedEventInstancesWhenSave.Union(ClonedEventInstancesWhenPreClone)) {
                    Audio.ReleaseSnapshot(eventInstance);
                }

                ClonedEventInstancesWhenSave.Clear();
                ClonedEventInstancesWhenPreClone.Clear();
            },
            preCloneEntities: () => ClonedEventInstancesWhenPreClone.Clear()
        );
    }
}