using System;
using System.IO;
using System.Runtime;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace Rgdsplus.VerticalWorld
{
    public sealed class ModEntry : Mod
    {
        internal static IMonitor Log;
        internal static string LogPath;
        public override void Entry(IModHelper helper)
        {
            Log = Monitor;
            LogPath = Path.Combine(helper.DirectoryPath, "vertical-world.log");
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 2 * 1024 * 1024)
                File.WriteAllText(LogPath, "");
            CompanionPreferences.Initialize(helper);
            VerticalWorldRenderer.Install();
            // A safe-mode launch must not take the lower-panel lease: without
            // an exported frame that would leave the user with a black panel.
            if (Environment.GetEnvironmentVariable("SDV_THOR_EXPORT_DISABLED") != "1")
                ThorLeaseSession.Start(helper.DirectoryPath, helper);
            CompanionHistory.Initialize(helper);
            ModManagement.Initialize(helper);
            PortraitCompatibility.Initialize(helper);
            FrameMetrics.Initialize(helper.DirectoryPath);
            GameplayGc.Initialize(helper);
            DisplayModeController.Initialize(helper);
            UiTestDriver.Initialize(helper);
            FirstUseWarmup.Initialize(helper);
            helper.Events.GameLoop.ReturnedToTitle += (_, e) => VerticalWorldRenderer.ReleaseWorld();
            AppDomain.CurrentDomain.ProcessExit += (_, e) =>
            {
                ThorTouchTransport.Dispose();
                ThorLeaseSession.Stop();
            };
            var profile = DisplayProfile.Current;
            Write("loaded version=0.6.3-reference-v2; upper=" + profile.UpperPanel + "; lower=" + profile.LowerPanel
                + "; upper-content=" + profile.UpperContent + "; lower-content=" + profile.LowerContent
                + "; reference=" + profile.UpperReferenceWidth + "x" + profile.UpperReferenceHeight
                + "/" + profile.LowerReferenceWidth + "x" + profile.LowerReferenceHeight
                + "; outputs=" + (profile.SeparateOutputs ? "lease" : "combined")
                + "; world-passes=" + (VerticalWorldRenderer.TwoPass ? 2 : 1));
        }
        internal static void Write(string line)
        {
            try { File.AppendAllText(LogPath, DateTime.UtcNow.ToString("O") + " " + line + Environment.NewLine); }
            catch { }
        }
    }

    internal static class VerticalWorldRenderer
    {
        internal static bool Enabled { get; private set; }
        internal static bool Active => Enabled
            && (!DisplayProfile.Current.SeparateOutputs || ThorLeaseSession.Ready);
        // Successful device build: upper=left (DRM plane 98), lower=right (plane 58).
        internal static readonly bool TwoPass = Environment.GetEnvironmentVariable("SDV_DUAL_WORLD_PASSES") == "2";
        internal static readonly bool LowerCachePrototype = Environment.GetEnvironmentVariable("SDV_LOWER_WORLD_CACHE") == "1";
        private static readonly int LowerCacheInterval = ParseCacheInterval();
        internal static Rectangle PhysicalUpper => ToRectangle(DisplayProfile.Current.UpperContent);
        internal static Rectangle PhysicalLower => ToRectangle(DisplayProfile.Current.LowerContent);
        internal static int WorldWidth => ScreenGeometry.Width(Game1.options?.zoomLevel ?? 1f);
        internal static int WorldHeight => ScreenGeometry.Height(Game1.options?.zoomLevel ?? 1f);
        internal static int LowerWorldWidth => ScreenGeometry.LowerWidth(Game1.options?.zoomLevel ?? 1f);
        internal static int LowerWorldHeight => ScreenGeometry.LowerHeight(Game1.options?.zoomLevel ?? 1f);
        internal static bool DrawingWorld;
        private static bool secondPass, worldThisFrame, cachedWorld, lowerCacheValid;
        private static int lowerCacheFrame = -1;
        private static RenderTarget2D world, lower, upperUi, bottomOutput, bottomUiOutput;
        private static ThorFrameTransport bottomTransport, bottomUiTransport;
        private static ThorDmabufTransport bottomDmabuf;
        private static int bottomUiSignature;
        private static bool bottomWorldInitialized, bottomUiInitialized;
        internal static int WorldRasterWidth => world?.Width ?? 0;
        internal static int WorldRasterHeight => world?.Height ?? 0;
        internal static RenderTarget2D UpperUi => upperUi;
        private static bool upperUiThisFrame;
        private static bool NeedsUpperUi => DisplayModeController.Expanded && (Game1.dialogueUp || Game1.activeClickableMenu is DialogueBox);
        // A continuous world should be drawn once into one tall render target.
        // The narrower lower panel is a centered crop of that same frame. Apart
        // from halving world draw calls, this guarantees that both panels use
        // the same simulation snapshot and camera position.
        private static bool UseTwoPass => (TwoPass || LowerCachePrototype)
            && !DisplayModeController.Expanded;
        private static GameLocation lastLocation;
        private static int cameraX, cameraY;
        private static long lastGeometryLog;
        private static int presentationSamples;
        private static readonly Action<int, int> AllocateLightmap = (Action<int, int>)Delegate.CreateDelegate(typeof(Action<int, int>), AccessTools.Method(typeof(Game1), "allocateLightmap"));

        private static int ParseCacheInterval()
        {
            return int.TryParse(Environment.GetEnvironmentVariable("SDV_LOWER_CACHE_INTERVAL"), out var value)
                ? Math.Max(1, Math.Min(30, value)) : 4;
        }

        private static Rectangle ToRectangle(DisplayRect value) =>
            new Rectangle(value.X, value.Y, value.Width, value.Height);

        private static Rectangle AspectFit(int sourceWidth, int sourceHeight, Rectangle bounds)
        {
            float scale = Math.Min(bounds.Width / (float)sourceWidth,
                bounds.Height / (float)sourceHeight);
            int width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
            int height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
            return new Rectangle(bounds.X + (bounds.Width - width) / 2,
                bounds.Y + (bounds.Height - height) / 2, width, height);
        }

        internal static void Install()
        {
            if (Environment.GetEnvironmentVariable("SDV_DUAL_SCREEN_VERTICAL_WORLD") != "1"
                && !DisplayProfile.Current.SeparateOutputs) return;
            var harmony = new Harmony("RGDSplus.VerticalWorld");
            try
            {
                Enabled = true;
                WorldRenderQuality.Install(harmony);
                FramePacing.Install(harmony);
                WorldSpriteEffect.Install(harmony);
                TreeSpriteTrim.Install(harmony);
                Patch(harmony, typeof(Game1), "Draw", nameof(DrawPrefix), nameof(DrawPostfix));
                Patch(harmony, typeof(Game1), "Update", nameof(UpdatePrefix), nameof(UpdatePostfix));
                Patch(harmony, typeof(Game1), "DrawWorld", nameof(WorldPrefix), nameof(WorldPostfix), nameof(WorldFinalizer));
                Patch(harmony, typeof(Game1), "ShouldDrawOnBuffer", nameof(BufferPrefix));
                Patch(harmony, typeof(Game1), "renderScreenBuffer", nameof(PresentPrefix));
                Patch(harmony, typeof(GameRunner), "Draw", postfix: nameof(RunnerDrawPostfix));
                UiPatches.Install(harmony);
                WeatherViewport.Install(harmony);
                TitlePresentation.Install(harmony);
                DialogueRouting.Install(harmony);
                ModEntry.Log?.Log("Reference v2 installed: Select toggles continuous world / persistent lower pages; companion Start blocked; upper dialogue routing enabled.", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Enabled = false;
                harmony.UnpatchAll(harmony.Id);
                ModEntry.Log?.Log("Reference v2 installation failed; patches removed: " + ex, LogLevel.Error);
                ModEntry.Write("install-failed " + ex);
            }
        }

        private static void Patch(Harmony harmony, Type type, string method, string prefix = null, string postfix = null, string finalizer = null)
        {
            harmony.Patch(AccessTools.Method(type, method) ?? throw new MissingMethodException(type.Name, method),
                prefix == null ? null : new HarmonyMethod(typeof(VerticalWorldRenderer), prefix),
                postfix == null ? null : new HarmonyMethod(typeof(VerticalWorldRenderer), postfix),
                finalizer: finalizer == null ? null : new HarmonyMethod(typeof(VerticalWorldRenderer), finalizer));
        }

        private static void UpdatePrefix(Game1 __instance)
        {
            if (!Active || __instance.takingMapScreenshot) return;
            FrameMetrics.BeginUpdate();
            UiPatches.EnsureViewports();
        }
        private static void UpdatePostfix() { if (Active) FrameMetrics.EndUpdate(); }
        private static void DrawPrefix(Game1 __instance)
        {
            if (!Active || __instance.takingMapScreenshot) return;
            FrameMetrics.BeginFrame();
            worldThisFrame = false;
            EnsureTargets(__instance);
            upperUiThisFrame = NeedsUpperUi;
            if (upperUiThisFrame)
            {
                var device = __instance.GraphicsDevice;
                var targets = device.GetRenderTargets();
                var viewport = device.Viewport;
                device.SetRenderTarget(upperUi);
                device.Clear(Color.Transparent);
                device.SetRenderTargets(targets);
                device.Viewport = viewport;
            }
            UiPatches.EnsureViewports();
        }
        private static void DrawPostfix() { if (Active) FrameMetrics.EndFrame(); }
        private static void RunnerDrawPostfix(GameRunner __instance)
        {
            if (!Active) return;
            var pp = __instance.GraphicsDevice.PresentationParameters;
            __instance.GraphicsDevice.Viewport = new Viewport(0, 0, pp.BackBufferWidth, pp.BackBufferHeight);
            LoadingOverlay.DrawBootstrap(__instance.GraphicsDevice);
        }
        private static bool BufferPrefix(Game1 __instance, ref bool __result)
        {
            if (!Active || __instance.takingMapScreenshot) return true;
            __result = true;
            return false;
        }

        internal sealed class WorldState
        {
            internal RenderTarget2D Destination;
            internal xTile.Dimensions.Rectangle Viewport;
            internal bool Active;
        }
        private static readonly WorldState GameplayWorldState = new WorldState();
        internal static bool IsSecondPass => secondPass;
        internal static xTile.Dimensions.Rectangle SceneViewport()
        {
            int w=WorldWidth,h=WorldHeight;
            var location=Game1.currentLocation;
            int x=Game1.viewport.X,y=Game1.viewport.Y;
            if(location?.Map!=null && Game1.player!=null && !Game1.eventUp && !Game1.viewportFreeze)
            {
                x=ScreenGeometry.DualCameraX(Game1.player.Position.X,location.Map.DisplayWidth,w);
                if (DisplayModeController.Expanded)
                    y = ScreenGeometry.CameraY(Game1.player.Position.Y, location.Map.DisplayHeight, h, false);
                else if (ReferenceEquals(location, lastLocation))
                    y = ScreenGeometry.TrackDualScreenY(Game1.player.Position.Y, location.Map.DisplayHeight,
                        h, LowerWorldHeight, cameraY);
                else
                    y = ScreenGeometry.CameraY(Game1.player.Position.Y, location.Map.DisplayHeight,
                        h, LowerWorldHeight);
            }
            return new xTile.Dimensions.Rectangle(x, y, w,
                DisplayModeController.Expanded ? h : h + LowerWorldHeight);
        }
        private static void WorldPrefix(Game1 __instance, ref RenderTarget2D target_screen, out WorldState __state)
        {
            __state = null;
            if (!Active || secondPass || __instance.takingMapScreenshot || Game1.currentLocation == null) return;
            EnsureTargets(__instance);
            int w = WorldWidth, h = WorldHeight;
            bool expanded = DisplayModeController.Expanded;
            ResizeWorld(ref world, __instance.GraphicsDevice, w,
                expanded || UseTwoPass ? h : h + LowerWorldHeight, "Reference.World");
            if (UseTwoPass) ResizeWorld(ref lower, __instance.GraphicsDevice,
                LowerWorldWidth, LowerWorldHeight, "Reference.Lower");
            var location = Game1.currentLocation;
            int mapW = location.Map.DisplayWidth, mapH = location.Map.DisplayHeight;
            bool locationChanged = !ReferenceEquals(location, lastLocation);
            if (locationChanged) lowerCacheValid = false;
            var scene=SceneViewport();
            cameraX=scene.X;cameraY=scene.Y;
            // Loading can re-enter DrawWorld, so it keeps an isolated state.
            // Normal gameplay is single-threaded and single-pass; reuse one
            // object there to remove a persistent 60 allocations/second.
            __state = Game1.gameMode == 3 ? GameplayWorldState : new WorldState();
            __state.Destination = target_screen ?? __instance.screen;
            __state.Viewport = Game1.viewport;
            __state.Active = true;
            DrawingWorld = true;
            FrameMetrics.BeginWorld();
            Game1.viewport = new xTile.Dimensions.Rectangle(cameraX, cameraY, w,
                expanded || UseTwoPass ? h : h + LowerWorldHeight);
            // Extended dimensions exist ONLY during DrawWorld, never in Update or menu layout.
            AllocateLightmap(Math.Max(ScreenGeometry.PanelWidth, ScreenGeometry.LowerPanelWidth),
                expanded || UseTwoPass ? ScreenGeometry.PanelHeight
                    : ScreenGeometry.PanelHeight + ScreenGeometry.LowerPanelHeight);
            __instance.GraphicsDevice.SetRenderTarget(world);
            target_screen = world;
            long now = Environment.TickCount;
            if (Environment.GetEnvironmentVariable("SDV_DIAGNOSTICS") == "1"
                && (locationChanged || now - lastGeometryLog > 10000))
            {
                ModEntry.Write("geometry mode=" + DisplayModeController.State.Mode + "; location=" + location.Name + "; map=" + mapW + "x" + mapH + "; camera=" + cameraX + "," + cameraY + "; panel-world=" + w + "x" + h + "; ui=" + Game1.uiViewport.Width + "x" + Game1.uiViewport.Height + "; zoom=" + Game1.options.zoomLevel + "; ui-scale=" + Game1.options.uiScale);
                lastGeometryLog = now;
            }
            lastLocation = location;
        }

        private static void WorldPostfix(Game1 __instance, GameTime time, ref RenderTarget2D target_screen, WorldState __state)
        {
            if (__state == null || !__state.Active) return;
            if (UseTwoPass && (!LowerCachePrototype || ShouldRefreshLower()))
            {
                secondPass = true;
                try
                {
                    int lowerX = ScreenGeometry.AlignedLowerX(cameraX, WorldWidth, LowerWorldWidth,
                        Game1.currentLocation.Map.DisplayWidth);
                    Game1.viewport = new xTile.Dimensions.Rectangle(lowerX, cameraY + WorldHeight,
                        LowerWorldWidth, LowerWorldHeight);
                    __instance.GraphicsDevice.SetRenderTarget(lower);
                    __instance.DrawWorld(time, lower);
                    lowerCacheValid = true;
                    lowerCacheFrame = Environment.TickCount;
                }
                finally { secondPass = false; }
            }
            worldThisFrame = cachedWorld = true;
            RestoreWorld(__instance, __state);
            target_screen = __state.Destination;
            FrameMetrics.EndWorld();
        }
        private static Exception WorldFinalizer(Game1 __instance, WorldState __state, Exception __exception)
        {
            if (__state?.Active == true) RestoreWorld(__instance, __state);
            if (__exception != null) ModEntry.Write("world-failed " + __exception);
            return __exception;
        }
        private static void RestoreWorld(Game1 game, WorldState state)
        {
            DrawingWorld = false;
            Game1.viewport = state.Viewport;
            game.GraphicsDevice.SetRenderTarget(state.Destination);
            // DrawWorld lives in our separate world target. Keep the native screen
            // transparent for later world-space overlays (e.g. BobberBar, whose
            // StartWorldDrawInUI deliberately leaves the ordinary UI target).
            game.GraphicsDevice.Clear(Color.Transparent);
            state.Active = false;
            UiPatches.EnsureUiViewport();
        }

        private static bool ShouldRefreshLower()
        {
            if (!lowerCacheValid) return true;
            if (Game1.player == null || Game1.currentLocation == null) return true;
            if (Game1.player.UsingTool) return true;
            int lowerTop = cameraY + WorldHeight;
            float playerY = Game1.player.Position.Y;
            const int margin = 96;
            if (playerY >= lowerTop - margin && playerY <= lowerTop + LowerWorldHeight + margin) return true;
            return Environment.TickCount - lowerCacheFrame >= LowerCacheInterval * 16;
        }

        private static bool PresentPrefix(Game1 __instance, RenderTarget2D target_screen)
        {
            if (!Active || __instance.takingMapScreenshot) return true;
            FrameMetrics.BeginPresent();
            var device = __instance.GraphicsDevice;
            bool separateOutputs = DisplayProfile.Current.SeparateOutputs;
            if (separateOutputs && Environment.GetEnvironmentVariable("SDV_THOR_EXPORT_DISABLED") != "1")
            {
                if (Environment.GetEnvironmentVariable("SDV_THOR_DMABUF") == "1")
                    PublishBottomDmabuf(__instance);
                else PublishBottomOutput(__instance);
            }
            if (presentationSamples++ < 3)
                ModEntry.Write("presentation backbuffer=" + device.PresentationParameters.BackBufferWidth + "x" + device.PresentationParameters.BackBufferHeight + "; window=" + __instance.Window.ClientBounds + "; transport=" + __instance.localMultiplayerWindow + "; ui=" + __instance.uiScreen?.Width + "x" + __instance.uiScreen?.Height);
            device.SetRenderTarget(null);
            device.Viewport = new Viewport(0, 0, device.PresentationParameters.BackBufferWidth, device.PresentationParameters.BackBufferHeight);
            device.Clear(Color.Black);
            var batch = Game1.spriteBatch;
            bool expanded = DisplayModeController.Expanded;
            bool showWorld = worldThisFrame || (cachedWorld && Game1.gameMode == 3 && Game1.activeClickableMenu != null && ReferenceEquals(lastLocation, Game1.currentLocation));
            batch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp);
            if (showWorld && world != null && !world.IsDisposed)
            {
                batch.Draw(world, PhysicalUpper, WorldRenderQuality.Source(world, new Rectangle(0, 0, WorldWidth, WorldHeight)), Color.White);
                if (!expanded && !separateOutputs)
                {
                    var lowerSource = UseTwoPass
                        ? new Rectangle(0, 0, LowerWorldWidth, LowerWorldHeight)
                        : new Rectangle((WorldWidth - LowerWorldWidth) / 2, WorldHeight,
                            LowerWorldWidth, LowerWorldHeight);
                    var lowerTexture = UseTwoPass ? lower : world;
                    batch.Draw(lowerTexture, PhysicalLower,
                        WorldRenderQuality.Source(lowerTexture, lowerSource), Color.White);
                }
            }
            else if (__instance.screen != null)
                batch.Draw(__instance.screen, separateOutputs || expanded ? PhysicalUpper : PhysicalLower, Color.White);
            batch.End();
            if (expanded && !separateOutputs)
            {
                var transform = Matrix.CreateScale(PhysicalLower.Width / (float)Game1.uiViewport.Width, PhysicalLower.Height / (float)Game1.uiViewport.Height, 1f)
                    * Matrix.CreateTranslation(PhysicalLower.X, PhysicalLower.Y, 0);
                batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, null, null, transform);
                if (Game1.activeClickableMenu == null || Game1.activeClickableMenu is DialogueBox)
                    Dashboard.Draw(batch, DisplayModeController.SelectedPage, false);
                else Dashboard.DrawBackdrop(batch);
                batch.End();
            }
            batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            if (worldThisFrame && __instance.screen != null && !__instance.screen.IsDisposed)
                batch.Draw(__instance.screen, PhysicalUpper, Color.White);
            if (Game1.activeClickableMenu is TitleMenu title)
                TitlePresentation.DrawUpper(batch, title, PhysicalUpper);
            else if (showWorld && (Game1.fadeToBlack || Game1.globalFade))
                batch.Draw(Game1.staminaRect, PhysicalUpper, Color.Black * Game1.fadeToBlackAlpha);
            if (expanded && upperUiThisFrame && upperUi != null && !upperUi.IsDisposed)
                // Dialogue is laid out in the lower panel's logical UI space.
                // Preserve that aspect ratio when routing it to the wider upper
                // display instead of widening glyphs and portraits.
                batch.Draw(upperUi, AspectFit(upperUi.Width, upperUi.Height, PhysicalUpper), Color.White);
            DisplayColorControls.DrawOverlay(batch, PhysicalUpper, true);
            if (!separateOutputs && __instance.uiScreen != null && !__instance.uiScreen.IsDisposed)
                batch.Draw(__instance.uiScreen, PhysicalLower, Color.White);
            batch.End();
            FrameMetrics.EndPresent();
            UiTestDriver.AfterPresent();
            return false;
        }

        private static void PublishBottomOutput(Game1 game)
        {
            var device = game.GraphicsDevice;
            // Repeated full-resolution GPU readbacks can make FEX abort on
            // ArmadaOS. The native presenter scales this transport surface to
            // the physical panel, so use a smaller raster by default while
            // keeping the panel mode and touch coordinates at 1240x1080.
            int divisor = int.TryParse(Environment.GetEnvironmentVariable("SDV_THOR_EXPORT_DIVISOR"),
                out int requestedDivisor) ? Math.Clamp(requestedDivisor, 1, 4) : 2;
            int width = Math.Max(1, DisplayProfile.Current.LowerPanel.Width / divisor);
            int height = Math.Max(1, DisplayProfile.Current.LowerPanel.Height / divisor);
            Resize(ref bottomOutput, device, width, height, "Thor.BottomOutput");
            bottomTransport ??= new ThorFrameTransport(width, height);
            var previousTargets = device.GetRenderTargets();
            var previousViewport = device.Viewport;
            device.SetRenderTarget(bottomOutput);
            device.Viewport = new Viewport(0, 0, width, height);
            device.Clear(Color.Black);
            var batch = Game1.spriteBatch;
            bool expanded = DisplayModeController.Expanded;
            var lowerTexture = UseTwoPass ? lower : world;
            if (!expanded && lowerTexture != null && !lowerTexture.IsDisposed)
            {
                batch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp);
                var lowerSource = UseTwoPass
                    ? new Rectangle(0, 0, LowerWorldWidth, LowerWorldHeight)
                    : new Rectangle((WorldWidth - LowerWorldWidth) / 2, WorldHeight,
                        LowerWorldWidth, LowerWorldHeight);
                batch.Draw(lowerTexture, new Rectangle(0, 0, width, height),
                    WorldRenderQuality.Source(lowerTexture, lowerSource), Color.White);
                batch.End();
            }
            if (expanded)
            {
                var transform = Matrix.CreateScale(width / (float)Game1.uiViewport.Width,
                    height / (float)Game1.uiViewport.Height, 1f);
                batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp,
                    null, null, null, transform);
                if (Game1.activeClickableMenu == null || Game1.activeClickableMenu is DialogueBox)
                    Dashboard.Draw(batch, DisplayModeController.SelectedPage, false);
                else Dashboard.DrawBackdrop(batch);
                batch.End();
            }
            // Title and save loading are mostly static and are also where FEX
            // is most vulnerable to concurrent GPU readback.  Publish one
            // complete frame there, then begin continuous capture only after
            // gameplay has entered game mode 3.
            bool captureWorld = !bottomWorldInitialized || Game1.gameMode == 3
                || Game1.fadeToBlack || Game1.globalFade;
            if (captureWorld || bottomTransport.Pending)
                bottomTransport.Publish(bottomOutput, captureWorld);
            if (captureWorld) bottomWorldInitialized = true;

            // Keep fast-changing world pixels on an exact 2x integer-scaled
            // transport, but preserve text/HUD/menu raster quality in a native
            // resolution alpha overlay that the ARM64 presenter composites.
            int nextUiSignature = BottomUiSignature();
            // A menu being open does not by itself require a ten-FPS GPU
            // readback. Signature changes still refresh its contents, while
            // fades, dialogue and gameplay HUD transitions remain animated.
            bool uiAnimated = Game1.dialogueUp || Game1.eventUp
                || Game1.fadeToBlack || Game1.globalFade
                || (Game1.gameMode == 3 && PlayerNearToolbar());
            bool captureUi = !bottomUiInitialized || nextUiSignature != bottomUiSignature || uiAnimated;
            int uiDivisor = int.TryParse(Environment.GetEnvironmentVariable("SDV_THOR_UI_DIVISOR"),
                out int requestedUiDivisor) ? Math.Clamp(requestedUiDivisor, 1, 4) : 2;
            int uiWidth = Math.Max(1, DisplayProfile.Current.LowerPanel.Width / uiDivisor);
            int uiHeight = Math.Max(1, DisplayProfile.Current.LowerPanel.Height / uiDivisor);
            Resize(ref bottomUiOutput, device, uiWidth, uiHeight, "Thor.BottomUiOutput");
            bottomUiTransport ??= new ThorFrameTransport(uiWidth, uiHeight,
                "/dev/shm/stardew-thor-ui", "SDV_THOR_UI_FPS");
            if (captureUi)
            {
                device.SetRenderTarget(bottomUiOutput);
                device.Viewport = new Viewport(0, 0, uiWidth, uiHeight);
                device.Clear(Color.Transparent);
                if (expanded)
                {
                    var uiTransform = Matrix.CreateScale(uiWidth / (float)Game1.uiViewport.Width,
                        uiHeight / (float)Game1.uiViewport.Height, 1f);
                    batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp,
                        null, null, null, uiTransform);
                    if (Game1.activeClickableMenu == null || Game1.activeClickableMenu is DialogueBox)
                        Dashboard.Draw(batch, DisplayModeController.SelectedPage, false);
                    else Dashboard.DrawBackdrop(batch);
                    batch.End();
                }
                if (game.uiScreen != null && !game.uiScreen.IsDisposed)
                {
                    batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
                    batch.Draw(game.uiScreen, new Rectangle(0, 0, uiWidth, uiHeight), Color.White);
                    batch.End();
                }
                bottomUiSignature = nextUiSignature;
                bottomUiInitialized = true;
            }
            if (captureUi || bottomUiTransport.Pending)
                bottomUiTransport.Publish(bottomUiOutput, captureUi);
            device.SetRenderTargets(previousTargets);
            device.Viewport = previousViewport;
        }

        private static void PublishBottomDmabuf(Game1 game)
        {
            bottomDmabuf ??= new ThorDmabufTransport();
            if (!bottomDmabuf.EnsureOpen()) return;
            var device = game.GraphicsDevice;
            int width = bottomDmabuf.Width;
            int height = bottomDmabuf.Height;
            Resize(ref bottomOutput, device, width, height, "Thor.DmabufOutput");
            var previousTargets = device.GetRenderTargets();
            var previousViewport = device.Viewport;
            device.SetRenderTarget(bottomOutput);
            device.Viewport = new Viewport(0, 0, width, height);
            device.Clear(Color.Black);
            int canvasWidth = DisplayProfile.Current.LowerContent.Width;
            int canvasHeight = DisplayProfile.Current.LowerContent.Height;
            // Logical landscape (1240x1080) -> physical portrait (1080x1240),
            // clockwise. The DRM plane itself only accepts rotation=0.
            var rotation = new Matrix(
                0f, 1f, 0f, 0f,
                -1f, 0f, 0f, 0f,
                0f, 0f, 1f, 0f,
                width, 0f, 0f, 1f);
            var batch = Game1.spriteBatch;
            bool expanded = DisplayModeController.Expanded;
            var lowerTexture = UseTwoPass ? lower : world;
            if (!expanded && lowerTexture != null && !lowerTexture.IsDisposed)
            {
                batch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp,
                    null, null, null, rotation);
                var lowerSource = UseTwoPass
                    ? new Rectangle(0, 0, LowerWorldWidth, LowerWorldHeight)
                    : new Rectangle((WorldWidth - LowerWorldWidth) / 2, WorldHeight,
                        LowerWorldWidth, LowerWorldHeight);
                batch.Draw(lowerTexture, new Rectangle(0, 0, canvasWidth, canvasHeight),
                    WorldRenderQuality.Source(lowerTexture, lowerSource), Color.White);
                batch.End();
            }
            if (expanded)
            {
                var transform = Matrix.CreateScale(canvasWidth / (float)Game1.uiViewport.Width,
                    canvasHeight / (float)Game1.uiViewport.Height, 1f) * rotation;
                batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp,
                    null, null, null, transform);
                if (Game1.activeClickableMenu == null || Game1.activeClickableMenu is DialogueBox)
                    Dashboard.Draw(batch, DisplayModeController.SelectedPage, false);
                else Dashboard.DrawBackdrop(batch);
                batch.End();
            }
            if (game.uiScreen != null && !game.uiScreen.IsDisposed)
            {
                batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp,
                    null, null, null, rotation);
                // The title scene can leave a one-pixel blue edge after the
                // landscape-to-portrait rotation. Title-only overscan hides
                // that sampling edge without changing gameplay geometry.
                var destination = Game1.activeClickableMenu is TitleMenu
                    ? new Rectangle(-2, -2, canvasWidth + 4, canvasHeight + 4)
                    : new Rectangle(0, 0, canvasWidth, canvasHeight);
                batch.Draw(game.uiScreen, destination, Color.White);
                batch.End();
            }
            batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            DisplayColorControls.DrawOverlay(batch, new Rectangle(0, 0, width, height), false);
            batch.End();
            bottomDmabuf.Publish();
            device.SetRenderTargets(previousTargets);
            device.Viewport = previousViewport;
        }

        private static bool PlayerNearToolbar()
        {
            if (Game1.player == null) return false;
            int screenY = (int)Game1.player.Position.Y + 32 - cameraY;
            return screenY > WorldHeight + LowerWorldHeight - 192;
        }

        private static int BottomUiSignature()
        {
            var hash = new HashCode();
            hash.Add(Game1.activeClickableMenu?.GetType());
            hash.Add(Game1.dialogueUp); hash.Add(Game1.eventUp);
            hash.Add(Game1.timeOfDay); hash.Add(Game1.player?.CurrentToolIndex ?? -1);
            hash.Add(Game1.player?.health ?? 0); hash.Add((int)(Game1.player?.stamina ?? 0));
            if (Game1.player != null)
                for (int i = 0; i < Math.Min(12, Game1.player.Items.Count); i++)
                {
                    var item = Game1.player.Items[i];
                    hash.Add(item?.QualifiedItemId); hash.Add(item?.Stack ?? 0); hash.Add(item?.Quality ?? 0);
                }
            return hash.ToHashCode();
        }

        private static void EnsureTargets(Game1 game)
        {
            WorldRenderQuality.ConfigurePresentation();
            var screen = game.screen;
            var uiScreen = game.uiScreen;
            Resize(ref screen, game.GraphicsDevice, WorldWidth, WorldHeight, "@Game1.screen");
            Resize(ref uiScreen, game.GraphicsDevice, ScreenGeometry.LowerWidth(Game1.options?.uiScale ?? 1f),
                ScreenGeometry.LowerHeight(Game1.options?.uiScale ?? 1f), "@Game1.uiScreen");
            if (NeedsUpperUi)
                Resize(ref upperUi, game.GraphicsDevice, uiScreen.Width, uiScreen.Height, "Reference.Dialogue");
            // The native setters dispose the previous value even for self-assignment.
            if (!ReferenceEquals(game.screen, screen)) game.screen = screen;
            if (!ReferenceEquals(game.uiScreen, uiScreen)) game.uiScreen = uiScreen;
        }
        private static void ResizeWorld(ref RenderTarget2D target, GraphicsDevice device, int width, int height, string name)
        {
            Resize(ref target, device, WorldRenderQuality.Pixels(width), WorldRenderQuality.Pixels(height), name);
            WorldRenderQuality.Register(target, width, height);
        }
        private static void Resize(ref RenderTarget2D target, GraphicsDevice device, int width, int height, string name)
        {
            if (target != null && !target.IsDisposed && target.Width == width && target.Height == height) return;
            var old = target;
            target = new RenderTarget2D(device, width, height, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents) { Name = name };
            old?.Dispose();
            cachedWorld = false;
            FrameMetrics.TargetAllocations++;
        }
        internal static void ReleaseWorld()
        {
            world?.Dispose(); lower?.Dispose(); upperUi?.Dispose(); bottomOutput?.Dispose(); bottomUiOutput?.Dispose();
            world = lower = upperUi = bottomOutput = bottomUiOutput = null;
            bottomTransport?.Dispose();
            bottomUiTransport?.Dispose();
            bottomDmabuf?.Dispose();
            bottomTransport = bottomUiTransport = null;
            bottomDmabuf = null;
            bottomWorldInitialized = bottomUiInitialized = false;
            WorldRenderQuality.Clear();
            WorldSpriteEffect.Release();
            TreeSpriteTrim.Clear();
            cachedWorld = worldThisFrame = false;
            lowerCacheValid = false;
            lowerCacheFrame = -1;
            lastLocation = null;
        }
        internal static void InvalidateWorld()
        {
            cachedWorld = worldThisFrame = false;
            lowerCacheValid = false;
            lowerCacheFrame = -1;
            lastGeometryLog = 0;
        }
    }
}
