"""Gitberth's audit log (/data/audit.db), read only: its entries since a mark, and the check that Gitberth's own Docker work
was never recorded as a change made outside Gitberth."""
import os
import time

from . import config
from .gitberth import Db

AUDIT = Db(os.path.join(config.GBDATA, "audit.db"))

# Docker joins and leaves a container's networks as it starts and stops it, seconds apart from the container's own events on
# a busy engine. Each join or leave waits for its container's next event, and at most 30 s, before it's said.
JOIN_WAIT = 35


def last_id():
    """The newest entry's number: a mark to look at what came after."""
    row = AUDIT.one("SELECT MAX(id) AS id FROM entries")
    return (row or {}).get("id") or 0


def since(first, where="1=1", *params):
    """The entries after entry `first` that match `where`, oldest first."""
    return AUDIT.query(f"SELECT * FROM entries WHERE id > ? AND {where} ORDER BY id", first, *params)


def no_network_change_outside(container, first, wait=JOIN_WAIT):
    """Asserts that no join or leave of the container's networks after entry `first` was recorded as made outside Gitberth,
    once any could have been said."""
    time.sleep(wait)
    said = [(e["id"], e["action"], e["summary"]) for e in since(first, "actor_kind = 'Docker' AND action LIKE 'docker.network.%' AND container = ?", container)]
    assert said == [], said
