using System;
using Microsoft.Xna.Framework;

namespace Celeste.Mod.SpeedrunTool.SaveLoad;

/// <summary>
/// 读档后"冻结 + 读秒恢复"，动画仿 Phigros 的暂停恢复：
/// 画面压暗，一个白色圆从大收缩到中心，中间显示剩余秒数，收缩结束时解除冻结。
/// <para>由 <see cref="ModSettings.ResumeCountdown"/> 控制（单位 0.5 秒，0 = 关闭）。</para>
/// <para>和 <see cref="FreezeAfterLoadStateType"/> 的区别：那个是"一直冻结等你按键"，
/// 这个是"冻结固定时长后自动恢复"，适合需要一点反应时间的图。</para>
/// </summary>
internal static class ResumeCountdown {
    private static float remaining;
    private static float total;

    internal static bool Active => remaining > 0f;

    internal static float Seconds => Math.Max(0f, ModSettings.ResumeCountdown * 0.5f);

    [Load]
    private static void Load() {
        On.Celeste.Level.Render += Render;
        On.Celeste.Level.Update += LevelOnUpdate;
    }

    [Unload]
    private static void Unload() {
        On.Celeste.Level.Render -= Render;
        On.Celeste.Level.Update -= LevelOnUpdate;
    }

    internal static void Start(float seconds) {
        total = Math.Max(seconds, 0.001f);
        remaining = total;
    }

    internal static void Cancel() {
        remaining = 0f;
        total = 0f;
    }

    private static void LevelOnUpdate(On.Celeste.Level.orig_Update orig, Level self) {
        orig(self);

        if (remaining <= 0f) {
            return;
        }

        // 注意：冻结时 GameFreezeManager 会跳过 orig，但本 hook 在 orig 之后仍然执行，
        // 所以倒计时能正常推进。
        remaining -= Engine.RawDeltaTime;
        if (remaining > 0f) {
            return;
        }

        remaining = 0f;
        total = 0f;
        if (Engine.Scene is Level level) {
            StateManager.Instance.OutOfFreeze(level);
        }
    }

    private static void Render(On.Celeste.Level.orig_Render orig, Level self) {
        orig(self);

        if (remaining <= 0f) {
            return;
        }

        // 返祖版：只读数字，不画圆、不压暗（要动画再说）
        Draw.SpriteBatch.Begin();

        Vector2 center = new(Engine.Width / 2f, Engine.Height / 2f);
        string text = Math.Ceiling(remaining).ToString("0");

        ActiveFont.DrawOutline(text, center, new Vector2(0.5f, 0.5f), Vector2.One * 3f,
            Color.White, 3f, Color.Black * 0.8f);

        Draw.SpriteBatch.End();
    }}
