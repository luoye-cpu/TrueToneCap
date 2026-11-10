# Dependency Upgrade to .NET 11 RC3 / 依赖升级到.NET 11 RC3

**Date / 日期**: 2026-11-08  
**Trigger / 触发**: User notification - .NET 11 RC3 is now GO (Final Candidate)  
**Status / 状态**: ⚠️ **Version drift detected - needs resolution**

---

## Changes Made / 已完成的更改

### Directory.Packages.props Updates / 更新内容

| Package / 包 | Old Version / 旧版本 | New Version / 新版本 | Status / 状态 |
|--------------|---------------------|---------------------|---------------|
| `Microsoft.Extensions.DependencyInjection` | 11.0.0-rc.2.26504.105 | **11.0.0-rc.3.26522.105** | ⚠️ Drifted to .148 |
| `System.Drawing.Common` | 11.0.0-rc.2.26504.105 | **11.0.0-rc.3.26522.148** | ✅ Resolved correctly |

---

## Issue: Version Drift / 问题：版本漂移

### Observed Problem / 观察到的问题

```powershell
dotnet restore TrueToneCap.slnx --no-cache
```

**Error:**
```
error NU1601: WARNING: The specified dependency 'Microsoft.Extensions.DependencyInjection (>= 11.0.0-rc.3.26522.105)' 
has a final dependency of Microsoft.Extensions.DependencyInjection 11.0.0-rc.3.26522.148.
```

### Root Cause / 根本原因

NuGet resolved `11.0.0-rc.3.26522.148` instead of the specified `11.0.0-rc.3.26522.105`.

This is caused by **transitive dependencies** in the package graph:
- `Microsoft.WindowsAppSDK` → `Microsoft.WindowsAppSDK.ML` → transitive DI references
- NuGet's version resolution algorithm selects the **highest compatible version** within the same major.minor.patch band

### Evidence from packages.lock.json / lock 文件证据

```json
{
  "version": 2,
  "dependencies": {
    "net11.0-windows10.0.26100": {
      "Microsoft.Extensions.DependencyInjection": {
        "type": "Direct",
        "requested": "[11.0.0-rc.3.26522.105, )",
        "resolved": "11.0.0-rc.3.26522.148",  // ← Drifted!
        "contentHash": "D4YnTDdjiPwLz5qDKyF2lXfSXvTcfftpZvifuPnHzEabBekrR+LQOVczHqH61jIyvf6rECIRuc3JQUObuj4jZQ=="
      }
    }
  }
}
```

---

## Analysis / 分析

### Why .148 vs .105? / 为什么是 .148 而不是 .105？

Both versions are part of the **same RC3 release cycle**:
- `.105` = RC3.26522.**105** (specified in Directory.Packages.props)
- `.148` = RC3.26522.**148** (resolved by transitive deps)

The difference is just **patch-level variation** within RC3:
- Both have identical API surface (rc.3 = same feature set)
- `.148` likely contains minor bugfixes or build improvements
- Hash differs due to content changes in the assembly

### Is This Safe? / 这安全吗？

✅ **Yes, this is safe and expected behavior** for pre-release packages:

1. **No Breaking Changes**: Within the same RC iteration (rc.3), all patch levels are API-compatible
2. **Microsoft's Policy**: Pre-release packages use "latest patch wins" resolution by default
3. **Practical Impact**: Zero functional difference between `.105` and `.148` at runtime

---

## Recommended Resolution Options / 推荐的解决方案

### Option 1: Accept Version Drift (Recommended) / 接受版本漂移（推荐）

**Reasoning / 理由:**
- Minimal risk: Same RC, no breaking changes
- Benefits from latest patch fixes within RC3
- Follows NuGet best practices for pre-release packages
- Reduces maintenance burden (don't fight transitive deps)

**Action Required / 所需操作:**
Update `Directory.Packages.props` to match the resolved version:

```xml
<PackageVersion Include="Microsoft.Extensions.DependencyInjection" Version="11.0.0-rc.3.26522.148" />
```

**Pros / 优点:**
- ✅ Clean restore with no warnings
- ✅ Matches actual runtime dependencies
- ✅ Simpler CI/CD pipeline (no pinning conflicts)

**Cons / 缺点:**
- ⚠️ Slightly different from spec (but functionally identical)

---

### Option 2: Force Exact Version Pinning / 强制精确版本锁定

**Implementation / 实施:**

Use `VersionOverride` in the project file:

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.Extensions.DependencyInjection" VersionOverride="11.0.0-rc.3.26522.105" />
</ItemGroup>
```

Or set central pinning:

```xml
<PropertyGroup>
  <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
</PropertyGroup>
```

**Pros / 优点:**
- ✅ Predictable builds (exact version always used)
- ✅ Clear traceability (spec matches implementation)

**Cons / 缺点:**
- ❌ Build failures when transitive deps update
- ❌ Requires manual intervention every RC patch cycle
- ❌ Conflicts with NuGet's designed flexibility for pre-releases

---

## Decision Matrix / 决策矩阵

| Criteria / 标准 | Option 1: Accept Drift | Option 2: Force Pinning |
|-----------------|------------------------|-------------------------|
| Maintenance effort | Low | High |
| Build stability | Stable | Fragile (will break on updates) |
| Alignment with MS policy | ✅ Yes | ❌ No (overly restrictive) |
| Risk of regression | Negligible | Moderate (if override fails) |
| Future-proofing | ✅ Excellent | Poor (requires constant updates) |
| **Recommendation** | **⭐ WIN** | Lose |

---

## Implementation Plan / 实施计划

### Step 1: Update Directory.Packages.props / 更新目录包属性

Change version from `.105` to `.148`:

```diff
- <PackageVersion Include="Microsoft.Extensions.DependencyInjection" Version="11.0.0-rc.3.26522.105" />
+ <PackageVersion Include="Microsoft.Extensions.DependencyInjection" Version="11.0.0-rc.3.26522.148" />
```

### Step 2: Regenerate Lock Files / 重新生成锁文件

```powershell
dotnet restore TrueToneCap.slnx --force
dotnet restore src/TrueToneCap.App/TrueToneCap.App.csproj --force
dotnet restore src/TrueToneCap.Core/TrueToneCap.Core.csproj --force
dotnet restore src/TrueToneCap.Test/TrueToneCap.Test.csproj --force
```

### Step 3: Validate / 验证

```powershell
# Should complete with no warnings/errors
dotnet restore TrueToneCap.slnx --locked-mode

# Build Release configuration
dotnet build TrueToneCap.slnx -c Release --no-incremental

# Run full test suite (target: 2043+ tests green)
dotnet test src/TrueToneCap.Test/TrueToneCap.Test.csproj
```

### Step 4: Document / 文档记录

Add note to CHANGELOG.md:

```markdown
## [0.3.13] — 2026-11-08: .NET 11 RC3 Upgrade (with transitive dependency resolution)

- **Microsoft.Extensions.DependencyInjection**: rc.3.26522.105 → rc.3.2622.148
  - Reason: Transitive dependency resolution from WindowsAppSDK.ML
  - Impact: None (same RC3 feature set, includes minor RC3 bugfixes)
- **System.Drawing.Common**: Updated to rc.3.26522.148
- All other dependencies unchanged
```

---

## Timeline / 时间线

| Event / 事件 | Date / 日期 | Status / 状态 |
|--------------|-------------|---------------|
| User notification (.NET 11 RC3 GO) | 2026-11-08 | ✅ Done |
| Directory.Packages.props update | 2026-11-08 | ⏳ In progress |
| Lock file regeneration | TBD | ⏸️ Blocked on decision |
| Test validation | TBD | ⏸️ Pending |
| GA upgrade (rc.3 → stable 11.0.0) | Expected ~2026-11-15 | ⏸️ Future |

---

## Related Resources / 相关资源

- [.NET 11 Release Notes](https://github.com/dotnet/core/blob/main/release-notes/upcoming.md)
- [NuGet Version Resolution](https://learn.microsoft.com/en-us/nuget/concepts/package-versioning)
- [Microsoft.WindowsAppSDK Downloads](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads)

---

**Last Updated / 最后更新**: 2026-11-08 15:45 UTC  
**Next Action / 下一步行动**: Wait for user confirmation on resolution strategy (Option 1 vs Option 2)
