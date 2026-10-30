"""
invisibility_ar2.py - multi-person, gesture-controlled invisibility filter (IVRAR)

Every person is tracked separately and controls ONLY their own vanish state
with their own hand:
  open palm  -> that person vanishes
  fist       -> that person reappears
  peace sign -> next effect for that person

Keys (click the video window first):
  b      capture background (EVERYONE step out of frame, 3 s countdown)
  m      mode: Person  <->  Cloth colour
  c      calibrate cloth colour: press c, then CLICK on the cloth
  e      next effect for everyone
  space  toggle vanish for everyone
  q/esc  quit

Run:  python invisibility_ar2.py [camera_index_or_url]
Needs: pip install ultralytics   (multi-person). Without it, falls back to
single-person mode using MediaPipe.
"""
import collections
import sys
import time

import cv2
import numpy as np

WIN = "Invisibility AR v2"
W, H = 640, 480
EFFECTS = ["Shimmer", "Neon glow", "Hard cut"]
MODES = ["Person", "Cloth colour"]
GLOW_COLOR = np.array([255, 210, 40], np.float32)  # BGR neon cyan


# ---------------------------------------------------------------- gestures
def finger_states(pts):
    """pts: 21 (x, y) pixel points. Returns [index, middle, ring, pinky] extended.
    Orientation-independent: a finger is extended if its tip is farther from
    the wrist than its middle joint."""
    wx, wy = pts[0]

    def d(i):
        return float(np.hypot(pts[i][0] - wx, pts[i][1] - wy))

    return [d(t) > d(p) * 1.15 for t, p in zip((8, 12, 16, 20), (6, 10, 14, 18))]


def classify(up):
    i, m, r, p = up
    if i and m and r and p:
        return "OPEN"
    if not any(up):
        return "FIST"
    if i and m and not r and not p:
        return "PEACE"
    return None


# ------------------------------------------------------------------ people
class Person:
    _next = 1

    def __init__(self, box):
        self.id = Person._next
        Person._next += 1
        self.box = box
        self.missed = 0
        self.soft = None       # person mask 0..1
        self.soft_eff = None   # mask actually used for vanishing (mode dependent)
        self.vanish = False
        self.alpha = 0.0
        self.effect = 0
        self.hist = collections.deque(maxlen=6)
        self.last_stable = None
        self.stable = None

    def step(self, g):
        self.hist.append(g)
        stable = None
        if len(self.hist) == self.hist.maxlen:
            top = max(set(self.hist), key=self.hist.count)
            if top is not None and self.hist.count(top) >= 4:
                stable = top
        if stable and stable != self.last_stable:
            if stable == "OPEN":
                self.vanish = True
            elif stable == "FIST":
                self.vanish = False
            elif stable == "PEACE":
                self.effect = (self.effect + 1) % len(EFFECTS)
        self.last_stable = stable
        self.stable = stable
        target = 1.0 if self.vanish else 0.0
        if self.alpha < target:
            self.alpha = min(self.alpha + 0.06, target)
        else:
            self.alpha = max(self.alpha - 0.06, target)


def center(b):
    return ((b[0] + b[2]) / 2.0, (b[1] + b[3]) / 2.0)


def update_tracks(people, dets):
    """dets: list of (box, mask). Greedy nearest-centre matching."""
    n_old = len(people)
    pairs = sorted(
        (float(np.hypot(*np.subtract(center(p.box), center(d[0])))), pi, di)
        for pi, p in enumerate(people)
        for di, d in enumerate(dets)
    )
    used_p, used_d = set(), set()
    for dist, pi, di in pairs:
        if dist > 160:
            break
        if pi in used_p or di in used_d:
            continue
        used_p.add(pi)
        used_d.add(di)
        p = people[pi]
        box, mask = dets[di]
        p.box = box
        p.missed = 0
        p.soft = mask if p.soft is None else 0.5 * p.soft + 0.5 * mask
    for di, (box, mask) in enumerate(dets):
        if di not in used_d:
            q = Person(box)
            q.soft = mask
            people.append(q)
    for pi in range(n_old):
        if pi not in used_p:
            people[pi].missed += 1  # keep last mask for a few frames
    return [p for p in people if p.missed <= 8]


def assign_hand(people, wx, wy):
    """Which person does a hand (wrist at wx, wy) belong to?"""
    xi = int(np.clip(wx, 0, W - 1))
    yi = int(np.clip(wy, 0, H - 1))
    best, score = None, 0.3
    for p in people:
        if p.soft is not None and p.soft[yi, xi] > score:
            best, score = p, float(p.soft[yi, xi])
    if best is not None:
        return best
    bd = 1e9
    for p in people:  # fallback: hand just outside the mask (e.g. raised hand)
        x1, y1, x2, y2 = p.box
        if x1 - 50 <= wx <= x2 + 50 and y1 - 80 <= wy <= y2 + 50:
            d = np.hypot(wx - (x1 + x2) / 2, wy - (y1 + y2) / 2)
            if d < bd:
                best, bd = p, d
    return best


# --------------------------------------------------------- person finders
class YoloPeople:
    """Per-person instance masks (YOLOv8-seg)."""

    def __init__(self):
        from ultralytics import YOLO
        self.model = YOLO("yolov8n-seg.pt")  # downloads on first run

    def __call__(self, frame):
        r = self.model.predict(frame, imgsz=480, conf=0.4, classes=[0],
                               retina_masks=True, verbose=False)[0]
        dets = []
        if r.masks is None:
            return dets
        masks = r.masks.data.cpu().numpy()
        boxes = r.boxes.xyxy.cpu().numpy()
        for m, b in zip(masks, boxes):
            m = m.astype(np.float32)
            if m.shape != (H, W):
                m = cv2.resize(m, (W, H))
            m = np.clip((cv2.GaussianBlur(m, (0, 0), 2) - 0.3) / 0.4, 0.0, 1.0)
            dets.append((tuple(float(v) for v in b), m))
        return dets


class SelfieSinglePerson:
    """Fallback: one combined mask for everybody (MediaPipe)."""

    def __init__(self, mp):
        self.seg = mp.solutions.selfie_segmentation.SelfieSegmentation(model_selection=1)

    def __call__(self, frame):
        rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
        m = self.seg.process(rgb).segmentation_mask.astype(np.float32)
        m = np.clip((m - 0.4) / 0.2, 0.0, 1.0)
        ys, xs = np.where(m > 0.5)
        if len(xs) < 2000:
            return []
        return [((float(xs.min()), float(ys.min()), float(xs.max()), float(ys.max())), m)]


# ------------------------------------------------------------- cloth mode
DEFAULT_CLOTH = {"h": 0, "s": 120, "v": 70}  # red


def sample_cloth(frame, x, y, r=8):
    hsv = cv2.cvtColor(frame, cv2.COLOR_BGR2HSV)
    patch = hsv[max(0, y - r):y + r + 1, max(0, x - r):x + r + 1].reshape(-1, 3).astype(np.float32)
    ang = patch[:, 0] * 2 * np.pi / 180
    h = (np.degrees(np.arctan2(np.sin(ang).mean(), np.cos(ang).mean())) / 2) % 180
    s = float(np.median(patch[:, 1]))
    v = float(np.median(patch[:, 2]))
    if s < 40:
        return None  # too grey/white to key on
    return {"h": int(round(h)) % 180, "s": int(max(60, s * 0.55)), "v": int(max(40, v * 0.45))}


def cloth_mask(frame, c, tol=11):
    hsv = cv2.cvtColor(frame, cv2.COLOR_BGR2HSV)
    s0, v0 = int(c["s"]), int(c["v"])
    lo, hi = c["h"] - tol, c["h"] + tol
    if lo < 0:
        m = cv2.bitwise_or(cv2.inRange(hsv, (0, s0, v0), (hi, 255, 255)),
                           cv2.inRange(hsv, (180 + lo, s0, v0), (179, 255, 255)))
    elif hi > 179:
        m = cv2.bitwise_or(cv2.inRange(hsv, (lo, s0, v0), (179, 255, 255)),
                           cv2.inRange(hsv, (0, s0, v0), (hi - 180, 255, 255)))
    else:
        m = cv2.inRange(hsv, (lo, s0, v0), (hi, 255, 255))
    m = cv2.morphologyEx(m, cv2.MORPH_OPEN, np.ones((5, 5), np.uint8))
    m = cv2.morphologyEx(m, cv2.MORPH_CLOSE, np.ones((9, 9), np.uint8))
    n, lab, stats, _ = cv2.connectedComponentsWithStats(m)
    keep = np.zeros_like(m)
    for i in range(1, n):  # drop small specks (lips, stray pixels)
        if stats[i, cv2.CC_STAT_AREA] >= 1500:
            keep[lab == i] = 255
    keep = cv2.GaussianBlur(keep, (0, 0), 2)
    return keep.astype(np.float32) / 255.0


# ---------------------------------------------------------------- effects
_rng = np.random.default_rng(7)
_NX = cv2.resize(_rng.standard_normal((18, 24)).astype(np.float32), (W, H), interpolation=cv2.INTER_CUBIC)
_NY = cv2.resize(_rng.standard_normal((18, 24)).astype(np.float32), (W, H), interpolation=cv2.INTER_CUBIC)
_GX, _GY = np.meshgrid(np.arange(W, dtype=np.float32), np.arange(H, dtype=np.float32))


def edge_band(mask_soft):
    binary = (mask_soft > 0.5).astype(np.uint8)
    k = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (17, 17))
    band = cv2.dilate(binary, k).astype(np.float32) - cv2.erode(binary, k).astype(np.float32)
    band = cv2.GaussianBlur(band, (0, 0), 4)
    return np.clip(band * 1.6, 0.0, 1.0)


def ripple(img, amp_map, t):
    nx = np.roll(_NX, (int(t * 55) % H, int(t * 30) % W), axis=(0, 1))
    ny = np.roll(_NY, (int(t * 35) % H, int(t * 60) % W), axis=(0, 1))
    return cv2.remap(img, _GX + nx * amp_map, _GY + ny * amp_map,
                     cv2.INTER_LINEAR, borderMode=cv2.BORDER_REFLECT)


def glow(img, strength_map, t):
    pulse = 0.7 + 0.3 * np.sin(t * 7.0)
    add = np.clip(strength_map, 0, 1.6)[..., None] * GLOW_COLOR * pulse
    return np.clip(img.astype(np.float32) + add, 0, 255).astype(np.uint8)


def render(frame, bg, people, t):
    out = frame.astype(np.float32)
    bgf = bg.astype(np.float32)
    amp_map = np.zeros((H, W), np.float32)
    glow_map = np.zeros((H, W), np.float32)
    for p in people:
        soft = p.soft_eff
        if soft is None:
            continue
        eff = EFFECTS[p.effect]
        m = (soft > 0.5).astype(np.float32) if eff == "Hard cut" else soft
        m = (m * p.alpha)[..., None]
        out = out * (1 - m) + bgf * m
        if eff == "Hard cut":
            continue
        band = edge_band(soft)
        vis = min(1.0, p.alpha * 4.0)
        ts = 4.0 * p.alpha * (1.0 - p.alpha)  # strongest mid-transition
        if eff == "Shimmer":
            amp_map += band * (2.0 + 2.0 * vis + 14.0 * ts)
        else:  # Neon glow: always visible outline, so it never "doesn't show"
            glow_map += band * (0.45 + 0.5 * vis + 0.8 * ts)
            amp_map += band * (1.5 + 4.0 * ts)
    out = np.clip(out, 0, 255).astype(np.uint8)
    if glow_map.any():
        out = glow(out, glow_map, t)
    if amp_map.any():
        out = ripple(out, amp_map, t)
    return out


# --------------------------------------------------------------------- ui
def put(img, text, x, y, color=(255, 255, 255), scale=0.55):
    cv2.putText(img, text, (x, y), cv2.FONT_HERSHEY_SIMPLEX, scale, (0, 0, 0), 3, cv2.LINE_AA)
    cv2.putText(img, text, (x, y), cv2.FONT_HERSHEY_SIMPLEX, scale, color, 1, cv2.LINE_AA)


def grab_background(cap, n=30):
    frames = []
    while len(frames) < n:
        ok, f = cap.read()
        if ok:
            frames.append(cv2.flip(cv2.resize(f, (W, H)), 1).astype(np.float32))
    return np.mean(frames, axis=0).astype(np.uint8)


def main():
    import mediapipe as mp

    src = sys.argv[1] if len(sys.argv) > 1 else "0"
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
        finder = YoloPeople()
        multi = True
    except Exception as e:  # not installed / no internet for the model download
        print(f"[!] Multi-person mode unavailable ({e}).\n"
              "    Run: pip install ultralytics   -> falling back to single-person mode.")
        finder = SelfieSinglePerson(mp)
        multi = False

    ui = {"click": None, "calibrating": False}

    def on_mouse(event, x, y, flags, param):
        if event == cv2.EVENT_LBUTTONDOWN and ui["calibrating"]:
            ui["click"] = (x, y)

    cv2.namedWindow(WIN)
    cv2.setMouseCallback(WIN, on_mouse)

    people = []
    bg = None
    countdown_until = None
    mode = 0
    cloth = dict(DEFAULT_CLOTH)
    toast, toast_until = "", 0.0
    fps, last = 0.0, time.time()
    t0 = time.time()

    def show(img):
        cv2.imshow(WIN, img)
        return cv2.waitKey(1) & 0xFF

    while True:
        ok, frame = cap.read()
        if not ok:
            continue
        frame = cv2.flip(cv2.resize(frame, (W, H)), 1)
        now = time.time()
        t = now - t0
        fps = 0.9 * fps + 0.1 / max(now - last, 1e-3)
        last = now

        # ---- background capture
        if countdown_until is not None:
            left = countdown_until - now
            if left > 0:
                out = frame.copy()
                put(out, f"Everyone step out of frame... {int(left) + 1}", 12, 40, (0, 255, 255), 0.8)
                if show(out) in (ord("q"), 27):
                    break
                continue
            bg = grab_background(cap)
            countdown_until = None
            people = []
        if bg is None:
            out = frame.copy()
            put(out, "Press B to capture background (everyone out of frame)", 12, 40, (0, 255, 255), 0.65)
            k = show(out)
            if k in (ord("q"), 27):
                break
            if k == ord("b"):
                countdown_until = time.time() + 3
            continue

        # ---- people (per-person masks) and hands (per-person gestures)
        people = update_tracks(people, finder(frame))
        res = hands.process(cv2.cvtColor(frame, cv2.COLOR_BGR2RGB))
        hand_lms = res.multi_hand_landmarks or []
        gest = {}
        for hl in hand_lms:
            pts = [(l.x * W, l.y * H) for l in hl.landmark]
            g = classify(finger_states(pts))
            owner = assign_hand(people, *pts[0])
            if owner is not None and g is not None:
                gest[owner.id] = g
        for p in people:
            p.step(gest.get(p.id))

        # ---- cloth calibration
        if ui["click"] is not None:
            c = sample_cloth(frame, *ui["click"])
            if c is None:
                toast = "That spot is too grey - click a saturated colour"
            else:
                cloth = c
                mode = 1
                toast = "Cloth colour set"
            toast_until = now + 2.5
            ui["click"] = None
            ui["calibrating"] = False

        # ---- which pixels vanish for each person
        if mode == 1:
            cm = cloth_mask(frame, cloth)
            for p in people:
                p.soft_eff = cm * (p.soft > 0.5).astype(np.float32)  # cloth AND this person
        else:
            for p in people:
                p.soft_eff = p.soft

        out = render(frame, bg, people, t)

        # ---- HUD
        for hl in hand_lms:
            draw.draw_landmarks(out, hl, conns)
        for p in people:
            x1, y1 = int(p.box[0]), int(p.box[1])
            label = f"#{p.id} {p.stable or '-'} | {'VANISHED' if p.vanish else 'visible'} | {EFFECTS[p.effect]}"
            put(out, label, max(4, x1), max(18, y1 - 8),
                (80, 255, 255) if p.vanish else (255, 255, 255), 0.5)
        put(out, f"Mode: {MODES[mode]}  People: {len(people)}  {fps:.0f} fps"
            + ("" if multi else "  (single-person fallback)"), 12, 22)
        if ui["calibrating"]:
            put(out, "Click on the cloth...", 12, 46, (0, 255, 255), 0.7)
        elif now < toast_until:
            put(out, toast, 12, 46, (0, 255, 255), 0.6)
        put(out, "b bg | m mode | c cloth colour | e effect | space vanish | q quit",
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

    cap.release()
    cv2.destroyAllWindows()


if __name__ == "__main__":
    main()
