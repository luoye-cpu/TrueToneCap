#!/usr/bin/env python3
# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: 2026 TrueToneCap contributors
"""有道"有没有免密的整句翻译接口"取证脚本（交接 §35）。

为什么要有这个脚本：README / 设置页都写着"有道没有免密的翻译接口，需自备 AppID/密钥"。
这是一条**负断言** —— 负断言必须有能一票区分"有/没有"的实验，不能靠印象，
也不能只测一个端点就说"没有"。本脚本把四条路各撞一次，并把判据写成"结论被推翻时变红"。

判据（全部成立才算 PASS）：
  J1 官方 openapi 缺 appKey → errorCode 101（= 必填参数为空，不是签名错）
  J2 官方 openapi 带假 appKey + 乱填 sign → errorCode 108（AppID 不存在）
     ⇒ J1+J2 合起来才说明"101 归因于缺 AppID"这个解释是对的：
       服务端先查参数齐不齐、再查 AppID、最后才验签，所以 101 与 108 可区分。
  J3 免密词典接口 jsonapi：**正对照**（单词 hello 必须拿到 web_trans，证明这个口活着、
     也确实会返回网页级结果）+ **整句拿不到任何译文载荷**（英文带标点句 / 中文句 / 韩文句 /
     菜单串四种形态）+ **内容判定**（短语查到的 web_trans 是爬来的例句节选，带 <b> 标签或省略号，
     不是可直接展示的译文）
     ⇒ 免密面只到"词与短语的词典/例句"级，不覆盖本程序的实际用法（OCR 得到的是整句）
     ⚠ 这一条初版问的是"fanyi / cs 有没有值"，而那两个字段**根本不在 jsonapi 的响应里**
       （整句载荷叫 web_trans）⇒ 判据错却报绿。重写成"正对照 + 缺席 + 形态"三段，见下面注释。
  J4 网页版 translate_o 免签 POST → errorCode 50（前端私有签名校验不通过），
     旧 GET 端点 → 302 跳走、不给译文
     ⇒ 网页版能翻整句，但要么带不走签名、要么端点已跳转；本程序不移植那套前端签名逻辑。
     ⚠ 两条判据都栽过同一个坑，这里各记一次：(a) urllib 默认**跟随重定向**，只看最终 status
       会把 302→200 误判成"端点活着"；(b) 网页版回的是**整数** errorCode，官方口回的是**字符串**，
       拿 `!= "0"` 比整数恒真 ⇒ 那条判据当时是空的。现在 post() 统一在出口处转字符串。

退出码：0=结论成立；1=结论被推翻（例如 J3 出了整句译文 ⇒ 真的存在免密通道，
应重新评估"默认只走官方 API"这个决定）；3=网络不可达，SKIP（门禁不该被网络钉死）。

用法：py scripts/probes/youdao_free_endpoint_probe.py     （手动跑，不接进 verify.ps1）
"""

import json
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) TrueToneCap-probe"
TIMEOUT = 12.0


def post(url, fields):
    """返回 (http_status, errorCode **字符串或 None**, 原始响应文本)。

    ⚠ errorCode 的类型两家不一致：官方 `openapi` 回**字符串** `"101"`，网页版 `translate_o`
    回**整数** `50`。首版拿 `web_code != "0"` 去比一个 int，恒真 ⇒ 那条判据是空的（自己没发现，
    是重读时抓到的）。统一在出口处转成字符串，比较处就不许再假设类型。
    """
    body = urllib.parse.urlencode(fields).encode()
    req = urllib.request.Request(url, data=body, method="POST",
                                 headers={"User-Agent": UA,
                                          "Content-Type": "application/x-www-form-urlencoded"})
    with urllib.request.urlopen(req, timeout=TIMEOUT) as r:
        raw = r.read().decode("utf-8", "replace")
    try:
        code = json.loads(raw).get("errorCode")
        return r.status, (None if code is None else str(code)), raw
    except json.JSONDecodeError:
        return r.status, None, raw


def _fetch_webtrans(q):
    """取有道免密 jsonapi 的**整句载荷** web_trans（没有则 None）。
    不可达时返回 (None, 原因)，可达但无载荷返回 (None, None)。"""
    url = "https://dict.youdao.com/jsonapi?" + urllib.parse.urlencode({"q": q})
    req = urllib.request.Request(url, headers={"User-Agent": UA})
    try:
        with urllib.request.urlopen(req, timeout=TIMEOUT) as r:
            payload = json.loads(r.read().decode("utf-8", "replace"))
    except (urllib.error.URLError, OSError, json.JSONDecodeError) as e:
        return None, str(e)
    return payload.get("web_trans") or None, None


def _looks_like_corpus_excerpt(web_trans, query):
    """短语查到的到底是"译文"还是"从网上爬来的例句节选"。
    实测形态：带 <b> 高亮标签、带 "..."／"…" 截断，或结果比原短语长出好几倍。"""
    s = json.dumps(web_trans, ensure_ascii=False)
    return "<b>" in s or "..." in s or "…" in s or len(s) > 6 * max(2, len(query)) + 40



def get_status(url):
    """**不**跟随重定向地取状态码 —— 跟随时 302 会落到一个 200 的错误页，
    状态码判据就会被洗白（首跑的假红即源于此）。"""
    req = urllib.request.Request(url, headers={"User-Agent": UA})
    opener = urllib.request.build_opener(_NoRedirect)
    try:
        with opener.open(req, timeout=TIMEOUT) as r:
            return r.status
    except urllib.error.HTTPError as e:
        return e.code


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None  # 让 urlopen 把 3xx 当 HTTPError 抛出，从而原样报出状态码


def main():
    ts = str(int(time.time() * 1000))
    results, skips = [], []

    def check(name, ok, detail):
        results.append((name, bool(ok), detail))
        print(f"  {'✅' if ok else '❌'} {name}: {detail}")

    # ── J1 / J2：官方接口把"缺参数"和"AppID 错"分得很清楚 ──
    base = "https://openapi.youdao.com/api"
    common = {"q": "hello", "from": "AUTO", "to": "zh-CHS",
              "salt": ts, "sign": "deadbeef", "signType": "v3", "curTime": ts}
    try:
        _, code_no_key, _ = post(base, common)
        _, code_bad_key, _ = post(base, {**common, "appKey": "1234567890"})
    except (urllib.error.URLError, OSError) as e:
        print(f"  ⚠️  官方接口不可达（{e}）⇒ J1/J2 无法判定")
        skips.append("openapi.youdao.com")
    else:
        check("J1 缺 appKey ⇒ 101", code_no_key == "101", f"实测 errorCode={code_no_key}")
        check("J2 假 appKey ⇒ 108（先查 ID 再验签）", code_bad_key == "108",
              f"实测 errorCode={code_bad_key}")

    # ── J3：免密词典接口对"整句"不给译文（带正对照，见下面的注释） ──
    # ⚠ 这一条重写过一次。初版问的是"fanyi / cs 两个字段有没有值"，而那两个字段**本来就不在
    #   jsonapi 的响应里**（整句载荷叫 web_trans）——于是我"证明"了一个我根本没在看的量。
    #   判据错却报绿，比缺判据更糟。现在三件事一起做：
    #     J3a 正对照：单词必须能拿到 web_trans ⇒ 证明这个免密口确实活着、确实会返回网页级结果，
    #                于是 J3b 的"没有"就不可能是夹具造出来的。
    #     J3b 整句（英/中/韩三种形态，含 OCR 常见的菜单串）拿不到任何译文载荷。
    #     J3c 拿到的那点结果本身是什么：短语的 web_trans 是**爬来的例句节选**（带 <b> 标签或省略号），
    #        不是可展示的译文。将来若变成干净译文，这条会红，逼着重新看一眼结论。
    j3a_has, j3a_err = _fetch_webtrans("hello")
    if j3a_err is not None:
        skips.append("dict.youdao.com/jsonapi")
        print(f"  ⚠️  jsonapi 不可达（{j3a_err}）⇒ J3 整组无法判定")
    else:
        check("J3a 正对照：单词有 web_trans（接口确实会返回网页级结果）",
              bool(j3a_has), f"hello → {'有' if j3a_has else '无'}，节选 {_brief(j3a_has)}")
        sentences = {
            "英文带标点整句": "Please save the file before exiting.",
            "中文整句": "今天天气很好，我们出去玩吧",
            "韩文短句": "파일을 저장합니다",
            "OCR 菜单串": "File Edit View Help",
        }
        missing, leaked, unreachable = [], [], []
        for label, s in sentences.items():
            wt, err = _fetch_webtrans(s)
            if err is not None:
                unreachable.append(label)
            elif wt:
                leaked.append(label)
            else:
                missing.append(label)
        # 不可达的那些**不许**算进"无载荷"，否则一次断网就能把这条判成绿
        judged = len(missing) + len(leaked)
        check(f"J3b 整句一律无译文载荷（可判 {judged}/{len(sentences)} 种形态）",
              not leaked and judged == len(sentences),
              f"无载荷={missing}" + (f"；不可达={unreachable} ⇒ 本条不予判定" if unreachable else "")
              + (f"；意外拿到载荷={leaked}" if leaked else ""))
        if j3a_has:
            excerpt = _looks_like_corpus_excerpt(j3a_has, "hello")
            check("J3c 短语查到的也是例句节选而不是译文", excerpt,
                  "形态判定=" + ("语料摘录（含 HTML 标签或省略号）" if excerpt
                              else "看起来像成句译文 ⇒ 免密面的能力要重看"))


    # ── J4：网页版两条路 ──
    web_code, web_raw = None, ""
    try:
        _, web_code, web_raw = post(
            "https://fanyi.youdao.com/translate_o?smartresult=dict&smartresult=rule",
            {"i": "hello", "from": "en", "to": "zh-CHS", "salt": ts, "sign": "deadbeef",
             "signType": "v3"})
    except (urllib.error.URLError, OSError) as e:
        print(f"  ⚠️  translate_o 不可达（{e}）⇒ J4a 无法判定")
        skips.append("fanyi.youdao.com/translate_o")
    old_get = None
    try:
        old_get = get_status("https://fanyi.youdao.com/translate?from=en&to=zh-CHS&i=hello&tt=1")
    except (urllib.error.URLError, OSError) as e:
        print(f"  ⚠️  旧 GET 端点不可达（{e}）⇒ J4b 无法判定")
        skips.append("fanyi.youdao.com/translate(GET)")
    # 判"没拿到译文"要同时看两处：errorCode 非 0 **且**响应里没有 translate/baUrl 之类的载荷。
    # 只比 errorCode 是不够的 —— 首版这里拿字符串 "0" 去比整数 0，恒真，等于没判。
    web_got_payload = web_code == "0" or '"translate"' in web_raw
    if web_code is not None or old_get is not None:
        check("J4 网页版：免签 POST 被拒 / 旧 GET 不给译文",
              (web_code is None or not web_got_payload) and (old_get is None or old_get >= 300),
              f"translate_o errorCode={web_code} 载荷={web_got_payload}, "
              f"旧 GET HTTP={old_get}（3xx=跳走，无译文）")

    if len(skips) == 4 or not results:
        print("⚠️  SKIP：网络不可达，本脚本的结论需要一台能出网的机器")
        return 3
    if not results:
        return 3
    failed = [n for n, ok, _ in results if not ok]
    skipped = len(skips)
    print(f"\n{'❌' if failed else '✅'} {len(results) - len(failed)}/{len(results)} 条判据成立"
          + (f"，另有 {skipped} 条因网络跳过" if skipped else ""))
    if failed:
        print("   未成立：" + " / ".join(failed))
        print("   ⇒ 结论可能已变：若存在免密整句翻译，需重新评估『默认只走有道官方 API』")
    return 1 if failed else 0


def _brief(v):
    s = json.dumps(v, ensure_ascii=False) if v is not None else "null"
    return s if len(s) <= 60 else s[:57] + "..."


if __name__ == "__main__":
    sys.exit(main())
