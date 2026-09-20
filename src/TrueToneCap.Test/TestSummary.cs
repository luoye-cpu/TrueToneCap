// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Test/TestSummary.cs
// 各套件「结果汇总」的统一输出入口。
//
// 为什么需要它: 2026-09-14 之前，8 个套件各自手打总结行，演化出三种互不兼容的写法：
//     `结果: N 通过, 0 失败`
//     `完成: 93 通过, 0 失败, 3760ms`
//     `结果: ✅ 153 通过  ❌ 0 失败  ⚠ 0 警告`
// 后果是任何用正则统计测试结果的脚本都会漏掉套件 —— CI 上表现为「少算通过数」，
// 而且这种缺失不会报错，只会让结果看起来"少一点"。
//
// 约定:
//   · 人类可读行：固定以 "  结果: " 开头，字段顺序与分隔符固定
//   · 机器可读行：固定以 "RESULT " 开头，key=value 形式
//   两行都由此方法产生，新增套件请直接调用它，不要再手写。
//
// 关于「跳过」(skipped):
//   环境不满足时必须【显式跳过】，不能伪装成通过、也不能当成失败：
//     · 伪装通过 = 恒真断言，门禁失去意义（本项目明令禁止）
//     · 当成失败 = 该套件永远无法进 headless 门禁，覆盖率上不去
//   跳过数单独计数并打印原因，既不影响退出码，也在结果里留痕。
//   ⚠️ skipped 追加在 RESULT 行【末尾】：既有解析器用 `passed=(\d+)\s+failed=(\d+)`
//      匹配前两个字段，新增字段放末尾才不会破坏它们。

namespace TrueToneCap.Test;

internal static class TestSummary
{
    /// <summary>输出统一的总结行（人类可读 + 机器可读各一行）。不改变控制流。</summary>
    /// <param name="suiteArg">套件参数名，如 "--unit-tests"</param>
    public static void Report(string suiteArg, int passed, int failed, int warnings = 0,
                              long? elapsedMs = null, int skipped = 0)
    {
        var line = $"  结果: {passed} 通过, {failed} 失败";
        if (warnings > 0) line += $", {warnings} 警告";
        if (skipped > 0) line += $", {skipped} 跳过";
        if (elapsedMs.HasValue) line += $" ({elapsedMs.Value}ms)";
        Console.WriteLine(line);

        // 机器可读：固定前缀 + key=value，便于 CI 直接 grep/解析
        // skipped 放末尾 —— 既有正则只取 passed/failed，位于前面的字段不能动。
        Console.WriteLine(
            $"RESULT suite={suiteArg} passed={passed} failed={failed} " +
            $"warnings={warnings} elapsedMs={(elapsedMs.HasValue ? elapsedMs.Value.ToString() : "-")} " +
            $"skipped={skipped}");
    }
}
