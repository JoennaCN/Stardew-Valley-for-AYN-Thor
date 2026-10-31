"""The bottom panel, borrowed from gamescope as a DRM lease and driven with atomic commits.

gamescope's lease socket (Armada's patches) answers a connection with one byte: "L" with
the lease's file descriptor attached, or "B" when someone else holds it. The connection
stays open for as long as the lease is used; closing it returns the panel, which
gamescope then blanks. Writing "I" asks for the panel's touch input over the same socket.

The structures below mirror libdrm's xf86drmMode.h; only the leading fields that are read
are declared, since libdrm allocates them.
"""

from __future__ import annotations

import ctypes as C
import errno
import fcntl
import mmap
import os
import select
import socket
import struct

LEASE_SOCKET = "/tmp/gamescope-lease.sock"

DRM_CLIENT_CAP_UNIVERSAL_PLANES = 2
DRM_CLIENT_CAP_ATOMIC = 3
DRM_MODE_OBJECT_CRTC = 0xCCCCCCCC
DRM_MODE_OBJECT_CONNECTOR = 0xC0C0C0C0
DRM_MODE_OBJECT_PLANE = 0xEEEEEEEE
DRM_MODE_TYPE_PREFERRED = 1 << 3
DRM_PLANE_TYPE_PRIMARY = 1
DRM_MODE_PAGE_FLIP_EVENT = 0x01
DRM_MODE_ATOMIC_NONBLOCK = 0x0200
DRM_MODE_ATOMIC_ALLOW_MODESET = 0x0400
DRM_CLOEXEC = 0x01
DRM_RDWR = 0x02


def _iowr(nr: int, size: int) -> int:
    # _IOWR('d', nr, size) on Linux: direction read|write, then size, type and number.
    return (3 << 30) | (size << 16) | (ord("d") << 8) | nr


class _CreateDumb(C.Structure):
    _fields_ = [("height", C.c_uint32), ("width", C.c_uint32), ("bpp", C.c_uint32), ("flags", C.c_uint32),
                ("handle", C.c_uint32), ("pitch", C.c_uint32), ("size", C.c_uint64)]


class _MapDumb(C.Structure):
    _fields_ = [("handle", C.c_uint32), ("pad", C.c_uint32), ("offset", C.c_uint64)]


DRM_IOCTL_MODE_CREATE_DUMB = _iowr(0xB2, C.sizeof(_CreateDumb))
DRM_IOCTL_MODE_MAP_DUMB = _iowr(0xB3, C.sizeof(_MapDumb))


class _ModeRes(C.Structure):
    _fields_ = [("count_fbs", C.c_int), ("fbs", C.POINTER(C.c_uint32)),
                ("count_crtcs", C.c_int), ("crtcs", C.POINTER(C.c_uint32)),
                ("count_connectors", C.c_int), ("connectors", C.POINTER(C.c_uint32))]


class _PlaneRes(C.Structure):
    _fields_ = [("count_planes", C.c_uint32), ("planes", C.POINTER(C.c_uint32))]


class _ModeInfo(C.Structure):
    _fields_ = [("clock", C.c_uint32),
                ("hdisplay", C.c_uint16), ("hsync_start", C.c_uint16), ("hsync_end", C.c_uint16),
                ("htotal", C.c_uint16), ("hskew", C.c_uint16),
                ("vdisplay", C.c_uint16), ("vsync_start", C.c_uint16), ("vsync_end", C.c_uint16),
                ("vtotal", C.c_uint16), ("vscan", C.c_uint16),
                ("vrefresh", C.c_uint32), ("flags", C.c_uint32), ("type", C.c_uint32), ("name", C.c_char * 32)]


class _Connector(C.Structure):
    _fields_ = [("connector_id", C.c_uint32), ("encoder_id", C.c_uint32), ("connector_type", C.c_uint32),
                ("connector_type_id", C.c_uint32), ("connection", C.c_int), ("mm_width", C.c_uint32),
                ("mm_height", C.c_uint32), ("subpixel", C.c_int),
                ("count_modes", C.c_int), ("modes", C.POINTER(_ModeInfo))]


class _ObjectProperties(C.Structure):
    _fields_ = [("count_props", C.c_uint32), ("props", C.POINTER(C.c_uint32)),
                ("prop_values", C.POINTER(C.c_uint64))]


class _Property(C.Structure):
    _fields_ = [("prop_id", C.c_uint32), ("flags", C.c_uint32), ("name", C.c_char * 32)]


class LeaseError(RuntimeError):
    pass


class LeaseBusy(LeaseError):
    """Someone else holds the panel: Armada's own bottom-screen session, or another plugin."""


def receive_lease(path: str = LEASE_SOCKET) -> tuple:
    """Connects to gamescope's lease socket. Returns (socket, lease fd); keep the socket open."""
    sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM | socket.SOCK_CLOEXEC)
    try:
        sock.connect(path)
        data, ancillary, _, _ = sock.recvmsg(1, socket.CMSG_SPACE(struct.calcsize("i")))
    except OSError:
        sock.close()
        raise
    fds = [
        struct.unpack("i", payload[: struct.calcsize("i")])[0]
        for level, kind, payload in ancillary
        if level == socket.SOL_SOCKET and kind == socket.SCM_RIGHTS
    ]
    if data == b"L" and fds:
        return sock, fds[0]
    for fd in fds:
        os.close(fd)
    sock.close()
    if data == b"B":
        raise LeaseBusy("lease_busy")
    raise LeaseError(f"unexpected_reply:{data!r}")


def _libdrm():
    drm = C.CDLL("libdrm.so.2", use_errno=True)
    drm.drmSetClientCap.argtypes = [C.c_int, C.c_uint64, C.c_uint64]
    drm.drmModeGetResources.restype = C.POINTER(_ModeRes)
    drm.drmModeGetResources.argtypes = [C.c_int]
    drm.drmModeFreeResources.argtypes = [C.POINTER(_ModeRes)]
    drm.drmModeGetPlaneResources.restype = C.POINTER(_PlaneRes)
    drm.drmModeGetPlaneResources.argtypes = [C.c_int]
    drm.drmModeFreePlaneResources.argtypes = [C.POINTER(_PlaneRes)]
    drm.drmModeGetConnector.restype = C.POINTER(_Connector)
    drm.drmModeGetConnector.argtypes = [C.c_int, C.c_uint32]
    drm.drmModeFreeConnector.argtypes = [C.POINTER(_Connector)]
    drm.drmModeObjectGetProperties.restype = C.POINTER(_ObjectProperties)
    drm.drmModeObjectGetProperties.argtypes = [C.c_int, C.c_uint32, C.c_uint32]
    drm.drmModeFreeObjectProperties.argtypes = [C.POINTER(_ObjectProperties)]
    drm.drmModeGetProperty.restype = C.POINTER(_Property)
    drm.drmModeGetProperty.argtypes = [C.c_int, C.c_uint32]
    drm.drmModeFreeProperty.argtypes = [C.POINTER(_Property)]
    drm.drmModeCreatePropertyBlob.argtypes = [C.c_int, C.c_void_p, C.c_size_t, C.POINTER(C.c_uint32)]
    drm.drmModeAddFB.argtypes = [C.c_int, C.c_uint32, C.c_uint32, C.c_uint8, C.c_uint8, C.c_uint32, C.c_uint32,
                                 C.POINTER(C.c_uint32)]
    drm.drmModeAtomicAlloc.restype = C.c_void_p
    drm.drmModeAtomicAddProperty.argtypes = [C.c_void_p, C.c_uint32, C.c_uint32, C.c_uint64]
    drm.drmModeAtomicCommit.argtypes = [C.c_int, C.c_void_p, C.c_uint32, C.c_void_p]
    drm.drmModeAtomicFree.argtypes = [C.c_void_p]
    drm.drmPrimeHandleToFD.argtypes = [C.c_int, C.c_uint32, C.c_uint32, C.POINTER(C.c_int)]
    return drm


class Buffer:
    def __init__(self, fb_id: int, handle: int, pitch: int, size: int, memory: mmap.mmap):
        self.fb_id = fb_id
        self.handle = handle
        self.pitch = pitch
        self.size = size
        self.memory = memory


class LeasedPanel:
    """Two XRGB8888 dumb buffers on the leased CRTC, flipped with nonblocking atomic commits.

    `fd` becomes readable when a flip lands; call `drain()` then. `touch_socket` is the lease
    socket, which also carries touch events once `request_touch()` has been sent.
    """

    def __init__(self, path: str = LEASE_SOCKET):
        self.touch_socket, self.fd = receive_lease(path)
        try:
            self._drm = _libdrm()
            self._setup()
        except Exception:
            self.close()
            raise

    def _setup(self) -> None:
        drm = self._drm
        drm.drmSetClientCap(self.fd, DRM_CLIENT_CAP_UNIVERSAL_PLANES, 1)
        # The leased plane isn't its CRTC's legacy primary, so only atomic modesetting works.
        if drm.drmSetClientCap(self.fd, DRM_CLIENT_CAP_ATOMIC, 1) != 0:
            raise LeaseError("no_atomic")
        self._crtc, self._connector, self._mode = self._crtc_connector_mode()
        self._plane = self._pick_plane()
        self.width, self.height = self._mode.hdisplay, self._mode.vdisplay
        self._props = {
            self._crtc: self._property_ids(self._crtc, DRM_MODE_OBJECT_CRTC),
            self._connector: self._property_ids(self._connector, DRM_MODE_OBJECT_CONNECTOR),
            self._plane: self._property_ids(self._plane, DRM_MODE_OBJECT_PLANE),
        }
        self.buffers = [self._dumb_buffer(), self._dumb_buffer()]
        self._shown = 0
        self._flip_pending = False
        self._commit(self.buffers[0].fb_id, modeset=True)

    def request_touch(self) -> None:
        try:
            self.touch_socket.send(b"I")
        except OSError:
            pass

    @property
    def back_index(self) -> int:
        return 1 - self._shown

    def present(self) -> bool:
        """Shows the back buffer at the next vblank."""
        if not self.wait_flip(0):
            return False
        target = self.back_index
        self._commit(self.buffers[target].fb_id, modeset=False)
        self._shown = target
        self._flip_pending = True
        return True

    def present_fb(self, fb_id: int, in_fence_fd: int = -1) -> bool:
        """Queues an externally allocated framebuffer for the next vblank."""
        if not self.wait_flip(0):
            return False
        self._commit(fb_id, modeset=False, in_fence_fd=in_fence_fd)
        self._flip_pending = True
        return True

    def export_buffer_fd(self, index: int) -> int:
        """Returns a close-on-exec DMA-BUF fd for one scanout buffer."""
        exported = C.c_int(-1)
        buffer = self.buffers[index]
        if self._drm.drmPrimeHandleToFD(
                self.fd, buffer.handle, DRM_CLOEXEC | DRM_RDWR, C.byref(exported)) != 0:
            raise LeaseError("prime_export_failed")
        return exported.value

    def wait_flip(self, timeout: float) -> bool:
        if self._flip_pending and select.select([self.fd], [], [], timeout)[0]:
            self.drain()
        return not self._flip_pending

    def drain(self) -> None:
        """Consumes the page-flip events waiting on the fd."""
        try:
            os.set_blocking(self.fd, False)
            os.read(self.fd, 4096)
        except (BlockingIOError, InterruptedError):
            return
        finally:
            os.set_blocking(self.fd, True)
        self._flip_pending = False

    def close(self) -> None:
        for buffer in getattr(self, "buffers", []):
            buffer.memory.close()
        try:
            os.close(self.fd)
        except OSError:
            pass
        self.touch_socket.close()

    # --- setup -------------------------------------------------------------------

    def _crtc_connector_mode(self) -> tuple:
        drm = self._drm
        res = drm.drmModeGetResources(self.fd)
        if not res:
            raise LeaseError("no_resources")
        try:
            r = res.contents
            if r.count_crtcs < 1 or r.count_connectors < 1:
                raise LeaseError("empty_lease")
            crtc = r.crtcs[0]
            connector_id = r.connectors[0]
        finally:
            drm.drmModeFreeResources(res)
        connector = drm.drmModeGetConnector(self.fd, connector_id)
        if not connector:
            raise LeaseError("no_connector")
        try:
            c = connector.contents
            if c.count_modes < 1:
                raise LeaseError("no_mode")
            modes = [c.modes[i] for i in range(c.count_modes)]
            mode = next((m for m in modes if m.type & DRM_MODE_TYPE_PREFERRED), modes[0])
            return crtc, c.connector_id, _ModeInfo.from_buffer_copy(mode)
        finally:
            drm.drmModeFreeConnector(connector)

    def _pick_plane(self) -> int:
        drm = self._drm
        res = drm.drmModeGetPlaneResources(self.fd)
        if not res or res.contents.count_planes < 1:
            raise LeaseError("no_plane")
        try:
            planes = [res.contents.planes[i] for i in range(res.contents.count_planes)]
        finally:
            drm.drmModeFreePlaneResources(res)
        for plane in planes:
            if self._property_values(plane, DRM_MODE_OBJECT_PLANE).get("type") == DRM_PLANE_TYPE_PRIMARY:
                return plane
        return planes[0]

    def _property_values(self, obj: int, kind: int) -> dict:
        drm = self._drm
        props = drm.drmModeObjectGetProperties(self.fd, obj, kind)
        if not props:
            raise LeaseError("no_properties")
        found = {}
        try:
            p = props.contents
            for i in range(p.count_props):
                prop = drm.drmModeGetProperty(self.fd, p.props[i])
                if prop:
                    found[prop.contents.name.decode()] = p.prop_values[i]
                    drm.drmModeFreeProperty(prop)
        finally:
            drm.drmModeFreeObjectProperties(props)
        return found

    def _property_ids(self, obj: int, kind: int) -> dict:
        drm = self._drm
        props = drm.drmModeObjectGetProperties(self.fd, obj, kind)
        if not props:
            raise LeaseError("no_properties")
        ids = {}
        try:
            p = props.contents
            for i in range(p.count_props):
                prop = drm.drmModeGetProperty(self.fd, p.props[i])
                if prop:
                    ids[prop.contents.name.decode()] = p.props[i]
                    drm.drmModeFreeProperty(prop)
        finally:
            drm.drmModeFreeObjectProperties(props)
        return ids

    def _dumb_buffer(self) -> Buffer:
        create = _CreateDumb(height=self.height, width=self.width, bpp=32)
        fcntl.ioctl(self.fd, DRM_IOCTL_MODE_CREATE_DUMB, create, True)
        mapping = _MapDumb(handle=create.handle)
        fcntl.ioctl(self.fd, DRM_IOCTL_MODE_MAP_DUMB, mapping, True)
        memory = mmap.mmap(self.fd, create.size, mmap.MAP_SHARED, mmap.PROT_READ | mmap.PROT_WRITE,
                           offset=mapping.offset)
        fb = C.c_uint32()
        if self._drm.drmModeAddFB(self.fd, self.width, self.height, 24, 32, create.pitch, create.handle,
                                  C.byref(fb)) != 0:
            raise LeaseError("add_fb_failed")
        return Buffer(fb.value, create.handle, create.pitch, create.size, memory)

    def _commit(self, fb_id: int, modeset: bool, source_width: int | None = None,
                source_height: int | None = None, rotation: int = 1,
                in_fence_fd: int = -1) -> None:
        drm = self._drm
        request = drm.drmModeAtomicAlloc()
        if not request:
            raise LeaseError("atomic_alloc_failed")

        def put(obj: int, name: str, value: int) -> None:
            prop = self._props[obj].get(name)
            if prop is None:
                raise LeaseError(f"missing_property:{name}")
            if drm.drmModeAtomicAddProperty(request, obj, prop, value) < 0:
                raise LeaseError(f"add_property_failed:{name}")

        try:
            if modeset:
                blob = C.c_uint32()
                if drm.drmModeCreatePropertyBlob(self.fd, C.byref(self._mode), C.sizeof(self._mode), C.byref(blob)) != 0:
                    raise LeaseError("mode_blob_failed")
                put(self._crtc, "MODE_ID", blob.value)
                put(self._crtc, "ACTIVE", 1)
                put(self._connector, "CRTC_ID", self._crtc)
            put(self._plane, "FB_ID", fb_id)
            put(self._plane, "CRTC_ID", self._crtc)
            source_width = source_width or self.width
            source_height = source_height or self.height
            for name, value in (("SRC_X", 0), ("SRC_Y", 0), ("SRC_W", source_width << 16), ("SRC_H", source_height << 16),
                                ("CRTC_X", 0), ("CRTC_Y", 0), ("CRTC_W", self.width), ("CRTC_H", self.height)):
                put(self._plane, name, value)
            if "rotation" in self._props[self._plane]:
                put(self._plane, "rotation", rotation)
            if in_fence_fd >= 0:
                if "IN_FENCE_FD" not in self._props[self._plane]:
                    raise LeaseError("missing_property:IN_FENCE_FD")
                put(self._plane, "IN_FENCE_FD", C.c_uint64(in_fence_fd).value)
            flags = DRM_MODE_ATOMIC_ALLOW_MODESET if modeset else DRM_MODE_ATOMIC_NONBLOCK | DRM_MODE_PAGE_FLIP_EVENT
            # libdrm returns -errno here rather than -1.
            result = drm.drmModeAtomicCommit(self.fd, request, flags, None)
            if result != 0:
                code = -result if result < -1 else C.get_errno()
                raise LeaseError(f"commit_failed:{errno.errorcode.get(code, code)}")
        finally:
            drm.drmModeAtomicFree(request)
