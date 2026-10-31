using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Enums;
using StardewValley;
using StardewValley.Menus;

namespace Rgdsplus.VerticalWorld
{
    // Draw over the dual-screen output while the game or save loader yields
    // between stages. No background task touches the graphics device.
    internal static class LoadingOverlay
    {
        private static bool visible;
        private static bool loadingSave;
        private static string status = "正在启动游戏组件";
        private static float progress;
        private static long stageStarted;
        private static long titleScreenStarted;
        private static LoadStage stage = LoadStage.None;
        private static bool stageReported;
        private static SpriteBatch bootstrapBatch;
        private static Texture2D bootstrapPixel;
        private static GraphicsDevice bootstrapDevice;
        private static string brandLogoPath;
        private static Texture2D brandLogo;
        private const long StartupTitleSettleMilliseconds = 2500;

        internal static void Initialize(IModHelper helper)
        {
            if (Environment.GetEnvironmentVariable("SDV_DUAL_SCREEN_VERTICAL_WORLD") != "1") return;
            brandLogoPath = Path.Combine(helper.DirectoryPath, "assets", "ZeroFrameLogo.png");
            Set(false, "正在启动游戏组件", .04f);
            helper.Events.GameLoop.GameLaunched += (_, _) => Set(false, "正在准备游戏界面", .12f);
            helper.Events.Specialized.LoadStageChanged += (_, e) => OnLoadStage(e.NewStage);
            helper.Events.GameLoop.SaveLoaded += (_, _) => OnSaveLoaded();
            helper.Events.GameLoop.ReturnedToTitle += (_, _) =>
            {
                stage = LoadStage.None;
                stageReported = false;
                titleScreenStarted = 0;
                Set(false, "正在返回标题画面", .08f);
            };
            helper.Events.GameLoop.UpdateTicked += (_, _) => UpdateProgress();
        }

        private static void Set(bool save, string message, float value)
        {
            visible = true;
            loadingSave = save;
            status = message;
            progress = MathHelper.Clamp(value, .02f, .96f);
            stageStarted = Environment.TickCount64;
        }

        private static void OnLoadStage(LoadStage next)
        {
            stage = next;
            stageReported = true;
            progress = ProgressForStage(next);
            switch (next)
            {
                case LoadStage.None:
                    if (!Context.IsWorldReady) Set(false, "正在进入游戏", .10f);
                    break;
                case LoadStage.CreatedBasicInfo: Set(false, "正在创建新游戏", .20f); break;
                case LoadStage.CreatedInitialLocations: Set(false, "正在准备游戏地图", .38f); break;
                case LoadStage.CreatedLocations: Set(false, "正在载入游戏内容", .56f); break;
                case LoadStage.CreatedSaveFile: Set(false, "正在建立存档", .76f); break;
                case LoadStage.SaveParsed: Set(true, "正在读取存档数据", .20f); break;
                case LoadStage.SaveLoadedBasicInfo: Set(true, "正在读取农场信息", .42f); break;
                case LoadStage.SaveAddedLocations: Set(true, "正在恢复游戏地图", .62f); break;
                case LoadStage.SaveLoadedLocations: Set(true, "正在恢复地图内容", .78f); break;
                case LoadStage.Preloaded: Set(true, "正在准备游戏界面", .90f); break;
                case LoadStage.Loaded:
                case LoadStage.Ready:
                    // SMAPI reaches these stages before SaveLoaded subscribers
                    // have finished; keep the overlay up through that event.
                    if (loadingSave) break;
                    break;
                case LoadStage.ReturningToTitle: Hide(); break;
            }
        }

        internal static bool ShouldKeepSaveOverlay(LoadStage value, bool saveLoading) =>
            saveLoading && value is LoadStage.Loaded or LoadStage.Ready;

        internal static bool ShouldHideForWorldReady(bool worldReady, bool saveLoading,
            bool hasLoadStage, LoadStage value) =>
            worldReady && !saveLoading && (!hasLoadStage || value is LoadStage.Loaded or LoadStage.Ready);

        internal static bool StartupTitleSettled(long titleShownAt, long now) =>
            titleShownAt != 0 && now - titleShownAt >= StartupTitleSettleMilliseconds;

        internal static float ProgressForStage(LoadStage value) => value switch
        {
            LoadStage.None => .10f,
            LoadStage.CreatedBasicInfo => .20f,
            LoadStage.CreatedInitialLocations => .38f,
            LoadStage.CreatedLocations => .56f,
            LoadStage.CreatedSaveFile => .76f,
            LoadStage.SaveParsed => .20f,
            LoadStage.SaveLoadedBasicInfo => .42f,
            LoadStage.SaveAddedLocations => .62f,
            LoadStage.SaveLoadedLocations => .78f,
            LoadStage.Preloaded => .90f,
            LoadStage.Loaded => .96f,
            LoadStage.Ready => 1f,
            _ => 0f
        };

        internal static void BeginTest(bool save)
        {
            stage = save ? LoadStage.SaveParsed : LoadStage.CreatedLocations;
            stageReported = true;
            Set(save, save ? "正在读取存档数据" : "正在载入游戏内容", save ? .42f : .56f);
        }

        internal static void EndTest() => Hide();

        private static void OnSaveLoaded()
        {
            titleScreenStarted = 0;
            Hide();
        }

        internal static object Status() => new { visible, loadingSave, status, progress };

        // GameRunner.Draw is the first reliable backbuffer hook. Draw here as
        // well as in the composed dual-screen frame so the pre-title/SMAPI
        // black-screen interval still shows a live loading indicator.
        internal static void DrawBootstrap(GraphicsDevice device)
        {
            if (!visible || device == null) return;
            if (bootstrapDevice != device || bootstrapBatch == null || bootstrapPixel == null
                || bootstrapPixel.IsDisposed)
            {
                bootstrapBatch?.Dispose();
                bootstrapPixel?.Dispose();
                bootstrapDevice = device;
                bootstrapBatch = new SpriteBatch(device);
                bootstrapPixel = new Texture2D(device, 1, 1, false, SurfaceFormat.Color);
                bootstrapPixel.SetData(new[] { Color.White });
            }

            int width = Math.Max(1, device.PresentationParameters.BackBufferWidth);
            int height = Math.Max(1, device.PresentationParameters.BackBufferHeight);
            bootstrapBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            bootstrapBatch.Draw(bootstrapPixel, new Rectangle(0, 0, width, height), new Color(9, 12, 20, 250));
            if (width >= 1600)
            {
                DrawBootstrapPanel(bootstrapBatch, new Rectangle(0, 0, width / 2, height));
                DrawBootstrapPanel(bootstrapBatch, new Rectangle(width / 2, 0, width - width / 2, height));
            }
            else
            {
                DrawBootstrapPanel(bootstrapBatch, new Rectangle(0, 0, width, height));
            }
            bootstrapBatch.End();
        }

        private static void DrawBootstrapPanel(SpriteBatch batch, Rectangle panel)
        {
            DrawBrandPanel(batch, bootstrapPixel, panel);
        }

        private static void UpdateProgress()
        {
            if (!visible) return;
            if (ShouldHideForWorldReady(Context.IsWorldReady, loadingSave, stageReported, stage))
            {
                Hide();
                return;
            }
            // The stage has no total item count. Move slowly instead of implying
            // a fabricated ETA, and hold below complete until SMAPI says Ready.
            float dwell = (Environment.TickCount64 - stageStarted) / 1000f;
            progress = Math.Min(.94f, progress + Math.Min(.002f, dwell * .00003f));
            if (loadingSave || Game1.activeClickableMenu is not TitleMenu)
            {
                titleScreenStarted = 0;
                return;
            }
            if (titleScreenStarted == 0)
                titleScreenStarted = Environment.TickCount64;
            else if (StartupTitleSettled(titleScreenStarted, Environment.TickCount64))
                Hide();
        }

        private static void Hide() => visible = false;

        internal static void Draw(SpriteBatch batch)
        {
            if (!visible || !VerticalWorldRenderer.Enabled || Game1.staminaRect == null || Game1.smallFont == null) return;
            int dots = (int)(Environment.TickCount64 / 450 % 4);
            string suffix = new string('.', dots);
            DrawPanel(batch, VerticalWorldRenderer.PhysicalUpper, suffix);
            DrawPanel(batch, VerticalWorldRenderer.PhysicalLower, suffix);
        }

        private static void DrawPanel(SpriteBatch batch, Rectangle panel, string suffix)
        {
            batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            DrawBrandPanel(batch, Game1.staminaRect, panel);
            batch.End();
        }

        private static void DrawBrandPanel(SpriteBatch batch, Texture2D pixel, Rectangle panel)
        {
            batch.Draw(pixel, panel, new Color(28, 40, 36, 252));
            int cardWidth = Math.Min(Math.Max(320, panel.Width - 72), 920);
            int cardHeight = Math.Min(Math.Max(230, panel.Height - 72), 360);
            var card = new Rectangle(panel.Center.X - cardWidth / 2,
                panel.Center.Y - cardHeight / 2, cardWidth, cardHeight);
            DrawFrame(batch, pixel, card);

            int logoSize = Math.Min(card.Height - 56, Math.Min(272, card.Width / 3));
            var logoRect = new Rectangle(card.X + 28, card.Center.Y - logoSize / 2, logoSize, logoSize);
            Texture2D logo = LoadLogo(batch.GraphicsDevice);
            if (logo != null) batch.Draw(logo, logoRect, Color.White);

            int contentLeft = logoRect.Right + 30;
            int contentRight = card.Right - 30;
            int contentCenter = contentLeft + (contentRight - contentLeft) / 2;
            var track = new Rectangle(contentLeft, card.Center.Y + 34,
                Math.Max(32, contentRight - contentLeft), 30);
            batch.Draw(pixel, new Rectangle(track.X - 6, track.Y - 6, track.Width + 12, track.Height + 12),
                new Color(80, 43, 25));
            batch.Draw(pixel, track, new Color(238, 206, 132));
            var fill = new Rectangle(track.X + 4, track.Y + 4,
                Math.Max(1, (int)((track.Width - 8) * Math.Clamp(progress, .02f, .96f))), track.Height - 8);
            batch.Draw(pixel, fill, new Color(211, 117, 48));
            for (int x = fill.X + 8; x < fill.Right; x += 16)
                batch.Draw(pixel, new Rectangle(x, fill.Y + 3, 7, Math.Max(1, fill.Height - 6)), new Color(238, 157, 64));

            if (Game1.smallFont == null) return;
            DrawCentered(batch, "Stardew Valley for AYN Thor", contentCenter, card.Y + 64,
                new Color(255, 238, 177), 1.05f);
            DrawCentered(batch, "by ZeroFrame LAB", contentCenter, card.Y + 112,
                new Color(232, 180, 92), .94f);
            int percent = Math.Clamp((int)Math.Round(progress * 100), 1, 99);
            DrawCentered(batch, percent + "%", contentCenter, track.Bottom + 24,
                new Color(255, 238, 177), .88f);
        }

        private static void DrawFrame(SpriteBatch batch, Texture2D pixel, Rectangle card)
        {
            batch.Draw(pixel, card, new Color(117, 69, 38));
            batch.Draw(pixel, new Rectangle(card.X + 8, card.Y + 8, card.Width - 16, card.Height - 16),
                new Color(224, 169, 83));
            batch.Draw(pixel, new Rectangle(card.X + 14, card.Y + 14, card.Width - 28, card.Height - 28),
                new Color(62, 75, 54));
            batch.Draw(pixel, new Rectangle(card.X + 20, card.Y + 20, card.Width - 40, card.Height - 40),
                new Color(42, 54, 43));
        }

        private static Texture2D LoadLogo(GraphicsDevice device)
        {
            if (brandLogo != null && !brandLogo.IsDisposed && brandLogo.GraphicsDevice == device) return brandLogo;
            if (string.IsNullOrEmpty(brandLogoPath) || device == null) return null;
            try
            {
                brandLogo?.Dispose();
                using var stream = File.OpenRead(brandLogoPath);
                return brandLogo = Texture2D.FromStream(device, stream);
            }
            catch (Exception ex) { ModEntry.Write("brand-logo-load-failed " + ex.Message); return null; }
        }

        private static string DisplayStatus(bool chinese)
        {
            if (chinese) return status;
            return stage switch
            {
                LoadStage.None => "Starting game",
                LoadStage.CreatedBasicInfo => "Creating new game",
                LoadStage.CreatedInitialLocations => "Preparing farm map",
                LoadStage.CreatedLocations => "Loading game content",
                LoadStage.CreatedSaveFile => "Writing save file",
                LoadStage.SaveParsed => "Reading save data",
                LoadStage.SaveLoadedBasicInfo => "Loading farm information",
                LoadStage.SaveAddedLocations => "Restoring locations",
                LoadStage.SaveLoadedLocations => "Loading map contents",
                LoadStage.Preloaded => "Finalizing game setup",
                _ => loadingSave ? "Loading save data" : "Loading game data"
            };
        }

        private static void DrawCentered(SpriteBatch batch, string text, int centerX, int y, Color color,
            float scale = 1.15f)
        {
            Vector2 size = Game1.smallFont.MeasureString(text) * scale;
            batch.DrawString(Game1.smallFont, text, new Vector2(centerX - size.X / 2, y),
                color, 0, Vector2.Zero, scale, SpriteEffects.None, 1f);
        }
    }
}
