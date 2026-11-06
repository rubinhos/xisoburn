"""Window for xisoburn-receiver (Tkinter, standard library).

Shows what to type on the Xbox (address, port, token), the discs being received with their progress,
and the log. Settings are remembered in %APPDATA%\\xisoburn-receiver\\settings.json.
The HTTP server itself is xisoburn_receiver; this module only drives it.
"""

import json
import os
import queue
import secrets
import string
import sys
import threading
import time
import tkinter as tk
from tkinter import filedialog, messagebox, ttk

import xisoburn_receiver as rx

APP_DIR = os.path.join(os.environ.get("APPDATA") or os.path.expanduser("~"), "xisoburn-receiver")
SETTINGS_PATH = os.path.join(APP_DIR, "settings.json")
SECTOR = rx.SECTOR


def load_settings():
    try:
        with open(SETTINGS_PATH, "r", encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return {}


def save_settings(settings):
    try:
        os.makedirs(APP_DIR, exist_ok=True)
        with open(SETTINGS_PATH, "w", encoding="utf-8") as f:
            json.dump(settings, f, indent=2)
    except OSError:
        pass


def new_token():
    alphabet = string.ascii_lowercase + string.digits
    return "".join(secrets.choice(alphabet) for _ in range(8))


def default_out():
    return os.path.join(os.path.expanduser("~"), "Videos", "xISOBurn")


def fmt_size(nbytes):
    return "%.2f GiB" % (nbytes / 2**30) if nbytes >= 2**30 else "%.0f MiB" % (nbytes / 2**20)


class App:
    POLL_MS = 500

    def __init__(self, root, out=None, port=None, token=None, bind="0.0.0.0", autostart=False):
        self.root = root
        self.bind = bind
        self.server = None
        self.thread = None
        self.events = queue.Queue()
        self.speed = {}  # disc name -> (time, received sectors, bytes/s)

        s = load_settings()
        self.out_var = tk.StringVar(value=out or s.get("out") or default_out())
        self.port_var = tk.StringVar(value=str(port or s.get("port") or 8770))
        self.token_var = tk.StringVar(value=token or s.get("token") or new_token())
        self.state_var = tk.StringVar(value="Stopped")
        self.address_var = tk.StringVar(value="-")
        self.conn_port_var = tk.StringVar(value="-")
        self.conn_token_var = tk.StringVar(value="-")

        root.title("xISOBurn receiver")
        root.minsize(760, 560)
        self.build()
        root.protocol("WM_DELETE_WINDOW", self.on_close)
        root.after(self.POLL_MS, self.tick)
        self.log("Choose the output folder and press Start. Then type the address, port and token on the "
                 "Xbox (xISOBurn > Settings).")
        if autostart:
            root.after(100, self.start)

    # ------------------------------------------------------------ layout

    def build(self):
        pad = {"padx": 8, "pady": 4}
        main = ttk.Frame(self.root, padding=10)
        main.pack(fill="both", expand=True)
        main.columnconfigure(0, weight=1)
        main.rowconfigure(3, weight=1)
        main.rowconfigure(4, weight=1)

        # Settings
        box = ttk.LabelFrame(main, text="Settings", padding=8)
        box.grid(row=0, column=0, sticky="ew")
        box.columnconfigure(1, weight=1)
        ttk.Label(box, text="Output folder").grid(row=0, column=0, sticky="w", **pad)
        self.out_entry = ttk.Entry(box, textvariable=self.out_var)
        self.out_entry.grid(row=0, column=1, columnspan=3, sticky="ew", **pad)
        self.browse_btn = ttk.Button(box, text="Browse...", command=self.browse)
        self.browse_btn.grid(row=0, column=4, **pad)
        ttk.Label(box, text="Port").grid(row=1, column=0, sticky="w", **pad)
        self.port_entry = ttk.Entry(box, textvariable=self.port_var, width=8)
        self.port_entry.grid(row=1, column=1, sticky="w", **pad)
        ttk.Label(box, text="Token").grid(row=1, column=2, sticky="e", **pad)
        self.token_entry = ttk.Entry(box, textvariable=self.token_var, width=16)
        self.token_entry.grid(row=1, column=3, sticky="w", **pad)
        self.token_btn = ttk.Button(box, text="New token", command=lambda: self.token_var.set(new_token()))
        self.token_btn.grid(row=1, column=4, **pad)

        # Controls
        bar = ttk.Frame(main)
        bar.grid(row=1, column=0, sticky="ew", pady=(8, 0))
        self.start_btn = ttk.Button(bar, text="Start", command=self.start)
        self.start_btn.pack(side="left", padx=4)
        self.stop_btn = ttk.Button(bar, text="Stop", command=self.stop, state="disabled")
        self.stop_btn.pack(side="left", padx=4)
        ttk.Button(bar, text="Allow in Windows Firewall", command=self.firewall).pack(side="left", padx=4)
        ttk.Button(bar, text="Open output folder", command=self.open_folder).pack(side="left", padx=4)
        ttk.Label(bar, textvariable=self.state_var, font=("Segoe UI", 10, "bold")).pack(side="right", padx=4)

        # What to type on the Xbox
        conn = ttk.LabelFrame(main, text="Type these on the Xbox (xISOBurn > Settings)", padding=8)
        conn.grid(row=2, column=0, sticky="ew", pady=(8, 0))
        big = ("Segoe UI", 15, "bold")
        for col, (label, var) in enumerate((("PC address", self.address_var), ("Port", self.conn_port_var),
                                             ("Token", self.conn_token_var))):
            ttk.Label(conn, text=label).grid(row=0, column=col, sticky="w", padx=12)
            ttk.Label(conn, textvariable=var, font=big).grid(row=1, column=col, sticky="w", padx=12)
            conn.columnconfigure(col, weight=1 if col == 0 else 0)

        # Discs
        discs = ttk.LabelFrame(main, text="Discs", padding=8)
        discs.grid(row=3, column=0, sticky="nsew", pady=(8, 0))
        discs.columnconfigure(0, weight=1)
        discs.rowconfigure(0, weight=1)
        cols = ("received", "percent", "speed", "status")
        self.tree = ttk.Treeview(discs, columns=cols, height=4)
        self.tree.heading("#0", text="Disc")
        self.tree.heading("received", text="Received")
        self.tree.heading("percent", text="%")
        self.tree.heading("speed", text="Speed")
        self.tree.heading("status", text="Status")
        self.tree.column("#0", width=150)
        self.tree.column("received", width=170)
        self.tree.column("percent", width=70, anchor="e")
        self.tree.column("speed", width=90, anchor="e")
        self.tree.column("status", width=260)
        self.tree.grid(row=0, column=0, sticky="nsew")
        self.progress = ttk.Progressbar(discs, maximum=100)
        self.progress.grid(row=1, column=0, sticky="ew", pady=(6, 0))

        # Log
        logbox = ttk.LabelFrame(main, text="Log", padding=8)
        logbox.grid(row=4, column=0, sticky="nsew", pady=(8, 0))
        logbox.columnconfigure(0, weight=1)
        logbox.rowconfigure(0, weight=1)
        self.text = tk.Text(logbox, height=8, wrap="word", state="disabled", font=("Consolas", 9))
        scroll = ttk.Scrollbar(logbox, command=self.text.yview)
        self.text.configure(yscrollcommand=scroll.set)
        self.text.grid(row=0, column=0, sticky="nsew")
        scroll.grid(row=0, column=1, sticky="ns")

    # ------------------------------------------------------------ actions

    def browse(self):
        folder = filedialog.askdirectory(initialdir=self.out_var.get() or default_out(),
                                         title="Folder for the ISO files")
        if folder:
            self.out_var.set(os.path.normpath(folder))

    def open_folder(self):
        folder = self.out_var.get()
        if folder.lower().endswith(".iso"):
            folder = os.path.dirname(folder)
        if os.path.isdir(folder) and hasattr(os, "startfile"):
            os.startfile(folder)
        else:
            messagebox.showinfo("xISOBurn receiver", "The folder does not exist yet; it is created on Start.")

    def firewall(self):
        port = self.port_or_none()
        if port is None:
            return
        cmd = rx.firewall_command(port)
        if os.name != "nt":
            messagebox.showinfo("Firewall", cmd)
            return
        try:
            import ctypes
            # Runs PowerShell elevated: Windows shows the administrator prompt.
            args = '-NoProfile -Command "%s"' % cmd
            r = ctypes.windll.shell32.ShellExecuteW(None, "runas", "powershell.exe", args, None, 0)
            if r <= 32:
                raise OSError("ShellExecute returned %d" % r)
            self.log("Firewall: rule requested for TCP port %d on private networks." % port)
        except Exception as e:
            messagebox.showerror("Firewall", "Could not start PowerShell as administrator (%s).\n\nRun this in an "
                                             "administrator PowerShell instead:\n\n%s" % (e, cmd))

    def port_or_none(self):
        try:
            port = int(self.port_var.get().strip())
            if 1 <= port <= 65535:
                return port
        except ValueError:
            pass
        messagebox.showerror("xISOBurn receiver", "The port must be a number from 1 to 65535.")
        return None

    def start(self):
        if self.server is not None:
            return
        port = self.port_or_none()
        if port is None:
            return
        out = self.out_var.get().strip()
        token = self.token_var.get().strip()
        if not out:
            messagebox.showerror("xISOBurn receiver", "Choose the output folder.")
            return
        if not token:
            self.token_var.set(new_token())
            token = self.token_var.get()
        try:
            os.makedirs(out if not out.lower().endswith(".iso") else os.path.dirname(os.path.abspath(out)), exist_ok=True)
            server = rx.make_server(out, port, token, self.bind, console=False, on_event=self.on_event)
        except OSError as e:
            messagebox.showerror("xISOBurn receiver", "Could not start on port %d:\n%s\n\nIs another receiver "
                                                      "already running? Try another port." % (port, e))
            return
        save_settings({"out": out, "port": port, "token": token})
        self.server = server
        self.thread = threading.Thread(target=server.serve_forever, daemon=True)
        self.thread.start()

        addrs = rx.local_addresses()
        self.address_var.set("  or  ".join(addrs) if addrs else "(no network address found)")
        self.conn_port_var.set(str(port))
        self.conn_token_var.set(token)
        self.state_var.set("Running")
        self.set_editable(False)
        self.log("Started: saving to %s, port %d. Waiting for the Xbox..." % (server.receiver.out, port))

    def stop(self):
        if self.server is None:
            return
        server, self.server = self.server, None
        server.shutdown()
        server.receiver.close_all()
        server.server_close()
        self.state_var.set("Stopped")
        self.address_var.set("-")
        self.conn_port_var.set("-")
        self.conn_token_var.set("-")
        self.set_editable(True)
        self.log("Stopped. Progress is kept; the Xbox app resumes when the receiver runs again.")

    def set_editable(self, editable):
        state = "normal" if editable else "disabled"
        for w in (self.out_entry, self.port_entry, self.token_entry, self.browse_btn, self.token_btn):
            w.configure(state=state)
        self.start_btn.configure(state="normal" if editable else "disabled")
        self.stop_btn.configure(state="disabled" if editable else "normal")

    def on_close(self):
        if self.server is not None:
            active = [r for r in self.server.receiver.snapshot() if time.time() - r[4] < 10]
            if active and not messagebox.askyesno("xISOBurn receiver",
                                                  "A disc is being received. Stop and close?\n"
                                                  "(Progress is kept; the copy can be resumed later.)"):
                return
            self.stop()
        self.root.destroy()

    # ------------------------------------------------------------ updates

    def on_event(self, name, message):
        # Called from server threads: hand over to the Tk thread.
        self.events.put((name, message))

    def log(self, message, name=""):
        line = "%s  %s%s\n" % (time.strftime("%H:%M:%S"), "[%s] " % name if name else "", message)
        self.text.configure(state="normal")
        self.text.insert("end", line)
        self.text.see("end")
        self.text.configure(state="disabled")

    def tick(self):
        try:
            while True:
                name, message = self.events.get_nowait()
                self.log(message, name)
        except queue.Empty:
            pass
        if self.server is not None:
            self.refresh_discs(self.server.receiver.snapshot())
        self.root.after(self.POLL_MS, self.tick)

    def refresh_discs(self, rows):
        now = time.time()
        latest = None
        for name, path, received, total, last_write, sha in rows:
            t0, r0, bps = self.speed.get(name, (now, received, 0.0))
            if now - t0 >= 2.0:
                bps = (received - r0) * SECTOR / (now - t0)
                t0, r0 = now, received
            self.speed[name] = (t0, r0, bps)
            pct = received * 100.0 / total if total else 0
            if sha:
                status = "Finished - SHA-256 %s..." % sha[:16]
            elif now - last_write < 5:
                status = "Receiving"
            else:
                status = "Paused / waiting for the Xbox"
            values = ("%s / %s" % (fmt_size(received * SECTOR), fmt_size(total * SECTOR)), "%.1f" % pct,
                      ("%.1f MiB/s" % (bps / 2**20)) if status == "Receiving" else "-", status)
            if self.tree.exists(name):
                self.tree.item(name, values=values)
            else:
                self.tree.insert("", "end", iid=name, text=name, values=values)
            if latest is None or last_write > latest[0]:
                latest = (last_write, pct)
        if latest is not None:
            self.progress["value"] = latest[1]


def run(out=None, port=None, token=None, bind="0.0.0.0"):
    if os.name == "nt":
        try:  # crisp text on high-DPI screens
            import ctypes
            ctypes.windll.shcore.SetProcessDpiAwareness(1)
        except Exception:
            pass
    root = tk.Tk()
    try:
        ttk.Style().theme_use("vista" if os.name == "nt" else "clam")
    except tk.TclError:
        pass
    App(root, out=out, port=port, token=token, bind=bind, autostart=bool(out))
    root.mainloop()
    return 0


if __name__ == "__main__":
    sys.exit(run())
