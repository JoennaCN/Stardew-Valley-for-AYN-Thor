"""Host-side bridge between the FEX game container and Armada's lower panel.

The SMAPI mod only writes request/heartbeat files in its own directory. This
daemon runs under ArmadaOS's native user systemd, where it can stop Plasma,
acquire the DRM lease, and run the ARM64 presenter without crossing FEX.
"""

from __future__ import annotations

import os
import shlex
import signal
import subprocess
import sys
import time

ARMADA_UNIT = "armada-bottom-screen.service"
ARMADA_GAMESCOPE_UNIT = "armada-bottom-gamescope.service"
STALE_SECONDS = 30


def command(*args: str) -> subprocess.CompletedProcess:
    return subprocess.run(args, text=True, capture_output=True, check=False, timeout=10)


def device_environment() -> dict:
    result = command("/usr/libexec/armada/device-env")
    values = {}
    for line in result.stdout.splitlines():
        name, separator, raw = line.partition("=")
        if not separator or not name.isidentifier():
            continue
        try:
            parts = shlex.split(raw)
        except ValueError:
            continue
        values[name] = parts[0] if parts else ""
    return values


def read_token(path: str) -> str | None:
    try:
        with open(path) as handle:
            value = handle.read().strip()
        return value or None
    except OSError:
        return None


def remove(path: str) -> None:
    try:
        os.unlink(path)
    except FileNotFoundError:
        pass


def heartbeat_fresh(path: str, token: str) -> bool:
    try:
        return read_token(path) == token and time.time() - os.path.getmtime(path) < STALE_SECONDS
    except OSError:
        return False


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        print("usage: bridge.py <VerticalWorld-mod-directory>", file=sys.stderr)
        return 2

    mod_dir = os.path.abspath(argv[1])
    runtime_dir = os.path.join(mod_dir, "runtime")
    os.makedirs(runtime_dir, exist_ok=True)
    request_path = os.path.join(runtime_dir, "bridge.request")
    heartbeat_path = os.path.join(runtime_dir, "bridge.heartbeat")
    ready_path = os.path.join(runtime_dir, "bridge.ready")
    stop_path = os.path.join(runtime_dir, "bridge.stop")
    log_path = os.path.join(mod_dir, "vertical-bridge.log")
    app_path = os.path.join(mod_dir, "armada", "thor", "native", "dmabuf_presenter.py")

    stopping = False

    def stop(*_args):
        nonlocal stopping
        stopping = True

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)

    child = None
    restore_plasma = False
    restore_gamescope = False
    active_token = None
    failed_token = None

    def release(log) -> None:
        nonlocal child, restore_plasma, restore_gamescope, active_token
        remove(ready_path)
        if child is not None and child.poll() is None:
            child.terminate()
            try:
                child.wait(timeout=3)
            except subprocess.TimeoutExpired:
                child.kill()
        if restore_gamescope:
            result = command("/usr/bin/systemctl", "--user", "start", ARMADA_GAMESCOPE_UNIT)
            log.write(f"gamescope-restore status={result.returncode} error={result.stderr.strip()!r}\n")
        if restore_plasma:
            result = command("/usr/bin/systemctl", "--user", "start", "--no-block", ARMADA_UNIT)
            log.write(f"plasma-restore status={result.returncode} error={result.stderr.strip()!r}\n")
        child = None
        restore_plasma = False
        restore_gamescope = False
        active_token = None

    with open(log_path, "a", buffering=1) as log:
        log.write(f"\nbridge-start pid={os.getpid()} mod={mod_dir}\n")
        try:
            while not stopping:
                token = read_token(request_path)
                if token is None:
                    failed_token = None
                    remove(stop_path)
                    if child is not None:
                        log.write("request-removed\n")
                        release(log)
                    time.sleep(0.25)
                    continue

                if active_token is not None:
                    requested_stop = os.path.exists(stop_path)
                    stale = not heartbeat_fresh(heartbeat_path, active_token)
                    replaced = token != active_token
                    exited = child is None or child.poll() is not None
                    if requested_stop or stale or replaced or exited:
                        log.write(f"session-end token={active_token} stop={requested_stop} stale={stale} replaced={replaced} child={None if child is None else child.poll()}\n")
                        if exited and not requested_stop and not stale and not replaced:
                            failed_token = active_token
                        release(log)
                    time.sleep(0.25)
                    continue

                if token == failed_token or not heartbeat_fresh(heartbeat_path, token):
                    time.sleep(0.25)
                    continue

                remove(stop_path)
                remove(ready_path)
                state = command("/usr/bin/systemctl", "--user", "is-active", ARMADA_UNIT).stdout.strip()
                gamescope_state = command(
                    "/usr/bin/systemctl", "--user", "is-active", ARMADA_GAMESCOPE_UNIT
                ).stdout.strip()
                restore_plasma = state in ("active", "activating")
                restore_gamescope = gamescope_state in ("active", "activating")
                log.write(
                    f"session-start token={token} plasma={state!r} gamescope={gamescope_state!r}\n"
                )
                if restore_plasma:
                    result = command("/usr/bin/systemctl", "--user", "stop", ARMADA_UNIT)
                    log.write(f"plasma-stop status={result.returncode} error={result.stderr.strip()!r}\n")
                    if result.returncode != 0:
                        restore_plasma = False
                        restore_gamescope = False
                        failed_token = token
                        continue
                if restore_gamescope:
                    result = command("/usr/bin/systemctl", "--user", "stop", ARMADA_GAMESCOPE_UNIT)
                    log.write(f"gamescope-stop status={result.returncode} error={result.stderr.strip()!r}\n")
                    if result.returncode != 0:
                        failed_token = token
                        release(log)
                        continue

                environment = dict(os.environ)
                environment.update(device_environment())
                environment["SDV_THOR_READY_PATH"] = ready_path
                environment["SDV_THOR_READY_VALUE"] = token
                environment.setdefault("SDV_THOR_FRAME_PATH", "/dev/shm/stardew-thor-frame")
                environment.setdefault("SDV_THOR_UI_PATH", "/dev/shm/stardew-thor-ui")
                environment.setdefault("SDV_THOR_TOUCH_PATH", "/dev/shm/stardew-thor-touch")
                environment.setdefault("SDV_THOR_DMABUF_METADATA", "/dev/shm/stardew-thor-dmabuf.json")
                environment.setdefault("SDV_THOR_DMABUF_CONTROL", "/dev/shm/stardew-thor-dmabuf-control")
                environment.setdefault("SDV_THOR_DMABUF_SOCKET", "/dev/shm/stardew-thor-dmabuf.sock")
                environment.setdefault("SDV_THOR_DMABUF_FENCE_SOCKET", "/dev/shm/stardew-thor-dmabuf-fence.sock")
                remove(environment["SDV_THOR_FRAME_PATH"])
                remove(environment["SDV_THOR_UI_PATH"])
                remove(environment["SDV_THOR_DMABUF_METADATA"])
                remove(environment["SDV_THOR_DMABUF_CONTROL"])
                remove(environment["SDV_THOR_DMABUF_SOCKET"])
                remove(environment["SDV_THOR_DMABUF_FENCE_SOCKET"])
                child = subprocess.Popen(["/usr/bin/python3", app_path], env=environment,
                                         stdout=log, stderr=subprocess.STDOUT)
                active_token = token
                log.write(f"native-start pid={child.pid} token={token}\n")
        finally:
            release(log)
            remove(ready_path)
            log.write("bridge-stop\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
