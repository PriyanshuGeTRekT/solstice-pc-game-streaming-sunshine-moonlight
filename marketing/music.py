"""Original ambient music bed for the Solstice videos (synthesised, so no licensing issues).

python music.py <seconds> <out.wav>
Am7 - Fmaj7 - Cmaj7 - G6 at 100 bpm: soft detuned pad, a plucked arpeggio and a sub bass,
with a gentle fade in and a long fade out.
"""
import sys
import wave

import numpy as np

SR = 48000
BPM = 100.0
BEAT = 60.0 / BPM
BAR = 4 * BEAT


def midi(n):
    return 440.0 * 2 ** ((n - 69) / 12)


# Chord tones (MIDI) for pad and arpeggio, bass root.
CHORDS = [
    ([57, 60, 64, 67], 45),  # Am7
    ([53, 57, 60, 64], 41),  # Fmaj7
    ([48, 52, 55, 59], 36),  # Cmaj7
    ([55, 59, 62, 64], 43),  # G6
]


def lowpass(x, cutoff):
    a = np.exp(-2 * np.pi * cutoff / SR)
    y = np.empty_like(x)
    acc = 0.0
    for i in range(len(x)):  # one-pole filter, fine for a short render
        acc = (1 - a) * x[i] + a * acc
        y[i] = acc
    return y


def saw(freq, t, phase=0.0):
    return 2 * ((freq * t + phase) % 1.0) - 1


def render(seconds):
    n = int(seconds * SR)
    t = np.arange(n) / SR
    left = np.zeros(n)
    right = np.zeros(n)
    chord_len = 2 * BAR
    rng = np.random.default_rng(7)

    # Pad: each chord as detuned saws with a slow swell, crossfaded into the next.
    start = 0.0
    k = 0
    while start < seconds:
        notes, bass = CHORDS[k % len(CHORDS)]
        s0 = int(start * SR)
        length = int((chord_len + 1.2) * SR)
        s1 = min(n, s0 + length)
        if s1 <= s0:
            break
        tt = t[s0:s1] - start
        env = np.minimum(1, tt / 1.4) * np.minimum(1, np.maximum(0, (chord_len + 1.2 - tt) / 1.4))
        for note in notes:
            f = midi(note)
            for det, pan in ((-0.07, 0.3), (0.07, 0.7)):
                w = saw(f * 2 ** (det / 12), tt, rng.random()) * env * 0.045
                left[s0:s1] += w * (1 - pan)
                right[s0:s1] += w * pan
        # Sub bass: sine on the root with soft attack.
        b = np.sin(2 * np.pi * midi(bass) * tt) * env * 0.16
        left[s0:s1] += b
        right[s0:s1] += b
        start += chord_len
        k += 1

    left = lowpass(left, 1400)
    right = lowpass(right, 1400)

    # Arpeggio: plucked sine/triangle eighth notes, entering after the intro.
    step = BEAT / 2
    pattern = [0, 2, 1, 3, 2, 1, 3, 2]
    i = 0
    tpos = 2 * BAR
    while tpos < seconds - 4:
        chord = CHORDS[int(tpos // chord_len) % len(CHORDS)][0]
        note = chord[pattern[i % len(pattern)]] + 12
        s0 = int(tpos * SR)
        dur = int(0.9 * SR)
        s1 = min(n, s0 + dur)
        tt = t[s0:s1] - tpos
        f = midi(note)
        tone = (np.sin(2 * np.pi * f * tt) + 0.25 * np.sin(2 * np.pi * 2 * f * tt)) * np.exp(-tt * 5.5) * 0.06
        pan = 0.35 + 0.3 * ((i % 4) / 3)
        left[s0:s1] += tone * (1 - pan)
        right[s0:s1] += tone * pan
        i += 1
        tpos += step

    # Simple stereo echo for space.
    d = int(BEAT * 0.75 * SR)
    l2, r2 = left.copy(), right.copy()
    l2[d:] += right[:-d] * 0.22
    r2[d:] += left[:-d] * 0.22

    mix = np.stack([l2, r2], axis=1)
    fade_in = np.minimum(1, t / 1.5)
    fade_out = np.minimum(1, np.maximum(0, (seconds - t) / 3.5))
    mix *= (fade_in * fade_out)[:, None]
    mix /= max(1e-9, np.abs(mix).max())
    mix *= 0.5  # about -6 dBFS peak: a bed under on-screen text, not a track
    return mix


def save(mix, path):
    data = (np.clip(mix, -1, 1) * 32767).astype("<i2")
    with wave.open(path, "wb") as w:
        w.setnchannels(2)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(data.tobytes())


if __name__ == "__main__":
    save(render(float(sys.argv[1])), sys.argv[2])
