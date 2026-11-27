# AGENTS.md

本文件提供給在此倉庫（`WhiteC404/KeyASIO.Net` 個人維護 Fork）工作的 AI Agent 與開發者，規範 Fork 分支管理、隔離建置工具鏈、核心架構不變量、防回歸紅線與標準驗證流程。

> 專案整體分層架構與外掛契約請同時參閱上游文件 [docs/architecture.md](docs/architecture.md)；本 Fork 相對於上游的改動與合併指南見 [docs/fork-notes.md](docs/fork-notes.md)。

---

## Fork 定位與分支規範

- **遠端設定**：
  - `origin`：`https://github.com/WhiteC404/KeyASIO.Net.git`（個人維護 Fork）
  - `upstream`：`https://github.com/Milkitic/KeyASIO.Net.git`（原作者上游）
- **維護模式**：本 Fork 僅供個人長期維護使用（不對上游提交 PR），以 `master` 為主分支（GitHub Actions CI 僅在 `master` push 與 PR 觸發，見 `.github/workflows/ci.yml`）。
- **同步上游（務必帶 `--tags`）**：
  - 本專案以 `MinVer` 依 Git Tag（`MinVerTagPrefix = v`）產生組件版本；若本機缺少上游 `v*` 標籤，建置產物會退回 `1.0.0-alpha.0.x` 並觸發誤報更新。
  - 每次同步上游請執行：
    ```powershell
    git fetch upstream --tags
    git merge upstream/master
    ```
- **避免無謂合併衝突**：不要大幅改寫上游維護的 `README.md`、`docs/architecture.md` 或 `docs/Updates/`；本 Fork 專屬規則與變更記錄統一維護於本檔與 `docs/fork-notes.md`。

---

## 建置與測試工具鏈

本工作區使用隔離的 `.NET 10` SDK 與快取（位於上層工作區 `..\..\work\`），建置與測試請一律設定對應環境變數，避免依賴系統全域 SDK：

```powershell
# 從倉庫根目錄（dev\KeyASIO.Net）執行；若工作區結構不同請將 $workspaceRoot 改為實際上層路徑
$workspaceRoot       = (Resolve-Path '..\..').Path
$env:DOTNET_ROOT     = Join-Path $workspaceRoot 'work\toolchains\dotnet'
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$env:DOTNET_CLI_HOME = Join-Path $workspaceRoot 'work\dotnet-home'
$env:NUGET_PACKAGES  = Join-Path $workspaceRoot 'work\nuget-packages'
$sdk = Join-Path $env:DOTNET_ROOT 'dotnet.exe'

# 1. 全方案 Release 建置（對齊 CI）
& $sdk build KeyAsio.Net.slnx -c Release --nologo -v minimal

# 2. 執行全部自動化單元測試（對齊 CI）
& $sdk test tests/KeyAsio.UnitTests/KeyAsio.UnitTests.csproj -c Release --no-build --nologo
& $sdk test tests/AudioTests/AudioTests.csproj -c Release --no-build --nologo

# 3. 驗證組件版本號未異常退回 1.0.0-alpha
$exe = Resolve-Path 'src/Apps/KeyAsio/bin/Release/net10.0/KeyASIO.exe'
[System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion

# 4. 檢查空白字元與 Diff 整潔度
git diff --check
```

- **子模組相依**：方案包含 `dependencies/` 下的 Git Submodules（`OverlayAPI.LazerProtocol`、`Milki.Extensions`、`NAudio`），切分支或新 clone 時確保子模組已更新。
- **建置產物路徑**：最新版可執行檔輸出位於 `src\Apps\KeyAsio\bin\Release\net10.0\KeyASIO.exe`（請留意勿誤開過期的 `bin\Debug` 產物）。

---

## 核心架構紅線與關鍵不變量

### 1. 分層依賴邊界（承襲 `docs/architecture.md`）
- `KeyAsio`（`src/Apps/KeyAsio`）為唯一的 Composition Root，下層專案不可透過 `IServiceProvider` 解析服務。
- `KeyAsio.Sync` **不可**依賴 `KeyAsio.Application`；外掛僅能參考 `KeyAsio.Plugins.Contracts`，不可參考宿主內部專案或 Avalonia/Suki。
- 所有音訊裝置切換與取樣率異動一律經由 `IAudioDeviceOperationCoordinator` 進行交易式處理，失敗須同時還原裝置與持久化設定。

### 2. 不可破壞的同步與音訊不變量
以下皆為本 Fork 已修復並由單元測試守護的高風險邊界，修改相關程式時必須維持：

- **會話生命週期與冪等收尾**：
  - `PlayingState.Exit` 必須呼叫 `GameplaySessionManager.Stop()`，確保切往結算畫面（`ResultsScreen`）、選歌畫面（`SongSelection`）或遊戲關閉（`NotRunning`）都能即時停止會話、清除滑條循環音效並還原 GC 延遲模式。
  - `GameplaySessionManager.Stop()` 對 `SessionStopped` 事件必須具備冪等性，避免重複觸發。
- **打擊音預載優先權**：
  - 整首背景音樂解碼必須與打擊音預載解耦（`GameplayAudioService` 獨立音樂快取工作管線），進圖後先排入首批打擊音快取再載入音樂／皮膚，避免開局前幾顆無聲。
- **四模式 Sequencer 執行緒安全與邊界**：
  - `StandardHitsoundSequencer`、`TaikoHitsoundSequencer`、`CatchHitsoundSequencer`、`ManiaHitsoundSequencer` 的音符佇列存取必須加鎖保護。
  - Mania 軌道計算必須夾擠於有效範圍 `[0, columnCount - 1]`（特別是偶數鍵下 `X = 512` 不可越界）。
  - Auto / Cinema / Relax / Replay 模式下，自動播放時應忽略手動按鍵誤觸；`Mods.Unknown` 不可被誤判為 DoubleTime（須維持 `1.0x` 倍率）。
- **狀態同步順序與版本防競態**：
  - `MemoryScan` 區分 `osu!stable` 與 `osu!lazer`（兩者程序名皆為 `osu!`）須比對執行檔 `CompanyName == "ppy"`。
  - 狀態進入 `Playing` 前必須先完成譜面、Mods、IsReplay 更新，再發布狀態變更；`SyncController` 須保留非同步狀態轉換版本號檢查，防止過期轉換覆蓋最新狀態。
  - `MemorySyncBridge` 啟動多來源時若發生局部失敗，須自動回滾已啟動的來源；並以 `LazerIpcGameSyncSource.AvailabilityChanged` 控制 Stable 掃描抑制。
- **設定存檔邊界**：
  - `SettingsManager` 必須正確持久化所有可變設定（含 `NightcoreBeats`、`TailPlaybackBehavior`、`EnableExtendedVolume` 等）並妥善清理防抖計時器，同時**不可**繞過 `IAudioDeviceOperationCoordinator` 直接寫回 `SampleRate` 等裝置交易屬性。
- **多開 IPC、日誌與設定檔解析**：
  - `IpcService` 在 `AllowMultipleInstance` 為 `true` 時應跳過單例管線監聽，且管線被佔用時須平滑退出不刷錯誤日誌。
  - `nlog.config` 須使用 NLog 6 相容語法（使用 `text` 而非已移除的 `regex` 屬性）。
  - `MemoryScan.EnsureSongsDirectory` 解析 `osu!.<user>.cfg` 時須在去除註解後的有效片段上搜尋 `=`。

---

## 本機環境與安全規範

1. **不得未經明確指示改動日常正式安裝目錄**：若本機存在獨立於此倉庫之外的正式使用目錄，除非使用者明確要求同步設定或部署覆蓋，否則只讀不改。
2. **禁止殘留測試垃圾**：若進行 UI／煙霧測試建立暫存目錄（例如 `..\..\work\keyasio-dev-smoke-*`），驗證完畢必須立即清理。
3. **實機音訊與單例衝突注意**：若本機 `appsettings.yaml` 設為 `allowMultipleInstance: false` 或使用獨占式 ASIO 裝置，啟動或重啟測試實例前應先檢查既有的 `KeyASIO` 進程，避免與日常實例搶占裝置或觸發單例鎖。
4. **切勿提交敏感或本機設定**：不可提交私鑰（`keyasio_private.key`）、本機 `appsettings.yaml`、個人絕對路徑／硬體資訊、日誌（`logs/`）或臨時排錯紀錄。

---

## 文檔同步義務

- 每次向 `master` 新增相對於 `upstream/master` 的新修復或功能，需同步記錄於 [docs/fork-notes.md](docs/fork-notes.md)。
- 每次 `git merge upstream/master` 完成後，需回頭核對 [docs/fork-notes.md](docs/fork-notes.md)，標註已被上游吸收的項目並移除過期說明。
