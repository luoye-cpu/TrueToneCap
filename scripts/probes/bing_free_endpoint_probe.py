#!/usr/bin/env python3
# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: 2026 TrueToneCap contributors
r"""Bing／微软"有没有国内免密可调用的整句翻译端点"取证脚本。

为什么要有这个脚本：主循环 2026-09-21（本机，中国大陆网络）已实测
  - edge.microsoft.com/translate/auth → 404（很多库用的那个免密 JWT 入口今天不存在）
  - api-edge.cognitive.microsofttranslator.com/translate 免 token POST → 401 401001
    ⇒ 微软翻译服务本体**在国内可达**，只是要 token。
那 token 能不能免密拿到？社区库（translatepy／bing-translate-api）走的是另一条路：
从 www.bing.com/translator 的 HTML 里抓 IG／IID／key／token，再 POST ttranslatev3。
本脚本按**真实源码**构造请求，并把"通／不通"写成能一票区分的判据。

参数名来源（不是凭印象编的，均已下载上游源码逐行核对）：
  [S1] bing-translate-api 4.3.0（github.com/plainheart/bing-translate-api）src/index.js
       GET https://www.bing.com/translator
         IG   = /IG:"([^"]+)"/            IID = /data-iid="([^"]+)"/
         helper = /params_AbusePreventionHelper\s?=\s?([^\]]+\])/ → JSON [key, token, 有效期ms]
       POST https://{同一子域}/ttranslatev3?isVertical=1&&IG=…&IID=…
         form: fromLang / text / token / key / to   （头带 UA + Referer）
       → JSON [{"translations":[{"text":…}],"detectedLanguage":{…}}]
  [S2] translatepy 2.3（github.com/Animenosekai/translate）translators/bing.py+microsoft.py
       同上，但 helper 变量名是 params_RichTranslateHelper，form 多一个 isAuthv2=true；
       另一条路：POST https://www.bing.com/tfetspktok 取 Bearer → api.cognitive.microsofttranslator.com
       本脚本把这两条"源码里看到的路"都单独探一次（E1／E2），**不计入** PASS 判据。

四条陷阱（判据就是照着它们写的）：
  T1 urllib 默认**跟随 302**：POST 被重定向后会退化成 GET、丢掉 form body，
     于是 www.bing.com/ttranslatev3 看着是 "HTTP 200 + 空 body"。
     ⇒ 抓页面允许跟随并记下最终 host，翻译请求直接打**最终 host**；需要原始状态码时挂不跟随的 handler。
  T2 Bing 的真实状态码在**响应体**里（HTTP 200 + {"statusCode":205}）。
     ⇒ 所有判据比对 body 里的译文实质内容，不看 HTTP 状态码；对照组专治"200 空 body／HTML 错误页被判成成功"。
  T3 IG 疑似不参与校验（伪造 IG 仍返回译文）⇒ 免密的实际凭据只有 key+token，token 约 1 小时过期。
     ⚠ 这条是**附带观察**，不是本脚本设计出来证的，别过度解读。
  T4 同名字段塞 3 个 text 只翻第一条 ⇒ OCR 多行不能这么拼（会静默丢句）。

判据（B1–B5 全部成立才算 PASS）：
  B1 从公开 bing 页面能取到 IG／IID／key／token（只打印前 8 字符 + 长度，不落完整凭据）
  B2 免密整句 英→中：拿到非空译文，且译文含中文
  B3 免密整句 中→英：同上，反向
  B4 对照组：①同站点不存在的路径 ②把 token 改坏 ③key 改成过去时刻 —— 三者都**不得**拿到 B2 那句的译文
  B5 多行批量：一次请求塞 3 句（换行拼接），3 句译文都要出现在同一次响应里；
     同时记录"是不是一次返回 3 个条目"（[S1] 的行为是 1 个条目 + 换行保留，**不是** 3 条数组）

退出码：0 = 免密整句翻译这条路在本机确实通（B1–B5 成立）；
        1 = 不通或任一判据不成立；
        3 = 网络不可达导致无法判定（**不退 0**）。

用法：py scripts/probes/bing_free_endpoint_probe.py     （手动跑，不接进 verify.ps1）
"""

import json
import re
import sys
import time
import http.cookiejar
import urllib.error
import urllib.parse
import urllib.request

UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
      "Chrome/153.0.0.0 Safari/537.36 Edg/153.0.0.0")
TIMEOUT = 12.0
EN_SENT = "Please save the file before exiting."
ZH_SENT = "今天天气很好，我们出去玩吧"
BATCH = ["Hello world.", EN_SENT, "The user clicked Cancel."]
PAGES = ("https://www.bing.com/translator",
         "https://www.bing.com/search?q=hello",
         "https://www.bing.com/")


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None  # 让 3xx 以 HTTPError 抛出，从而能看到**原始**状态码


_JAR = http.cookiejar.CookieJar()


def _opener(nofollow=False):
    handlers = [urllib.request.HTTPCookieProcessor(_JAR)]
    if nofollow:
        handlers.append(_NoRedirect())
    return urllib.request.build_opener(*handlers)


def fetch_page(url):
    """抓页面：**允许**跟随重定向（www→cn 是 Bing 的正常地域跳转），返回最终 URL。"""
    req = urllib.request.Request(url, headers={"User-Agent": UA,
                                               "Accept-Language": "en-US,en;q=0.9"})
    t0 = time.time()
    with _opener().open(req, timeout=TIMEOUT) as r:
        return r.geturl(), r.status, r.read().decode("utf-8", "replace"), (time.time() - t0) * 1000


def post_form(url, fields, referer=None):
    """form-urlencoded POST。抛 URLError 由调用方按网络不可达处理。"""
    body = urllib.parse.urlencode(fields).encode()
    headers = {"User-Agent": UA,
               "Content-Type": "application/x-www-form-urlencoded; charset=UTF-8",
               "Accept": "application/json, text/javascript, */*; q=0.01"}
    if referer:
        headers["Referer"] = referer
        headers["Origin"] = re.match(r"https://[^/]+", referer).group(0)
    req = urllib.request.Request(url, data=body, method="POST", headers=headers)
    t0 = time.time()
    try:
        with _opener().open(req, timeout=TIMEOUT) as r:
            return (r.status, r.read().decode("utf-8", "replace"),
                    r.headers.get("content-type", ""), (time.time() - t0) * 1000)
    except urllib.error.HTTPError as e:
        # 4xx/5xx 也是"拿到了响应"，不能算成网络不可达（否则日志会说谎）
        return (e.code, e.read().decode("utf-8", "replace"),
                e.headers.get("content-type", ""), (time.time() - t0) * 1000)


def raw_status(url, method="GET"):
    """不跟随重定向地拿**原始**状态码 + Location，避免 302→200 被洗白（T1）。"""
    req = urllib.request.Request(url, method=method, headers={"User-Agent": UA})
    opener = _opener(nofollow=True)
    try:
        with opener.open(req, timeout=TIMEOUT) as r:
            return r.status, r.headers.get("Location", ""), ""
    except urllib.error.HTTPError as e:
        return e.code, e.headers.get("Location", ""), e.read().decode("utf-8", "replace")[:120]


def scrape_creds(html):
    """按 [S1] 的三个正则取四要素；helper 变量名兼容 [S2] 的 RichTranslateHelper。"""
    out = {"ig": _first(html, r'IG:"([^"]+)"'),
           "iid": _first(html, r'data-iid="([^"]+)"'),
           "key": "", "token": "", "ttl_ms": "", "helper": ""}
    for name, pat in (("AbusePreventionHelper", r'params_AbusePreventionHelper\s*=\s*([^\]]+\])'),
                      ("RichTranslateHelper", r'params_RichTranslateHelper\s*=\s*(\[[^\]]*\])')):
        m = re.search(pat, html)
        if not m:
            continue
        try:
            arr = json.loads(m.group(1))
        except json.JSONDecodeError:
            continue
        key = next((x for x in arr if isinstance(x, (int, str)) and str(x).isdigit()), "")
        tok = next((x for x in arr if isinstance(x, str) and not x.isdigit() and len(x) >= 16), "")
        ttl = next((x for x in arr if isinstance(x, int) and 1000 <= x <= 86_400_000), "")
        if key and tok:
            out.update(key=str(key), token=tok, ttl_ms=str(ttl), helper=name)
            return out
    return out


def endpoint(host, creds):
    return (f"https://{host}/ttranslatev3?isVertical=1&&IG={creds['ig']}&IID={creds['iid']}")


def translate(url, text, from_lang, to_lang, creds, extra=None):
    """一次真实请求。返回 (译文或 None, 详情串, 耗时ms)。"""
    fields = [("text", text), ("fromLang", from_lang), ("to", to_lang),
              ("key", creds["key"]), ("token", creds["token"])]
    if extra:
        fields += extra
    t0 = time.time()
    status, body, ctype, _ = post_form(url, fields, referer=creds.get("referer"))
    ms = (time.time() - t0) * 1000
    tr, note = parse_translation(body)
    return tr, f"HTTP {status} ct={ctype.split(';')[0] or '?'} {note} {ms:.0f}ms", ms


def parse_translation(body):
    """从响应体里取译文。取不到就给原因 —— 判据只看这个，不看 HTTP 状态码（T2）。"""
    stripped = body.strip()
    if not stripped:
        return None, "空body"
    if stripped[0] not in "[{":
        return None, f"非JSON({len(stripped)}B)"
    try:
        data = json.loads(stripped)
    except json.JSONDecodeError:
        return None, "JSON解析失败"
    if isinstance(data, dict):
        return None, f"体内statusCode={data.get('statusCode')}"
    if not isinstance(data, list) or not data or not isinstance(data[0], dict):
        return None, "数组为空/形状不符"
    item = data[0]
    trans = (item.get("translations") or [{}])[0].get("text", "")
    if not trans or not trans.strip():
        return None, "translations[0].text 为空"
    detected = (item.get("detectedLanguage") or {}).get("language", "-")
    return trans, f"len={len(trans)} detected={detected} items={len(data)}"


def has_cjk(s):
    return bool(re.search(r"[\u4e00-\u9fff]", s))


def has_latin_words(s):
    return bool(re.search(r"[A-Za-z]{3,}\s*[A-Za-z]", s))


def _first(html, pat):
    m = re.search(pat, html)
    return m.group(1) if m else ""


def _brief(v):
    """凭据形态一律截断：前 8 字符 + 长度，不把完整值写进日志。"""
    s = "" if v is None else str(v)
    return f"'{s[:8]}…'(len {len(s)})" if s else "取不到"


def main():
    net_fail = 0      # 网络层失败次数（URLError/超时）
    net_ok = 0        # 至少连上并拿到 HTTP 响应的次数
    results = []

    def check(name, ok, detail):
        results.append((name, bool(ok), detail))
        print(f"  {'✅' if ok else '❌'} {name}\n      {detail}")

    print("── 1) 抓页面取 IG/IID/key/token ──")
    creds = None
    referer = None
    host = None
    for page in PAGES:
        try:
            final_url, status, html, ms = fetch_page(page)
            net_ok += 1
        except (urllib.error.URLError, OSError) as e:
            net_fail += 1
            print(f"  ⚠️  {page} → 网络不可达（{type(e).__name__}: {getattr(e, 'reason', e)}）")
            continue
        c = scrape_creds(html)
        c["referer"] = final_url
        print(f"  {page}\n      → {final_url}  HTTP {status}  {ms:.0f}ms  HTML {len(html)}B")
        print(f"      IG={_brief(c['ig'])}  IID={_brief(c['iid'])}  "
              f"key={_brief(c['key'])}  token={_brief(c['token'])}  "
              f"helper=params_{c['helper'] or '?'}  tokenTTL={c['ttl_ms'] or '?'}ms")
        if c["ig"] and c["iid"] and c["key"] and c["token"] and creds is None:
            creds, referer = c, final_url
            host = re.match(r"https://([^/]+)", final_url).group(1)

    if creds is None:
        print("  ⇒ 三个候选页面都没凑齐 IG+IID+key+token")
        check("B1 取到 IG/IID/key/token", False, "见上方逐页结果")
        if net_fail and not net_ok:
            print("\n⚠️  SKIP：bing 完全不可达，无法判定")
            return 3
        return 1

    check("B1 取到 IG/IID/key/token", True,
          f"来源 {referer}；IG={_brief(creds['ig'])} IID={_brief(creds['iid'])} "
          f"key={_brief(creds['key'])} token={_brief(creds['token'])} "
          f"体内 TTL={creds['ttl_ms']}ms（key 形如毫秒时间戳，token 32 字符）")

    url = endpoint(host, creds)
    print(f"\n  翻译端点：直接打抓页面后得到的**最终子域** {host}（若打 www.bing.com 会中 T1 的招）\n"
          f"    {url}")

    # T1 取证：同一请求打 www.bing.com —— 跟随 vs 不跟随
    www_url = endpoint("www.bing.com", creds)
    try:
        st_raw, loc, peek = raw_status(www_url, "GET")
        net_ok += 1
        print(f"  [T1] GET {www_url[:58]}… 不跟随 → HTTP {st_raw} Location={loc[:40]!r}")
    except (urllib.error.URLError, OSError) as e:
        net_fail += 1
        print(f"  [T1] 原始状态码探测不可达：{e}")
    try:
        status, body, ctype, ms = post_form(www_url, [("text", EN_SENT), ("fromLang", "en"),
                                                      ("to", "zh-Hans"), ("key", creds["key"]),
                                                      ("token", creds["token"])], referer)
        net_ok += 1
        print(f"  [T1] POST www.bing.com（**跟随** 302）→ HTTP {status} "
              f"body {len(body)}B → {parse_translation(body)[1]}；"
              "这一行就是【状态码被重定向洗白】的现场")
    except (urllib.error.URLError, OSError) as e:
        net_fail += 1
        print(f"  [T1] POST www.bing.com 不可达：{e}")

    print("\n── 2) B2 免密整句 英→中 ──")
    try:
        tr_en, d_en, _ = translate(url, EN_SENT, "en", "zh-Hans", creds)
        net_ok += 1
    except (urllib.error.URLError, OSError) as e:
        net_fail += 1
        tr_en, d_en = None, f"网络不可达（{type(e).__name__}: {getattr(e, 'reason', e)}）"
    ok = bool(tr_en) and has_cjk(tr_en) and tr_en.strip() != EN_SENT.strip()
    check("B2 英→中拿到含中文的非空译文", ok,
          f"{d_en}  译文={tr_en if tr_en else '无'}")

    print("\n── 3) B3 免密整句 中→英（fromLang=auto-detect）──")
    try:
        tr_zh, d_zh, _ = translate(url, ZH_SENT, "auto-detect", "en", creds)
        net_ok += 1
    except (urllib.error.URLError, OSError) as e:
        net_fail += 1
        tr_zh, d_zh = None, f"网络不可达（{type(e).__name__}: {getattr(e, 'reason', e)}）"
    ok = bool(tr_zh) and has_latin_words(tr_zh) and not has_cjk(tr_zh) and tr_zh.strip() != ZH_SENT.strip()
    check("B3 中→英拿到英文译文（且不回显原文）", ok,
          f"{d_zh}  原文={ZH_SENT} → 译文={tr_zh if tr_zh else '无'}")

    print("\n── 4) B4 对照组（防 200 空 body／HTML 错误页／体内错误被判成成功）──")
    bogus = dict(creds)
    bogus["token"] = "X" * len(creds["token"])
    stale = dict(creds)
    stale["key"] = str(int(creds["key"]) - 86_400_000)  # key 是毫秒时间戳 → 拨回一天
    noauth = dict(creds)
    noauth["key"], noauth["token"] = "", ""
    controls = []
    for tag, cu, extra_fields in (
            ("token 改坏", bogus, None),
            ("key 拨回一天", stale, None),
            ("key+token 全不带", noauth, None),
            ("IG 伪造", dict(creds, ig="0" * 32), None)):
        try:
            tr, d, ms = translate(endpoint(host, cu), EN_SENT, "en", "zh-Hans", cu)
            net_ok += 1
        except (urllib.error.URLError, OSError) as e:
            net_fail += 1
            tr, d = None, f"网络不可达（{e}）"
        controls.append((tag, tr, d))
        print(f"  {tag}：译文={tr if tr else '无'}  ← {d}")

    path_probe = f"https://{host}/ttranslatev3_this_path_does_not_exist_zz"
    try:
        http_code, loc, peek = raw_status(path_probe, "GET")
        net_ok += 1
        leaked = parse_translation(peek or "")[0] or (EN_SENT[:10] in (peek or ""))
        check("B4a 同站点不存在的路径不给译文", not leaked,
              f"GET {path_probe} → HTTP {http_code}（不跟随重定向），"
              f"{len(peek or '')}B，前 40 字符 {peek[:40]!r} ⇒ {parse_translation(peek or '')[1]}")
    except (urllib.error.URLError, OSError) as e:
        net_fail += 1
        check("B4a 同站点不存在的路径不给译文", False, f"网络不可达（{e}）")
    for tag, tr, d in controls:
        if tag == "IG 伪造":
            print(f"      {tag} → 译文={tr if tr else '无'}："
                  "IG 疑似不参与校验（见 T3），所以**不**作为判据，只是观察")
            continue
        check(f"B4 {tag} 不给译文", tr is None, d)

    print("\n── 5) B5 多行批量（OCR 场景）──")
    b5_name = f"B5 {len(BATCH)} 句一次请求 ⇒ 同一次响应里 {len(BATCH)} 行译文都拿到（换行结构保留）"
    joined = "\n".join(BATCH)
    try:
        status, body, ctype, ms = post_form(url, [("text", joined), ("fromLang", "en"),
                                                  ("to", "zh-Hans"), ("key", creds["key"]),
                                                  ("token", creds["token"])], referer)
        net_ok += 1
        data = json.loads(body)
        items = len(data) if isinstance(data, list) else 0
        texts = [((it.get("translations") or [{}])[0]).get("text", "") for it in data] \
            if isinstance(data, list) else []
        blob = "\n".join(texts)
        print(f"      HTTP {status} {ms:.0f}ms 条目数={items} 合并译文={blob!r}")
        lines = blob.split("\n")
        cjk_lines = sum(1 for ln in lines if has_cjk(ln))
        ok = (items >= 1 and len(lines) == len(BATCH)
              and cjk_lines == len(BATCH) and all(ln.strip() for ln in lines))
        check(b5_name, ok,
              f"一次请求返回的**条目数={items}**（不是 {len(BATCH)} 条数组 ⇒ 取不到 items[i]，"
              f"只能按 \\n 切，这条**不成立**已点名）；按 \\n 切出的行数={len(lines)}/{len(BATCH)}，"
              f"其中含中文 {cjk_lines}/{len(BATCH)}")
    except (urllib.error.URLError, OSError) as e:
        net_fail += 1
        check(b5_name, False, f"网络不可达（{e}）")
    except (json.JSONDecodeError, ValueError) as e:
        check(b5_name, False, f"响应非 JSON（{e}）：{body[:80]!r}")

    try:  # T4 取证：同名 text 重复 3 次
        status, body, ctype, ms = post_form(url, [("text", BATCH[0]), ("text", BATCH[1]),
                                                  ("text", BATCH[2]), ("fromLang", "en"),
                                                  ("to", "zh-Hans"), ("key", creds["key"]),
                                                  ("token", creds["token"])], referer)
        net_ok += 1
        data = json.loads(body)
        got = [((it.get("translations") or [{}])[0]).get("text", "") for it in data] if isinstance(data, list) else []
        print(f"  [T4] 同名 text 重复 3 次：HTTP {status} {ms:.0f}ms → {got} "
              f"⇒ 只翻第一条，另外两句**静默丢失**（别这么拼）")
    except (urllib.error.URLError, OSError, json.JSONDecodeError) as e:
        net_fail += 1
        print(f"  [T4] 不可达／非 JSON：{e}")

    print("\n── 6) 源码里看到的另两条路：今天实测通不通（**不计入 PASS**，"
          "只为把\"我实测到了\"和\"我只在别人源码里看到\"分开）──")
    # E1 [S2] translatepy 的 MicrosoftTranslate：POST /tfetspktok 取 Bearer → cognitive translator
    for tag, tok_url in (("E1 www.bing.com", "https://www.bing.com/tfetspktok"),
                         ("E1 最终子域", f"https://{host}/tfetspktok")):
        try:
            code0, loc, _ = raw_status(tok_url, "POST")
            req = urllib.request.Request(tok_url, data=b"{}", method="POST",
                                         headers={"User-Agent": UA, "Content-Type": "application/json",
                                                  "Referer": referer, "Origin": "https://" + host})
            t0 = time.time()
            try:
                with _opener().open(req, timeout=TIMEOUT) as r:
                    code1, body1 = r.status, r.read().decode("utf-8", "replace")
            except urllib.error.HTTPError as e:
                code1, body1 = e.code, e.read().decode("utf-8", "replace")
            net_ok += 1
            got = "token" in body1[:400]
            print(f"  {tag} POST /tfetspktok → 不跟随 HTTP {code0}"
                  + (f"（Location→{loc[:34]}…）" if loc else "")
                  + f"；跟随 HTTP {code1} {body1[:56]!r}")
            why = "" if body1.strip() or code1 >= 400 else "（200 空 body = T1 的 302 洗白现场，真结论看另一行）"
            print(f"      判定：{'拿得到 Bearer token' if got else '拿不到 Bearer token'}"
                  f"（HTTP {code1} ⇒ [S2] 的 MicrosoftTranslate 入口今天{'通' if got else '不通'}）{why}")
        except (urllib.error.URLError, OSError) as e:
            net_fail += 1
            print(f"  {tag} POST /tfetspktok → 网络不可达（{e}）")
    # E2 流传的 api.bing.com/translator_text（[S1]/[S2] 源码里都没有这个端点，按 [S1] 的参数真 POST 一次）
    try:
        st = (f"https://api.bing.com/translator_text?IG={creds['ig']}&IID={creds['iid']}")
        status, body, ctype, ms = post_form(st, [("text", EN_SENT), ("fromLang", "en"),
                                                 ("to", "zh-Hans"), ("key", creds["key"]),
                                                 ("token", creds["token"])], referer)
        net_ok += 1
        print(f"  E2 POST {st[:56]}… → HTTP {status} {ms:.0f}ms 体内前 56 字符 {body[:56]!r}\n"
              f"      判定：{parse_translation(body)[1]} ⇒ 这条路"
              f"{'拿到译文' if parse_translation(body)[0] else '不给译文'}（源码里也没找到它，属\"流传参数\"）")
    except (urllib.error.URLError, OSError) as e:
        net_fail += 1
        print(f"  E2 api.bing.com/translator_text → 网络不可达（{e}）")

    print("\n── 7) 附带观察：同一份凭据连打 5 次（限流／captcha 的量级，不判绿）──")
    ok_n, worst = 0, 0.0
    for i in range(5):
        try:
            tr, d, ms = translate(url, f"Retry probe line {i + 1}.", "en", "zh-Hans", creds)
            net_ok += 1
            ok_n += 1 if tr else 0
            worst = max(worst, ms)
            print(f"      #{i + 1} {'拿到 ' + repr(tr) if tr else '没拿到'} ← {d}")
        except (urllib.error.URLError, OSError) as e:
            net_fail += 1
            print(f"      #{i + 1} 网络不可达：{e}")
    print(f"      ⇒ {ok_n}/5 成功，最慢 {worst:.0f}ms；"
          "真正的限流阈值（体内 429 + ShowCaptcha）不在这 5 次的射程内，别把这条当容量结论")

    if net_ok == 0 and net_fail:
        print("\n⚠️  SKIP：bing 完全不可达，无法判定（不退 0）")
        return 3

    failed = [n for n, ok, _ in results if not ok]
    print(f"\n{'❌' if failed else '✅'} {len(results) - len(failed)}/{len(results)} 条判据成立"
          + (f"（另有 {net_fail} 次请求因网络跳过）" if net_fail else ""))
    if failed:
        print("   未成立：" + " / ".join(failed))
        print("   ⇒ 免密整句翻译在本机走不通（或只走通一半），别接进产品")
        return 1
    print("   ⇒ 实测结论：本机（中国大陆网络）可用 www.bing.com/translator 抓 key+token、"
          "再 POST 同子域 /ttranslatev3 做免密整句翻译。")
    print("   ⚠ 通 ≠ 该用。接进产品前至少要认下这几笔："
          "\n     · ToS：这是网页前端自用接口，微软没授权第三方调用；OCR 文本会随请求离开本机（隐私面）。"
          "\n     · 稳定性：token 约 1h 过期（见 B1 的体内 TTL），且要先抓 ~650KB 的 translator 页面；"
          "页面一改正则就断，bing-translate-api 遇到体内 400 只会重抓一次。"
          "\n     · 限流：真实状态码在体内 —— 429 会附 ShowCaptcha（[S1] 到此为止，需要人过验证码），"
          "所以【能不能一直用】本脚本没测、也测不出来。"
          "\n     · 地域：本脚本全部证据来自 cn.bing.com（www.bing.com 在国内 302 到它），"
          "换台海外机器结论未必迁移。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
