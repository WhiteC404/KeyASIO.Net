# Fork 維護紀錄與上游合併備忘（WhiteC404/KeyASIO.Net）

本文件記錄個人維護 Fork（`origin`：`WhiteC404/KeyASIO.Net`）相對於原作者上游（`upstream`：`Milkitic/KeyASIO.Net`）的所有額外提交、修復範圍、回歸測試與日後合併上游時的衝突處理指引。

- **目前基準上游提交**：`d995795`（`Update Directory.Packages.props`，上游標籤 `v4.0.0-alpha.14`）
- **目前 Fork 分支狀態**：`master` 位於 `80c1eb7`（`dev` 同步於 `688493d`）
- **工作流與開發紅線**：見 [../AGENTS.md](../AGENTS.md)

---

## 相對於 `upstream/master` 的提交清單

### 1. `8cdcbd4`（2026-11-19）— `Fix osu!stable detection and lazer fallback`

解決 `osu!stable` 與 `osu!lazer` 進程同為 `osu!` 導致的誤附著與回退異常：

- **`src/Core/KeyAsio.Sync/Sources/MemoryScan.cs`**
  - 新增 `FindStableProcess()`，透過 `FileVersionInfo.GetVersionInfo(path).CompanyName == "ppy"` 精準辨識 `osu!stable`，避免誤連到 `osu!lazer` 進程，並正確釋放非目標的 `Process` 控制代碼。
  - 連線失敗時於 `finally` 立即執行 `CleanupProcess(..., delayOnDisconnect: false)` 避免殘留失效控制代碼。
- **`src/Core/KeyAsio.Sync/Sources/MemorySyncBridge.cs`**
  - 改由訂閱 `LazerIpcGameSyncSource.AvailabilityChanged`（取代直接依賴 `LazerIpcBridge.ChannelConnectionChanged`）控制 `_stableSource.SetMemoryScanSuppressed(source.IsAvailable)`，確保 Lazer 不可用時能正確回退到 Stable 掃描。

---

### 2. `688493d`（2026-11-27）— `Fix gameplay session lifecycle, hitsound caching, sequencer races, sync ordering, and settings persistence`

全面審查並修復遊玩會話生命週期、打擊音快取、四模式排序器競態、同步順序、設定存檔與執行期細節（共變更 25 檔，新增 14 個回歸測試，驗證結果：`KeyAsio.UnitTests` 96/96、`AudioTests` 4/4）：

#### P1：會話生命週期、打擊音預載與排序器修復
- **會話離開即乾淨收尾**（`PlayingState.cs`、`GameplaySessionManager.cs`）：
  - `PlayingState.Exit` 統一呼叫 `_gameplaySessionManager.Stop()`，確保進入結算畫面（`ResultsScreen`）、返回選歌（`SongSelection`）或關閉遊戲（`NotRunning`）皆即時終止會話、停止滑條循環音效並還原 GC 延遲模式。
  - `GameplaySessionManager.Stop()` 對 `SessionStopped` 事件維持冪等，避免重複發送。
- **打擊音預載不再被音樂解碼阻塞**（`GameplayAudioService.cs`、`GameplaySessionManager.cs`、`BeatmapHitsoundLoader.cs`）：
  - 音樂預載獨立出 `_musicCachingWorker` 管線，進圖後優先排入首批打擊音快取，消除開局前幾顆無聲問題。
- **四模式排序器執行緒安全與軌道／倍率修正**（`StandardHitsoundSequencer.cs`、`TaikoHitsoundSequencer.cs`、`CatchHitsoundSequencer.cs`、`ManiaHitsoundSequencer.cs`、`SyncSessionContext.cs`、`KeyboardBindingInitializer.cs`）：
  - 修正 Mania 偶數鍵（4K/6K/8K/10K）於 `X = 512` 時軌道索引四捨五入越界導致整圖無聲的問題。
  - 四模式佇列存取加鎖保護；Auto / Cinema / Relax / Replay 模式下忽略手動按鍵誤觸。
  - 修正 `Mods.Unknown` 被誤判為 DoubleTime（1.5x）倍率的問題，維持正常 `1.0x`。

#### P2：同步順序、來源協調與設定持久化
- **進圖資料更新順序與非同步狀態防競態**（`OsuMemoryData.cs`、`SyncController.cs`、`GameSyncSourceCoordinator.cs`、`MemorySyncBridge.cs`）：
  - 確保進入 `Playing` 狀態前先更新譜面、Mods 與 `IsReplay`，再發送狀態切換事件。
  - `SyncController` 加入狀態轉換版本號檢查，防止舊的非同步轉換覆蓋較新狀態。
  - `MemorySyncBridge` 局部來源啟動失敗時自動回滾已啟動的來源，並支援停用中途重新啟用同步。
- **設定存檔完整性**（`SettingsManager.cs`）：
  - 修復 `NightcoreBeats`、`TailPlaybackBehavior`、`EnableExtendedVolume` 等設定未正確持久化問題並清理防抖計時器，同時維持 `SampleRate` 等音訊裝置屬性僅透過 `IAudioDeviceOperationCoordinator` 交易持久化。

#### P3：多開 IPC、NLog 6、cfg 解析與相容性
- **多開 IPC 防刷錯**（`IpcService.cs`）：`AllowMultipleInstance` 啟用時跳過單例管線監聽，管線被佔用時平滑退出。
- **NLog 6 設定修正**（`nlog.config`）：將已移除的 `regex` 屬性改為 `text`。
- **Stable cfg 解析修正**（`MemoryScan.cs`）：`EnsureSongsDirectory` 改為在去除註解後的有效片段上搜尋 `=`。
- **Sentry 與測試工具建置修正**（`EmbeddedSentryConfiguration.cs`、`tests/ColumnTest/Program.cs`、`tests/StressTest/Program.cs`）。

#### 本 Fork 新增的回歸測試檔
- `tests/KeyAsio.UnitTests/GameplaySessionManagerTests.cs`
- `tests/KeyAsio.UnitTests/MemorySyncBridgeTests.cs`
- `tests/KeyAsio.UnitTests/SettingsManagerTests.cs`
- `tests/KeyAsio.UnitTests/PlayingStateTests.cs`（擴充 Auto／Replay／`Mods.Unknown` 倍率案例）

---

### 3. `80c1eb7`（2026-11-27）— `Fix missing combobreak sound after toggling AllowAutoLoadSkins`

修復關閉再開啟「自動載入皮膚（`AllowAutoLoadSkins`）」後，預設打擊音／斷連音效（`internal://combobreak`）變為無聲的問題：

- **`src/Common/KeyAsio.Application/Services/SkinManager.cs`**
  - 關閉 `AllowAutoLoadSkins` 時改為呼叫 `_stableDefaultResources.Clear()`（並同步清除 Lazer 預設資源與皮膚目錄索引、發出 `ResourcesChanged`），不再將 `_stableDefaultResources` 各項覆寫為 `Array.Empty<byte>()`。
  - 記錄已提取資源的來源路徑（`_stableDefaultResourcesPath` / `_lazerDefaultResourcesPath`），確保重新啟用 `AllowAutoLoadSkins` 或變更 `OsuFolderPath` 時能正確重新從 `osu!gameplay.dll` / `osu.Game.Resources.dll` 提取預設音效。
  - `TryGetStableResource` / `TryGetLazerResource` 增加 `data.Length > 0` 防護；預設資源字典改為 `ConcurrentDictionary` 避免背景音訊快取執行緒讀寫競態；`ExtractDefaultResources` 遇到單筆非位元組陣列或過短資源時改為 `continue` 而非提早 `return`。
  - `PublishSkinListAsync` 補上 `SynchronizationContext.Current` 的空值保護。
- **`src/Core/KeyAsio.Sync/Services/GameplayAudioService.cs`**
  - `ResolveAndLoadSkinAudioAsync` 對 `TryGetLazerResource` 與 `TryGetStableResource` 回傳的位元組陣列增加 `Length > 0` 防護，避免空位元組流寫入靜音快取。
- **回歸測試**：
  - 新增 `tests/KeyAsio.UnitTests/SkinManagerTests.cs`（驗證開關 `AllowAutoLoadSkins` 與變更 `OsuFolderPath` 重新提取 `combobreak`）。
  - 擴充 `tests/KeyAsio.UnitTests/GameplayAudioServiceTests.cs`（驗證空位元組資源不會污染快取且後續可自動恢復）。

---

## 日後合併 `upstream/master` 的標準步驟與衝突指引

```powershell
git checkout master
git fetch upstream --tags
git merge upstream/master
```

遇到合併衝突時，依下列原則處理：

1. **確認上游是否已修復相同問題**：
   - 若上游已重構或以更佳方式修復 `MemoryScan`、`GameplaySessionManager`、`*HitsoundSequencer`、`SettingsManager`、`IpcService` 或 `nlog.config` 中的對應問題，可採用上游實作，但**必須保留並跑過本 Fork 新增的回歸測試**（`GameplaySessionManagerTests`、`MemorySyncBridgeTests`、`SettingsManagerTests`、`PlayingStateTests`）驗證沒有回歸。
2. **務必優先檢查版本號**：
   - 合併後若 `KeyASIO.exe` 的 `ProductVersion` 出現 `1.0.0-alpha.*`，表示本機缺少上游新標籤，補跑 `git fetch upstream --tags` 後重新建置即可。
3. **合併後必跑驗證**：
   - 依 [../AGENTS.md](../AGENTS.md) 執行全方案 `Release` 建置、`KeyAsio.UnitTests`、`AudioTests`、`ProductVersion` 檢查與 `git diff --check`。
   - 更新本文件頂部的「基準上游提交」並標註已被上游吸收的項目。
