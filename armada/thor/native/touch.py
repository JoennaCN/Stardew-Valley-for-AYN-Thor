"""Touches on the bottom panel, as (kind, x, y) with x and y from 0 to 1 in the panel's own orientation.

Two sources, because Armada builds differ in which works:
- the lease socket: gamescope forwards the panel's touches to the lease holder that asked;
- the touchscreen's evdev node, when the user can read it.
Whichever reports a touch first is used from then on, so a touch never arrives twice.
"""

from __future__ import annotations

import fcntl
import os
import struct

# gamescope's DrmLeaseEvent: type, touch id, time (ms), x, y — with x and y normalised.
LEASE_EVENT = struct.Struct("=IiIff")
LEASE_DOWN, LEASE_MOTION, LEASE_UP = 1, 2, 3

# struct input_event on 64-bit Linux: timeval, type, code, value.
INPUT_EVENT = struct.Struct("llHHi")
EV_SYN, EV_KEY, EV_ABS = 0x00, 0x01, 0x03
SYN_REPORT = 0
BTN_TOUCH = 0x14A
ABS_X, ABS_Y = 0x00, 0x01
ABS_MT_SLOT, ABS_MT_POSITION_X, ABS_MT_POSITION_Y, ABS_MT_TRACKING_ID = 0x2F, 0x35, 0x36, 0x39
# EVIOCGABS(axis) = _IOR('E', 0x40 + axis, struct input_absinfo), which is six 32-bit ints.
EVIOCGABS = (2 << 30) | (24 << 16) | (ord("E") << 8) | 0x40


class LeaseTouchParser:
    """Follows the first finger down and ignores the rest, as a tap-and-drag UI needs."""

    def __init__(self):
        self._partial = b""
        self._finger = None

    def feed(self, data: bytes) -> list:
        data = self._partial + data
        whole = len(data) - len(data) % LEASE_EVENT.size
        self._partial = data[whole:]
        events = []
        for offset in range(0, whole, LEASE_EVENT.size):
            kind, finger, _time, x, y = LEASE_EVENT.unpack_from(data, offset)
            if kind == LEASE_DOWN and self._finger is None:
                self._finger = finger
                events.append(("down", x, y))
            elif kind == LEASE_MOTION and finger == self._finger:
                events.append(("move", x, y))
            elif kind == LEASE_UP and finger == self._finger:
                self._finger = None
                events.append(("up", None, None))
            # Other types (suspend/resume) are for holders that opted into yielding; this one didn't.
        return events


class EvdevTouchParser:
    """Single-touch view of a multitouch digitizer: slot 0 only."""

    def __init__(self, x_max: int, y_max: int, x: int = 0, y: int = 0):
        self.x_max, self.y_max = max(1, x_max), max(1, y_max)
        # The kernel drops a position equal to the last one, so a tap where the previous one
        # lifted comes without coordinates: start from where the device says the finger was.
        self._x, self._y = x, y
        self._slot = 0
        self._down = self._was_down = self._moved = False
        self._partial = b""

    def feed(self, data: bytes) -> list:
        data = self._partial + data
        whole = len(data) - len(data) % INPUT_EVENT.size
        self._partial = data[whole:]
        events = []
        for offset in range(0, whole, INPUT_EVENT.size):
            _, _, kind, code, value = INPUT_EVENT.unpack_from(data, offset)
            self._event(kind, code, value, events)
        return events

    def _event(self, kind: int, code: int, value: int, events: list) -> None:
        if kind == EV_ABS:
            if code == ABS_MT_SLOT:
                self._slot = value
            elif self._slot != 0:
                return
            elif code in (ABS_MT_POSITION_X, ABS_X):
                self._x, self._moved = value, True
            elif code in (ABS_MT_POSITION_Y, ABS_Y):
                self._y, self._moved = value, True
            elif code == ABS_MT_TRACKING_ID:
                self._down = value >= 0
        elif kind == EV_KEY and code == BTN_TOUCH:
            self._down = value != 0
        elif kind == EV_SYN and code == SYN_REPORT:
            x, y = self._x / self.x_max, self._y / self.y_max
            if self._down and not self._was_down:
                events.append(("down", x, y))
            elif self._down and self._moved:
                events.append(("move", x, y))
            elif not self._down and self._was_down:
                events.append(("up", None, None))
            self._was_down = self._down
            self._moved = False


def find_evdev(name: str, root: str = "/sys/class/input") -> str | None:
    try:
        entries = sorted(os.listdir(root))
    except OSError:
        return None
    for entry in entries:
        if not entry.startswith("event"):
            continue
        try:
            with open(os.path.join(root, entry, "device", "name")) as handle:
                if handle.read().strip() == name:
                    return f"/dev/input/{entry}"
        except OSError:
            continue
    return None


def _axis(fd: int, *codes: int) -> tuple:
    for code in codes:
        info = bytearray(24)
        try:
            fcntl.ioctl(fd, EVIOCGABS + code, info, True)
        except OSError:
            continue
        value, _minimum, maximum = struct.unpack_from("iii", info)
        if maximum > 0:
            return value, maximum
    return 0, 0


class TouchInput:
    """Both sources, merged. `fds` are to be watched; `read(fd)` returns events from that fd."""

    def __init__(self, lease_socket, touchscreen_name: str | None):
        self._lease_socket = lease_socket
        self._lease_parser = LeaseTouchParser()
        self._evdev_fd = None
        self._evdev_parser = None
        self._source = None
        path = find_evdev(touchscreen_name) if touchscreen_name else None
        if path:
            try:
                fd = os.open(path, os.O_RDONLY | os.O_NONBLOCK | os.O_CLOEXEC)
            except OSError:
                fd = None
            if fd is not None:
                # The multitouch axes read 0 between touches; ABS_X/ABS_Y keep the last position.
                x, x_max = _axis(fd, ABS_X, ABS_MT_POSITION_X)
                y, y_max = _axis(fd, ABS_Y, ABS_MT_POSITION_Y)
                self._evdev_fd = fd
                self._evdev_parser = EvdevTouchParser(x_max, y_max, x, y)

    @property
    def fds(self) -> list:
        fds = []
        if self._lease_socket is not None:
            fds.append(self._lease_socket.fileno())
        if self._evdev_fd is not None:
            fds.append(self._evdev_fd)
        return fds

    def read(self, fd: int) -> list:
        if self._evdev_fd is not None and fd == self._evdev_fd:
            source, parser = "evdev", self._evdev_parser
            try:
                data = os.read(fd, INPUT_EVENT.size * 64)
            except BlockingIOError:
                return []
        else:
            source, parser = "lease", self._lease_parser
            try:
                data = self._lease_socket.recv(LEASE_EVENT.size * 32)
            except BlockingIOError:
                return []
            if not data:
                raise ConnectionError("lease_socket_closed")
        events = parser.feed(data)
        if self._source is None and any(kind == "down" for kind, _, _ in events):
            self._source = source
        return events if self._source in (None, source) else []

    def close(self) -> None:
        if self._evdev_fd is not None:
            os.close(self._evdev_fd)
            self._evdev_fd = None
