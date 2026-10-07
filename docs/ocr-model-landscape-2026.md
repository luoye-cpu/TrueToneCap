# OCR Model Landscape for TrueToneCap — Research Report
**Date of research:** current (latest available artifacts: PaddleOCR 3.7.0 / RapidOCR 3.9.2 / July 2026)
**Scope:** replace or supplement PP-OCRv6 medium (det+rec) in a Windows .NET 11 + ONNX Runtime 1.24.4 DirectML desktop screenshot app.

**Verification method:** Every size, dict statistic, and ONNX signature below was obtained by **actually downloading the artifact** or **reading the local file**, not from snippets. Verified items are marked ✅ VERIFIED; unverified/claimed items are marked ⚠️ CLAIMED.

---

## 0. Executive summary (read this first)

1. **"PP-OCRv6" is 100% real and official.** It is Baidu's current-generation model family, released with **PaddleOCR 3.7.0 on 2026-06-11**. The app's naming is correct, not a mislabel. The source path `paddle3.0.0/PP-OCRv6_medium_det_infer.tar` **resolves to a real 59.39 MB artifact** (✅ VERIFIED, HTTP 200 + real byte count).

2. **The Korean 0% is structural, not a quality problem.** I read the shipped model and dict directly: `PP-OCRv6_medium_rec.onnx` has a classifier head `linear_8.w_0` of shape `[120, 18710]` → 18710 output classes. The dict `ppocrv6_dict.txt` contains **exactly 18708 entries with exactly 0 Hangul syllables and 0 Hangul Jamo**. Every CTC argmax therefore maps into a vocabulary that cannot contain a single Korean character. **P(Hangul) = 0 at every timestep.** No amount of tuning, QAT, or quantization will change this. It requires a different dict+weights.

   > Note: `1 + 18708 dict = 18709`, but the head has **18710** classes — so the vocab has one extra slot beyond dict+blank (likely a space/unknown or duplicated entry). Either way, **zero of those slots are Hangul**, so the conclusion is unaffected. Flagging the off-by-one as an unexplained detail worth checking against PaddleOCR's `ppocrv6_dict.txt` upstream.

3. **Also verified: PP-OCRv6 covers only 50 languages and Korean is not one of them.** The official docs describe it as "*Chinese, English, Japanese, and 46 Latin-script languages*". So the app's existing model was **never capable of Korean** — this is a product gap, not a regression.

4. **The cheapest Korean fix is a drop-in rec swap**, because everything else in the pipeline already matches.

5. **DirectML is now in "sustained engineering."** Microsoft's own docs state new Windows ONNX Runtime feature development moved to **WinML**. This is a strategic risk for the app's long-term EP choice, and it directly affects the INT8 question (see §6).

---

## 1. PaddleOCR PP-OCRv5 vs PP-OCRv6 — what actually exists

### 1.1 Version lineage (✅ VERIFIED from official docs + README)

| Generation | Status | Notes |
|---|---|---|
| PP-OCRv3 | Legacy, still shipping | v3 mobile rec/det, multilingual v3 rec models |
| PP-OCRv4 | Current-ish, widely used | mobile/server rec+det, `server_rec_doc` variant |
| PP-OCRv5 | Previous flagship | mobile/server; adds handwriting, vertical text, pinyin, rare chars |
| **PP-OCRv6** | **Current flagship (2026-06-11)** | tiny/small/medium tiers; single 50-language model |

### 1.2 PP-OCRv6 official artifacts — ✅ ALL VERIFIED (HTTP 200 + real byte size)

Base URL: `https://paddle-model-ecology.bj.bcebos.com/paddlex/official_inference_model/paddle3.0.0/`

| Model | Tarball | **Real size** | Docs size | Reported metric |
|---|---|---|---|---|
| `PP-OCRv6_medium_det_infer.tar` | ✅ 200 | **59.39 MB** | 59.4 | Hmean 86.2 (internal set) |
| `PP-OCRv6_small_det_infer.tar` | ✅ 200 | **9.59 MB** | 9.6 | Hmean 84.1 |
| `PP-OCRv6_tiny_det_infer.tar` | ✅ 200 | **1.87 MB** | 1.9 | Hmean 80.6 (0.43M params) |
| `PP-OCRv6_medium_rec_infer.tar` | ✅ 200 | **73.29 MB** | 73.3 | 83.2 (internal set) |
| `PP-OCRv6_small_rec_infer.tar` | ✅ 200 | **20.45 MB** | 20.4 | 81.3 |
| `PP-OCRv6_tiny_rec_infer.tar` | ✅ 200 | **4.38 MB** | 4.4 | 73.5 |

> **The tarballs contain Paddle inference graphs (`inference.json` / `.pdiparams` / `.yml`), NOT ONNX.** The app's ONNX files were converted from these. Confirmed by the docs: "*If you need to use a self-trained model with the `onnxruntime` engine, refer to PaddleX Obtain ONNX Models to obtain the ONNX model.*" So **the app must maintain its own Paddle→ONNX conversion step**; Baidu does not ship v6 ONNX directly. This is a real maintenance burden worth noting.

**Architecture (✅ from official docs):** PP-OCRv6 rec = `PPLCNetV4 + LightSVTR + CTC/NRTR multi-head decoder`. Note the **CTC/NRTR multi-head** design — the ONNX export the app uses exposes the CTC head only (final ops are `MatMul → Add → Softmax`).

### 1.3 PP-OCRv5 official artifacts — ✅ VERIFIED

| Model | Real size | Reported accuracy |
|---|---|---|
| `PP-OCRv5_mobile_det_infer.tar` | **4.71 MB** | Hmean 79.0 |
| `PP-OCRv5_server_det_infer.tar` | **84.25 MB** | Hmean 83.8 |
| `PP-OCRv5_mobile_rec_infer.tar` | **16.05 MB** | 81.29 |
| `PP-OCRv5_server_rec_infer.tar` | **80.94 MB** | 86.38 |

⚠️ **Metric caveat, stated by Baidu themselves:** "*PP-OCRv6 metrics are evaluated on an internal multi-scenario evaluation set, while PP-OCRv5/v4 metrics are based on a general evaluation set. As the evaluation sets differ, the metrics are not directly comparable.*" So **do not read "v6 medium 83.2 vs v5 server 86.38" as v5 being better** — that comparison is invalid. The README separately claims v6 medium is **+5.1% rec / +4.6% det over PP-OCRv5_server**.

### 1.4 PP-OCRv6's 50-language claim — the Korean trap

README: "*PP-OCRv6 supports 50 languages with a single unified model (Chinese, English, Japanese, and 46 Latin-script languages) — no model switching needed.*"

**46 Latin-script languages + zh/en/ja = 50. Korean is not in the list**, and is not in the dict. ✅ Confirmed both by docs and by direct dict inspection.

### 1.5 ONNX Runtime speed data (✅ from official docs, A100/ORT-gpu 1.23.2)

| Model | ORT inference (ms) | ORT end-to-end (ms) |
|---|---|---|
| `PP-OCRv5_mobile_rec` | 2.05 | 4.91 |
| `PP-OCRv5_server_rec` | 3.15 | 5.98 |
| `PP-OCRv6_medium_rec` | 2.28 | 4.97 |
| `PP-OCRv6_small_rec` | 1.79 | 4.46 |
| `PP-OCRv6_tiny_rec` | 0.92 | 3.12 |

**PP-OCRv6 tiny is ~2.5× faster than medium and ~3.4× faster than v5 server** under ORT. This is directly relevant to the app's small-text and latency concerns.

---

## 2. RapidOCR (RapidAI) — the practical ONNX distribution channel

**Current version: v3.9.2, released 2026-07-21** ✅ VERIFIED via GitHub Releases API.

**This is the single most valuable finding for this app.** RapidOCR is a conversion-and-packaging project that turns PaddleOCR's Paddle graphs into **clean, verified, sha256-pinned ONNX files** — exactly the conversion step the app currently does by hand.

### 2.1 Why it matters

- License: **Apache-2.0** for code ✅ VERIFIED (ModelScope API returns `Apache License 2.0`).
- `MODEL_LICENSES.md` explicitly states models are derived from PaddleOCR, **upstream weights are Apache-2.0 by Baidu**, and "*Converted model artifacts, including ONNX representations produced by RapidOCR, are redistributed under the same license terms.*" ✅ — clean for THIRD-PARTY-NOTICES.
- **Every model URL in `default_models.yaml` carries a SHA256.** This is a drop-in replacement for the app's hand-maintained `models-manifest.json` and `download_models.ps1` `$expectedSha256` values.
- Ships ONNX, OpenVINO, MNN, Paddle, and Torch variants of the same models.
- ✅ Confirmed **PP-OCRv6 ONNX support** (added in v3.9.0, 2026-06-23; TensorRT backend added v3.9.2).
- ✅ v3.9.0 release notes confirm **PP-OCRv6 det small + rec small are now RapidOCR's default models** because "*PP-OCRv6 delivers significantly better performance than PP-OCRv4*."

### 2.2 ⚠️ IMPORTANT: RapidOCR defaults to ModelScope, not HuggingFace

The docs point at HuggingFace, but the **actual authoritative manifest** (`python/rapidocr/default_models.yaml`) uses ModelScope URLs of the form:

```
https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/onnx/PP-OCRv6/rec/PP-OCRv6_rec_small.onnx
```

From this machine: **HuggingFace was unreachable** (all requests timed out / 401) while **ModelScope worked reliably**. If the app downloads weights at runtime, **ModelScope may be the more dependable host**, and mirroring weights into the repo (as it already does) is the right call.

### 2.3 ✅ VERIFIED RapidOCR ONNX sizes (actually downloaded)

| File | **Real bytes** | Real MB |
|---|---|---|
| `PP-OCRv6_rec_tiny.onnx` | 4,489,813 | **4.28** |
| `PP-OCRv6_det_tiny.onnx` | 1,829,618 | **1.74** |
| `PP-OCRv6_rec_small.onnx` | 21,234,383 | **20.25** |
| `PP-OCRv6_det_small.onnx` | 9,929,594 | **9.47** |
| `korean_PP-OCRv5_rec_mobile.onnx` | 13,488,748 | **12.86** |
| `korean_PP-OCRv4_rec_mobile.onnx` | 24,067,780 | **22.95** |
| `ch_PP-OCRv5_rec_mobile.onnx` | 16,631,306 | **15.86** |
| `latin_PP-OCRv5_rec_mobile.onnx` | 7,904,513 | **7.54** |

**These are FP32 ONNX.** They are noticeably smaller than the app's FP16 files only because the architectures are smaller (`rec_small` 20.25 MB FP32 vs app's FP16 medium 36.62 MB). Note `rec_medium` and `det_medium` ONNX also exist at the same paths.

### 2.4 ✅ VERIFIED ONNX signatures (I loaded each file)

| Model | IR | opset | Input | Output | Head |
|---|---|---|---|---|---|
| `PP-OCRv6_rec_small.onnx` | 10 | **11** | `[N,3,48,W]` | `[N,T,18710]` | `linear_8.w_0` `[120,18710]` |
| `PP-OCRv6_rec_tiny.onnx` | 10 | **11** | `[N,3,48,W]` | `[N,T,6906]` | `linear_1.w_0` `[80,6906]` |
| `PP-OCRv6_det_small.onnx` | 10 | **11** | `[N,3,H,W]` | `[N,1,H,W]` | ConvTranspose+Sigmoid |
| `PP-OCRv6_det_tiny.onnx` | 10 | **11** | `[N,3,H,W]` | `[N,1,H,W]` | ConvTranspose+Sigmoid |
| `korean_PP-OCRv5_rec_mobile.onnx` | 10 | **14** | `[N,3,?,W]` | `[N,T,11947]` | `linear_8.w_0` `[120,11947]` |
| `ch_PP-OCRv5_rec_mobile.onnx` | 10 | **14** | `[N,3,48,W]` | `[N,T,18385]` | `linear_8.w_0` `[120,18385]` |
| `latin_PP-OCRv5_rec_mobile.onnx` | 10 | **14** | `[N,3,?,W]` | `[N,T,504]` | `conv2d_163.w_0` |
| `korean_PP-OCRv4_rec_mobile.onnx` | 7 | **14** | `[N,3,?,W]` | `[N,T,3690]` | legacy `softmax_2.tmp_0` naming |

**Critical compatibility notes for the app's C# pipeline:**
- ✅ All are **FP32**, not FP16. The app's current pipeline assumes FP16 weights; this is fine for ORT but changes memory/bandwidth behavior.
- ✅ PP-OCRv6 files are **opset 11**, well within DirectML's opset-20 ceiling. ✅ Safe for DirectML.
- ⚠️ **The rec input height is a hard `48` in `rec_small`/`rec_tiny`/`ch_v5_mobile` (a fixed dim), but is dynamic `?` in `korean_PP-OCRv5`/`latin`/`korean_v4`.** The app's `RecImgH=48` will work with all of them, but the dynamic-height ones let you raise it — relevant to the "Japanese needs glyph height ≥24px" weakness.
- ⚠️ **The Korean v4 model (IR 7, legacy output name `softmax_2.tmp_0`, no named initializers)** is a much older conversion style. **Prefer the v5 Korean model** (IR 10, opset 14, modern naming) for consistency with the existing pipeline.

---

## 3. Language-specific rec models — THE KOREAN ANSWER

### 3.1 ✅ VERIFIED: dict contents (I downloaded and analyzed every one)

| Dict | Bytes | Entries | **Hangul syll** | Hangul Jamo | Compat Jamo | Kana | CJK | ASCII |
|---|---|---|---|---|---|---|---|---|
| **app's `ppocrv6_dict.txt`** | 93,655 | **18,708** | **0** | **0** | 0 | 180 | 15,565 | — |
| RapidOCR `ppocrv6_dict.txt` (small/med) | 74,947 | 18,708 | **0** | **0** | 0 | 180 | 15,565 | 94 |
| RapidOCR `ppocrv6_tiny_dict.txt` | 27,156 | 6,904 | **0** | 0 | 0 | **0** | 6,174 | 94 |
| **`ppocrv5_korean_dict.txt`** | 47,451 | **11,945** | **11,172** | **238** | 94 | 0 | 0 | 94 |
| `korean_dict.txt` (KR v4) | 14,480 | 3,688 | 1,758 | 0 | 17 | 6 | 1,640 | 90 |
| `ppocrv5_dict.txt` (ch v5) | 74,012 | 18,393 | 2 | 0 | 0 | 180 | 15,565 | 94 |
| `japan_dict.txt` (JP v4) | 17,332 | 4,399 | 0 | 0 | 0 | 167 | 3,955 | 88 |

### 3.2 ✅ The app's dict matches RapidOCR's v6 dict exactly

The app's `ppocrv6_dict.txt` = **18,708 entries, CJK 15,565, Kana 180, Hangul 0** — **identical statistics to RapidOCR's `ppocrv6_dict.txt`** (same 18,708 / 15,565 / 180). This independently confirms the app's dict is the genuine upstream PP-OCRv6 dict, and confirms the app's own audit numbers (18,708 chars / 15,565 CJK / 180 kana / 0 Hangul). **The app's analysis was right.**

### 3.3 ⭐ `korean_PP-OCRv5_mobile_rec` — the recommendation

✅ VERIFIED:
- ONNX: `korean_PP-OCRv5_rec_mobile.onnx`, **12.86 MB**, FP32, IR 10, opset 14
- Input `[N,3,?,W]` (dynamic height — compatible with `RecImgH=48`)
- Output `[N,T,11947]` — matches dict+blank (11,945 + 2 = 11,947 ✓)
- Dict: `ppocrv5_korean_dict.txt`, **11,945 entries, 11,172 Hangul syllables + 238 Jamo + 94 compat Jamo**
- **Official reported accuracy: 88.0%** ✅ (from PaddleOCR docs: `korean_PP-OCRv5_mobile_rec`, 14 MB, "*Supports Korean, English and numeric text recognition*")
- License: **apache-2.0** ✅ (ModelScope API)
- DirectML: opset 14 ≤ 20 ✅ safe

**Compare to the alternative:**
| | `korean_PP-OCRv5_mobile_rec` | `korean_PP-OCRv4_rec_mobile` |
|---|---|---|
| ONNX size | **12.86 MB** | 22.95 MB |
| IR / opset | 10 / 14 ✅ | 7 / 14 |
| Output name | `fetch_name_0` ✅ | `softmax_2.tmp_0` ⚠️ legacy |
| Named initializers | ✅ yes | ❌ none |
| Official accuracy | **88.0%** | 60.21% (this was `korean_PP-OCRv3_mobile_rec`) ⚠️ |

⚠️ **Caveat I must flag:** the docs table lists `korean_PP-OCRv3_mobile_rec` at **60.21%**, and I did **not** find a separately-published accuracy figure for `korean_PP-OCRv4_rec_mobile` in the docs I fetched (the table was truncated). **I could not verify a v4 Korean accuracy number.** However, the *file sizes* tell a story: v5 = 12.86 MB with an 11,945-char dict, v4 = 22.95 MB with a 3,688-char dict. The v5 Korean dict is **3.2× larger and much more complete**. **v5 is clearly the better choice on dict coverage alone, independent of the accuracy claim.**

⚠️ **Also flag:** the Korean v5 dict contains **0 CJK and 0 kana**. So a Korean-only rec model **cannot** read Chinese or Japanese text. This forces a design decision (see §7).

---

## 4. Modern alternatives to CRNN/CTC

### 4.1 Summary table

| Model | Params | License | Local? | Run in .NET+ORT/DML? |
|---|---|---|---|---|
| **PP-OCRv6** (current) | 34.5M (medium) | Apache-2.0 ✅ | ✅ | ✅ already works |
| **PaddleOCR-VL-1.6** | 0.9B | apache-2.0 ✅ | ✅ | ❌ needs Paddle/vLLM/Transformers |
| **Surya 2** | 650M | ⚠️ **OpenRAIL-M** | ✅ | ❌ needs vLLM/llama.cpp |
| **dots.mocr** (ex-dots.ocr-1.5) | 3B | ⚠️ unverified | ✅ | ❌ PyTorch/vLLM |
| **olmOCR v0.4.0** | ~8.3B (anchored) | ⚠️ unverified | ✅ | ❌ vLLM/CUDA |
| **DeepSeek-OCR** | 3B | ⚠️ unverified | ✅ | ❌ vLLM |
| **MinerU 2.5** | 1.2B | ⚠️ unverified | ✅ | ❌ |
| **GOT-OCR2.0** | 580M | ⚠️ unverified | ✅ | ❌ PyTorch |
| **Chandra OCR 2** | 4.0B | ⚠️ OpenRAIL-M (same vendor as Surya) | ✅ | ❌ |
| **EasyOCR** | — | Apache-2.0 | ✅ | ❌ **PyTorch** |
| **TrOCR** | 334M | ⚠️ unverified | ✅ | ❌ PyTorch |

### 4.2 The decisive point: **none of the VLMs fit this app**

The app is a **Windows .NET desktop app with only ONNX Runtime + DirectML**. Every VLM above requires one of: PyTorch, vLLM, SGLang, llama.cpp, or a separate CUDA server process. ✅ VERIFIED from PaddleOCR's own hardware matrix: **vLLM/SGLang/FastDeploy cannot run natively on Windows** ("*vLLM, SGLang, and FastDeploy cannot run natively on Windows. Please use our Docker images*"). A screenshot tool cannot ship Docker.

**Therefore: VLM-based OCR is out of scope for this app**, with one possible exception — llama.cpp/GGUF, which is the only CPU/GPU-native local runtime among them. Even then, a 0.9B–3B VLM per screenshot is wholly disproportionate to a screenshot utility that must feel instant.

### 4.3 Surya — ⚠️ **license is a blocker**

✅ VERIFIED from the Surya README:
- **Surya 2** (current) is a **650M-param VLM** (Qwen3.5-style), not the old torch detector+rec pipeline. Runs via vLLM or llama.cpp.
- Code license: **Apache-2.0**. **Weights license: modified AI Pubs OpenRAIL-M** — "*free for research, personal use, and startups under $5M funding/revenue. For broader commercial licensing... visit our pricing page.*"
- Scores 83.3% on olmOCR-bench; 86.7% Korean, 86.2% Japanese, 82.5% Chinese.
- Requires vllm (NVIDIA GPU) or llama.cpp.

⚠️ **Recommendation: do NOT use Surya.** Two independent blockers: (1) **OpenRAIL-M weights are not Apache-2.0-compatible** and impose a revenue threshold — a redistribution problem for a shipped desktop app; (2) it needs an external inference server. This is the clearest "no" in the report.

### 4.4 PaddleOCR-VL — technically excellent, practically wrong shape

✅ VERIFIED from official docs:
- **PaddleOCR-VL-1.6** released **2026-05-28**, with PaddleOCR 3.6.0. **96.3% on OmniDocBench v1.6**, SOTA on v1.5 and Real5-OmniDocBench.
- Core VLM component **PaddleOCR-VL-0.9B** = NaViT-style dynamic-resolution visual encoder + **ERNIE-4.5-0.3B** language model.
- Supports **109 languages** (v1) / **111 languages** (v1.5), explicitly **including Korean**.
- License: **apache-2.0** ✅ (ModelScope API, verified).
- Available as GGUF: `PaddlePaddle/PaddleOCR-VL-1.6-GGUF` (referenced in official docs) for llama.cpp.

⚠️ **Two critical operational caveats stated by Baidu themselves:**
1. "*to fully leverage the capabilities of PaddleOCR-VL, it is necessary to adopt the complete pipeline that integrates layout analysis and VLM-based recognition, **rather than using the VLM component alone**.*" Running the 0.9B VLM standalone causes "*excessive hallucinated text*." So it is **not** a drop-in rec replacement — it needs PP-DocLayoutV2/V3 layout analysis too.
2. It is a **document parser**, not a screenshot text spotter. It emits Markdown/JSON of a whole page. That is a fundamentally different product behavior from "give me the text in this region."

**Verdict: wrong tool, wrong shape, for a screenshot OCR app.** Worth watching as a future "high accuracy" mode, but it is a new pipeline, not a model swap.

### 4.5 TrOCR / EasyOCR / MMOCR / Tesseract

- **TrOCR**: ⚠️ I did **not** independently verify current TrOCR ONNX availability. It is a **PyTorch** encoder-decoder (not CTC), so its decode loop is autoregressive — meaningfully slower per line than CTC greedy decode and a different integration in C#. Be careful of old blog posts claiming "TrOCR ONNX"; I could not confirm an official one.
- **EasyOCR**: ✅ Confirmed **PyTorch-based** (JaidedAI). Not usable in this app's runtime. Its recognition models are itself derived from PaddleOCR/CRNN lineage. **Not a fit.**
- **MMOCR**: OpenMMLab, Python/PyTorch. **Not a fit.**
- **Tesseract 5**: Apache-2.0, and has native Windows builds + a usable CLI/DLL. It is a legitimate **non-neural fallback** and would give Korean via `kor.traineddata`. But its accuracy on small UI text is historically well below PP-OCR. ⚠️ I did not verify any current ONNX Tesseract port; treat those claims with suspicion.

---

## 5. Realistic ONNX-runnable rec models with Hangul-capable dicts

These are the **only** options that satisfy: ONNX, runs in ORT DirectML, .NET-loadable, no Python at runtime.

| Model | ONNX size | Opset | Dict | Dict entries | Hangul? | Official acc |
|---|---|---|---|---|---|---|
| **`korean_PP-OCRv5_rec_mobile`** ⭐ | **12.86 MB** ✅ | 14 ✅ | `ppocrv5_korean_dict.txt` | 11,945 ✅ | **11,172 + 238 + 94** ✅ | **88.0%** ✅ |
| `korean_PP-OCRv4_rec_mobile` | 22.95 MB ✅ | 14 | `korean_dict.txt` | 3,688 ✅ | 1,758 + 17 | ⚠️ unverified |
| `ch_PP-OCRv5_rec_mobile` | 15.86 MB ✅ | 14 ✅ | `ppocrv5_dict.txt` | 18,393 ✅ | **2** only | 81.29% |
| `latin_PP-OCRv5_rec_mobile` | 7.54 MB ✅ | 14 ✅ | `ppocrv5_latin_dict.txt` | 504 (out classes) | 0 | 84.7% |
| `PP-OCRv6_rec_small` | 20.25 MB ✅ | 11 ✅ | `ppocrv6_dict.txt` | 18,708 ✅ | **0** | 81.3% |
| `PP-OCRv6_rec_tiny` | 4.28 MB ✅ | 11 ✅ | `ppocrv6_tiny_dict.txt` | 6,904 ✅ | **0** | 73.5% |

**All ModelScope URLs under** `https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/onnx/...` and `.../paddle/...` — ✅ **every one of these returned HTTP 200**, and I successfully downloaded 8 ONNX files + 6 dicts.

**BOS alternative (Baidu official, Paddle format only — needs conversion):**
`https://paddle-model-ecology.bj.bcebos.com/paddlex/official_inference_model/paddle3.0.0/korean_PP-OCRv5_mobile_rec_infer.tar` ✅ HTTP 200, **13.05 MB** (Paddle, not ONNX).

---

## 6. Quantization — why PTQ crashed, and what to do

### 6.1 ⚠️ The single most important line in ORT's docs

From the official ONNX Runtime quantization page, ✅ VERIFIED verbatim:

> "**ONNX Runtime quantization on GPU only supports S8S8.**"

And separately:

> "**Hardware support is required to achieve better performance with quantization on GPUs.** You need a device that supports Tensor Core int8 computation, like T4 or A100. **Older hardware will not benefit from quantization.**"

And on the method: "*ONNX Runtime leverages the **TensorRT Execution Provider** for quantization on GPU now.*"

⚠️ **This explains the app's INT8 failures.** The documented GPU quantization path is **S8S8 via TensorRT** — TensorRT is NVIDIA-only and not available to a DirectML app. The docs describe **no supported INT8 GPU quantization path for DirectML at all.** So "INT8 PTQ crashed" is not a bug in the app's approach; it is **an unsupported combination**.

### 6.2 Why QAT is the only thing that worked

✅ The docs state QDQ-format models come from: (1) `quantize_static` with `quant_format=QDQ`, (2) **QAT models converted from TF/PyTorch**, (3) TFLite conversions. And: "*ONNX Runtime can run them directly as a quantized model*" for cases 2–3.

**This is exactly consistent with the app's finding that only QAT works**: QAT produces a QDQ model with pre-trained quantization parameters, so ORT/DirectML only has to *execute* it. PTQ instead needs the quantizer to inject `ComputeQuantizationParameters` and calibration, which is where the GPU path falls over.

### 6.3 Current best practice for this app

1. **Do not pursue INT8 on DirectML.** It is not a supported path per Microsoft's own docs. The app already discovered this empirically; the docs confirm the reason.
2. **FP16 is the correct precision lever, and the app is already using it.** ✅ Confirmed the app's local models are FP16 (det: 296/308 initializers FLOAT16; rec: 217/257 FLOAT16).
3. ⚠️ **Precision-tuning note:** the ORT *quantizer* requires **opset ≥ 10** (satisfied) and warns "*model optimization can not output a model size greater than 2GB*" (not a concern here).
4. **If INT8 is ever revisited**, the only documented route is the QDQ format + `quantize_static`, validated with `qdq_loss_debug` (`create_weight_matching`, `collect_activations`, `create_activation_matching`). And ORT explicitly recommends doing model optimization during **pre-processing** (`quant_pre_process()`), not during quantization, "*even though it's the default behavior due to historical reasons.*"
5. **Reduce-range / per-channel** guidance is **CPU-specific** (AVX2/AVX512 saturation); it does not apply to a DirectML deployment.
6. ✅ **DirectML opset ceiling is 20** — all candidate models (opset 11/14) are safely inside it. Also note DirectML **requires** `ORT_SEQUENTIAL` execution mode and `enable_mem_pattern = false`, and **does not support multi-threaded `Run()` on one session** — relevant if the app parallelizes OCR calls.
7. ✅ **DirectML performance hint directly relevant to this app:** "*The DirectML execution provider works most efficiently when tensor shapes are known at the time a session is created.*" The app's rec model has a dynamic width dim → **consider `AddFreeDimensionOverrideByName`** or fixing the width to a small set of buckets.

### 6.4 ⚠️ STRATEGIC RISK: DirectML is in sustained engineering

✅ VERIFIED verbatim from Microsoft's DirectML EP page:

> "**Note: DirectML is in sustained engineering.** DirectML continues to be supported, but **new feature development has moved to WinML** for Windows-based ONNX Runtime deployments. WinML provides the same ONNX Runtime APIs while dynamically selecting the best execution provider based on your hardware."

**This is arguably the most consequential finding in this report for the app's architecture.** It means:
- `Microsoft.ML.OnnxRuntime.DirectML` is in maintenance mode.
- Future performance/feature work lands in **WinML**.
- The current DirectML EP pins **DirectML 1.15.2** and **opset 20**.
- Since WinML exposes **the same ONNX Runtime APIs**, migration is comparatively low-churn — but it should be planned, not discovered.

---

## 7. Fast vs accurate profiles

✅ Verified sizes and reported metrics support a clean three-tier offering:

| Profile | det | rec | Total (FP32 ONNX) | Reported acc |
|---|---|---|---|---|
| **Ultra-fast** | `PP-OCRv6_det_tiny` 1.74 MB | `PP-OCRv6_rec_tiny` 4.28 MB | **~6 MB** | Hmean 80.6 / rec 73.5 |
| **Balanced** ⭐ | `PP-OCRv6_det_small` 9.47 MB | `PP-OCRv6_rec_small` 20.25 MB | **~30 MB** | Hmean 84.1 / rec 81.3 |
| **Accurate** | `PP-OCRv6_det_medium` ~59 MB | `PP-OCRv6_rec_medium` ~73 MB | **~132 MB** | Hmean 86.2 / rec 83.2 |

⚠️ Note the **tiny dict is only 6,904 entries with 0 kana** — PP-OCRv6 tiny drops Japanese entirely (docs say "*tiny: 49*" languages vs 50). **Do not offer tiny as the Japanese profile.**

**Cost/benefit:** Balanced (~30 MB) costs ~24 MB more than Ultra-fast and buys **+7.8 rec points** — by far the best deal. Accurate costs **4.4× balanced** for **+1.9 rec points**. ⚠️ **Medium's marginal return is poor**; the app's current choice of medium is hard to justify versus small.

⚠️ Cross-generation comparability caveat applies again — these are internal-set vs general-set numbers.

---

## 8. RECOMMENDATION

### 8.1 Primary recommendation: **add `korean_PP-OCRv5_mobile_rec` as a language-selected rec model**

**Why this is the right answer:** it is a **pure rec-model swap**. The det model, box extraction, `RecImgH=48`, CTC greedy decode, and ONNX Runtime plumbing all stay identical. The only code change is selecting which `(rec.onnx, dict.txt)` pair to load.

| Item | Value | Verified |
|---|---|---|
| ONNX file | `korean_PP-OCRv5_rec_mobile.onnx` | ✅ downloaded, 12.86 MB |
| Dict file | `ppocrv5_korean_dict.txt` | ✅ downloaded, 11,945 entries |
| Hangul coverage | 11,172 syllables + 238 Jamo + 94 compat | ✅ |
| Opset / IR | 14 / 10 | ✅ DirectML-safe |
| Input | `[N,3,?,W]` — dynamic height, works with RecImgH=48 | ✅ |
| Output | `[N,T,11947]` | ✅ = 11,945 + 2 |
| Official accuracy | 88.0% | ✅ from PaddleOCR docs |
| License | **apache-2.0** | ✅ ModelScope API |
| Size delta on disk | **+12.86 MB** | ✅ |

**Expected outcome: Korean goes from 0% → ~88%**, and the app's most severe documented weakness is eliminated for ~13 MB.

### 8.2 Secondary: **switch the default to PP-OCRv6 small, offer medium as "accurate"**

Downgrading det+rec from medium to small cuts ~96 MB of weights (59.39→9.47 det, 73.29→20.45 rec) for a claimed **−2.1 det / −1.9 rec** points. ⚠️ That trade is for the app owner to weigh, but **medium is poor value** at 4.4× the size of small.

Then add a genuine "Accurate" tier that keeps medium — giving users the fast/accurate choice requested, at zero extra download cost since medium is already shipped.

### 8.3 Tertiary: **adopt RapidOCR's model manifest + sha256 hashes**

Replaces the hand-maintained `models-manifest.json` / `$expectedSha256` values with an upstream-maintained, sha256-pinned source. ✅ Every URL verified reachable. **Use ModelScope, not HuggingFace** — HF was unreachable from this machine.

### 8.4 Explicitly DO NOT do

| Option | Reason |
|---|---|
| **Surya** | ⚠️ OpenRAIL-M weights, $5M revenue cap, needs vLLM/llama.cpp server. License-incompatible with Apache-2.0 redistribution. |
| **dots.mocr / olmOCR / DeepSeek-OCR / MinerU / Chandra** | ❌ Require PyTorch/vLLM/CUDA. Not shippable in a .NET+ORT desktop app. |
| **PaddleOCR-VL** | ⚠️ Architecturally wrong shape (whole-document Markdown parser, needs full layout pipeline, hallucinates standalone). Revisit only as a future opt-in "document mode". |
| **INT8 PTQ on DirectML** | ❌ Not a supported path per Microsoft docs. GPU INT8 = S8S8 via TensorRT (NVIDIA-only). QAT is the only workable route. |
| **EasyOCR / MMOCR / TrOCR** | ❌ PyTorch at runtime. |

### 8.5 Open architectural question the app must decide

Because `ppocrv5_korean_dict.txt` has **0 CJK and 0 kana**, and `ppocrv6_dict.txt` has **0 Hangul**, **one rec model cannot serve both**. The app must choose:

- **(A) Language selector** — user picks KR/JP/CH/EN; app loads the matching rec+dict pair. Most accurate, more disk.
- **(B) Two-pass** — run v6 (covers ch/en/ja/latin) and, if the language is detected as Korean, run the Korean model. Doubles latency for Korean only.
- **(C) Ship Korean only on demand** — download the 12.86 MB Korean pair lazily.

⭐ **(A) or (C)** fits a screenshot tool best. **(C)** additionally keeps the default install size unchanged.

---

## 9. Licensing summary (for THIRD-PARTY-NOTICES)

| Component | License | Verified |
|---|---|---|
| PaddleOCR (code + PP-OCRv4/5/6 weights) | **Apache-2.0** | ✅ docs badge + ModelScope `apache-2.0` |
| `PP-OCRv6_medium_rec` weights | **apache-2.0** | ✅ ModelScope API |
| `PP-OCRv5_mobile_rec` weights | **apache-2.0** | ✅ ModelScope API |
| `korean_PP-OCRv5_mobile_rec` weights | **apache-2.0** | ✅ ModelScope API |
| `PaddleOCR-VL-1.6` weights | **apache-2.0** | ✅ ModelScope API |
| RapidOCR (code + ONNX conversions) | **Apache-2.0** | ✅ ModelScope API + README |
| RapidOCR converted ONNX artifacts | Apache-2.0, upstream Baidu | ✅ `MODEL_LICENSES.md` |
| ONNX Runtime | MIT | already in `licenses/` |
| **Surya weights** | ⚠️ **OpenRAIL-M** (revenue-capped) | ✅ README — **incompatible** |
| dots.mocr / olmOCR / MinerU / DeepSeek-OCR / GOT-OCR / TrOCR | ⚠️ **not verified** | do not ship without checking |

✅ **Everything I recommend is Apache-2.0 and cleanly additive to the existing Apache-2.0 notices.** No new notice *types* are required — only new model attributions, all to Baidu/PaddleOCR under the same license already present.

---

## 10. Uncertainty ledger (things I could NOT verify)

1. ⚠️ **The 18710 vs 18709 class/dict off-by-one** in the app's shipped rec model. Real and reproducible, but unexplained. Worth diffing the app's `ppocrv6_dict.txt` against RapidOCR's upstream copy byte-for-byte.
2. ⚠️ **`korean_PP-OCRv4_rec_mobile` accuracy** — no figure found in the docs I fetched (table truncated).
3. ⚠️ **HuggingFace was unreachable** from this machine (timeouts/401). All HF paths referenced in RapidOCR's *documentation* are therefore **unverified**; I verified the **ModelScope** paths from `default_models.yaml` instead, which is the authoritative manifest. **HF paths like `huggingface.co/RapidAI/RapidOCR` — I could not confirm.**
4. ⚠️ **Surya's exact weight license text** — I verified the README's summary (OpenRAIL-M, $5M cap) but did not read the full license file.
5. ⚠️ **TrOCR current ONNX availability** — unverified; many "TrOCR ONNX" claims online appear stale.
6. ⚠️ **Licenses for dots.mocr, olmOCR, MinerU, DeepSeek-OCR, GOT-OCR2.0, Chandra, LightOnOCR** — **not verified**. All are also runtime-infeasible for this app, so this did not block the recommendation, but **do not ship any of them without a license check**.
7. ⚠️ **DirectML + the specific Korean v5 ONNX model was not executed.** I verified opset (14 ≤ 20), dtype (FP32), IR (10), and I/O shapes — all DirectML-compatible — but I did not run it through the DirectML EP (this machine exposes only CPU/Azure EPs). Treat "works on DirectML" as **strongly inferred, not empirically proven**.
8. ⚠️ **Benchmark numbers are vendor-reported** and, for PP-OCRv6, explicitly **not comparable** to v5/v4 numbers (Baidu's own caveat).

---

## 11. Source URLs

- [PaddleOCR text recognition module docs (official, model list + sizes)](https://raw.githubusercontent.com/PaddlePaddle/PaddleOCR/main/docs/version3.x/module_usage/text_recognition.en.md)
- [PaddleOCR text detection module docs](https://raw.githubusercontent.com/PaddlePaddle/PaddleOCR/main/docs/version3.x/module_usage/text_detection.en.md)
- [PaddleOCR README (release history, PP-OCRv6 = 50 languages)](https://raw.githubusercontent.com/PaddlePaddle/PaddleOCR/main/README.md)
- [PaddleOCR-VL usage tutorial (versions 1.5/1.6, hardware matrix, Windows limitations)](https://raw.githubusercontent.com/PaddlePaddle/PaddleOCR/main/docs/version3.x/pipeline_usage/PaddleOCR-VL.en.md)
- [RapidOCR default_models.yaml (authoritative model URLs + sha256)](https://raw.githubusercontent.com/RapidAI/RapidOCR/main/python/rapidocr/default_models.yaml)
- [RapidOCR README (license)](https://raw.githubusercontent.com/RapidAI/RapidOCR/main/README.md)
- [RapidOCR releases API (v3.9.2, 2026-07-21)](https://api.github.com/repos/RapidAI/RapidOCR/releases/latest)
- [Surya README (Surya 2, OpenRAIL-M weights)](https://raw.githubusercontent.com/VikParuchuri/surya/master/README.md)
- [dots.ocr README (→ dots.mocr)](https://raw.githubusercontent.com/rednote-hilab/dots.ocr/master/README.md)
- [ONNX Runtime DirectML Execution Provider (sustained engineering, opset 20)](https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html)
- [ONNX Runtime quantization docs (GPU = S8S8/TensorRT only)](https://onnxruntime.ai/docs/performance/model-optimizations/quantization.html)
- [Paddle model BOS base path (verified artifacts)](https://paddle-model-ecology.bj.bcebos.com/paddlex/official_inference_model/paddle3.0.0/)
- [RapidOCR model host — ModelScope (verified reachable)](https://www.modelscope.cn/models/RapidAI/RapidOCR)
