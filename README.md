# MyFences

自己用的 [Stardock Fences](https://www.stardock.com/products/fences/) 風格桌面整理工具，Windows 10 / 11，C# / WPF（.NET 8）。

## 功能

- **分區（Fence）**：桌面上半透明、可拖曳、可縮放的框，放捷徑 / 檔案 / 資料夾
  - 從桌面或檔案總管把東西拖進分區（只存路徑，**不會搬動或刪除原始檔**）
  - 分區之間互相拖曳搬移、在同一分區內拖曳排序
  - 雙擊開啟、右鍵「開啟檔案位置」、Delete 鍵從分區移除
- **資料夾分區（Folder Portal）**：即時顯示某個資料夾的內容
- **收合**：雙擊標題列收合成一條，滑鼠移上去時暫時展開
- **雙擊桌面隱藏（Quick-Hide）**：雙擊桌面空白處隱藏 / 顯示所有分區（雙擊圖示開檔不會觸發，可在系統匣關閉）
- **快照**：系統匣 →「快照」
  - 手動建立快照（永久保留），隨時還原
  - 每天自動建立一份快照；「依規則整理桌面」和「還原快照」前也會自動備份（自動快照保留 14 天）
  - 存在 `%AppData%\MyFences\snapshots\`
- **自動整理規則**：分區右鍵 →「自動整理規則…」，多條規則用逗號分隔，`+` 表示「而且」

  | 規則 | 意思 |
  |---|---|
  | `pdf` 或 `.pdf` | 副檔名 |
  | `folder` / `*` | 資料夾 / 所有檔案 |
  | `name:報表` | 檔名包含「報表」（可用 `*` `?`，例如 `name:2024*`） |
  | `newer:7` / `older:30` | 7 天內修改過 / 超過 30 天沒動 |
  | `larger:100` / `smaller:1` | 大於 100 MB / 小於 1 MB |
  | `pdf+name:發票` | 副檔名 pdf **而且**檔名有「發票」 |

  - 一個項目符合多個分區時，看「規則優先順序」（高 / 一般 / 低），同順序照建立順序
  - 系統匣「依規則整理桌面」一次整理；桌面出現新檔案時自動整理（可關閉）；桌面檔案改名時分區會跟著更新
- **外觀**：每個分區可調顏色、背景不透明度、圖示大小（32 / 48 / 64）
- **Ctrl+Alt+F** 或雙擊系統匣圖示：顯示 / 隱藏所有分區
- **隱藏系統桌面圖示**：搭配分區使用，桌面就只剩分區
- 鎖定位置、對齊 10px 格線、開機自動啟動、按 Win+D 時分區仍留在桌面上

預設會建立三個分區：「捷徑」（`.lnk, .url`）、「資料夾」（`folder`）、「文件」（常見文件副檔名）。

## 安裝 / 執行

### 直接下載

每次推到 `main`，GitHub Actions 會建一個單一 exe：到 repo 的 **Actions** → 最新一次 `build` → 下載 `MyFences-win-x64`，解壓後執行 `MyFences.exe`（self-contained，不用另外裝 .NET）。

### 自己 build

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)：

```powershell
dotnet run --project src/MyFences
# 或發佈成單一 exe
dotnet publish src/MyFences -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish
```

也可以用 Visual Studio 2022 開 `MyFences.sln`。

## 設定檔

`%AppData%\MyFences\config.json`（系統匣 →「開啟設定資料夾」）。分區位置、項目、規則都在這裡，可以直接備份或手動編輯（編輯前先結束程式）。

## 已知限制

- 分區存的是「路徑參照」，不是把桌面圖示真的藏起來；建議開「隱藏系統桌面圖示」，讓分區成為桌面唯一的顯示
- 多螢幕不同 DPI 時，位置以 WPF 的 DIP 為單位存檔
