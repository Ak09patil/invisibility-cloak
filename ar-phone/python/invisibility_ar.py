"""
invisibility_ar.py - gesture-controlled invisibility filter (IVRAR)

Keys (click the video window first):
  b      capture background (step OUT of the frame, 3s countdown)
  m      switch mode: Person segmentation  <->  Red cloth (HSV)
  e      cycle effect: Shimmer / Neon glow / Hard cut
  space  toggle vanish manually
  q/esc  quit

Gestures:
  open palm  -> vanish
  fist       -> reappear
  peace sign -> next effect

Keep the camera still after capturing the background.
"""
import collections
import time

import cv2
import mediapipe as mp
import numpy as np

WIN = "Invisibility AR"
W, H = 640, 480
EFFECTS = ["Shimmer", "Neon glow", "Hard cut"]
MODES = ["Person segmentation", "Red cloth (HSV)"]

mp_hands = mp.solutions.hands
mp_seg = mp.solutions.selfie_segmentation
mp_draw = mp.solutions.drawing_utils


# ---------------------------------------------------------------- gestures
def finger_states(lm):
    """Returns [thumb, index, middle, ring, pinky] extended flags."""
    tips = [8, 12, 16, 20]
    pips = [6, 10, 14, 18]
    up = [lm[t].y < lm[p].y for t, p in zip(tips, pips)]
    px, py = lm[17].x, lm[17].y
    d_tip = np.hypot(lm[4].x - px, lm[4].y - py)
    d_ip = np.hypot(lm[3].x - px, lm[3].y - py)
    thumb = d_tip > d_ip * 1.1
    return [thumb] + up


def classify(fingers):
    _, i, m, r, p = fingers
    if i and m and r and p:
        return "OPEN"
    if not (i or m or r or p):
        return "FIST"
    if i and m and not r and not p:
        return "PEACE"
    return None


# ------------------------------------------------------------------ masks
def segmentation_mask(rgb, seg, prev):
    m = seg.process(rgb).segmentation_mask.astype(np.float32)
    if prev is not None:
        m = 0.6 * prev + 0.4 * m  # temporal smoothing, kills flicker
    return m


def soft_threshold(m):
    return np.clip((m - 0.4) / 0.2, 0.0, 1.0)


def red_cloth_mask(frame):
    hsv = cv2.cvtColor(frame, cv2.COLOR_BGR2HSV)
    m1 = cv2.inRange(hsv, (0, 120, 70), (10, 255, 255))
    m2 = cv2.inRange(hsv, (170, 120, 70), (180, 255, 255))
    m = cv2.bitwise_or(m1, m2)
    k = np.ones((5, 5), np.uint8)
    m = cv2.morphologyEx(m, cv2.MORPH_OPEN, k, iterations=2)
    m = cv2.dilate(m, k, iterations=2)
    m = cv2.GaussianBlur(m, (0, 0), 3)
    return m.astype(np.float32) / 255.0


def edge_band(mask_soft):
    binary = (mask_soft > 0.5).astype(np.uint8)
    k = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (17, 17))
    band = cv2.dilate(binary, k).astype(np.float32) - cv2.erode(binary, k).astype(np.float32)
    band = cv2.GaussianBlur(band, (0, 0), 4)
    return np.clip(band * 1.6, 0.0, 1.0)


# ---------------------------------------------------------------- effects
_rng = np.random.default_rng(7)
_NX = cv2.resize(_rng.standard_normal((18, 24)).astype(np.float32), (W, H), interpolation=cv2.INTER_CUBIC)
_NY = cv2.resize(_rng.standard_normal((18, 24)).astype(np.float32), (W, H), interpolation=cv2.INTER_CUBIC)
_GX, _GY = np.meshgrid(np.arange(W, dtype=np.float32), np.arange(H, dtype=np.float32))


def ripple(img, band, t, amp):
    nx = np.roll(_NX, (int(t * 55) % H, int(t * 30) % W), axis=(0, 1))
    ny = np.roll(_NY, (int(t * 35) % H, int(t * 60) % W), axis=(0, 1))
    mx = _GX + nx * amp * band
    my = _GY + ny * amp * band
    return cv2.remap(img, mx, my, cv2.INTER_LINEAR, borderMode=cv2.BORDER_REFLECT)


def glow(img, band, t, strength):
    color = np.array([255, 210, 40], np.float32)  # BGR cyan-ish neon
    pulse = 0.65 + 0.35 * np.sin(t * 7.0)
    add = band[..., None] * color * strength * pulse
    return np.clip(img.astype(np.float32) + add, 0, 255).astype(np.uint8)


# ------------------------------------------------------------------ main
def grab_background(cap, n=30):
    frames = []
    while len(frames) < n:
        ok, f = cap.read()
        if ok:
            frames.append(cv2.flip(cv2.resize(f, (W, H)), 1).astype(np.float32))
    return np.mean(frames, axis=0).astype(np.uint8)


def put(img, text, y, color=(255, 255, 255), scale=0.6):
    cv2.putText(img, text, (12, y), cv2.FONT_HERSHEY_SIMPLEX, scale, (0, 0, 0), 3, cv2.LINE_AA)
    cv2.putText(img, text, (12, y), cv2.FONT_HERSHEY_SIMPLEX, scale, color, 1, cv2.LINE_AA)


def main():
    cap = cv2.VideoCapture(0)
    if not cap.isOpened():
        raise SystemExit("Could not open camera (check macOS camera permission for Terminal).")
    cap.set(cv2.CAP_PROP_FRAME_WIDTH, W)
    cap.set(cv2.CAP_PROP_FRAME_HEIGHT, H)

    hands = mp_hands.Hands(max_num_hands=1, min_detection_confidence=0.6, min_tracking_confidence=0.6)
    seg = mp_seg.SelfieSegmentation(model_selection=1)

    bg = None
    countdown_until = None
    mode = 0
    effect = 0
    vanish = False
    alpha = 0.0
    prev_mask = None
    hist = collections.deque(maxlen=5)
    last_stable = None
    t0 = time.time()

    while True:
        ok, frame = cap.read()
        if not ok:
            continue
        frame = cv2.flip(cv2.resize(frame, (W, H)), 1)
        rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
        now = time.time()
        t = now - t0

        # ---- gestures
        res = hands.process(rgb)
        g = None
        if res.multi_hand_landmarks:
            hl = res.multi_hand_landmarks[0]
            g = classify(finger_states(hl.landmark))
            mp_draw.draw_landmarks(frame, hl, mp_hands.HAND_CONNECTIONS)
        hist.append(g)
        stable = None
        if len(hist) == hist.maxlen:
            top = max(set(hist), key=hist.count)
            if top is not None and hist.count(top) >= 3:
                stable = top
        if stable and stable != last_stable:
            if stable == "OPEN":
                vanish = True
            elif stable == "FIST":
                vanish = False
            elif stable == "PEACE":
                effect = (effect + 1) % len(EFFECTS)
        last_stable = stable

        # ---- background capture
        if countdown_until is not None:
            left = countdown_until - now
            if left > 0:
                out = frame.copy()
                put(out, f"Step out of frame... {int(left) + 1}", 40, (0, 255, 255), 0.9)
                cv2.imshow(WIN, out)
                if cv2.waitKey(1) & 0xFF in (ord("q"), 27):
                    break
                continue
            bg = grab_background(cap)
            countdown_until = None
            prev_mask = None

        if bg is None:
            out = frame.copy()
            put(out, "Press B to capture background (step out of frame)", 40, (0, 255, 255), 0.7)
            cv2.imshow(WIN, out)
            k = cv2.waitKey(1) & 0xFF
            if k in (ord("q"), 27):
                break
            if k == ord("b"):
                countdown_until = time.time() + 3
            continue

        # ---- vanish amount eases toward target
        target = 1.0 if vanish else 0.0
        step = 0.06
        alpha = min(alpha + step, target) if alpha < target else max(alpha - step, target)

        # ---- mask
        if mode == 0:
            prev_mask = segmentation_mask(rgb, seg, prev_mask)
            soft = soft_threshold(prev_mask)
        else:
            soft = red_cloth_mask(frame)

        band = edge_band(soft)
        ts = 4.0 * alpha * (1.0 - alpha)  # 0 at rest, 1 mid-transition
        band_eff = band * min(1.0, alpha * 4.0)

        m = soft if EFFECTS[effect] != "Hard cut" else (soft > 0.5).astype(np.float32)
        m = (m * alpha)[..., None]
        out = (frame.astype(np.float32) * (1 - m) + bg.astype(np.float32) * m).astype(np.uint8)

        if EFFECTS[effect] == "Shimmer":
            out = ripple(out, band_eff, t, amp=4.0 + 14.0 * ts)
        elif EFFECTS[effect] == "Neon glow":
            out = glow(out, band_eff, t, strength=0.7 + 0.8 * ts)
            out = ripple(out, band_eff, t, amp=2.0 + 4.0 * ts)

        # ---- HUD
        put(out, f"Mode: {MODES[mode]}   Effect: {EFFECTS[effect]}", 24)
        put(out, f"Gesture: {stable or '-'}   {'VANISHED' if vanish else 'visible'}", 48)
        put(out, "b bg | m mode | e effect | space vanish | q quit", H - 12, (200, 200, 200), 0.5)

        cv2.imshow(WIN, out)
        k = cv2.waitKey(1) & 0xFF
        if k in (ord("q"), 27):
            break
        elif k == ord("b"):
            countdown_until = time.time() + 3
        elif k == ord("m"):
            mode = (mode + 1) % len(MODES)
            prev_mask = None
        elif k == ord("e"):
            effect = (effect + 1) % len(EFFECTS)
        elif k == ord(" "):
            vanish = not vanish

    cap.release()
    cv2.destroyAllWindows()


if __name__ == "__main__":
    main()
