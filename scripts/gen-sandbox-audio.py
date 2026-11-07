#!/usr/bin/env python3
"""Generates the sandbox WAVs (22.05 kHz, 16-bit, mono) with the Python stdlib only.

Music loops are 8 beats at 120 BPM (4.0 s) and wrap seamlessly: every note length divides the loop and the last note's
release ends before the loop point. Deterministic (fixed seeds) so re-running produces byte-identical files; every file
stays well under 300 KB.
"""
import math
import os
import random
import struct
import wave

RATE = 22050
BPM = 120.0
BEAT = 60.0 / BPM
LOOP_BEATS = 8
LOOP_SECONDS = LOOP_BEATS * BEAT  # 4.0; LOOP_MS below is the frame-count source of truth
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "demo", "sandbox", "audio")

# Lengths the authored project relies on (DemoProject.BuildSandbox mirrors these as frame counts in AssetLengths).
CHIRP_MS = (350, 450, 300)
STINGER_MS = 1000
LINE_EN_MS = 1600
LINE_DE_MS = 1800
CLICK_MS = 80
LOOP_MS = 4000


def write(name, samples):
    os.makedirs(OUT, exist_ok=True)
    path = os.path.join(OUT, name)
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(b"".join(struct.pack("<h", max(-32767, min(32767, int(s * 32767)))) for s in samples))
    return os.path.getsize(path)


def frames(ms):
    """Frame count for a duration in whole milliseconds; integer arithmetic so C# can mirror it exactly."""
    return RATE * ms // 1000


def note_hz(semitones_from_a4):
    return 440.0 * 2.0 ** (semitones_from_a4 / 12.0)


def env_ad(i, n, attack, release):
    """Linear attack / linear release envelope over n samples (attack and release in samples)."""
    if i < attack:
        return i / attack
    if i >= n - release:
        return max(0.0, (n - i) / release)
    return 1.0


def saturate(x):
    return math.tanh(x)


# ---- birds ----------------------------------------------------------------------------------------------------------

def chirp(ms, base_hz, sweep_hz, trill_hz, seed):
    rng = random.Random(seed)
    n = frames(ms)
    out = []
    phase = 0.0
    jitter = rng.uniform(0.9, 1.1)
    for i in range(n):
        t = i / RATE
        u = i / n
        hz = (base_hz + sweep_hz * math.sin(math.pi * u)) * jitter
        hz += 400.0 * math.sin(2 * math.pi * trill_hz * t)
        phase += 2 * math.pi * hz / RATE
        env = math.sin(math.pi * u) ** 1.5
        out.append(math.sin(phase) * env * 0.6)
    return out


# ---- music ----------------------------------------------------------------------------------------------------------

def calm_pad():
    """Two sustained chords (Am, Fmaj7) over the 8-beat loop, soft detuned sines; crossfaded at the chord change and loop."""
    n = frames(LOOP_MS)
    half = n // 2
    chords = (
        [note_hz(-24), note_hz(-21), note_hz(-17), note_hz(-12)],  # A2 C3 E3 A3
        [note_hz(-28), note_hz(-24), note_hz(-21), note_hz(-17)],  # F2 A2 C3 E3
    )
    fade = frames(250)
    out = [0.0] * n
    for c, chord in enumerate(chords):
        start = c * half
        for i in range(half):
            t = (start + i) / RATE
            s = 0.0
            for k, hz in enumerate(chord):
                detune = 1.0 + 0.0015 * (k - 1.5)
                s += math.sin(2 * math.pi * hz * detune * t) * 0.5 + math.sin(2 * math.pi * hz * 2 * t) * 0.08
            s /= len(chord)
            e = env_ad(i, half, fade, fade)
            out[start + i] += saturate(s * 1.4) * e * 0.55
    return out


def calm_arp():
    """Sixteen eighth-note plucks (A minor pentatonic), each decaying within its slot so the loop wraps cleanly."""
    n = frames(LOOP_MS)
    slot = n // 16
    pattern = [0, 3, 7, 10, 12, 10, 7, 3, 0, 3, 7, 12, 15, 12, 7, 3]
    out = [0.0] * n
    for k, semis in enumerate(pattern):
        hz = note_hz(-12 + semis)
        for i in range(slot):
            t = i / RATE
            env = math.exp(-t * 9.0) * (1.0 if i > 40 else i / 40.0)
            s = math.sin(2 * math.pi * hz * t) + 0.3 * math.sin(2 * math.pi * hz * 2 * t) + 0.1 * math.sin(2 * math.pi * hz * 3 * t)
            out[k * slot + i] += s * env * 0.35
    return out


def combat_drums():
    """Kick on every beat, snare on 2/4, closed hats on eighths with accents; all hits decay inside their slot."""
    rng = random.Random(7)
    n = frames(LOOP_MS)
    out = [0.0] * n
    eighth = n // 16

    def kick(pos):
        length = frames(220)
        for i in range(min(length, n - pos)):
            t = i / RATE
            hz = 48.0 + 110.0 * math.exp(-t * 28.0)
            out[pos + i] += math.sin(2 * math.pi * hz * t) * math.exp(-t * 11.0) * 0.95

    def snare(pos):
        length = frames(180)
        for i in range(min(length, n - pos)):
            t = i / RATE
            noise = rng.uniform(-1.0, 1.0)
            body = math.sin(2 * math.pi * 190.0 * t)
            out[pos + i] += (noise * 0.6 + body * 0.4) * math.exp(-t * 22.0) * 0.7

    def hat(pos, accent):
        length = frames(60)
        hp = 0.0
        prev = 0.0
        for i in range(min(length, n - pos)):
            t = i / RATE
            x = rng.uniform(-1.0, 1.0)
            hp = 0.7 * (hp + x - prev)  # crude high-pass
            prev = x
            out[pos + i] += hp * math.exp(-t * 60.0) * (0.35 if accent else 0.2)

    for beat in range(LOOP_BEATS):
        pos = beat * 2 * eighth
        kick(pos)
        if beat % 2 == 1:
            snare(pos)
    for e in range(16):
        hat(e * eighth, e % 2 == 0)
    for i in range(n):
        out[i] = saturate(out[i] * 1.2) * 0.9
    return out


def combat_lead():
    """Driving sawtooth riff in A minor, sixteenth notes with a hard gate so every note ends inside its slot."""
    n = frames(LOOP_MS)
    slot = n // 32
    riff = [0, 0, 12, 0, 10, 0, 7, 8, 0, 0, 12, 0, 15, 12, 10, 7,
            0, 0, 12, 0, 10, 0, 7, 8, 3, 3, 15, 3, 10, 8, 7, 5]
    out = [0.0] * n
    gate = frames(10)
    for k, semis in enumerate(riff):
        hz = note_hz(-12 + semis)
        length = int(slot * 0.9)
        for i in range(length):
            t = i / RATE
            ph = (hz * t) % 1.0
            saw = 2.0 * ph - 1.0
            ph2 = (hz * 1.004 * t) % 1.0
            saw2 = 2.0 * ph2 - 1.0
            e = env_ad(i, length, gate, gate)
            out[k * slot + i] += (saw + saw2) * 0.5 * e * math.exp(-t * 3.0) * 0.5
    for i in range(n):
        out[i] = saturate(out[i] * 1.5) * 0.7
    return out


def stinger():
    """One-second brass-like hit: detuned saws on an A5 power chord with a fast attack and long release."""
    n = frames(STINGER_MS)
    out = [0.0] * n
    notes = [note_hz(0), note_hz(7), note_hz(12)]
    for i in range(n):
        t = i / RATE
        s = 0.0
        for hz in notes:
            for d in (0.995, 1.0, 1.006):
                ph = (hz * d * t) % 1.0
                s += 2.0 * ph - 1.0
        s /= len(notes) * 3
        env = env_ad(i, n, frames(5), frames(600))
        cutoff = math.exp(-t * 4.0)
        out[i] = saturate(s * (0.6 + cutoff) * 1.3) * env * 0.8
    return out


# ---- dialogue / UI -----------------------------------------------------------------------------------------------------

def line(ms, base_hz, syllables, seed):
    """Vowel-like formant beeps in a rhythmic pattern; distinct per locale by pitch and syllable count."""
    rng = random.Random(seed)
    n = frames(ms)
    out = [0.0] * n
    per = n // syllables
    for s in range(syllables):
        hz = base_hz * (1.0 + rng.uniform(-0.12, 0.12))
        length = int(per * 0.75)
        for i in range(length):
            t = i / RATE
            vib = 1.0 + 0.01 * math.sin(2 * math.pi * 5.5 * t)
            f = hz * vib
            v = (math.sin(2 * math.pi * f * t) * 0.6 + math.sin(2 * math.pi * f * 2 * t) * 0.3
                 + math.sin(2 * math.pi * f * 3 * t) * 0.15)
            e = env_ad(i, length, frames(20), frames(50))
            out[s * per + i] += v * e * 0.5
    return out


def click():
    n = frames(CLICK_MS)
    return [math.sin(2 * math.pi * 1800 * i / RATE) * (1.0 - i / n) ** 2 * 0.7 for i in range(n)]


if __name__ == "__main__":
    files = [
        ("bird_chirp_1.wav", chirp(CHIRP_MS[0], 2800.0, 900.0, 24.0, 1)),
        ("bird_chirp_2.wav", chirp(CHIRP_MS[1], 3400.0, -700.0, 31.0, 2)),
        ("bird_chirp_3.wav", chirp(CHIRP_MS[2], 2300.0, 1200.0, 18.0, 3)),
        ("calm_pad.wav", calm_pad()),
        ("calm_arp.wav", calm_arp()),
        ("combat_drums.wav", combat_drums()),
        ("combat_lead.wav", combat_lead()),
        ("stinger.wav", stinger()),
        ("line_en.wav", line(LINE_EN_MS, 220.0, 5, 11)),
        ("line_de.wav", line(LINE_DE_MS, 150.0, 7, 12)),
        ("ui_click.wav", click()),
    ]
    for name, samples in files:
        size = write(name, samples)
        print(f"{name}: {len(samples) / RATE:.3f} s, {size} bytes")
    print("wrote", len(files), "files to", os.path.normpath(OUT))
