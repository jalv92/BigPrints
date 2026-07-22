#!/usr/bin/env python3
"""Generate the BigPrints alert sounds (stdlib only, no deps).

Buy  = short ascending two-tone chirp (660 -> 990 Hz)
Sell = short descending two-tone chirp (990 -> 660 Hz)
Output: ../sounds/BigPrintBuy.wav, ../sounds/BigPrintSell.wav (44.1 kHz, 16-bit mono)
"""
import math
import os
import struct
import wave

RATE = 44100
DUR = 0.32          # seconds total
FADE = 0.012        # seconds fade in/out per segment, kills clicks
AMP = 0.55


def segment(freq, dur):
    n = int(RATE * dur)
    fade_n = int(RATE * FADE)
    out = []
    for i in range(n):
        env = 1.0
        if i < fade_n:
            env = i / fade_n
        elif i > n - fade_n:
            env = (n - i) / fade_n
        # sine + a touch of 2nd harmonic so it cuts through chart-room noise
        s = math.sin(2 * math.pi * freq * i / RATE) + 0.25 * math.sin(4 * math.pi * freq * i / RATE)
        out.append(AMP * env * s / 1.25)
    return out


def write_wav(path, freqs):
    samples = []
    for f in freqs:
        samples += segment(f, DUR / len(freqs))
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(b"".join(struct.pack("<h", int(max(-1, min(1, s)) * 32767)) for s in samples))
    print(f"wrote {path} ({len(samples) / RATE:.2f}s)")


if __name__ == "__main__":
    out_dir = os.path.join(os.path.dirname(__file__), "..", "sounds")
    os.makedirs(out_dir, exist_ok=True)
    write_wav(os.path.join(out_dir, "BigPrintBuy.wav"), [660, 990])
    write_wav(os.path.join(out_dir, "BigPrintSell.wav"), [990, 660])
