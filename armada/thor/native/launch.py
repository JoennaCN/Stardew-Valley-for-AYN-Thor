"""Own the Armada lower-panel session for the lifetime of one Stardew process."""

from __future__ import annotations

import os
import shlex
import signal
import subprocess
import sys
import time

ARMADA_UNIT = "armada-bottom-screen.service"


def host_path(path: str) -> str:
    exposed = "/run/host" + path
    return exposed if os.path.exists(exposed) else path


def command(*args: str) -> subprocess.CompletedProcess:
    return subprocess.run(args, text=True, capture_output=True, check=False, timeout=10)


def device_environment() -> dict:
    result = command(host_path("/usr/libexec/armada/device-env"))
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


def process_alive(pid: int) -> bool:
    try:
        os.kill(pid, 0)
        return True
    except OSError:
        return False


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        print("usage: launch.py <stardew-pid>", file=sys.stderr)
        return 2
    game_pid = int(argv[1])
    stop_path = f"/dev/shm/stardew-thor-stop-{game_pid}"
    ready_path = os.environ.get("SDV_THOR_READY_PATH", f"/dev/shm/stardew-thor-ready-{game_pid}")
    log_path = os.environ.get("SDV_THOR_NATIVE_LOG", "/tmp/stardew-thor-native.log")
    for path in (stop_path, ready_path):
        try:
            os.unlink(path)
        except FileNotFoundError:
            pass
    stopping = False

    def stop(*_args):
        nonlocal stopping
        stopping = True

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)

    child = None
    restore = False
    systemctl = host_path("/usr/bin/systemctl")
    try:
        with open(log_path, "a", buffering=1) as native_log:
            native_log.write(f"\nlauncher-start game_pid={game_pid} python={sys.executable} systemctl={systemctl}\n")
            state_result = command(systemctl, "--user", "is-active", ARMADA_UNIT)
            state = state_result.stdout.strip()
            native_log.write(f"bottom-service state={state!r} status={state_result.returncode} error={state_result.stderr.strip()!r}\n")
            restore = state in ("active", "activating")
            if restore:
                stopped = command(systemctl, "--user", "stop", ARMADA_UNIT)
                native_log.write(f"bottom-service-stop status={stopped.returncode} error={stopped.stderr.strip()!r}\n")
                if stopped.returncode != 0:
                    return 3

            environment = dict(os.environ)
            environment.update(device_environment())
            host_lease = host_path("/tmp/gamescope-lease.sock")
            if os.path.exists(host_lease):
                environment["SDV_THOR_LEASE_SOCKET"] = host_lease
            frame_path = environment.get("SDV_THOR_FRAME_PATH", "/dev/shm/stardew-thor-frame")
            try:
                os.unlink(frame_path)
            except FileNotFoundError:
                pass
            app = os.path.join(os.path.dirname(os.path.abspath(__file__)), "app.py")
            native_python = host_path("/usr/bin/python3")
            native_log.write(f"native-start python={native_python} lease={environment.get('SDV_THOR_LEASE_SOCKET', '/tmp/gamescope-lease.sock')} frame={frame_path}\n")
            child = subprocess.Popen([native_python, app], env=environment,
                                     stdout=native_log, stderr=subprocess.STDOUT)
            while not stopping and process_alive(game_pid) and not os.path.exists(stop_path):
                if child.poll() is not None:
                    native_log.write(f"native-exit status={child.returncode}\n")
                    return child.returncode
                time.sleep(0.25)
            native_log.write("launcher-stop\n")
            return 0
    finally:
        if child is not None and child.poll() is None:
            child.terminate()
            try:
                child.wait(timeout=3)
            except subprocess.TimeoutExpired:
                child.kill()
        if restore:
            command(systemctl, "--user", "start", "--no-block", ARMADA_UNIT)
        for path in (stop_path, ready_path):
            try:
                os.unlink(path)
            except FileNotFoundError:
                pass


if __name__ == "__main__":
    sys.exit(main(sys.argv))
