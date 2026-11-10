// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Models/TranslationProviderType.cs
// 翻译源类型枚举

namespace TrueToneCap.Core.Models;

/// <summary>
/// 翻译服务提供者的类型。
/// 用于区分不同的翻译来源（在线免费、在线官方、离线本地）。
/// </summary>
public enum TranslationProviderType
{
    /// <summary>无翻译源。</summary>
    None = 0,
    
    /// <summary>免费公共在线端点 (Edge/Bing/MyMemory/Google)。</summary>
    OnlineFree = 1,
    
    /// <summary>官方 API (有道开放平台、自定义 LLM)。</summary>
    OnlineOfficial = 2,
    
    /// <summary>本地离线翻译 (Argos Translate)。</summary>
    OfflineArgos = 3
}
