using System;
using System.IO;
using System.Net.Sockets;

namespace Rgdsplus.VerticalWorld
{
    // Receives panel-local touch events from the native DRM lease client.
    // Packet: kind (1=down, 2=move, 3=up), x float32, y float32.
    internal static class ThorTouchTransport
    {
        private const int PacketSize = 9;
        private static readonly byte[] packet = new byte[PacketSize];
        private static Socket socket;
        private static string path;
        private static bool failed;

        internal static void Poll(Action<float, float, bool, bool> touchChanged)
        {
            if (!DisplayProfile.Current.SeparateOutputs || failed) return;
            EnsureOpen();
            if (socket == null) return;
            try
            {
                while (socket.Poll(0, SelectMode.SelectRead))
                {
                    int count = socket.Receive(packet);
                    if (count != PacketSize) continue;
                    byte kind = packet[0];
                    float x = BitConverter.ToSingle(packet, 1);
                    float y = BitConverter.ToSingle(packet, 5);
                    if (float.IsNaN(x) || float.IsNaN(y)) continue;
                    touchChanged?.Invoke(x, y, kind == 1, kind == 3);
                }
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock) { }
            catch (Exception ex)
            {
                failed = true;
                ModEntry.Write("thor-touch-read-failed " + ex.GetType().Name + ": " + ex.Message);
                Dispose();
            }
        }

        private static void EnsureOpen()
        {
            if (socket != null) return;
            try
            {
                path = Environment.GetEnvironmentVariable("SDV_THOR_TOUCH_PATH");
                if (string.IsNullOrWhiteSpace(path)) path = "/dev/shm/stardew-thor-touch";
                try { File.Delete(path); }
                catch (FileNotFoundException) { }
                socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
                socket.Blocking = false;
                socket.Bind(new UnixDomainSocketEndPoint(path));
                ModEntry.Write("thor-touch-open path=" + path);
            }
            catch (Exception ex)
            {
                failed = true;
                ModEntry.Write("thor-touch-open-failed " + ex.GetType().Name + ": " + ex.Message);
                Dispose();
            }
        }

        internal static void Dispose()
        {
            socket?.Dispose();
            socket = null;
            try { if (!string.IsNullOrEmpty(path)) File.Delete(path); }
            catch { }
        }
    }
}
