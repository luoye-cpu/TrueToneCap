// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.App/Services/SelfTest.cs
//
// 无头自测模式：TrueToneCap.exe --selftest --out <dir> [--formats PNG,AVIF]
//             只跑高精度源探针：TrueToneCap.exe --hdr-probe --out <dir>
//
// ── 为什么需要它 ──
// 发布包的校验（scripts/verify-package.ps1）此前只能检查"文件是否存在、体积是否达标、
// PE 头是否合法、native --version 是否可跑"，【从未真正启动主程序跑一遍编码链路】。
// 于是这类问题在校验全绿的情况下依然会发生：
//   · WinAppSDK / PRI / XAML 资源缺失 → 启动即崩
//   · AOT 裁剪掉编码器的反射路径 → 一编码就炸
//   · native dll 未随包分发 → 格式编码直接失败
// 本模式让 CI 与发布校验能真正"跑一遍"：生成测试图 → 逐个格式编码 → 校验产物 →
// 输出结果 JSON 并以退出码表达成败。
//
// ── 设计约束 ──
// · 必须在 App 构造函数【最早期】拦截（早于单实例 Mutex、主题初始化、OCR 引擎加载），
//   否则自测会去抢单实例锁、白白加载 67MB 的 ONNX 模型，CI 上又慢又会互相干扰。
// · 不创建任何窗口，不进入 XAML 消息循环。
//
// ── 高精度源探针 (--hdr-probe，随 --selftest 默认附带执行) ──
// 2026-09-19 §28 把"源精度由显示器能力决定"写进了代码，但它【没有任何行为级证据】：
// 实机套件 --wgc-avif-tests 自建会话且帧池硬编码 B8G8R8A8（不经 CaptureMonitorAsync），
// 而 Test 工程只引用 Core、碰不到 App 层的 WgcCaptureService ⇒ 判据只能落在 App 进程内。
// 本探针走真实捕获入口拿 CaptureResult，把"拿到的 float 帧是否真的比 8-bit 多"
// 量化成可复读的数字（整格占比 / 中性像素去重量级 / min-max 是否越出 SDR 范围），
// 能力不足时判 SKIP 而不是 PASS（交接文档 §29 P0-2）。

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TrueToneCap.App.Models;
using TrueToneCap.Core.Encoding;
using TrueToneCap.Core.Logging;   // LogLevel / LogCategory（定义已移到 Core）

namespace TrueToneCap.App.Services;

/// <summary>无头自测：验证编码链路在真实进程中可用 + 高精度源探针（§28 的行为级判据）。</summary>
public static class SelfTest
{
    /// <summary>自测退出码（0=全部成功）。</summary>
    public static int ExitCode { get; private set; }

    /// <summary>
    /// 若命令行含 --selftest / --hdr-probe 则执行并返回 true（调用方应立即退出进程）。
    /// </summary>
    public static bool TryRunFromCommandLine()
    {
        var args = Environment.GetCommandLineArgs();
        bool selfTest = args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase));
        // --hdr-probe：只跑高精度源探针（不必先把六种格式编码一遍），方便随手复验 §28。
        bool probeOnly = !selfTest
            && args.Any(a => string.Equals(a, "--hdr-probe", StringComparison.OrdinalIgnoreCase));
        // --display-audit：逐屏色域/精度审计卡（见 RunDisplayAudit），不跑编码也不跑探针。
        // --display-audit-emit：在审计之外，逐屏再各落两枚真 PNG（下拉=跟随显示器 / Rec.2020），
        //   并把**从盘上读回来**的 cICP/iCCP 打进审计卡。理由：交接 §41 那轮发现"决定层对了"
        //   不等于"产物对了"，而副屏那一枚末端读数过去只能靠人肉截屏、且要挪光标。
        bool emitAudit = args.Any(a => string.Equals(a, "--display-audit-emit", StringComparison.OrdinalIgnoreCase));
        bool displayAudit = !selfTest && !probeOnly
            && args.Any(a => string.Equals(a, "--display-audit", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(a, "--display-audit-emit", StringComparison.OrdinalIgnoreCase));
        // --settings-migration：只跑设置侧三条自检（默认值交还门 / 越界归一回落 / GainMap 重映射；
        // 毫秒级，随手复验 §49 用；--selftest 里也带）
        bool settingsGate = !selfTest && !probeOnly && !displayAudit
            && args.Any(a => string.Equals(a, "--settings-migration", StringComparison.OrdinalIgnoreCase));
        // --sdr-semantics-probe：8-bit 桌面会话到底交回 authored 还是已按纸白抬升的字节
        // （兜底覆盖层过曝的归因探针，见 Services/SdrSemanticsProbe.cs 的文件头）。
        // 它要建一扇置顶补丁窗，但不需要 XAML ⇒ 与其余无头档一样拦在 AppServices.Initialize 之前。
        bool semanticsProbe = !selfTest && !probeOnly && !displayAudit && !settingsGate
            && args.Any(a => string.Equals(a, SdrSemanticsProbe.Flag, StringComparison.OrdinalIgnoreCase));
        if (!selfTest && !probeOnly && !displayAudit && !settingsGate && !semanticsProbe)
            return false;

        string outDir = "";
        // 可选：只跑指定格式（逗号分隔），用于定位"某个格式卡住/崩溃"的场景
        string? only = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--out", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                outDir = args[i + 1];
            else if (string.Equals(args[i], "--formats", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                only = args[i + 1];
        }
        if (string.IsNullOrWhiteSpace(outDir))
            outDir = Path.Combine(Path.GetTempPath(), "TrueToneCap_SelfTest");

        // ⚠️⚠️ 必须放到线程池执行 —— 这里是【WinUI 主线程 (STA)】。
        // 若直接在主线程 `Run(...)`，其内部的 `EncodeSdrAsync(...).GetAwaiter().GetResult()`
        // 会阻塞主线程；而编码器内的 await 完成后要回到主线程同步上下文恢复 →
        // 主线程在等结果、结果在等主线程 = 死锁。
        // 实测症状极具迷惑性：PNG 文件已经写好（483 字节），但进程 45s 都不退出，
        // 且没有任何异常 —— 看起来像"某个格式卡住"，实为同步上下文死锁。
        // 放到 Task.Run 后，内部 await 恢复到线程池，不再依赖 UI 线程。
        // （高精度源探针同理：CaptureMonitorAsync 内部有 SDR + Float16 两段硬阻塞的首帧等待，
        //   而探针最多连跑两轮。）
        if (displayAudit)
        {
            // 审计卡的用途是"发给维护者看"，所以默认落在 exe 同目录而不是 %TEMP%：
            // 本机就有清理器会秒删 %TEMP% 里新建的文件（历史上真丢过证据）。
            string auditPath = string.IsNullOrWhiteSpace(outDir) || outDir.StartsWith(Path.GetTempPath())
                ? Path.Combine(AppContext.BaseDirectory, "display-audit.txt")
                : Path.Combine(outDir, "display-audit.txt");
            ExitCode = Task.Run(() => RunDisplayAudit(auditPath, emitSamples: emitAudit)).GetAwaiter().GetResult();
            return true;
        }
        if (settingsGate)
        {
            Directory.CreateDirectory(outDir);
            // 三条设置侧自检一起跑：都是毫秒级、都不依赖环境，分开看没有意义
            ExitCode = Task.Run(() =>
            {
                int bad = 0;
                if (RunSettingsMigrationCheck(outDir).Status == "fail") bad++;
                if (RunNormalizeGateCheck(outDir).Status == "fail") bad++;
                if (RunGainMapMergeCheck(outDir).Status == "fail") bad++;
                return bad > 0 ? 1 : 0;
            }).GetAwaiter().GetResult();
            return true;
        }
        // 与审计卡同理：证据要落在 exe 同目录，%TEMP% 里的新建文件本机有清理器会秒删。
        if (semanticsProbe)
        {
            string probePath = outDir.StartsWith(Path.GetTempPath())
                ? Path.Combine(AppContext.BaseDirectory, "sdr-semantics-probe.txt")
                : Path.Combine(outDir, "sdr-semantics-probe.txt");
            // 不套 Task.Run：探针自己起一条会 pump 消息的工作线程（补丁窗必须建在那条线程上），
            // 而捕获与采样都在线程池里跑 ⇒ 这里阻塞主线程不会造出 :85 那条同步上下文死锁。
            ExitCode = SdrSemanticsProbe.Run(probePath);
            return true;
        }
        ExitCode = Task.Run(() => Run(outDir, only, probeOnly)).GetAwaiter().GetResult();
        return true;
    }

    /// <summary>
    /// 落一枚真 PNG 样本并把<b>从盘上读回来</b>的声明打进审计卡。
    /// <para>刻意复用产品那三件套（<c>PreparePixelsWithIcc</c> + <c>FormatHelper.GetColorMetadata</c>
    /// + <c>ManagedPngEncoder</c>），而不是在这里"算一遍应该写什么" —— 自检测自己应该写的东西，
    /// 就是 §41.5 驳回的那类恒真式。</para>
    /// </summary>
    private static void EmitColorSample(StringBuilder sb, TrueToneCap.Core.Capture.DisplayInfo d,
        TrueToneCap.App.Models.CaptureResult cap, string label, string tag, bool acm)
    {
        var bgra = cap.SdrPixels ?? cap.GetDisplayPixels();
        if (bgra is null) { sb.AppendLine($"    [{label}] 无 8-bit 像素 ⇒ 跳过"); return; }
        try
        {
            // 本样本落盘的格式恒为 PNG（下面 settings.Format 与 ManagedPngEncoder 都是 PNG）
            // ⇒ 决定按 PNG 构造，让"决定"与"它服务的那个容器"同源。PNG 不属于
            // "默认档 = Rec.709"那一族（白名单只有 JPEG LI / GainMap / WebP）⇒ 读数与旧行为一致。
            var decision = TrueToneCap.Core.Capture.ColorDecision.Build(
                d, tag, d.IsHdr, acm, TrueToneCap.Core.Encoding.OutputFormat.PNG);
            var (px, icc, degraded) = CapturePipelineService.PreparePixelsWithIcc(
                bgra, cap.Width, cap.Height, decision);
            var settings = new TrueToneCap.Core.Encoding.EncodingSettings
            {
                Format = TrueToneCap.Core.Encoding.OutputFormat.PNG,
                ColorSpaceTag = decision.TargetTag,
                OutputBitDepth = 8,
            };
            if (icc is not null) settings.IccProfile = icc;
            // 与 EncodeAndSaveAsync 逐行同构：降级时 tag 与 ICC 必须一起改，否则就是同一个谎
            if (degraded is not null) { settings.ColorSpaceTag = degraded; settings.IccProfile = icc; }

            var dir = Path.Combine(AppContext.BaseDirectory, "color-samples");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{d.Name.Replace('\\', '_').Replace('.', '_')}-{tag}.png");
            var (metadataIcc, cicp) = TrueToneCap.Core.Encoding.FormatHelper.GetColorMetadata(settings);
            TrueToneCap.Core.Encoding.ManagedPngEncoder.Encode(
                px, cap.Width, cap.Height, path, 8, metadataIcc, cicp);

            var (diskCicp, diskIccLen) = ReadPngColorChunks(path);
            sb.AppendLine($"    [{label}] 决定={decision.Describe()}" +
                          (degraded is null ? "" : $"  ⚠降级声明={degraded}"));
            sb.AppendLine($"          盘上={Path.GetFileName(path)}  " +
                          $"cICP={diskCicp}  iCCP={(diskIccLen == 0 ? "无" : $"{diskIccLen} B")}  " +
                          $"像素{(ReferenceEquals(px, bgra) ? "未搬动" : "已搬动")}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"    [{label}] 落盘失败 {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>只读 PNG 的 cICP / iCCP 两块（不依赖本仓库任何编码器代码 ⇒ 独立参照）。</summary>
    private static (string cicp, int iccpLen) ReadPngColorChunks(string path)
    {
        var b = File.ReadAllBytes(path);
        string cicp = "无"; int iccp = 0;
        int i = 8;
        while (i + 8 <= b.Length)
        {
            int len = (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
            string type = System.Text.Encoding.ASCII.GetString(b, i + 4, 4);
            if (type == "cICP" && len >= 4)
                cicp = $"{b[i + 8]}/{b[i + 9]}/{b[i + 10]}/{b[i + 11]}";
            else if (type == "iCCP")
            {
                int z = i + 8;
                while (z < b.Length && b[z] != 0) z++;
                iccp = len - (z - (i + 8)) - 2;   // 扣掉关键词与压缩法字节
            }
            else if (type == "IEND") break;
            i += 12 + len;
        }
        return (cicp, iccp);
    }

    /// <summary>
    /// <c>--display-audit</c>：逐屏出一张"证据 → 分类 → 实际取到的源"的审计卡。
    /// <para>为什么要有这个：三档判据（<c>ClassifyDisplayColor</c>）的输入散在 DXGI 与 DISPLAYCONFIG 两处，
    /// 现场只报一个词（<c>NarrowSdr</c>）不足以判断是"驱动没答复 / DWM 此刻按 SDR 合成 / 面板真 8-bit"，
    /// 而这三种情形的处置完全不同。这里把 <see cref="TrueToneCap.Core.Capture.DisplayEnumerator.DescribeColorEvidence"/>
    /// 的整行输入、拼接 HDR 源的选择结果、以及**真实捕获**拿到的东西放在一起。</para>
    /// <para>后半段刻意与"声明"分开：分类是声明侧的依据，而 float 像素的实测动态范围（min/max、
    /// 落在 SDR 盒子外的占比）才是"这一帧到底是不是扩展范围"的内容侧证据。两者不一致时以这里为准去查。</para>
    /// <param name="emitSamples">逐屏再各落两枚真 PNG（下拉=跟随显示器 / Rec.2020）并<b>从盘上读回</b>
    /// 它的 cICP/iCCP。§41 那轮的教训是"决定层对了"推不出"产物对了"，而副屏的末端读数过去只能靠
    /// 人肉截屏（还得挪光标）。开了这一档，两块的两种下拉各有一枚文件可验。</param>
    /// <returns>0 = 卡片写出且【管线落点】三类直通无分叉；非 0 = 写盘失败或落点分叉（分叉条数逐条印在卡片末尾）。
    /// 2026-09-28（批次 B）之前本函数只在写盘失败时退非零 ⇒ "✗ 判定与落点分叉"是一行只能给人眼读的装饰。</returns>
    /// </summary>
    private static int RunDisplayAudit(string auditPath, bool emitSamples = false)
    {
        var sb = new StringBuilder();
        // 批次 B（2026-09-28 审查轮）：审计卡过去只【印】"✗ 判定与落点分叉"，进程仍然退 0 ⇒
        // 这条最要紧的分叉只有人眼读到卡片才算判过。现在攒进清单，末尾据此定退出码。
        var landingFailures = new List<string>();
        var state = CapabilityService.DetectDisplayState();
        sb.AppendLine($"=== TrueToneCap 显示器色域/精度审计  {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        sb.AppendLine($"系统级：HDR 当前启用={Yes(state.hdr)}  HDR 硬件支持={Yes(state.supportsHdr)}  ACM(注册表)={Yes(state.acm)}");
        sb.AppendLine("说明：分类=Unknown 时源精度放行（多试一次 Float16），但输出声明一律不放开；");
        sb.AppendLine("      拼接的 HDR 源只接受逐屏有明确证据的屏，且 HDR 档优先于广色域档。");

        var displays = TrueToneCap.Core.Capture.DisplayEnumerator.EnumerateDisplays();
        sb.AppendLine($"枚举到活跃显示器 {displays.Count} 块");

        var src = TrueToneCap.Core.Capture.DisplayEnumerator.SelectHdrSourceDisplay(displays, state.acm);
        sb.AppendLine($"多屏拼接的 HDR 源 → {(src is null ? "(无) 本次按全 SDR 拼接" : $"{src.Name} 0x{src.MonitorHandle:X}")}");
        sb.AppendLine();

        // ═══ 落点·全局（这一段与"哪块屏"无关 ⇒ 排在逐屏循环【之外】，整轮只跑一次）═══
        // 台账 §17.6 第 1 条。它原先长在下面那个逐屏循环里面，两笔代价都只在多屏那天显现：
        // ① 同一句分叉按屏数吹成 N 条 ⇒ 卡片末尾"判红 N 条"的那个 N 不是缺陷数；
        // ② 这里的 Load() 不是纯读 —— 迁移门触发时它末尾会 Save()、MigratePlaintextApiKey 还会写
        //    SecretStore，而审计跑在单实例锁之前 ⇒ 每块屏就把"可能改盘"的动作重复一遍。
        // 留在循环里的只有那 4 条格式探针：它们的期望值要按 d.IsHdr 降档，那才是真·屏相关。
        CapturePipelineService? probePipeline = null;
        AppSettingsData? snap = null;
        var dflt = new AppSettingsData();   // 只用来做差分对照，不是喂给管线的那份
        try
        {
            // 审计跑在单实例锁之前 ⇒ AppServices 还没初始化，这里直接造一条管线。
            // ═══ 批次 B（2026-09-28 审查轮）：原先喂的是 new AppSettingsData() 默认快照 ⇒
            // "用户亲手选的增益档/质量档有没有落到 EncodingSettings"这一格从来没被判过，
            // 卡片上只有 HdrOutput 一个布尔。现在读真正被产品读的那一份
            // （SettingsService.SettingsPath 只有一个答案：软件同目录）。
            // ⚠ 与这条探针自己的旧注释相反：Load() 确实【会读】那个文件，且当同目录尚无文件
            //   而旧位置有时，它会搬一份过来（SettingsService.MigrateFromLegacyLocation）⇒
            //   所以这里把"读没读到、能不能持久化、来源是哪条路径"原样印在卡片上。
            // ⚠ 而且它不只读：迁移门触发时 Load() 末尾会 if (migrated) Save() ⇒ 重写那份文件
            //   （盖 SettingsRevision/GainMapMerged 章、清掉明文 Key、按修订号交还默认质量档），
            //   MigratePlaintextApiKey 还会写 SecretStore。审计又跑在单实例锁之前，所以第二个
            //   实例也会走这条路。⇒ 任何拿 --display-audit 取证的脚本必须跑前拍 settings.json 的
            //   sha1、跑后核指纹（tests/manual/safety/audit-case.ps1 正是这么做的，交接报告 §14.1
            //   v6proof 那格量到的 sha1 变化就是这条写路的实测证据）。
            probePipeline = new CapturePipelineService(new SettingsService());
            var settingsSource = new SettingsService();
            bool fileExistedBefore = File.Exists(SettingsService.SettingsPath);
            snap = settingsSource.Load();
            sb.AppendLine("管线落点·全局(真调 BuildEncodingSettings；这一段与屏无关，整轮只跑一次):");
            sb.AppendLine($"  设置来源 {SettingsService.SettingsPath}  " +
                          $"文件本来就在={(fileExistedBefore ? "是" : "否")}  可持久化={SettingsService.CanPersist}  " +
                          $"实际读进来={(settingsSource.LastLoadCameFromDisk ? "是" : "否")}  ⇒ 喂给管线的是" +
                          // "文件在"不等于"读到了"：JSON 语法坏掉时 Load() 走 catch 用默认值表，
                          // 这里若只按文件在出话，卡片就在最关键的一句上说反话（2026-09-29 实测撞到）。
                          (!settingsSource.LastLoadCameFromDisk
                                ? (fileExistedBefore
                                    ? "默认值表（★ 文件在但没读进来 —— 语法/权限/反序列化失败，看日志那条 Warn）"
                                    : "默认值表（文件不存在）")
                                : "你磁盘上那份"));
            // ═══ 换格式保底开关（2026-09-29 用户口径）═══
            // 这一行是"关掉之后确实关掉了吗"的【真身读数】：Core 侧那 10 格矩阵判的是
            // 编码器认不认这个布尔，这里判的是"设置里那个勾有没有抵达 EncodingSettings"。
            // 探针只取布尔本身 ⇒ 不需要屏幕色彩决定（decisionOrNull=null 时管线自己构造）。
            // ⚠ 原先只拿 snap 自己的值比 ⇒ 那是【单向锁】：EncodingSettings.PngFallbackEnabled
            //   的默认是【假】，用户关着开关时把管线里那行接线删掉，两侧也都是假 ⇒ 卡片照样
            //   "一致 ✓"，这一格在最该判的关态没有牙（增益档同病：默认 Gray，设成 Gray 时那句
            //   镜像期望式恒绿）。⇒ 两个方向都跑：另造一份把这两格取反的快照（只在内存里，不写盘），
            //   要求落点等于取反后的值。删掉接线后无论盘上是什么值都必红。
            var flipped = snap.Snapshot();
            flipped.PngFallbackEnabled = !snap.PngFallbackEnabled;
            flipped.GainMapMode = snap.GainMapMode == "Gray" ? "Rgb" : "Gray";
            var probeEs = probePipeline.BuildEncodingSettings(
                OutputFormat.PNG, false, null, snap, null, state.acm, null);
            var probeEsFlipped = probePipeline.BuildEncodingSettings(
                OutputFormat.PNG, false, null, flipped, null, state.acm, null);
            bool fbConsistent = snap.PngFallbackEnabled == probeEs.PngFallbackEnabled
                && flipped.PngFallbackEnabled == probeEsFlipped.PngFallbackEnabled;
            var wantGmFlipped = flipped.GainMapMode == "Gray" ? GainMapMode.Gray : GainMapMode.Rgb;
            bool gmConsistent = probeEsFlipped.GainMapMode == wantGmFlipped;
            sb.AppendLine($"  换格式保底（设置项 PngFallbackEnabled）= {snap.PngFallbackEnabled}" +
                          $" ⇒ 抵达编码设置 EncodingSettings.PngFallbackEnabled = {probeEs.PngFallbackEnabled}" +
                          $"；反向 {flipped.PngFallbackEnabled} ⇒ {probeEsFlipped.PngFallbackEnabled}" +
                          $" {(fbConsistent ? "（两个方向都跟得上 ✓）" : "（★ 至少一个方向丢了：开关在管线上不生效）")}");
            if (!fbConsistent)
                landingFailures.Add(
                    "全局·保底开关: 设置 " + $"{snap.PngFallbackEnabled}/{flipped.PngFallbackEnabled}" +
                    $" 抵达编码设置 {probeEs.PngFallbackEnabled}/{probeEsFlipped.PngFallbackEnabled}");
            if (!gmConsistent)
                landingFailures.Add(
                    $"全局·增益档反向: 设置 GainMapMode=\"{flipped.GainMapMode}\" 落点={probeEsFlipped.GainMapMode}" +
                    "（正向随每条格式探针判，这一格补的是反方向）");
            if (Math.Abs(snap.GetQuality(OutputFormat.JPEG_XL) - dflt.GetQuality(OutputFormat.JPEG_XL)) < 1e-6 &&
                snap.GainMapMode == dflt.GainMapMode &&
                snap.ChromaJpegLi == dflt.ChromaJpegLi &&
                snap.BitDepthJpegXl == dflt.BitDepthJpegXl)
                sb.AppendLine("  ⚠ 你这份设置与默认值在【这几根轴】上完全相同 ⇒ 下面逐屏那几条差分读数不判别，" +
                              "只证明默认值走得通；要判「用户所选有没有落地」得先改一格再跑");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  管线落点·全局: {ex.GetType().Name}: {ex.Message}");
        }

        // ═══ 呈现帧契约（2026-09-30 改道：SDR 与 HDR 用同一张预览窗，这一段读的就是"那张窗上该看见什么"）═══
        // 为什么放在审计里而不是只靠 Test 工程那 18 条：Test 判的是 Core 里的纯函数，
        // 而"App 侧到底把哪个 HDR 输出状态、哪个系统白点喂进了这个函数"是接线问题，
        // 只有产品自己这一份进程内的读数能证明它。夹具用 256 级灰阶（alpha 故意一半 0 一半 255），
        // 所以这行同时是"参考白乘没乘"和"信不信源 alpha"两件事的实机证据。
        try
        {
            var ramp = new byte[256 * 4];
            for (int k = 0; k < 256; k++)
            {
                ramp[k * 4] = (byte)k; ramp[k * 4 + 1] = (byte)k; ramp[k * 4 + 2] = (byte)k;
                ramp[k * 4 + 3] = (byte)((k % 2 == 0) ? 0 : 255);
            }
            int whiteNits = TrueToneCap.Core.Capture.DisplayEnumerator.GetSdrWhiteLevel();
            var pres = TrueToneCap.Core.PixelOps.BgraToScrgbForDisplay(ramp, 256, 1, state.hdr, whiteNits);
            float maxV = 0f; int aBad = 0;
            for (int k = 0; k < 256; k++) { if (pres[k * 4] > maxV) maxV = pres[k * 4]; if (pres[k * 4 + 3] != 1f) aBad++; }
            float scale = state.hdr && whiteNits > 0
                ? whiteNits / TrueToneCap.Core.PixelOps.NominalSdrReferenceWhiteNits : 1f;
            string rangeVerdict = !state.hdr
                ? (maxV <= 1.000001f ? "（非 HDR 输出：值域未越 1 ⇒ 不会被 DWM 数值钳白）"
                                     : "（★ 非 HDR 输出却越 1 ⇒ 会被钳白，这一档不该乘白点）")
                : (pres[255 * 4] > 1f ? "（HDR 输出：白已抬到系统白点之上 ✓）"
                                      : "（★ HDR 输出但白没抬起来 ⇒ 白点没乘或读不到）");
            sb.AppendLine($"呈现帧契约: HDR输出={state.hdr} 系统SDR白点={whiteNits}nits 缩放=×{scale:0.###} " +
                          $"码点255→{pres[255 * 4]:0.####} 整条max={maxV:0.####} A≠1的格数={aBad} {rangeVerdict}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"呈现帧契约: 读数失败 {ex.GetType().Name}: {ex.Message}");
        }

        sb.AppendLine();

        var wgc = new WgcCaptureService();
        try
        {
            foreach (var d in displays)
            {
                sb.AppendLine($"── {d.Name} 0x{d.MonitorHandle:X}{(d.IsPrimary ? " [主屏]" : "")}  {d.Width}x{d.Height} @({d.X},{d.Y}) ──");
                sb.AppendLine("  " + TrueToneCap.Core.Capture.DisplayEnumerator
                              .DescribeColorEvidence(d, state.acm));

                // ═══ 这份截图真正会用到的【唯一】色彩决定（Core 的 ColorDecision）═══
                // 2026-09-22 审查轮之前，这一行印的是"第二份决定"：像素路自己解析一遍下拉、
                // 又用另一套宽松的 EDID 阈值取源色域（交接 §41）。现在像素路与声明路消费同一个对象，
                // 所以审计里这一行就是产物会怎么写的那一行 —— 两侧不一致已经不可能发生。
                try
                {
                    // 这两行是"两档下拉各自解析成什么"的格式无关读数 ⇒ 显式按 PNG 构造。
                    // 为什么不传 null：本卡片的用途是与界面那张卡对照，而界面那张卡读的决定带格式
                    // （MainWindow.ColorManagement 传 ui.Format）。用 PNG 这个【不属于 Rec.709 默认档】
                    // 的成员，读到的正是"未受格式口径收口"的解析结果 —— 两档各自的差异才看得清；
                    // 换 JPEG/WebP 的话两行都会被格式收口成 sRGB，反而看不出下拉本身解析成了什么。
                    var dec = TrueToneCap.Core.Capture.ColorDecision.Build(
                        d, "System", d.IsHdr, state.acm, TrueToneCap.Core.Encoding.OutputFormat.PNG);
                    var decExplicit = TrueToneCap.Core.Capture.ColorDecision.Build(
                        d, "BT2020", d.IsHdr, state.acm, TrueToneCap.Core.Encoding.OutputFormat.PNG);
                    sb.AppendLine("  色彩决定(源色域→目标=声明，像素路与声明路共用):");
                    sb.AppendLine($"    下拉=跟随显示器 → {dec.Describe()}");
                    sb.AppendLine($"    下拉=Rec.2020    → {decExplicit.Describe()}");
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"  色彩决定: 构造失败 {ex.GetType().Name}: {ex.Message}");
                }

                // ═══ 界面「色域映射」卡片逐格式印一遍（2026-09-26，交接 §53）═══
                // 为什么放在审计里而不是靠人去看界面：卡片现在渲染的是 ColorDecision +
                // FormatColorFacts，与这里【同一个函数】⇒ 这张卡就是卡片的镜像，且能覆盖
                // "光标不在那块屏上"的另一块屏（挪光标去逐屏截屏既做不到也不该由测试代做）。
                // HDR 一律按"开着"问 —— 要看的正是"开关为真时到底交付什么"，而它不是一种答案。
                try
                {
                    var fmts = new[]
                    {
                        ("PNG", TrueToneCap.Core.Encoding.OutputFormat.PNG),
                        ("JPEG", TrueToneCap.Core.Encoding.OutputFormat.JPEG_LI),
                        ("JPEG XL", TrueToneCap.Core.Encoding.OutputFormat.JPEG_XL),
                        ("AVIF", TrueToneCap.Core.Encoding.OutputFormat.AVIF),
                        ("WebP", TrueToneCap.Core.Encoding.OutputFormat.WebP),
                        ("TIFF", TrueToneCap.Core.Encoding.OutputFormat.TIFF),
                        ("增益图(JPEG)", TrueToneCap.Core.Encoding.OutputFormat.JPEG_GAINMAP),
                    };
                    sb.AppendLine("  交付形态(=界面卡片那行箭头; HDR 开关按开、两档目标色域各问一次):");
                    foreach (var (name, f) in fmts)
                    {
                        // 决定带【本格自己的格式】：JPEG / WebP 的默认档是 Rec.709，
                        // 不带 f 的话这两格会印出 PNG 才有的行为 ⇒ 这张卡就不再是界面卡片的镜像。
                        var sSrgb = TrueToneCap.Core.Encoding.FormatColorFacts.Delivery(
                            TrueToneCap.Core.Capture.ColorDecision.Build(d, "sRGB", true, state.acm, f), f);
                        var sWide = TrueToneCap.Core.Encoding.FormatColorFacts.Delivery(
                            TrueToneCap.Core.Capture.ColorDecision.Build(d, "BT2020", true, state.acm, f), f);
                        sb.AppendLine($"    {name,-17} Rec.709⇒{sSrgb,-19} Rec.2020⇒{sWide}");
                    }
                    // 声明通道逐格式一句：同一枚目标色域在不同容器里长什么样（BT.709 只出现在 matrix 那一维）
                    sb.AppendLine("  声明通道(=界面卡片那句灰字; 目标按本屏解析):");
                    foreach (var (name, f) in fmts)
                    {
                        // 同样带本格的格式：这条印的是"目标色域按本屏解析后，装进这个容器长什么样"，
                        // 而目标色域本身就受格式口径影响（JPEG/WebP 默认档 Rec.709）⇒ 决定与容器必须同源。
                        var dec = TrueToneCap.Core.Capture.ColorDecision.Build(
                            d, "System", true, state.acm, f);
                        var del = TrueToneCap.Core.Encoding.FormatColorFacts.Delivery(dec, f);
                        sb.AppendLine($"    {name,-17} " + TrueToneCap.Core.Encoding.FormatColorFacts.Declaration(
                            f, dec.TargetTag, del == TrueToneCap.Core.Encoding.HdrDelivery.PqContainer));
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"  交付形态: 渲染失败 {ex.GetType().Name}: {ex.Message}");
                }

                // ═══ 管线落点：那句"HDR 直通/色调映射"到底有没有变成 settings.HdrOutput ═══
                // 上面印的是 Core 的判定，产物却由 BuildEncodingSettings 写出的那个布尔决定 ——
                // 上一轮的缺陷正是断在这条缝上：判定说"增益图形态"，管线另有一条【无条件】的
                // "HDR + sRGB ⇒ HdrOutput=false"，于是 JpegGainMapEncoder 走 base-only 分支，
                // 用户开着增益图开关拿到的是一张没有增益图的普通 JPEG（界面还全程说"HDR 直通"）。
                // 只测 Core 或只测编码器都看不见这一格，所以这里真调一次管线。
                // 刻意不取 PNG / AVIF 两格：它们各自的位深协商与编码器回落在 ToastService 上有副作用，
                // 而这条判据要的是"审计跑起来不会往用户桌面弹框"。
                try
                {
                    // 与屏无关的那一段（设置读没读进来 / 保底开关两个方向 / 增益档反向 / 与默认值同轴的警告）
                    // 已挪到逐屏循环【之前】，整轮只跑一次 ⇒ 这里只剩屏相关的那 4 条格式探针。
                    // 全局段没建成 = 这 4 条【没跑】，必须明说：走下面那个 catch 留痕，不许静默跳过。
                    if (probePipeline is null || snap is null)
                        throw new InvalidOperationException("全局落点段没建成 ⇒ 本屏这 4 条格式探针【没跑】，不算通过");
                    sb.AppendLine("  管线落点(真调 BuildEncodingSettings; 交付形态 ⇒ 写进编码设置的布尔):");
                    var probes = new (string Label, OutputFormat Fmt, string Tag, bool WantHdr)[]
                    {
                        ("增益图 + Rec.709 底图", OutputFormat.JPEG_GAINMAP, "sRGB", true),
                        ("增益图 + Rec.2020 底图", OutputFormat.JPEG_GAINMAP, "BT2020", true),
                        ("JPEG XL + Rec.709 目标", OutputFormat.JPEG_XL, "sRGB", false),
                        ("JPEG XL + Rec.2020 目标", OutputFormat.JPEG_XL, "BT2020", true),
                    };
                    foreach (var p in probes)
                    {
                        bool hdrScreen = d is { IsHdr: true };
                        // 带 p.Fmt：这 4 条探针走的正是产品那条路（决定 → BuildEncodingSettings），
                        // 而路径上的格式口径收口（JPEG 家族/WebP 的 Rec.709 默认档）就发生在决定里
                        // ⇒ 不带格式的话，探针量到的色域/形态与真实产物不是同一份输入。
                        var dcd = TrueToneCap.Core.Capture.ColorDecision.Build(d, p.Tag, true, state.acm, p.Fmt);
                        var es = probePipeline.BuildEncodingSettings(
                            p.Fmt, true, null, snap, p.Tag, state.acm, dcd);
                        var es0 = probePipeline.BuildEncodingSettings(
                            p.Fmt, true, null, dflt, p.Tag, state.acm, dcd);
                        var del = FormatColorFacts.Delivery(dcd, p.Fmt);
                        // HDR 屏之外的一切都拿不到写 PQ 的授权 ⇒ 期望值随屏幕降一档（不许把"屏不授权"读成判据失败）
                        bool expect = hdrScreen ? p.WantHdr : false;
                        bool ok = es.HdrOutput == expect;
                        sb.AppendLine($"    {p.Label,-24} 分类={dcd.ScreenClass} 形态={del,-20} " +
                                      $"HdrOutput={es.HdrOutput} 色域={es.ColorSpaceTag} " +
                                      $"色度={es.ChromaSubsampling} 位深={es.OutputBitDepth} " +
                                      $"增益档={es.GainMapMode} 质量={es.Quality:0.###} ⇒ 期望 {expect} " +
                                      $"{(ok ? "✓" : "✗ 判定与落点分叉")}");
                        if (!ok)
                            landingFailures.Add(
                                $"{d.Name}·{p.Label}: 交付形态说 {del} 而落点 HdrOutput={es.HdrOutput}，期望 {expect}");

                        // 用户所选有没有落地：只判【不需要再抄一张格式↔字段键表】的两条直通规则。
                        // ① 增益档直通（Core 侧那句 `s.GainMapMode == "Gray" ? Gray : Rgb` 的镜像，
                        //    它是字符串→枚举的唯一一步，不是按格式分支的表）；
                        // ② 质量差分：两份快照的该格式质量【本来不同】时，落点必须不同
                        //    （用产品自己的 GetQuality(f) 取值 ⇒ 不复制键表，改表也不会在这里过期）。
                        var wantGm = snap.GainMapMode == "Gray" ? GainMapMode.Gray : GainMapMode.Rgb;
                        if (es.GainMapMode != wantGm)
                            landingFailures.Add(
                                $"{d.Name}·{p.Label}: 设置里 GainMapMode=\"{snap.GainMapMode}\" 而落点={es.GainMapMode}");
                        double qSnap = snap.GetQuality(p.Fmt), qDflt = dflt.GetQuality(p.Fmt);
                        if (Math.Abs(qSnap - qDflt) > 1e-6 &&
                            Math.Abs(es.Quality - es0.Quality) <= 1e-6)
                            landingFailures.Add(
                                $"{d.Name}·{p.Label}: 质量 {qDflt}→{qSnap} 换了设置，落点却仍是 {es.Quality:0.###}（没落地）");
                        sb.AppendLine($"      └ 差分(默认⇒你的): 质量 {es0.Quality:0.###}→{es.Quality:0.###} " +
                                      $"色度 {es0.ChromaSubsampling}→{es.ChromaSubsampling} " +
                                      $"位深 {es0.OutputBitDepth}→{es.OutputBitDepth} " +
                                      $"增益档 {es0.GainMapMode}→{es.GainMapMode}");
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"  管线落点: {ex.GetType().Name}: {ex.Message}");
                }

                try
                {
                    TrueToneCap.App.Models.CaptureResult? cap = null;
                    var cfg = new WgcCaptureConfig
                    {
                        TargetMonitor = d.MonitorHandle,
                        PreferHdr = true,
                        FrameTimeoutMs = 4000,
                    };
                    // 与 --hdr-probe 同样给两次机会：会话池是持久的，第一次常撞上"帧池刚建、DWM 还没成帧"
                    for (int attempt = 0; attempt < 2 && cap?.HdrPixels is null; attempt++)
                    {
                        var t = Task.Run(() => wgc.CaptureMonitorAsync(cfg));
                        if (!t.Wait(TimeSpan.FromSeconds(25)))
                        {
                            sb.AppendLine("  捕获: 25s 未返回（会话建不起来或无桌面会话）⇒ 内容侧无法评估");
                            break;
                        }
                        cap = t.Result;
                    }
                    if (cap is not null)
                    {
                        sb.AppendLine($"  实得源: SDR={(cap.SdrPixels is not null ? "有" : "无")}  " +
                                      $"Float16={(cap.HdrPixels is not null ? "有" : "无")}  " +
                                      $"帧 {cap.Width}x{cap.Height}  耗时 {cap.CaptureTimeMs}ms" +
                                      (cap.DegradationWarning is null ? "" : $"  ⚠降级:{cap.DegradationWarning}"));
                        if (cap.HdrPixels is { Length: > 0 } px)
                        {
                            var st = AnalyzeHdrFloat(px, cap.Width, cap.Height, PaperWhiteScrgb());
                            sb.AppendLine($"  内容实测(声明域外的东西到底有没有): float min={F(st.Min)} max={F(st.Max)} 纸白={F(st.PaperWhite)} " +
                                          $">纸白 占 {F(st.AboveWhitePct)}%（判据用这条）  >1.0 占 {F(st.Above1Pct)}%（旧锚点，只留作对照）  <0 占 {F(st.Below0Pct)}%  抽样 {st.Pixels} px");
                            sb.AppendLine("  读法: 【>纸白】占比要落在 " +
                                          $"{HeadroomMinPct:0.##}%~{HeadroomMaxPct:0}% 才算'SDR 白之上确有头部余量'" +
                                          "（与高精度源探针同一条判据）。⚠ 2026-09-28 批次 D 之前这里锚的是 >1.0：" +
                                          "本机纸白 120 nits ⇒ SDR 内容本身就压在 1.5 上，>1.0 占比量的是【桌面有多亮】" +
                                          "（本机一周内五次读到 1.5% / 61% / 74% / 87% / 92%，全部与精度无关）；");
                            sb.AppendLine("        纸白由 DisplayEnumerator.GetSdrWhiteLevel() 现读，整格尺子的 LSB 也按它缩放" +
                                          "⇒ 8-bit 内容被抬到该白点后仍会被认成 8-bit（否则这条会读成\"偏离整格\"=假绿）；");
                            sb.AppendLine("        占比≈0 且 max≈1.0 ⇒ 这一帧的亮度仍在 SDR 盒子里 —— 广色域与否要看" +
                                          "上面 DISPLAYCONFIG 的合成模式与 EDID 原色，不能从亮度占比反推。");
                        }
                        else if (cap.HdrPixels is null)
                            sb.AppendLine("  内容实测: 不适用 —— 分类未放行 Float16，源就是 8-bit BGRA（这就是本次的结论本身）");

                        if (emitSamples)
                        {
                            sb.AppendLine("  落盘样本（走的是产品同一套决定 + 同一编码器，声明从盘上读回）:");
                            EmitColorSample(sb, d, cap, "跟随显示器", "System", state.acm);
                            EmitColorSample(sb, d, cap, "Rec.2020", "BT2020", state.acm);
                        }
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"  捕获异常: {ex.GetType().Name}: {ex.Message}");
                }
                sb.AppendLine();
            }
        }
        finally
        {
            try { wgc.Dispose(); } catch { }
        }

        // 落点判决写进卡片本身，并且据此定退出码：审计此前"印了 ✗ 仍退 0"，
        // 分叉只能靠人读到那一行 ⇒ 无头复跑等于没判。
        if (landingFailures.Count > 0)
        {
            sb.AppendLine($"══ 管线落点判红 {landingFailures.Count} 条 ══");
            foreach (var f in landingFailures) sb.AppendLine($"  ✗ {f}");
        }
        else
            sb.AppendLine("管线落点：HdrOutput / 增益档直通 / 质量差分 三类一致，0 条分叉");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(auditPath)!);
            File.WriteAllText(auditPath, sb.ToString(), new UTF8Encoding(true));
            Console.WriteLine(sb.ToString());
            Console.WriteLine($"[display-audit] 已写入 {auditPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[display-audit] ❌ 写文件失败: {ex.Message}");
            Console.WriteLine(sb);
            return 1;
        }
        if (landingFailures.Count > 0)
        {
            Console.WriteLine($"[display-audit] ❌ 管线落点分叉 {landingFailures.Count} 条 ⇒ 退出码非零"
                              + $"（卡片: {auditPath}）");
            return 1;
        }
        return 0;
    }

    /// <summary>把进度增量写入文件（每个用例后立即 flush）。
    /// GUI 子系统应用没有控制台，Console 输出不可见且不可靠；
    /// 一旦进程在退出阶段卡住，只有落盘的进度能回答"到底走到哪一步"。</summary>
    private static void Progress(string outDir, string line)
    {
        try
        {
            File.AppendAllText(Path.Combine(outDir, "progress.txt"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");
        }
        catch { }
    }

    private static int Run(string outDir, string? onlyFormats = null, bool probeOnly = false)
    {
        Directory.CreateDirectory(outDir);
        Progress(outDir, probeOnly ? "start (hdr-probe only)" : "start");
        const int W = 96, H = 96;
        var bgra = MakeGradient(W, H);

        // 覆盖「托管编码器 + 各 native 工具」，任一类缺失都会在这里暴露
        var cases = new (OutputFormat Fmt, string Ext, string Note)[]
        {
            (OutputFormat.PNG,     ".png",  "托管 PNG 编码器"),
            (OutputFormat.TIFF,    ".tiff", "托管 TIFF 编码器"),
            (OutputFormat.JPEG_LI, ".jpg",  "native jpegli (jxl.dll)"),
            (OutputFormat.JPEG_XL, ".jxl",  "native cjxl 子进程"),
            (OutputFormat.AVIF,    ".avif", "native avifenc 子进程"),
            (OutputFormat.WebP,    ".webp", "native cwebp 子进程"),
        };

        // 只跑指定格式（逗号分隔，大小写不敏感）——用于定位"某一格式卡住/崩溃"
        if (!string.IsNullOrWhiteSpace(onlyFormats))
        {
            var want = onlyFormats.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                  .Select(s => s.ToUpperInvariant()).ToHashSet();
            cases = cases.Where(c => want.Contains(c.Fmt.ToString().ToUpperInvariant())).ToArray();
            if (cases.Length == 0)
            {
                Console.WriteLine($"[selftest] ❌ --formats 未匹配到任何格式: {onlyFormats}");
                return 2;
            }
        }
        // --hdr-probe：只要探针，不跑任何编码
        if (probeOnly) cases = Array.Empty<(OutputFormat Fmt, string Ext, string Note)>();

        var entries = new List<string>();     // 每个检查项一条 JSON 对象（编码用例 + 高精度源探针）
        int failed = 0;
        // ═══ 分类计数：最后一行要把 ok / skip / missingBackend / fail 拆开打印 ═══
        // 只报 failed 的话，读日志的人看不出"这台机器没有编码器"与"产品坏了"是不是同一回事。
        int okCount = 0, skipCount = 0, missingCount = 0, failCount = 0;
        void Tally(string st)
        {
            switch (st)
            {
                case "ok": okCount++; break;
                case "skip": skipCount++; break;
                case "missingBackend": missingCount++; break;
                default: failCount++; break;   // fail 与 fallback（后者按既有口径同样计失败）
            }
        }
        for (int i = 0; i < cases.Length; i++)
        {
            var c = cases[i];
            var sw = Stopwatch.StartNew();
            var path = Path.Combine(outDir, "selftest_" + c.Fmt + c.Ext);
            string status, detail = "";

            try
            {
                if (File.Exists(path)) File.Delete(path);
                var encoder = EncoderFactory.Create(c.Fmt);
                // ═══ 2026-09-19 修复：自检质量必须按【该编码器自己的刻度】取 ═══
                // 原先一律喂 80f，等于把三把不同的尺子当一把用：
                //   · 百分比刻度（PNG/TIFF 恒 100 无损、WebP 50–100）—— 80 有意义；
                //   · butteraugli 距离刻度（JPEG LI 0.5–3.0、JPEG XL 0.1–4.0、GainMap 0.5–3.0）——
                //     数值【越小越清晰】，80 远在区间之外，会被编码器钳成区间端点式的荒谬值
                //     （实现里是 clamp 到 25.0），自检"通过"的其实是最低质量；
                //   · AVIF 是 CRF（0–63，0=无损）—— 80 同样越界。
                // 取 Default（各编码器 GetQualityRange 里的产品默认值，本就落在自己区间内，
                // 且是真实用户会拿到的档位）；再钳一次是防某个编码器声明的 Default 越界。
                var (qMin, qMax, qDefault, _) = encoder.GetQualityRange();
                float quality = Math.Clamp(qDefault, qMin, qMax);
                var settings = new EncodingSettings
                {
                    Format = c.Fmt,
                    Quality = quality,
                    HdrOutput = false,
                    ChromaSubsampling = "444",
                    OutputBitDepth = 8,
                };
                encoder.EncodeSdrAsync(bgra, W, H, settings, path).GetAwaiter().GetResult();
                sw.Stop();

                if (File.Exists(path) && new FileInfo(path).Length > 0)
                    status = "ok";
                else
                {
                    // native 工具缺失时某些编码器会【静默回退为 PNG】并仍返回成功，
                    // 产物变成同名 .png。这在发布校验中必须被看见 —— 否则会误以为
                    // "AVIF 编码通过"，实际包里根本没有 AVIF 能力。
                    var fallbackPng = Path.ChangeExtension(path, ".png");
                    if (File.Exists(fallbackPng) && new FileInfo(fallbackPng).Length > 0)
                    {
                        status = "fallback";
                        detail = $"native 工具缺失，已回退为 PNG（{Path.GetFileName(fallbackPng)}）";
                    }
                    else
                    {
                        status = "fail";
                        detail = "编码器返回成功，但输出文件缺失或为空";
                    }
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                // 取最内层异常：native 失败常被包装多层，只看外层会误导
                var root = ex;
                while (root.InnerException is not null) root = root.InnerException;
                detail = root.GetType().Name + ": " + root.Message;
                // ═══ 2026-09-30：同一个根因（后端可执行文件不在位）过去在【一次自测里】被分成两类结局 ═══
                // InvalidOperationException 的 cjxl/cjpegli 走 IsNativeMissing ⇒ skip，而 DllNotFoundException
                // 的 avifenc/cwebp ⇒ fail，于是"开发态构建没带工具"这一种情形报出 failed=2、退出码 1，
                // 读退出码的人分不清"产品坏了"与"这台机器/这份构建没有编码器"。缺失现在单独成第三类
                // missingBackend（不计入 failed），真失败一律照旧计。
                status = BackendToolMissing(c.Fmt, root) ? "missingBackend"
                     : IsNativeMissing(root) ? "skip" : "fail";
            }

            // fallback 同样计为失败：发布包不该缺 native 而悄悄降级成 PNG
            if (status is "fail" or "fallback") failed++;
            Tally(status);

            Progress(outDir, $"{c.Fmt} {status} {sw.ElapsedMilliseconds}ms {detail}");

            entries.Add(JsonEntry(c.Fmt.ToString(), c.Note, status, sw.ElapsedMilliseconds,
                File.Exists(path) ? new FileInfo(path).Length : 0, detail));
        }

        // ═══ 迁移门自检排在判据自检之前：它同样不依赖显示器/GPU/native，且它管的
        // 是"用户看到的默认档从哪来"——门死了，后面所有"默认值合适吗"的讨论都无意义。═══
        var sm = RunSettingsMigrationCheck(outDir);
        if (sm.Status == "fail") failed++;
        Tally(sm.Status);
        entries.Add(JsonEntry("SETTINGS_MIGRATION", sm.Note, sm.Status, sm.Ms, 0, sm.Detail, sm.JsonExtra));

        var ng = RunNormalizeGateCheck(outDir);
        if (ng.Status == "fail") failed++;
        Tally(ng.Status);
        entries.Add(JsonEntry("NORMALIZE", ng.Note, ng.Status, ng.Ms, 0, ng.Detail, ng.JsonExtra));

        var gmm = RunGainMapMergeCheck(outDir);
        if (gmm.Status == "fail") failed++;
        Tally(gmm.Status);
        entries.Add(JsonEntry("GAINMAP_MIGRATION", gmm.Note, gmm.Status, gmm.Ms, 0, gmm.Detail, gmm.JsonExtra));

        // ═══ 判据自检（合成帧）排在探针【前面】：尺子先自证，再谈读数。═══
        // 它不依赖显示器/GPU/桌面 ⇒ 没有 HDR 屏、探针只能 SKIP 的机器，也仍然能证明判据是活的。
        var judge = RunHdrJudgeCheck(outDir);
        if (judge.Status == "fail") failed++;
        Tally(judge.Status);
        entries.Add(JsonEntry("HDR_JUDGE", judge.Note, judge.Status, judge.Ms, 0, judge.Detail, judge.JsonExtra));

        // ═══ 高精度源探针（§28 "源精度由显示器能力决定"的端到端判据）═══
        // 与编码用例同一套 status 词表（ok / fail / skip）与退出码约定：skip 不计失败。
        var probe = RunHdrSourceProbe(outDir);
        if (probe.Status == "fail") failed++;
        Tally(probe.Status);
        entries.Add(JsonEntry("HDR_SOURCE", probe.Note, probe.Status, probe.Ms, 0, probe.Detail, probe.JsonExtra));

        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine($"  \"generatedUtc\": \"{DateTime.UtcNow:o}\",");
        sb.AppendLine($"  \"version\": \"{typeof(SelfTest).Assembly.GetName().Version}\",");
        sb.AppendLine("  \"results\": [");
        for (int i = 0; i < entries.Count; i++)
            sb.AppendLine(entries[i] + (i < entries.Count - 1 ? "," : ""));
        sb.AppendLine("  ],");
        sb.AppendLine($"  \"failed\": {failed},");
        // 分账：missingBackend 单独一条，读 JSON 的人不必逐条数 status 就知道"这台机器缺几枚工具"
        sb.AppendLine($"  \"counts\": {{ \"ok\": {okCount}, \"skip\": {skipCount}, " +
                      $"\"missingBackend\": {missingCount}, \"fail\": {failCount} }}");
        sb.AppendLine("}");

        var jsonPath = Path.Combine(outDir, "selftest.json");
        File.WriteAllText(jsonPath, sb.ToString());
        // ⚠ `failed=` 这个键名不能改、也不能挪位置：外部工具就 grep 这一段
        Progress(outDir, $"json-written failed={failed} ok={okCount} skip={skipCount} " +
                         $"missingBackend={missingCount} fail={failCount}");

        // entries.Count 而不是 cases.Length + 4：非编码用例实际是【5】条（迁移门 / 归一 /
        // 增益图重映射 / 判据自检 / 源探针），旧算式少算一格 ⇒ 分母 10、实际 11 项读数。
        int total = entries.Count;
        Console.WriteLine($"[selftest] 结果: {total - failed}/{total} 通过 → {jsonPath}");
        Console.WriteLine($"[selftest] 分类: ok={okCount} skip={skipCount} missingBackend={missingCount} " +
                          $"fail={failCount}（计入 failed 的只有 fail ⇒ 退出码 {(failed > 0 ? 1 : 0)}；" +
                          $"共 {total} 项读数）");
        if (failCount != failed || okCount + skipCount + missingCount + failCount != total)
        {
            // 分类计数与既有 failed 必须自洽，否则这里新增的是第二把尺子而不是同一把：
            // 汇总数与点名条数不等 ⇒ 有一条 status 没进 tally，立刻点名。
            Progress(outDir, $"★ 分账不自洽: ok+skip+missingBackend+fail={okCount + skipCount + missingCount + failCount} " +
                             $"而读数={total}；fail={failCount} 而 failed={failed} ⇒ 分类计数不可信");
            Console.WriteLine($"[selftest] ★ 分账不自洽（fail={failCount} vs failed={failed}）⇒ 上面的分类计数不可信");
        }
        Progress(outDir, "returning");
        return failed > 0 ? 1 : 0;
    }

    /// <summary>拼一条 results 数组元素（保持既有键顺序；extra 供探针追加实测数字字段）。</summary>
    private static string JsonEntry(string format, string note, string status,
        long ms, long bytes, string detail, string extra = "")
    {
        var s = new StringBuilder();
        s.AppendLine("    {");
        s.AppendLine($"      \"format\": \"{Escape(format)}\",");
        s.AppendLine($"      \"note\": \"{Escape(note)}\",");
        s.AppendLine($"      \"status\": \"{Escape(status)}\",");
        s.AppendLine($"      \"ms\": {ms},");
        s.AppendLine($"      \"bytes\": {bytes},");
        s.Append($"      \"detail\": \"{Escape(detail)}\"");
        if (extra.Length > 0) s.Append("," + extra.TrimStart(','));   // extra 自带前导逗号与换行缩进
        s.AppendLine();
        s.Append("    }");
        return s.ToString();
    }

    /// <summary>判断是否"本机缺少该 native 工具"（属环境限制，而非代码缺陷）。</summary>
    private static bool IsNativeMissing(Exception ex)
    {
        if (ex is not InvalidOperationException) return false;
        var m = ex.Message;
        return m.Contains("不可用", StringComparison.Ordinal)
            || m.Contains("需要", StringComparison.Ordinal)
            || m.Contains("native", StringComparison.OrdinalIgnoreCase)
            || m.Contains("PATH", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 该格式的<b>外部编码工具在本机是否不在位</b> —— 第三类结局 <c>missingBackend</c> 的判据。
    /// <para>为什么不匹配异常文案：四条抛出点写的都是 <c>if (!XxxNative.IsAvailable) throw …</c>，
    /// 所以"探针说没工具"与"这条异常是缺失造成的"本是同一个条件 —— 直接问探针，既不看文案
    /// （旧 <see cref="IsNativeMissing"/> 那套 substring 匹配保留作次级出口，只在探针说"工具在位"
    /// 时按老口径记 skip），也不会因为某句中文改了措辞就静默失去分辨力。</para>
    /// <para>两个条件都要，缺一会判反：
    /// ① 根异常属"模块/可执行文件不在位"这一族（2026-09-30 实测量到的形态：cjxl/cjpegli 抛
    /// <see cref="InvalidOperationException"/>、avifenc/cwebp 抛 <see cref="DllNotFoundException"/>）；
    /// 其余异常（IO / 参数 / 取消）与"没带工具"无关，一律留在 fail 里。
    /// ② 该格式的那枚外部工具确实探不到。AVIF 只问 avifenc 一条，不再顺带探 NVENC/MFT：
    /// 探针自己会建 D3D 设备与 NVENC 会话（不缓存、且 P/Invoke 层崩溃在 .NET 上不可捕获，
    /// 见 NativeEncoderGuard），自测不该为分类再去付一次会话创建。已知代价写清楚 ——
    /// 开发态【没带 avifenc】的构建里，若硬件后端在位而这一次编码失败，硬件链会落到
    /// libaom 这条最后兜底路并抛出"avifenc 不可用"，于是这一格记 missingBackend 而不是 fail
    /// （异常原文照旧逐行印在 progress.txt 里，没有被吞）。发布包恒带 avifenc ⇒ 探针为真 ⇒
    /// 任何硬件失败仍按 fail 计，所以被让出去的只有"这台机器根本没带软件编码器"这一种情形。</para>
    /// <para>成本：编码失败前，抛出点已经把对应探针跑过一遍并缓存（<c>NativeXxxEncoder._available</c>），
    /// 这里读到的是缓存，不额外起子进程。</para>
    /// </summary>
    private static bool BackendToolMissing(OutputFormat fmt, Exception root)
    {
        if (root is not (DllNotFoundException or InvalidOperationException or EntryPointNotFoundException))
            return false;
        return fmt switch
        {
            OutputFormat.JPEG_LI or OutputFormat.JPEG_GAINMAP => !JpegLiNative.IsAvailable,
            OutputFormat.JPEG_XL => !NativeJxlEncoder.IsAvailable,
            OutputFormat.WebP => !NativeWebPEncoder.IsAvailable,
            OutputFormat.AVIF => !NativeAvifEncoder.IsAvailable,
            _ => false,   // PNG / TIFF 是托管编码器，没有"外部工具缺失"这一说
        };
    }

    // ══════════════════════════════════════════════════════════════════════
    //  高精度源探针 —— §28「源精度由显示器能力决定」的端到端行为判据
    // ══════════════════════════════════════════════════════════════════════
    //
    // 为什么必须在 App 进程内：Test 工程只引用 Core，碰不到 App 层的 WgcCaptureService；
    // 实机套件 --wgc-avif-tests 又是自建会话 + 帧池硬编码 B8G8R8A8，根本不经过
    // CaptureMonitorAsync ⇒ 那条策略改对改错都没有任何判据会红。这里走【生产入口】补上。
    //
    // 三条判据互相独立，数字全部进报告：
    //  (a) HdrPixels is not null —— Float16 会话真建起来、真成了帧、尺寸与 SDR 一致；
    //  (b) 整格占比 —— float 值离【本帧纸白下的 8-bit 整格】(1/(255×纸白)) 有多远（真 8-bit 源几乎全落在整格上）；
    //  (c) 中性像素去重量级数 —— 给 (b) 兜底，见 AnalyzeHdrFloat 的注释。
    //
    // 判据自己也要有证据（RunHdrJudgeCheck）：四帧【合成】台阶帧（纸白 1.0 与 1.5 各一对）跑同一条 JudgeHdrFloat，
    // 要求 256 级判 FAIL、1024 级判 PASS。这条自检今天抓到两个死缺陷 —— NeutralN 从未 ++、
    // 整格容差 0.6 > 0.5 使判据恒真 —— 两者都无法靠"真机跑一次"发现。

    // 阈值（报告里原样复述，便于跨机器比较）
    private const double GridLockedPct = 99.0;      // 整格占比 ≥ 此值 ⇒ float 帧仍是 8-bit 台阶
    private const double GridFreePct = 95.0;        // 整格占比 ≤ 此值 ⇒ 明显偏离 1/256 整格
    private const int NeutralLevelsPass = 320;      // 中性级数 ≥ 此值 ⇒ 精度确实不止 8-bit（>256 且留余量）
    private const int NeutralLevelsFail = 260;      // 中性级数 ≤ 此值 ⇒ 精度止步于 8-bit
    private const int MinSampledPixels = 20_000;    // 抽样像素下限，不足 → SKIP
    private const int MinNeutralPixels = 500;       // 中性像素下限（级数统计的样本量），不足 → SKIP
    private const int MinInRangeSamples = 200;      // [0,1] 内通道值下限，不足 ⇒ (b) 判据不适用 → SKIP
    private const int MinContentLevelsForFail = 64; // 画面至少这么多不同级数，整格占比才谈得上"否证 8-bit"
    private const double HeadroomMinPct = 0.05;     // >1.0 占比下限 ⇒ "确有 SDR 白之外的内容"
    private const double HeadroomMaxPct = 50.0;     // 再高就更可能是白点缩放上移，而非扩展范围

    private const string ProbeNote = "高精度源探针 (真实 WGC 捕获 → float 帧精度)";
    private const string JudgeNote = "高精度源探针判据自检 (合成台阶帧，无环境依赖)";

    /// <summary>跑高精度源探针。返回 (名称, ok/fail/skip, 一句实测数字, 耗时, 追加的 JSON 数字字段)。</summary>
    private static (string Note, string Status, string Detail, long Ms, string JsonExtra) RunHdrSourceProbe(string outDir)
    {
        var sw = Stopwatch.StartNew();
        // 能力描述行：任何一条出口都带上它，让"为什么 SKIP"在输出里自证
        string capLine = "屏枚举未完成";
        try
        {
            // ── 1. 先问能力（与 CanDeliverExtendedRange 同一判据来源：DXGI + ACM）──
            // 能力不足时直接 SKIP，【不发起捕获】：无 GPU / 无桌面的会话（CI）在这里就被挡住，
            // 不会白付 Float16 首帧超时。这与 §28 的负缓存同源，只是这里连试都不必试。
            var displays = TrueToneCap.Core.Capture.DisplayEnumerator.EnumerateDisplays();
            bool acm = CapabilityService.DetectDisplayState().acm;
            var target = displays.FirstOrDefault(d => d.IsHdr)
                         ?? displays.FirstOrDefault(d => d.SupportsHdr)
                         ?? displays.FirstOrDefault(d => d.BitsPerColor >= 10);
            if (target is null && acm)
                target = displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();

            capLine = target is null
                ? $"屏 {displays.Count} 个，无一 IsHdr/SupportsHdr/BitsPerColor≥10，ACM={Yes(acm)}"
                : $"目标屏={target.Name} IsHdr={Yes(target.IsHdr)} SupportsHdr={Yes(target.SupportsHdr)} " +
                  $"BitsPerColor={target.BitsPerColor} 色域={target.ColorSpace} 峰值={target.MaxLuminance:F0}nits ACM={Yes(acm)}";

            if (target is null)
                return Probe("skip", sw, outDir,
                    $"HdrPixels==null? 未捕获 | {capLine} | SKIP: 能力不足 ⇒ §28 的正确行为就是退 8-bit BGRA，" +
                    $"判据不适用（阈值 整格≥{GridLockedPct:F0}%且级数≤{NeutralLevelsFail} 才 FAIL、" +
                    $"整格≤{GridFreePct:F0}%且级数≥{NeutralLevelsPass} 才 PASS）");

            // ── 2. 真实捕获：生产入口 CaptureMonitorAsync（PreferHdr=true，与三条截图路径一致）──
            // 本模式跑在 AppServices.Initialize() 之前 ⇒ AppServices.Wgc 不可用（Provider 会抛），
            // 这里直接 new 服务实例，走的仍是同一个公开方法。副作用正好合用：
            // CanDeliverExtendedRange 里的 ACM 回退读 AppServices.Settings 会抛 ⇒ 其 catch 是
            // "宽松放行" ⇒ Float16 会话一定会被尝试。
            var cfg = new WgcCaptureConfig
            {
                TargetMonitor = target.MonitorHandle,
                PreferHdr = true,
                FrameTimeoutMs = 4000,
            };
            TrueToneCap.App.Models.CaptureResult? cap = null;
            var wgc = new WgcCaptureService();
            try
            {
                // 两次机会：会话池是持久的，第一次常有"帧池刚建、DWM 还没成帧"的时间差
                for (int attempt = 0; attempt < 2 && cap?.HdrPixels is null; attempt++)
                {
                    var t = Task.Run(() => wgc.CaptureMonitorAsync(cfg));
                    // 整体 25s 上限：环境起不来的会话（无 GPU/无桌面）不该把自测拖成挂死
                    if (!t.Wait(TimeSpan.FromSeconds(25)))
                        return Probe("skip", sw, outDir,
                            $"HdrPixels==null? 未确定 | {capLine} | SKIP: 捕获 25s 未返回（会话建不起来或无桌面会话），判据无法评估");
                    cap = t.Result;
                }
            }
            finally
            {
                try { wgc.Dispose(); } catch { }   // 有界（每会话排空回调最多 2s），不会拖住退出
            }
            if (cap is null)
                return Probe("skip", sw, outDir, $"HdrPixels==null? 未确定 | {capLine} | SKIP: 捕获未返回结果");

            bool hdrNull = cap.HdrPixels is null;
            string head = $"HdrPixels==null? {Yes(hdrNull)} | 帧 {cap.Width}x{cap.Height} 捕获耗时 {cap.CaptureTimeMs}ms"
                          + (cap.DegradationWarning is null ? "" : $" ⚠降级:{cap.DegradationWarning}")
                          + $" | {capLine}";

            // ── 3. 判据 (a)：能力判定放行、SDR 帧也拿到了，却没拿到 float ──
            if (hdrNull)
            {
                // HDR 模式下 DWM 本就该以扩展范围合成 ⇒ 拿不到 float = §28 没兑现，判 FAIL。
                // 非 HDR 模式（只是 ≥10-bit 面板或 ACM）则两种可能都成立：DWM 在该模式下
                // 可以合理地不为 Float16 成帧。此时不判 FAIL，但要把代价说清楚 ——
                // 能力判定是"宽松放行"，而 s_hdrCapabilityCache 只由能力写入 ⇒ 这种屏上
                // 每次截图都会白付一次 Float16 首帧超时（见 WgcCaptureService 2026-09-15 那条修复）。
                bool hdrMode = target.IsHdr;
                return Probe(hdrMode ? "fail" : "skip", sw, outDir,
                    head + (hdrMode
                        ? " | FAIL: 屏处于 HDR 模式，Float16 会话两次都没交付像素 ⇒ 下游拿到的仍是 8-bit BGRA（§28 未兑现）"
                        : " | SKIP: 非 HDR 模式下 Float16 未成帧，无法据此判定策略失败；但 §28 的宽松放行会让每次截图都白付一次 Float16 首帧超时"));
            }

            // ── 4. 判据 (b)(c) ──
            var st = AnalyzeHdrFloat(cap.HdrPixels!, cap.Width, cap.Height, PaperWhiteScrgb());
            // 读数与结论分家：结论是纯函数 JudgeHdrFloat —— 合成帧自检走【同一条】判定分支，
            // 这样才能证明"这把尺子会红"，而不是只证明"这次量出来是好的"。
            string numbers = st.NumbersText;
            string thresholds = HdrJudgeThresholds;
            var (status, verdict) = JudgeHdrFloat(st);

            string extra =
                $",\n      \"hdrPixelsNull\": {Bool(hdrNull)}, \"sampledPixels\": {st.Pixels}, \"inRangeChannels\": {st.GridN}, " +
                $"\n      \"gridPctRaw\": {F(st.GridRawPct)}, \"gridPctSrgbDecoded\": {F(st.GridEncPct)}, \"gridPctUsed\": {F(st.GridPct)}, " +
                $"\n      \"neutralPixels\": {st.NeutralN}, \"neutralLevels\": {st.NeutralLevels}, \"neutralLevelsSaturated\": {Bool(st.LevelsSaturated)}, " +
                $"\n      \"contentLevels\": {st.ContentLevels}, \"contentLevelsSaturated\": {Bool(st.ContentSaturated)}, " +
                $"\n      \"floatMin\": {F(st.Min)}, \"floatMax\": {F(st.Max)}, \"paperWhite\": {F(st.PaperWhite)}, " +
                $"\"aboveOnePct\": {F(st.Above1Pct)}, \"aboveWhitePct\": {F(st.AboveWhitePct)}, \"belowZeroPct\": {F(st.Below0Pct)}";
            return Probe(status, sw, outDir, $"{head} | {numbers} | {verdict} {thresholds}", extra);
        }
        catch (Exception ex)
        {
            // 取最内层异常（与编码用例同一口径）
            var root = ex;
            while (root.InnerException is not null) root = root.InnerException;
            // 捕获层起不来（无 GPU / 无桌面 / WGC 不可用）属环境限制 ⇒ SKIP；
            // 其余异常是探针自身的问题 ⇒ FAIL。两者都必须落进报告，绝不冒泡带崩进程。
            bool environmental = root is InvalidOperationException or NotImplementedException
                or NotSupportedException or TimeoutException or OperationCanceledException
                or System.Runtime.InteropServices.COMException
                or System.ComponentModel.Win32Exception
                or EntryPointNotFoundException;
            var status = environmental ? "skip" : "fail";
            return Probe(status, sw, outDir,
                $"HdrPixels==null? 未确定 | {capLine} | {status.ToUpperInvariant()}: {root.GetType().Name}: {root.Message}");
        }
    }

    /// <summary>探针出口：停表 + 落 progress.txt + 落应用日志，保证"没打印就等于没跑"不成立。</summary>
    private static (string Note, string Status, string Detail, long Ms, string JsonExtra) Probe(
        string status, Stopwatch sw, string outDir, string detail, string jsonExtra = "")
    {
        sw.Stop();
        Progress(outDir, $"HDR_SOURCE {status} {sw.ElapsedMilliseconds}ms {detail}");
        try { LogService.Info("SelfTest", $"[hdr-probe] {status.ToUpperInvariant()} ({sw.ElapsedMilliseconds}ms) {detail}", LogCategory.Capture); }
        catch { }
        Console.WriteLine($"[selftest] HDR_SOURCE {status} {detail}");
        return (ProbeNote, status, detail, sw.ElapsedMilliseconds, jsonExtra);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  判据本体 + 合成帧自检
    // ─────────────────────────────────────────────────────────────────────
    //
    // 探针只在真机、且只在屏有高精度能力时才有读数；而"读数一片好"看不出【尺子本身是死是活】。
    // 这次就是靠两帧合成数据抓到判据自身的两个缺陷（详见下面两处 2026-09-19 注释）：
    //   · 中性像素计数从未 ++ ⇒ NeutralN 恒 0 ⇒ 探针在任何机器上最多只能输出 SKIP；
    //   · 整格容差 0.6 LSB8 > 0.5 ⇒ |q−round(q)|≤0.5 恒成立 ⇒ 判据 (b) 恒为 100%，
    //     FAIL 分支失去分辨力、:424 那条 PASS 分支永不可达。
    // 两个缺陷都不会被"真机跑一次看看"发现 —— 前者永远 SKIP（看起来像环境问题），
    // 后者永远 100%（看起来像"帧就是 8-bit 台阶"，反而像在用证据支持结论）。
    // 合成帧比现实更苛刻：纯净台阶、无 dither、无浮点合成 ⇒ 连它都分不开，就不配判真实桌面。

    /// <summary>阈值原文（报告里复述，便于跨机器比较）。</summary>
    private static string HdrJudgeThresholds =>
        $"（阈值: 整格≥{GridLockedPct:F0}%且中性级数≤{NeutralLevelsFail}且无 >纸白 样本⇒FAIL；" +
        $"整格≤{GridFreePct:F0}%且(中性级数≥{NeutralLevelsPass} 或 >纸白 占 {F(HeadroomMinPct)}%–{F(HeadroomMaxPct)}%)⇒PASS；其余⇒SKIP）";

    /// <summary>把实测数字变成结论 —— 纯函数，真机探针与合成帧自检走的是这一条分支。</summary>
    private static (string Status, string Verdict) JudgeHdrFloat(HdrFloatStats st)
    {
        // 头部余量：8-bit 帧在结构上给不出"越出自己台阶"的值（色域矩阵行和为 1、元素非负，也造不出 <0），
        // 所以"确实有样本亮过【本帧的纸白】"是与 gamma/矩阵无关的独立证据。
        // ⚠ 2026-09-28 批次 D：这条以前锚在 1.0 上，而 1.0 只在"纸白 = 80 nits"那一块屏上才等于 SDR 白；
        //   本机 120 nits ⇒ SDR 内容整体压在 1.5，">1.0 占比"量的是桌面有多亮（一周内读到 1.5%–92%），
        //   既让这一支恒假，又让下面 fitsSdrBox 恒假 ⇒ FAIL 那一支永远打不出来。现在两条都用 st.PaperWhite。
        // 上限 50% 的剩余用途不是排"白点缩放"（缩放已被锚处理），而是【怀疑锚自己】：
        // 过半像素都在纸白之上，更可能是纸白取错了屏 / 整帧本来就是 HDR 场景，那时"头部余量"这个说法失去意义。
        bool headroom = st.AboveWhitePct >= HeadroomMinPct && st.AboveWhitePct <= HeadroomMaxPct;
        bool fitsSdrBox = st.AboveWhitePct < 0.01 && st.Below0Pct < 0.01;

        if (st.Pixels < MinSampledPixels || st.NeutralN < MinNeutralPixels)
            return ("skip", $"SKIP: 样本不足（抽样需 ≥{MinSampledPixels} px 且中性像素需 ≥{MinNeutralPixels} 个）⇒ 级数统计没有意义，不判 PASS");

        if (st.NeutralLevels >= NeutralLevelsPass && headroom)
            // 最强的一条：级数与头部余量都不依赖"帧处在哪个域"（gamma/白点缩放/色域矩阵
            // 都改变不了中性像素有多少个不同级数，也造不出 8-bit 表达不了的 >1.0 值）
            return ("ok", $"PASS: 中性像素去重量级 {st.LevelsText} 破 256 上限，且 SDR 白之上确有头部余量（>1.0 占 {F(st.Above1Pct)}%）" +
                          " ⇒ 与 gamma/矩阵/白点无关地证明：下游拿到的精度不止 8-bit");

        if (st.GridN < MinInRangeSamples)
            // 几乎所有值都在 [0,1] 之外 ⇒ 帧的参考白点不是 1.0（或整屏都是 HDR 内容），
            // 整格占比失去分母，也无法与"白点缩放上移"区分 —— (b) 在这里不可信。
            return ("skip", $"SKIP: [0,1] 内通道值仅 {st.GridN} 个（<{MinInRangeSamples}）⇒ 整格判据失去参照（白点缩放未知），代理指标不可信");

        if (st.ContentLevels < MinContentLevelsForFail && st.NeutralLevels <= NeutralLevelsFail)
            // 画面本来就单调（纯色壁纸 + 一个白窗口）⇒ "全部落在整格上"没有信息量，
            // 既不能证真也不能证伪，按 SKIP 处理（判据值仍然全部打印）。
            return ("skip", $"SKIP: 画面太单调（[0,1] 内只有 {st.ContentText} 种不同值 < {MinContentLevelsForFail}）" +
                            " ⇒ 整格占比没有可分辨的台阶，判据不成立");

        if (st.GridPct >= GridLockedPct && st.NeutralLevels <= NeutralLevelsFail && fitsSdrBox)
            // 整格占比 + 中性级数 + "全在 SDR 框内" 三条同时成立 ⇒ Float16 帧里没有一丝
            // 8-bit 表达不了的东西 —— 这正是"容器声明高位深、源只有 8-bit"的假高精度。
            return ("fail", "FAIL: Float16 帧仍是纯 8-bit 台阶（升位深式假高精度，§28 未兑现）");

        if (st.GridPct <= GridFreePct && (st.NeutralLevels >= NeutralLevelsPass || headroom))
            return ("ok", "PASS: float 帧明显偏离 1/256 整格"
                          + (st.NeutralLevels >= NeutralLevelsPass ? $"，且中性级数 {st.LevelsText} 破 256 上限" : "")
                          + (headroom ? $"，且真有 SDR 白之上的头部余量（>1.0 占 {F(st.Above1Pct)}%）" : "")
                          + " ⇒ 下游拿到的精度确实不止 8-bit");

        return ("skip", "SKIP: 证据落在灰区或互相矛盾（色域矩阵/浮点合成会洗掉整格判据，级数与整格不同向时不猜）");
    }

    /// <summary>造一帧纯台阶合成图：每像素 R=G=B=列号/(levels−1)，无噪声、无 dither、无浮点合成。</summary>
    private static (float[] Px, int W, int H) StepFrame(int levels)
    {
        // 行数按 levels 反比缩放：保证抽样量 ≥MinSampledPixels(2 万) 又不必为 1024 级造 400 万像素
        int w = levels, h = Math.Max(128, 65_000 / levels);
        var px = new float[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float v = (float)x / (levels - 1);
                int i = (y * w + x) * 4;
                px[i] = v; px[i + 1] = v; px[i + 2] = v; px[i + 3] = 1f;
            }
        return (px, w, h);
    }

    /// <summary>把整帧 RGB 抬到指定纸白（α 不动）—— 模拟 DWM 把 SDR 内容合成进 HDR 桌面。</summary>
    private static (float[] Px, int W, int H) Scaled((float[] Px, int W, int H) f, float k)
    {
        var px = (float[])f.Px.Clone();
        for (int i = 0; i + 3 < px.Length; i += 4) { px[i] *= k; px[i + 1] *= k; px[i + 2] *= k; }
        return (px, f.W, f.H);
    }

    /// <summary>
    /// 判据自检：256 级台阶帧【必须】判 FAIL（它就是"升位深式假高精度"的样子），
    /// 1024 级台阶帧【必须】判 PASS。跑不通就说明判据是死的 —— 此时 HDR_SOURCE 的读数一律作废。
    /// </summary>
    private static (string Note, string Status, string Detail, long Ms, string JsonExtra) RunHdrJudgeCheck(string outDir)
    {
        var sw = Stopwatch.StartNew();
        var f8 = StepFrame(256);
        var f10 = StepFrame(1024);
        var s8 = AnalyzeHdrFloat(f8.Px, f8.W, f8.H);
        var s10 = AnalyzeHdrFloat(f10.Px, f10.W, f10.H);
        var (st8, v8) = JudgeHdrFloat(s8);
        var (st10, v10) = JudgeHdrFloat(s10);

        // ═══ 批次 D（2026-09-28）：同一对台阶帧抬到"这一档纸白"再判一遍 ═══
        // 台阶帧默认纸白 = 1.0，等于只在"SdrWhiteLevel 恰好 80 nits"那种屏上自证。而本机纸白 120 nits，
        // DWM 把 SDR 内容整体抬到 1.5 ⇒ 一份货真价实的 8-bit 内容，在写死 1/255 的整格尺子上只剩约一半
        // 落格、且 ">1.0 占比" 落进头部余量区间 ⇒ 旧判据会把它读成 **PASS**（假绿，正是 §28 要抓的形状）。
        // 这里把"旧锚点判成什么"一并量出来，而不是只在注释里说：夹具本身即证据。
        const float FixtureWhite = 1.5f;          // 120 nits / 80
        var g8 = Scaled(f8, FixtureWhite);
        var g10 = Scaled(f10, FixtureWhite);
        var t8 = AnalyzeHdrFloat(g8.Px, g8.W, g8.H, FixtureWhite);
        var t10 = AnalyzeHdrFloat(g10.Px, g10.W, g10.H, FixtureWhite);
        var (st8w, v8w) = JudgeHdrFloat(t8);
        var (st10w, v10w) = JudgeHdrFloat(t10);
        string oldAnchor8 = JudgeHdrFloat(AnalyzeHdrFloat(g8.Px, g8.W, g8.H)).Status;   // 纸白仍按 1.0 判
        sw.Stop();

        bool ok = st8 == "fail" && st10 == "ok" && st8w == "fail" && st10w == "ok";
        string status = ok ? "ok" : "fail";
        string detail =
            $"256 级台阶帧（应 FAIL）→ 实判 {st8.ToUpperInvariant()}：整格 {F(s8.GridPct)}%、中性级数 {s8.LevelsText}、中性像素 {s8.NeutralN} | " +
            $"1024 级台阶帧（应 PASS）→ 实判 {st10.ToUpperInvariant()}：整格 {F(s10.GridPct)}%、中性级数 {s10.LevelsText}、中性像素 {s10.NeutralN} | " +
            $"纸白 {F(FixtureWhite)} 同一对（8bit 应 FAIL / 10bit 应 PASS）→ 实判 {st8w.ToUpperInvariant()} / {st10w.ToUpperInvariant()}" +
            $"：整格 {F(t8.GridPct)}%、>纸白 {F(t8.AboveWhitePct)}%，而【旧锚点(纸白按 1.0)】把这份 8-bit 判成 {oldAnchor8.ToUpperInvariant()}" +
            (ok ? " ⇒ 判据会红也会绿，且在纸白≠80nits 的屏上仍然有牙，HDR_SOURCE 的读数可信"
                : $" ⇒ 判据自身失效（{v8} / {v10} / {v8w} / {v10w}）⇒ 本次 HDR_SOURCE 读数一律作废");

        // 与探针同一出口纪律：既落 progress.txt 也打控制台 —— "没打印就等于没跑"不成立。
        Progress(outDir, $"HDR_JUDGE {status} {sw.ElapsedMilliseconds}ms {detail}");
        Console.WriteLine($"[selftest] HDR_JUDGE {status} {detail}");

        return (JudgeNote, status, detail, sw.ElapsedMilliseconds,
            $",\n      \"latticePct8bit\": {F(s8.GridPct)}, \"latticePct10bit\": {F(s10.GridPct)}, " +
            $"\n      \"fixturePaperWhite\": {F(FixtureWhite)}, \"verdict8bitAtWhite\": \"{st8w}\", " +
            $"\"verdict8bitAtWhiteOldAnchor\": \"{oldAnchor8}\", \"latticePct8bitAtWhite\": {F(t8.GridPct)}, " +
            $"\n      \"levels8bit\": {s8.LevelsText}, \"levels10bit\": {s10.LevelsText}, \"neutralPixels8bit\": {s8.NeutralN}");
    }

    /// <summary>
    /// 默认值迁移门的自检：把两份夹具喂进<b>生产那条分支</b>（<c>SettingsService.MigrateStaleDefaults</c>），
    /// 要求"无修订号的老文件必须被交还默认表""带修订号的文件必须原样不动"。
    /// <para>为什么只能在 App 进程内跑：Test 工程只引用 Core、碰不到 <c>AppSettingsData</c>，
    /// 而这条门的语义恰好发生在「JSON 缺键 → 属性初始化器填什么」这一步 ——
    /// 源码哨兵能钉初值的写法，钉不住 System.Text.Json 拿它补缺键的<b>实际结果</b>。</para>
    /// <para>实机触发过一次（交接 §49）：一份 2026-09-16 的全量序列化旧文件被
    /// <c>MigrateFromLegacyLocation</c> 搬进发布包目录，其中 <c>QualityAvif</c>=63（CRF 区间上界=最差档）
    /// 一路活到界面显示 <c>CRF: 63</c>。</para>
    /// <para>四条读数的性质：<b>独立参照</b>=缺键读作 0、rev-1 文件必须重迁、反例的 63/50/3 必须仍在
    /// （都是字面量，不由被测常数推导）；<b>构造恒等</b>=正例迁后各键 == <c>CreateDefaults()</c>
    /// （该表的字面量另由门禁哨兵钉死为 15/90/1.0/1.0/1.0，故此处不重复钉数字，
    /// 但单看这一条不足以证明门有牙 —— 反例那条才是）。</para>
    /// </summary>
    private static (string Note, string Status, string Detail, long Ms, string JsonExtra)
        RunSettingsMigrationCheck(string outDir)
    {
        const string Note = "老设置文件的默认值交还（迁移门）";
        var sw = Stopwatch.StartNew();

        // 夹具=本机真实存在的那份 2026-09-16 全量序列化文件（键名、取值、顺序都照抄，不按被测阈值反造）
        // ⚠ 这是"GainMap 并入 JPEG LI"之前的文件 ⇒ 里面的 FormatIndex:4 按【旧表】是 AVIF（新表 4 是 WebP）。
        //   本自检不碰 FormatIndex（重映射另有 GAINMAP_MIGRATION 那条），留着原值只是为了让夹具像真的。
        const string legacy =
            """{"SettingsVersion":1,"FormatIndex":4,"Quality":100,"QualityGainMap":3,"QualityJpegLi":0.5,"QualityJpegXl":0.5,"QualityAvif":63,"QualityWebp":50,"ColorSpaceIndex":5,"ChromaAvif":"444"}""";
        // 修订号 1 那批文件：顶着"已迁移"的章、值却是迁移失效期写下的 ⇒ 必须按作废处理
        string rev1 = "{\"SettingsRevision\":1," + legacy[1..];
        // 与当前修订号一致的文件必须一律不动（否则"把缺键判成永远未迁移"这种伪修法也能过检）
        string stamped = $"{{\"SettingsRevision\":{AppSettingsData.CurrentSettingsRevision}," + legacy[1..];

        var ctx = AppJsonContext.Default;
        var a = JsonSerializer.Deserialize(legacy, ctx.AppSettingsData);
        var r1 = JsonSerializer.Deserialize(rev1, ctx.AppSettingsData);
        var b = JsonSerializer.Deserialize(stamped, ctx.AppSettingsData);
        if (a is null || b is null || r1 is null)
            return (Note, "fail", "反序列化夹具失败（返回 null）", sw.ElapsedMilliseconds, "");

        // ⚠ 必须在调用迁移之前取值：MigrateStaleDefaults 是就地改写 s 的，
        // 迁移后再读 a.SettingsRevision 得到的永远是"当前号"，那一条判据就自证成功了。
        int revRead = a.SettingsRevision;
        bool revReadAsStale = revRead == 0;

        var q0 = (a.QualityAvif, a.QualityWebp, a.QualityGainMap, a.ColorSpaceIndex);
        bool fired = SettingsService.MigrateStaleDefaults(a);
        var d = AppSettingsData.CreateDefaults();
        bool handedBack = fired
            && a.QualityAvif == d.QualityAvif && a.QualityWebp == d.QualityWebp
            && a.QualityGainMap == d.QualityGainMap && a.QualityJpegLi == d.QualityJpegLi
            && a.QualityJpegXl == d.QualityJpegXl && a.ColorSpaceIndex == d.ColorSpaceIndex
            && a.SettingsRevision == AppSettingsData.CurrentSettingsRevision;

        // rev 1 作废：光看初值修不好已经盖了章的那批文件
        bool rev1Refired = SettingsService.MigrateStaleDefaults(r1)
            && r1.QualityAvif == d.QualityAvif && r1.SettingsRevision == AppSettingsData.CurrentSettingsRevision;

        bool keptIntact = !SettingsService.MigrateStaleDefaults(b)
            && b.QualityAvif == 63 && b.QualityWebp == 50 && b.QualityGainMap == 3;

        sw.Stop();
        bool ok = revReadAsStale && handedBack && rev1Refired && keptIntact;
        string status = ok ? "ok" : "fail";
        string detail =
            $"缺键的老文件读作 SettingsRevision={revRead}（应为 0）| " +
            $"门是否触发={fired}，把 {F(q0.Item1)}/{F(q0.Item2)}/{F(q0.Item3)}/cs{q0.Item4} 交还为 " +
            $"{F(a.QualityAvif)}/{F(a.QualityWebp)}/{F(a.QualityGainMap)}/cs{a.ColorSpaceIndex} | " +
            $"盖章为 1 的文件重迁={rev1Refired}（AVIF 现 {F(r1.QualityAvif)}，当前修订号={AppSettingsData.CurrentSettingsRevision}）| " +
            $"盖章为当前号的同内容文件：不动={keptIntact}（AVIF 仍 {F(b.QualityAvif)}）" +
            (ok ? " ⇒ 门是活的，老用户的旧质量档会被交还默认表一次后由用户自选"
                : " ⇒ 门是死的：老文件里的区间端点会被当成用户选择永久保留，界面上就是 CRF: 63");

        Progress(outDir, $"SETTINGS_MIGRATION {status} {sw.ElapsedMilliseconds}ms {detail}");
        Console.WriteLine($"[selftest] SETTINGS_MIGRATION {status} {detail}");

        return (Note, status, detail, sw.ElapsedMilliseconds,
            $",\n      \"readRevision\": {revRead}, \"gateFired\": {Bool(fired)}, " +
            $"\"handedBack\": {Bool(handedBack)}, \"rev1Refired\": {Bool(rev1Refired)}, " +
            $"\"stampedKeptIntact\": {Bool(keptIntact)}, \"legacyAvifCrf\": {F(q0.Item1)}");
    }

    /// <summary>
    /// 越界 / 非法设置值的归一回落自检（2026-09-29 审查批次 E）。
    /// <para>为什么必须真调一次：B 域台账（本轮批次 C 的 L5 那份）是<b>读码</b>得出的，而这张表里
    /// 两类回落的方向恰好相反 —— 质量越界<b>交还默认</b>（AVIF <c>999</c> ⇒ 15，<b>不是</b>钳到区间
    /// 端点 63），位深越界<b>就近收敛</b>（AVIF <c>16</c> ⇒ 12，<b>不是</b>回落 8）。把其中一类的方向
    /// 写反，读码时没人会发现，产物却每一张都偏 ⇒ 在这里把"越界前 → 实得"逐轴打到进程输出上。</para>
    /// <para>三条臂各管一件事，缺一条就有能蒙过去的错法：
    /// ① <b>越界臂</b>钉回落目标；
    /// ② <b>合法非默认臂</b>钉"回落不是无条件的"——若有人把这段写成"一律回默认"，只有这一臂会红；
    /// ③ <b>大小写臂</b>钉"改写成界面 Tag 的确切拼法"而不是回落默认——本轮 <c>FitTag</c> 修复
    /// （旧白名单写 "RGB"、XAML 挂 "Rgb"，而回填下拉用 <c>==</c> 比）的直接产物。</para>
    /// <para>期望值一律写字面量，不从被测代码的常数推导（同 SETTINGS_MIGRATION 那条反例的纪律）。
    /// 唯一例外是 <c>FormatIndex</c> 的回落落点：那张顺序表本身就是持久化契约（Core 的
    /// <c>FormatOrder</c> 另有逐格对撞），这里钉的是"越界必须落在 PNG 那一项"。</para>
    /// <para>只能在 App 进程内跑：理由与 <see cref="RunSettingsMigrationCheck"/> 同——Test 工程只引用
    /// Core，够不到 <c>AppSettingsData</c>；而把夹具写进盘上那份 settings.json 借 <c>Load()</c> 触发，
    /// 等于为了测试改用户机器上的设置。</para>
    /// </summary>
    private static (string Note, string Status, string Detail, long Ms, string JsonExtra)
        RunNormalizeGateCheck(string outDir)
    {
        const string Note = "越界设置的归一回落（Normalize 分支直调）";
        var sw = Stopwatch.StartNew();
        var ctx = AppJsonContext.Default;

        var got = new List<string>();
        int bad = 0;
        void Expect(string arm, string label, string input, string? actual, string want)
        {
            bool ok = actual == want;
            if (!ok) bad++;
            got.Add($"{arm}·{label} {input}→{actual ?? "<null>"}" + (ok ? "" : $"（期望 {want}）❌"));
        }

        // ── 臂①：越界值必须回落。质量轴交还默认，位深轴就近收敛，字符串轴回落默认 ──
        var oob = SettingsOrNull(ctx, Json(
            ("SettingsRevision", "2"),
            ("QualityPng", "50"), ("QualityGainMap", "99"), ("QualityJpegLi", "0.1"), ("QualityJpegXl", "9"),
            ("QualityAvif", "999"), ("QualityWebp", "3"), ("QualityTiff", "0"),
            ("FormatIndex", "99"), ("ColorSpaceIndex", "6"),
            ("BitDepthPng", "17"), ("BitDepthJpegLi", "10"), ("BitDepthJpegXl", "12"), ("BitDepthAvif", "16"),
            ("BitDepthWebP", "10"), ("BitDepthTiff", "10"), ("BitDepthGainMap", "12"),
            ("ChromaAvif", "\"999\""), ("ChromaJpegXl", "\"422\""), ("ChromaJpegLi", "\"42\""), ("ChromaGainMap", "\"0\""),
            ("ThemeMode", "\"neon\""), ("Language", "\"fr\""), ("OcrEngineMode", "\"DirectX\""),
            ("GainMapMode", "\"Mono\""), ("ArchiveMode", "\"Week\""), ("ToastPosition", "\"Middle\""),
            ("AvifEncoder", "\"Nvidia\"")));
        if (oob is null) return (Note, "fail", "反序列化越界夹具失败（返回 null）", sw.ElapsedMilliseconds, "");
        SettingsService.Normalize(oob);
        Expect("越界", "QualityPng", "50", F(oob.QualityPng), "100.00");
        Expect("越界", "QualityGainMap", "99", F(oob.QualityGainMap), "1.00");
        Expect("越界", "QualityJpegLi", "0.1", F(oob.QualityJpegLi), "1.00");
        Expect("越界", "QualityJpegXl", "9", F(oob.QualityJpegXl), "1.00");
        // 这条是整张表最容易读反的一格：CRF 区间是 0–63，越界**不钳到 63**，而是交还默认 15
        Expect("越界", "QualityAvif", "999", F(oob.QualityAvif), "15.00");
        Expect("越界", "QualityWebp", "3", F(oob.QualityWebp), "90.00");
        Expect("越界", "QualityTiff", "0", F(oob.QualityTiff), "100.00");
        Expect("越界", "FormatIndex", "99", oob.FormatIndex.ToString(), "0");   // 落点 = PNG
        Expect("越界", "ColorSpaceIndex", "6", oob.ColorSpaceIndex.ToString(), "0");
        Expect("越界", "BitDepthPng", "17", oob.BitDepthPng.ToString(), "8");
        Expect("越界", "BitDepthJpegLi", "10", oob.BitDepthJpegLi.ToString(), "8");   // 编码器无位深概念
        Expect("越界", "BitDepthJpegXl", "12", oob.BitDepthJpegXl.ToString(), "10");  // JXL 只认 8/10/16
        Expect("越界", "BitDepthAvif", "16", oob.BitDepthAvif.ToString(), "12");      // 就近收敛，不回落 8
        Expect("越界", "BitDepthWebP", "10", oob.BitDepthWebP.ToString(), "8");
        Expect("越界", "BitDepthTiff", "10", oob.BitDepthTiff.ToString(), "16");      // >8 一律保精度到 16
        Expect("越界", "BitDepthGainMap", "12", oob.BitDepthGainMap.ToString(), "8");
        Expect("越界", "ChromaAvif", "999", oob.ChromaAvif, "420");
        Expect("越界", "ChromaJpegXl", "422", oob.ChromaJpegXl, "444");  // JXL 值域不含 422 ⇒ 走自己的表
        Expect("越界", "ChromaJpegLi", "42", oob.ChromaJpegLi, "420");
        Expect("越界", "ChromaGainMap", "0", oob.ChromaGainMap, "420");
        Expect("越界", "ThemeMode", "neon", oob.ThemeMode, "Default");
        Expect("越界", "Language", "fr", oob.Language, "zh");
        Expect("越界", "OcrEngineMode", "DirectX", oob.OcrEngineMode, "OnnxGpu");
        Expect("越界", "GainMapMode", "Mono", oob.GainMapMode, "Gray");
        Expect("越界", "ArchiveMode", "Week", oob.ArchiveMode, "Month");  // 界面从来没有这一项（2026-09-29 摘掉）
        Expect("越界", "ToastPosition", "Middle", oob.ToastPosition, "BottomRight");
        Expect("越界", "AvifEncoder", "Nvidia", oob.AvifEncoder, "Auto");

        // ── 臂②：合法但非默认的值必须原样活着（否则"一律回默认"的错法也能过检）──
        var keep = SettingsOrNull(ctx, Json(
            ("SettingsRevision", "2"),
            ("QualityAvif", "20"), ("QualityWebp", "50"), ("QualityJpegLi", "2.5"), ("QualityJpegXl", "4"),
            ("FormatIndex", "3"), ("ColorSpaceIndex", "3"),
            ("BitDepthPng", "10"), ("BitDepthAvif", "10"), ("BitDepthJpegXl", "16"), ("BitDepthTiff", "8"),
            ("ChromaAvif", "\"422\""), ("ChromaJpegXl", "\"420\""), ("ChromaJpegLi", "\"444\""),
            ("ThemeMode", "\"OLED\""), ("OcrEngineMode", "\"WindowsOcr\""), ("GainMapMode", "\"Rgb\""),
            ("ArchiveMode", "\"Day\""), ("ToastPosition", "\"WindowsNotify\""), ("AvifEncoder", "\"SvtAv1\"")));
        if (keep is null) return (Note, "fail", "反序列化合法夹具失败（返回 null）", sw.ElapsedMilliseconds, "");
        SettingsService.Normalize(keep);
        Expect("合法", "QualityAvif", "20", F(keep.QualityAvif), "20.00");
        Expect("合法", "QualityWebp", "50", F(keep.QualityWebp), "50.00");   // 区间下界也属于"用户所选"
        Expect("合法", "QualityJpegLi", "2.5", F(keep.QualityJpegLi), "2.50");
        Expect("合法", "QualityJpegXl", "4", F(keep.QualityJpegXl), "4.00");
        Expect("合法", "FormatIndex", "3", keep.FormatIndex.ToString(), "3");
        // 旧 6 项色域表里的 3=DCI_P3 / 5=BT2020：这里必须【活着交给界面】，由 MainWindow 的
        // legacyTags→currentTags 迁移按标签收口。Normalize 把它们改成 0 反而是错的
        // （等于绕过那条迁移、把用户挑好的 Rec.2020 静默换成"跟随显示器"）。
        // 本机实测老文件里就是 5 ⇒ 这一臂不是假想输入。
        Expect("合法", "ColorSpaceIndex(旧表 3)", "3", keep.ColorSpaceIndex.ToString(), "3");
        Expect("合法", "BitDepthPng", "10", keep.BitDepthPng.ToString(), "10");
        Expect("合法", "BitDepthAvif", "10", keep.BitDepthAvif.ToString(), "10");
        Expect("合法", "BitDepthJpegXl", "16", keep.BitDepthJpegXl.ToString(), "16");
        Expect("合法", "BitDepthTiff", "8", keep.BitDepthTiff.ToString(), "8");
        Expect("合法", "ChromaAvif", "422", keep.ChromaAvif, "422");         // AVIF 下拉确有 4:2:2 项
        Expect("合法", "ChromaJpegXl", "420", keep.ChromaJpegXl, "420");
        Expect("合法", "ChromaJpegLi", "444", keep.ChromaJpegLi, "444");
        Expect("合法", "ThemeMode", "OLED", keep.ThemeMode, "OLED");
        Expect("合法", "OcrEngineMode", "WindowsOcr", keep.OcrEngineMode, "WindowsOcr");
        Expect("合法", "GainMapMode", "Rgb", keep.GainMapMode, "Rgb");
        Expect("合法", "ArchiveMode", "Day", keep.ArchiveMode, "Day");
        Expect("合法", "ToastPosition", "WindowsNotify", keep.ToastPosition, "WindowsNotify");
        Expect("合法", "AvifEncoder", "SvtAv1", keep.AvifEncoder, "SvtAv1");

        // ── 臂③：大小写变体必须改写成界面 Tag 的拼法（既不原样留着，也不回落默认）──
        // 原样留着 = 本轮修掉的缺陷（SetComboByTag 用 == 匹配 ⇒ 下拉空白 + 下次保存静默翻转）；
        // 回落默认 = 另一种错法（把"用户想要 RGB 增益"读成"没设过"）。两个方向同时钉死才叫补牙。
        // 小写与大写各一枚夹具：FitTag 的匹配循环按 tags 顺序取第一个 IgnoreCase 命中，
        // 只测一侧会放过"只在某一种大小写下才改写"的错法。
        var cased = SettingsOrNull(ctx, Json(
            ("SettingsRevision", "2"),
            ("ThemeMode", "\"dark\""), ("OcrEngineMode", "\"windowsocr\""), ("GainMapMode", "\"rgb\""),
            ("ArchiveMode", "\"month\""), ("ToastPosition", "\"bottomleft\""), ("AvifEncoder", "\"libaom\"")));
        if (cased is null) return (Note, "fail", "反序列化大小写夹具失败（返回 null）", sw.ElapsedMilliseconds, "");
        SettingsService.Normalize(cased);
        Expect("小写", "ThemeMode", "dark", cased.ThemeMode, "Dark");
        Expect("小写", "OcrEngineMode", "windowsocr", cased.OcrEngineMode, "WindowsOcr");
        Expect("小写", "GainMapMode", "rgb", cased.GainMapMode, "Rgb");
        Expect("小写", "ArchiveMode", "month", cased.ArchiveMode, "Month");
        Expect("小写", "ToastPosition", "bottomleft", cased.ToastPosition, "BottomLeft");
        Expect("小写", "AvifEncoder", "libaom", cased.AvifEncoder, "LibAom");

        // 全大写这一支正是旧白名单放行过的那一串（"RGB"）：改写成 Tag，而不是回落默认
        var upper = SettingsOrNull(ctx, Json(
            ("SettingsRevision", "2"),
            ("ThemeMode", "\"LIGHT\""), ("GainMapMode", "\"RGB\""), ("ArchiveMode", "\"YEAR\""),
            ("ToastPosition", "\"TOPRIGHT\"")));
        if (upper is null) return (Note, "fail", "反序列化全大写夹具失败（返回 null）", sw.ElapsedMilliseconds, "");
        SettingsService.Normalize(upper);
        Expect("大写", "ThemeMode", "LIGHT", upper.ThemeMode, "Light");
        Expect("大写", "GainMapMode", "RGB", upper.GainMapMode, "Rgb");
        Expect("大写", "ArchiveMode", "YEAR", upper.ArchiveMode, "Year");
        Expect("大写", "ToastPosition", "TOPRIGHT", upper.ToastPosition, "TopRight");

        // 旧档 "Avifenc"（精确大小写）必须显式迁到 LibAom，不能被白名单回落顺手改成 Auto：
        // Auto 优先走硬件，那会把"用户选了软件编码器"翻转成硬件路径。
        var legacy = SettingsOrNull(ctx, """{"SettingsRevision":2,"AvifEncoder":"Avifenc"}""");
        if (legacy is null) return (Note, "fail", "反序列化 Avifenc 夹具失败（返回 null）", sw.ElapsedMilliseconds, "");
        SettingsService.Normalize(legacy);
        Expect("迁移", "AvifEncoder", "Avifenc", legacy.AvifEncoder, "LibAom");

        // 同一道迁移门的大小写臂。写它的理由不是"多一格 coverage"，而是两道工具口径必须同向：
        // FitTag 按 IgnoreCase 认得 "avifenc"，而迁移门原先按精确大小写比 ⇒ 小写那份【逃过迁移】，
        // 直接被白名单回落成 Auto = 优先走硬件，正是上面那句 ⚠ 禁止的翻转。旧码这一臂必红。
        var legacyLower = SettingsOrNull(ctx, """{"SettingsRevision":2,"AvifEncoder":"avifenc"}""");
        if (legacyLower is null) return (Note, "fail", "反序列化 avifenc 小写夹具失败（返回 null）", sw.ElapsedMilliseconds, "");
        SettingsService.Normalize(legacyLower);
        Expect("迁移", "AvifEncoder(小写逃过迁移门)", "avifenc", legacyLower.AvifEncoder, "LibAom");

        // 色域上界单独一枚夹具：本机老文件实测就是 5（旧 6 项表的最后一项 BT2020）。
        // 上界写死的字面量若比 legacyTags 表长小一格（把 5 写成 4），5 会被判越界 ⇒ 静默变 0=跟随显示器，
        // 用户挑的 Rec.2020 蒸发且界面上看不出来。文本侧另有 CorePipelineTests 那条"上界==表长-1"对撞，
        // 这一臂管的是【真跑一遍仍然是那个结果】。
        var csIdx = SettingsOrNull(ctx, Json(("SettingsRevision", "2"), ("ColorSpaceIndex", "5")));
        if (csIdx is null) return (Note, "fail", "反序列化色域上界夹具失败（返回 null）", sw.ElapsedMilliseconds, "");
        SettingsService.Normalize(csIdx);
        Expect("合法", "ColorSpaceIndex(旧表 5=BT2020)", "5", csIdx.ColorSpaceIndex.ToString(), "5");

        sw.Stop();
        bool ok = bad == 0;
        string status = ok ? "ok" : "fail";
        string detail = $"{got.Count} 轴读毕，{(ok ? "全部符合期望" : $"{bad} 条不符")}：" + string.Join(" | ", got);
        Progress(outDir, $"NORMALIZE {status} {sw.ElapsedMilliseconds}ms {detail}");
        Console.WriteLine($"[selftest] NORMALIZE {status} {detail}");
        return (Note, status, detail, sw.ElapsedMilliseconds,
            $",\n      \"arms\": {got.Count}, \"mismatched\": {bad}");
    }

    /// <summary>反序列化夹具；源生成上下文对非法 JSON 返回 null 而不是抛，调用方按 fail 处理。</summary>
    private static AppSettingsData? SettingsOrNull(AppJsonContext ctx, string json)
        => JsonSerializer.Deserialize(json, ctx.AppSettingsData);

    /// <summary>
    /// 用 (键, JSON 字面量) 拼一份最小 settings.json 夹具。值已经是字面量（字符串自带引号），
    /// 这里不做任何类型推断 —— "422" 这类看着像数字的色度串必须保持带引号。
    /// </summary>
    private static string Json(params (string Key, string Value)[] pairs)
    {
        if (pairs.Length == 0)
            throw new ArgumentException("夹具为空：Normalize 会被喂一份全默认对象，越界臂就此变成空转");
        return "{" + string.Join(",", pairs.Select(p => $"\"{p.Key}\":{p.Value}")) + "}";
    }

    /// <summary>
    /// 「GainMap 并入 JPEG LI」的一次性重映射自检（2026-09-26 用户口径）。
    /// <para>为什么必须行为级跑：这条改动的风险全在<b>裸索引</b>上 —— 旧表 1=GainMap、新表 1=JPEG LI，
    /// 而越界钳制对两者都不触发。重映射表本身在 Core（门禁已逐格对撞），但
    /// "表 + 反序列化 + 参数搬格 + 盖章"这条链只有 App 进程能整条跑一遍。</para>
    /// <para>夹具取本机真实形态：一份 2026-09-25 之前的文件（无 <c>GainMapMerged</c> 键、
    /// <c>FormatIndex:1</c>、GainMap 侧质量被用户改成 2.5 而 JPEG LI 侧仍是默认）。
    /// 反例两格：已盖章的文件不许再动；两侧都改过时必须保留 JPEG LI 侧而不是静默覆盖。</para>
    /// </summary>
    private static (string Note, string Status, string Detail, long Ms, string JsonExtra)
        RunGainMapMergeCheck(string outDir)
    {
        const string Note = "GainMap 并入 JPEG LI 的重映射";
        var sw = Stopwatch.StartNew();
        var ctx = AppJsonContext.Default;
        var d = AppSettingsData.CreateDefaults();

        // ① 老 GainMap 用户：格式号 1 在旧表里就是增益图
        var a = JsonSerializer.Deserialize(
            """{"FormatIndex":1,"QualityGainMap":2.5,"QualityJpegLi":1.0,"ChromaGainMap":"444","ChromaJpegLi":"420"}""",
            ctx.AppSettingsData)!;
        bool fired = SettingsService.MigrateGainMapMerge(a);
        bool gmUser = fired && a.FormatIndex == 1 && a.GainMapEnabled
            && a.QualityJpegLi == 2.5 && a.ChromaJpegLi == "444" && a.GainMapMerged;

        // ② 老 JPEG LI 用户（旧表 2 号）：必须仍停在 JPEG LI，且【不许】被打开增益图
        var b = JsonSerializer.Deserialize(
            """{"FormatIndex":2,"QualityGainMap":2.5,"QualityJpegLi":0.8}""", ctx.AppSettingsData)!;
        bool liUser = SettingsService.MigrateGainMapMerge(b)
            && b.FormatIndex == 1 && !b.GainMapEnabled
            && b.QualityJpegLi == 0.8;   // GainMap 侧改过但 LI 侧也改过 ⇒ 保留 LI 侧

        // ③ 已盖章的文件：二次启动不得再搬（否则每次启动都重算一遍历史）
        var c = JsonSerializer.Deserialize(
            """{"GainMapMerged":true,"FormatIndex":2,"QualityJpegLi":0.8}""", ctx.AppSettingsData)!;
        int cIdx = c.FormatIndex;
        bool again = SettingsService.MigrateGainMapMerge(c);
        bool idempotent = !again && c.FormatIndex == cIdx && !c.GainMapEnabled;

        sw.Stop();
        bool ok = gmUser && liUser && idempotent;
        string status = ok ? "ok" : "fail";
        string detail =
            $"老 GainMap 用户(旧1)：格式→{a.FormatIndex} 增益图={a.GainMapEnabled} " +
            $"质量 {d.QualityJpegLi}→{a.QualityJpegLi} 色度 {a.ChromaJpegLi} | " +
            $"老 JPEG LI 用户(旧2)：格式→{b.FormatIndex} 增益图={b.GainMapEnabled} 质量仍 {b.QualityJpegLi} | " +
            $"已盖章文件：再跑={again} 格式仍 {c.FormatIndex}" +
            (ok ? " ⇒ 裸索引不会静默换格式，且迁移只跑一次"
                : " ⇒ 索引/参数搬错，用户会静默换格式或丢自己挑的档位");

        Progress(outDir, $"GAINMAP_MIGRATION {status} {sw.ElapsedMilliseconds}ms {detail}");
        Console.WriteLine($"[selftest] GAINMAP_MIGRATION {status} {detail}");
        return (Note, status, detail, sw.ElapsedMilliseconds,
            $",\n      \"gmUser\": {Bool(gmUser)}, \"liUser\": {Bool(liUser)}, \"idempotent\": {Bool(idempotent)}, " +
            $"\"newFormatIndex\": {a.FormatIndex}");
    }

    /// <summary>float 帧精度统计。</summary>
    /// <remarks>
    /// ⚠ 判据 (b) 的可信度（不假装它成立）：整格占比只在"帧里的值是 8-bit 编码值、或其
    /// **单调逐通道**变换"时有效。仓库口径认为 WGC Float16 帧是**线性 scRGB**（1.0 = 80 nits，
    /// 见 ToneMapper.cs 头注释 —— 这条口径至今【没有实测】），而线性化本身就把 n/255 打散到
    /// 整格之外 ⇒ 所以同时统计"用同源的正向 gamma 表（ToneMapper.LinearToSrgbLut，与生产
    /// 8-bit 量化同一张表，不另写一份）反解回编码域后的整格占比"，取两者较大值为 (b)。
    /// 洗不掉的干扰还剩两条，都已按"不可信 ⇒ SKIP"处理：
    /// · **跨通道色域矩阵**（709→2020 等）混合 R/G/B ⇒ 8-bit 源也会显得不落在整格上（假 PASS）。
    ///   这正是判据 (c) 存在的理由：矩阵行和为 1 ⇒ 中性像素 (c,c,c) 映回 (c,c,c)，
    ///   而 gamma / 白点缩放 / 矩阵都改变不了一共有多少个**不同级数** ⇒ 中性像素去重量级
    ///   ≤ 256 就是"精度止步于 8-bit"的硬证据。PASS 必须 (b) 与 (c) 同时成立。
    /// · **未知白点缩放**：若 SDR 白在帧里不是 1.0，[0,1] 内样本会几乎清零 ⇒ 分母不成立，
    ///   调用处按 SKIP 处理（见 MinInRangeSamples）。
    /// 残留假阳性：DWM 的浮点合成（半透明层混合、文本抗锯齿、面板 dither）会把 8-bit 应用内容
    /// 合成本来不止 256 级的流 —— 这时"交付给下游的流精度 >8-bit"依然为真，但"面板真给了
    /// 10-bit 内容"未被证明。本探针测的是前者。
    /// </remarks>
    /// <summary>本帧的 SDR 白点落在 scRGB 的哪个位置：纸白 nits / 80（1.0 = 80 nits 是 scRGB 的定义）。</summary>
    /// <remarks>读不到纸白时返回 1.0 ⇒ 判据退化回"以 1.0 为白点"的老写法，不引入新失败。
    /// 地板 max(nits, 80) 与 <c>JpegGainMapEncoder.ComputeWhitePoint</c> 同一条（那边是产品亮度基准，
    /// 这里只是把同一把尺借给探针，别再长第三份）。本机实测 120 nits ⇒ 1.5。</remarks>
    private static float PaperWhiteScrgb()
    {
        int nits = TrueToneCap.Core.Capture.DisplayEnumerator.GetSdrWhiteLevel();
        return nits > 0 ? Math.Max(nits, 80) / 80f : 1f;
    }

    private static HdrFloatStats AnalyzeHdrFloat(float[] px, int w, int h, float paperWhite = 1f)
    {
        // ═══ 2026-09-28 批次 D：这个 LSB 与下面的 SDR 窗口都必须随【帧自己的纸白】走 ═══
        // "8-bit 假设"的正确表述是：帧 = (8-bit 台阶) × 该屏的 SDR 白点。本机纸白 120 nits ⇒ 白点在 1.5，
        // 于是 DWM 合成出来的 SDR 内容台阶间距是 1.5/255 而不是 1/255。写死 1/255 的后果不是判不出红，
        // 而是【把假高精度读成绿】：一份 8-bit 内容被抬到 1.5 白点后，在 1/255 的尺子上只有约一半落在整格上
        // ⇒ "明显偏离整格"，同时 >1.0 占比落进头部余量区间 ⇒ 两条证据同时"支持"它。
        // paperWhite=1 时本函数与改前逐位相同（LSB、窗口、越界阈值全退化回原式），所以老读数不受影响。
        float pw = paperWhite > 0f ? paperWhite : 1f;
        float Lsb = pw / 255f;
        // ═══ 2026-09-19 判据自检抓到的缺陷：容差必须【小于 0.5 LSB8】═══
        // q = v/Lsb 后测的是 |q − round(q)|，而这个量【恒 ≤ 0.5】（round 的定义）。旧值 0.6f
        // 让"落在 1/256 整格上"成为恒真式 ⇒ 判据 (b) 永为 100% ⇒ 依赖它的两条分支全废
        // （FAIL 分支失去分辨力、:424 的 PASS 分支永不可达）。真机读数"HDR float 帧整格 100.00%"
        // 就是这条恒真式的现场显形。
        // 0.25 = FP16 噪声上界的两倍：FP16 在 1.0 附近相对精度 2^-11 ⇒ 绝对噪声 ≤0.124 LSB8，
        // 真在整格上的值仍算"在"，被矩阵/合成洗开的值才判"不在"。
        const float GridTolLsb = 0.25f;
        float NeutralTol = 0.75f * Lsb;  // R≈G≈B 的判定窗口（跟着纸白缩放：8-bit 的量化噪声也按同一比例放大）
        const int LevelCap = 8192;             // 级数去重上限（到顶记为"≥8192"）

        int total = Math.Min(w * h, px.Length / 4);
        int stride = Math.Max(1, total / 200_000);   // 最多约 20 万像素，4K 屏也不会失控

        int pixels = 0, gridN = 0, chan = 0, snapRaw = 0, snapEnc = 0, neutral = 0, above1 = 0, aboveWhite = 0, below0 = 0;
        float min = float.MaxValue, max = float.MinValue;
        var levels = new HashSet<int>();
        var inRangeLevels = new HashSet<int>();
        bool saturated = false, contentSaturated = false;

        for (int p = 0; p < total; p += stride)
        {
            int i = p * 4;                       // 帧内序为 R,G,B,A（与 ToneMapper.FloatToSRgbBytes 同源）
            float r = px[i], g = px[i + 1], b = px[i + 2];
            pixels++;

            bool neutralish = MathF.Abs(r - g) <= NeutralTol && MathF.Abs(g - b) <= NeutralTol && MathF.Abs(r - b) <= NeutralTol;
            if (neutralish && float.IsFinite(g))
            {
                // ═══ 2026-09-19 修复：这个计数原先从没 ++ 过 ═══
                // 结果 NeutralN 恒为 0 ⇒ 判定第一关 `NeutralN < MinNeutralPixels` 恒成立
                // ⇒ 探针在任何机器上都只能输出 SKIP，PASS/FAIL 两条分支都是死代码。
                // 现场证据：真机报告同时打印出"中性像素 0 个 → 去重量级 942"——
                // 级数是同一个 if 块里统计的，能攒到 942 就证明有几十万个像素被判为中性。
                neutral++;
                if (levels.Count < LevelCap) levels.Add(BitConverter.SingleToInt32Bits(g));
                else saturated = true;
            }

            for (int c = 0; c < 3; c++)
            {
                float v = c == 0 ? r : c == 1 ? g : b;
                if (!float.IsFinite(v)) continue;
                chan++;
                if (v < min) min = v;
                if (v > max) max = v;
                // above1 的容差【钉死成旧写法的 1/255】：它现在只是对照读数，
                // 若跟着 pw 缩放，就不再是旧锚点、而是新尺子量出来的旧阈值了。
                if (v > 1f + 1f / 255f) above1++;
                if (v > pw + Lsb) aboveWhite++;
                else if (v < -Lsb) below0++;
                if (v < 0f || v > pw) continue;   // 整格占比只在 SDR 范围内统计（越界本身就是扩展范围的证据）

                gridN++;
                // 画面自身的丰富度：整格占比要能否证"精度止步于 8-bit"，前提是这一帧里
                // 确实有足够的不同级数可看（§3 斜坡判据同一条纪律：64 级以下不判）。
                if (inRangeLevels.Count < LevelCap) inRangeLevels.Add(BitConverter.SingleToInt32Bits(v));
                else contentSaturated = true;
                float q = v / Lsb;
                if (MathF.Abs(q - MathF.Round(q)) <= GridTolLsb) snapRaw++;
                // 反解到编码域：与生产 8-bit 量化同一张表，避免"自己写第二份实现自证"。
                // ⚠ 必须先除以纸白再查表：LUT 的定义域是"编码值 0..1"，纸白 1.5 的帧里 v 可以到 1.5，
                //   直接喂进去等于查表越界（今天靠上面那行 `v > 1f` 的窗口挡住了，窗口一放宽就必须自己钉住）。
                float e = TrueToneCap.Core.Processing.ToneMapper.LinearToSrgbLut(v / pw) * 255f;
                if (MathF.Abs(e - MathF.Round(e)) <= GridTolLsb) snapEnc++;
            }
        }

        return new HdrFloatStats(pixels, gridN, chan,
            gridN == 0 ? 100.0 : 100.0 * snapRaw / gridN,
            gridN == 0 ? 100.0 : 100.0 * snapEnc / gridN,
            neutral, levels.Count, saturated,
            inRangeLevels.Count, contentSaturated,
            chan == 0 ? 0f : min, chan == 0 ? 0f : max, above1, aboveWhite, below0, pw);
    }

    /// <summary>高精度源探针的实测数字（占比一律 0–100）。</summary>
    private sealed record HdrFloatStats(
        int Pixels, int GridN, int ChanValues,
        double GridRawPct, double GridEncPct,
        int NeutralN, int NeutralLevels, bool LevelsSaturated,
        int ContentLevels, bool ContentSaturated,
        float Min, float Max, int Above1, int AboveWhite, int Below0, float PaperWhite)
    {
        /// <summary>整格占比取两域中较大者：任一域能证明"落在整格上"，就认为 8-bit 假设没被推翻。</summary>
        public double GridPct => Math.Max(GridRawPct, GridEncPct);
        public string GridDomain => GridEncPct >= GridRawPct ? "sRGB反解域" : "raw域";
        public double Above1Pct => ChanValues == 0 ? 0 : 100.0 * Above1 / ChanValues;
        /// <summary>越出【本帧纸白】的占比 —— 判据用它，不用 Above1Pct（纸白不在 1.0 的屏上后者量的是桌面亮度）。</summary>
        public double AboveWhitePct => ChanValues == 0 ? 0 : 100.0 * AboveWhite / ChanValues;
        public double Below0Pct => ChanValues == 0 ? 0 : 100.0 * Below0 / ChanValues;
        public string LevelsText => LevelsSaturated ? $"≥{NeutralLevels}" : NeutralLevels.ToString(CultureInfo.InvariantCulture);
        public string ContentText => ContentSaturated ? $"≥{ContentLevels}" : ContentLevels.ToString(CultureInfo.InvariantCulture);
        /// <summary>全部实测数字，一条打印出来 —— 结论只由 JudgeHdrFloat 从这些数字推出，不含别的输入。</summary>
        public string NumbersText =>
            $"抽样 {Pixels} px（{ChanValues} 个通道值）| float min={F(Min)} max={F(Max)} " +
            $"越出 SDR 范围: >1.0 占 {F(Above1Pct)}%  >纸白({F(PaperWhite)}) 占 {F(AboveWhitePct)}%  <0 占 {F(Below0Pct)}% | " +
            $"落在本帧 8-bit 整格(1/(255×纸白))上的比例: raw 域 {F(GridRawPct)}%、线性→sRGB 反解后 {F(GridEncPct)}% " +
            $"→ 采用{GridDomain} {F(GridPct)}%（[0,1] 内 {GridN} 个通道值、{ContentText} 种不同值参与）| " +
            $"中性像素 {NeutralN} 个 → 去重量级 {LevelsText}";
    }

    private static string Yes(bool v) => v ? "yes" : "no";
    private static string Bool(bool v) => v ? "true" : "false";
    /// <summary>不变文化数字格式化（JSON 里必须是点号小数点，本机界面语言是中文也不例外）。</summary>
    private static string F(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
    private static string F(float v) => v.ToString("F4", CultureInfo.InvariantCulture);

    /// <summary>生成带渐变与色块的测试图（确保不是纯色，能真实走到编码器内部）。</summary>
    private static byte[] MakeGradient(int w, int h)
    {
        var bgra = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bgra[i + 0] = (byte)(x * 255 / Math.Max(1, w - 1));      // B
                bgra[i + 1] = (byte)(y * 255 / Math.Max(1, h - 1));      // G
                bgra[i + 2] = (byte)((x + y) * 255 / Math.Max(1, w + h - 2)); // R
                bgra[i + 3] = 255;                                       // A
            }
        }
        return bgra;
    }

    private static string Escape(string s) =>
        s.Replace("\\", "\\\\", StringComparison.Ordinal)
         .Replace("\"", "\\\"", StringComparison.Ordinal)
         .Replace("\r", " ", StringComparison.Ordinal)
         .Replace("\n", " ", StringComparison.Ordinal);
}
