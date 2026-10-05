# 2026-09-29 修复轮的变异实测清单（判据没有"红过"就不算有牙）
# 规矩：每把锁三段读数齐全才算数 —— 接上时绿 → 把产品改回旧写法当场红且点名 → 还原后绿。
# 每步都要在改动之后重跑构建（`[1.9/2]` 会因宿主比源码旧而作废整轮，这是第三次撞上它）。
# 顺序按"改一处只影响一把锁"排，避免一次变异让多条红、归属不清。

| # | 要破的产品写法 | 破坏方式（把代码改回缺陷态） | 期望红的判据（断言名） | 期望它【不】红 |
|---|---|---|---|---|
| M1 | BT.601 权重回到装反 | BitmapPreprocessor.cs 四处 `* 29 / * 77` 换回 `* 77 / * 29` | 「灰度权重按 BT.601 落在正确通道上：暖色不算深底」+ 冷色那条（两臂应同时反） | 其余 OcrServiceTests 的 7 条 |
| M2 | 空 content 当译文 | TranslationService.cs 把 `RequireTranslatedContent(content)` 换回 `content?.Trim() ?? text` | 「端点回空 content 必须判失败」 | 「有内容时原样去空白返回」 |
| M3 | 在途被读成崩溃残留 | EncoderCrashSentinel.IsBlocked 去掉 `InFlightByThisProcess` 那道判断 | 「本进程在途的 Arm 不算崩溃残留」+「别的进程留下的残留仍判崩溃」（后者必须仍绿） | 「最后一个返回者才清除哨兵」 |
| M4 | 消费后文件没删（reader 还开着） | IsBlocked 恢复成 `using var reader` 直落到 File.Delete | 「残留被读后即消费」/「旧两行格式仍判为崩溃」（都断言 File.Exists=false） | 首行不匹配那两条（不该删的确实没删） |
| M5 | PID 归属被绕过 | `Arm` 写两行（去掉 PID 行） | 「旧两行格式…仍判为崩溃」仍绿 ⇒ 说明这条不是靠 PID 侥幸；再看 M3 变红 | —— |
| M6 | PNG 保底不带声明 | FormatEncoders.cs 保底那行换回 5 参 `Encode(bgra, w, h, pngPath, 8)` 并删掉 notifier 赋值 | 「PNG 保底那一路真的把声明与留痕交出去了」 | 「两个入口同输入必须同输出」（它判的是映射同源） |
| M7 | 迁移重写排回 Normalize 之前 | SettingsService.Load 把 `if (migrated) Save()` 挪回 :123 之前 | 「迁移重写排在 Normalize 之后」 | 「迁移结果必须驱动写盘」（旧锁只看存在 ⇒ 证明它为什么不够） |
| M8 | 凭据无条件删除 | SaveSecret 的 `else if (allowDelete)` 改回 `else` | 「清除凭据必须挂在「本次读取可信」上」 | —— |
| M9 | 识字/翻译落回原始像素 | SelectionOverlay 守卫删掉 `or ActionResult.Ocr or ActionResult.Translate`；HDR 路 case Ocr 改回喂 sdrRegion | 「SDR 覆盖层：合成守卫必须同时含四个目的地」+「HDR 覆盖层：case Ocr 必须走 ComposeAnnotatedSdr」 | 「先退出标注再合成」等既有行为 |
| M10 | 无感截图失败静默 | 三处新增的 SetStatus/ShowCaptureFailed 逐个删 | 「【锚点】必须同时给用户状态栏与失败提示」按点名逐条红 | 「【锚点】仍在源码里」（锚没动 ⇒ 绿，证明两条分工不同） |
| M11 | 行格式又分叉 | LogParser.cs 两条正则去掉 `(?:#\S+\s+)?` | `[1.96/2]` 那一步 RC=1 并点名被试臂 | 四条臂里的两条负样本（仍应被拒） |
| M12 | 断言数量悄悄变少 | 临时注释掉 CorePipelineTests 里任意 30 条 `Assert(` | 汇总里「--unit-tests 断言数从基线 N 掉到 M」并 GATE_EXIT=1 | 其它套件的比对 |
| M13 | 钩子接线自检本身 | 把 `$hooksEff` 换回第一版写法（`-or` 折 bool） | `[1.95/2]` 必须打 ⚠️ 而不是 ✅（本轮它先打过一次假 ✅） | —— |

# 已知不必再验的：M4/M13 的"旧写法"已被本轮实跑复现过一次（M4=5 条失败里的 2 条；M13=独立 PS 探针
# 量出 bool-vs-str : True）。写进报告时按"当场复现"引用，不要写成推论。
