"""Decode a styled QR the way a phone would: full size, shrunk, blurred, and printed-and-photographed-ish."""
import sys
import cv2
import numpy as np

path = sys.argv[1]
expect = sys.argv[2]
img = cv2.imread(path)
det = cv2.QRCodeDetector()

cases = {"full": img}
for w in (800, 480, 320, 240):
    cases[f"shrink{w}"] = cv2.resize(img, (w, w), interpolation=cv2.INTER_AREA)
b = cv2.resize(img, (480, 480), interpolation=cv2.INTER_AREA)
cases["blur480"] = cv2.GaussianBlur(b, (5, 5), 1.2)
# tilt + warm paper tint, like a photo of a printed card
h, w = b.shape[:2]
src = np.float32([[0, 0], [w, 0], [w, h], [0, h]])
dst = np.float32([[30, 20], [w - 10, 0], [w - 25, h - 15], [15, h - 30]])
M = cv2.getPerspectiveTransform(src, dst)
warped = cv2.warpPerspective(b, M, (w, h), borderValue=(230, 236, 240))
warped = (warped.astype(np.float32) * np.array([0.92, 0.97, 1.0])).clip(0, 255).astype(np.uint8)
cases["photo480"] = warped

ok_all = True
for name, im in cases.items():
    data, pts, _ = det.detectAndDecode(im)
    ok = data == expect
    ok_all &= ok
    print(f"{name:10s} {'OK ' if ok else 'FAIL'} {data[:60]!r}")
sys.exit(0 if ok_all else 1)
