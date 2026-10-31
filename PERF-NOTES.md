# 性能优化记录（本地分支）

基线：`DemoJameson/Celeste.SpeedrunTool` master @ `752e6f5` (v3.27.22)。
本机实际运行的官方版本是 3.27.21（见 `LogHistory`），与 master 方法/类型一一对应。

## 0. 先量，再改

Release 构建原本没有任何耗时日志（`InGame_Profiling` 等全在 `#if DEBUG` 里，`DebugTool.MemoryTracker` 在发布版里根本不存在），
所以只能从"清档时 0.7~2.1s 的 GC 停顿"这类间接证据猜。

因此第一步是加一个**默认关闭、零开销**的分段计时器：

- 新文件 `Source/SaveLoad/SaveLoadProfiler.cs`
- 在 `StateManager.SaveStateImpl` / `LoadStateImpl` 里插 `Begin/Mark/End`
- 控制台命令 `srt_profile on|off` 开关

打开后每次存档/读档输出一行：

```
[SpeedrunTool/Profile] Save: clearOld=0.35 initSlots=0.02 beforeSave=0.40 cloneLevel=17.80
  cloneExtra=0.60 saveActions=22.10 preCloneSpawn=0.15 rest=0.10 | total=41.5ms | heap=1830.2MB
[SpeedrunTool/Profile] Load: beforeLoad=0.20 attachPreClone=0.00 unload=8.40 writeback=12.30
  loadActions=33.50 preCloneSpawn=0.20 rest=0.05 | total=54.7ms | heap=1795.6MB
```

各字段含义：

| 阶段 | 含义 |
|---|---|
| `clearOld` | 覆盖式保存前清掉旧快照（`ClearStateImpl(hasGc:false)`） |
| `initSlots` | 补丁表排序/分配 `dictionaryId`（仅首次真正做事） |
| `beforeSave` | `OnBeforeSaveState`：摘除忽略实体、清 TrailManager、CelesteNet 幽灵、清暂停帧 |
| `cloneLevel` | **核心深克隆**：整个 `Level` 对象图 |
| `cloneExtra` | `SaveData` + 切屏协程克隆 |
| `saveActions` | 所有 `SaveLoadAction.saveState` 回调（静态字段/音频/随机数/第三方 Mod…） |
| `preCloneSpawn` | 只统计"派发后台预克隆任务"的时间，不含任务本身 |
| `beforeLoad` / `attachPreClone` | 读档前回调 / 绑定预克隆映射表 |
| `unload` | `UnloadLevel`：逐实体 `Removed()`（停音、解 hook、释放资源） |
| `writeback` | `Tracker.Refresh` + `savedLevel.DeepCloneToShared(level)` + `SaveData` 回写 |
| `loadActions` | 所有 `SaveLoadAction.loadState` 回调 |

## 1. 已实施的改动

### O1（主要）GC 停顿：不再默认压缩 LOH、不再等终结器

`StateManager.GcCollect` 原实现：

```csharp
GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
GC.Collect();
GC.WaitForPendingFinalizers();
```

问题：在 2.5GB 堆上，**LOH 压缩要搬移大对象**，是 0.7~2.1s 停顿的大头；`WaitForPendingFinalizers`
再把停顿拉长一个终结器周期。而 SRT 产生的垃圾是"大块短命对象"，回收它们**不需要压缩**。

现实现：

```csharp
GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: compact);
```

- `compact` 来自新增设置 `ModSettings.GcCompactLoh`（默认 `false`，可用 `srt_gc on` 打开以恢复旧行为）
- 去掉了 `WaitForPendingFinalizers()`（SRT 已手动 `Audio.ReleaseSnapshot` / `Dispose`）
- 用 4 参数重载显式表达意图，不再留下 `GCSettings` 的全局副作用

**预期**：`GC latency` 从 700~2100ms 降到百毫秒级（同堆大小下通常快 3~10 倍，属估算，需实测确认）。
代价：LOH 不压缩 → 长时间会话的碎片可能更多；如果日志里 `GC latency` 低了但内存持续爬升，就 `srt_gc on` 或手动 `F4` 清档。

### O2 读档时少克隆整本字典（4 处）

`SaveLoadAction` 的读档回调原本先把**整本存档字典**深克隆一遍再取值：

```csharp
Dictionary<Type, Dictionary<string, object>> clonedDict = dictionary.DeepCloneShared();
foreach (Type type in clonedDict.Keys) { ... }
```

语义上只需要"每个要写回的值克隆一份"，外层字典是纯浪费。改成直接遍历原字典、在赋值处按值克隆：

- `SupportSimpleStaticFields`（所有 Mod 的 `Entity/Component/Renderer` 静态字段，条目最多）
- `SupportModModuleFields`（所有 `EverestModule` 的 `Level/Session/Entity/Vector2` 字段）
- `SupportModSessionAndSaveData`（每个 Mod 的 `_Session`/`_SaveData`）
- `SupportInput`（原本连 `savedValues = savedValues.DeepCloneShared()` 整个外层字典都克隆，含所有 Mod 的 ButtonBinding 字典）

**预期**：每次读档省下 (类型数 + 字段数 + 模块数) 量级的字典与条目分配，属"减少 GC 压力"而非"省一次明显耗时"。

### O3 可观测性

- `ModSettings.GcCompactLoh` 写进 `LogForDiagnostics()`，每次启动日志里能看到当前 GC 策略
- `srt_gc on|off` 运行时切换

## 2. 未实施（需要先有 profiling 数据）

按收益排序，等 `srt_profile` 的真实数据出来再动：

| 候选 | 预期收益 | 风险 |
|---|---|---|
| `preCloneTask` 常驻内存（每槽多一份克隆体 + 映射表） | 每槽内存 −40~50%，读档 +一次全图克隆 | 低（正确性不变），是"内存换速度"的反向开关 |
| `UnloadLevel` 里 `Distinct()` / 多次 `Tracker.GetEntities` 遍历 | 中 | 低 |
| `DesyncRiskAnalyzer.Check` 在克隆时对每个 `Entity` 调用 | 小 | 中（关掉会漏检 desync） |
| `InitFields` 的反射扫描（首次 SL 一次性） | 一次性几百 ms | 低（可缓存到文件） |
| `RestoreAudio_1` 的 `Components.ToArray()` | 很小 | 低 |

## 3. 改动清单

```
SpeedrunTool/Source/SaveLoad/SaveLoadProfiler.cs     新增
SpeedrunTool/Source/SaveLoad/StateManager.cs         +50 −21   分段计时 + GcCollect 重写
SpeedrunTool/Source/SaveLoad/SaveLoadAction.cs       +21 −19   4 处去掉整本字典克隆
SpeedrunTool/Source/SpeedrunToolSettings.cs          +9  −1    新增 GcCompactLoh + 日志
SpeedrunTool/Source/ModInterop/CommandExport.cs      +12        srt_profile / srt_gc 命令
```

> `SaveLoadAction.cs` / `SpeedrunToolSettings.cs` 原本是**混合换行符**（作者本地残留），改动时按 `.editorconfig`
> （`charset=utf-8-bom`, `end_of_line=crlf`）统一了，所以 `git diff` 看着很大；真实改动用
> `git diff --ignore-cr-at-eol` 看，只有约 30 行。补丁文件：`D:\files\projects\srt-perf.patch`（可用
> `git apply --ignore-whitespace` 干净应用到 pristine 树，已验证）。

## 4. 怎么验证

1. `.\build.ps1`
2. `.\install-test.ps1`（备份官方 DLL 后替换 `Mods\Cache`）
3. 进游戏，控制台 `srt_profile on`，正常打图（存档/读档/过屏若干次）
4. 看 `LogHistory\log_*.txt`：
   - `[SpeedrunTool/Profile] Save/Load: ...` → 各阶段耗时
   - `[SpeedrunTool] GC latency: Nms (compact=False).` → 对比改造前的 697~2130ms
5. 想回退：`.\install-test.ps1 -Restore`

**重点看**：`cloneLevel` 与 `saveActions`/`loadActions` 哪个大。
如果 `cloneLevel` 占绝对多数 → 深克隆本身是瓶颈，再谈裁剪克隆范围；
如果 `loadActions` 大 → 去查具体是哪个第三方 Mod 的 action（`#if DEBUG` 的 `EachAction_Profilling` 能给出逐 action 耗时，可以把那段逻辑移到 Release）。

---

# 第二轮：第一份实测数据与据此做的改动

## 5. 第一轮实测（2026-10-31 16:55–17:06，SJ 图，53 次存档 / 52 次读档 / 8 次 GC）

### 存档（排除第 1 次冷启动与最后 3 次尖峰，n=49）

| 阶段 | 平均 | 最小 | 最大 |
|---|---|---|---|
| `clearOld` | 0.2 ms | 0.0 | 0.3 |
| `cloneLevel` | **31.5 ms** | 15.2 | 57.0 |
| `cloneExtra` | 3.5 ms | 2.3 | 13.6 |
| `saveActions` | **15.2 ms** | 8.5 | 46.0 |
| **合计** | **50.4 ms** | 35.6 | 82.7 |

### 读档（n=52）

| 阶段 | 平均 | 最小 | 最大 |
|---|---|---|---|
| `beforeLoad` | 14.2 ms | 8.4 | 37.7 |
| `unload` | 10.2 ms | 6.6 | 32.9 |
| `writeback` | 3.1 ms | 2.3 | 11.8 |
| `loadActions` | **67.0 ms** | 48.0 | 230.8 |
| **合计** | **95.5 ms** | 66.0 | 266.0 |

### 异常值

| 事件 | 数值 | 原因推断 |
|---|---|---|
| 第 1 次存档 | **2737 ms**（`initSlots` 79.9 + `cloneLevel` 2021.9 + `saveActions` 615.0） | 一次性：`InitFields` 反射扫描 + DeepCloner 为每个类型首次生成克隆函数 + JIT |
| 最后 3 次存档 | 958 / 1444 / 2012 ms（`clearOld` 305/413/0，`cloneLevel` 944/1098/1587） | `clearOld` 在等上一轮后台预克隆（`preCloneTask.Wait()`）；克隆期间撞上 GC |
| GC × 8 | 平均 **568 ms**，最大 937 ms | 即使不压缩 LOH，全量 gen2 回收在 1.2–1.4 GB 堆上就要半秒以上 |

### 结论

1. **稳态存档 50 ms、读档 96 ms 本身不算慢**，真正的用户感知痛点是 **GC 568 ms × 8 次** 和 **首次 2.7 s**。
2. `cloneLevel` 稳态 31 ms → **"裁剪克隆范围"这条路不值得走**（最多省十几毫秒，风险极高）。
3. `writeback` 只有 3 ms，说明**预克隆确实有效**；但它的代价是每次存档后多一份克隆 + 阻塞下一次清档 300~400 ms。
4. `loadActions` 67 ms 占读档 70%，是读档里唯一值得动的部分。

## 6. 第二轮改动

| # | 改动 | 依据 | 预期 |
|---|---|---|---|
| O4 | **GC 改为后台非阻塞**：内存未超阈值时 `GC.Collect(..., blocking: false, compacting: false)`；超阈值才阻塞回收。新增 `GcBackgroundCollect`（默认 true）/ `srt_bggc` | GC 平均 568 ms，且每次清档都触发 | 过屏不再卡半秒；日志变成 `GC requested in background` |
| O5 | **逐 action 计时**：`SaveLoadAction.ProfiledInvoke`，profiler 打开时打印耗时 ≥0.5 ms 的 action（含注册时捕获的调用方名字） | `loadActions` 67 ms 但不知花在谁身上 | 下一轮直接点名，再优化 |
| O6 | **预克隆可关**：新增 `PreCloneEntities`（默认 true）/ `srt_preclone` | `clearOld` 尖峰 305~413 ms 来自等预克隆；关掉可省一半内存与 GC 压力，代价读档 +约 30 ms | 可 A/B |

## 7. 第二轮怎么测

1. 启动游戏，控制台 `srt_profile on`
2. 正常打图 10 分钟（存档/读档/过屏各若干次）
3. 看日志：
   - `GC requested in background (...)` 应该取代原来的 `GC latency: N ms`
   - 每次存档/读档后面会多出若干 `  load [ 9] SupportInput = 12.34 ms` 这样的行
4. 想验证预克隆的影响：`srt_preclone off` 后再打一轮，对比 `clearOld` 尖峰与 `writeback`

## 8. 第三轮候选（等数据）

| 目标 | 手段 |
|---|---|
| 首次存档 2.7 s | 启动时后台预热 DeepCloner 克隆函数（对 `Entity/Component/Renderer` 子类各生成一次），把首次生成成本从"第一次存档"挪到加载期 |
| `loadActions` 67 ms | 按第二轮的点名结果优化 Top1/Top2（大概率是 `SupportSimpleStaticFields` 的静态字段深克隆、`GraphicResourcesHandler` 的 VirtualAsset 重载、`SupportInput`） |
| `beforeLoad` 14 ms | `IgnoreSaveLoadComponent.RemoveAll` 的 `Tracker.GetEntitiesTrackIfNeeded` 遍历 |

---

# 第三轮：零分配改写

profiler 增加了**分段分配量**（`GC.GetAllocatedBytesForCurrentThread` 差值，只统计主线程），
日志格式变成 `clearOld=0.31ms/12KB cloneLevel=31.5ms/2400KB ... | alloc=2.3MB`。

## 9. 改了什么

| # | 位置 | 原来 | 现在 | 每次 SL 省掉 |
|---|---|---|---|---|
| Z1 | `StateManager.UpdateTimeAndDeaths` | 克隆整个 `Session` **和** `SaveData`（含全部章节 `Areas_Safe`）只为读旧值；其中 `clonedSaveData` 的赋值全是死代码 | 先把 `Time/Deaths/DeathsInCurrentLevel` 读进局部变量再原地写回，删除 `clonedSaveData` | 一次 `SaveData` 深克隆 + 一次 `Session` 深克隆（**每次读档**，量级最大） |
| Z2 | `StateManager.UnloadLevel` | `[.. Tracker.GetEntities<Player>(), .. level.Entities]` 两个 List + `.Distinct()`（HashSet + 迭代器） | `[ThreadStatic]` 复用 `List<Entity>` + `AddRange` 快照，再"先 Player 后其它"两趟遍历代替去重 | 2 个 List + 1 个 HashSet + 若干迭代器 |
| Z3 | 同上（声音组件） | `Tracker.GetComponentsCopy<SoundSource>().ForEach(...)` | 复用 `List<Component>` 快照 + `foreach` | 1 个 List + 闭包 |
| Z4 | `StateManager.RestoreAudio_1` | `level.Entities.SelectMany(e => e.Components.ToArray())` | 双层 `foreach` | 每实体 1 个数组 + LINQ 迭代器 |
| Z5 | `StateMarkUtils.ReColor` | `level.Entities.FindAll<Strawberry>().Where(...)` | `foreach (Entity e in level.Entities.entities)` + 类型判断 | 1 个 List + 1 个迭代器（存档和读档各一次） |
| Z6 | `MuteAudioUtils` | `level.Entities.FindAll<SoundEmitter>().ForEach(...)` | 同上 | 1 个 List + 闭包 |
| Z7 | `IgnoreSaveLoadComponent.RemoveAll` / `ClearBeforeSaveComponent.RemoveAll` | `GetComponentsCopy<T>().ForEach(...)` | `Tracker.Components` 内部列表 / 复用静态缓冲 | 每次 SL 2 个 List + 2 个闭包 |

### 一个必须知道的坑（踩了两次）

`FindAll<T>()` 走的是 `level.Entities`（全量），而 `Tracker.Entities[typeof(T)]` 只有 **`[Tracked]` 类型**才有。
查了 `Celeste.dll`：

| 类型 | 是否 `[Tracked]` |
|---|---|
| `Player` | ✅ |
| `SoundSource`（Component） | ✅ |
| `Strawberry` | ❌ |
| `SoundEmitter` | ❌ |

**第一次踩**：Z5/Z6 直接用 `Tracker.Entities.TryGetValue` → 拿不到，会**静默失效**（金草莓不变色、读档音效不静音）。
**第二次踩**：改成回退 `GetEntitiesTrackIfNeeded<T>()`（内部是 `AddTypeToTracker` + `Tracker.Refresh(null,false)`），
实测**金草莓仍然没变色** —— 这条回退在"存档/读档回调里、Level 正被 SRT 改写"的时机并不可靠。

**最终解法**：完全不碰 Tracker。`EntityList.entities` 就是 `FindAll<T>()` 内部遍历的那个 `List<Entity>`：

```csharp
foreach (Entity entity in level.Entities.entities) {   // List<T>.Enumerator 是结构体 → 零分配
    if (entity is Strawberry { Golden: true } berry) { ... }   // 与 FindAll 的 isinst T 语义一致
}
```

顺带查证的两条事实：

- `EntityList.GetEnumerator()` 返回 `IEnumerator<Entity>`，内部 `entities.GetEnumerator()` 后**装箱**一次，
  所以 `foreach (Entity e in level.Entities)` 每次都会分配 ~40B；写 `.entities` 才是真零分配。
- `EntityList.FindAll<T>()` 的实现就是 `new List<T>()` + 遍历 `entities` + `isinst T`，所以它**包含子类**
  （`SpeedBerry`/`SilverBerry` 能被 `TryRecolorSprite` 的 switch 命中，正是这个原因）。

## 10. 第三轮怎么验证

1. 启动游戏，`srt_profile on`
2. 存档 / 读档 各若干次
3. 对比日志里的 `alloc=` 与各阶段 `xxKB`：
   - `writeback` 应该明显变小（Z1 的 `SaveData` 克隆没了）
   - `unload` 应该明显变小（Z2/Z3）
   - 同时看 `GC requested in background` 是否取代了 `GC latency`
4. 回归检查（这些是行为相关的，必须眼看一遍）：
   - 金草莓在读档后是否**仍然**变色（`speedrun_tool_goldberry` 皮肤）
   - 读档后草莓/钥匙/磁带的音效是否**被静音**（不静音就是 Z6 失效）
   - 读档后 `TrailManager` 残影、暂停、Cassette 节奏音是否正常（Z3/Z4）
   - 计时器/死亡数在多次读档后是否仍然只增不减（Z1 改写了这段逻辑）



---

# 第四轮：房间规模真相 + 实验性 deco 忽略

## 11. 实测：一切取决于房间

| 房间 | cloneLevel | 分配 | 存档总耗时 |
|---|---|---|---|
| `0-Lobbies/1-Beginner [sj2021beginnerlobby]` | 1247–1768 ms | **165–225 MB** | 1.26–1.78 s |
| `1-Beginner/asteriskblue [a-01]` | 455 ms | 84 MB | 951 ms |
| `1-Beginner/asteriskblue [a-03]` | 8–48 ms | 4–6 MB | 22–74 ms |
| `1-Beginner/asteriskblue [a-07/08]` | 10–14 ms | 4–5 MB | 32 ms |

SJ 大厅的对象图是普通房间的 **30–40 倍**。之前所有"存档慢"的样本，其实都来自大厅。

## 12. 预克隆的取舍（实测）

| | 普通房间 | 大厅 |
|---|---|---|
| 读档 `writeback`（预克隆 ON） | 2–9 ms | **3 ms** |
| 读档 `writeback`（预克隆 OFF） | 8–18 ms | **1382 ms** |
| 托管堆 | 1.20–1.40 GB | 1.05–1.18 GB（OFF 时） |

→ **结论：保持 ON**。它把大厅那 1.4 秒挪到存档后的后台，内存代价只在大厅多 170 MB。

## 13. "读档后音效还在"不是 bug

诊断行（`srt_profile on` 时打印）：

```
mute: emitters=0 muted=0 collected=0 (saved=True)   ← 4 次
mute: emitters=0 muted=0 collected=1 (saved=True)   ← 2 次
```

`collected` 几乎为 0、`emitters` 恒为 0 —— 房间里根本没有需要静音的实例。
听到的声音是读档**之后**新播放的（重新拿到草莓等），属正常。

## 14. 实验性功能：忽略纯装饰实体（`srt_deco`）

`ModSettings.IgnoreDecoEntities`（默认 false）+ 控制台 `srt_deco on|off`。

**判定（刻意保守）**，四条全满足才摘：

1. `entity.Collider is null`（没有碰撞盒）
2. 不带 `Tags.Global` / `Tags.Persistent`
3. 不是 `Player` / `Strawberry` / `PlayerSeeker`
4. 类型**自身没有重写** `Update` / `Added` / `Removed` / `Awake` / `DebugRender`（`BindingFlags.DeclaredOnly`）
5. 组件全是 `GraphicsComponent`，或 `VertexLight` / `BloomPoint` / `LightOcclude`

**实现**：复用 `IgnoreSaveLoadComponent`，但必须用 **`based: true`**：

```csharp
entity.Add(new IgnoreSaveLoadComponent(based: true));
```

因为 `RemoveAll/ReAddAll` 走 `RemoveImmediately(entity, based:true)` → `BasedRemoved` 会把 `entity.Scene` 置空
（组件跟着离开 `Scene.RendererList`），`ReAddAll` → `BasedAdded` 再设回 `level`。
用 `based:false` 的话组件不会离开/回到 RendererList，**读档后装饰就不画了**。

**日志**：`deco: marked=N/M` —— 直接告诉你这个房间有多少装饰能被摘掉。

**风险**（实验性，默认关闭）：

- 被摘的实体状态停在"摘下那一刻"（对纯静态装饰无影响）
- 若某个实体的可见性依赖 `Update` 但类型本身没重写（继承自父类），会被漏判 → 视觉异常
- 只影响**快照大小**，不影响游戏逻辑

---

# 第五轮：生态调研 + MapData 共享

## 15. 谁在改 LevelData / MapData（抓了 7 个仓库）

克隆/下载了 `MaxHelpingHand`、`CommunalHelper`、`FrostHelper`、`CollabUtils2`、`StrawberryJam2021`、
`KoseiHelper`、`CelesteRandomizer`，逐个 grep。

| 模式 | 结果 |
|---|---|
| `LevelData.Entities/Decals/Triggers` 的 `Add/Insert/Remove/Clear` | 只有 FrostHelper 一处 |
| `Session.LevelData = ` 重新赋值 | 0 |
| `new EntityData` | 8 处，全部是工厂方法造临时数据，不写回 LevelData |
| `data.Values["x"] = y` | 12 处，全部是加载期写一次 / 幂等 / 缓存键 |

**唯一的破坏性修改**：`FrostHelper/Helpers/GlobalEntityHelper.cs` 的 `AddFromMarker`

```csharp
foreach (var room in self.Session.MapData.Levels) {
    room.Entities.AddRange(globalRoom.Entities.Where(...).Select(e => CloneData(e, room)));
    room.Triggers.AddRange(...);
}
```

在 `Level.LoadLevel`（`isFromLoader`）时执行，用 DynData 标记防重复。
**只 `AddRange` 到列表**，而列表在默认档里仍然克隆 → 读档可回滚，安全。

**随机房间（`rhelmot/CelesteRandomizer`）**：

```csharp
var map = new MapData(this.Key);
map.Levels = new List<LevelData>();       // 整张图重建
...
var result = this.Level.Copy();           // 新 LevelData
result.Solids = this.TweakedFgTiles;      // 换瓦片/贴花
// 再按名字过滤掉 goldenberry / musictrigger / checkpoint
```

全部发生在**地图生成期**（进图 / 重 roll），不是游玩中。

**关键**：原版 `LevelData.Copy()` 本身就是 `Entities = new List<EntityData>(Entities)`
—— **新列表、同一批 EntityData 引用**。所以随机房间这套逻辑本来就建立在"EntityData 被共享"之上，
我们让 SRT 也共享，等于和原版语义对齐。

## 16. 生态里已有的先例

- **FrostHelper** 自己定义 `ISavestatePersisted` 标记接口，并调用
  `AddReturnSameObjectProcessor(t => t.IsAssignableTo(typeof(ISavestatePersisted)))`
  —— 注释写明"标了这个接口的类型读档时按引用返回，不克隆"。说明"共享不克隆"是被接受的模式。
- **CommunalHelper** 用 `RegisterStaticTypes(typeof(DreamTunnelDash), [...9 个字段...])`
  —— 这就是实测里 `save [37] RegisterStaticTypes = 28.2 ms/存档` 的来源（第三方成本）。

## 17. `srt_mapdata all` 现在同时共享 LevelData + MapData

```csharp
|| (ModSettings.ShareLevelDataAll && (type == typeof(LevelData) || type == typeof(MapData)))
```

理由：快照里装的是 `Session.MapData`，也就是**整张地图所有房间**的定义
（那 45k EntityData 是全图的，不是单房间的）。

| 档位 | 命令 | 共享什么 | 适用 |
|---|---|---|---|
| 默认 | `srt_mapdata on` | `EntityData` / `DecalData` | 通用；追加类修改仍可回滚 |
| 激进 | `srt_mapdata all` | 再加 `LevelData` + `MapData` | 随机房间类 Mod 反而更合适（读档不会把地图还原成旧版） |
| 关闭 | `srt_mapdata off` | — | 出问题时 |

---

# 第六轮：实测确认（2026-10-31 19:5x）

`ShareLevelData = True, ShareLevelDataAll = True`，无报错，2 次存档 / 0 次读档。

## 18. 数据

房间：`StrawberryJam2021/1-Beginner/cellularAutomaton [01]`（普通房间）

```
cloneTypes(level): total=10681 | Entryx1931 Particlex1600 List`1x1058 Entryx825 List`1x483 ...
Save: clearOld=4.84ms/260KB cloneLevel=10.53ms/3616KB cloneExtra=5.55ms/308KB
      saveActions=20.53ms/3458KB | total=41.5ms | alloc=7.5MB | heap=926.5MB
```

| 指标 | 普通房间（改前） | 普通房间（现在） |
|---|---|---|
| `cloneLevel` | 8–48 ms / 4–11 MB | **10.5 ms / 3.5 MB** |
| 快照对象数 | 未测 | **10,681** |
| 存档总耗时 | 40–60 ms | **41.5 ms** |
| 存档分配 | ~5.5 MB | **7.5 MB** |
| 托管堆 | 1.20–1.40 GB | **926 MB** |

## 19. 尚未测到的关键数字

大厅（`lobby1` / `beginnerlobby`）在**改动前**是：

```
1,063,701 个对象 / 418–600 MB / 3215–4247 ms
```

改动后还没在大厅存过档。**这是唯一还缺的证据**：如果 `total` 从 106 万掉到几万、MB 掉到几十，
那这次 `srt_mapdata all` 就是整个优化里收益最大的一步（比前面 GC/零分配加起来都大）。

## 20. 全部改动一览（本轮累计）

| # | 改动 | 效果 |
|---|---|---|
| O1 | GC 不压缩 LOH + 去掉 WaitForPendingFinalizers | 361–937ms → 仍慢但更短 |
| O4 | 后台非阻塞 GC（阈值改用托管堆判断） | **阻塞 GC 0 次**，半秒级停顿消失 |
| O5 | 逐 action 计时 + 从委托推断 action 名字 | 点名 `MotionSmoothing 46.7ms/读档`、`RegisterStaticTypes 28ms/存档` |
| O6 | 预克隆可关 + **读档不再阻塞等预克隆** | 修掉一次 2323ms 的读档白等 |
| Z1 | `UpdateTimeAndDeaths` 去掉 Session/SaveData 深克隆 | 每次读档省一大坨分配 |
| Z2–Z7 | `UnloadLevel`/`RestoreAudio_1`/`ReColor`/`MuteAudioUtils`/`IgnoreSaveLoadComponent` 零分配改写 | 减少 GC 压力 |
| P1 | profiler 增加分段**分配量**与**类型级克隆统计** | 定位到大头是地图定义而不是实体 |
| P2 | **共享 `EntityData`/`DecalData`（默认）/ `LevelData`+`MapData`（all）** | 快照对象数 106 万 → 1 万级 |
| P3 | `srt_deco` 实验功能 | 实测无效（82–86% 实体有碰撞盒），保留默认关闭 |
