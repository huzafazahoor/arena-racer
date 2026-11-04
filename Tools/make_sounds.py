"""Synthesises every sound effect in the game (no third-party audio). Run: python Tools/make_sounds.py"""
import math, os, random, struct, sys, wave

RATE = 22050
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Assets", "ArenaRacer", "Audio")
rng = random.Random(42)


def save(name, samples, peak=0.85):
    top = max(1e-9, max(abs(s) for s in samples))
    data = b"".join(struct.pack("<h", int(max(-1.0, min(1.0, s / top * peak)) * 32767)) for s in samples)
    with wave.open(os.path.join(OUT, name + ".wav"), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(data)
    print(f"{name}.wav  {len(samples) / RATE:.2f} s")


def lowpass(samples, cutoff, loop=False):
    a = 1.0 - math.exp(-2.0 * math.pi * cutoff / RATE)
    out, y = [0.0] * len(samples), 0.0
    for _ in range(2 if loop else 1):  # a second pass round the loop settles the filter so it tiles seamlessly
        for i, s in enumerate(samples):
            y += a * (s - y)
            out[i] = y
    return out


def env(t, attack, decay):
    return min(1.0, t / attack) * math.exp(-t / decay) if attack > 0 else math.exp(-t / decay)


def engine():
    f0, n = 70.0, int(RATE * 0.5)  # 35 whole cycles: loops cleanly
    phases = [rng.random() * 6.283 for _ in range(10)]
    noise = lowpass([rng.uniform(-1, 1) for _ in range(n)], 900, loop=True)
    out = []
    for i in range(n):
        t = i / RATE
        s = sum((1.0 / k ** 1.15) * math.sin(2 * math.pi * k * f0 * t + phases[k]) for k in range(1, 10))
        s *= 0.75 + 0.25 * math.sin(2 * math.pi * f0 * 0.5 * t)  # uneven firing
        s += noise[i] * 0.35
        out.append(math.tanh(1.6 * s))
    return out


def skid():
    n = int(RATE * 1.0)
    white = [rng.uniform(-1, 1) for _ in range(n)]
    band = [a - b for a, b in zip(lowpass(white, 3200, True), lowpass(white, 900, True))]
    return [s * (0.8 + 0.2 * math.sin(2 * math.pi * 7 * i / RATE)) for i, s in enumerate(band)]


def boost():
    n = int(RATE * 0.75)
    out, y, phase = [], 0.0, 0.0
    for i in range(n):
        t = i / RATE
        cutoff = 400 + 3600 * math.sin(math.pi * min(1.0, t / 0.75))
        a = 1.0 - math.exp(-2 * math.pi * cutoff / RATE)
        y += a * (rng.uniform(-1, 1) - y)
        phase += 2 * math.pi * (180 + 520 * t / 0.75) / RATE
        out.append((y * 0.9 + math.sin(phase) * 0.35) * env(t, 0.04, 0.35))
    return out


def bump():
    n = int(RATE * 0.32)
    out, phase = [], 0.0
    for i in range(n):
        t = i / RATE
        phase += 2 * math.pi * (110 * math.exp(-t * 6) + 45) / RATE
        out.append(math.sin(phase) * env(t, 0.002, 0.09) + rng.uniform(-1, 1) * env(t, 0.0005, 0.012) * 0.8)
    return lowpass(out, 2500)


def tone(freqs, length, decay, attack=0.004):
    n = int(RATE * length)
    out = []
    for i in range(n):
        t = i / RATE
        s = sum(math.sin(2 * math.pi * f * t) + 0.25 * math.sin(2 * math.pi * 3 * f * t) for f in freqs)
        out.append(s * env(t, attack, decay) * min(1.0, (length - t) / 0.01))
    return out


def sequence(notes, step, hold):
    out = []
    for k, f in enumerate(notes):
        length = hold if k == len(notes) - 1 else step
        part = tone([f], length, length * 0.6)
        if k == len(notes) - 1:
            part = [s * (1 + 0.15 * math.sin(2 * math.pi * 6 * i / RATE)) for i, s in enumerate(part)]
        out += part
    return out


def click():
    n = int(RATE * 0.05)
    return [math.sin(2 * math.pi * 2000 * i / RATE) * env(i / RATE, 0.001, 0.008) for i in range(n)]


if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    save("engine", engine(), 0.8)
    save("skid", skid(), 0.7)
    save("boost", boost())
    save("bump", bump())
    save("beep", tone([660], 0.18, 0.09))
    save("go", tone([990, 1320], 0.5, 0.25))
    save("lap", sequence([880, 1175], 0.16, 0.3))
    save("finish", sequence([523, 659, 784, 1047], 0.13, 0.7))
    save("click", click(), 0.5)
