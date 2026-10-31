# SPDX-License-Identifier: Apache-2.0
# tests/manual/safety/translate_batch_probe.py
# 翻译批量化的真实读数（一次性仪表，产品代码路径的口径复刻，不改产品、不进门禁）。
#
#   py C:\PLAN\TrueToneCap\tests\manual\safety\translate_batch_probe.py [--rows 14] [--conc 4] [--reps 3]
#
# 口径全部抄自 src/TrueToneCap.Core/Services/TranslationService.cs：
#   Edge 口 URL/参数形状        : EdgeFreeEndpointRequest()（body = 裸 JSON 字符串数组；无 source ⇒ 不发 from=）
#   必须带非空 User-Agent       : EdgeClientUa = "TrueToneCap/desktop-translation-client"
#   单次请求时限                : TryEdgeFreeEndpointAsync() 里的 8 s quickCts（HttpClient.Timeout=15 s 为外圈）
#   逐行并发                    : SemaphoreSlim(4)（见 TranslationService.cs:572 注释）
# 只发合成字符串，绝不发真实截图文本、绝不带任何凭据。
import argparse
import concurrent.futures
import json
import statistics
import sys
import threading
import time
import urllib.error
import urllib.request

EDGE_URL = "https://edge.microsoft.com/translate/translatetext"
EDGE_UA = "TrueToneCap/desktop-translation-client"   # TranslationService.EdgeClientUa
PER_REQUEST_TIMEOUT_S = 8.0                           # TryEdgeFreeEndpointAsync 的 quickCts
CONCURRENCY = 4                                       # SemaphoreSlim(4)

# 合成语料：明显是假数据的短行（菜单/聊天风格），长度分布贴近一屏 OCR 的行宽
SYN_ROWS = [
    "hello world", "file edit view", "save to folder", "copy to clipboard",
    "the quick brown fox jumps", "测试 一行 中文 文本", "settings and privacy",
    "retry later please", "capture region now", "ocr preview window",
    "translate this line", "good afternoon everyone", "please confirm the save path",
    "network unreachable here",
]


def edge_post(rows, target, source, to_log):
    """一次 HTTP 往返：body 是裸 JSON 字符串数组（与产品逐条发的形状相同，条数 = len(rows)）。"""
    url = f"{EDGE_URL}?&to={urllib.parse.quote(target)}"
    if source:
        url += f"&from={urllib.parse.quote(source)}"
    body = json.dumps(rows, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(url, data=body, method="POST", headers={
        "Content-Type": "application/json; charset=utf-8",
        "User-Agent": EDGE_UA,
    })
    t0 = time.perf_counter()
    try:
        with urllib.request.urlopen(req, timeout=PER_REQUEST_TIMEOUT_S) as resp:
            raw = resp.read().decode("utf-8", "replace")
            ms = (time.perf_counter() - t0) * 1000.0
            to_log.append((len(rows), ms, "ok"))
            data = json.loads(raw)
            out = []
            for item in data:
                tr = item.get("translations") or []
                out.append((tr[0].get("text") if tr else None))
            return out, ms
    except Exception as ex:  # noqa: BLE001
        ms = (time.perf_counter() - t0) * 1000.0
        to_log.append((len(rows), ms, f"FAIL:{type(ex).__name__}:{ex}"[:160]))
        return None, ms


def main():
    import urllib.parse  # noqa: F401  (在 edge_post 里用到，这里确保可用)
    ap = argparse.ArgumentParser()
    ap.add_argument("--rows", type=int, default=14)
    ap.add_argument("--conc", type=int, default=CONCURRENCY)
    ap.add_argument("--reps", type=int, default=3)
    ap.add_argument("--target", default="zh-Hans")
    ap.add_argument("--source", default="")
    args = ap.parse_args()
    rows = [SYN_ROWS[i % len(SYN_ROWS)] for i in range(args.rows)]

    log = []
    print(f"═══ [translate-batch probe] Edge translatetext · rows={args.rows} "
          f"conc={args.conc} reps={args.reps} ═══")

    # ── 可达性前置：一发不通就如实退出，不打印推算数 ──
    probe, probe_ms = edge_post(["hello world"], args.target, args.source, log)
    if probe is None or probe[0] is None:
        print("❌ 网络出口未通 —— Edge translatetext 单发即失败，本轮翻译批量化【仍未量】")
        print("   判据：上面那条 FAIL 就是证明（退出码 3 = 前置条件缺失，不打印纸面数）")
        for r in log:
            print("   ", r)
        return 3

    wall_n = []
    per_req_n = []
    # 臂 N：逐行发（产品现状）—— N 个各含 1 条的请求，并发 conc
    for _ in range(args.reps):
        t0 = time.perf_counter()
        with concurrent.futures.ThreadPoolExecutor(max_workers=args.conc) as ex:
            list(ex.map(lambda s: edge_post([s], args.target, args.source, log)[0], rows))
        wall_n.append((time.perf_counter() - t0) * 1000.0)
        per_req_n.extend([e[1] for e in log[-args.rows:]])

    texts_single = None
    t0 = time.perf_counter()
    texts_single, ms1 = edge_post(rows, args.target, args.source, log)
    wall_1 = (time.perf_counter() - t0) * 1000.0

    # 臂 1 的 reps（含首发的 warm 之后再做 reps-1 次）
    wall_1_all = [wall_1]
    for _ in range(args.reps - 1):
        _, _ = edge_post(rows, args.target, args.source, log)
        wall_1_all.append(log[-1][1])

    print(f"  逐行×N (并发{args.conc})  wall 中位 = {statistics.median(wall_n):.0f} ms"
          f"  样本={['%.0f' % x for x in wall_n]}  每请求 ms 中位={statistics.median(per_req_n):.0f}")
    print(f"  批量 1×N              wall = {statistics.median(wall_1_all):.0f} ms"
          f"  样本={['%.0f' % x for x in wall_1_all]}")
    ratio = statistics.median(wall_n) / max(1.0, statistics.median(wall_1_all))
    print(f"  比值(逐行/批量) = {ratio:.2f}x    HTTP 往返数：逐行={args.rows} 批量=1")

    # 语义对撞：同 N 条，批量译文 vs 逐行译文（逐行各发一次取最后一条 log）
    singles = []
    for s in rows:
        t, _ = edge_post([s], args.target, args.source, log)
        singles.append(t[0] if t else None)
    same = sum(1 for a, b in zip(texts_single, singles) if a == b)
    print(f"  译文一致性：批量 vs 逐行 同串 {same}/{args.rows}")
    for i, (a, b) in enumerate(zip(texts_single, singles)):
        if a != b:
            print(f"    row{i} 批量={a!r} 逐行={b!r}")

    print("\n── 全部往返（rows, ms, status）──")
    for r in log:
        print("   ", r)
    return 0


if __name__ == "__main__":
    sys.exit(main())
