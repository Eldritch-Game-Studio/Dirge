#!/usr/bin/env python3
"""Generates the demo WAVs (44.1 kHz, 16-bit, mono): a 2 s filtered-noise wind bed and a 0.1 s sine click.

Deterministic (fixed seed) so re-running produces byte-identical files.
"""
import math
import os
import random
import struct
import wave

RATE = 44100
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "demo", "audio")


def write(name, samples):
    os.makedirs(OUT, exist_ok=True)
    with wave.open(os.path.join(OUT, name), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(b"".join(struct.pack("<h", max(-32767, min(32767, int(s * 32767)))) for s in samples))


def wind(seconds=2.0):
    rng = random.Random(1)
    n = int(RATE * seconds)
    out = []
    lp = 0.0
    for i in range(n):
        # one-pole low-pass over white noise; slow amplitude swell so looping is audible
        lp += 0.02 * (rng.uniform(-1.0, 1.0) - lp)
        env = 0.6 + 0.4 * math.sin(2 * math.pi * i / n)
        out.append(lp * env * 0.9)
    return out


def click(seconds=0.1):
    n = int(RATE * seconds)
    return [math.sin(2 * math.pi * 1200 * i / RATE) * (1.0 - i / n) * 0.8 for i in range(n)]


if __name__ == "__main__":
    write("wind.wav", wind())
    write("click.wav", click())
    print("wrote wind.wav (2.0 s) and click.wav (0.1 s) to", os.path.normpath(OUT))
