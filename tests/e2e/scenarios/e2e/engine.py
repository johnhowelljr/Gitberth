"""The rig's Docker engine (the one Gitberth drives), through the docker CLI and the socket mounted into the runner:
containers, images, volumes, a container's environment and labels, exec, logs."""
import json
import subprocess

from . import config
from .wait import until

# The rig's own network in its engine, and the stand-ins' container on it (inner.sh makes both).
RIG_NETWORK = "gbe2e"
STANDIN = "gbe2e-standin"


class DockerError(AssertionError):
    pass


def docker(*args, check=True, timeout=120, input=None):
    r = subprocess.run(["docker", *args], capture_output=True, text=True, timeout=timeout, input=input)
    if check and r.returncode != 0:
        raise DockerError(f"docker {' '.join(args)} failed ({r.returncode}): {r.stderr.strip() or r.stdout.strip()}")
    return r.stdout


class Engine:
    def inspect(self, name):
        """docker container inspect, or None when there's no such container."""
        out = docker("container", "inspect", name, check=False)
        data = json.loads(out) if out.strip().startswith("[") else []
        return data[0] if data else None

    def exists(self, name):
        return self.inspect(name) is not None

    def running(self, name):
        c = self.inspect(name)
        return bool(c and c["State"]["Running"])

    def wait_running(self, name, timeout=120):
        """Waits for the container to exist and run; returns its inspect."""
        until(lambda: self.running(name), timeout, f"the container {name} to run",
              seen=lambda: (self.inspect(name) or {}).get("State", "no such container"))
        return self.inspect(name)

    def wait_gone(self, name, timeout=120):
        """Waits for the container to be removed."""
        until(lambda: not self.exists(name), timeout, f"the container {name} to be removed",
              seen=lambda: (self.inspect(name) or {}).get("State"))

    def containers(self, name=None, label=None, all=True):
        """docker ps as dicts (Names, Image, State, Status, Labels, Ports, ...); name is a substring filter."""
        args = ["ps", "--format", "{{json .}}", "--no-trunc"]
        if all:
            args.append("-a")
        if name:
            args += ["--filter", f"name={name}"]
        if label:
            args += ["--filter", f"label={label}"]
        return [json.loads(line) for line in docker(*args).splitlines() if line.strip()]

    def container_names(self, prefix=None):
        names = [c["Names"] for c in self.containers()]
        return sorted(n for n in names if prefix is None or n.startswith(prefix))

    def env(self, name):
        """The container's environment as Docker has it (Config.Env), as a dict."""
        c = self.inspect(name)
        assert c is not None, f"no container {name}"
        return dict(e.split("=", 1) if "=" in e else (e, "") for e in c["Config"]["Env"] or [])

    def labels(self, name):
        c = self.inspect(name)
        assert c is not None, f"no container {name}"
        return c["Config"]["Labels"] or {}

    def image_of(self, name):
        """The image reference the container was created from (Config.Image)."""
        c = self.inspect(name)
        return c["Config"]["Image"] if c else None

    def mounts(self, name):
        c = self.inspect(name)
        return c["Mounts"] if c else []

    def host_ports(self, name):
        """{"8080/tcp": [host ports]} from the container's live port bindings."""
        c = self.inspect(name)
        out = {}
        for port, bindings in ((c or {}).get("NetworkSettings", {}).get("Ports") or {}).items():
            out[port] = sorted({int(b["HostPort"]) for b in bindings or [] if b.get("HostPort")})
        return out

    def networks(self, name):
        c = self.inspect(name)
        return sorted(((c or {}).get("NetworkSettings", {}).get("Networks") or {}).keys())

    def address(self, name, network):
        """The container's IPv4 address on a network."""
        c = self.inspect(name)
        ip = (((c or {}).get("NetworkSettings") or {}).get("Networks") or {}).get(network, {}).get("IPAddress")
        assert ip, f"{name} has no address on the network {network} (it's on {self.networks(name)})"
        return ip

    def exec(self, name, *cmd, check=True, timeout=60, env=None):
        """Runs a command in the container (with env's variables added); returns its output."""
        flags = [x for k, v in (env or {}).items() for x in ("-e", f"{k}={v}")]
        return docker("exec", *flags, name, *cmd, check=check, timeout=timeout)

    def logs(self, name, since=None, tail=None, timestamps=False, streams=False):
        """The container's output: stdout, then stderr; with streams, the two apart as (stdout, stderr)."""
        args = ["logs"]
        if since is not None:
            args += ["--since", str(int(since)) if isinstance(since, (int, float)) else since]
        if tail is not None:
            args += ["--tail", str(tail)]
        if timestamps:
            args.append("--timestamps")
        r = subprocess.run(["docker", *args, name], capture_output=True, text=True, timeout=60)
        return (r.stdout, r.stderr) if streams else r.stdout + r.stderr

    def images(self, repository=None):
        """Image references (repo:tag), optionally only one repository's: images("gitberth/my-app")."""
        out = docker("image", "ls", "--format", "{{.Repository}}:{{.Tag}}")
        refs = [line.strip() for line in out.splitlines() if line.strip() and not line.startswith("<none>")]
        return sorted(r for r in refs if repository is None or r.split(":", 1)[0] == repository)

    def image_exists(self, ref):
        return docker("image", "inspect", ref, check=False).strip().startswith("[{")

    def image_labels(self, ref):
        out = docker("image", "inspect", ref)
        return (json.loads(out)[0]["Config"].get("Labels")) or {}

    def volumes(self, prefix=None):
        names = [v.strip() for v in docker("volume", "ls", "-q").splitlines() if v.strip()]
        return sorted(v for v in names if prefix is None or v.startswith(prefix))

    def volume_exists(self, name):
        return name in self.volumes()

    def anonymous_volumes(self):
        """Volumes with a 64-hex name: ones Docker made for a VOLUME nobody named."""
        return [v for v in self.volumes() if len(v) == 64 and all(ch in "0123456789abcdef" for ch in v)]

    def network_exists(self, name):
        return docker("network", "inspect", name, check=False).strip().startswith("[{")

    def recreate_fake_cloudflared(self, token, network="host"):
        """The fake cloudflared again, on the host's network (default, like the live install), on Gitberth's shared network
        ("gitberth"), or on neither ("bridge"): TunnelLocator then sees the matching network mode, and the tunnel's
        traffic reaches services from that network alone (localhost is its own container off the host's network), as a
        real cloudflared's would. It reaches the stand-in (Cloudflare, for a real one) where it is: from the host's
        network the rig's network is routed; off it, the stand-in joins the fake's network, so the fake itself never
        joins the rig's, where it could reach Gitberth by name even when Gitberth isn't on the fake's network. Wait for
        it with cloudflare.wait_runs(). The rig puts it back on the host's network at the next run."""
        docker("rm", "-f", "cloudflare-tunnel", check=False)
        joined = self.networks(STANDIN)
        # The stand-in leaves any network an earlier call joined it to: it's only ever on the rig's and the fake's.
        for other in joined:
            if other not in (RIG_NETWORK, network):
                docker("network", "disconnect", "-f", other, STANDIN, check=False)
        if network != "host" and network not in joined:
            docker("network", "connect", network, STANDIN)
        standin = self.address(STANDIN, RIG_NETWORK if network == "host" else network)
        docker("run", "-d", "--name", "cloudflare-tunnel", "--network", network, "--restart", "unless-stopped",
               "-e", f"STANDIN=http://{standin}:8099", "-e", f"E2E_NETWORK={network}", "--label", "gbe2e.part=cloudflared",
               "gbe2e-cloudflared:fake", "tunnel", "run", "--token", token)


def gitberth_logs(since=None, tail=None):
    """Gitberth's own log (its container's output)."""
    return Engine().logs(config.GITBERTH_CONTAINER, since=since, tail=tail)
