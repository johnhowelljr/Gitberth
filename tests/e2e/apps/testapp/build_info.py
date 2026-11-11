"""What the build saw, written into the image at build time and served at /build. Values of variables that may be
secret are given only as SHA-256 hashes, so a scenario can tell which scope's build-time secret reached the build
without the image holding it."""
import hashlib
import json
import os
import time

# The base image's own variables, left out.
BASE = {"PATH", "HOME", "HOSTNAME", "LANG", "GPG_KEY", "PYTHON_VERSION", "PYTHON_SHA256", "SHLVL", "PWD", "TERM", "OLDPWD"}
# Shown as they are: Gitberth's Vercel variables and the scenarios' own public ones.
PLAIN = ("VERCEL_", "NEXT_PUBLIC_", "E2E_PUBLIC_")


def main():
    env = {k: v for k, v in os.environ.items() if k not in BASE}
    marker = open("/app/marker.txt").read().strip() if os.path.exists("/app/marker.txt") else None
    print(json.dumps({
        "built_at": int(time.time()),
        "marker": marker,
        "plain": {k: v for k, v in sorted(env.items()) if k.startswith(PLAIN)},
        "sha256": {k: hashlib.sha256(v.encode()).hexdigest() for k, v in sorted(env.items())},
    }, indent=1))


if __name__ == "__main__":
    main()
