// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Test/StringLiteralScanner.cs
// C# 源码字符串字面量扫描器（手写状态机）—— 给"HDR 抓帧窗烘焙中文"盲区计数当尺子
// 为什么不许用正则：上一轮实测正则会把 $"…{expr}…" 从插值洞处切成 `)} 全屏={(_mpoFullsc` 这类碎片，
// 数出 98 而手点是 41 —— 计数单位一旦没有意义，钉住的基线就是一把会随机抖动的尺子。

namespace TrueToneCap.Test;

using System.Collections.Generic;
using System.Text;

/// <summary>C# 字符串字面量扫描器：按字面量的【完整形态】切源码，注释与字符字面量内的引号不算开头。</summary>
public static class StringLiteralScanner
{
    /// <summary>字面量形态（前缀决定转义规则与是否存在插值洞）。</summary>
    public enum Kind { Regular, Verbatim, Interpolated, InterpolatedVerbatim }

    /// <summary>一条字面量。<see cref="Text"/> 是源码原文（含前缀与引号），但插值洞整段剔除、
    /// 只留 <c>{…}</c> 占位 —— 否则洞里的表达式源码会把这条的"文案"淹掉，CJK 判定也会算错归属。
    /// <see cref="Start"/> 指向这条的<b>前缀</b>起始处（<c>@"</c> 算在 @ 上、<c>$@"</c> 算在 $ 上），
    /// 与 <see cref="Result.UnterminatedAt"/> 用同一个定义，别两处各指一处。</summary>
    public sealed class Literal
    {
        public int Start;
        public int Line;
        public Kind Kind;
        public string Text = "";
        public override string ToString() => $"第{Line}行 {Text}";
    }

    /// <summary>扫描结果。
    /// <para>未闭合引号的处置是【显式选定】的：不抛异常（一把尺子不该因为读到一个坏文件就把自己炸掉），
    /// 而是「不产出这条残破字面量 + 把位置记在 <see cref="UnterminatedAt"/>」。
    /// 调用方必须判红：一旦带着这个标记继续数，数出来的就不再是字面量。</para></summary>
    public sealed class Result
    {
        public List<Literal> Literals { get; } = new();
        public int UnterminatedAt = -1;      // -1 = 全程闭合
        public string UnterminatedNote = "";
    }

    /// <summary>扫描整份源码文本。
    /// <para>覆盖面：普通串（<c>\"</c> <c>\\</c>）、逐字串 <c>@""</c>（<c>""</c> 为转义引号）、
    /// 插值串 <c>$""</c>（含嵌套花括号与洞内串）、<c>$@""</c> / <c>@$""</c>、
    /// 行注释 / 块注释（可嵌套）/ 文档注释、字符字面量（含 <c>'"'</c>、<c>'\''</c>）。</para>
    /// <para>不覆盖原始字符串字面量 <c>"""…"""</c>（当前目标文件里一处都没有），但两种坏法都会响：
    /// 跨行的那种在开头那条就报 <see cref="Result.UnterminatedAt"/> ≥ 0（调用方判红）；
    /// 单行的那种退化成 3 条（两条空串 + 中间一条）⇒ 条数撞基线。详见 <c>RawStringFailsLoud</c> 两例。</para></summary>
    public static Result Scan(string source) => new Cursor(source).Run();

    /// <summary>是否含 U+4E00–U+9FFF（CJK 统一表意文字基本区）—— 盲区计数只认这一档。</summary>
    public static bool HasCjk(string text)
    {
        foreach (char c in text)
            if (c >= '\u4e00' && c <= '\u9fff') return true;
        return false;
    }

    /// <summary>挑出含中文的字面量（扫描顺序）。</summary>
    public static List<Literal> CjkLiterals(Result r)
    {
        var list = new List<Literal>();
        foreach (var l in r.Literals)
            if (HasCjk(l.Text)) list.Add(l);
        return list;
    }

    /// <summary>单遍游标。每个分支都必须推进 <c>_i</c>（否则会原地打转），未识别字符按一个代码点步进。</summary>
    sealed class Cursor
    {
        readonly string _s;
        int _i;
        int _line = 1;
        readonly Result _r = new();

        public Cursor(string s) { _s = s; }

        int N => _s.Length;
        char Peek(int off) => _i + off < N ? _s[_i + off] : '\0';

        public Result Run()
        {
            while (_i < N)
            {
                char c = _s[_i];
                if (c == '/' && Peek(1) == '/') { SkipLineComment(); continue; }
                if (c == '/' && Peek(1) == '*') { SkipBlockComment(); continue; }
                if (TryStartLiteral()) continue;
                if (c == '\'') { SkipCharLiteral(); continue; }
                if (c == '\n') { _line++; }
                _i++;
            }
            return _r;
        }

        /// <summary>当前位置是否是一条字符串字面量的开头（含 @ / $ 前缀的各种写法）。</summary>
        bool TryStartLiteral()
        {
            char c = _s[_i];
            if (c == '"') { ScanString(Kind.Regular, "", false, false); return true; }
            if (c == '@')
            {
                if (Peek(1) == '"') { ScanString(Kind.Verbatim, "@", true, false); return true; }
                // @$"" 前缀反写的插值逐字串
                if (Peek(1) == '$' && Peek(2) == '"') { ScanString(Kind.InterpolatedVerbatim, "@$", true, true); return true; }
                return false;                       // @class —— 逐字标识符，不是串
            }
            if (c == '$')
            {
                if (Peek(1) == '"') { ScanString(Kind.Interpolated, "$", false, true); return true; }
                if (Peek(1) == '@' && Peek(2) == '"') { ScanString(Kind.InterpolatedVerbatim, "$@", true, true); return true; }
                return false;                       // $变量名之类
            }
            return false;
        }

        void ScanString(Kind kind, string prefix, bool verbatim, bool interp)
        {
            int start = _i, startLine = _line;
            var sb = new StringBuilder(prefix);
            _i += prefix.Length;
            sb.Append('"'); _i++;                   // 起始引号

            bool closed = false;
            while (_i < N)
            {
                char c = _s[_i];

                // 非逐字串：反斜杠连下一个字符整体吃掉（\" \\ \n 续行都走这里，故 \' 不会开启字符字面量）
                if (!verbatim && c == '\\')
                {
                    sb.Append(c);
                    if (_i + 1 < N) { if (_s[_i + 1] == '\n') _line++; sb.Append(_s[_i + 1]); }
                    _i += 2;
                    continue;
                }

                if (c == '"')
                {
                    if (verbatim && Peek(1) == '"') { sb.Append("\"\""); _i += 2; continue; }
                    sb.Append('"'); _i++; closed = true; break;
                }

                if (c == '\n')
                {
                    // 非逐字串不许跨行 ⇒ 判为未闭合；停在换行处，一个漏引号不许吃掉整份文件
                    if (!verbatim) break;
                    _line++; sb.Append(c); _i++; continue;
                }

                if (interp && c == '{') { HandleBrace(sb); continue; }
                if (interp && c == '}' && Peek(1) == '}') { sb.Append("}}"); _i += 2; continue; }

                sb.Append(c); _i++;
            }

            if (!closed) { MarkUnterminated(start, startLine, verbatim ? "逐字" : "普通"); return; }
            _r.Literals.Add(new Literal { Start = start, Line = startLine, Kind = kind, Text = sb.ToString() });
        }

        /// <summary>串里的 <c>{</c>：要么是转义 <c>{{</c>，要么是一个插值洞。
        /// 洞内的源码【不进】本条的 Text，但洞内表达式照样要被扫一遍 —— 里面的串是独立的一条字面量，
        /// 计一次；洞里的 <c>{</c> <c>}</c>（数组初始化器、lambda、嵌套插值）用深度计数配对。</summary>
        void HandleBrace(StringBuilder sb)
        {
            if (Peek(1) == '{') { sb.Append("{{"); _i += 2; return; }

            sb.Append("{…}");
            _i++;                                   // 进洞
            int depth = 1;
            while (_i < N && depth > 0)
            {
                char c = _s[_i];
                if (c == '/' && Peek(1) == '/') { SkipLineComment(); continue; }
                if (c == '/' && Peek(1) == '*') { SkipBlockComment(); continue; }
                if (TryStartLiteral()) continue;
                if (c == '\'') { SkipCharLiteral(); continue; }
                if (c == '\n') { _line++; _i++; continue; }
                if (c == '{') { depth++; _i++; continue; }
                if (c == '}') { depth--; _i++; continue; }
                _i++;
            }
        }

        /// <summary>字符字面量：扫过但【不】计入字符串结果。里面的 <c>"</c> 不许被当成串开头。</summary>
        void SkipCharLiteral()
        {
            int start = _i;
            _i++;
            while (_i < N)
            {
                char c = _s[_i];
                if (c == '\\') { if (Peek(1) == '\n') _line++; _i += 2; continue; }
                if (c == '\'') { _i++; return; }
                if (c == '\n') break;               // 没收尾 ⇒ 这是个孤撇号（多半来自散文/被改坏的源码）
                _i++;
            }
            // 没收尾 ⇒ 这是个孤撇号（散文里漏进代码、或被注释/串的处理顺序错位）：
            // 只跳过它自己那位字符再往下扫，绝不许把后面的代码当串吞掉。
            _i = start + 1;
        }

        void SkipLineComment()
        {
            _i += 2;
            while (_i < N && _s[_i] != '\n') _i++;   // 换行留给上层计数
        }

        void SkipBlockComment()
        {
            _i += 2;
            while (_i < N)
            {
                char c = _s[_i];
                if (c == '/' && Peek(1) == '*') { _i += 2; continue; }   // C# 块注释可嵌套
                if (c == '*' && Peek(1) == '/') { _i += 2; return; }
                if (c == '\n') _line++;
                _i++;
            }
            // 未闭合的块注释天然吃到文件尾：不是字面量问题，不记 UnterminatedAt
        }

        void MarkUnterminated(int start, int startLine, string label)
        {
            if (_r.UnterminatedAt >= 0) return;      // 记第一处就够：调用方只看"有没有"
            _r.UnterminatedAt = start;
            _r.UnterminatedNote = $"第 {startLine} 行的 {label}字面量未闭合" +
                (_i >= N ? "（一直到文件尾都没找到收尾引号）" : "（该行内没有收尾引号）");
        }
    }
}
