using System;
using System.IO;
using System.Runtime.InteropServices;
using StardewModdingAPI;

namespace Rgdsplus.VerticalWorld
{
    internal static class ThorLeaseSession
    {
        private static string token;
        private static string requestPath;
        private static string heartbeatPath;
        private static string stopPath;
        private static string readyPath;
        private static bool readyCached;
        private static int heartbeatTicks;

        internal static bool Ready
        {
            get
            {
                if (readyCached) return true;
                if (string.IsNullOrEmpty(readyPath) || string.IsNullOrEmpty(token)) return false;
                try { return readyCached = File.Exists(readyPath) && File.ReadAllText(readyPath).Trim() == token; }
                catch { return false; }
            }
        }

        internal static void Start(string modDirectory, IModHelper helper)
        {
            if (!DisplayProfile.Current.SeparateOutputs || !RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;
            try
            {
                string runtime = Path.Combine(modDirectory, "runtime");
                Directory.CreateDirectory(runtime);
                requestPath = Path.Combine(runtime, "bridge.request");
                heartbeatPath = Path.Combine(runtime, "bridge.heartbeat");
                stopPath = Path.Combine(runtime, "bridge.stop");
                readyPath = Path.Combine(runtime, "bridge.ready");
                token = Environment.ProcessId + "-" + DateTime.UtcNow.Ticks;
                readyCached = false;
                heartbeatTicks = 0;
                Delete(stopPath);
                Delete(readyPath);
                File.WriteAllText(requestPath, token + "\n");
                Pulse();
                helper.Events.GameLoop.OneSecondUpdateTicked += (_, __) =>
                {
                    if (++heartbeatTicks >= 10) { heartbeatTicks = 0; Pulse(); }
                };
                ModEntry.Write("thor-bridge-request token=" + token + "; path=" + requestPath);
            }
            catch (Exception ex)
            {
                ModEntry.Write("thor-lease-launcher-failed " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static void Stop()
        {
            try
            {
                if (!string.IsNullOrEmpty(stopPath)) File.WriteAllText(stopPath, token + "\n");
                Delete(requestPath);
                Delete(readyPath);
            }
            catch (Exception ex)
            {
                ModEntry.Write("thor-lease-stop-failed " + ex.GetType().Name + ": " + ex.Message);
            }
            token = null;
            readyPath = null;
            readyCached = false;
        }

        private static void Pulse()
        {
            if (string.IsNullOrEmpty(heartbeatPath) || string.IsNullOrEmpty(token)) return;
            try { File.WriteAllText(heartbeatPath, token + "\n"); }
            catch (Exception ex) { ModEntry.Write("thor-bridge-heartbeat-failed " + ex.Message); }
        }

        private static void Delete(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }
}
