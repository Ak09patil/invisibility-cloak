"""
invisibility_server.py - the laptop "server" for the PHONE app (IVRAR)

The phone app opens ITS OWN camera and streams frames here over Wi-Fi.
This script runs the same AI pipeline as before (YOLOv8-seg people masks,
MediaPipe hand gestures, per-person tracking) and sends the result back, so
the phone shows the person vanishing - using the phone's camera.

  phone  --camera frame (JPEG)-->  laptop (AI)  --background / frame+masks / status-->  phone

Gestures (shown by anyone in front of the PHONE's camera):
  open palm  -> that person vanishes (with the glow)
  fist       -> that person reappears
  peace sign -> next effect

Keys on the laptop (click the preview window first):
  b      capture background (same as the phone's CAPTURE button)
  m      mode: Person <-> Cloth colour
  c      calibrate cloth colour (then click the cloth in the preview)
  e      next effect for everyone
  space  toggle vanish for everyone
  q/esc  quit

Run:  python invisibility_server.py            (then type the printed IP into the phone app)
      python invisibility_server.py --port 5055 --effect shimmer
Needs invisibility_ar2.py and invisibility_unity.py in the same folder.
"""
import argparse
import collections
import socket
import struct
import threading
import time

import cv2
import numpy as np

import invisibility_ar2 as ar
from invisibility_ar2 import EFFECTS, H, MODES, W
from invisibility_unity import MAX_SLOTS, assign_slots, build_packet_data

# phone -> laptop message types (first payload byte)
FROM_FRAME = 0x10    # camera frame, JPEG
FROM_COMMAND = 0x11  # UTF-8 text: "bg" | "effect" | "vanish" | "mode"
# laptop -> phone message types (same as the old Unity link, plus status text)
TO_BG = 1
TO_FRAME = 2
TO_STATUS = 3

BG_FRAMES = 20       # frames averaged for the clean background
STOP = threading.Event()   # lets tests (or code) end main() cleanly


# ------------------------------------------------------------------ network
def local_ip():
    """Best guess of this computer's Wi-Fi address (what the phone must type)."""
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        s.connect(("10.255.255.255", 1))  # no packet is sent; just picks the interface
        return s.getsockname()[0]
    except OSError:
        try:
            return socket.gethostbyname(socket.gethostname())
        except OSError:
            return "127.0.0.1"
    finally:
        s.close()


def pack_frame(jpg, png, meta_json):
    return (bytes([TO_FRAME])
            + struct.pack("<I", len(jpg)) + jpg
            + struct.pack("<I", len(png)) + png
            + struct.pack("<I", len(meta_json)) + meta_json)


class PhoneServer:
    """Accepts ONE phone at a time. Newest camera frame wins; newest result wins."""

    def __init__(self, port=5055):
        self.port = port
        self.srv = None
        self.conn = None
        self.running = False
        self.need_bg = False            # set True on every new connection
        self.lock = threading.Lock()
        self.frame_jpg = None           # newest camera frame from the phone
        self.commands = collections.deque()
        self.cv = threading.Condition()
        self.out_frame = None
        self.out_misc = collections.deque()

    @property
    def connected(self):
        return self.conn is not None

    def start(self):
        self.srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.srv.bind(("0.0.0.0", self.port))
        self.srv.listen(2)
        self.running = True
        threading.Thread(target=self._accept_loop, daemon=True).start()
        threading.Thread(target=self._send_loop, daemon=True).start()

    def stop(self):
        self.running = False
        with self.cv:
            self.cv.notify_all()
        for s in (self.conn, self.srv):
            try:
                if s is not None:
                    s.close()
            except OSError:
                pass

    # -- accepting / reading
    def _accept_loop(self):
        while self.running:
            try:
                c, addr = self.srv.accept()
            except OSError:
                break
            c.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            old = self.conn
            self.conn = c
            self.need_bg = True
            with self.lock:
                self.frame_jpg = None
            if old is not None:
                try:
                    old.close()
                except OSError:
                    pass
            print(f"[+] Phone connected from {addr[0]}")
            threading.Thread(target=self._read_loop, args=(c,), daemon=True).start()

    @staticmethod
    def _read_exact(c, n):
        buf = bytearray()
        while len(buf) < n:
            chunk = c.recv(n - len(buf))
            if not chunk:
                raise ConnectionError("closed")
            buf += chunk
        return bytes(buf)

    def _read_loop(self, c):
        try:
            while self.running:
                n = struct.unpack("<I", self._read_exact(c, 4))[0]
                if n <= 0 or n > 16 * 1024 * 1024:
                    break
                payload = self._read_exact(c, n)
                kind = payload[0]
                if kind == FROM_FRAME:
                    with self.lock:
                        self.frame_jpg = payload[1:]   # keep only the newest
                elif kind == FROM_COMMAND:
                    self.commands.append(payload[1:].decode("utf-8", "ignore").strip())
        except (OSError, ConnectionError):
            pass
        finally:
            if self.conn is c:
                self.conn = None
                print("[-] Phone disconnected")
            try:
                c.close()
            except OSError:
                pass

    # -- sending (own thread, so a slow phone never freezes the AI loop)
    def _send_loop(self):
        while self.running:
            with self.cv:
                while self.running and not self.out_misc and self.out_frame is None:
                    self.cv.wait(0.5)
                if not self.running:
                    return
                if self.out_misc:
                    payload = self.out_misc.popleft()
                else:
                    payload, self.out_frame = self.out_frame, None
            c = self.conn
            if c is None:
                continue
            try:
                c.sendall(struct.pack("<I", len(payload)) + payload)
            except OSError:
                if self.conn is c:
                    self.conn = None
                try:
                    c.close()
                except OSError:
                    pass

    def _queue(self, payload, newest_only):
        with self.cv:
            if newest_only:
                self.out_frame = payload
            else:
                self.out_misc.append(payload)
            self.cv.notify()

    def send_bg(self, bg):
        ok, jpg = cv2.imencode(".jpg", bg, [cv2.IMWRITE_JPEG_QUALITY, 90])
        if ok:
            self._queue(bytes([TO_BG]) + jpg.tobytes(), False)
            self.need_bg = False

    def send_frame(self, jpg, png, meta_json):
        self._queue(pack_frame(jpg, png, meta_json), True)

    def send_status(self, text):
        self._queue(bytes([TO_STATUS]) + text.encode("utf-8"), False)

    # -- what the AI loop reads
    def take_frame(self):
        with self.lock:
            f, self.frame_jpg = self.frame_jpg, None
        return f

    def take_commands(self):
        out = []
        while self.commands:
            out.append(self.commands.popleft())
        return out


# ---------------------------------------------------------------- pipeline
class Pipeline:
    """All the AI state. process() takes one camera frame, returns what to send back."""

    def __init__(self, finder, hands_fn, default_effect=1):
        self.finder = finder
        self.hands_fn = hands_fn        # frame -> list of 21-point pixel lists
        self.default_effect = default_effect
        self.people = []
        self.slots = [None] * MAX_SLOTS
        self.bg = None
        self.bg_dirty = False           # a new background that still has to be sent
        self.state = "need_bg"          # need_bg | countdown | collecting | live
        self.countdown_until = 0.0
        self.collect = []
        self.mode = 0
        self.cloth = dict(ar.DEFAULT_CLOTH)
        self.hands = []                 # last hands (for the laptop preview only)
        self.status = ""

    # -- commands (phone buttons or laptop keys)
    def request_bg(self, now):
        self.state = "countdown"
        self.countdown_until = now + 3.0
        self.collect = []

    def next_effect(self):
        for p in self.people:
            p.effect = (p.effect + 1) % len(EFFECTS)
        self.default_effect = (self.default_effect + 1) % len(EFFECTS)

    def toggle_vanish(self):
        anyone = any(not p.vanish for p in self.people)
        for p in self.people:
            p.vanish = anyone

    def toggle_mode(self):
        self.mode = (self.mode + 1) % len(MODES)

    def calibrate(self, frame, x, y):
        c = ar.sample_cloth(frame, x, y)
        if c is None:
            return False
        self.cloth = c
        self.mode = 1
        return True

    def apply_command(self, cmd, now):
        cmd = cmd.lower()
        if cmd == "bg":
            self.request_bg(now)
        elif cmd == "effect":
            self.next_effect()
        elif cmd == "vanish":
            self.toggle_vanish()
        elif cmd == "mode":
            self.toggle_mode()

    # -- one frame
    def process(self, frame, now):
        """Returns (png, meta_json). self.status is updated for the phone."""
        if self.state == "countdown":
            left = self.countdown_until - now
            if left > 0:
                self.status = f"Step out of the frame... {int(left) + 1}"
                return self._empty()
            self.state = "collecting"
            self.collect = []
        if self.state == "collecting":
            self.collect.append(frame.astype(np.float32))
            self.status = f"Hold still... capturing background ({len(self.collect)}/{BG_FRAMES})"
            if len(self.collect) >= BG_FRAMES:
                self.bg = np.mean(self.collect, axis=0).astype(np.uint8)
                self.bg_dirty = True
                self.collect = []
                self.people = []
                self.slots = [None] * MAX_SLOTS
                self.state = "live"
            return self._empty()
        if self.state == "need_bg" or self.bg is None:
            self.status = "Tap CAPTURE BACKGROUND, then step out of the frame"
            return self._empty()

        # ---- live: people + per-person gestures
        known = {p.id for p in self.people}
        self.people = ar.update_tracks(self.people, self.finder(frame))
        for p in self.people:
            if p.id not in known:
                p.effect = self.default_effect   # new person starts with the chosen effect
        assign_slots(self.people, self.slots)
        self.hands = self.hands_fn(frame)
        gest = {}
        for pts in self.hands:
            g = ar.classify(ar.finger_states(pts))
            owner = ar.assign_hand(self.people, *pts[0])
            if owner is not None and g is not None:
                gest[owner.id] = g
        for p in self.people:
            p.step(gest.get(p.id))

        if self.mode == 1:
            cm = ar.cloth_mask(frame, self.cloth)
            for p in self.people:
                p.soft_eff = cm * (p.soft > 0.5).astype(np.float32)
        else:
            for p in self.people:
                p.soft_eff = p.soft

        gestures = ", ".join(f"#{p.id} {p.stable or '-'}" for p in self.people) or "nobody seen"
        self.status = f"Live | {MODES[self.mode]} | {EFFECTS[self.default_effect]} | {gestures}"
        return build_packet_data(self.people, self.slots)

    def _empty(self):
        self.hands = []
        return build_packet_data([], self.slots)


def make_hands_fn():
    import mediapipe as mp
    hands = mp.solutions.hands.Hands(max_num_hands=6, min_detection_confidence=0.5,
                                     min_tracking_confidence=0.5)

    def fn(frame):
        res = hands.process(cv2.cvtColor(frame, cv2.COLOR_BGR2RGB))
        out = []
        for hl in (res.multi_hand_landmarks or []):
            out.append([(l.x * W, l.y * H) for l in hl.landmark])
        return out
    return fn


def make_finder():
    try:
        return ar.YoloPeople(), True
    except Exception as e:
        import mediapipe as mp
        print(f"[!] Multi-person mode unavailable ({e}).\n"
              "    Run: pip install ultralytics   -> falling back to single-person mode.")
        return ar.SelfieSinglePerson(mp), False


# --------------------------------------------------------------------- main
def decode(jpg):
    """JPEG bytes -> (BGR frame resized to W x H, whether it had to be resized)."""
    f = cv2.imdecode(np.frombuffer(jpg, np.uint8), cv2.IMREAD_COLOR)
    if f is None:
        return None, False
    if f.shape[1] != W or f.shape[0] != H:
        return cv2.resize(f, (W, H)), True
    return f, False


def serve_once(server, pipe, jpg, now, last_status):
    """Process one phone frame and queue the answer. Returns the frame and new status."""
    frame, resized = decode(jpg)
    if frame is None:
        return None, last_status
    png, meta = pipe.process(frame, now)
    if server.connected:
        if server.need_bg and pipe.bg is not None:
            server.send_bg(pipe.bg)
        elif pipe.bg_dirty:
            server.send_bg(pipe.bg)
        pipe.bg_dirty = False
        if resized:
            ok, enc = cv2.imencode(".jpg", frame, [cv2.IMWRITE_JPEG_QUALITY, 80])
            jpg = enc.tobytes() if ok else jpg
        server.send_frame(jpg, png, meta)
        if pipe.status != last_status:
            server.send_status(pipe.status)
            last_status = pipe.status
    return frame, last_status


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=5055)
    ap.add_argument("--effect", default="glow", choices=["shimmer", "glow", "cut"],
                    help="starting effect for everyone (default: glow)")
    ap.add_argument("--headless", action="store_true", help="no preview window")
    args = ap.parse_args()

    finder, multi = make_finder()
    pipe = Pipeline(finder, make_hands_fn(),
                    default_effect={"shimmer": 0, "glow": 1, "cut": 2}[args.effect])
    server = PhoneServer(args.port)
    server.start()
    ip = local_ip()
    print("=" * 56)
    print(f"  Type this into the phone app:   {ip}")
    print(f"  (port {args.port}; phone and laptop on the SAME network)")
    print("=" * 56)

    ui = {"click": None, "calibrating": False}
    win = "Laptop server (preview)"
    if not args.headless:
        cv2.namedWindow(win)
        cv2.setMouseCallback(
            win, lambda ev, x, y, f, p: ui.update(click=(x, y))
            if ev == cv2.EVENT_LBUTTONDOWN and ui["calibrating"] else None)

    last_frame, last_status = None, None
    fps, last_t = 0.0, time.time()
    toast, toast_until = "", 0.0
    was_connected = False

    while not STOP.is_set():
        now = time.time()
        if server.connected and not was_connected:
            last_status = None          # re-send status to a fresh phone
        was_connected = server.connected

        for c in server.take_commands():
            pipe.apply_command(c, now)

        jpg = server.take_frame()
        if jpg is not None:
            frame, last_status = serve_once(server, pipe, jpg, now, last_status)
            if frame is not None:
                last_frame = frame
                fps = 0.9 * fps + 0.1 / max(now - last_t, 1e-3)
                last_t = now
        else:
            time.sleep(0.004)

        if ui["click"] is not None and last_frame is not None:
            ok = pipe.calibrate(last_frame, *ui["click"])
            toast = "Cloth colour set" if ok else "That spot is too grey - click a saturated colour"
            toast_until = now + 2.5
            ui["click"], ui["calibrating"] = None, False

        if args.headless:
            continue

        # ---- laptop preview (what the phone is sending, plus the AI's view)
        if last_frame is None:
            out = np.zeros((H, W, 3), np.uint8)
            ar.put(out, f"Waiting for the phone...  type {ip}", 12, 40, (0, 255, 255), 0.7)
        else:
            out = last_frame.copy()
            for pts in pipe.hands:
                for x, y in pts:
                    cv2.circle(out, (int(x), int(y)), 3, (0, 255, 0), -1)
            for p in pipe.people:
                x1, y1 = int(p.box[0]), int(p.box[1])
                label = (f"#{p.id} {p.stable or '-'} | {'VANISH' if p.vanish else 'visible'}"
                         f" | {EFFECTS[p.effect]}")
                ar.put(out, label, max(4, x1), max(18, y1 - 8),
                       (80, 255, 255) if p.vanish else (255, 255, 255), 0.5)
        state = "Phone: connected" if server.connected else "Phone: waiting"
        ar.put(out, f"{state}  {pipe.status[:44]}  {fps:.0f} fps", 12, 22,
               (0, 255, 0) if server.connected else (0, 165, 255), 0.5)
        if ui["calibrating"]:
            ar.put(out, "Click on the cloth...", 12, 46, (0, 255, 255), 0.7)
        elif now < toast_until:
            ar.put(out, toast, 12, 46, (0, 255, 255), 0.6)
        ar.put(out, "b bg | m mode | c cloth | e effect | space vanish | q quit",
               12, H - 12, (200, 200, 200), 0.45)
        cv2.imshow(win, out)
        k = cv2.waitKey(1) & 0xFF
        if k in (ord("q"), 27):
            break
        elif k == ord("b"):
            pipe.request_bg(time.time())
        elif k == ord("m"):
            pipe.toggle_mode()
        elif k == ord("c"):
            ui["calibrating"] = True
        elif k == ord("e"):
            pipe.next_effect()
        elif k == ord(" "):
            pipe.toggle_vanish()

    server.stop()
    cv2.destroyAllWindows()


if __name__ == "__main__":
    main()
