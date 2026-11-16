"""The cert maker: the rig's own test CA and the stand-ins' certificate, made with openssl the first time the stand-in
starts (nothing here is committed). The CA's private key stays in the stand-in's state volume; only its certificate goes
to the trust volume, which Gitberth's CA bundle is made from.

EdgeCerts plays Cloudflare's edge certificates: a visitor's HTTPS request for any other host name (Gitberth's uptime
checks, through GITBERTH_UPTIME_CONNECT_TO) gets one from the test CA, chosen by the name it asks for (SNI)."""
import os
import secrets
import shutil
import ssl
import subprocess
import threading

# Every name the stand-ins answer for over HTTPS.
HOSTS = [
    "github.com", "www.github.com", "api.github.com", "codeload.github.com", "raw.githubusercontent.com",
    "objects.githubusercontent.com", "uploads.github.com", "api.cloudflare.com", "gbe2e-standin",
    # Where notifications go: Discord's and Slack's webhooks, an ntfy server, any webhook.
    "discord.com", "hooks.slack.com", "ntfy.e2e.test", "notify.e2e.test",
    # GitLab: a self-managed instance's address, and gitlab.com (Gitberth's default instance).
    "gitlab.e2e.test", "gitlab.com",
]


def _openssl(*args):
    r = subprocess.run(["openssl", *args], capture_output=True, text=True)
    if r.returncode != 0:
        raise RuntimeError(f"openssl {' '.join(args[:2])} failed: {r.stderr.strip()}")


def ensure(state_dir, trust_dir):
    """Makes what's missing and returns (certificate, key) for the HTTPS server."""
    d = os.path.join(state_dir, "certs")
    os.makedirs(d, exist_ok=True)
    os.makedirs(trust_dir, exist_ok=True)
    ca_key, ca_crt = os.path.join(d, "ca.key"), os.path.join(d, "ca.pem")
    if not (os.path.exists(ca_key) and os.path.exists(ca_crt)):
        _openssl("req", "-x509", "-newkey", "rsa:2048", "-nodes", "-keyout", ca_key, "-out", ca_crt, "-days", "3650",
                 "-subj", "/CN=Gitberth e2e rig test CA",
                 "-addext", "basicConstraints=critical,CA:TRUE",
                 "-addext", "keyUsage=critical,keyCertSign,cRLSign")
        for stale in ("server.pem", "server.key", "hosts"):
            p = os.path.join(d, stale)
            if os.path.exists(p):
                os.remove(p)
    srv_key, srv_crt, hosts_file = os.path.join(d, "server.key"), os.path.join(d, "server.pem"), os.path.join(d, "hosts")
    wanted = ",".join(HOSTS)
    have = open(hosts_file).read().strip() if os.path.exists(hosts_file) else ""
    if have != wanted or not (os.path.exists(srv_key) and os.path.exists(srv_crt)):
        csr, ext = os.path.join(d, "server.csr"), os.path.join(d, "server.ext")
        with open(ext, "w") as f:
            f.write("basicConstraints=CA:FALSE\n")
            f.write("keyUsage=critical,digitalSignature,keyEncipherment\n")
            f.write("extendedKeyUsage=serverAuth\n")
            f.write("subjectAltName=" + ",".join(f"DNS:{h}" for h in HOSTS) + "\n")
        _openssl("req", "-new", "-newkey", "rsa:2048", "-nodes", "-keyout", srv_key, "-out", csr, "-subj", "/CN=github.com")
        _openssl("x509", "-req", "-in", csr, "-CA", ca_crt, "-CAkey", ca_key, "-CAcreateserial", "-out", srv_crt,
                 "-days", "825", "-sha256", "-extfile", ext)
        with open(hosts_file, "w") as f:
            f.write(wanted)
    shutil.copyfile(ca_crt, os.path.join(trust_dir, "ca.pem"))
    os.chmod(ca_key, 0o600)
    os.chmod(srv_key, 0o600)
    return srv_crt, srv_key


class EdgeCerts:
    """Cloudflare's edge certificates, from the rig's test CA: a host name in one of the Cloudflare stand-in's zones gets
    the zone's (it covers the zone and *.zone, as Cloudflare's free certificate does, so a name further down fails a
    client's check unless the zone has Total TLS, which covers the exact name); any other name gets its own. Each is made
    with openssl the first time it's asked for, valid for 90 days unless a scenario set fewer for the host (days_for),
    and kept in memory."""

    DEFAULT_DAYS = 90

    def __init__(self, state_dir, zone_for):
        self.dir = os.path.join(state_dir, "certs", "edge")
        os.makedirs(self.dir, exist_ok=True)
        self.ca_crt = os.path.join(state_dir, "certs", "ca.pem")
        self.ca_key = os.path.join(state_dir, "certs", "ca.key")
        self.zone_for = zone_for
        self.lock = threading.Lock()
        self.contexts = {}
        self.days = {}

    def days_for(self, host, days):
        """Certificates for the host from now on are valid this many days (None: the default again)."""
        with self.lock:
            if days is None:
                self.days.pop(host.lower(), None)
            else:
                self.days[host.lower()] = max(1, int(days))

    def names_for(self, host):
        zone = self.zone_for(host)
        if zone is None or zone.get("total_tls"):
            return [host]
        return [zone["name"], "*." + zone["name"]]

    def context_for(self, host):
        host = host.lower().rstrip(".")
        names = self.names_for(host)
        with self.lock:
            days = self.days.get(host, self.DEFAULT_DAYS)
            key = (tuple(names), days)
            context = self.contexts.get(key)
            if context is None:
                crt, pem_key = self._make(names, days)
                context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
                context.load_cert_chain(crt, pem_key)
                self.contexts[key] = context
            return context

    def _make(self, names, days):
        stem = os.path.join(self.dir, secrets.token_hex(6))
        key, csr, ext, crt = stem + ".key", stem + ".csr", stem + ".ext", stem + ".pem"
        with open(ext, "w") as f:
            f.write("basicConstraints=CA:FALSE\n")
            f.write("keyUsage=critical,digitalSignature\n")
            f.write("extendedKeyUsage=serverAuth\n")
            f.write("subjectAltName=" + ",".join(f"DNS:{n}" for n in names) + "\n")
        _openssl("req", "-new", "-newkey", "ec", "-pkeyopt", "ec_paramgen_curve:prime256v1", "-nodes", "-keyout", key, "-out", csr,
                 "-subj", f"/CN={names[0]}")
        _openssl("x509", "-req", "-in", csr, "-CA", self.ca_crt, "-CAkey", self.ca_key, "-set_serial", "0x" + secrets.token_hex(12),
                 "-out", crt, "-days", str(days), "-sha256", "-extfile", ext)
        os.chmod(key, 0o600)
        return crt, key

    def sni(self, ssl_object, server_name, context):
        """The server's SNI callback: an API host keeps the stand-ins' own certificate, any other name gets the edge's."""
        name = (server_name or "").lower()
        if not name or name in HOSTS:
            return None
        try:
            ssl_object.context = self.context_for(name)
        except Exception as e:  # noqa: BLE001 - the handshake then fails, which the client sees
            print(f"[standin] no edge certificate for {name}: {e}", flush=True)
        return None
