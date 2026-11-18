"""How the shots' browser reaches Gitberth: as a program on the rig's engine host would, so Gitberth sees the requests
come from the host's LAN address (192.168.50.10) and its audit log names that address, not the rig's inside network.

The runner's container sits on the rig's own network, whose gateway is the address Gitberth would see. So the browser
uses a SOCKS5 proxy in this process, which lets it reach Gitberth alone (http://192.168.50.10:8080) and hands each
connection to a relay container on the engine's own network (docker run --network host) over a unix socket in the
folder both share. The relay opens the TCP connection from the host's address. A unix socket listens on no port, so
Gitberth's Network page shows nothing new; and the relay carries Gitberth's helper label, so no page lists it."""
import os
import socket
import threading

from e2e.engine import docker

NAME = "gbdocs-relay"
# The runner's /out is the engine's /e2e/out (inner.sh mounts it): the folder the relay and this process share.
SHARED, ENGINE_SHARED = "/out/relay", "/e2e/out/relay"
SOCKET = "gitberth.sock"

RELAY = r"""
import os, socket, sys, threading
path, host, port = sys.argv[1], sys.argv[2], int(sys.argv[3])
if os.path.exists(path):
    os.unlink(path)
srv = socket.socket(socket.AF_UNIX)
srv.bind(path)
os.chmod(path, 0o666)
srv.listen(128)

def pipe(a, b):
    try:
        while True:
            data = a.recv(65536)
            if not data:
                break
            b.sendall(data)
    except OSError:
        pass
    for s in (a, b):
        try:
            s.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass

def serve(c):
    try:
        u = socket.create_connection((host, port), timeout=10)
        u.settimeout(None)
    except OSError:
        c.close()
        return
    threading.Thread(target=pipe, args=(u, c), daemon=True).start()
    pipe(c, u)

while True:
    c, _ = srv.accept()
    threading.Thread(target=serve, args=(c,), daemon=True).start()
"""


def _exact(sock, n):
    data = b""
    while len(data) < n:
        chunk = sock.recv(n - len(data))
        if not chunk:
            raise ConnectionError("closed")
        data += chunk
    return data


def _pipe(a, b):
    try:
        while True:
            data = a.recv(65536)
            if not data:
                break
            b.sendall(data)
    except OSError:
        pass
    for s in (a, b):
        try:
            s.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass


class Relay:
    """The relay container and this process's SOCKS5 proxy (127.0.0.1:<port>), for Gitberth at host:port alone."""

    def __init__(self, image, host, port=8080, socks_port=18381):
        self.host, self.port, self.socks_port = host, port, socks_port
        self.path = os.path.join(SHARED, SOCKET)
        os.makedirs(SHARED, exist_ok=True)
        docker("rm", "-f", NAME, check=False)
        docker("run", "-d", "--rm", "--name", NAME, "--network", "host", "--label", "gitberth.helper=rig",
               "-v", f"{ENGINE_SHARED}:/relay", "--entrypoint", "python3", image, "-c", RELAY,
               f"/relay/{SOCKET}", host, str(port))
        for _ in range(100):
            if os.path.exists(self.path):
                break
            threading.Event().wait(0.1)
        else:
            raise RuntimeError(f"the relay didn't start: {docker('logs', NAME, check=False)}")
        self.server = socket.socket()
        self.server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.server.bind(("127.0.0.1", socks_port))
        self.server.listen(128)
        threading.Thread(target=self._accept, daemon=True).start()

    @property
    def proxy(self):
        return {"server": f"socks5://127.0.0.1:{self.socks_port}"}

    def _accept(self):
        while True:
            try:
                c, _ = self.server.accept()
            except OSError:
                return
            threading.Thread(target=self._socks, args=(c,), daemon=True).start()

    def _socks(self, c):
        """SOCKS5 without authentication, CONNECT to Gitberth only; anything else is refused (not allowed by ruleset)."""
        try:
            _, n = _exact(c, 2)
            _exact(c, n)
            c.sendall(b"\x05\x00")
            _, cmd, _, kind = _exact(c, 4)
            if kind == 1:
                host = socket.inet_ntoa(_exact(c, 4))
            elif kind == 3:
                host = _exact(c, _exact(c, 1)[0]).decode()
            else:
                _exact(c, 16)
                host = None
            port = int.from_bytes(_exact(c, 2), "big")
            if cmd != 1 or (host, port) != (self.host, self.port):
                c.sendall(b"\x05\x02\x00\x01" + b"\x00" * 6)
                c.close()
                return
            u = socket.socket(socket.AF_UNIX)
            u.connect(self.path)
            c.sendall(b"\x05\x00\x00\x01" + b"\x00" * 6)
        except (OSError, ConnectionError):
            c.close()
            return
        threading.Thread(target=_pipe, args=(u, c), daemon=True).start()
        _pipe(c, u)

    def close(self):
        try:
            self.server.close()
        finally:
            docker("rm", "-f", NAME, check=False)
            try:
                os.unlink(self.path)
            except OSError:
                pass
