using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework.Graphics;

namespace Rgdsplus.VerticalWorld
{
    // Publishes the lower display as triple-buffered BGRA frames. A native ARM64
    // lease client presents these bytes on ArmadaOS's secondary DRM connector.
    // The game remains the sole owner of rendering and state.
    internal sealed class ThorFrameTransport : IDisposable
    {
        private const int HeaderSize = 64;
        private const int SharedBufferCount = 3;
        private const long Magic = 0x31524F4854564453; // "SDVTHOR1"
        private readonly string path;
        private readonly int width;
        private readonly int height;
        private readonly int stride;
        private readonly int bytesPerFrame;
        private readonly int minimumFrameGap;
        private readonly int maximumFrames;
        private FileStream file;
        private MemoryMappedFile mapping;
        private MemoryMappedViewAccessor view;
        private byte[] pixels;
        private GCHandle pixelsPin;
        private bool pixelsPinned;
        private int activeBuffer;
        private long sequence;
        private long lastPublish;
        private int publishedFrames;
        private bool failed;
        private readonly bool usePbo = Environment.GetEnvironmentVariable("SDV_THOR_PBO") != "0";
        private uint[] pbo;
        private IntPtr[] pboFences;
        private Queue<int> pboPending;
        private Stack<int> pboFree;

        private const uint GlPixelPackBuffer = 0x88EB;
        private const uint GlStreamRead = 0x88E1;
        private const uint GlBgra = 0x80E1;
        // BGRA + REV produces little-endian B,G,R,A bytes, matching the DRM
        // XRGB8888/Cairo buffer without a CPU channel shuffle.
        private const uint GlUnsignedInt8888Rev = 0x8367;
        private const uint GlReadOnly = 0x88B8;
        private const uint GlSyncGpuCommandsComplete = 0x9117;
        private const uint GlAlreadySignaled = 0x911A;
        private const uint GlConditionSatisfied = 0x911C;

        [DllImport("libGL.so.1")] private static extern void glGenBuffers(int count, uint[] buffers);
        [DllImport("libGL.so.1")] private static extern void glDeleteBuffers(int count, uint[] buffers);
        [DllImport("libGL.so.1")] private static extern void glBindBuffer(uint target, uint buffer);
        [DllImport("libGL.so.1")] private static extern void glBufferData(uint target, IntPtr size, IntPtr data, uint usage);
        [DllImport("libGL.so.1")] private static extern void glReadPixels(int x, int y, int width, int height,
            uint format, uint type, IntPtr data);
        [DllImport("libGL.so.1")] private static extern IntPtr glMapBuffer(uint target, uint access);
        [DllImport("libGL.so.1")] private static extern byte glUnmapBuffer(uint target);
        [DllImport("libGL.so.1")] private static extern IntPtr glFenceSync(uint condition, uint flags);
        [DllImport("libGL.so.1")] private static extern uint glClientWaitSync(IntPtr sync, uint flags, ulong timeout);
        [DllImport("libGL.so.1")] private static extern void glDeleteSync(IntPtr sync);
        [DllImport("libGL.so.1")] private static extern void glFlush();

        internal ThorFrameTransport(int width, int height, string pathOverride = null,
            string fpsVariable = "SDV_THOR_EXPORT_FPS")
        {
            this.width = Math.Max(1, width);
            this.height = Math.Max(1, height);
            stride = checked(this.width * 4);
            bytesPerFrame = checked(stride * this.height);
            int fps = int.TryParse(Environment.GetEnvironmentVariable(fpsVariable), out int parsed)
                ? Math.Max(1, Math.Min(60, parsed)) : 15;
            minimumFrameGap = Math.Max(1, 1000 / fps);
            maximumFrames = int.TryParse(Environment.GetEnvironmentVariable("SDV_THOR_MAX_EXPORT_FRAMES"),
                out int requestedFrames) ? Math.Max(0, requestedFrames) : 0;
            path = pathOverride ?? Environment.GetEnvironmentVariable("SDV_THOR_FRAME_PATH");
            if (string.IsNullOrWhiteSpace(path)) path = "/dev/shm/stardew-thor-frame";
        }

        internal string Path => path;
        internal bool Pending => pboPending != null && pboPending.Count > 0;

        internal void Publish(RenderTarget2D frame, bool capture = true)
        {
            if (failed || (maximumFrames > 0 && publishedFrames >= maximumFrames)
                || frame == null || frame.IsDisposed || frame.Width != width || frame.Height != height) return;
            long now = Environment.TickCount64;
            if (now - lastPublish < minimumFrameGap) return;
            try
            {
                EnsureOpen();
                if (usePbo)
                {
                    if (!ReadPbo(capture)) return;
                }
                else
                {
                    // MonoGame's RenderTarget2D.GetData intermittently aborts
                    // inside FEX.  Read directly into permanently pinned memory
                    // instead; client-memory glReadPixels is synchronous, so a
                    // complete BGRA frame is available when this call returns.
                    if (!capture) return;
                    glReadPixels(0, 0, width, height, GlBgra, GlUnsignedInt8888Rev,
                        pixelsPin.AddrOfPinnedObject());
                }
                int next = (activeBuffer + 1) % SharedBufferCount;
                view.WriteArray(HeaderSize + next * bytesPerFrame, pixels, 0, pixels.Length);
                sequence++;
                view.Write(40, DateTime.UtcNow.Ticks);
                view.Write(28, next);
                // Sequence is the commit marker and must be written last.
                view.Write(32, sequence);
                view.Flush();
                activeBuffer = next;
                lastPublish = now;
                publishedFrames++;
            }
            catch (Exception ex)
            {
                failed = true;
                ModEntry.Write("thor-frame-publish-failed " + ex.GetType().Name + ": " + ex.Message);
                Dispose();
            }
        }

        // The caller invokes Publish while the lower RenderTarget2D is bound.
        // Pixel-pack buffers keep the GPU readback asynchronous: this frame
        // is queued into one PBO while the previous completed PBO is published.
        private bool ReadPbo(bool capture)
        {
            if (pbo == null)
            {
                pbo = new uint[3];
                pboFences = new IntPtr[pbo.Length];
                pboPending = new Queue<int>(pbo.Length);
                pboFree = new Stack<int>(pbo.Length);
                glGenBuffers(pbo.Length, pbo);
                for (int i = 0; i < pbo.Length; i++)
                {
                    glBindBuffer(GlPixelPackBuffer, pbo[i]);
                    glBufferData(GlPixelPackBuffer, (IntPtr)bytesPerFrame, IntPtr.Zero, GlStreamRead);
                    pboFree.Push(i);
                }
                glBindBuffer(GlPixelPackBuffer, 0);
                ModEntry.Write("thor-frame-pbo-open buffers=" + pbo[0] + "," + pbo[1]);
            }

            bool ready = false;
            if (pboPending.Count > 0)
            {
                int readIndex = pboPending.Peek();
                uint status = glClientWaitSync(pboFences[readIndex], 0, 0);
                if (status == GlAlreadySignaled || status == GlConditionSatisfied)
                {
                    pboPending.Dequeue();
                    glDeleteSync(pboFences[readIndex]);
                    pboFences[readIndex] = IntPtr.Zero;
                    glBindBuffer(GlPixelPackBuffer, pbo[readIndex]);
                    IntPtr mapped = glMapBuffer(GlPixelPackBuffer, GlReadOnly);
                    if (mapped == IntPtr.Zero) throw new InvalidOperationException("glMapBuffer returned null");
                    Marshal.Copy(mapped, pixels, 0, bytesPerFrame);
                    glUnmapBuffer(GlPixelPackBuffer);
                    pboFree.Push(readIndex);
                    ready = true;
                }
            }

            if (capture && pboFree.Count > 0)
            {
                int writeIndex = pboFree.Pop();
                glBindBuffer(GlPixelPackBuffer, pbo[writeIndex]);
                // This PBO is only returned to the free stack after its fence
                // signals, so its storage can be reused without orphaning and
                // reallocating a full frame on every publish.
                glReadPixels(0, 0, width, height, GlBgra, GlUnsignedInt8888Rev, IntPtr.Zero);
                pboFences[writeIndex] = glFenceSync(GlSyncGpuCommandsComplete, 0);
                // Submit the transfer without waiting. Otherwise some drivers
                // leave the fence queued until a later swap, causing bursts.
                glFlush();
                pboPending.Enqueue(writeIndex);
            }
            glBindBuffer(GlPixelPackBuffer, 0);
            if (!ready) lastPublish = Environment.TickCount64;
            return ready;
        }

        private void EnsureOpen()
        {
            if (view != null) return;
            string directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            long length = HeaderSize + bytesPerFrame * (long)SharedBufferCount;
            file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            file.SetLength(length);
            mapping = MemoryMappedFile.CreateFromFile(file, null, length, MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.None, true);
            view = mapping.CreateViewAccessor(0, length, MemoryMappedFileAccess.ReadWrite);
            pixels = new byte[bytesPerFrame];
            pixelsPin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            pixelsPinned = true;
            view.Write(0, Magic);
            view.Write(8, 1);
            view.Write(12, width);
            view.Write(16, height);
            view.Write(20, stride);
            view.Write(24, bytesPerFrame);
            view.Write(28, activeBuffer);
            view.Write(32, sequence);
            view.Flush();
            ModEntry.Write("thor-frame-open path=" + path + "; size=" + width + "x" + height
                + "; fps=" + Math.Max(1, 1000 / minimumFrameGap) + "; max-frames=" + maximumFrames
                + "; readback=" + (usePbo ? "pbo" : "direct"));
        }

        public void Dispose()
        {
            view?.Dispose();
            mapping?.Dispose();
            file?.Dispose();
            view = null;
            mapping = null;
            file = null;
            if (pixelsPinned)
            {
                pixelsPin.Free();
                pixelsPinned = false;
            }
            pixels = null;
            if (pbo != null)
            {
                if (pboFences != null)
                    for (int i = 0; i < pboFences.Length; i++)
                        if (pboFences[i] != IntPtr.Zero) glDeleteSync(pboFences[i]);
                try { glDeleteBuffers(pbo.Length, pbo); }
                catch { }
                pbo = null;
                pboFences = null;
                pboPending = null;
                pboFree = null;
            }
        }
    }
}
