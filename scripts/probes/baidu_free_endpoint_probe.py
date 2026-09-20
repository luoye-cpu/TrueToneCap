#!/usr/bin/env python3
# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: 2026 TrueToneCap contributors
"""百度"有没有免密的整句翻译接口"取证脚本（交接 §35.2）。

与有道那条（`youdao_free_endpoint_probe.py`）成对：维护者问的是"百度、有道这类传统机器翻译
应该有免费可调用的接口"，所以两家都要有可复跑的证据，不能只测一家就写进 README。

**本脚本存在的首要理由是它自己撞出来的那条控制组**：
`https://fanyi.baidu.com/<任何不存在的路径>` 一律回 **HTTP 200 + 空 body**。
⇒ 在这个站点上"状态码 200"零信息量。首版我按状态码记了三条"端点返回 200"，
其中 `/v2/trans`、`/ttranslate`、`/api/second-request/text-translate` 其实与假路径**同形**
（200 且空），也就是说它们根本没在提供服务，是我把"没报错"读成了"活着"。
现在每个端点都必须先过一遍假路径对照，判据只看**响应体的实质内容**。

退出码：0=结论成立（免密面只到词级，整句拿不到译文）；1=结论被推翻（某条免密路真给出了整句译文，
那就得重新评估"默认只走有道官方 API"）；3=网络不可达，SKIP。

用法：py scripts/probes/baidu_free_endpoint_probe.py     （手动跑，不接进 verify.ps1）
"""

import json
import sys
import urllib.error
import urllib.parse
import urllib.request

UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) TrueToneCap-probe"
TIMEOUT = 12.0
EN_SENTENCE = "Please save the file before exiting."
ZH_SENTENCE = "今天天气很好，我们出去玩吧"


def fetch(url, fields=None):
    """返回 (status, body_text)。网络不可达时抛 URLError。fields 非空则 POST 表单。"""
    data = urllib.parse.urlencode(fields).encode() if fields else None
    req = urllib.request.Request(url, data=data, method="POST" if fields else "GET",
                                 headers={"User-Agent": UA,
                                          "Content-Type": "application/x-www-form-urlencoded"}
                                 if fields else {"User-Agent": UA})
    with urllib.request.urlopen(req, timeout=TIMEOUT) as r:
        return r.status, r.read().decode("utf-8", "replace")


def _sug_data(q):
    """免密 /sug 接口返回的条目列表（不是列表时给 None）。"""
    status, body = fetch("https://fanyi.baidu.com/sug", {"kw": q})
    try:
        doc = json.loads(body)
    except json.JSONDecodeError:
        return None
    return doc.get("data") if isinstance(doc.get("data"), list) else None


def _body_shape(body):
    s = body.strip()
    if not s:
        return "空（与假路径同形）"
    if s[0] == "<":
        return f"网页 HTML {len(s)} B"
    return f"JSON/文本 {len(s)} B"


def _extract_translation(body):
    """只在**解析成功的 JSON** 里找译文字段（dst / tgt），HTML 与坏 JSON 一律返回 None。
    返回找到的第一条非空译文，或 None。"""
    s = body.strip()
    if not s or s[0] == "<":
        return None
    try:
        doc = json.loads(s)
    except json.JSONDecodeError:
        return None
    stack = [doc]
    while stack:
        node = stack.pop()
        if isinstance(node, dict):
            for key in ("dst", "tgt"):
                v = node.get(key)
                if isinstance(v, str) and v.strip():
                    return v.strip()
            stack.extend(node.values())
        elif isinstance(node, list):
            stack.extend(node)
    return None


def _brief(v, n=60):
    s = str(v)
    return s if len(s) <= n else s[:n - 1] + "…"


def main():
    results = []

    def check(name, ok, detail):
        results.append((name, bool(ok)))
        print(f"  {'✅' if ok else '❌'} {name}: {detail}")

    unreachable = []

    # ── K0 控制组：假路径的形态 ⇒ 后面所有判据都以它为参照 ──
    try:
        ctrl_status, ctrl_body = fetch("https://fanyi.baidu.com/not-a-real-endpoint-8f3a1d")
    except (urllib.error.URLError, OSError) as e:
        print(f"  ⚠️  fanyi.baidu.com 不可达（{e}）⇒ 整组无法判定")
        print("⚠️  SKIP：需要一台能出网的机器")
        return 3
    check("K0 控制组：不存在的路径也是 200（⇒ 状态码在此站无信息量）",
          ctrl_status == 200 and ctrl_body.strip() == "",
          f"假路径 HTTP={ctrl_status} body 长度={len(ctrl_body.strip())}")

    # ── K1 正对照：/sug 对单词确实返回条目 ⇒ 这个免密口活着 ──
    try:
        word_items = _sug_data("hello")
        en_items = _sug_data(EN_SENTENCE)
        zh_items = _sug_data(ZH_SENTENCE)
    except (urllib.error.URLError, OSError, json.JSONDecodeError) as e:
        print(f"  ⚠️  /sug 不可达（{e}）⇒ K1/K2 无法判定")
        unreachable.append("sug")
    else:
        check("K1 正对照：单词 /sug 有条目", bool(word_items),
              f"hello → {len(word_items or [])} 条，样例 {json.dumps((word_items or [])[:1], ensure_ascii=False)[:80]}")
        check("K2 整句 /sug 零条目（英、中各一次）",
              not en_items and not zh_items,
              f"英文句 {len(en_items or [])} 条 / 中文句 {len(zh_items or [])} 条")

    # ── K3 流传的免密整句端点：v2/trans 与旧 translate，逐个与 K0 形态比对 ──
    for label, url in (
            ("K3a /v2/trans（老文档里的免密整句口）",
             "https://fanyi.baidu.com/v2/trans?appid=0&method=cal.b_itt&from=en&to=zh&f=3&text="
             + urllib.parse.quote(EN_SENTENCE)),
            ("K3b /translate（最老的 GET 口）",
             "https://fanyi.baidu.com/translate?from=en&to=zh&text="
             + urllib.parse.quote(EN_SENTENCE))):
        try:
            st, body = fetch(url)
        except (urllib.error.URLError, OSError, json.JSONDecodeError) as e:
            print(f"  ⚠️  {label} 不可达（{e}）")
            unreachable.append(label)
            continue
        # "拿不到整句译文"的判据要看**实质**，不能看字段名字符串出没出现：
        # /translate 返回的是翻译网页（27 KB HTML，SPA 的 JS bundle 里就带 `trans_result` 字样），
        # 按"body 含 dst/tgt/trans_result"判会把它误读成"接口给了译文"（首跑正是这样假红的）。
        shape = _body_shape(body)
        found = _extract_translation(body)
        check(f"{label} 不给整句译文", found is None,
              f"HTTP={st} 响应形态={shape}"
              + (f"，取到译文：{_brief(found)}" if found else ""))

    judged = len(results)
    if judged == 0:
        print("⚠️  SKIP：没有任何一条判据可判")
        return 3
    failed = [n for n, ok in results if not ok]
    # 这条不参与"未成立"的统计：它是自检，未成立时要单独说明"通过只是因为没测到东西"。
    check("K4 覆盖面：至少判满 4 条且无不可达项（否则上面的'通过'只是没测到东西）",
          judged >= 4 and not unreachable, f"实判 {judged} 条，不可达 {len(unreachable)} 项")
    print(f"\n{'❌' if failed else '✅'} {judged - len(failed)}/{judged} 条判据成立"
          + (f"；未成立：{' / '.join(failed)}" if failed else ""))
    if unreachable:
        print(f"   ⚠️  不可达 {len(unreachable)} 项：{' / '.join(unreachable)}")
    if failed:
        print("   ⇒ 结论可能已变：若某条免密路真能整句翻译，需重新评估默认后端的选择")
    return 1 if (failed or judged < 4 or unreachable) else 0


if __name__ == "__main__":
    sys.exit(main())
