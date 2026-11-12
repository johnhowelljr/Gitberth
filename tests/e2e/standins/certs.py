"""The cert maker: the rig's own test CA and the stand-ins' certificate, made with openssl the first time the stand-in
starts (nothing here is committed). The CA's private key stays in the stand-in's state volume; only its certificate goes
to the trust volume, which Gitberth's CA bundle is made from."""
import os
import shutil
import subprocess

# Every name the stand-ins answer for over HTTPS.
HOSTS = [
    "github.com", "www.github.com", "api.github.com", "codeload.github.com", "raw.githubusercontent.com",
    "objects.githubusercontent.com", "uploads.github.com", "api.cloudflare.com", "gbe2e-standin",
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
