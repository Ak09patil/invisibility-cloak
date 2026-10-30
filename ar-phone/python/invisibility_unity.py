"""
invisibility_unity.py - perception half of the Unity invisibility AR system

Python does the vision (multi-person segmentation, per-person hand gestures,
cloth keying) and STREAMS the results to Unity over TCP.  Unity does the
rendering: shader compositing, dissolve / shimmer / neon-glow, particles.

Streamed every frame:
  - camera frame (JPEG)
  - person masks: one PNG, RGBA = slots 0..3 (one person per channel)
  - JSON: per slot -> person id, vanish state, effect, gesture, outline points
Streamed once (and again whenever Unity reconnects):
  - clean background (JPEG)

Keys (click the Python preview window first):
  b      capture background (EVERYONE out of frame, 3 s countdown)
  m      mode: Person  <->  Cloth colour
  c      calibrate cloth colour: press c, then CLICK on the cloth
  e      next effect for everyone
  space  toggle vanish for everyone
  q/esc  quit

Needs invisibility_ar2.py in the same folder.
Run:  python invisibility_unity.py [camera_index_or_url] [unity_host]
"""
import json
import socket
import struct
import sys
import time

import cv2
import numpy as np

import invisibility_ar2 as ar
from invisibility_ar2 import EFFECTS, H, MODES, W

PORT = 5055
MASK_W, MASK_H = 320, 240
MAX_SLOTS = 4


# ---------------------------------------------------------------- network
class UnitySender:
    def __init__(self, host="127.0.0.1", port=PORT):
        self.addr = (host, port)
        self.sock = None
        self.last_try = 0.0
        self.need_bg = True

    @property
    def connected(self):
        return self.sock is not None

    def poll(self):
        """Try to (re)connect about once a second."""
        if self.sock is not None:
            return
        now = time.time()
        if now - self.last_try < 1.0:
            return
        self.last_try = now
        try:
            s = socket.create_connection(self.addr, timeout=0.25)
            s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            s.settimeout(1.0)
            self.sock = s
            self.need_bg = True  # a fresh Unity needs the background again
        except OSError:
            self.sock = None

    def close(self):
        if self.sock is not None:
            try:
                self.sock.close()
            except OSError:
                pass
        self.sock = None

    def _send(self, payload):
        if self.sock is None:
            return False
        try:
            self.sock.sendall(struct.pack("<I", len(payload)) + payload)
            return True
        except OSError:
            self.close()
            return False

    def send_bg(self, bg):
        ok, jpg = cv2.imencode(".jpg", bg, [cv2.IMWRITE_JPEG_QUALITY, 90])
        if ok and self._send(b"\x01" + jpg.tobytes()):
            self.need_bg = False

    def send_frame(self, frame, png, meta_json):
        ok, jpg = cv2.imencode(".jpg", frame, [cv2.IMWRITE_JPEG_QUALITY, 80])
        if not ok:
            return
        jpg = jpg.tobytes()
        payload = (b"\x02"
                   + struct.pack("<I", len(jpg)) + jpg
                   + struct.pack("<I", len(png)) + png
                   + struct.pack("<I", len(meta_json)) + meta_json)
        self._send(payload)


# ------------------------------------------------------------ packet data
def assign_slots(people, slots):
    """Each tracked person gets a fixed slot 0..3 (= mask channel) while tracked."""
    ids = {p.id for p in people}
    for s in range(MAX_SLOTS):
        if slots[s] not in ids:
            slots[s] = None
    for p in people:
        if p.id not in slots:
            for s in range(MAX_SLOTS):
                if slots[s] is None:
                    slots[s] = p.id
                    break


def contour_points(mask01, n=48):
    """Outline of a 0..1 mask as <= n evenly spaced normalised (x, y) points."""
    binary = (mask01 > 0.5).astype(np.uint8) * 255
    cs, _ = cv2.findContours(binary, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
    if not cs:
        return []
    c = max(cs, key=cv2.contourArea)
    if len(c) < 8:
        return []
    idx = np.linspace(0, len(c) - 1, min(n, len(c))).astype(int)
    pts = c[idx, 0, :]
    return [round(float(v), 3) for x, y in pts for v in (x / MASK_W, y / MASK_H)]


def build_packet_data(people, slots):
    rgba = np.zeros((MASK_H, MASK_W, 4), np.uint8)
    meta = []
    for p in people:
        if p.id not in slots or p.soft_eff is None:
            continue
        s = slots.index(p.id)
        small = cv2.resize(p.soft_eff, (MASK_W, MASK_H), interpolation=cv2.INTER_AREA)
        rgba[..., s] = np.clip(small * 255, 0, 255).astype(np.uint8)
        meta.append({
            "slot": s,
            "id": int(p.id),
            "vanish": bool(p.vanish),
            "effect": int(p.effect),
            "gesture": p.stable or "",
            "contour": contour_points(small),
        })
    ok, png = cv2.imencode(".png", cv2.cvtColor(rgba, cv2.COLOR_RGBA2BGRA),
                           [cv2.IMWRITE_PNG_COMPRESSION, 1])
    return png.tobytes(), json.dumps({"people": meta}).encode("utf-8")


# --------------------------------------------------------------------- main
def main():
    import mediapipe as mp

    src = sys.argv[1] if len(sys.argv) > 1 else "0"
    host = sys.argv[2] if len(sys.argv) > 2 else "127.0.0.1"
    cap = cv2.VideoCapture(int(src) if src.isdigit() else src)
    if not cap.isOpened():
        raise SystemExit("Could not open camera (check macOS camera permission for Terminal).")
    cap.set(cv2.CAP_PROP_FRAME_WIDTH, W)
    cap.set(cv2.CAP_PROP_FRAME_HEIGHT, H)

    hands = mp.solutions.hands.Hands(max_num_hands=6, min_detection_confidence=0.5,
                                     min_tracking_confidence=0.5)
    draw = mp.solutions.drawing_utils
    conns = mp.solutions.hands.HAND_CONNECTIONS

    try:
        finder = ar.YoloPeople()
        multi = True
    except Exception as e:
        print(f"[!] Multi-person mode unavailable ({e}).\n"
              "    Run: pip install ultralytics   -> falling back to single-person mode.")
        finder = ar.SelfieSinglePerson(mp)
        multi = False

    sender = UnitySender(host)
    slots = [None] * MAX_SLOTS

    ui = {"click": None, "calibrating": False}

    def on_mouse(event, x, y, flags, param):
        if event == cv2.EVENT_LBUTTONDOWN and ui["calibrating"]:
            ui["click"] = (x, y)

    win = "Python perception (preview)"
    cv2.namedWindow(win)
    cv2.setMouseCallback(win, on_mouse)

    people = []
    bg = None
    countdown_until = None
    mode = 0
    cloth = dict(ar.DEFAULT_CLOTH)
    toast, toast_until = "", 0.0
    fps, last = 0.0, time.time()

    def show(img):
        cv2.imshow(win, img)
        return cv2.waitKey(1) & 0xFF

    while True:
        ok, frame = cap.read()
        if not ok:
            continue
        frame = cv2.flip(cv2.resize(frame, (W, H)), 1)
        now = time.time()
        fps = 0.9 * fps + 0.1 / max(now - last, 1e-3)
        last = now
        sender.poll()

        # ---- background capture
        if countdown_until is not None:
            left = countdown_until - now
            if left > 0:
                out = frame.copy()
                ar.put(out, f"Everyone step out of frame... {int(left) + 1}", 12, 40, (0, 255, 255), 0.8)
                if show(out) in (ord("q"), 27):
                    break
                continue
            bg = ar.grab_background(cap)
            countdown_until = None
            people = []
            slots = [None] * MAX_SLOTS
            sender.need_bg = True
        if bg is None:
            out = frame.copy()
            ar.put(out, "Press B to capture background (everyone out of frame)", 12, 40, (0, 255, 255), 0.65)
            k = show(out)
            if k in (ord("q"), 27):
                break
            if k == ord("b"):
                countdown_until = time.time() + 3
            continue

        # ---- people + per-person gestures
        people = ar.update_tracks(people, finder(frame))
        assign_slots(people, slots)
        res = hands.process(cv2.cvtColor(frame, cv2.COLOR_BGR2RGB))
        hand_lms = res.multi_hand_landmarks or []
        gest = {}
        for hl in hand_lms:
            pts = [(l.x * W, l.y * H) for l in hl.landmark]
            g = ar.classify(ar.finger_states(pts))
            owner = ar.assign_hand(people, *pts[0])
            if owner is not None and g is not None:
                gest[owner.id] = g
        for p in people:
            p.step(gest.get(p.id))

        # ---- cloth calibration
        if ui["click"] is not None:
            c = ar.sample_cloth(frame, *ui["click"])
            if c is None:
                toast = "That spot is too grey - click a saturated colour"
            else:
                cloth = c
                mode = 1
                toast = "Cloth colour set"
            toast_until = now + 2.5
            ui["click"] = None
            ui["calibrating"] = False

        # ---- mask used for vanishing
        if mode == 1:
            cm = ar.cloth_mask(frame, cloth)
            for p in people:
                p.soft_eff = cm * (p.soft > 0.5).astype(np.float32)
        else:
            for p in people:
                p.soft_eff = p.soft

        # ---- stream to Unity
        if sender.connected:
            if sender.need_bg:
                sender.send_bg(bg)
            png, meta = build_packet_data(people, slots)
            sender.send_frame(frame, png, meta)

        # ---- preview HUD (the composited result is in Unity)
        out = frame.copy()
        for hl in hand_lms:
            draw.draw_landmarks(out, hl, conns)
        for p in people:
            x1, y1 = int(p.box[0]), int(p.box[1])
            slot = slots.index(p.id) if p.id in slots else -1
            label = f"#{p.id} {p.stable or '-'} | {'VANISH' if p.vanish else 'visible'} | {EFFECTS[p.effect]}"
            if slot < 0:
                label += " | (no slot)"
            ar.put(out, label, max(4, x1), max(18, y1 - 8),
                   (80, 255, 255) if p.vanish else (255, 255, 255), 0.5)
        status = "Unity: connected" if sender.connected else "Unity: waiting (press Play)"
        ar.put(out, f"{status}  Mode: {MODES[mode]}  People: {len(people)}  {fps:.0f} fps"
               + ("" if multi else "  (single-person fallback)"), 12, 22,
               (0, 255, 0) if sender.connected else (0, 165, 255))
        if ui["calibrating"]:
            ar.put(out, "Click on the cloth...", 12, 46, (0, 255, 255), 0.7)
        elif now < toast_until:
            ar.put(out, toast, 12, 46, (0, 255, 255), 0.6)
        ar.put(out, "b bg | m mode | c cloth colour | e effect | space vanish | q quit",
               12, H - 12, (200, 200, 200), 0.45)

        k = show(out)
        if k in (ord("q"), 27):
            break
        elif k == ord("b"):
            countdown_until = time.time() + 3
        elif k == ord("m"):
            mode = (mode + 1) % len(MODES)
        elif k == ord("c"):
            ui["calibrating"] = True
        elif k == ord("e"):
            for p in people:
                p.effect = (p.effect + 1) % len(EFFECTS)
        elif k == ord(" "):
            anyone = any(not p.vanish for p in people)
            for p in people:
                p.vanish = anyone

    sender.close()
    cap.release()
    cv2.destroyAllWindows()


if __name__ == "__main__":
    main()
