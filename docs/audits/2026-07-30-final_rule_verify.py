"""Final adjudication: checkpoint rule WITH NO FLOOR (only ext==0 arithmetic guard)
vs the draft's floor=8 version, on both recordings. Also degenerate-event census:
how often would a sub-8t extension actually occur? (can't know base rate at n=2,
but confirm both events clear trivially and floor is inert -> dropping it changes nothing)."""
import json

TICK = 0.25
BASE = "/tmp/claude-1000/-home-javlo-Code-Projects-main-project/cbf37f3f-340e-4533-924e-eb9fa665481a/scratchpad"

def load(d):
    with open(f"{BASE}/{d}/event.json") as f:
        ev = json.load(f)
    trig = ev["meta"]["trigger"]
    t_end = trig["t_end_ms"]
    tape = ev["tape"]  # [t_ms, price, size, side, bid, ask]
    # sweep extreme: lowest (sell sweep) price traded in [t_start, t_end]
    sw = [r for r in tape if trig["t_start_ms"] <= r[0] <= t_end]
    sweep_extreme = min(r[1] for r in sw)  # sell sweep both events
    post = [r for r in tape if r[0] > t_end]
    return t_end, sweep_extreme, post

def checkpoint(post, t_end, sweep_extreme, floor_ticks):
    """floor_ticks=0 -> no floor, only ext==0 guard. Returns (label, ext_t, rec, t_res) or censored info."""
    extreme, extreme_t, trig_t = sweep_extreme, t_end, t_end
    WINDOW, CAP = 60_000, 180_000
    for t, price, *_ in post:
        if price < extreme:
            extreme, extreme_t = price, t
        ext = sweep_extreme - extreme
        has = ext >= (floor_ticks - 0.5) * TICK if floor_ticks else ext >= TICK / 2
        if t - extreme_t > WINDOW:
            if not has:
                return ("NO_EXTENSION" if floor_ticks else "NO_EXT(0t)", 0, 0, t)
            rec = (price - extreme) / ext
            return ("REVERSAL" if rec >= 0.5 else "CONTINUATION", ext / TICK, rec, t)
        if t - trig_t > CAP:
            if not has:
                return ("NO_EXTENSION" if floor_ticks else "NO_EXT(0t)", 0, 0, t)
            rec = (price - extreme) / ext
            return ("CONTINUATION", ext / TICK, rec, t)
    # censored
    last_t, last_p = post[-1][0], post[-1][1]
    ext = sweep_extreme - extreme
    rec = (last_p - extreme) / ext if ext > 0 else float("nan")
    return ("CENSORED", ext / TICK, rec, last_t,
            (extreme_t + WINDOW - t_end) / 1000.0)  # when checkpoint would fall

for name, d in (("R", "analysis"), ("C", "analysis2")):
    t_end, sw, post = load(d)
    print(f"== event {name}: sweep_extreme={sw}, t_end rel file end +{(post[-1][0]-t_end)/1000:.1f}s")
    for fl in (0, 8):
        r = checkpoint(post, t_end, sw, fl)
        tag = "no-floor" if fl == 0 else "floor=8"
        if r[0] == "CENSORED":
            print(f"  {tag}: CENSORED at +{(r[3]-t_end)/1000:.1f}s  ext={r[1]:.0f}t rec_at_end={r[2]*100:.1f}%  checkpoint_would_be=+{r[4]:.1f}s")
        else:
            print(f"  {tag}: {r[0]}  ext={r[1]:.0f}t rec={r[2]*100:.1f}% at +{(r[3]-t_end)/1000:.1f}s")
    # C extra: max recovery within 60s of the global low (refuter/draft cross-check)
    extreme = min(p for _, p, *_ in post)
    ext = sw - extreme
    lows = [t for t, p, *_ in post if p == extreme]
    lo_t = lows[0]
    inw = [(p - extreme) / ext for t, p, *_ in post if lo_t <= t <= lo_t + 60_000]
    print(f"  global low {extreme} at +{(lo_t-t_end)/1000:.1f}s ext={ext/TICK:.0f}t; "
          f"max rec within 60s of low={max(inw)*100:.1f}%")
