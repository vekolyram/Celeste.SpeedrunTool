using FMOD;
using FMOD.Studio;
using System.Collections.Generic;

namespace Celeste.Mod.SpeedrunTool.SaveLoad;

internal static class MuteAudioUtils {
    private static readonly HashSet<string> RequireMuteAudioPaths = new() {
        "event:/game/general/strawberry_get",
        "event:/game/general/strawberry_laugh",
        "event:/game/general/strawberry_flyaway",
        "event:/game/general/seed_complete_main",
        "event:/game/general/key_get",
        "event:/game/general/cassette_get",
        "event:/game/05_mirror_temple/eyewall_destroy",
        "event:/char/badeline/boss_hug",
        "event:/char/badeline/boss_laser_fire",
    };

    private static readonly List<EventInstance> RequireMuteAudios = new();

    [Load]
    private static void Load() {
        On.FMOD.Studio.EventDescription.createInstance += EventDescriptionOnCreateInstance;
    }

    [Unload]
    private static void Unload() {
        On.FMOD.Studio.EventDescription.createInstance -= EventDescriptionOnCreateInstance;
    }

    private static RESULT EventDescriptionOnCreateInstance(On.FMOD.Studio.EventDescription.orig_createInstance orig, EventDescription self,
        out EventInstance instance) {
        RESULT result = orig(self, out instance);

        if (StateManager.Instance.IsSaved && instance != null && self.getPath(out string path) == RESULT.OK && path != null &&
            RequireMuteAudioPaths.Contains(path)) {
            RequireMuteAudios.Add(instance);
        }

        return result;
    }

    public static void AddAction() {
        SaveLoadAction.InternalSafeAdd(
            loadState: (_, level) => {
                // 零分配改写: FindAll<T>() + ForEach 会新建 List 和委托闭包。
                // 直接遍历 EntityList.entities (结构体枚举器, 零分配), 语义与 FindAll 一致。
                // 不能用 Tracker: SoundEmitter 不是 [Tracked] 类型。
                int emitters = 0;
                int emitterMuted = 0;
                foreach (Entity entity in level.Entities.entities) {
                    if (entity is SoundEmitter emitter) {
                        emitters++;
                        if (emitter.Source.instance is { } eventInstance) {
                            eventInstance.setVolume(0f);
                            emitterMuted++;
                        }
                    }
                }

                int collected = RequireMuteAudios.Count;
                foreach (EventInstance sfx in RequireMuteAudios) {
                    sfx.setVolume(0f);
                }

                RequireMuteAudios.Clear();

                // 只在开 profiler 时打一行, 用来诊断"读档后音效还在"
                if (SaveLoadProfiler.Enabled) {
                    Logger.Info("SpeedrunTool/Profile",
                        $"  mute: emitters={emitters} muted={emitterMuted} collected={collected} (saved={StateManager.Instance.IsSaved})");
                }
            }, clearState: () => RequireMuteAudios.Clear()
        );
    }
}