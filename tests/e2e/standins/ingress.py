"""A remotely managed tunnel's ingress, read the way cloudflared reads it (Python's standard library only).

- decode(config): the configuration Cloudflare pushes, decoded with cloudflared's field types (RemoteConfigJSON,
  UnvalidatedIngressRule, OriginRequestConfig, AccessConfig) and checked as ingress/ingress.go checks it
  (validateIngress, validateAccessConfiguration, validateHostname). Anything cloudflared refuses raises ConfigError
  with the text cloudflared logs as its error.
- match(rules, host, path): the rule cloudflared picks for a request (Ingress.FindMatchingRule, Rule.Matches): the
  first whose hostname (exact, or a leading "*.") and path regex fit, else the last one.

The fake cloudflared runs on it. Nothing here contacts anything."""
import re

HOP_BY_HOP = ("connection", "keep-alive", "proxy-authenticate", "proxy-authorization", "proxy-connection", "te", "trailer",
              "transfer-encoding", "upgrade")

LAST_RULE_NOT_CATCH_ALL = "The last ingress rule must match all URLs (i.e. it should not have a hostname or path filter)"
BAD_WILDCARD = 'Hostname patterns can have at most one wildcard character ("*") and it can only be used for subdomains, e.g. "*.example.com"'
HOSTNAME_WITH_PORT = "Hostname cannot contain a port"


class ConfigError(Exception):
    """A configuration cloudflared refuses; str() is what it logs as error=."""


# ---- decoding with cloudflared's Go types (encoding/json: unknown fields ignored, null leaves the zero value, keys
# matched exactly or else case-insensitively) ----

def _kind(value):
    if isinstance(value, bool):
        return "bool"
    if isinstance(value, (int, float)):
        return "number"
    if isinstance(value, str):
        return "string"
    if isinstance(value, list):
        return "array"
    if isinstance(value, dict):
        return "object"
    return "null"


def _field(obj, name):
    if name in obj:
        return obj[name]
    for k, v in obj.items():
        if k.lower() == name.lower():
            return v
    return None


def _type_error(value, struct, path, gotype):
    return ConfigError(f"json: cannot unmarshal {_kind(value)} into Go struct field {struct}.{path} of type {gotype}")


def _string(obj, name, struct, path):
    v = _field(obj, name)
    if v is not None and not isinstance(v, str):
        raise _type_error(v, struct, path, "string")
    return v or ""


def _bool(obj, name, struct, path):
    v = _field(obj, name)
    if v is not None and not isinstance(v, bool):
        raise _type_error(v, struct, path, "bool")
    return bool(v)


def _int(obj, name, struct, path, gotype="int"):
    v = _field(obj, name)
    if v is not None and (isinstance(v, bool) or not isinstance(v, int)):
        raise _type_error(v, struct, path, gotype)
    return v


def _duration(obj, name, path):
    # config.CustomDuration takes whole seconds as a JSON number, nothing else (strconv.ParseInt on the raw value).
    v = _field(obj, name)
    if v is None:
        return None
    if isinstance(v, bool) or not isinstance(v, int):
        raw = f'"{v}"' if isinstance(v, str) else str(v).lower()
        raise ConfigError(f'strconv.ParseInt: parsing "{raw}": invalid syntax')
    return v


def _origin_request(o, path):
    """OriginRequestConfig: its typed fields; anything else is ignored, as Go ignores unknown fields."""
    if o is None:
        return {}
    if not isinstance(o, dict):
        raise _type_error(o, "UnvalidatedIngressRule", path, "config.OriginRequestConfig")
    s = "OriginRequestConfig"
    out = {}
    for name in ("connectTimeout", "tlsTimeout", "tcpKeepAlive", "keepAliveTimeout"):
        if _duration(o, name, f"{path}.{name}") is not None:
            out[name] = _field(o, name)
    for name in ("noHappyEyeballs", "matchSNItoHost", "noTLSVerify", "disableChunkedEncoding", "bastionMode", "http2Origin"):
        if _field(o, name) is not None:
            out[name] = _bool(o, name, s, f"{path}.{name}")
    for name in ("httpHostHeader", "originServerName", "caPool", "proxyAddress", "proxyType"):
        if _field(o, name) is not None:
            out[name] = _string(o, name, s, f"{path}.{name}")
    if _field(o, "keepAliveConnections") is not None:
        out["keepAliveConnections"] = _int(o, "keepAliveConnections", s, f"{path}.keepAliveConnections")
    if _field(o, "proxyPort") is not None:
        port = _int(o, "proxyPort", s, f"{path}.proxyPort", "uint")
        if port < 0:
            raise ConfigError(f"json: cannot unmarshal number {port} into Go struct field {s}.{path}.proxyPort of type uint")
        out["proxyPort"] = port
    rules = _field(o, "ipRules")
    if rules is not None:
        if not isinstance(rules, list):
            raise _type_error(rules, s, f"{path}.ipRules", "[]config.IngressIPRule")
        for r in rules:
            if not isinstance(r, dict):
                raise _type_error(r, s, f"{path}.ipRules", "config.IngressIPRule")
            _string(r, "prefix", "IngressIPRule", f"{path}.ipRules.prefix")
            _bool(r, "allow", "IngressIPRule", f"{path}.ipRules.allow")
            ports = _field(r, "ports")
            if ports is not None and (not isinstance(ports, list) or any(isinstance(p, bool) or not isinstance(p, int) for p in ports)):
                raise _type_error(ports, "IngressIPRule", f"{path}.ipRules.ports", "[]int")
        out["ipRules"] = rules
    access = _field(o, "access")
    if access is not None:
        if not isinstance(access, dict):
            raise _type_error(access, s, f"{path}.access", "config.AccessConfig")
        a = "AccessConfig"
        tags = _field(access, "audTag")
        if tags is not None and (not isinstance(tags, list) or any(not isinstance(t, str) for t in tags)):
            bad = tags if not isinstance(tags, list) else next(t for t in tags if not isinstance(t, str))
            raise _type_error(bad, a, f"{path}.access.audTag", "[]string" if not isinstance(tags, list) else "string")
        out["access"] = {"required": _bool(access, "required", a, f"{path}.access.required"),
                         "teamName": _string(access, "teamName", a, f"{path}.access.teamName"),
                         "audTag": list(tags or []),
                         "environment": _string(access, "environment", a, f"{path}.access.environment")}
    return out


# ---- validation, as ingress/ingress.go does it ----

def _has_port(hostname):
    """Whether Go's net.SplitHostPort would split it (then cloudflared refuses the hostname)."""
    i = hostname.rfind(":")
    if i < 0:
        return False
    if hostname.startswith("["):
        end = hostname.find("]")
        if end < 0 or end + 1 != i:
            return False
        return "[" not in hostname[1:] and "]" not in hostname[end + 1:]
    return ":" not in hostname[:i] and "[" not in hostname and "]" not in hostname


# RE2 (Go's regexp) has no lookaround, backreferences, atomic groups, possessive quantifiers, conditionals or \Z.
_NOT_RE2 = re.compile(r"\(\?(?:=|!|<=|<!|>|\(|P=)|\\[1-9]|\\Z|[*+?}]\+")


def _regex_problem(pattern):
    try:
        re.compile(pattern)
    except re.error as e:
        return f"error parsing regexp: {e}: `{pattern}`"
    if _NOT_RE2.search(pattern):
        return f"error parsing regexp: invalid or unsupported Perl syntax: `{pattern}`"
    return None


def _service(text):
    """What a rule's service is (kind, details), or ConfigError as validateIngress refuses it."""
    if text.startswith("unix:") or text.startswith("unix+tls:"):
        return {"kind": "unix", "path": text.split(":", 1)[1]}
    if text.startswith("http_status:"):
        raw = text[len("http_status:"):]
        if not re.fullmatch(r"[+-]?\d+", raw):
            raise ConfigError(f'invalid HTTP status code: strconv.Atoi: parsing "{raw}": invalid syntax')
        code = int(raw)
        if code < 100 or code > 999:
            raise ConfigError(f"invalid HTTP status code: {code}")
        return {"kind": "status", "code": code}
    if text in ("hello_world", "hello-world"):
        return {"kind": "hello"}
    if text == "socks-proxy":
        return {"kind": "socks"}
    if text == "bastion":
        return {"kind": "bastion"}
    # Go's url.Parse, as far as cloudflared looks: the scheme, the host (and port) of the authority, and the path.
    m = re.fullmatch(r"(?P<scheme>[A-Za-z][A-Za-z0-9+.-]*):(?P<rest>.*)", text, re.S)
    scheme = m["scheme"].lower() if m else ""
    rest = m["rest"] if m else text
    host, port, path = "", None, ""
    if scheme and rest.startswith("//"):
        authority = re.match(r"[^/?#]*", rest[2:]).group(0)
        path = re.match(r"[^?#]*", rest[2 + len(authority):]).group(0)
        hostport = authority.rsplit("@", 1)[-1]
        if hostport.startswith("["):
            end = hostport.find("]")
            if end < 0:
                raise ConfigError(f"parse \"{text}\": missing ']' in host")
            host, after = hostport[1:end], hostport[end + 1:]
            if after and not re.fullmatch(r":\d*", after):
                raise ConfigError(f'parse "{text}": invalid port "{after}" after host')
            port = after[1:] or None
        else:
            i = hostport.rfind(":")
            if i >= 0:
                if not re.fullmatch(r":\d*", hostport[i:]):
                    raise ConfigError(f'parse "{text}": invalid port "{hostport[i:]}" after host')
                host, port = hostport[:i], hostport[i + 1:] or None
            else:
                host = hostport
    if not scheme or not host:
        raise ConfigError(f"{text} is an invalid address, please make sure it has a scheme and a hostname")
    if path:
        raise ConfigError(f"{text} is an invalid address, ingress rules don't support proxying to a different path on the origin "
                          "service. The path will be the same as the eyeball request's path")
    kind = "http" if scheme in ("http", "https", "ws", "wss") else "tcp"
    return {"kind": kind, "scheme": scheme, "host": host, "port": int(port) if port else None, "url": text}


def decode(config):
    """The ingress rules cloudflared would run for this configuration (a list of dicts: hostname, path, service,
    originRequest, plus the parsed service and compiled path), or ConfigError with the error it would log."""
    if not isinstance(config, dict):
        raise ConfigError(f"json: cannot unmarshal {_kind(config)} into Go value of type orchestration.newRemoteConfig")
    defaults = _origin_request(_field(config, "originRequest"), "originRequest")
    warp = _field(config, "warp-routing")
    if warp is not None and not isinstance(warp, dict):
        raise _type_error(warp, "RemoteConfigJSON", "warp-routing", "config.WarpRoutingConfig")
    raw = _field(config, "ingress")
    if raw is None:
        raw = []
    if not isinstance(raw, list):
        raise _type_error(raw, "RemoteConfigJSON", "ingress", "[]config.UnvalidatedIngressRule")
    typed = []
    for r in raw:
        if not isinstance(r, dict):
            raise ConfigError(f"json: cannot unmarshal {_kind(r)} into Go struct field RemoteConfigJSON.ingress of type config.UnvalidatedIngressRule")
        typed.append({"hostname": _string(r, "hostname", "UnvalidatedIngressRule", "ingress.hostname"),
                      "path": _string(r, "path", "UnvalidatedIngressRule", "ingress.path"),
                      "service": _string(r, "service", "UnvalidatedIngressRule", "ingress.service"),
                      "originRequest": _origin_request(_field(r, "originRequest"), "ingress.originRequest")})
    rules = []
    for i, r in enumerate(typed):
        service = _service(r["service"])
        access = r["originRequest"].get("access")
        if access and access["required"] and access["audTag"] and not access["teamName"]:
            raise ConfigError("access.TeamName cannot be blank when access.audTags are present")
        hostname, path = r["hostname"], r["path"]
        if _has_port(hostname):
            raise ConfigError(HOSTNAME_WITH_PORT)
        if hostname.rfind("*") > 0:
            raise ConfigError(BAD_WILDCARD)
        catch_all = hostname in ("", "*") and path == ""
        last = i == len(typed) - 1
        if last and not catch_all:
            raise ConfigError(LAST_RULE_NOT_CATCH_ALL)
        if not last and catch_all:
            raise ConfigError(f"Rule #{i + 1} is matching the hostname '{hostname}', but this will match every hostname, meaning the "
                              "rules which follow it will never be triggered.")
        compiled = None
        if path:
            problem = _regex_problem(path)
            if problem:
                raise ConfigError(f"Rule #{i + 1} has an invalid regex: {problem}")
            compiled = re.compile(path)
        origin = dict(defaults)
        origin.update(r["originRequest"])
        rules.append({"index": i, "hostname": hostname, "path": path, "service": r["service"], "parsed": service,
                      "regex": compiled, "originRequest": origin})
    return rules


def problem(config):
    """Why cloudflared would refuse the configuration, or None."""
    try:
        decode(config)
        return None
    except ConfigError as e:
        return str(e)


def _host_matches(rule_host, host):
    if rule_host == host:
        return True
    return rule_host.startswith("*.") and host.endswith(rule_host[1:])


def match(rules, host, path):
    """(index, rule) cloudflared sends a request for host and path to: the first rule that fits, else the last."""
    host = host or ""
    if _has_port(host):
        host = host[1:host.find("]")] if host.startswith("[") else host.rsplit(":", 1)[0]
    for i, rule in enumerate(rules):
        host_ok = rule["hostname"] in ("", "*") or _host_matches(rule["hostname"], host)
        path_ok = rule["regex"] is None or rule["regex"].search(path or "") is not None
        if host_ok and path_ok:
            return i, rule
    return len(rules) - 1, rules[-1]
