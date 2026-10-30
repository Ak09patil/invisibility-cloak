"""
HSV Calibration Tool
---------------------
Helps you find the exact lower/upper HSV bounds for YOUR cloak fabric
under YOUR lighting, instead of guessing. Hold the cloak in front of the
camera and adjust the sliders until only the cloak shows up white in the
mask window (everything else black).

Usage:
    python calibrate.py
    python calibrate.py --camera 1

Press 'p' to print the current HSV range (paste it into COLOR_RANGES in
invisibility_cloak.py). Press 'q' to quit.
"""

import argparse
import cv2
import numpy as np


def nothing(_):
    pass


def main():
    parser = argparse.ArgumentParser(description="HSV calibration tool")
    parser.add_argument("--camera", type=int, default=0)
    args = parser.parse_args()

    cap = cv2.VideoCapture(args.camera)
    if not cap.isOpened():
        raise RuntimeError(f"Could not open camera index {args.camera}")

    cv2.namedWindow("Trackbars")
    cv2.createTrackbar("H min", "Trackbars", 0, 180, nothing)
    cv2.createTrackbar("H max", "Trackbars", 180, 180, nothing)
    cv2.createTrackbar("S min", "Trackbars", 80, 255, nothing)
    cv2.createTrackbar("S max", "Trackbars", 255, 255, nothing)
    cv2.createTrackbar("V min", "Trackbars", 60, 255, nothing)
    cv2.createTrackbar("V max", "Trackbars", 255, 255, nothing)

    print("Adjust sliders so only the cloak is white in the Mask window.")
    print("Press 'p' to print the current range, 'q' to quit.")

    while True:
        ret, frame = cap.read()
        if not ret:
            break
        frame = cv2.flip(frame, 1)
        hsv = cv2.cvtColor(frame, cv2.COLOR_BGR2HSV)

        h_min = cv2.getTrackbarPos("H min", "Trackbars")
        h_max = cv2.getTrackbarPos("H max", "Trackbars")
        s_min = cv2.getTrackbarPos("S min", "Trackbars")
        s_max = cv2.getTrackbarPos("S max", "Trackbars")
        v_min = cv2.getTrackbarPos("V min", "Trackbars")
        v_max = cv2.getTrackbarPos("V max", "Trackbars")

        lower = np.array([h_min, s_min, v_min])
        upper = np.array([h_max, s_max, v_max])
        mask = cv2.inRange(hsv, lower, upper)
        result = cv2.bitwise_and(frame, frame, mask=mask)

        cv2.imshow("Live", frame)
        cv2.imshow("Mask", mask)
        cv2.imshow("Result", result)

        key = cv2.waitKey(1) & 0xFF
        if key == ord('q'):
            break
        elif key == ord('p'):
            print(f"lower = ({h_min}, {s_min}, {v_min})   upper = ({h_max}, {s_max}, {v_max})")

    cap.release()
    cv2.destroyAllWindows()


if __name__ == "__main__":
    main()
