// ttc_jpegli shim ↔ cjpegli.exe 逐字节对照器（集成前硬门禁）。
//
// 用法: ttc_selftest.exe <in.ppm> <cjpegli.exe> [profile.icm]
//
// 门禁: 同一 PPM/PGM 输入、同一参数，shim 输出与 cjpegli.exe 输出必须
// 逐字节相同（两者走同一份编译出来的上游代码，差异只可能来自 shim 的
// 输入构造或参数映射错误）。ICC 路径无法与 cjpegli PPM 输入对照
// （PPM 载不下 ICC），改验证: 输出里 APP2 分块链重组后与输入 profile
// 逐字节相同。

#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include <windows.h>

typedef int (*EncodeRgbFn)(const uint8_t*, int, int, float, const char*, int,
                           int, const uint8_t*, size_t, uint8_t**, size_t*);
typedef int (*EncodeGrayFn)(const uint8_t*, int, int, float, int, int,
                            uint8_t**, size_t*);
typedef void (*FreeFn)(void*);
typedef const char* (*ErrFn)(void);
typedef const char* (*VerFn)(void);

struct Ppm {
  int w = 0, h = 0;
  std::vector<uint8_t> rgb;  // RGB24
};

static bool ReadFileBytes(const std::string& path, std::vector<uint8_t>* out) {
  FILE* f = std::fopen(path.c_str(), "rb");
  if (f == nullptr) return false;
  std::fseek(f, 0, SEEK_END);
  long len = std::ftell(f);
  std::fseek(f, 0, SEEK_SET);
  out->resize(static_cast<size_t>(len));
  size_t rd = std::fread(out->data(), 1, out->size(), f);
  std::fclose(f);
  return rd == out->size();
}

static bool WriteFileBytes(const std::string& path, const std::vector<uint8_t>& d) {
  FILE* f = std::fopen(path.c_str(), "wb");
  if (f == nullptr) return false;
  size_t wr = std::fwrite(d.data(), 1, d.size(), f);
  std::fclose(f);
  return wr == d.size();
}

// 解析 P6 PPM（容忍注释与任意空白；本工具产出的文件形态固定，但稳一点）。
static bool ParsePpm(const std::vector<uint8_t>& d, Ppm* p) {
  size_t pos = 0;
  auto next_token = [&](std::string* tok) -> bool {
    tok->clear();
    while (pos < d.size()) {
      uint8_t c = d[pos];
      if (c == '#') {
        while (pos < d.size() && d[pos] != '\n') ++pos;
      } else if (c == ' ' || c == '\t' || c == '\r' || c == '\n') {
        ++pos;
      } else {
        break;
      }
    }
    while (pos < d.size()) {
      uint8_t c = d[pos];
      if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '#') break;
      tok->push_back(static_cast<char>(c));
      ++pos;
    }
    return !tok->empty();
  };
  std::string tok;
  if (!next_token(&tok) || tok != "P6") return false;
  if (!next_token(&tok)) return false;
  p->w = std::atoi(tok.c_str());
  if (!next_token(&tok)) return false;
  p->h = std::atoi(tok.c_str());
  if (!next_token(&tok)) return false;
  if (std::atoi(tok.c_str()) != 255) return false;
  if (pos >= d.size()) return false;
  ++pos;  // 单个空白分隔符
  size_t need = static_cast<size_t>(p->w) * p->h * 3;
  if (d.size() - pos < need) return false;
  p->rgb.assign(d.begin() + pos, d.begin() + pos + need);
  return p->w > 0 && p->h > 0;
}

static void WritePnm(const std::string& path, bool gray, int w, int h,
                     const uint8_t* px) {
  FILE* f = std::fopen(path.c_str(), "wb");
  if (f == nullptr) return;
  std::fprintf(f, gray ? "P5\n%d %d\n255\n" : "P6\n%d %d\n255\n", w, h);
  std::fwrite(px, 1, static_cast<size_t>(w) * h * (gray ? 1 : 3), f);
  std::fclose(f);
}

static int RunCjpegli(const std::string& exe, const std::string& args) {
  // ⚠ 不要给 exe 路径加引号：system() → cmd /c 会剥掉首尾引号导致解析错乱。
  // 本工具链路径无空格，直接裸写；文件路径的引号保留在 args 里（非首位）。
  std::string cmd = exe + " " + args + " 2>NUL";
  int rc = std::system(cmd.c_str());
  return rc == 0 ? 0 : -1;
}

// 从 JPEG 里重组 APP2 ICC 分块链（ICC.1 规范读法，与产品解码端同口径）。
static bool ExtractIccFromJpeg(const std::vector<uint8_t>& j,
                               std::vector<uint8_t>* icc) {
  icc->clear();
  int last_seq = 0, total = -1;
  size_t pos = 2;  // SOI
  while (pos + 4 <= j.size()) {
    if (j[pos] != 0xFF) return false;
    uint8_t marker = j[pos + 1];
    if (marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD7)) { ++pos; continue; }
    if (marker == 0xD9) break;
    size_t seg_len = (static_cast<size_t>(j[pos + 2]) << 8) | j[pos + 3];
    if (seg_len < 2 || pos + 2 + seg_len > j.size()) return false;
    if (marker == 0xE2 && seg_len > 16 &&
        std::memcmp(&j[pos + 4], "ICC_PROFILE", 12) == 0) {
      int seq = j[pos + 16], cnt = j[pos + 17];
      if (total == -1) total = cnt;
      if (cnt != total || seq != last_seq + 1) return false;
      icc->insert(icc->end(), j.begin() + pos + 18, j.begin() + pos + 2 + seg_len);
      last_seq = seq;
    }
    if (marker == 0xDA) break;  // SOS 之后不再扫
    pos += 2 + seg_len;
  }
  return total >= 1 && last_seq == total && !icc->empty();
}

struct CaseResult {
  bool ok = false;
  size_t shim_size = 0, ref_size = 0;
  double shim_ms = 0, ref_ms = 0;
  size_t first_diff = 0;
  std::string note;
};

static CaseResult CompareCase(const std::string& name, const std::string& cjpegli,
                              const std::string& args,
                              EncodeRgbFn enc_rgb, EncodeGrayFn enc_gray,
                              FreeFn fre, ErrFn errf, bool gray,
                              const uint8_t* px, int w, int h, float dist,
                              const char* chroma, int prog, int xyb,
                              const uint8_t* icc, size_t icc_len,
                              const std::string& ref_jpg) {
  CaseResult r;
  std::vector<uint8_t> ref;
  auto t0 = std::chrono::steady_clock::now();
  if (RunCjpegli(cjpegli, args) != 0) {
    r.note = "cjpegli run failed";
    return r;
  }
  auto t1 = std::chrono::steady_clock::now();
  if (!ReadFileBytes(ref_jpg, &ref)) {
    r.note = "cjpegli output missing";
    return r;
  }
  r.ref_ms = std::chrono::duration<double, std::milli>(t1 - t0).count();
  r.ref_size = ref.size();

  uint8_t* out = nullptr;
  size_t out_len = 0;
  t0 = std::chrono::steady_clock::now();
  int rc = gray ? enc_gray(px, w, h, dist, prog, xyb, &out, &out_len)
                : enc_rgb(px, w, h, dist, chroma, prog, xyb, icc, icc_len,
                          &out, &out_len);
  t1 = std::chrono::steady_clock::now();
  r.shim_ms = std::chrono::duration<double, std::milli>(t1 - t0).count();
  if (rc != 0) {
    r.note = std::string("shim error ") + std::to_string(rc) + ": " + errf();
    if (out) fre(out);
    return r;
  }
  r.shim_size = out_len;
  if (out_len != ref.size()) {
    r.note = "size mismatch";
  } else {
    const uint8_t* a = out;
    const uint8_t* b = ref.data();
    for (size_t i = 0; i < out_len; ++i) {
      if (a[i] != b[i]) { r.first_diff = i; break; }
    }
    r.ok = r.first_diff == 0;
    if (!r.ok) r.note = "byte diff @" + std::to_string(r.first_diff);
  }
  fre(out);
  return r;
}

int main(int argc, char** argv) {
  if (argc < 3) {
    std::fprintf(stderr, "usage: ttc_selftest <in.ppm> <cjpegli.exe> [icm]\n");
    return 2;
  }
  HMODULE dll = LoadLibraryA("ttc_jpegli.dll");
  if (dll == nullptr) {
    std::fprintf(stderr, "LoadLibraryA(ttc_jpegli.dll) failed: %lu\n",
                 GetLastError());
    return 2;
  }
  auto enc_rgb = reinterpret_cast<EncodeRgbFn>(GetProcAddress(dll, "ttc_jpegli_encode_rgb"));
  auto enc_gray = reinterpret_cast<EncodeGrayFn>(GetProcAddress(dll, "ttc_jpegli_encode_gray"));
  auto fre = reinterpret_cast<FreeFn>(GetProcAddress(dll, "ttc_jpegli_free"));
  auto errf = reinterpret_cast<ErrFn>(GetProcAddress(dll, "ttc_jpegli_error"));
  auto verf = reinterpret_cast<VerFn>(GetProcAddress(dll, "ttc_jpegli_version"));
  if (!enc_rgb || !enc_gray || !fre || !errf || !verf) {
    std::fprintf(stderr, "GetProcAddress failed\n");
    return 2;
  }
  std::printf("shim version: %s\n", verf());

  std::vector<uint8_t> ppm_bytes;
  if (!ReadFileBytes(argv[1], &ppm_bytes)) {
    std::fprintf(stderr, "cannot read %s\n", argv[1]);
    return 2;
  }
  Ppm ppm;
  if (!ParsePpm(ppm_bytes, &ppm)) {
    std::fprintf(stderr, "bad PPM\n");
    return 2;
  }
  std::printf("input: %dx%d\n", ppm.w, ppm.h);

  const std::string cjpegli = argv[2];
  const std::string dir = "ttc_shim_case";
  CreateDirectoryA(dir.c_str(), nullptr);
  const std::string in_rgb = dir + "\\in.ppm";
  WritePnm(in_rgb, false, ppm.w, ppm.h, ppm.rgb.data());

  // 灰度输入：标准 Rec.601 luma（公式本身不影响对照结论——双方吃同一份字节）
  std::vector<uint8_t> gray(static_cast<size_t>(ppm.w) * ppm.h);
  for (size_t i = 0; i < gray.size(); ++i) {
    uint8_t R = ppm.rgb[3 * i], G = ppm.rgb[3 * i + 1], B = ppm.rgb[3 * i + 2];
    gray[i] = static_cast<uint8_t>((R * 77 + G * 151 + B * 28 + 128) >> 8);
  }
  const std::string in_gray = dir + "\\in.pgm";
  WritePnm(in_gray, true, ppm.w, ppm.h, gray.data());

  int fails = 0, total = 0;
  auto report = [&](const char* name, const CaseResult& r) {
    ++total;
    if (!r.ok) ++fails;
    std::printf("%-34s %s  shim=%6zuB ref=%6zuB  shim=%6.1fms ref=%6.1fms  %s\n",
                name, r.ok ? "PASS" : "FAIL", r.shim_size, r.ref_size,
                r.shim_ms, r.ref_ms, r.note.c_str());
  };

  const char* chromas[] = {"444", "420"};
  for (const char* cs : chromas) {
    for (int xyb = 0; xyb <= 1; ++xyb) {
      for (int prog : {2, 0}) {
        char name[64];
        std::snprintf(name, sizeof(name), "rgb d0.5 %s xyb%d p%d", cs, xyb, prog);
        std::string flags = std::string("--distance 0.5000") +
                            (prog == 0 ? " --progressive_level 0" : "") +
                            (xyb ? " --xyb" : "") +
                            (" --chroma_subsampling ") + cs;
        std::string args = flags + " \"" + in_rgb + "\" \"" + dir + "\\ref.jpg\"";
        CaseResult r = CompareCase(name, cjpegli, args, enc_rgb,
                                   enc_gray, fre, errf, false, ppm.rgb.data(),
                                   ppm.w, ppm.h, 0.5f, cs, prog, xyb, nullptr,
                                   0, dir + "\\ref.jpg");
        report(name, r);
      }
    }
  }

  for (int xyb = 0; xyb <= 1; ++xyb) {
    for (int prog : {2, 0}) {
      char name[64];
      std::snprintf(name, sizeof(name), "gray d1.0 xyb%d p%d", xyb, prog);
      std::string flags = std::string("--distance 1.0000") +
                          (prog == 0 ? " --progressive_level 0" : "") +
                          (xyb ? " --xyb" : "");
      std::string args = flags + " \"" + in_gray + "\" \"" + dir + "\\ref.jpg\"";
      CaseResult r = CompareCase(name, cjpegli, args, enc_rgb, enc_gray,
                                 fre, errf, true, gray.data(), ppm.w, ppm.h,
                                 1.0f, "444", prog, xyb, nullptr, 0,
                                 dir + "\\ref.jpg");
      report(name, r);
    }
  }

  // ICC 路径：PPM 载不下 ICC，无法与 cjpegli 对照；改为验证库原生嵌入的
  // APP2 分块链重组后与输入 profile 逐字节一致（与产品 C# 注入同规范）。
  if (argc >= 4) {
    std::vector<uint8_t> icc;
    if (!ReadFileBytes(argv[3], &icc)) {
      std::fprintf(stderr, "cannot read icm %s\n", argv[3]);
      return 2;
    }
    std::printf("icc profile: %zu bytes\n", icc.size());
    struct IccCase { const char* name; int prog; int xyb; };
    const IccCase icc_cases[] = {
        {"icc rgb p2 noxyb", 2, 0},
        {"icc rgb p0 noxyb", 0, 0},
        {"icc rgb p2 xyb", 2, 1},
    };
    for (const auto& c : icc_cases) {
      uint8_t* out = nullptr;
      size_t out_len = 0;
      int rc = enc_rgb(ppm.rgb.data(), ppm.w, ppm.h, 0.5f, "444", c.prog, c.xyb,
                       icc.data(), icc.size(), &out, &out_len);
      ++total;
      if (rc != 0) {
        ++fails;
        std::printf("%-34s FAIL  shim error %d: %s\n", c.name, rc, errf());
        continue;
      }
      std::vector<uint8_t> got;
      bool ext_ok = ExtractIccFromJpeg(
          std::vector<uint8_t>(out, out + out_len), &got);
      // 非 XYB：重组结果必须与输入 profile 逐字节一致（库原生嵌入，取代 C# 注入）。
      // XYB：库恒写 XYB 色彩空间 ICC（这正是少数解码器翻车的根源，属预期行为），
      //      改验「合法 ICC 头 + 不等于输入 profile」。
      bool ok;
      if (c.xyb) {
        ok = ext_ok && got.size() >= 132 && got != icc &&
             (((got[0] << 24) | (got[1] << 16) | (got[2] << 8) | got[3]) ==
              static_cast<uint32_t>(got.size()));
      } else {
        ok = ext_ok && got.size() == icc.size() &&
             std::memcmp(got.data(), icc.data(), icc.size()) == 0;
      }
      if (!ok) ++fails;
      std::printf("%-34s %s  jpeg=%6zuB icc=%s\n", c.name, ok ? "PASS" : "FAIL",
                  out_len,
                  ext_ok ? (got.size() == icc.size() &&
                                    std::memcmp(got.data(), icc.data(),
                                                icc.size()) == 0
                                ? "identical"
                                : (ok ? "valid-xyb-icc" : "DIFF"))
                         : "unparseable");
      fre(out);
    }
  }

  std::printf("\n%d/%d passed, %d failed\n", total - fails, total, fails);
  FreeLibrary(dll);
  return fails == 0 ? 0 : 1;
}
