# Guard Center

## 快速開始：直接執行 exe，或用 build.bat 建置

**Guard Center 可打包成單一 `Guard Center.exe`，在 Windows x64 上直接啟動；也可以下載原始碼，雙擊 `build.bat` 自行產生這個 exe，不必安裝或開啟 Visual Studio。**

### 已取得建置完成的 exe

直接雙擊單檔版本的 `Guard Center.exe` 即可使用，不需要另外安裝 .NET Desktop Runtime 或 SDK。
首次啟動會自動把內含的必要元件展開至使用者的 `%LOCALAPPDATA%\Guard Center\Portable`，再開啟主程式。

### 從原始碼產生 exe

1. 下載此 Repository 的 ZIP 並完整解壓縮，或使用 Git clone。
2. 如果電腦尚未安裝 SDK，從 [Microsoft 官方下載頁](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) 安裝 **.NET 8 SDK 的 Windows x64 版本**。
3. 雙擊專案根目錄的 [`build.bat`](build.bat)，等待建置完成；也可以在 PowerShell 執行：

   ```powershell
   .\build.bat
   ```

4. 開啟 `dist`，執行裡面的 **`Guard Center.exe`**。`dist` 只有這一個檔案，可以單獨複製給其他 Windows x64 使用者。

建置需要的 NuGet 套件與發布用執行元件會由 .NET 自動下載，第一次建置請保持網路連線。
訊息中的 `restore` 是取得專案相依套件的正常步驟。
建置訊息使用英文，結束後視窗會保留結果，按任意鍵才關閉。

### 如果提示缺少元件

| 提示或情況 | 處理方式 |
| --- | --- |
| 找不到 `dotnet`，或顯示未安裝 .NET SDK | 安裝上方官方連結中的 .NET 8 SDK，完成後重新開啟 `build.bat`。 |
| 套件下載失敗或無法連線至 NuGet | 確認網路連線與 NuGet 來源可用，再執行 `build.bat`；相依套件會自動重新取得。 |
| 使用 UAC Guard 等功能時提示需要額外元件 | 依該模組的設定／安裝提示補齊必要元件，並在需要時完成 Windows UAC 授權。硬體功能亦需相容的裝置與驅動。 |

上述單檔使用方式適用於 `build.bat` 產生的 `dist\Guard Center.exe`。
一般 `dotnet build` 的輸出仍需搭配其附檔與 .NET Desktop Runtime。
目前 GitHub 的 Download ZIP 提供原始碼，尚無公開 Releases 成品；可依上方步驟自行建置。

---

## 專案介紹

Guard Center 是一套專為 Windows 設計的桌面系統工具，將日常使用電腦時分散在 Windows、驅動程式、控制台與不同應用程式中的功能集中到同一個介面。

它目前包含：

- Audio Guard
- Device Guard
- Keyboard Guard
- Game Helper
- App Guard
- Link Guard
- Display Guard
- Power Guard
- UAC Guard
- VSR Guard

Guard Center 採用 **C# / .NET 8 / WPF** 開發。

---

# 主要功能

## Audio Guard

Audio Guard 用來管理 Windows 音訊 Session，以及在音訊裝置切換時執行保護動作。

### App Mixer

App Mixer 會列出目前存在的 Core Audio Session。

你可以針對不同應用程式：

- 調整音量
- 靜音 / 取消靜音
- 查看目前正在使用音訊的程式

如果某個程式沒有出現在列表中，先讓該程式播放一次音訊，再回到 Audio Guard。

Guard Center 只會在需要時更新音訊 Session，避免單純瀏覽介面時持續執行不必要的重型操作。

---

## Audio Zero Guard

Audio Zero Guard 是裝置切換保護功能。

例如：

1. 原本使用耳機。
2. 耳機突然拔除。
3. Windows 自動切換到喇叭。
4. 新的輸出裝置可能保留原本的高音量。
5. Audio Zero Guard 偵測到音訊拓樸改變後，立即執行設定的保護。

可選功能：

### Set mute

音訊裝置切換後，將目前播放裝置設為靜音。

### Set volume to 0

音訊裝置切換後，將播放裝置 Master Volume 設成 `0`。

### Zero audio when enabled

開啟 Audio Guard 時立即執行一次目前設定的保護。

### React to property changes

除了裝置增加、移除與切換之外，也將驅動程式或 Audio Endpoint 的屬性變化視為需要重新套用保護的事件。

### Poll interval

設定 Audio Guard 檢查音訊拓樸的間隔。

單位為毫秒。

### Retry delays

設定裝置切換後的補充檢查時間。

部分 Windows 音訊驅動不會在裝置切換瞬間立即完成所有狀態更新，因此 Guard Center 可以稍後再次執行保護。

---

# Device Guard

Device Guard 用來檢查 Windows 硬體與 PnP 裝置狀態。

它分成三個部分：

1. 目前可偵測裝置
2. 電腦核心硬體
3. 輸入裝置堆疊

---

## 目前可偵測裝置

列出 Windows 現在仍能辨識到的 PnP 裝置，例如：

- Bluetooth Adapter
- USB Camera
- USB Audio
- 其他可重新啟動的 PnP Device

Guard Center 可以對指定裝置執行重新啟動，用來處理某些：

- 裝置卡死
- 驅動短暫異常
- USB 裝置沒有正常工作
- Bluetooth / Camera / Audio 裝置需要重新初始化

的情況。

---

## 電腦核心硬體

Guard Center 會從整個 Windows 裝置樹判斷主要硬體能力，例如：

- Bluetooth
- Graphics
- Network
- Audio
- Camera
- USB

並區分：

- 正常
- 異常
- Disabled
- Missing
- Driver required
- Restart required
- Unsupported
- Ambiguous identity

等狀態。

修復操作具有風險等級：

- Low
- Medium
- High
- Critical

較高風險的修復不會在沒有明確授權的情況下直接執行。

部分裝置修復可能需要系統管理員權限、重新啟動 Windows，或重新安裝驅動程式。

---

## 輸入裝置堆疊

這個區域主要用來檢查特殊 Keyboard / Mouse Input Stack。

目前包含對：

- Interception
- HuaJuan 相容層
- Windows Input Device
- Device Namespace

等項目的檢查。

Guard Center 會盡量依照硬體身分辨識輸入裝置，而不是依賴可能隨重新插拔而改變的 Windows Device Number。

---

# Keyboard Guard

Keyboard Guard 將幾個 Windows 中很分散、但經常影響實際使用體驗的鍵盤功能集中管理。

## Disable Shift + Space width toggle

Microsoft 注音等中文輸入法中：

`Shift + Space`

可能會切換：

- 全形
- 半形

開啟此功能後，Guard Center 會阻止這個「全形 / 半形切換行為」。

Shift 與 Space 本身仍然會正常傳給目前程式，因此不會直接把這兩個按鍵封鎖。

## 停用連按五次 Shift 啟動相黏鍵

停用 Windows 的：

**連續按五次 Shift → Sticky Keys**

快捷鍵。

這不會停用 Shift 本身，也不會影響：

- Shift + 字母
- Ctrl + Shift
- 遊戲中的 Shift 操作

Guard Center 只修改 Sticky Keys 的快捷鍵行為。

## Windows keyboard settings

可以直接開啟 Windows：

- Typing Settings
- Language Settings

不用自己在 Windows Settings 裡尋找。

---

# Game Helper

Game Helper 提供一些與遊戲操作相關的系統功能。

## Screen Crosshair

可以在螢幕中央顯示 FPS 準心。

Crosshair：

- 永遠置頂
- 背景透明
- 滑鼠穿透
- 不攔截遊戲滑鼠操作

可以從 Guard Center 主介面控制，也可以直接從系統匣切換：

`Screen crosshair: On / Off`

## Only show in selected apps

Crosshair 可以限制為：

**只有指定程式位於前景時才顯示。**

例如只加入：

`game.exe`

之後，在桌面、瀏覽器或其他程式中就不會出現 Crosshair。

## Keep Enhance pointer precision off

Windows 的：

**Enhance pointer precision**

就是 Windows Mouse Acceleration。

開啟這個 Guard 後：

1. Guard Center 會立即將 Enhance pointer precision 關閉。
2. 之後每 30 秒重新檢查。
3. 如果其他程式重新打開 Mouse Acceleration，Guard Center 會再次關閉。

這是一個全域設定，不綁定單一遊戲。

---

# Game Protection Settings

Game Helper 可以針對個別遊戲建立 Protection Profile。

每個 App 可以獨立設定。

目前包含：

### Windows Key Protection

遊戲執行時封鎖 Windows Key，避免誤觸跳回桌面。

### Right Ctrl + D

提供遊戲期間的特殊 Show Desktop 行為。

### Input Method Protection

針對遊戲控制 Microsoft ENG / Windows Input Method 行為，避免遊戲期間輸入法突然切換。

## Manage apps

Game Helper 的 App 管理分成：

### Protected apps

已經加入 Game Protection 的 App。

### All apps

Guard Center 會從 Windows 已安裝程式、Start Menu 等來源建立 Application Catalog。

第一次切換到 All apps 時才進行背景載入，不會因為單純開啟 Game Helper 就掃描整台電腦。

新增 App 後，即可設定各項 Game Protection。

---

# App Guard

App Guard 是 Guard Center 的應用程式管理中心。

它會整合不同來源建立 Application Catalog，例如：

- Windows Registry
- Start Menu
- Installed Apps
- Steam
- Executable information

Guard Center 會盡量將實際上屬於同一個應用程式的不同來源資料合併，而不是直接顯示大量重複項目。

## App 資訊

展開 App 後，可以查看例如：

- App Name
- Publisher
- Executable Path
- Install Location
- Source
- Running State
- Uninstall information

## App 操作

依照該 App 可以取得的資訊不同，可以執行：

- 啟動 App
- 開啟相關位置
- 管理執行中的 Process
- Terminate
- Uninstall

### Terminate

只會針對符合該 Executable Path 的 Process 執行終止。

執行前會再次要求確認。

Guard Center 不允許從 App Guard 終止 Guard Center 自己。

### Uninstall

如果 Windows Application Catalog 中存在有效 Uninstall Command，就可以從 App Guard 啟動該解除安裝流程。

執行前同樣會要求確認。

---

# Explorer Integration

App Guard 可以加入 Windows Explorer 右鍵選單。

開啟：

**Shortcut right-click entry**

後，Guard Center 會為：

- `.exe`
- `.lnk`

加入 Guard Center 入口。

之後可以直接在 Explorer 中選擇程式並交給 App Guard 處理。

關閉後會移除這個整合。

---

# Link Guard

Link Guard 用來建立應用程式之間的啟動與存活關係。

適合兩個需要一起執行的程式。

例如：

```text
Game.exe → Helper.exe
```

當 Game 啟動後，自動啟動 Helper。

## One-way

```text
A → B
```

意思是：

> A 執行時，B 必須一起執行。

但是：

> 單獨執行 B 不會啟動 A。

例如：

```text
Game.exe → MonitoringTool.exe
```

## Bidirectional

```text
A ↔ B
```

雙向連結。

啟動任何一邊，都會自動啟動另一邊。

## 每條 Rule 可獨立設定

每個 Link Rule 可以獨立控制：

- Enabled
- One-way / Bidirectional
- 是否透過 gsudo 啟動
- Linked App 的結束行為
- 是否持續維持 Linked App 執行
- Launch Delay

因此不同應用程式組可以使用完全不同的行為。

## Manage linked apps

選擇：

**Manage apps**

後有兩個頁籤：

### All apps

列出 Application Catalog 中的 App。

選擇 Trigger App 後，再選擇要和它連動的 App。

### Linked apps

顯示目前已經建立的 Link Rule，可以查看與修改現有關係。

---

# Display Guard

Display Guard 用來控制支援的實體螢幕。

依照顯示器與驅動程式能力不同，Guard Center 可能透過例如：

- DDC/CI
- WMI
- Windows Display APIs

取得硬體控制能力。

並不是所有顯示器都支援所有功能。

## Brightness

如果螢幕支援 Hardware Brightness，可以直接從 Guard Center 調整亮度。

支援：

- Slider
- `+`
- `−`

按鈕可以以 1% 為單位調整。

## Contrast

如果顯示器提供 Contrast Control，也可以從 Guard Center 調整。

沒有提供 Contrast Control 的螢幕不會強行顯示這項功能。

# Multi-monitor Sync

如果同時有多台支援的螢幕，可以使用：

- Sync Brightness
- Sync Contrast

一次調整所有支援該功能的螢幕。

不支援的顯示器會被跳過。

# Display Modes

Display Guard 內建：

- Standard
- Reading
- Scenery
- Movie
- Game
- Custom
- Live

Profile 會依照每台實體 Display 儲存。

因此多螢幕系統中，每台螢幕可以保存不同的 Brightness / Contrast。

如果某個 Profile 中的螢幕目前沒有連線，套用 Profile 時會直接跳過，不會因此阻止其他螢幕套用。

## Live Mode

Live Mode 會保存目前實際調整。

適合直接將 Guard Center 當作螢幕硬體控制面板使用。

## Custom Mode

可以把目前所有支援螢幕的狀態保存成自己的 Custom Profile。

## 顯示器控制注意事項

實體顯示器不是記憶體中的普通數值。

DDC / WMI Command 可能需要數十到數百毫秒才會真正完成，因此 Guard Center 會：

- 將硬體操作序列化
- 避免 Slider 拖曳產生大量硬體 Command
- 防止舊的 Hardware Readback 覆蓋使用者剛設定的新值

如果顯示器剛：

- 開機
- 睡眠恢復
- 重新插拔
- 切換 Display Configuration

可以使用 Refresh 重新偵測。

---

# Power Guard

Power Guard 用來暫時阻止 Windows 因為閒置而自動睡眠。

它使用 Windows Power Request API。

**不會直接修改原本的 Windows Power Plan。**

## 保持清醒

開啟：

**保持清醒**

後，Windows 不會因為 Idle Timer 而自動進入 Sleep。

手動：

- Sleep
- Shutdown
- Restart

仍然可以正常使用。

## 螢幕恆亮

可以額外開啟：

**螢幕恆亮**

此時：

- 系統保持清醒
- Display 也保持開啟

如果關閉這個選項：

- 系統仍然保持清醒
- 螢幕則照原本 Windows 設定自動熄滅

# 保持時間

可以設定 Power Guard 的有效時間。

包含有限時間以及：

### Until Manual

Power Guard 一直保持作用，直到使用者自己關閉。

### Custom

自訂：

**1 分鐘 ～ 30 天**

## 倒數時間軸

有限時間模式下會顯示剩餘時間。

可以直接拖動 Timeline 修改剩餘時間。

變更 Duration 時，倒數會從目前時間重新開始。

## Power Guard 限制

Power Guard 使用標準 Windows Power Request。

實際效果仍可能受到：

- Windows Policy
- Modern Standby
- Battery Policy
- Lock Screen
- OEM Power Management

影響。

---

# VSR Guard

VSR Guard 用來設定：

**NVIDIA RTX Video Super Resolution**

目前主要針對 Google Chrome。

## 必要條件

完整 VSR 功能需要：

- NVIDIA RTX GPU
- 正常 NVIDIA Driver
- Google Chrome
- Chrome Graphics Acceleration
- Windows 將 Chrome 指派給 High Performance GPU
- NVIDIA RTX Video Super Resolution 開啟

Guard Center 會逐項檢查。

## Readiness

VSR Guard 會顯示：

### NVIDIA RTX GPU

確認：

- NVIDIA GPU 是否存在
- 是否屬於支援 RTX VSR 的 GPU
- Driver 是否正常

### Google Chrome

確認：

- Chrome 是否存在
- Chrome Version
- Chrome Executable Path

### Power source

Notebook 使用 Battery Power 時，瀏覽器可能優先採用低功耗處理方式。

因此建議需要 RTX Video Enhancement 時使用 AC Power。

## Required Settings

Guard Center 依序檢查：

### 1. Chrome High performance GPU

確認 Windows Graphics Preference 是否將 Chrome 指派給 High Performance GPU。

Optimus Notebook 特別需要注意這個設定。

### 2. Chrome graphics acceleration

確認 Chrome：

**Use graphics acceleration when available**

已開啟。

修改這個設定後通常需要重新啟動 Chrome。

### 3. NVIDIA RTX Video Super Resolution

確認 NVIDIA Display Driver 中的 VSR Flag 是否已啟用。

## Set up all

如果硬體條件符合，可以使用：

**Set up all**

讓 Guard Center 協助完成可自動完成的設定。

部分操作可能需要：

- 關閉 Chrome
- 系統管理員權限
- 重新啟動 Chrome

## NVIDIA Control Panel

VSR Guard 也可以直接開啟 NVIDIA Control Panel。

可進一步管理：

- Super Resolution
- Quality
- HDR
- Deinterlacing
- Inverse Telecine

等 NVIDIA 原生設定。

---

# UAC Guard

UAC Guard 是 Guard Center 中權限最高、也最需要理解後再使用的功能。

它主要提供給使用 **OpenAI Codex Windows App** 的使用者，讓 Codex 在需要執行系統管理員命令時，可以透過受控制的 `gsudo` Session 執行，而不是反覆跳出 Windows UAC。

**ChatGPT / Codex GUI 本身仍然以一般使用者權限執行。**

UAC Guard 不會關閉 Windows UAC。

# UAC Guard 組成

UAC Guard 會檢查：

- OpenAI.Codex AppX Package
- gsudo
- Guard Center UAC Host
- Windows Scheduled Task
- Program Files 中的 Protected Host
- ACL
- 目前 gsudo Session

## 一鍵安裝／修復

第一次使用 UAC Guard 時：

選擇：

**一鍵安裝／修復**

Guard Center 會建立需要的受保護元件。

這個步驟會要求一次 Windows UAC 授權。

# 授權模式

目前有兩種模式。

## Codex 行程週期

**建議模式。**

Guard Center 偵測：

`Codex app-server`

的 PID。

授權只提供給：

- 該 Codex Process
- 它建立的 Child Processes

Codex 結束後，授權 Session 會被撤銷。

下一次 Codex 啟動後，Guard Center 會重新綁定新的 PID。

這是隔離程度較高的模式。

## Guard Center 生命週期

高風險模式。

Guard Center 開啟後建立 gsudo Session。

直到：

**Guard Center 完全退出**

才撤銷。

這樣即使 Codex 本身重新啟動，也不需要重新建立授權。

代價是：

> 在這段期間，同一個 Windows 使用者執行的其他程式也可能利用目前的 gsudo Cache。

只有確實需要這種工作流程時才建議使用。

## 立即重新偵測／授權

手動重新建立目前選擇的授權 Session。

## 立即終止工作階段

立即執行：

```text
gsudo -k
```

撤銷目前 Administrator Session。

不需要關閉 Guard Center。

## 檢查實際設定

UAC Guard 提供：

### 工作排程器

直接打開 Windows Task Scheduler。

### 受保護目錄

直接查看安裝在 Program Files 中的 UAC Guard Host。

因此 UAC Guard 的授權機制不需要依賴不可見的背景狀態。

## 解除安裝 UAC Guard

只會移除：

- UAC Guard Scheduled Task
- Protected Host
- UAC Guard 自動授權元件
- 舊版 Codex Administrator Launcher 遺留檔案

不會刪除：

- Codex
- ChatGPT
- 對話
- Guard Center 其他設定

也不會修改 Windows UAC Policy。

---

# 系統匣

Guard Center 啟動後會建立 System Tray Icon。

系統匣可以快速操作：

- Open Guard Center
- Power Guard
- 保持清醒
- 螢幕恆亮
- Power Guard Duration
- Screen Crosshair
- Zero playback devices now
- Open Device Guard
- Exit

雙擊 Tray Icon 可以重新打開主視窗。

---

# Windows 開機啟動

在：

**Settings → Startup**

可以開啟：

**Launch at Windows startup**

登入 Windows 後 Guard Center 會：

1. 自動啟動
2. 縮到 System Tray

不會強制把主視窗留在桌面上。

---

# Application Icon

Settings 中可以更換 Guard Center 圖示。

支援：

- PNG
- JPG / JPEG
- BMP
- ICO

自訂圖示會套用到：

- Guard Center Window
- Taskbar
- System Tray
- Startup Shortcut
- Explorer Integration
- App Guard

選擇：

**Reset to default**

即可恢復內建圖示。

---

# 介面操作

左側 Sidebar 可以切換各個 Module。

Module 順序可以拖曳調整，設定會自動保存。

Guard Center 也支援 UI Scale。

可以使用：

`Ctrl + Mouse Wheel`

調整介面比例。

目前允許範圍：

```text
85% ～ 135%
```

---

# 設定保存

Guard Center 會自動保存使用者設定，包括：

- Audio Guard
- Game Helper
- App Protection
- Link Guard Rules
- Display Profiles
- Power Guard
- UAC Guard Mode
- Sidebar Order
- UI Scale
- Custom Icon

因此正常關閉並重新啟動後，不需要重新設定。

---

# 系統需求

## 作業系統

Guard Center 是 Windows 專用程式。

建議：

- Windows 10
- Windows 11

部分功能依賴現代 Windows API，因此 Windows 11 是主要使用環境。

## Runtime / 開發環境

專案 Target Framework：

```text
net8.0-windows
```

使用：

- .NET 8
- WPF
- Windows Forms interoperability
- System.Management
- WPF-UI

---

# 從原始碼執行

目前 GitHub Repository 尚未提供正式 Releases，因此可以直接從原始碼建置。

需要先安裝：

**.NET 8 SDK**

然後 Clone：

```powershell
git clone https://github.com/daniel88516/Guard-Center.git
cd Guard-Center
```

Restore：

```powershell
dotnet restore
```

Build：

```powershell
dotnet build
```

執行：

```powershell
dotnet run --project "Guard Center.csproj"
```

或直接執行：

```text
bin\Debug\net8.0-windows\Guard Center.exe
```

# 建置可交付版本

建置者在 Windows x64 電腦安裝 .NET 8 SDK 後，於專案根目錄執行：

```powershell
.\build.bat
```

腳本會發布主程式及 UAC Guard Host，封裝成 `dist\Guard Center.exe`。
**`dist` 最終只有這一個檔案**；交付時可以單獨複製此 exe，不必附上原始碼或 `bin` 目錄。
建置訊息使用英文；成功或失敗時視窗會停留，按任意鍵才關閉。

使用者在 Windows x64 上首次啟動時，單檔啟動器會自動將內含的程式檔及
.NET 8 執行環境展開至 `%LOCALAPPDATA%\Guard Center\Portable`，再啟動主程式。
因此使用者不需安裝 .NET Desktop Runtime、開啟 IDE 或自行編譯。
一般設定存於 `%LOCALAPPDATA%\Guard Center\Portable\State\Shared\settings.ini`；
更新或移動 exe 時仍可沿用設定。不同版本的展開檔目前可能留在該資料夾。

UAC Guard 等特殊功能仍可能需要相應硬體、驅動、額外元件與使用者授權。
目前尚未提供公開的 GitHub Release 或安裝程式。

---

# 哪些功能需要 Administrator？

Guard Center 本身**不需要整個程式永遠以 Administrator 身分執行**。

一般功能會維持 Standard User 權限。

但下列操作可能需要提升權限：

- Device Guard 部分 Repair
- UAC Guard 安裝 / 修復
- UAC Guard Protected Host 管理
- VSR Driver-level 設定
- 部分 PnP Device 操作
- Link Guard 使用 gsudo 啟動 App

需要時 Guard Center 才會要求 Windows UAC。

---

# 使用上的重要觀念

Guard Center 的設計原則不是「啟動後一直掃描整台電腦」。

大部分昂貴操作只在有需要時執行。

例如：

- App Catalog 使用背景載入
- Display topology 使用事件式刷新
- Audio Session 只在相關頁面需要時更新
- Display DDC 操作不阻塞 UI
- Game Helper 的 All Apps 第一次開啟才掃描
- Link Guard 的 Application Catalog 需要時才載入

因此單純把 Guard Center 留在 System Tray，不代表它會不停執行所有 Hardware Scan。

---

# Troubleshooting

## 找不到 App Audio Session

先讓該 App 播放音訊，再回到 Audio Guard。

## Display Guard 看不到 Brightness / Contrast

可能原因：

- 顯示器不支援 DDC/CI
- 顯示器 OSD 中關閉 DDC/CI
- Driver 不提供該功能
- Display 剛從 Sleep 恢復
- Dock / Adapter 阻擋 DDC Command

先嘗試：

**Refresh**

並確認螢幕 OSD 中 DDC/CI 已啟用。

## VSR Guard 顯示 NVIDIA GPU Unsupported

確認：

- 使用的是 NVIDIA RTX GPU
- NVIDIA Driver 正常
- GPU 目前處於啟用狀態

## Chrome Graphics Acceleration 無法修改

如果 Chrome 正在執行，Chrome 可能會重新寫回自己的 Local State。

先：

1. 關閉所有 Chrome 視窗
2. 確認 Chrome Process 已完全退出
3. 再重新設定

## UAC Guard 找不到 Codex

UAC Guard 的 Codex Process Mode 需要找到：

**OpenAI Codex Windows App**

以及它的：

`app-server`

Process。

如果 Codex 尚未啟動，UAC Guard 會保持等待狀態。

## Device Guard 修復後仍異常

部分 Driver / PnP 問題不能單靠 Restart Device 解決。

可能仍需要：

- Windows Restart
- 重新插拔裝置
- 更新 Driver
- 重新安裝 Driver
- OEM Utility

Device Guard 會盡量顯示對應狀態，例如：

`Restart required`

或：

`Driver required`

---

# 技術架構

主要結構：

```text
Guard-Center
│
├─ App.xaml
├─ App.xaml.cs
│
├─ Modules
│  ├─ AppGuard
│  ├─ AudioGuard
│  ├─ DeviceGuard
│  ├─ DisplayGuard
│  ├─ GameHelper
│  ├─ KeyboardGuard
│  ├─ LinkGuard
│  ├─ PowerGuard
│  ├─ UACGuard
│  └─ VsrGuard
│
├─ Shared
│  ├─ Applications
│  ├─ Collections
│  ├─ GUI.cs
│  ├─ LinkGuardUI.cs
│  ├─ MainWindow.xaml
│  └─ Settings.cs
│
├─ Tools
│  └─ UacGuardHost
│
├─ GuardCenter.Tests
│
└─ Assets
```

主要程式與 UI 維持 Standard User Context。

只有需要系統管理員權限的特定操作才透過獨立的 Elevated Host 或 gsudo 執行。

---

# Project

Repository:

https://github.com/daniel88516/Guard-Center

Guard Center 的目標不是取代 Windows Settings，而是將實際日常會需要反覆調整、監控或修復的 Windows 功能集中到一個介面，減少在不同控制台、設定頁面與第三方工具之間切換。
