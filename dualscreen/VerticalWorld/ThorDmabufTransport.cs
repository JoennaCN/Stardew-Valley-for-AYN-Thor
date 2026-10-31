using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Rgdsplus.VerticalWorld
{
    // Imports native GBM scanout buffers as EGLImages and copies the currently
    // bound lower-screen FBO into them entirely on the GPU.
    internal sealed class ThorDmabufTransport : IDisposable
    {
        private const long Magic = 0x31464D4448544453;
        private const uint EglLinuxDmaBufExt = 0x3270;
        private const int EglNone = 0x3038;
        private const int EglWidth = 0x3057;
        private const int EglHeight = 0x3056;
        private const int EglFourcc = 0x3271;
        private const int EglPlane0Fd = 0x3272;
        private const int EglPlane0Offset = 0x3273;
        private const int EglPlane0Pitch = 0x3274;
        private const uint GlTexture2D = 0x0DE1;
        private const uint GlTextureMinFilter = 0x2801;
        private const uint GlTextureMagFilter = 0x2800;
        private const int GlNearest = 0x2600;
        private const uint GlFramebuffer = 0x8D40;
        private const uint GlReadFramebuffer = 0x8CA8;
        private const uint GlDrawFramebuffer = 0x8CA9;
        private const uint GlReadFramebufferBinding = 0x8CAA;
        private const uint GlDrawFramebufferBinding = 0x8CA6;
        private const uint GlColorAttachment0 = 0x8CE0;
        private const uint GlFramebufferComplete = 0x8CD5;
        private const uint GlColorBufferBit = 0x00004000;
        private const uint GlHandleTypeOpaqueFd = 0x9586;
        private const uint GlRgba8 = 0x8058;
        private const uint EglSyncNativeFenceAndroid = 0x3144;

        private readonly string metadataPath;
        private FileStream controlFile;
        private MemoryMappedFile controlMapping;
        private MemoryMappedViewAccessor control;
        private int[] fds;
        private uint[] textures, framebuffers, memories;
        private Socket fenceSocket;
        private IntPtr eglDisplay;
        private EglCreateSync createSync;
        private EglDestroySync destroySync;
        private EglDupNativeFenceFd dupNativeFenceFd;
        private bool explicitFence;
        private bool nativeFenceExport;
        private long nextAttempt;
        private bool failed;
        private GlCreateMemoryObjects createMemoryObjects;
        private GlDeleteMemoryObjects deleteMemoryObjects;
        private GlImportMemoryFd importMemoryFd;
        private GlTextureStorageMem2D textureStorageMem2D;
        private int publishSamples;

        internal ThorDmabufTransport(string path = "/dev/shm/stardew-thor-dmabuf.json")
        {
            metadataPath = path;
        }

        internal int Width { get; private set; }
        internal int Height { get; private set; }
        internal bool Ready => control != null;

        internal bool EnsureOpen()
        {
            int previousRead = 0, previousDraw = 0;
            bool bindingsCaptured = false;
            if (Ready) return true;
            if (failed || Environment.TickCount64 < nextAttempt) return false;
            nextAttempt = Environment.TickCount64 + 250;
            try
            {
                var metadata = JsonSerializer.Deserialize<Metadata>(File.ReadAllText(metadataPath));
                ModEntry.Write("thor-dmabuf-stage metadata");
                if (metadata == null || metadata.version != 1 || metadata.width < 1 || metadata.height < 1
                    || metadata.count < 2 || metadata.pitches == null || metadata.pitches.Length != metadata.count
                    || metadata.sizes == null || metadata.sizes.Length != metadata.count
                    || string.IsNullOrEmpty(metadata.control) || string.IsNullOrEmpty(metadata.socket))
                    return false;
                controlFile = new FileStream(metadata.control, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                controlMapping = MemoryMappedFile.CreateFromFile(controlFile, null, 64,
                    MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, true);
                control = controlMapping.CreateViewAccessor(0, 64, MemoryMappedFileAccess.ReadWrite);
                if (control.ReadInt64(0) != Magic) throw new InvalidDataException("DMA-BUF control magic mismatch");
                ModEntry.Write("thor-dmabuf-stage control");
                Width = metadata.width;
                Height = metadata.height;
                createMemoryObjects = Load<GlCreateMemoryObjects>("glCreateMemoryObjectsEXT");
                deleteMemoryObjects = Load<GlDeleteMemoryObjects>("glDeleteMemoryObjectsEXT");
                importMemoryFd = Load<GlImportMemoryFd>("glImportMemoryFdEXT");
                textureStorageMem2D = Load<GlTextureStorageMem2D>("glTextureStorageMem2DEXT");
                if (metadata.in_fence && !string.IsNullOrEmpty(metadata.fence_socket))
                {
                    eglDisplay = eglGetCurrentDisplay();
                    createSync = Load<EglCreateSync>("eglCreateSyncKHR");
                    destroySync = Load<EglDestroySync>("eglDestroySyncKHR");
                    dupNativeFenceFd = Load<EglDupNativeFenceFd>("eglDupNativeFenceFDANDROID");
                    fenceSocket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
                    fenceSocket.Connect(new UnixDomainSocketEndPoint(metadata.fence_socket));
                    explicitFence = eglDisplay != IntPtr.Zero;
                }
                nativeFenceExport = !explicitFence && metadata.in_fence
                    && metadata.implicit_fence_export;
                // Tell the native presenter whether this GL context can export
                // sync_file fences. Mode 2 lets the native process export the
                // dma-buf's Mesa implicit fence after this process flushes GL.
                control.Write(56, explicitFence ? 1 : nativeFenceExport ? 2 : 0);
                ModEntry.Write("thor-dmabuf-stage procedures");
                int count = metadata.count;
                ModEntry.Write("thor-dmabuf-stage receive-fds");
                fds = ReceiveFds(metadata.socket, count);
                ModEntry.Write("thor-dmabuf-stage received-fds=" + fds.Length);
                textures = new uint[count];
                framebuffers = new uint[count];
                memories = new uint[count];
                glGetIntegerv(GlReadFramebufferBinding, ref previousRead);
                glGetIntegerv(GlDrawFramebufferBinding, ref previousDraw);
                bindingsCaptured = true;
                glGenTextures(count, textures);
                glGenFramebuffers(count, framebuffers);
                createMemoryObjects(count, memories);
                for (int i = 0; i < count; i++)
                {
                    // glGenTextures only reserves a name. Bind it once so the
                    // object exists before EXT_memory_object DSA storage is set.
                    glBindTexture(GlTexture2D, textures[i]);
                    importMemoryFd(memories[i], (ulong)metadata.sizes[i], GlHandleTypeOpaqueFd, fds[i]);
                    // Ownership transfers to GL on a successful import.
                    fds[i] = -1;
                    textureStorageMem2D(textures[i], 1, GlRgba8, Width, Height, memories[i], 0);
                    ModEntry.Write("thor-dmabuf-stage memory=" + i);
                    glBindFramebuffer(GlFramebuffer, framebuffers[i]);
                    ModEntry.Write("thor-dmabuf-stage bind-framebuffer=" + i);
                    glFramebufferTexture2D(GlFramebuffer, GlColorAttachment0, GlTexture2D, textures[i], 0);
                    ModEntry.Write("thor-dmabuf-stage attach-texture=" + i);
                    if (glCheckFramebufferStatus(GlFramebuffer) != GlFramebufferComplete)
                        throw new InvalidOperationException("DMA-BUF framebuffer incomplete");
                    ModEntry.Write("thor-dmabuf-stage framebuffer=" + i);
                }
                glBindFramebuffer(GlReadFramebuffer, (uint)previousRead);
                glBindFramebuffer(GlDrawFramebuffer, (uint)previousDraw);
                bindingsCaptured = false;
                ModEntry.Write("thor-dmabuf-open size=" + Width + "x" + Height + "; buffers=" + count
                    + "; transport=memory-object-fd");
                return true;
            }
            catch (FileNotFoundException)
            {
                if (bindingsCaptured) RestoreBindings(previousRead, previousDraw);
                DisposePartial(); return false;
            }
            catch (Exception ex)
            {
                if (bindingsCaptured) RestoreBindings(previousRead, previousDraw);
                ModEntry.Write("thor-dmabuf-open-failed " + ex.GetType().Name + ": " + ex.Message);
                DisposePartial();
                failed = true;
                return false;
            }
        }

        private static void RestoreBindings(int read, int draw)
        {
            try
            {
                glBindFramebuffer(GlReadFramebuffer, (uint)read);
                glBindFramebuffer(GlDrawFramebuffer, (uint)draw);
            }
            catch { }
        }

        internal bool Publish()
        {
            if (!EnsureOpen()) return false;
            bool trace = publishSamples++ == 0;
            if (trace) ModEntry.Write("thor-dmabuf-publish begin");
            long sequence = control.ReadInt64(32);
            long acknowledged = control.ReadInt64(40);
            if (sequence != acknowledged) return false;
            int shown = control.ReadInt32(28);
            int next = (shown + 1 + framebuffers.Length) % framebuffers.Length;
            int previousRead = 0, previousDraw = 0;
            glGetIntegerv(GlReadFramebufferBinding, ref previousRead);
            glGetIntegerv(GlDrawFramebufferBinding, ref previousDraw);
            if (trace) ModEntry.Write("thor-dmabuf-publish bindings");
            glBindFramebuffer(GlReadFramebuffer, (uint)previousRead);
            glBindFramebuffer(GlDrawFramebuffer, framebuffers[next]);
            if (trace) ModEntry.Write("thor-dmabuf-publish targets");
            glBlitFramebuffer(0, 0, Width, Height, 0, 0, Width, Height,
                GlColorBufferBit, GlNearest);
            if (trace) ModEntry.Write("thor-dmabuf-publish blit");
            long nextSequence = sequence + 1;
            if (explicitFence)
            {
                IntPtr sync = createSync(eglDisplay, EglSyncNativeFenceAndroid, new[] { EglNone });
                if (sync == IntPtr.Zero) throw new InvalidOperationException("Native fence creation failed");
                int fenceFd = -1;
                try
                {
                    glFlush();
                    fenceFd = dupNativeFenceFd(eglDisplay, sync);
                    if (fenceFd < 0) throw new InvalidOperationException("Native fence export failed");
                    SendFenceFd(fenceSocket, fenceFd, nextSequence, next);
                }
                finally
                {
                    if (fenceFd >= 0) close(fenceFd);
                    destroySync(eglDisplay, sync);
                }
                if (trace) ModEntry.Write("thor-dmabuf-publish explicit-fence");
            }
            else if (nativeFenceExport && StardewValley.Game1.gameMode == 3)
            {
                glFlush();
                if (trace) ModEntry.Write("thor-dmabuf-publish implicit-fence-export");
            }
            else
            {
                glFinish();
                if (trace) ModEntry.Write(nativeFenceExport
                    ? "thor-dmabuf-publish loading-finish"
                    : "thor-dmabuf-publish finish-fallback");
            }
            glBindFramebuffer(GlReadFramebuffer, (uint)previousRead);
            glBindFramebuffer(GlDrawFramebuffer, (uint)previousDraw);
            control.Write(24, next);
            control.Write(48, DateTime.UtcNow.Ticks);
            control.Write(32, nextSequence);
            if (trace) ModEntry.Write("thor-dmabuf-publish submitted");
            return true;
        }

        public void Dispose()
        {
            DisposePartial();
            failed = true;
        }

        private void DisposePartial()
        {
            if (textures != null) try { glDeleteTextures(textures.Length, textures); } catch { }
            if (framebuffers != null) try { glDeleteFramebuffers(framebuffers.Length, framebuffers); } catch { }
            if (memories != null && deleteMemoryObjects != null)
                try { deleteMemoryObjects(memories.Length, memories); } catch { }
            if (fds != null) foreach (int fd in fds) if (fd >= 0) close(fd);
            fenceSocket?.Dispose();
            textures = framebuffers = memories = null; fds = null;
            control?.Dispose(); controlMapping?.Dispose(); controlFile?.Dispose();
            control = null; controlMapping = null; controlFile = null; fenceSocket = null;
            explicitFence = false;
            nativeFenceExport = false;
        }

        private static T Load<T>(string name) where T : Delegate
        {
            IntPtr address = eglGetProcAddress(name);
            if (address == IntPtr.Zero) throw new MissingMethodException(name);
            return Marshal.GetDelegateForFunctionPointer<T>(address);
        }

        private static int[] ReceiveFds(string path, int count)
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(path));
            IntPtr data = Marshal.AllocHGlobal(1);
            IntPtr iovecMemory = Marshal.AllocHGlobal(Marshal.SizeOf<Iovec>());
            int controlSize = 16 + ((count * sizeof(int) + 7) & ~7);
            IntPtr controlMemory = Marshal.AllocHGlobal(controlSize);
            try
            {
                Marshal.StructureToPtr(new Iovec { Base = data, Length = (UIntPtr)1 }, iovecMemory, false);
                var message = new MessageHeader
                {
                    Iov = iovecMemory,
                    IovLength = (UIntPtr)1,
                    Control = controlMemory,
                    ControlLength = (UIntPtr)controlSize,
                };
                int received = recvmsg(socket.SafeHandle.DangerousGetHandle().ToInt32(), ref message, 0);
                if (received != 1) throw new IOException("SCM_RIGHTS receive failed");
                var header = Marshal.PtrToStructure<ControlHeader>(controlMemory);
                if (header.Level != 1 || header.Type != 1 || header.Length.ToUInt64() < (ulong)(16 + count * 4))
                    throw new InvalidDataException("Invalid SCM_RIGHTS control message");
                var result = new int[count];
                Marshal.Copy(IntPtr.Add(controlMemory, 16), result, 0, count);
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(controlMemory);
                Marshal.FreeHGlobal(iovecMemory);
                Marshal.FreeHGlobal(data);
            }
        }

        private static void SendFenceFd(Socket socket, int fd, long sequence, int index)
        {
            IntPtr data = Marshal.AllocHGlobal(12);
            IntPtr iovecMemory = Marshal.AllocHGlobal(Marshal.SizeOf<Iovec>());
            IntPtr controlMemory = Marshal.AllocHGlobal(24);
            try
            {
                Marshal.WriteInt64(data, sequence);
                Marshal.WriteInt32(data, 8, index);
                Marshal.StructureToPtr(new Iovec { Base = data, Length = (UIntPtr)12 }, iovecMemory, false);
                Marshal.StructureToPtr(new ControlHeader
                {
                    Length = (UIntPtr)20,
                    Level = 1,
                    Type = 1,
                }, controlMemory, false);
                Marshal.WriteInt32(IntPtr.Add(controlMemory, 16), fd);
                var message = new MessageHeader
                {
                    Iov = iovecMemory,
                    IovLength = (UIntPtr)1,
                    Control = controlMemory,
                    ControlLength = (UIntPtr)24,
                };
                if (sendmsg(socket.SafeHandle.DangerousGetHandle().ToInt32(), ref message, 0) != 12)
                    throw new IOException("Native fence send failed");
            }
            finally
            {
                Marshal.FreeHGlobal(controlMemory);
                Marshal.FreeHGlobal(iovecMemory);
                Marshal.FreeHGlobal(data);
            }
        }

        private sealed class Metadata
        {
            public int version { get; set; }
            public int width { get; set; }
            public int height { get; set; }
            public int fourcc { get; set; }
            public int count { get; set; }
            public int[] pitches { get; set; }
            public int[] sizes { get; set; }
            public string control { get; set; }
            public string socket { get; set; }
            public string fence_socket { get; set; }
            public bool in_fence { get; set; }
            public bool implicit_fence_export { get; set; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Iovec { public IntPtr Base; public UIntPtr Length; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MessageHeader
        {
            public IntPtr Name;
            public uint NameLength;
            public IntPtr Iov;
            public UIntPtr IovLength;
            public IntPtr Control;
            public UIntPtr ControlLength;
            public int Flags;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ControlHeader
        {
            public UIntPtr Length;
            public int Level;
            public int Type;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlCreateMemoryObjects(int count, uint[] memories);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlDeleteMemoryObjects(int count, uint[] memories);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlImportMemoryFd(uint memory, ulong size, uint handleType, int fd);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlTextureStorageMem2D(uint texture, int levels, uint internalFormat,
            int width, int height, uint memory, ulong offset);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr EglCreateSync(IntPtr display, uint type, int[] attributes);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int EglDestroySync(IntPtr display, IntPtr sync);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int EglDupNativeFenceFd(IntPtr display, IntPtr sync);

        [DllImport("libEGL.so.1", CharSet = CharSet.Ansi)] private static extern IntPtr eglGetProcAddress(string name);
        [DllImport("libEGL.so.1")] private static extern IntPtr eglGetCurrentDisplay();
        [DllImport("libc.so.6")] private static extern int close(int fd);
        [DllImport("libc.so.6", SetLastError = true)] private static extern int recvmsg(int socket, ref MessageHeader message, int flags);
        [DllImport("libc.so.6", SetLastError = true)] private static extern int sendmsg(int socket, ref MessageHeader message, int flags);
        [DllImport("libGL.so.1")] private static extern void glGenTextures(int count, uint[] values);
        [DllImport("libGL.so.1")] private static extern void glDeleteTextures(int count, uint[] values);
        [DllImport("libGL.so.1")] private static extern void glBindTexture(uint target, uint texture);
        [DllImport("libGL.so.1")] private static extern void glTexParameteri(uint target, uint name, int value);
        [DllImport("libGL.so.1")] private static extern void glGenFramebuffers(int count, uint[] values);
        [DllImport("libGL.so.1")] private static extern void glDeleteFramebuffers(int count, uint[] values);
        [DllImport("libGL.so.1")] private static extern void glBindFramebuffer(uint target, uint framebuffer);
        [DllImport("libGL.so.1")] private static extern void glFramebufferTexture2D(uint target, uint attachment,
            uint textureTarget, uint texture, int level);
        [DllImport("libGL.so.1")] private static extern uint glCheckFramebufferStatus(uint target);
        [DllImport("libGL.so.1")] private static extern void glGetIntegerv(uint name, ref int value);
        [DllImport("libGL.so.1")] private static extern void glBlitFramebuffer(int srcX0, int srcY0, int srcX1, int srcY1,
            int dstX0, int dstY0, int dstX1, int dstY1, uint mask, int filter);
        [DllImport("libGL.so.1")] private static extern void glFinish();
        [DllImport("libGL.so.1")] private static extern void glFlush();
    }
}
