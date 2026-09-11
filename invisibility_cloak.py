"""
Real-Time Invisibility Cloak
-----------------------------
Uses HSV color masking + background substitution to make a solid-colored
cloak visually "disappear" in a live webcam feed.

Usage:
    python invisibility_cloak.py
    python invisibility_cloak.py --color green
    python invisibility_cloak.py --color red --camera 1

Controls:
    b  -> re-capture the background (do this once you step OUT of frame)
    q  -> quit
"""

import argparse
import time
import cv2
import numpy as np

# ---------------------------------------------------------------------------
# HSV ranges for common cloak colors. Tune these if detection is poor —
# use calibrate.py (included separately) to find the right values for your
# exact cloth and lighting.
# ---------------------------------------------------------------------------
COLOR_RANGES = {
    "red": [
        # red wraps around the HSV hue circle, so it needs two ranges
        ((0, 120, 70), (10, 255, 255)),
        ((170, 120, 70), (180, 255, 255)),
    ],
    "green": [
        ((36, 80, 60), (86, 255, 255)),
    ],
    "blue": [
        ((94, 80, 40), (126, 255, 255)),
    ],
}


def capture_background(cap, num_frames=40):
    """Average several frames to get a clean, noise-free background plate."""
    frames = []
    for _ in range(num_frames):
        ret, frame = cap.read()
        if ret:
            frames.append(frame.astype(np.float32))
        time.sleep(0.01)
    if not frames:
        raise RuntimeError("Could not read from camera to capture background.")
    background = np.mean(frames, axis=0).astype(np.uint8)
    return background


def build_mask(hsv_frame, color_ranges):
    """Threshold the frame in HSV space and clean up the resulting mask."""
    mask = np.zeros(hsv_frame.shape[:2], dtype=np.uint8)
    for lower, upper in color_ranges:
        mask |= cv2.inRange(hsv_frame, np.array(lower), np.array(upper))

    # Morphological cleanup: remove speckle noise, then smooth/fill edges
    kernel = np.ones((3, 3), np.uint8)
    mask = cv2.morphologyEx(mask, cv2.MORPH_OPEN, kernel, iterations=2)
    mask = cv2.morphologyEx(mask, cv2.MORPH_DILATE, kernel, iterations=1)
    mask = cv2.GaussianBlur(mask, (5, 5), 0)
    _, mask = cv2.threshold(mask, 127, 255, cv2.THRESH_BINARY)
    return mask


def main():
    parser = argparse.ArgumentParser(description="Real-time invisibility cloak")
    parser.add_argument("--color", choices=COLOR_RANGES.keys(), default="red",
                         help="Cloak color to detect (default: red)")
    parser.add_argument("--camera", type=int, default=0,
                         help="Camera index (default: 0)")
    args = parser.parse_args()

    cap = cv2.VideoCapture(args.camera)
    if not cap.isOpened():
        raise RuntimeError(f"Could not open camera index {args.camera}")

    print("Step out of frame... capturing background in 2 seconds.")
    time.sleep(2)
    background = capture_background(cap)
    print("Background captured. Step into frame with your cloak. Press 'q' to quit, 'b' to recapture background.")

    color_ranges = COLOR_RANGES[args.color]

    while True:
        ret, frame = cap.read()
        if not ret:
            print("Frame grab failed, exiting.")
            break

        frame = cv2.flip(frame, 1)

        hsv = cv2.cvtColor(frame, cv2.COLOR_BGR2HSV)
        mask = build_mask(hsv, color_ranges)
        mask_inv = cv2.bitwise_not(mask)

        # Cloak area -> pull from background; rest -> keep live frame
        cloak_area = cv2.bitwise_and(background, background, mask=mask)
        visible_area = cv2.bitwise_and(frame, frame, mask=mask_inv)
        output = cv2.addWeighted(cloak_area, 1, visible_area, 1, 0)

        cv2.imshow("Invisibility Cloak (press q to quit, b to recapture bg)", output)

        key = cv2.waitKey(1) & 0xFF
        if key == ord('q'):
            break
        elif key == ord('b'):
            print("Step out of frame... recapturing background in 2 seconds.")
            time.sleep(2)
            background = capture_background(cap)
            print("Background recaptured.")

    cap.release()
    cv2.destroyAllWindows()


if __name__ == "__main__":
    main()
