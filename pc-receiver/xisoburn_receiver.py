#!/usr/bin/env python3
"""xISOBurn receiver: receives a disc image from the xISOBurn Xbox app over the local network.

Usage (see docs/milestone-3.md):

    xisoburn-receiver --out D:\\DVDs\\backup                 (one ISO per disc, named by the app)
    xisoburn-receiver --out D:\\DVDs\\backup\\ACQUARIA.iso    (one fixed file)

Options: --port 8770, --token SECRET (default: generated once and kept next to the output),
--bind 0.0.0.0.

Protocol (HTTP/1.1, JSON responses, header X-Token on every request, local network clients only):

    GET  /hello                                   -> receiver info (used by "Test connection")
    GET  /status?name=N&total_sectors=T           -> {"exists", "received": [[start, end), ...], ...}
    PUT  /chunk?name=N&total_sectors=T            body = sectors; headers Content-Range: bytes a-b/size
                                                  and X-Content-SHA256 (hex of the body)
    POST /finish?name=N&total_sectors=T&complete=0|1   body = the app's .map (text)
                                                  -> writes N.map; if complete=1 also SHA-256 of the ISO

The image is written in place (sparse, full disc size) at the offsets the app sends, in any order;
the app may read the disc in several passes. Which sectors arrived is kept in N.iso.received.json,
so both sides can resume after an interruption. Only the standard library is used.
"""

import argparse
import bisect
import hashlib
import hmac
import http.server
import ipaddress
import json
import os
import re
import secrets
import socket
import string
import sys
import threading
import time
import urllib.parse

VERSION = 1
SECTOR = 2048
MAX_BODY = 1 << 20          # the app sends at most 64 sectors (128 KiB) per chunk
STATE_SAVE_INTERVAL = 5.0   # seconds between saves of the received-ranges file
NAME_RE = re.compile(r"^[A-Za-z0-9 _.\-]{1,64}$")
RANGE_RE = re.compile(r"^bytes (\d+)-(\d+)/(\d+)$")


class HttpError(Exception):
    def __init__(self, status, message):
        super().__init__(message)
        self.status = status
        self.message = message


def now():
    return time.strftime("%Y-%m-%d %H:%M:%S")


def make_sparse(f):
    """Marks the file as sparse on NTFS so the reserved size does not take disk space up front."""
    if os.name != "nt":
        return
    try:
        import ctypes
        import msvcrt
        from ctypes import wintypes
        FSCTL_SET_SPARSE = 0x000900C4
        handle = msvcrt.get_osfhandle(f.fileno())
        returned = wintypes.DWORD()
        ctypes.windll.kernel32.DeviceIoControl(wintypes.HANDLE(handle), FSCTL_SET_SPARSE,
                                               None, 0, None, 0, ctypes.byref(returned), None)
    except Exception:
        pass  # not sparse: the file is still correct, it just takes its full size immediately


class Image:
    """One ISO being received: the file, plus the merged list of [start, end) sector ranges received."""

    def __init__(self, path, total_sectors, logger):
        self.path = path
        self.total = total_sectors
        self.log = logger
        self.lock = threading.Lock()
        self.state_path = path + ".received.json"
        self.ranges = []
        self.last_save = 0.0
        self.last_percent = -1

        exists = os.path.exists(path)
        if exists and not os.path.exists(self.state_path):
            raise HttpError(409, "%s already exists and was not created by xisoburn-receiver; "
                                 "move it away or choose another name" % path)
        if os.path.exists(self.state_path):
            with open(self.state_path, "r", encoding="utf-8") as s:
                state = json.load(s)
            if state.get("total_sectors") != total_sectors:
                raise HttpError(409, "%s belongs to a disc with %s sectors, this one has %d; "
                                     "set OutputName in the app or move the file"
                                % (path, state.get("total_sectors"), total_sectors))
            self.ranges = [list(r) for r in state.get("ranges", [])]

        folder = os.path.dirname(os.path.abspath(path))
        os.makedirs(folder, exist_ok=True)
        self.file = open(path, "r+b" if exists else "w+b")
        if not exists:
            make_sparse(self.file)
            self.file.truncate(total_sectors * SECTOR)
            self.save_state(force=True)
            self.log("created %s (%d sectors, %.2f GiB)" % (path, total_sectors, total_sectors * SECTOR / 2**30))
        else:
            self.log("resuming %s: %d of %d sectors already received" % (path, self.received(), total_sectors))

    def received(self):
        return sum(e - s for s, e in self.ranges)

    def add_range(self, s, e):
        r = self.ranges
        i = bisect.bisect_left(r, [s, s])
        if i > 0 and r[i - 1][1] >= s:
            i -= 1
            s = min(s, r[i][0])
        j = i
        while j < len(r) and r[j][0] <= e:
            e = max(e, r[j][1])
            j += 1
        r[i:j] = [[s, e]]

    def write(self, lba, data):
        with self.lock:
            self.file.seek(lba * SECTOR)
            self.file.write(data)
            self.add_range(lba, lba + len(data) // SECTOR)
            if time.time() - self.last_save >= STATE_SAVE_INTERVAL:
                self.save_state()
            percent = self.received() * 100 // self.total
            if percent != self.last_percent and percent % 5 == 0:
                self.last_percent = percent
                self.log("%d%% received (%d of %d sectors)" % (percent, self.received(), self.total))

    def save_state(self, force=False):
        # Data first, then the file that claims it is there.
        self.file.flush()
        os.fsync(self.file.fileno())
        tmp = self.state_path + ".tmp"
        with open(tmp, "w", encoding="utf-8") as s:
            json.dump({"total_sectors": self.total, "ranges": self.ranges}, s)
        os.replace(tmp, self.state_path)
        self.last_save = time.time()

    def sha256(self):
        with self.lock:
            self.save_state(force=True)
            h = hashlib.sha256()
            with open(self.path, "rb") as f:
                while True:
                    block = f.read(1 << 20)
                    if not block:
                        break
                    h.update(block)
            return h.hexdigest()

    def close(self):
        with self.lock:
            try:
                self.save_state(force=True)
            finally:
                self.file.close()


class Receiver:
    """State shared by all requests: output location, token and the open images."""

    def __init__(self, out, token, console=True):
        self.out = os.path.abspath(out)
        self.single_file = self.out.lower().endswith(".iso")
        self.token = token
        self.console = console
        self.images = {}
        self.lock = threading.Lock()

    def base_path(self, name):
        if self.single_file:
            return self.out[:-4]
        return os.path.join(self.out, name)

    def logger(self, name):
        log_path = self.base_path(name) + ".log"

        def log(message):
            line = "%s  %s" % (now(), message)
            if self.console:
                print("[%s] %s" % (name, message), flush=True)
            try:
                os.makedirs(os.path.dirname(log_path), exist_ok=True)
                with open(log_path, "a", encoding="utf-8") as f:
                    f.write(line + "\n")
            except OSError:
                pass
        return log

    def image(self, name, total, create):
        with self.lock:
            img = self.images.get(name)
            if img is not None:
                if img.total != total:
                    raise HttpError(409, "%s is open for a disc with %d sectors, not %d" % (name, img.total, total))
                return img
            path = self.base_path(name) + ".iso"
            if not create and not os.path.exists(path):
                return None
            img = Image(path, total, self.logger(name))
            self.images[name] = img
            return img

    def close_all(self):
        with self.lock:
            for img in self.images.values():
                img.close()
            self.images.clear()


class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server_version = "xisoburn-receiver/%d" % VERSION

    def log_message(self, fmt, *args):
        pass  # requests are summarised in the per-image log instead

    # ------------------------------------------------------------ helpers

    @property
    def receiver(self):
        return self.server.receiver

    def send_json(self, status, obj, close=False):
        body = json.dumps(obj).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")  # /status must never come from an HTTP cache
        if close:
            self.send_header("Connection", "close")
            self.close_connection = True
        self.end_headers()
        self.wfile.write(body)

    def client_allowed(self):
        try:
            ip = ipaddress.ip_address(self.client_address[0])
            if getattr(ip, "ipv4_mapped", None):
                ip = ip.ipv4_mapped
            return ip.is_private or ip.is_loopback or ip.is_link_local
        except ValueError:
            return False

    def read_body(self):
        length = int(self.headers.get("Content-Length") or 0)
        if length > MAX_BODY:
            raise HttpError(413, "body too large (%d bytes)" % length)
        return self.rfile.read(length) if length > 0 else b""

    def params(self):
        url = urllib.parse.urlsplit(self.path)
        q = urllib.parse.parse_qs(url.query)
        return url.path, {k: v[0] for k, v in q.items()}

    @staticmethod
    def disc(params):
        name = params.get("name", "")
        if not NAME_RE.match(name):
            raise HttpError(400, "invalid name %r" % name)
        try:
            total = int(params.get("total_sectors", ""))
        except ValueError:
            raise HttpError(400, "invalid total_sectors")
        if total <= 0 or total > 50_000_000:
            raise HttpError(400, "invalid total_sectors %d" % total)
        return name, total

    def handle_any(self, method):
        try:
            # Read the body first, so an error response never leaves unread bytes on the connection.
            body = self.read_body() if method in ("PUT", "POST") else b""
            if not self.client_allowed():
                raise HttpError(403, "only clients on the local network are accepted")
            if not hmac.compare_digest(self.headers.get("X-Token", ""), self.receiver.token):
                raise HttpError(401, "wrong or missing X-Token")
            path, params = self.params()
            route = (method, path)
            if route == ("GET", "/hello"):
                self.send_json(200, {"app": "xisoburn-receiver", "version": VERSION, "out": self.receiver.out,
                                     "single_file": self.receiver.single_file})
            elif route == ("GET", "/status"):
                self.status(params)
            elif route == ("PUT", "/chunk"):
                self.chunk(params, body)
            elif route == ("POST", "/finish"):
                self.finish(params, body)
            else:
                raise HttpError(404, "no route %s %s" % (method, path))
        except HttpError as e:
            self.send_json(e.status, {"error": e.message}, close=e.status in (401, 403, 413))
        except Exception as e:  # keep the server alive whatever happens
            self.send_json(500, {"error": "%s: %s" % (type(e).__name__, e)})

    def do_GET(self):
        self.handle_any("GET")

    def do_PUT(self):
        self.handle_any("PUT")

    def do_POST(self):
        self.handle_any("POST")

    # ------------------------------------------------------------ routes

    def status(self, params):
        name, total = self.disc(params)
        img = self.receiver.image(name, total, create=False)
        if img is None:
            self.send_json(200, {"exists": False, "image": self.receiver.base_path(name) + ".iso",
                                 "received": [], "received_sectors": 0})
            return
        with img.lock:
            self.send_json(200, {"exists": True, "image": img.path, "total_sectors": img.total,
                                 "received": img.ranges, "received_sectors": img.received()})

    def chunk(self, params, body):
        name, total = self.disc(params)
        m = RANGE_RE.match(self.headers.get("Content-Range", ""))
        if not m:
            raise HttpError(400, "missing or invalid Content-Range")
        first, last, size = (int(x) for x in m.groups())
        if size != total * SECTOR:
            raise HttpError(400, "Content-Range size %d does not match %d sectors" % (size, total))
        if first % SECTOR or (last + 1) % SECTOR or last < first or last >= size:
            raise HttpError(400, "range %d-%d is not sector-aligned or out of the disc" % (first, last))
        if len(body) != last - first + 1:
            raise HttpError(400, "body has %d bytes, Content-Range says %d" % (len(body), last - first + 1))
        expected = self.headers.get("X-Content-SHA256", "").lower()
        actual = hashlib.sha256(body).hexdigest()
        if expected and not hmac.compare_digest(expected, actual):
            raise HttpError(400, "SHA-256 mismatch: the chunk was corrupted on the way; send it again")
        img = self.receiver.image(name, total, create=True)
        img.write(first // SECTOR, body)
        self.send_json(200, {"ok": True})

    def finish(self, params, body):
        name, total = self.disc(params)
        complete = params.get("complete", "0") == "1"
        img = self.receiver.image(name, total, create=False)
        if img is None:
            raise HttpError(404, "nothing received yet for %s" % name)
        base = self.receiver.base_path(name)
        if body:
            with open(base + ".map", "w", encoding="utf-8", newline="\n") as f:
                f.write(body.decode("utf-8", errors="replace"))
        missing = total - img.received()
        result = {"image": img.path, "missing_sectors": missing, "sha256": None}
        if complete:
            img.log("computing SHA-256 of %s ..." % img.path)
            digest = img.sha256()
            with open(base + ".sha256", "w", encoding="utf-8", newline="\n") as f:
                f.write("%s *%s\n" % (digest, os.path.basename(img.path)))
            result["sha256"] = digest
            img.log("finished: SHA-256 %s, %d sector(s) never received (zeros)" % (digest, missing))
        else:
            img.save_state(force=True)
            img.log("map updated (run paused or partial): %d of %d sectors received" % (img.received(), total))
        self.send_json(200, result)


class Server(http.server.ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True


def make_server(out, port, token, bind="0.0.0.0", console=True):
    server = Server((bind, port), Handler)
    server.receiver = Receiver(out, token, console)
    return server


def local_addresses():
    addrs = set()
    try:
        for info in socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET):
            addrs.add(info[4][0])
    except OSError:
        pass
    try:  # the address used to reach the local network (no packet is actually sent)
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
            s.connect(("192.168.0.1", 9))
            addrs.add(s.getsockname()[0])
    except OSError:
        pass
    return sorted(a for a in addrs if not a.startswith("127."))


def load_or_create_token(out):
    folder = os.path.dirname(os.path.abspath(out)) if out.lower().endswith(".iso") else os.path.abspath(out)
    os.makedirs(folder, exist_ok=True)
    path = os.path.join(folder, "xisoburn-receiver.token")
    if os.path.exists(path):
        with open(path, "r", encoding="utf-8") as f:
            token = f.read().strip()
        if token:
            return token, path
    alphabet = string.ascii_lowercase + string.digits
    token = "".join(secrets.choice(alphabet) for _ in range(8))
    with open(path, "w", encoding="utf-8") as f:
        f.write(token + "\n")
    return token, path


def main(argv=None):
    parser = argparse.ArgumentParser(prog="xisoburn-receiver",
                                     description="Receives disc images from the xISOBurn Xbox app.")
    parser.add_argument("--out", required=True,
                        help="output folder (one ISO per disc) or an .iso file path")
    parser.add_argument("--port", type=int, default=8770, help="TCP port (default 8770)")
    parser.add_argument("--token", help="shared secret typed into the app (default: generated and kept "
                                        "in xisoburn-receiver.token next to the output)")
    parser.add_argument("--bind", default="0.0.0.0", help="address to listen on (default: all)")
    args = parser.parse_args(argv)

    token = args.token
    token_note = "from --token"
    if not token:
        token, token_path = load_or_create_token(args.out)
        token_note = "kept in " + token_path

    server = make_server(args.out, args.port, token, args.bind)
    print("xISOBurn receiver %d" % VERSION)
    print("  output : %s%s" % (server.receiver.out, " (single file)" if server.receiver.single_file else " (one ISO per disc)"))
    print("  token  : %s   (%s)" % (token, token_note))
    addrs = local_addresses()
    print("  address: " + (", ".join("%s:%d" % (a, args.port) for a in addrs) if addrs else "port %d" % args.port))
    print()
    print("Type the address, port and token into xISOBurn on the Xbox.")
    print("Windows Firewall: allow the app on PRIVATE networks when Windows asks, or run once as administrator:")
    print('  New-NetFirewallRule -DisplayName "xISOBurn receiver" -Direction Inbound -Protocol TCP '
          '-LocalPort %d -Action Allow -Profile Private' % args.port)
    print("Only clients on the local network are accepted. Press Ctrl+C to stop.", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.receiver.close_all()
        server.server_close()
        print("stopped; progress saved.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
