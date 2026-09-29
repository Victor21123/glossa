"""End-to-end check of a running Glossa: finds words on screen, points at them, presses the hotkey and
screenshots the card.

Usage: .venv\\Scripts\\python.exe tools\\e2e_lookup.py OUT_DIR word1 word2 ...
The words must be visible on the primary monitor. Glossa must be running with Popup.HideFromCapture=false.
"""
import ctypes
import os
import sys
import time
from pathlib import Path

import dxcam
from PIL import Image
from rapidocr import RapidOCR

user32 = ctypes.windll.user32
user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))  # per-monitor v2: work in physical pixels

VK_MENU, VK_Q, VK_ESCAPE = 0x12, 0x51, 0x1B
KEYUP = 0x2


def key(vk, up=False):
    user32.keybd_event(vk, 0, KEYUP if up else 0, 0)


def hotkey():
    key(VK_MENU); key(VK_Q); time.sleep(0.03); key(VK_Q, True); key(VK_MENU, True)


def grab(cam):
    for _ in range(20):
        frame = cam.grab(new_frame_only=False)
        if frame is not None:
            return frame
        time.sleep(0.05)
    raise RuntimeError("no frame")


def find(ocr, frame, word):
    res = ocr(frame, return_word_box=True)
    for line_words in res.word_results or []:
        # latin: whole words; CJK: single characters -> match the first char of the target
        for text, _score, box in line_words:
            if word in text or (len(text) == 1 and text == word[0]):
                xs = [p[0] for p in box]; ys = [p[1] for p in box]
                return int(sum(xs) / 4), int(sum(ys) / 4)
    return None


def main():
    out = Path(sys.argv[1]); out.mkdir(parents=True, exist_ok=True)
    words = sys.argv[2:]
    wait = float(os.environ.get("E2E_WAIT", "6"))
    cam = dxcam.create(output_color="RGB")
    ocr = RapidOCR(params={"Global.log_level": "error"})
    frame = grab(cam)
    for i, w in enumerate(words):
        pos = find(ocr, frame, w)
        if pos is None:
            print(f"[{w}] not found on screen"); continue
        user32.SetCursorPos(*pos)
        time.sleep(0.3)
        t = time.perf_counter()
        hotkey()
        time.sleep(wait)
        shot = grab(cam)
        Image.fromarray(shot).save(out / f"{i:02d}_{w}.png")
        print(f"[{w}] at {pos}, shot after {time.perf_counter() - t:.1f}s")
        key(VK_ESCAPE); time.sleep(0.05); key(VK_ESCAPE, True)
        time.sleep(0.5)
    del cam


if __name__ == "__main__":
    main()
