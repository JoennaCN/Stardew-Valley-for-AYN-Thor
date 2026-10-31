"""Zero-copy lower-panel presenter for AYN Thor.

Allocates three scanout/render GBM buffers, exposes their DMA-BUF descriptors
through /proc, and page-flips buffers published by the FEX game through a tiny
shared control block. No game pixels are ever copied through CPU memory.
"""

from __future__ import annotations

import ctypes as C
import array
import fcntl
import json
import mmap
import os
import select
import signal
import socket
import struct
import sys
import time

from app import panel_to_canvas
from lease import LeaseBusy, LeaseError, LeasedPanel
from touch import TouchInput

MAGIC = 0x31464D4448544453  # "SDVTHDMF1"
CONTROL_SIZE = 64
HEADER = struct.Struct("<Qiiiiiiqqq")
# GL_RGBA8 stores bytes as R, G, B, A.  On little-endian DRM that byte layout
# is XBGR8888 (XR24 would interpret the same storage as B, G, R, X).
DRM_FORMAT_XBGR8888 = 0x34324258
GBM_BO_USE_SCANOUT = 1 << 0
GBM_BO_USE_RENDERING = 1 << 2
GBM_BO_USE_LINEAR = 1 << 4
TOUCH = struct.Struct("<Bff")
FENCE_MESSAGE = struct.Struct("<qi")
# Linux dma-buf implicit-sync export. This converts the GPU write fence
# attached by Mesa after glFlush into a sync_file accepted by DRM IN_FENCE_FD.
DMA_BUF_IOCTL_EXPORT_SYNC_FILE = 0xC0086202
DMA_BUF_SYNC_READ = 1 << 0
DOWN, MOVE, UP = 1, 2, 3


class GbmBuffer:
    def __init__(self, bo, fd: int, pitch: int, handle: int, fb_id: int):
        self.bo = bo
        self.fd = fd
        self.pitch = pitch
        self.handle = handle
        self.fb_id = fb_id


class DmabufPresenter:
    def __init__(self, panel: LeasedPanel, metadata_path: str, control_path: str,
                 socket_path: str, fence_path: str, touch_path: str, orientation: str,
                 touchscreen: str | None):
        self.panel = panel
        self.metadata_path = metadata_path
        self.control_path = control_path
        self.socket_path = socket_path
        self.fence_path = fence_path
        self.touch_path = touch_path
        self.orientation = orientation
        self.stopping = False
        self.pending_sequence = 0
        self.pending_index = -1
        self.fences = {}
        self.shown_index = -1
        self.last_point = (0.0, 0.0)
        self.gbm = C.CDLL("libgbm.so.1")
        self.render_fd = os.open("/dev/dri/renderD128", os.O_RDWR | os.O_CLOEXEC)
        self._configure_gbm()
        self.device = self.gbm.gbm_create_device(self.render_fd)
        if not self.device:
            raise LeaseError("gbm_create_device_failed")
        self.buffers = [self._allocate_buffer() for _ in range(3)]
        self.implicit_fence_export = all(self._probe_sync_export(item.fd)
                                         for item in self.buffers)
        self.control_file = open(control_path, "w+b", buffering=0)
        self.control_file.truncate(CONTROL_SIZE)
        self.control = mmap.mmap(self.control_file.fileno(), CONTROL_SIZE, mmap.MAP_SHARED,
                                 mmap.PROT_READ | mmap.PROT_WRITE)
        HEADER.pack_into(self.control, 0, MAGIC, 1, panel.width, panel.height, len(self.buffers),
                         -1, -1, 0, 0, time.time_ns())
        self.control.flush()
        try:
            os.unlink(socket_path)
        except FileNotFoundError:
            pass
        self.server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM | socket.SOCK_CLOEXEC)
        self.server.bind(socket_path)
        self.server.listen(2)
        self.server.setblocking(False)
        try:
            os.unlink(fence_path)
        except FileNotFoundError:
            pass
        self.fence_server = socket.socket(socket.AF_UNIX, socket.SOCK_DGRAM | socket.SOCK_CLOEXEC)
        self.fence_server.bind(fence_path)
        self.fence_server.setblocking(False)
        self._write_metadata()
        self.touch = TouchInput(panel.touch_socket, touchscreen)
        self.touch_sender = socket.socket(socket.AF_UNIX, socket.SOCK_DGRAM)
        panel.touch_socket.setblocking(False)
        panel.request_touch()

    def _configure_gbm(self) -> None:
        self.gbm.gbm_create_device.restype = C.c_void_p
        self.gbm.gbm_create_device.argtypes = [C.c_int]
        self.gbm.gbm_device_destroy.argtypes = [C.c_void_p]
        self.gbm.gbm_bo_create.restype = C.c_void_p
        self.gbm.gbm_bo_create.argtypes = [C.c_void_p, C.c_uint32, C.c_uint32,
                                           C.c_uint32, C.c_uint32]
        self.gbm.gbm_bo_get_fd.argtypes = [C.c_void_p]
        self.gbm.gbm_bo_get_stride.restype = C.c_uint32
        self.gbm.gbm_bo_get_stride.argtypes = [C.c_void_p]
        self.gbm.gbm_bo_destroy.argtypes = [C.c_void_p]

    def _allocate_buffer(self) -> GbmBuffer:
        flags = GBM_BO_USE_SCANOUT | GBM_BO_USE_RENDERING | GBM_BO_USE_LINEAR
        bo = self.gbm.gbm_bo_create(self.device, self.panel.width, self.panel.height,
                                    DRM_FORMAT_XBGR8888, flags)
        if not bo:
            raise LeaseError("gbm_linear_buffer_failed")
        fd = self.gbm.gbm_bo_get_fd(bo)
        pitch = self.gbm.gbm_bo_get_stride(bo)
        if fd < 0:
            self.gbm.gbm_bo_destroy(bo)
            raise LeaseError("gbm_fd_failed")
        drm = self.panel._drm
        drm.drmPrimeFDToHandle.argtypes = [C.c_int, C.c_int, C.POINTER(C.c_uint32)]
        drm.drmModeAddFB2.argtypes = [C.c_int, C.c_uint32, C.c_uint32, C.c_uint32,
                                      C.POINTER(C.c_uint32), C.POINTER(C.c_uint32),
                                      C.POINTER(C.c_uint32), C.POINTER(C.c_uint32), C.c_uint32]
        handle = C.c_uint32()
        if drm.drmPrimeFDToHandle(self.panel.fd, fd, C.byref(handle)) != 0:
            os.close(fd)
            self.gbm.gbm_bo_destroy(bo)
            raise LeaseError("prime_import_failed")
        handles = (C.c_uint32 * 4)(handle.value, 0, 0, 0)
        pitches = (C.c_uint32 * 4)(pitch, 0, 0, 0)
        offsets = (C.c_uint32 * 4)(0, 0, 0, 0)
        fb = C.c_uint32()
        if drm.drmModeAddFB2(self.panel.fd, self.panel.width, self.panel.height,
                             DRM_FORMAT_XBGR8888, handles, pitches, offsets,
                             C.byref(fb), 0) != 0:
            os.close(fd)
            self.gbm.gbm_bo_destroy(bo)
            raise LeaseError("add_external_fb_failed")
        return GbmBuffer(bo, fd, pitch, handle.value, fb.value)

    def _write_metadata(self) -> None:
        value = {
            "version": 1,
            "pid": os.getpid(),
            "width": self.panel.width,
            "height": self.panel.height,
            "fourcc": DRM_FORMAT_XBGR8888,
            "count": len(self.buffers),
            "pitches": [item.pitch for item in self.buffers],
            "sizes": [item.pitch * self.panel.height for item in self.buffers],
            "control": self.control_path,
            "socket": self.socket_path,
            "fence_socket": self.fence_path,
            "in_fence": "IN_FENCE_FD" in self.panel._props[self.panel._plane],
            "implicit_fence_export": self.implicit_fence_export,
            "touch": self.touch_path,
        }
        temporary = self.metadata_path + ".tmp"
        with open(temporary, "w") as handle:
            json.dump(value, handle, separators=(",", ":"))
        os.replace(temporary, self.metadata_path)

    def run(self) -> None:
        while not self.stopping:
            watched = [self.panel.fd, self.server.fileno(), self.fence_server.fileno(), *self.touch.fds]
            try:
                # The control block has no pollable notification fd yet. A 1ms
                # scan keeps the lower panel close to the game's next vblank;
                # 4.2ms polling could miss a refresh and look one frame behind.
                readable, _, _ = select.select(watched, [], [], 1 / 1000)
            except InterruptedError:
                continue
            if self.panel.fd in readable:
                self.panel.drain()
                if self.pending_sequence:
                    self.pending_sequence = 0
                    self.pending_index = -1
            if self.server.fileno() in readable:
                try:
                    connection, _ = self.server.accept()
                    rights = array.array("i", [item.fd for item in self.buffers])
                    connection.sendmsg([b"D"], [(socket.SOL_SOCKET, socket.SCM_RIGHTS, rights)])
                    connection.close()
                except (BlockingIOError, BrokenPipeError):
                    pass
            if self.fence_server.fileno() in readable:
                self._receive_fences()
            for fd in self.touch.fds:
                if fd in readable:
                    for event in self.touch.read(fd):
                        self._send_touch(*event)
            if self.pending_sequence:
                continue
            before = struct.unpack_from("<q", self.control, 32)[0]
            active = struct.unpack_from("<i", self.control, 24)[0]
            after = struct.unpack_from("<q", self.control, 32)[0]
            acknowledged = struct.unpack_from("<q", self.control, 40)[0]
            if before == after and before > acknowledged and 0 <= active < len(self.buffers):
                fence_mode = struct.unpack_from("<i", self.control, 56)[0]
                fence_fd = self.fences.pop((before, active), None) if fence_mode == 1 else -1
                if fence_mode == 1 and fence_fd is None:
                    continue
                if fence_mode == 2:
                    try:
                        fence_fd = self._export_sync_file(self.buffers[active].fd)
                    except OSError:
                        # Do not scan out an unfinished buffer. The game sees
                        # the unchanged acknowledgement and simply retries.
                        continue
                try:
                    if self.panel.present_fb(self.buffers[active].fb_id, fence_fd):
                        self.pending_sequence = before
                        self.pending_index = active
                        # Acknowledge when DRM accepts the fenced commit, not a
                        # vblank later. Triple buffering then lets the game
                        # prepare exactly one following frame while this flip is
                        # pending, avoiding periodic missed frames at 60 Hz.
                        self.shown_index = active
                        struct.pack_into("<i", self.control, 28, self.shown_index)
                        struct.pack_into("<q", self.control, 40, before)
                finally:
                    if fence_fd >= 0:
                        os.close(fence_fd)

    @staticmethod
    def _export_sync_file(fd: int) -> int:
        payload = bytearray(struct.pack("<Ii", DMA_BUF_SYNC_READ, -1))
        fcntl.ioctl(fd, DMA_BUF_IOCTL_EXPORT_SYNC_FILE, payload, True)
        sync_fd = struct.unpack("<Ii", payload)[1]
        if sync_fd < 0:
            raise OSError("dma_buf_export_sync_file_failed")
        return sync_fd

    @classmethod
    def _probe_sync_export(cls, fd: int) -> bool:
        try:
            sync_fd = cls._export_sync_file(fd)
            os.close(sync_fd)
            return True
        except OSError:
            return False

    def _receive_fences(self) -> None:
        while True:
            try:
                data, ancillary, _, _ = self.fence_server.recvmsg(
                    FENCE_MESSAGE.size, socket.CMSG_SPACE(struct.calcsize("i")))
            except BlockingIOError:
                return
            fds = [
                struct.unpack("i", payload[:struct.calcsize("i")])[0]
                for level, kind, payload in ancillary
                if level == socket.SOL_SOCKET and kind == socket.SCM_RIGHTS
            ]
            if len(data) != FENCE_MESSAGE.size or not fds:
                for fd in fds:
                    os.close(fd)
                continue
            sequence, index = FENCE_MESSAGE.unpack(data)
            key = (sequence, index)
            old = self.fences.pop(key, None)
            if old is not None:
                os.close(old)
            self.fences[key] = fds[0]
            for fd in fds[1:]:
                os.close(fd)

    def _send_touch(self, kind: str, nx, ny) -> None:
        code = {"down": DOWN, "move": MOVE, "up": UP}.get(kind)
        if code is None:
            return
        if kind != "up":
            self.last_point = panel_to_canvas(
                self.orientation, float(nx), float(ny), self.panel.width, self.panel.height)
        try:
            self.touch_sender.sendto(TOUCH.pack(code, *self.last_point), self.touch_path)
        except OSError:
            pass

    def close(self) -> None:
        self.touch.close()
        self.touch_sender.close()
        self.control.close()
        self.control_file.close()
        self.server.close()
        self.fence_server.close()
        for fd in self.fences.values():
            os.close(fd)
        for path in (self.metadata_path, self.control_path, self.socket_path, self.fence_path):
            try:
                os.unlink(path)
            except FileNotFoundError:
                pass
        for item in self.buffers:
            os.close(item.fd)
            self.gbm.gbm_bo_destroy(item.bo)
        self.gbm.gbm_device_destroy(self.device)
        os.close(self.render_fd)
        self.panel.close()


def main() -> int:
    metadata = os.environ.get("SDV_THOR_DMABUF_METADATA", "/dev/shm/stardew-thor-dmabuf.json")
    control = os.environ.get("SDV_THOR_DMABUF_CONTROL", "/dev/shm/stardew-thor-dmabuf-control")
    socket_path = os.environ.get("SDV_THOR_DMABUF_SOCKET", "/dev/shm/stardew-thor-dmabuf.sock")
    fence_path = os.environ.get("SDV_THOR_DMABUF_FENCE_SOCKET", "/dev/shm/stardew-thor-dmabuf-fence.sock")
    try:
        panel = LeasedPanel()
        presenter = DmabufPresenter(
            panel, metadata, control, socket_path, fence_path,
            os.environ.get("SDV_THOR_TOUCH_PATH", "/dev/shm/stardew-thor-touch"),
            os.environ.get("ARMADA_PANEL_ORIENTATION", "right"),
            os.environ.get("ARMADA_SECONDARY_TOUCHSCREEN"),
        )
    except LeaseBusy:
        return 4
    except (LeaseError, OSError, RuntimeError) as error:
        print(f"stardew-thor-dmabuf: {error}", file=sys.stderr)
        return 3
    ready_path = os.environ.get("SDV_THOR_READY_PATH")
    if ready_path:
        with open(ready_path, "w") as handle:
            handle.write(os.environ.get("SDV_THOR_READY_VALUE", "ready") + "\n")

    def stop(*_args):
        presenter.stopping = True

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    try:
        presenter.run()
    finally:
        presenter.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
