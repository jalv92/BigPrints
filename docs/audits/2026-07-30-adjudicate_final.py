# ponytail: one code path, both events, adjudicates every lead-vs-refuter disagreement
import json, statistics, datetime
from collections import deque, defaultdict

def load(d): return json.load(open(f'{d}/event.json'))

def corr_idx(vols):
    n = len(vols); idx = list(range(n))
    mi, mv = statistics.mean(idx), statistics.mean(vols)
    cov = sum((i-mi)*(v-mv) for i, v in zip(idx, vols))
    sdx, sdv = statistics.pstdev(idx), statistics.pstdev(vols)
    return cov/(sdx*sdv*n) if sdx*sdv > 0 else float('nan')

EV = [('R', 'analysis', datetime.datetime(2026,7,27,9,33,6,900000)),
      ('C', 'analysis2', datetime.datetime(2026,7,27,9,41,39,996000))]

res = {}
for name, d, t0 in EV:
    e = load(d)
    trig = e['meta']['trigger']; ts, te = trig['t_start_ms'], trig['t_end_ms']
    trades = sorted([r for r in e['tape'] if r[3] != 0], key=lambda r: r[0])
    sells = [r for r in trades if r[3] == -1]
    tmin, tmax = trades[0][0], trades[-1][0]
    sw = [r for r in sells if ts <= r[0] <= te]
    sw_end = min(r[1] for r in sw)
    print(f'===== {name}: ts={ts} te={te} tmin={tmin} tmax={tmax} sw_end={sw_end} '
          f'pretrig_hist={(ts-tmin)/1000:.1f}s =====')

    # ---- A1. D1: trailing-10s delta, spec (T=ts) and as-implemented (T=ts-2000); plus cum-since-start
    def d10(T): return sum(r[2]*r[3] for r in trades if T-10000 < r[0] <= T)
    cum_start = sum(r[2]*r[3] for r in trades if r[0] <= ts-2000)
    print(f'A1 D1-delta: cum_since_file_start@ts-2s={cum_start} (the reported number) | '
          f'trailing10s@ts={d10(ts-0)} trailing10s@ts-2s={d10(ts-2000)}')
    # percentile of trailing10s@ts within own file, ALL valid windows vs pre-trigger-only
    samples_all, samples_pre = [], []
    for T in range(tmin+10000, tmax+1, 1000):
        v = d10(T)
        samples_all.append((T, v))
        if T < ts: samples_pre.append((T, v))
    a = d10(ts)
    def pct(v, pop): return 100*sum(1 for _, x in pop if x <= v)/len(pop) if pop else float('nan')
    lo = min(samples_all, key=lambda p: p[1])
    print(f'A1 pct(trailing10@ts) vs ALL {len(samples_all)} windows: {pct(a, samples_all):.1f}% '
          f'(file min={lo[1]} at T={lo[0]}) | vs PRE-TRIGGER-only {len(samples_pre)} windows: {pct(a, samples_pre):.1f}%'
          + (f' (pre-trig min={min(v for _,v in samples_pre)})' if samples_pre else ''))

    # ---- A2. VWAP60 dist at ts (note truncation)
    w60 = [r for r in trades if ts-60000 < r[0] <= ts]
    vwap = sum(r[1]*r[2] for r in w60)/sum(r[2] for r in w60)
    px = max((r for r in trades if r[0] <= ts), key=lambda r: r[0])[1]
    print(f'A2 VWAP60@ts: dist={px-vwap:.2f} (effective lookback={min(60.0,(ts-tmin)/1000):.1f}s of 60s)')

    # ---- A3. D2 causal-only lumpiness
    maxp = max(r[2] for r in sw)
    all_s = [r[2] for r in sells]; causal_s = [r[2] for r in sells if r[0] < ts]
    ge_all = sum(1 for x in all_s if x >= maxp); ge_c = sum(1 for x in causal_s if x >= maxp)
    print(f'A3 D2: max trigger print={maxp} | whole-file N={len(all_s)} peers>={ge_all} '
          f'top-pct={100*ge_all/len(all_s):.3f}% | CAUSAL N={len(causal_s)} peers>={ge_c} '
          f'top-pct={100*ge_c/len(causal_s):.3f}%')

    # ---- A4. D3 three ways
    below = [r for r in sells if r[0] >= te and r[1] < sw_end]
    order, first_t = [], {}
    for r in below:
        if r[1] not in first_t:
            first_t[r[1]] = r[0]; order.append(r[1])
    # (i) SPEC-CAUSAL: volumes counted only up to t_eval = min(first-touch of 16th level, te+5000)
    t16 = first_t[order[15]] if len(order) >= 16 else None
    t_eval = min(x for x in [t16, te+5000] if x is not None)
    lv = defaultdict(int)
    for r in below:
        if r[0] <= t_eval and r[1] in order[:16]: lv[r[1]] += r[2]
    lv_levels = [p for p in order[:16] if p in lv]
    vols_i = [lv[p] for p in lv_levels]
    h = len(vols_i)//2
    print(f'A4 D3(i) SPEC-CAUSAL cutoff@t_eval={t_eval} ({(t_eval-te)/1000:.2f}s after te), '
          f'n_lvls={len(vols_i)} vols={vols_i}')
    print(f'   ratio(2nd/1st half)={statistics.mean(vols_i[h:])/statistics.mean(vols_i[:h]):.2f} '
          f'corr={corr_idx(vols_i):.2f}')
    # (ii) REFUTER variant: first16 levels, volume accumulated over WHOLE remaining file
    lv2 = defaultdict(int)
    for r in below:
        if r[1] in order[:16]: lv2[r[1]] += r[2]
    vols_ii = [lv2[p] for p in order[:16]]
    h2 = len(vols_ii)//2
    print(f'A4 D3(ii) refuter whole-file-accum on first16: vols={vols_ii} '
          f'ratio={statistics.mean(vols_ii[h2:])/statistics.mean(vols_ii[:h2]):.2f} corr={corr_idx(vols_ii):.2f}')
    # (iii) fixed te..te+5s, all levels traded (price-descending order), lead/attack3 variant
    leg5 = [r for r in sells if te <= r[0] <= te+5000]
    lv3 = defaultdict(int)
    for r in leg5: lv3[r[1]] += r[2]
    lvl3 = sorted(lv3, reverse=True); vols_iii = [lv3[p] for p in lvl3]
    h3 = len(vols_iii)//2
    print(f'A4 D3(iii) fixed te+5s all-levels price-desc: n={len(vols_iii)} '
          f'ratio={statistics.mean(vols_iii[h3:])/statistics.mean(vols_iii[:h3]):.2f} corr={corr_idx(vols_iii):.2f}')
    # original H2 (hindsight low_t boundary) for reference
    low_p = min(r[1] for r in trades); low_t = min(r[0] for r in trades if r[1] == low_p)
    lego = [r for r in sells if te <= r[0] <= low_t]
    lvo = defaultdict(int)
    for r in lego: lvo[r[1]] += r[2]
    lvlo = sorted(lvo, reverse=True); vols_o = [lvo[p] for p in lvlo]
    ho = len(vols_o)//2
    print(f'A4 H2-orig (te->low_t, HINDSIGHT): dur={(low_t-te)/1000:.1f}s n={len(vols_o)} '
          f'ratio={statistics.mean(vols_o[ho:])/statistics.mean(vols_o[:ho]):.2f} corr={corr_idx(vols_o):.2f} '
          f'vol@low={lvo[low_p]}')

    # ---- A5. H1 rolling 200ms sell sum: global peak + peaks outside trigger neighborhood
    dq = deque(); acc = 0; series = []
    for r in sells:
        dq.append((r[0], r[2])); acc += r[2]
        while dq and dq[0][0] < r[0]-200: acc -= dq.popleft()[1]
        series.append((r[0], acc))
    peak = max(series, key=lambda p: p[1])
    out5 = [p for p in series if not (ts-500 <= p[0] <= te+500)]
    top_out = sorted(out5, key=lambda p: -p[1])[:5]
    print(f'A5 H1: global peak={peak[1]}@t={peak[0]} | top-5 outside ts±500/te+500: '
          f'{[(t, v, round((t-te)/1000,1)) for t, v in top_out]}')

    # ---- A6. wall-clock + end state
    endwc = t0 + datetime.timedelta(milliseconds=tmax)
    trigwc = t0 + datetime.timedelta(milliseconds=ts)
    print(f'A6 wallclock: trigger={trigwc.time()} file_end={endwc.time()} last_px={trades[-1][1]}')

    # ---- A7. big prints >=20
    bigs = [(r[0], r[2], r[0]-ts) for r in sells if r[2] >= 20]
    print(f'A7 sells>=20 whole file: n={len(bigs)} detail={[(t, s, round(dt/1000,2)) for t, s, dt in bigs]}')

    # ---- A8. D5 10s composite re-verify
    w10p = [r for r in trades if te < r[0] <= te+10000]
    dd = sum(r[2]*r[3] for r in w10p); lp = max(w10p, key=lambda r: r[0])[1]
    print(f'A8 D5: delta(te,te+10s]={dd} price-sw_end={lp-sw_end:+.2f}')
    # sweep anatomy re-verify
    nlv = len(set(r[1] for r in sw)); vol = sum(r[2] for r in sw)
    print(f'A9 sweep: vol={vol} prints={len(sw)} dur={te-ts}ms levels={nlv} '
          f'thruput={vol/max(te-ts,1):.1f}c/ms maxprint={maxp}')
    res[name] = dict(t0=t0, tmax=tmax, ts=ts)
    print()

gap = (res['C']['t0'] + datetime.timedelta(milliseconds=res['C']['ts'])) - \
      (res['R']['t0'] + datetime.timedelta(milliseconds=res['R']['tmax']))
print(f'A6 GAP R-file-end -> C-trigger: {gap.total_seconds():.1f}s (unrecorded)')
