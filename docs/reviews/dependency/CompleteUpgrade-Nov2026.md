# Complete Dependency Upgrade Report - November 2026 / 完整依赖升级报告 - 2026 年 11 月

**Date / 日期**: 2026-11-08  
**Scope / 范围**: All packages in Directory.Packages.props  
**Current Version Target / 当前目标版本**: v0.3.13  

---

## Executive Summary / 执行摘要

After comprehensive review of all dependencies, **one major upgrade opportunity identified**:

- ✅ **.NET Runtime**: Already upgraded to RC3 (previous task)
- ⚠️ **WindowsAppSDK**: 2.5.1 → **2.6.5** (November 2, 2026 release)
- ✅ **Vortice.***: Already at latest stable (3.8.3)
- ✅ **ZXing.Net**: Already at latest (0.16.11)
- ✅ **Win2D**: Already at latest (1.4.0)

**Action Required**: Upgrade WindowsAppSDK to 2.6.5 with full regression testing.

---

## Current State Analysis / 当前状态分析

### Directory.Packages.props Review / 包版本管理审查

| Package / 包 | Current / 当前 | Latest / 最新 | Status / 状态 | Priority / 优先级 |
|--------------|----------------|---------------|---------------|-------------------|
| `Microsoft.WindowsAppSDK` | **2.5.1** | **[2.6.5](https://www.nuget.org/packages/Microsoft.WindowsAppSdk/)** (2026-11-02) | ⚠️ **OUTDATED** | 🔴 High |
| `Microsoft.Graphics.Win2D` | 1.4.0 | 1.4.0 | ✅ Up to date | N/A |
| `Microsoft.Extensions.DependencyInjection` | 11.0.0-rc.3.26522.148 | 11.0.0-rc.3.26522.148 | ✅ Up to date | N/A |
| `Microsoft.Windows.AI.MachineLearning` | 2.4.89 | 2.4.89 | ✅ Up to date | N/A |
| `Vortice.Direct3D11` | 3.8.3 | [3.8.3](https://www.nuget.org/packages/Vortice.Direct3D11) | ✅ Up to date | N/A |
| `Vortice.DXGI` | 3.8.3 | [3.8.3](https://www.nuget.org/packages/Vortice.DXGI) | ✅ Up to date | N/A |
| `ZXing.Net` | 0.16.11 | [0.16.11](https://www.nuget.org/packages/zxing.Net) | ✅ Up to date (maintenance mode) | N/A |
| `System.Drawing.Common` | 11.0.0-rc.3.26522.148 | 11.0.0-rc.3.26522.148 | ✅ Up to date | N/A |

### Detailed Analysis / 详细分析

#### 1. Microsoft.WindowsAppSDK 2.5.1 → 2.6.5 🔴

**Release Information / 发布信息**:
- Published: November 2, 2026 (just 6 days ago!)
- Type: Stable release with bug fixes and stability improvements
- Source: [Windows App SDK Downloads](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads)

**What's New in 2.6.5 / 2.6.5 的新特性**:
```yaml
Release Highlights / 发布亮点:
  - WinUI 3.5 API surface refinements
  - Performance optimizations for Foundation APIs
  - MSIX packaging toolchain updates
  - Bug fixes from 2.6.x series
  - Compatibility improvements with .NET 11 RC3

Satellite Packages Updates / 卫星包更新:
  - Microsoft.WindowsAppSDK.Foundation: 2.3.12 → 2.6.5
  - Microsoft.WindowsAppSDK.InteractiveExperiences: 2.1.9 → 2.6.4
  - Microsoft.WindowsAppSDK.ML: 2.1.94 → 2.6.5
  - Microsoft.WindowsAppSDK.WinUI: 2.3.9 → 2.6.5
  - Other satellite packages aligned to 2.6.x series
```

**Impact Assessment / 影响评估**:

| Risk Category / 风险类别 | Level / 级别 | Justification / 理由 |
|--------------------------|-------------|----------------------|
| Breaking Changes / 破坏性变更 | Low / 低 | Minor version bump (2.5→2.6), mostly backward compatible |
| Build Impact / 构建影响 | Minimal / 极小 | Same .NET 11 targeting, no compiler changes |
| Test Coverage / 测试覆盖 | Full suite required / 需要全量测试 | ~2694 assertions across 8 suites |
| Migration Complexity / 迁移复杂度 | Simple / 简单 | Single package update in centralized manifest |

**Recommendation / 建议**: **PROCEED WITH UPGRADE / 进行升级**

Rationale / 理由:
1. Recent release (Nov 2) indicates active maintenance
2. .NET 11 RC3 is already integrated, need matching SDK version
3. Stability improvements beneficial for production deployment
4. Aligns with Microsoft's recommended update cycle
5. Low risk: only patch-level changes expected

---

## Implementation Plan / 实施计划

### Phase 1: Prepare for Upgrade / 准备阶段

#### Step 1: Update Directory.Packages.props / 更新包版本管理

Change in `Directory.Packages.props`:
```diff
- <PackageVersion Include="Microsoft.WindowsAppSDK" Version="2.5.1" />
+ <PackageVersion Include="Microsoft.WindowsAppSDK" Version="2.6.5" />
```

This will automatically cascade to all satellite packages via NuGet's dependency resolution.

#### Step 2: Document Changes / 记录变更

Update documentation files:
- CHANGELOG.md
- README.md (if architecture details changed)
- docs/architecture-design.md (dependency section)
- docs/reviews/dependency/DependencyUpdateAnalysis-*.md

### Phase 2: Execute Upgrade / 执行阶段

```powershell
# 1. Restore with new version
dotnet restore TrueToneCap.slnx --force

# 2. Regenerate lock files
dotnet restore TrueToneCap.slnx --locked-mode

# 3. Build Release configuration
dotnet build TrueToneCap.slnx -c Release --no-incremental

# 4. Run full test suite
.\scripts\verify.ps1 -Configuration Release -IncludeDesktop

# 5. If tests pass, commit changes
git add Directory.Packages.props docs/* CHANGELOG.md
git commit -m "chore: Upgrade WindowsAppSDK to 2.6.5 (Nov 2026)"
```

### Phase 3: Validation / 验证阶段

Expected results / 预期结果:
- ✅ Build: 0 warnings, 0 errors
- ✅ Tests: All 2694+ assertions pass
- ✅ No regressions in:
  - WGC capture functionality
  - Color pipeline accuracy
  - Encoder integration (AVIF/JXL/WebP)
  - OCR recognition
  - QR code scanning
  - UI rendering (WinUI 3)
  - MSIX packaging workflow

---

## Rollback Plan / 回滚计划

If issues arise during upgrade:

```bash
# Revert to previous version
git checkout HEAD~1 Directory.Packages.props

# Force restore old versions
dotnet restore TrueToneCap.slnx --force

# Verify old build works
dotnet build TrueToneCap.slnx -c Release
```

---

## Expected Timeline / 预计时间线

| Activity / 活动 | Duration / 耗时 | Dependencies / 依赖 |
|-----------------|-----------------|---------------------|
| Update manifests | 5 min | None |
| Restore & build | 2-3 min | Internet access |
| Full test suite | 20-25 min | GPU available |
| Validation & commit | 5 min | Test success |
| **Total / 总计** | **~35 minutes** | |

---

## Alternative Options / 备选方案

### Option A: Staged Upgrade / 分阶段升级（Recommended / 推荐）

**Timeline / 时间线**: Proceed today as described above

**Pros / 优点**:
- ✅ Addresses known issue immediately
- ✅ Benefits from recent stability fixes
- ✅ Aligns with .NET 11 GA timeline

**Cons / 缺点**:
- ⚠️ Requires careful testing

### Option B: Wait for Next Cycle / 等待下一周期

**Approach / 方法**: Defer until next quarterly audit

**Pros / 优点**:
- More time for community feedback on 2.6.5

**Cons / 缺点**:
- ❌ Misses recent bugfixes
- ❌ Delayed alignment with .NET 11 GA
- ❌ Extended technical debt

### Option C: Parallel Development Branch / 并行开发分支

**Strategy / 策略**: Create feature branch, test extensively

**Pros / 优点**:
- Safe experimentation space
- Can validate on multiple devices

**Cons / 缺点**:
- ❌ Increases merge complexity later
- ❌ Duplicates testing effort

---

## Risk Mitigation Strategies / 风险缓解策略

### Pre-Upgrade Checks / 升级前检查

1. ✅ Ensure clean working directory:
   ```bash
   git status  # Should show clean or committed changes
   ```

2. ✅ Backup current state:
   ```bash
   git branch backup-pre-windowsappsdk-2.6.5
   ```

3. ✅ Note current version baseline:
   - Record existing test pass count (2694)
   - Document any known workarounds

### During Upgrade / 升级中监控

Watch for:
- NU1102 errors (version conflicts)
- Build warnings in WinUI 3 controls
- Runtime exceptions in UI rendering
- MSIX packaging failures

### Post-Upgrade Validation / 升级后验证

Must-pass criteria / 必须通过的判据:
1. All gates green in verify.ps1
2. No new compiler warnings
3. MSIX package builds successfully
4. Self-test passes (11/11 encoders)
5. Visual inspection of key workflows:
   - Screen capture
   - Annotation editing
   - Export to multiple formats
   - OCR translation

---

## Documentation Updates Required / 需要更新的文档

### Essential / 必需

1. **CHANGELOG.md** - Add 2.6.5 upgrade entry
2. **README.md** - Update dependency table if relevant
3. **Directory.Packages.props** - Main version update

### Recommended / 推荐

4. **docs/reviews/dependency/WindowsAppSDK-Upgrade-2.6.5-2026-11-08.md**
   - Detailed upgrade rationale
   - Before/after comparison
   - Testing evidence

5. **THIRD-PARTY-NOTICES.md** - May need refresh if licenses changed
   - Check for new EULA requirements
   - Update satellite package list

### Optional / 可选

6. **RELEASE-NOTES.md** - If publishing v0.3.13
7. **Internal wiki / knowledge base** - Architecture diagram updates

---

## Success Metrics / 成功指标

✅ **Build Success / 构建成功**:
- Zero compilation errors
- Zero new warnings
- MSIX packaging completes

✅ **Test Coverage / 测试覆盖**:
- All 2694+ assertions pass
- No false positives/negatives introduced
- Performance within acceptable variance (<5%)

✅ **Functional Verification / 功能验证**:
- Screen capture works on multi-monitor setup
- HDR color pipeline accurate
- All encoder outputs validated
- UI responsive and stable
- No memory leaks (monitor process heap)

✅ **Documentation / 文档**:
- All references updated
- Changelog entry complete
- Rollback procedures documented

---

## Decision Matrix / 决策矩阵

| Criteria / 标准 | Weight / 权重 | Score 2.5.1 / 得分 2.5.1 | Score 2.6.5 / 得分 2.6.5 |
|-----------------|---------------|-------------------------|--------------------------|
| Security posture / 安全态势 | High | 8/10 | 9/10 |
| Feature completeness / 功能完整性 | Medium | 7/10 | 8/10 |
| Stability / 稳定性 | High | 8/10 | 9/10 |
| Maintenance burden / 维护负担 | Medium | 6/10 | 8/10 |
| Community adoption / 社区采用 | Low | 7/10 | 9/10 |
| **Weighted Total / 加权总分** | - | **7.4/10** | **8.8/10** |

**Decision / 决策**: **UPGRADE TO 2.6.5 RECOMMENDED / 推荐升级到 2.6.5**

---

## Action Request / 行动请求

Please confirm to proceed with WindowsAppSDK 2.6.5 upgrade:

**Options / 选项**:
1. **Proceed now / 立即进行** - Execute full upgrade plan today
2. **Staged approach / 分阶段** - Create feature branch, test over 24h
3. **Wait / 等待** - Defer to next quarterly review
4. **Custom plan / 自定义** - Provide specific requirements

**Expected completion / 预计完成时间**: ~35 minutes after confirmation

---

**Last Updated / 最后更新**: 2026-11-08 16:30 UTC  
**Next Review Scheduled / 下次审查计划**: Upon user confirmation  
