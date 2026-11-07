using Celeste.Mod.SpeedrunTool.SaveLoad;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System.Collections.Generic;

namespace Celeste.Mod.SpeedrunTool.MoreSaveSlotsUI;

/// <summary>
/// 存档历史的时间轴 UI：屏幕下方一条横向缩略图，左右切换，回车读档。
/// <para>按 <c>NumPad8</c> 打开/关闭；打开时通过 <see cref="GameFreezeManager.UI_IsOpen"/> 冻结游戏。</para>
/// </summary>
internal static class SaveHistoryUI {
    private static bool open;
    private static int selection;
    private static float ease;

    private const string OpenSfx = "event:/ui/game/pause";
    private const string CloseSfx = "event:/ui/game/unpause";
    private const string MoveSfx = "event:/ui/main/rollover_up";
    private const string ConfirmSfx = "event:/ui/main/button_select";
    private const string FailSfx = "event:/ui/game/lookout_off";

    internal static bool OnScreen => open || ease > 0.01f;

    [Load]
    private static void Load() {
        On.Celeste.Level.Update += LevelOnUpdate;
        On.Celeste.Level.Render += LevelOnRender;
    }

    [Unload]
    private static void Unload() {
        On.Celeste.Level.Update -= LevelOnUpdate;
        On.Celeste.Level.Render -= LevelOnRender;
    }

    /// <summary>UI 打开期间游戏是冻结的（Level.Update 不跑），所以由 UI_IsOpen 每帧调用这里。</summary>
    internal static void Update() {
        TickEase();

        if (!open) {
            return;
        }

        if (Input.MenuCancel.Pressed || Input.ESC.Pressed || MInput.Keyboard.Pressed(Keys.NumPad8)) {
            Close();
            return;
        }

        IReadOnlyList<SaveSlot> entries = SaveHistory.Entries;
        if (entries.Count == 0) {
            Close();
            return;
        }

        if (Input.MenuLeft.Pressed) {
            selection = (selection + entries.Count - 1) % entries.Count;
            Audio.Play(MoveSfx);
        }
        else if (Input.MenuRight.Pressed) {
            selection = (selection + 1) % entries.Count;
            Audio.Play(MoveSfx);
        }

        if (Input.MenuConfirm.Pressed) {
            LoadEntry(selection);
        }
    }

    private static void TickEase() {
        float target = open ? 1f : 0f;
        ease = Calc.Approach(ease, target, Engine.RawDeltaTime * 4f);
    }

    private static void LevelOnUpdate(On.Celeste.Level.orig_Update orig, Level self) {
        orig(self);

        TickEase();

        if (open) {
            return; // 打开时游戏被冻结，输入由 Update() 处理
        }

        if (self.Paused == false && MInput.Keyboard.Pressed(Keys.NumPad8) && SaveHistory.Count > 0) {
            open = true;
            selection = 0;
            Audio.Play(OpenSfx);

            if (SaveLoadProfiler.Enabled) {
                for (int i = 0; i < SaveHistory.Entries.Count; i++) {
                    SaveSlot entry = SaveHistory.Entries[i];
                    Logger.Info("SpeedrunTool/History",
                        $"  [{i}] {entry.Name} saved={entry.StateManager?.IsSaved} preview={entry.Preview is not null} tex={entry.Preview?.Tex is not null} label={SaveHistory.Label(i)}");
                }
            }
        }
    }

    private static void Close() {
        if (open) {
            Audio.Play(CloseSfx);
        }

        open = false;
    }

    private static void LoadEntry(int index) {
        IReadOnlyList<SaveSlot> entries = SaveHistory.Entries;
        if (index < 0 || index >= entries.Count) {
            return;
        }

        SaveSlot entry = entries[index];
        if (entry.StateManager is not { IsSaved: true }) {
            Audio.Play(FailSfx);
            return;
        }

        // 切到那条历史槽再读档：之后它就是"当前槽"，再存档会把新状态推回历史
        if (!SaveSlotsManager.SwitchSlot(entry.Name)) {
            Audio.Play(FailSfx);
            return;
        }

        Audio.Play(ConfirmSfx);
        open = false;
        SaveLoadHotkeys.LoadStateAndMessage();
    }

    private static void LevelOnRender(On.Celeste.Level.orig_Render orig, Level self) {
        orig(self);

        if (ease <= 0.01f) {
            return;
        }

        IReadOnlyList<SaveSlot> entries = SaveHistory.Entries;
        float alpha = Calc.Clamp(ease, 0f, 1f);

        Draw.SpriteBatch.Begin();

        Draw.Rect(new Rectangle(0, 0, Engine.Width, Engine.Height), Color.Black * (0.6f * alpha));

        ActiveFont.DrawOutline("SAVE HISTORY", new Vector2(Engine.Width / 2f, Engine.Height * 0.14f),
            new Vector2(0.5f, 0.5f), Vector2.One * 0.9f, Color.White * alpha, 2f, Color.Black * alpha);

        if (entries.Count == 0) {
            ActiveFont.DrawOutline("(empty)", new Vector2(Engine.Width / 2f, Engine.Height / 2f),
                new Vector2(0.5f, 0.5f), Vector2.One * 0.7f, Color.Gray * alpha, 2f, Color.Black * alpha);
            Draw.SpriteBatch.End();
            return;
        }

        float totalWidth = Engine.Width * 0.86f;
        float gap = 12f;
        float slotWidth = Math.Min(280f, (totalWidth - gap * (entries.Count - 1)) / entries.Count);
        float slotHeight = slotWidth * 9f / 16f;
        float startX = (Engine.Width - (slotWidth * entries.Count + gap * (entries.Count - 1))) / 2f;
        float y = Engine.Height * 0.52f - slotHeight / 2f;

        for (int i = 0; i < entries.Count; i++) {
            bool highlight = i == selection;

            // 景深：选中的放大 + 全亮 + 白框，其余缩小 + 压暗，营造"焦点"
            float focusScale = highlight ? 1.08f : 0.9f;
            float focusAlpha = highlight ? 1f : 0.55f;
            float lift = highlight ? -8f : 0f;

            float centerX = startX + i * (slotWidth + gap) + slotWidth / 2f;
            float centerY = y + slotHeight / 2f + lift;
            float w = slotWidth * focusScale;
            float h = slotHeight * focusScale;
            Rectangle rect = new((int) (centerX - w / 2f), (int) (centerY - h / 2f), (int) w, (int) h);

            SaveSlot entry = entries[i];
            bool saved = entry.StateManager is { IsSaved: true };
            Texture2D tex = entry.Preview?.Tex;

            // 选中项的柔光底
            if (highlight) {
                Draw.Rect(new Rectangle(rect.X - 4, rect.Y - 4, rect.Width + 8, rect.Height + 8), Color.White * (0.18f * alpha));
            }

            Draw.Rect(rect, Color.Black * (0.6f * alpha));

            if (tex is not null && tex.Width > 0 && tex.Height > 0) {
                float scale = Math.Min(w / tex.Width, h / tex.Height);
                Vector2 size = new(tex.Width * scale, tex.Height * scale);
                Vector2 pos = new(rect.X + (w - size.X) / 2f, rect.Y + (h - size.Y) / 2f);
                Draw.SpriteBatch.Draw(tex, pos, null, Color.White * (alpha * focusAlpha), 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);

                if (!highlight) {
                    // 非选中：压暗做景深
                    Draw.Rect(rect, Color.Black * (0.45f * alpha));
                }
            }
            else {
                string placeholder = saved ? "(no preview)" : "(empty)";
                ActiveFont.DrawOutline(placeholder, new Vector2(rect.X + w / 2f, rect.Y + h / 2f),
                    new Vector2(0.5f, 0.5f), Vector2.One * 0.45f, Color.Gray * alpha, 2f, Color.Black * alpha);
            }

            Draw.HollowRect(rect, (highlight ? Color.White : Color.Gray * 0.6f) * (alpha * focusAlpha));

            string label = saved ? SaveHistory.Label(i) : "(empty)";
            ActiveFont.DrawOutline(label, new Vector2(centerX, rect.Bottom + 6f),
                new Vector2(0.5f, 0f), Vector2.One * (highlight ? 0.46f : 0.38f),
                (highlight ? Color.White : Color.LightGray) * (alpha * focusAlpha), 2f, Color.Black * alpha);
        }

        ActiveFont.DrawOutline("NumPad8 close   < > select   Confirm load",
            new Vector2(Engine.Width / 2f, Engine.Height * 0.84f),
            new Vector2(0.5f, 0.5f), Vector2.One * 0.45f, Color.Gray * alpha, 2f, Color.Black * alpha);

        Draw.SpriteBatch.End();
    }
}
