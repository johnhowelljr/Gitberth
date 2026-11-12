"""Where everything is, as inner.sh tells the runner."""
import os

GITBERTH_URL = os.environ.get("E2E_GITBERTH_URL", "http://gitberth:8080").rstrip("/")
STANDIN_URL = os.environ.get("E2E_STANDIN_URL", "http://gbe2e-standin:8099").rstrip("/")
ADMIN_PASSWORD = os.environ.get("E2E_ADMIN_PASSWORD", "")
# The LAN address Gitberth gives apps (GITBERTH_PUBLIC_HOST): the rig's engine container on gbe2e-lan.
PUBLIC_HOST = os.environ.get("E2E_PUBLIC_HOST", "192.168.70.10")
GITBERTH_CONTAINER = os.environ.get("E2E_GITBERTH_CONTAINER", "gitberth")
# Gitberth's data volume, mounted into the runner: its database (read only) and deploy logs.
GBDATA = os.environ.get("E2E_GBDATA", "/gbdata")
OUT = os.environ.get("E2E_OUT", "/out")
# Leave what the scenarios made in place, for a person to look at in the UI afterwards.
KEEP = bool(os.environ.get("E2E_KEEP"))
