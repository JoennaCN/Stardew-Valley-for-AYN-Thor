"""Present Stardew Valley's second render stream on ArmadaOS's leased panel.

The game remains the only owner of world and UI state. This process reads
triple-buffered BGRA frames into stable local snapshots, submits them to DRM,
and returns panel-local touch.
"""

from __future__ import annotations

import mmap
import os
import select
import signal
import socket
import struct
import sys
import time

from lease import LeaseBusy, LeaseError, LeasedPanel
from touch import TouchInput

MAGIC = 0x31524F4854564453
HEADER_SIZE = 64
HEADER = struct.Struct("<qiiiiiiqq")
TOUCH = struct.Struct("<Bff")
DOWN, MOVE, UP = 1, 2, 3


def orientation_matrix(orientation: str, panel_w: int, panel_h: int) -> tuple:
    if orientation == "right":
        return (0.0, 1.0, -1.0, 0.0, float(panel_w), 0.0)
    if orientation == "left":
        return (0.0, -1.0, 1.0, 0.0, 0.0, float(panel_h))
    if orientation == "upside_down":
        return (-1.0, 0.0, 0.0, -1.0, float(panel_w), float(panel_h))
    return (1.0, 0.0, 0.0, 1.0, 0.0, 0.0)


def canvas_size(orientation: str, panel_w: int, panel_h: int) -> tuple:
    return (panel_h, panel_w) if orientation in ("left", "right") else (panel_w, panel_h)


def panel_to_canvas(orientation: str, nx: float, ny: float, panel_w: int, panel_h: int) -> tuple:
    px, py = nx * panel_w, ny * panel_h
    if orientation == "right":
        return py, panel_w - px
    if orientation == "left":
        return panel_h - py, px
    if orientation == "upside_down":
        return panel_w - px, panel_h - py
    return px, py


class FrameStream:
    def __init__(self, path: str):
        self.path = path
        self.file = None
        self.mapping = None
        self.surface = None
        self.snapshot = None
        self.snapshot_sequence = -1
        self.width = self.height = self.stride = self.frame_bytes = 0
        self.buffer_count = 0

    def open(self, cairo) -> bool:
        if self.mapping is not None:
            return True
        try:
            self.file = open(self.path, "r+b", buffering=0)
            size = os.fstat(self.file.fileno()).st_size
            if size < HEADER_SIZE:
                self.close()
                return False
            # pycairo requires a writable buffer object even though this
            # process only reads the game's published frame slots.
            self.mapping = mmap.mmap(self.file.fileno(), size, access=mmap.ACCESS_WRITE)
            magic, version, width, height, stride, frame_bytes, _active, _sequence, _ticks = \
                HEADER.unpack_from(self.mapping, 0)
            if magic != MAGIC or version != 1 or width < 1 or height < 1 or stride < width * 4 \
                    or frame_bytes < stride * height or size < HEADER_SIZE + frame_bytes * 2:
                self.close()
                return False
            self.width, self.height, self.stride, self.frame_bytes = width, height, stride, frame_bytes
            self.buffer_count = (size - HEADER_SIZE) // frame_bytes
            # Never give Cairo a direct view of the producer's shared slots.
            # Even with three slots the producer can lap the display thread and
            # overwrite a slot while Cairo is scaling it, producing a vertical
            # seam made from two different frames.  Keep one private snapshot
            # and accept it only when the commit sequence remains unchanged for
            # the complete copy.
            self.snapshot = bytearray(frame_bytes)
            self.surface = cairo.ImageSurface.create_for_data(
                self.snapshot, cairo.FORMAT_ARGB32, width, height, stride)
            return True
        except (OSError, ValueError):
            self.close()
            return False

    def current(self):
        if self.mapping is None:
            return None
        sequence = struct.unpack_from("<q", self.mapping, 32)[0]
        if sequence == self.snapshot_sequence:
            return sequence, self.surface
        for _attempt in range(3):
            sequence_before = struct.unpack_from("<q", self.mapping, 32)[0]
            active_before = struct.unpack_from("<i", self.mapping, 28)[0]
            if not 0 <= active_before < self.buffer_count:
                return None
            start = HEADER_SIZE + active_before * self.frame_bytes
            self.snapshot[:] = self.mapping[start:start + self.frame_bytes]
            sequence_after = struct.unpack_from("<q", self.mapping, 32)[0]
            active_after = struct.unpack_from("<i", self.mapping, 28)[0]
            if sequence_before == sequence_after and active_before == active_after:
                self.snapshot_sequence = sequence_after
                self.surface.mark_dirty()
                return sequence_after, self.surface
        return None

    def close(self) -> None:
        if self.surface is not None:
            self.surface.finish()
        self.surface = None
        self.snapshot = None
        self.snapshot_sequence = -1
        if self.mapping is not None:
            try:
                self.mapping.close()
            except BufferError:
                pass
        if self.file is not None:
            self.file.close()
        self.mapping = self.file = None


class App:
    def __init__(self, panel, frame_path: str, ui_path: str, touch_path: str,
                 orientation: str, touchscreen: str | None):
        import cairo

        self.cairo = cairo
        self.panel = panel
        self.frames = FrameStream(frame_path)
        self.ui_frames = FrameStream(ui_path)
        self.touch_path = touch_path
        self.orientation = orientation
        self.outputs = [cairo.ImageSurface.create_for_data(
            b.memory, cairo.FORMAT_RGB24, panel.width, panel.height, b.pitch) for b in panel.buffers]
        self.matrix = cairo.Matrix(*orientation_matrix(orientation, panel.width, panel.height))
        self.touch = TouchInput(panel.touch_socket, touchscreen)
        self.touch_sender = socket.socket(socket.AF_UNIX, socket.SOCK_DGRAM)
        panel.touch_socket.setblocking(False)
        panel.request_touch()
        self.last_sequence = -1
        self.last_ui_sequence = -1
        self.last_point = (0.0, 0.0)
        self.stopping = False

    def run(self) -> None:
        while not self.stopping:
            watched = [self.panel.fd, *self.touch.fds]
            try:
                readable, _, _ = select.select(watched, [], [], 1 / 120)
            except InterruptedError:
                continue
            if self.panel.fd in readable:
                self.panel.drain()
            for fd in self.touch.fds:
                if fd in readable:
                    for event in self.touch.read(fd):
                        self._send_touch(*event)
            if not self.frames.open(self.cairo):
                time.sleep(0.05)
                continue
            current = self.frames.current()
            self.ui_frames.open(self.cairo)
            ui_current = self.ui_frames.current()
            frame_changed = current is not None and current[0] != self.last_sequence
            ui_changed = ui_current is not None and ui_current[0] != self.last_ui_sequence
            if current is not None and (frame_changed or ui_changed):
                if self._paint(current[1], None if ui_current is None else ui_current[1]):
                    self.last_sequence = current[0]
                    if ui_current is not None:
                        self.last_ui_sequence = ui_current[0]

    def _paint(self, source, ui_source) -> bool:
        # Never draw into a buffer until its previous page flip has landed.
        # A timed-out flip is dropped and retried on the next loop instead of
        # reusing a buffer that may still be scanned out (visible tearing).
        if not self.panel.wait_flip(0.05):
            return False
        output = self.outputs[self.panel.back_index]
        source.mark_dirty()
        ctx = self.cairo.Context(output)
        ctx.set_operator(self.cairo.OPERATOR_SOURCE)
        ctx.set_source_rgb(0, 0, 0)
        ctx.paint()
        ctx.set_matrix(self.matrix)
        canvas_w, canvas_h = canvas_size(self.orientation, self.panel.width, self.panel.height)
        ctx.scale(canvas_w / self.frames.width, canvas_h / self.frames.height)
        ctx.set_source_surface(source, 0, 0)
        ctx.get_source().set_filter(self.cairo.FILTER_NEAREST)
        ctx.paint()
        if ui_source is not None:
            ui_source.mark_dirty()
            ctx.set_operator(self.cairo.OPERATOR_OVER)
            ctx.set_matrix(self.matrix)
            ctx.scale(canvas_w / self.ui_frames.width, canvas_h / self.ui_frames.height)
            ctx.set_source_surface(ui_source, 0, 0)
            ctx.get_source().set_filter(self.cairo.FILTER_NEAREST)
            ctx.paint()
        output.flush()
        return self.panel.present()

    def _send_touch(self, kind: str, nx, ny) -> None:
        code = {"down": DOWN, "move": MOVE, "up": UP}.get(kind)
        if code is None:
            return
        if kind != "up":
            self.last_point = panel_to_canvas(
                self.orientation, float(nx), float(ny), self.panel.width, self.panel.height)
        x, y = self.last_point
        try:
            self.touch_sender.sendto(TOUCH.pack(code, x, y), self.touch_path)
        except OSError:
            pass

    def close(self) -> None:
        self.touch.close()
        self.touch_sender.close()
        self.frames.close()
        self.ui_frames.close()
        # Cairo keeps an exported view of each dumb-buffer mmap for as long as
        # its ImageSurface exists. Finish and release those surfaces before the
        # DRM buffer mappings are closed.
        for surface in self.outputs:
            surface.finish()
        self.outputs.clear()
        del surface
        self.panel.close()


def main() -> int:
    try:
        import cairo  # noqa: F401
    except ImportError:
        print("stardew-thor: python3-cairo is unavailable", file=sys.stderr)
        return 3
    try:
        panel = LeasedPanel(os.environ.get("SDV_THOR_LEASE_SOCKET", "/tmp/gamescope-lease.sock"))
    except LeaseBusy:
        print("stardew-thor: lower display lease is busy", file=sys.stderr)
        return 4
    except (LeaseError, OSError) as error:
        print(f"stardew-thor: unable to lease lower display: {error}", file=sys.stderr)
        return 3

    app = App(
        panel,
        os.environ.get("SDV_THOR_FRAME_PATH", "/dev/shm/stardew-thor-frame"),
        os.environ.get("SDV_THOR_UI_PATH", "/dev/shm/stardew-thor-ui"),
        os.environ.get("SDV_THOR_TOUCH_PATH", "/dev/shm/stardew-thor-touch"),
        os.environ.get("ARMADA_PANEL_ORIENTATION", "right"),
        os.environ.get("ARMADA_SECONDARY_TOUCHSCREEN"),
    )
    ready_path = os.environ.get("SDV_THOR_READY_PATH")
    if ready_path:
        with open(ready_path, "w") as ready:
            ready.write(os.environ.get("SDV_THOR_READY_VALUE", "ready") + "\n")

    def stop(*_args):
        app.stopping = True

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    try:
        app.run()
    except ConnectionError:
        pass
    finally:
        app.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
