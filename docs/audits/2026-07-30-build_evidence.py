#!/usr/bin/env python3
"""Build the evidence pack for event_093346_sell304 (MNQ 09-26, 2026-07-27).

Reads event.json (BigPrints recorder schema) and writes:
  report.txt          human-readable evidence summary
  timeline_1s.csv     per-second: price, buy/sell vol, delta, cumdelta, best bid/ask+sizes
  levels.csv          per price level: sell vol executed, bid displayed/refill stats, held/broke
  book_imbalance.csv  per snapshot: top10 bid/ask vol, imbalance, best sizes
  big_prints.csv      prints >= 15 contracts
  sweep_detail.csv    every print inside the trigger window
"""
import json, csv, os
from collections import defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
d = json.load(open(os.path.join(HERE, "event.json")))
meta, trig = d["meta"], d["meta"]["trigger"]
tape, inside, book = d["tape"], d["inside"], d["book"]
T0, T1 = trig["t_start_ms"], trig["t_end_ms"]          # 39096..39128
TICK = 0.25

out = open(os.path.join(HERE, "report.txt"), "w")
def w(s=""):
    out.write(s + "\n")

w(f"EVENT {meta['instrument']}  t0={meta['t0']}  trigger SELL {trig['volume']} @ {trig['max_print_price']}")
w(f"trigger window: {T0}..{T1} ms ({T1-T0} ms), {trig['n_prints']} prints, max single print {trig['max_print_size']}")
w(f"file span: {tape[0][0]}..{tape[-1][0]} ms  ({(tape[-1][0]-tape[0][0])/1000:.0f}s)")
w()

# ---- 1. per-second timeline -------------------------------------------------
sec = defaultdict(lambda: {"buy": 0, "sell": 0, "last": None, "hi": -1e9, "lo": 1e9, "n": 0})
for t, p, sz, side, b, a in tape:
    s = sec[t // 1000]
    if side == 1: s["buy"] += sz
    elif side == -1: s["sell"] += sz
    s["last"] = p; s["n"] += 1
    s["hi"] = max(s["hi"], p); s["lo"] = min(s["lo"], p)

# best bid/ask price+size at each second boundary (last seen value)
bb = {}; ba = {}
cur_b = cur_a = None
ins_by_sec = defaultdict(list)
for t, typ, p, sz in inside:
    if typ == 0: cur_b = (p, sz)
    else:        cur_a = (p, sz)
    k = t // 1000
    bb[k] = cur_b; ba[k] = cur_a

cum = 0
with open(os.path.join(HERE, "timeline_1s.csv"), "w", newline="") as f:
    cw = csv.writer(f)
    cw.writerow(["sec", "rel_to_trigger_s", "last", "hi", "lo", "buy", "sell", "delta", "cumdelta",
                 "bid", "bid_sz", "ask", "ask_sz", "n_prints"])
    for k in sorted(sec):
        s = sec[k]
        delta = s["buy"] - s["sell"]; cum += delta
        b = bb.get(k) or (None, None); a = ba.get(k) or (None, None)
        cw.writerow([k, k - T0 // 1000, s["last"], s["hi"], s["lo"], s["buy"], s["sell"], delta, cum,
                     b[0], b[1], a[0], a[1], s["n"]])

# ---- 2. trigger sweep detail ------------------------------------------------
sweep = [r for r in tape if T0 <= r[0] <= T1]
lvl_sweep = defaultdict(lambda: [0, 0])   # price -> [sell vol, n prints]
for t, p, sz, side, b, a in sweep:
    if side == -1:
        lvl_sweep[p][0] += sz; lvl_sweep[p][1] += 1
with open(os.path.join(HERE, "sweep_detail.csv"), "w", newline="") as f:
    cw = csv.writer(f); cw.writerow(["t_ms", "price", "size", "side", "bid", "ask"])
    for r in sweep: cw.writerow(r)
w("TRIGGER SWEEP (levels swept, sell prints only):")
for p in sorted(lvl_sweep, reverse=True):
    v, n = lvl_sweep[p]
    w(f"  {p:.2f}: {v} contracts in {n} prints")
buy_in_sweep = sum(r[2] for r in sweep if r[3] == 1)
w(f"  (buy prints inside window: {buy_in_sweep} contracts)")
w()

# ---- 3. per-level absorption analysis --------------------------------------
# For each level: executed sell volume at level (whole file, split pre/during/post trigger),
# displayed best-bid size stats while bid rested at that level, refill volume
# (sum of size increases while price unchanged), time at best, held-or-broke.
exec_sell = defaultdict(lambda: [0, 0, 0])   # price -> [pre, sweep, post]
for t, p, sz, side, b, a in tape:
    if side == -1:
        i = 0 if t < T0 else (1 if t <= T1 else 2)
        exec_sell[p][i] += sz

lvl = defaultdict(lambda: {"ms_at_best": 0, "max_sz": 0, "refill": 0, "drain": 0,
                           "arrivals": 0, "last_seen": None, "first_seen": None})
prev = None   # (t, price, size)
for t, typ, p, sz in inside:
    if typ != 0: continue
    if prev is not None:
        pt, pp, psz = prev
        L = lvl[pp]
        L["ms_at_best"] += t - pt
        if p == pp:
            if sz > psz: L["refill"] += sz - psz
            else:        L["drain"]  += psz - sz
        else:
            lvl[p]["arrivals"] += 1
            if lvl[p]["first_seen"] is None: lvl[p]["first_seen"] = t
    else:
        lvl[p]["arrivals"] += 1; lvl[p]["first_seen"] = t
    lvl[p]["max_sz"] = max(lvl[p]["max_sz"], sz)
    lvl[p]["last_seen"] = t
    prev = (t, p, sz)

# lowest bid ever + whether each level in the sweep path broke
min_bid = min(p for t, typ, p, sz in inside if typ == 0)
with open(os.path.join(HERE, "levels.csv"), "w", newline="") as f:
    cw = csv.writer(f)
    cw.writerow(["price", "sell_pre", "sell_sweep", "sell_post", "sell_total",
                 "ms_at_best_bid", "max_disp_sz", "refill_vol", "drain_vol",
                 "arrivals_at_best", "ever_broken(bid_went_below)"])
    for p in sorted(set(list(exec_sell) + list(lvl)), reverse=True):
        e = exec_sell.get(p, [0, 0, 0]); L = lvl.get(p)
        broke = "yes" if any(q < p for q in lvl if lvl[q]["arrivals"]) and min_bid < p else ("no" if L else "")
        cw.writerow([f"{p:.2f}", e[0], e[1], e[2], sum(e),
                     L["ms_at_best"] if L else "", L["max_sz"] if L else "",
                     L["refill"] if L else "", L["drain"] if L else "",
                     L["arrivals"] if L else "", "yes" if min_bid < p else "no"])

w("ABSORPTION AT THE LOW (levels 28500.50-28502.00):")
for p in [28502.0, 28501.75, 28501.5, 28501.25, 28501.0, 28500.75, 28500.5]:
    e = exec_sell.get(p, [0, 0, 0]); L = lvl.get(p)
    if L:
        w(f"  {p:.2f}: sold pre/sweep/post = {e[0]}/{e[1]}/{e[2]}  | at-best {L['ms_at_best']}ms, "
          f"max shown {L['max_sz']}, refilled +{L['refill']}, drained -{L['drain']}")
w(f"  lowest best-bid in file: {min_bid:.2f}")
w()

# ---- 4. book imbalance ------------------------------------------------------
with open(os.path.join(HERE, "book_imbalance.csv"), "w", newline="") as f:
    cw = csv.writer(f)
    cw.writerow(["t_ms", "rel_to_trigger_s", "bid10_vol", "ask10_vol", "imb(b/(b+a))",
                 "best_bid", "best_bid_sz", "best_ask", "best_ask_sz", "bid3_vol", "ask3_vol"])
    for s in book:
        bv = sum(v for _, v in s["bids"]); av = sum(v for _, v in s["asks"])
        b3 = sum(v for _, v in s["bids"][:3]); a3 = sum(v for _, v in s["asks"][:3])
        bb0 = s["bids"][0] if s["bids"] else (None, None)
        aa0 = s["asks"][0] if s["asks"] else (None, None)
        cw.writerow([s["t"], round((s["t"] - T0) / 1000, 2), bv, av,
                     round(bv / (bv + av), 3) if bv + av else "",
                     bb0[0], bb0[1], aa0[0], aa0[1], b3, a3])

# book right before / at / after trigger
def snap_at(tms):
    prior = [s for s in book if s["t"] <= tms]
    return prior[-1] if prior else None
for label, tms in [("5s before", T0 - 5000), ("last before", T0), ("first after", T1 + 300),
                   ("+5s", T1 + 5000), ("+30s", T1 + 30000)]:
    s = snap_at(tms)
    if s:
        bv = sum(v for _, v in s["bids"]); av = sum(v for _, v in s["asks"])
        w(f"BOOK {label} (t={s['t']}): bid10={bv} ask10={av} imb={bv/(bv+av):.2f}  "
          f"top3 bids={s['bids'][:3]}  top3 asks={s['asks'][:3]}")
w()

# ---- 5. reversal driver -----------------------------------------------------
def window_stats(a_ms, b_ms):
    buy = sell = 0; first = last = hi = lo = None
    for t, p, sz, side, _, _ in tape:
        if a_ms <= t < b_ms:
            if side == 1: buy += sz
            elif side == -1: sell += sz
            if first is None: first = p
            last = p
            hi = p if hi is None else max(hi, p); lo = p if lo is None else min(lo, p)
    return buy, sell, first, last, hi, lo

w("REVERSAL DRIVER (aggressor flow vs price by window):")
for label, a, b in [("pre  -30..-0s", T0 - 30000, T0), ("sweep", T0, T1 + 1),
                    ("post 0..+10s", T1, T1 + 10000), ("post +10..+30s", T1 + 10000, T1 + 30000),
                    ("post +30..+60s", T1 + 30000, T1 + 60000), ("post +60..+120s", T1 + 60000, T1 + 120000)]:
    buy, sell, first, last, hi, lo = window_stats(a, b)
    if first is not None:
        w(f"  {label:16s}: buy {buy:6d}  sell {sell:6d}  delta {buy-sell:+6d}  "
          f"price {first:.2f}->{last:.2f}  [{lo:.2f}..{hi:.2f}]")
w()

# ---- 6. big prints ----------------------------------------------------------
with open(os.path.join(HERE, "big_prints.csv"), "w", newline="") as f:
    cw = csv.writer(f); cw.writerow(["t_ms", "rel_s", "price", "size", "side", "bid", "ask"])
    for t, p, sz, side, b, a in tape:
        if sz >= 15:
            cw.writerow([t, round((t - T0) / 1000, 2), p, sz, side, b, a])
n15 = sum(1 for r in tape if r[2] >= 15)
w(f"prints >=15 contracts in file: {n15} (see big_prints.csv)")

# largest post-trigger buy prints
post_buys = sorted([r for r in tape if r[0] > T1 and r[3] == 1], key=lambda r: -r[2])[:12]
w("largest BUY prints after trigger:")
for t, p, sz, side, b, a in post_buys:
    w(f"  +{(t-T1)/1000:6.1f}s  {sz:3d} @ {p:.2f}")
out.close()
print("evidence pack written to", HERE)
for fn in ["report.txt"]:
    print(open(os.path.join(HERE, fn)).read())
