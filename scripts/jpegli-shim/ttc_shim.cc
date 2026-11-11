// TrueToneCap jpegli C-ABI shim。
//
// 设计要点（2026-11-12，与 cjpegli.exe 的逐字节一致性是硬门禁）：
//   · 不复刻任何编码逻辑 —— 输入像素在内存里包一层 PPM/PGM 容器，交给上游
//     jpegli::extras::DecodeBytes 解出 PackedPixelFile（与 cjpegli.exe 从磁盘
//     读 PPM 得到的结构逐字段相同），再交给同一份 EncodeJpeg。差异面为零。
//   · ICC 走库原生路径：ppf.icc + primary=kIccIsPrimary ⇒ EncodeJpeg 内部
//     jpegli_write_icc_profile 按 ICC.1 规范分块写 APP2（取代产品里的 C# 手工注入）。
//   · 参数默认值取 JpegSettings{}，与 cjpegli.exe 未传参时的默认一致
//     （adaptive quantization 开、optimize_coding 开、std_quant 关）。
//   · 输出缓冲在本 DLL 内 malloc，必须用 ttc_jpegli_free 归还（同 CRT）。
//   · 异常不得跨 ABI 边界：EncodeJpeg 内部已用 setjmp 兜住 jpegli 错误，
//     这里再包一层 try/catch 防御性兜底。

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

#include "lib/extras/dec/decode.h"
#include "lib/extras/enc/jpegli.h"
#include "lib/extras/packed_image.h"

using jpegli::extras::DecodeBytes;
using jpegli::extras::EncodeJpeg;
using jpegli::extras::JpegSettings;
using jpegli::extras::PackedPixelFile;

namespace {

char g_err[512];

void SetErr(const char* msg) {
  std::snprintf(g_err, sizeof(g_err), "%s", msg);
}

// 内存 PPM/PGM 容器 —— 头部格式与产品 JpegLiNative.WriteBgraToPpm /
// EncodeGray 的磁盘形态逐字节相同（"P6\n{w} {h}\n255\n" / P5）。
std::vector<uint8_t> BuildPnm(bool gray, int w, int h, const uint8_t* px) {
  char hdr[64];
  int n = std::snprintf(hdr, sizeof(hdr), "%s\n%d %d\n255\n",
                        gray ? "P5" : "P6", w, h);
  std::vector<uint8_t> buf;
  buf.reserve(static_cast<size_t>(n) +
              static_cast<size_t>(w) * h * (gray ? 1 : 3));
  buf.insert(buf.end(), hdr, hdr + n);
  buf.insert(buf.end(), px, px + static_cast<size_t>(w) * h * (gray ? 1 : 3));
  return buf;
}

int DoEncode(const std::vector<uint8_t>& pnm, const uint8_t* icc,
             size_t icc_len, JpegSettings settings, uint8_t** out,
             size_t* out_len) {
  *out = nullptr;
  *out_len = 0;

  PackedPixelFile ppf;
  if (!DecodeBytes(jpegli::Bytes(pnm.data(), pnm.size()),
                   jpegli::extras::ColorHints{}, &ppf)) {
    SetErr("DecodeBytes(PNM) failed");
    return -2;
  }
  if (icc != nullptr && icc_len > 0) {
    ppf.icc.assign(icc, icc + icc_len);
    ppf.primary_color_representation = PackedPixelFile::kIccIsPrimary;
  }

  std::vector<uint8_t> jpeg;
  if (!EncodeJpeg(ppf, settings, /*pool=*/nullptr, &jpeg)) {
    SetErr("EncodeJpeg failed");
    return -3;
  }
  uint8_t* buf = static_cast<uint8_t*>(std::malloc(jpeg.size()));
  if (buf == nullptr) {
    SetErr("out of memory");
    return -4;
  }
  std::memcpy(buf, jpeg.data(), jpeg.size());
  *out = buf;
  *out_len = jpeg.size();
  return 0;
}

}  // namespace

extern "C" {

// 彩色编码。rgb = 紧凑 RGB24（每像素 3 字节，行无填充）。
// chroma: "444"/"440"/"422"/"420"（NULL = 默认 444）。
// progressive_level: 0 = Baseline(SOF0, 增益图底图), 2 = Progressive(默认)。
// xyb: 0/1 —— 色彩空间开关（2026-11-12 用户拍板的「色彩空间」UI 档）。
// icc/icc_len: 可选 ICC profile；给定时由库原生嵌入 APP2。
// 成功返回 0，失败返回负数（可调 ttc_jpegli_error 取消息）。
__declspec(dllexport) int ttc_jpegli_encode_rgb(
    const uint8_t* rgb, int w, int h, float distance, const char* chroma,
    int progressive_level, int xyb, const uint8_t* icc, size_t icc_len,
    uint8_t** out, size_t* out_len) {
  g_err[0] = '\0';
  try {
    if (rgb == nullptr || w <= 0 || h <= 0 || out == nullptr ||
        out_len == nullptr) {
      SetErr("bad arguments");
      return -1;
    }
    if (distance < 0.0f || distance > 25.0f) {
      SetErr("distance out of range [0,25]");
      return -1;
    }
    JpegSettings settings;
    settings.distance = distance;
    if (chroma != nullptr && chroma[0] != '\0') {
      settings.chroma_subsampling = chroma;
    }
    settings.progressive_level = progressive_level;
    settings.xyb = (xyb != 0);
    return DoEncode(BuildPnm(false, w, h, rgb), icc, icc_len, settings, out,
                    out_len);
  } catch (...) {
    SetErr("unexpected exception in encode_rgb");
    return -5;
  }
}

// 灰度编码（增益图层，真 1 分量 YCbCr400）。
__declspec(dllexport) int ttc_jpegli_encode_gray(
    const uint8_t* gray, int w, int h, float distance, int progressive_level,
    int xyb, uint8_t** out, size_t* out_len) {
  g_err[0] = '\0';
  try {
    if (gray == nullptr || w <= 0 || h <= 0 || out == nullptr ||
        out_len == nullptr) {
      SetErr("bad arguments");
      return -1;
    }
    if (distance < 0.0f || distance > 25.0f) {
      SetErr("distance out of range [0,25]");
      return -1;
    }
    JpegSettings settings;
    settings.distance = distance;
    settings.progressive_level = progressive_level;
    settings.xyb = (xyb != 0);
    return DoEncode(BuildPnm(true, w, h, gray), nullptr, 0, settings, out,
                    out_len);
  } catch (...) {
    SetErr("unexpected exception in encode_gray");
    return -5;
  }
}

__declspec(dllexport) void ttc_jpegli_free(void* p) { std::free(p); }

__declspec(dllexport) const char* ttc_jpegli_error(void) { return g_err; }

__declspec(dllexport) const char* ttc_jpegli_version(void) { return "0.12.0-shim1"; }

}  // extern "C"
