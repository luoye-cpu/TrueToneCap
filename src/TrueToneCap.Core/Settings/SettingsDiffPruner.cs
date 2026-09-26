// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Settings/SettingsDiffPruner.cs
// 设置持久化的"只写差集"算法 —— 放在 Core 而不是 App，是为了让它能被真正测到。
//
// 为什么需要这一步：AppSettingsData / SettingsService 都在 App 程序集里，而 Test 项目只引用
// Core（见 TrueToneCap.Test.csproj），App 侧逻辑此前只能靠"读源码文本"的哨兵判据覆盖。
// 哨兵能证明【调用点还在】，证明不了【语义对】：本算法最常见的失效形态是
// "两份 JSON 因为格式化差异被判成不相等 ⇒ 一个键都删不掉"，那是不报错、不失败的空转，
// 恰好是"改了默认值却传不到老用户手上"这个原始缺陷的另一种表现。

using System.Text.Json.Nodes;

namespace TrueToneCap.Core.Settings;

/// <summary>把"当前设置"里等于默认值的键剪掉，只留用户真正改过的。</summary>
public static class SettingsDiffPruner
{
    /// <summary>
    /// 返回剪枝后的 JSON 文本。<paramref name="alwaysKeep"/> 里的键无论是否等于默认都保留
    /// —— 版本号必须走这条：它若被剪掉，下次读文件会读成 0，迁移就会反复触发。
    /// <para>参数刻意用 <c>string[]</c> 而不是 <c>IReadOnlyCollection&lt;string&gt;</c>：
    /// App 侧调用点写集合表达式时，CsWinRT 分析器会以 CsWinRT1032 报错
    /// （非可变接口目标类型无法确定具体类型，破坏 trimming/AOT）。</para>
    /// </summary>
    public static string PruneToJson(JsonObject live, JsonObject defaults,
        string[]? alwaysKeep = null)
    {
        var drop = new List<string>();
        foreach (var kv in live)
        {
            if (alwaysKeep?.Contains(kv.Key) == true) continue;
            if (!defaults.TryGetPropertyValue(kv.Key, out var def)) continue;
            if (JsonNode.DeepEquals(kv.Value, def)) drop.Add(kv.Key);
        }
        foreach (var k in drop) live.Remove(k);
        return live.ToJsonString();
    }

    /// <summary>
    /// 默认表里根本没有的键要不要留？必须留 —— 它可能是别的（更老的）版本写进来的未知字段，
    /// 删掉等于丢用户数据。单独抽出来是为了让"保留"这条分支能被断言，而不是靠读代码默认它成立。
    /// </summary>
    public static bool ShouldKeepUnknownKey(string key, JsonObject defaults) =>
        !defaults.ContainsKey(key);
}
