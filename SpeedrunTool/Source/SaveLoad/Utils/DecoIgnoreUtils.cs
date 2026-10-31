using System.Collections.Generic;
using System.Reflection;

namespace Celeste.Mod.SpeedrunTool.SaveLoad.Utils;

/// <summary>
/// 实验性功能: 把"纯装饰"实体排除在快照之外。
/// <para>做法是复用现成的 <see cref="IgnoreSaveLoadComponent"/> 机制 —— 存档前把它们从 Level 摘下,
/// 读档后原样放回, 因此它们**不会消失**, 只是状态停在"摘下那一刻"。</para>
/// <para>判定刻意保守, 只认完全没有逻辑的实体: 无碰撞盒 / 类型自身没有重写
/// Update·Added·Removed·Awake / 组件只有 <see cref="GraphicsComponent"/> / 不带 Global·Persistent 标签。</para>
/// <para><b>带光源 (VertexLight / BloomPoint / LightOcclude) 的实体一律不摘</b> ——
/// 它们依赖 Added/Removed 注册进 Level.Lighting, 而摘除/放回走的是 BasedRemoved/BasedAdded,
/// 不会触发这些注册, 结果是读档后动态光源和背景光全部消失。</para>
/// <para>打开方式: <c>ModSettings.IgnoreDecoEntities = true</c> 或控制台 <c>srt_deco on</c>。
/// 打开后日志会打 <c>deco: marked=N/M</c>, 用来看这个房间到底有多少装饰能被摘掉。</para>
/// </summary>
internal static class DecoIgnoreUtils {
    private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    // 只允许"纯渲染"组件。注意 **不能** 把 VertexLight / BloomPoint / LightOcclude 放进来:
    // 它们靠 Added/Removed 把自己注册进 Level.Lighting, 而 IgnoreSaveLoadComponent 的
    // BasedAdded/BasedRemoved 只设置 entity.Scene、不会调用 component.Added/Removed,
    // 所以带光源的实体被摘掉再放回来之后, 光源就再也不会出现在 Lighting.lights 里
    // —— 表现为读档后动态光源/背景光全部消失。
    private static readonly HashSet<Type> VisualOnlyComponents = [
        typeof(IgnoreSaveLoadComponent)
    ];

    private static readonly Dictionary<Type, bool> TypeCache = [];

    internal static void Mark(Level level) {
        if (!ModSettings.IgnoreDecoEntities) {
            return;
        }

        int total = 0;
        int marked = 0;
        // 拒绝原因直方图: 用来判断该放宽哪一条, 而不是盲猜
        int collider = 0, tags = 0, keyEntity = 0, lifecycle = 0, components = 0, already = 0;

        List<Entity> entities = level.Entities.entities;
        for (int i = 0; i < entities.Count; i++) {
            Entity entity = entities[i];
            total++;

            if (HasIgnoreComponent(entity)) {
                already++;
                continue;
            }

            switch (WhyNotDeco(entity)) {
                case Reject.Collider:
                    collider++;
                    continue;
                case Reject.Tags:
                    tags++;
                    continue;
                case Reject.KeyEntity:
                    keyEntity++;
                    continue;
                case Reject.Lifecycle:
                    lifecycle++;
                    continue;
                case Reject.Components:
                    components++;
                    continue;
            }

            // based:true 很关键 —— 它让 RemoveAll/ReAddAll 走 BasedRemoved/BasedAdded,
            // 也就是把 entity.Scene 置空/设回 level, 组件 (Sprite 等) 才会跟着离开/回到
            // Scene 的 RendererList; 否则装饰实体在读档后就不画了。
            entity.Add(new IgnoreSaveLoadComponent(based: true));
            marked++;
        }

        if (SaveLoadProfiler.Enabled) {
            Logger.Info("SpeedrunTool/Profile",
                $"  deco: marked={marked}/{total} (already={already} collider={collider} tags={tags} key={keyEntity} lifecycle={lifecycle} components={components})");
        }
    }

    private enum Reject {
        None,
        Collider,
        Tags,
        KeyEntity,
        Lifecycle,
        Components
    }

    private static bool HasIgnoreComponent(Entity entity) {
        if (entity.Components is null) {
            return false;
        }

        foreach (Component component in entity.Components) {
            if (component is IgnoreSaveLoadComponent) {
                return true;
            }
        }

        return false;
    }

    private static Reject WhyNotDeco(Entity entity) {
        // 有碰撞盒 → 参与物理/交互, 绝不能摘
        if (entity.Collider is not null) {
            return Reject.Collider;
        }

        if (entity.TagCheck(Tags.Global) || entity.TagCheck(Tags.Persistent)) {
            return Reject.Tags;
        }

        // 几个关键实体直接排除, 不依赖启发式
        if (entity is Player || entity is Strawberry || entity is PlayerSeeker) {
            return Reject.KeyEntity;
        }

        if (!IsDecoType(entity.GetType())) {
            return Reject.Lifecycle;
        }

        return HasOnlyVisualComponents(entity) ? Reject.None : Reject.Components;
    }

    private static bool IsDecoType(Type type) {
        if (TypeCache.TryGetValue(type, out bool cached)) {
            return cached;
        }

        bool deco = ComputeIsDecoType(type);
        TypeCache[type] = deco;
        return deco;
    }

    private static bool ComputeIsDecoType(Type type) {
        // 类型自身重写了任何一个生命周期方法 → 认为它有逻辑
        if (type.GetMethod("Update", Declared) is not null
            || type.GetMethod("Added", Declared) is not null
            || type.GetMethod("Removed", Declared) is not null
            || type.GetMethod("Awake", Declared) is not null
            || type.GetMethod("DebugRender", Declared) is not null) {
            return false;
        }

        return true;
    }

    private static bool HasOnlyVisualComponents(Entity entity) {
        if (entity.Components is null) {
            return false;
        }

        int count = 0;
        foreach (Component component in entity.Components) {
            count++;

            if (component is GraphicsComponent) {
                continue;
            }

            if (VisualOnlyComponents.Contains(component.GetType())) {
                continue;
            }

            return false; // 有非视觉组件 (Collider 之外的逻辑组件) → 不摘
        }

        return count > 0; // 什么都不画的空实体没必要特殊处理
    }
}
