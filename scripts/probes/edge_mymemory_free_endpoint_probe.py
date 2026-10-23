#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Edge 与 MyMemory 两条免认证翻译端点的取证脚本（手动跑，**不进门禁**）。

为什么需要它（2026-10-23）：维护者的口径是"免费档不该逼用户去注册拿密钥"，
产品侧据此把免费公共端点升成默认路径。升之前必须自己量清楚这三件事，
任何一件靠转述都会错：

1. **免认证到底成不成立** —— 逐个候选真发请求，判据是"能不能取回一句与原文不同的译文"，
   不是状态码（MyMemory 被拒时回的是 HTTP 200 + 体内 responseStatus=403）。
2. **必需的请求头有哪些** —— ⚠ 这条本脚本刻意用 `http.client` 而不是 `urllib`：
   `urllib` 会**自己注入** `User-Agent: Python-urllib/3.x`，于是"我没设 UA"会被误读成
   "这个端点不需要 UA"。上一轮就是这么错的，.NET 侧真发请求时第一档当场 400
   `Client Browser Version not supported` 并静默降到必应，而离线形状判据全绿。
3. **各家硬限制在哪一格** —— Edge 的单条容量、MyMemory 的 500 上限按**字符**不是字节。

跑法：`py scripts/probes/edge_mymemory_free_endpoint_probe.py`
消费方：`src/TrueToneCap.Core/Services/TranslationService.cs`
        （EdgeClientUa / MyMemoryMaxChars 两处常数的注释点名本脚本）
判据：  `src/TrueToneCap.Test/CorePipelineTests.cs`
        的 Test_EdgeFreeEndpointShape 与 Test_MyMemoryCharLimitAndSilentRejection
退出码：0 = 全部判据成立；1 = 有判据不成立（上游改版或本机网络变化）；
        **绝不出现"什么都没判还退 0"** —— 每条缺席结论前都有一条正控先跑。
"""
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
import http.client
import json
import ssl
import sys
import urllib.parse

TEXT = "Please save the file before exiting."
BODY = json.dumps([TEXT]).encode("utf-8")
CTX = ssl.create_default_context()
TIMEOUT = 8
failures = []
checks = 0


def request(host, path, body=None, headers=None, method="POST"):
    """只用 http.client：它不会替我们补 User-Agent，头的有无因此是可判的。"""
    h = dict(headers or {})
    conn = http.client.HTTPSConnection(host, timeout=TIMEOUT, context=CTX)
    try:
        conn.request(method, path, body=body, headers=h)
        r = conn.getresponse()
        return r.status, r.read().decode("utf-8", "replace")
    except Exception as ex:                                    # noqa: BLE001 - 取证要把任何异常都报出来
        return None, "%s: %s" % (type(ex).__name__, str(ex)[:80])
    finally:
        conn.close()


def edge(path, body=BODY, headers=None, method="POST"):
    hdr = {"Content-Type": "application/json", "User-Agent": "TrueToneCap/desktop-translation-client"}
    if headers is not None:
        hdr = headers
    return request("edge.microsoft.com", "/translate/translatetext" + path, body, hdr, method)


def edge_text(status, body):
    if status != 200:
        return None
    try:
        return json.loads(body)[0]["translations"][0]["text"]
    except Exception:
        return None


def check(name, ok, detail=""):
    global checks
    checks += 1
    print("%s %s%s" % ("OK  " if ok else "FAIL", name, ("  :: " + detail[:70]) if detail else ""), flush=True)
    if not ok:
        failures.append(name)


print("=== 0. 仪器自检（这三条不成立，后面的缺席结论一律不作数）===", flush=True)
s, b = request("www.bing.com", "/translator", None, {"User-Agent": "Mozilla/5.0"}, "GET")
# ⚠ http.client 不跟重定向：www.bing.com 给的是 302 + Location 到 cn.bing.com（本机实测）。
#    把"没跟跳转"读成"正控失败"就等于自己把仪器弄坏 —— 所以 2xx 与 3xx 都算可达，
#    再跟着 Location 取一次真页面，确认 HTML 体量在那儿。
if s in (301, 302, 303, 307, 308):
    import re
    m = re.search(r"https?://([^/]+)(/[^\"\s]*)?", b)
    if m:
        s2, b2 = request(m.group(1), (m.group(2) or "/"), None, {"User-Agent": "Mozilla/5.0"}, "GET")
        s, b = s2, b2
check("正控：bing 翻译页可达且返回大体积 HTML（http.client 不自动跟跳转）",
      s == 200 and len(b) > 10000, "HTTP %s, %d B" % (s, len(b)))
s, b = request("edge.microsoft.com", "/definitely-not-a-real-path-zz", None, {}, "GET")
check("对照：同主机假路径不是 200（否则『404 说明没服务』这条推理无效）", s is not None and s != 200, "HTTP %s" % s)
s, b = request("ttc-no-such-host.invalid", "/", None, {}, "GET")
check("对照：死主机给出异常而不是 200", s is None, b)

print("=== 1. Edge：UA 的有无是分水岭（上一轮就错在这里）===", flush=True)
s, b = edge("?&from=en&to=zh-Hans", BODY, {"Content-Type": "application/json"})          # 无 UA
check("无 UA ⇒ 400 且明写 Client Browser Version not supported",
      s == 400 and "Client Browser Version" in b, "HTTP %s %s" % (s, b))
s, b = edge("?&from=en&to=zh-Hans", BODY, {"Content-Type": "application/json", "User-Agent": ""})
check("空 UA ⇒ 同样 400（判的是『非空』而不是『存在』）", s == 400, "HTTP %s" % s)
for ua in ("curl/8.9.0", "Python-urllib/3.12", "TrueToneCap/desktop-translation-client"):
    s, b = edge("?&from=en&to=zh-Hans", BODY, {"Content-Type": "application/json", "User-Agent": ua})
    check("任意非空 UA 都放行（%s）⇒ 它不解析浏览器版本" % ua.split("/")[0],
          s == 200 and edge_text(s, b) is not None, "HTTP %s %s" % (s, (edge_text(s, b) or b)))
s, b = edge("?&from=en&to=zh-Hans", BODY, {"User-Agent": "curl/8.9.0"})                   # 无 Content-Type
check("无 Content-Type ⇒ 400（这个头是真必需）", s == 400 and edge_text(s, b) is None, "HTTP %s" % s)

print("=== 2. Edge：body 形状与参数（写错就是 400 空体）===", flush=True)
s, b = edge("?&from=en&to=zh-Hans", json.dumps({"text": TEXT}).encode(), None)
check("对象 {\"text\":…} 被拒", s == 400, "HTTP %s" % s)
s, b = edge("?&from=en&to=zh-Hans", json.dumps([{"text": TEXT}]).encode(), None)
check("对象数组 [{\"text\":…}] 被拒", s == 400, "HTTP %s" % s)
s, b = edge("?&from=en&to=zh-Hans", json.dumps([TEXT, "Hotkey is occupied."]).encode(), None)
try:
    pair = [x["translations"][0]["text"] for x in json.loads(b)]
except Exception:
    pair = []
check("裸字符串数组可用且批量按序对齐", s == 200 and len(pair) == 2, "HTTP %s -> %s" % (s, pair))
s, b = edge("?&from=auto&to=zh-Hans", BODY, None)
check("from=auto 被拒 ⇒ 自动检测必须【省略 from】", s == 400, "HTTP %s %s" % (s, b))
s, b = edge("?&to=zh-Hans", BODY, None)
check("省略 from 可用（自动检测的正确写法）", s == 200 and edge_text(s, b) is not None, "HTTP %s" % s)
for lang in ("zh-CN", "zh-TW", "ja", "ko", "ar", "th", "vi", "fr", "de", "es", "ru", "pt", "it", "en"):
    s, b = edge("?&from=en&to=" + lang, BODY, None)
    check("目标语言 %s 原样可用（产品侧无需映射表）" % lang,
          s == 200 and edge_text(s, b) is not None, "HTTP %s %s" % (s, b))
big = ("word " * 8000)[:40000]
s, b = edge("?&from=en&to=zh-Hans", json.dumps([big]).encode(), None)
check("单条 40000 字符仍可用", s == 200 and edge_text(s, b) is not None, "HTTP %s" % s)
s, b = edge("?&from=en&to=zh-Hans", json.dumps(["x" * 200000]).encode(), None)
check("单条 20 万字符被拒且给得出原因（上限落在 4 万与 20 万之间）",
      s == 400 and "exceeds" in b, "HTTP %s %s" % (s, b))

print("=== 3. MyMemory：免认证、按字符的上限、以及『200 里藏着拒绝』===", flush=True)
mm_host = "api.mymemory.translated.net"


def mm(q, pair_="en|zh-CN"):
    return request(mm_host, "/get?q=" + urllib.parse.quote(q) + "&langpair=" + pair_, None,
                   {"User-Agent": "TrueToneCap/desktop-translation-client"}, "GET")


def mm_text(body):
    try:
        d = json.loads(body)
        return d["responseData"]["translatedText"], d.get("responseStatus")
    except Exception:
        return None, None


s, b = mm(TEXT)
t, st = mm_text(b)
check("匿名 GET 出整句译文（无任何 key/注册）", s == 200 and st == 200 and t and t != TEXT,
      "HTTP %s status=%s -> %s" % (s, st, t))
s, b = mm("lighthouse")
t, st = mm_text(b)
check("单词也出（match=1 那条是翻译记忆命中，与生僻句的 MT 兜底是同族不同形）",
      s == 200 and t is not None, "HTTP %s -> %s" % (s, t))
s, b = request(mm_host, "/no-such-path-zz", None, {"User-Agent": "Mozilla/5.0"}, "GET")
check("对照：同主机假路径不是 200", s is not None and s != 200, "HTTP %s" % s)
s, b = mm("a" * 500)
t, st = mm_text(b)
check("恰好 500 字符通过", s == 200 and st == 200, "HTTP %s status=%s" % (s, st))
s, b = mm("a" * 501)
t, st = mm_text(b)
# ⚠ 真件的形状：拒绝时 responseStatus 是**字符串** "403"，而成功时是**数字** 200。
#    任何 `st == 403` 的写法在真件上恒假（本脚本上一轮就是这样"FAIL"的），
#    产品侧 ParseMyMemoryResponse 因此两种都读（Number → GetInt32，否则 int.TryParse）。
check("501 字符 ⇒ HTTP 200 但体内 responseStatus=403（真件是【字符串】），拒因写在译文栏里",
      s == 200 and str(st) == "403" and t and "LIMIT" in t.upper(),
      "HTTP %s status=%r(%s) -> %s" % (s, st, type(st).__name__, t))
check("同一条拒因也出现在 responseDetails 里（两个地方都写，产品只读译文栏也够用）",
      "LIMIT" in (json.loads(b).get("responseDetails") or "").upper(),
      str(json.loads(b).get("responseDetails"))[:60])
zh500 = "汉" * 500
s, b = mm(zh500, "zh-CN|en")
t, st = mm_text(b)
check("500 个汉字（1500 UTF-8 字节）也通过 ⇒ 上限按【字符】不是【字节】，官方 spec 页那句 bytes 说歪了",
      s == 200 and st == 200, "HTTP %s status=%s 字节=%d" % (s, st, len(zh500.encode("utf-8"))))
s, b = mm(TEXT, "autodetect|zh-CN")
t, st = mm_text(b)
check("源语言写 autodetect 可用（不是 auto）", s == 200 and st == 200 and t is not None, "HTTP %s" % s)

print("=== 4. 汇总 ===", flush=True)
print("checks=%d failures=%d %s" % (checks, len(failures), "RESULT edge_mymemory=PASS" if not failures
                                    else "RESULT edge_mymemory=FAIL " + " | ".join(failures)), flush=True)
sys.exit(0 if not failures else 1)
