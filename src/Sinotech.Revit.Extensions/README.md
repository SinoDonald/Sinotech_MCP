# SinotechExtensions：中興自行開發命令集

> Copyright © 2026 Donald（donaldlu@mail.sinotech.com.tw），保留所有權利。

這是 Sinotech_MCP 的**獨立命令集**，和原本衍生自開源專案的 `RevitMCPCommandSet` 分開編譯、分開部署。安裝後 Revit 啟動時即自動載入，不需要另外啟用。

## 功能一覽

依連結 CAD 自動建柱、樑、板、牆是四個**獨立**功能（參考 TYBIM_MCP 0.7.1 的做法）。自動建模直接執行、不跳確認、AI 不會再詢問；完成後可用 Revit 的「復原」還原。

| 功能 | MCP 工具 | 說明 |
|---|---|---|
| 讀取圖層 | `get_cad_layers` | 圖層雙線統計、建議牆圖層、柱形狀數、梁圖層名稱特徵、牆／樓板類型、樓層（唯讀） |
| 自動建柱 | `create_columns_from_cad` | 只建柱：依柱輪廓建立結構柱，基準樓層到頂部樓層 |
| 自動建梁 | `create_beams_from_cad` | 只建梁：依梁雙線求中心線，讀取 DWG 尺寸標註；`placeColumns` 可要求一併建下層柱 |
| 自動建板 | `create_floors_from_cad` | 只建板：依已有的牆柱圍出的房間，每個房間一塊、邊界在牆柱內緣 |
| 自動建牆 | `create_walls_from_cad` | 預設只建牆；`placeDoors`、`placeWindows`、`placeColumns`、`placeFloors` 明確要求才啟用；建好後自動接合（`autoJoin`） |
| 自動接合 | `auto_join_elements` | 接合／取消接合柱、梁、牆、板（指定元素 ID，或目前視圖的主體／目標品類）；執行前在 AI助手確認 |

使用範例（AI助手）：

1. 「用連結的 CAD 自動建牆」→ 只建牆。
2. 「建牆，門也一起放」→ 牆＋門（不放窗）；「建牆並放門窗」→ 牆＋門＋窗。
3. 「依 CAD 建柱」→ 只建柱。
4. 「依結構平面圖建梁」→ 只建梁。
5. 「牆柱建好後建板」→ 每個房間一塊樓板。
6. 「建牆，先不要接合」→ `autoJoin=false`，只建牆。
7. 「依 CAD 建柱，柱用 Concrete-Rectangular-Column：600 x 750mm」→ 指定柱類型（`columnTypeId`／`columnTypeName`）。
8. 「把目前視圖的牆跟柱、板接合，保留樓板原本的切割順序」→ `auto_join_elements`。

共同規則：

- 在 Revit 開啟含有**連結 DWG**的平面視圖（用「連結 CAD」放入，不支援「匯入 CAD」）。
- **只處理目前視圖裁剪範圍內的 CAD 圖形**；一張 DWG 有多個樓層平面時，請先開啟「裁剪視圖」框到要建的那一層。
- 未指定時，樓層使用目前平面視圖的樓層與其上一層；牆類型依 CAD 牆厚自動選型（見下節）。

### 牆類型自動選型（`WallTypeSelector.cs`）

使用者指定 `wallTypeId`／`wallTypeName` 時全部牆使用該類型；未指定時依 CAD 雙線間距（牆厚）：

1. 優先使用同厚度（±1 mm）的既有基本牆類型（同厚度有多個時，優先專案預設類型、再來單層類型）。
2. 沒有同厚度類型時，從厚度最接近的**單層、非垂直複合**基本牆複製一個新類型，名稱如「Sinotech CAD 120mm - RC 牆 15cm」，厚度設為 CAD 牆厚，材料與層功能沿用來源；同一次執行相同厚度只複製一次，重複執行沿用同名類型。
3. 不修改既有類型，也不縮放多層／複合牆。沒有可安全複製的單層類型時才用厚度最接近的類型，並在 `thicknessMismatches` 回報。新建的類型列在 `createdWallTypes`。

選型只依幾何厚度；CAD 沒有材料資訊，複製的類型沿用來源的材料與防火設定，請自行檢查。

### 何時自動接合（`ElementJoiner.cs`）

建牆分兩階段，避免一個接合錯誤讓整批牆回滾：

1. **建立**：每道牆建立後立即停用兩端接合（`DisallowWallJoinAtEnd`），門窗、柱與牆在同一個交易提交（一筆 Undo）。這個交易若出現「無法保持接合」（CannotKeepJoined），只在失敗元素都是本次新建、且 Revit 提供「解除接合」時套用解除接合，同一組只試一次（`recoveredJoinElementIds`）；不刪除、不移動牆，也不動既有元素。
2. **接合**（`autoJoin`，預設 true）：牆提交後，在另一個交易群組（另一筆 Undo）只處理本次新建的牆：
   - 候選配對：牆端點距另一道牆中心線在兩牆半厚和＋5 mm 以內，且高度範圍重疊。
   - 先恢復牆端接合（先整批，失敗再逐端）；牆端若會接到**本次以外的既有牆**，該端保留不接合（`endsKeptDisallowed`），不改動既有模型。
   - 牆端沒有接上的配對改試幾何接合；仍失敗就只復原該組，牆與其他成功的接合保留，失敗的元素 ID 與座標列在 `autoJoin.issues`。
3. 要求「先不要接合」時設 `autoJoin=false`，牆兩端保持停用接合（`joinsDeferred=true`），之後可用 `auto_join_elements` 處理。
4. 一併建板（`placeFloors`）時，樓板在接合後另一個交易建立。

回報 `success` 代表牆已建立，不代表所有接合都成功；請看 `autoJoin` 的 `wallEndJoinedPairs`、`geometryJoinedPairs`、`failedPairs`。`postProcessingError` 表示牆已提交但後續處理中止，不可重新建立同一批牆。

### 自動接合工具（`auto_join_elements`）

- 範圍二擇一，不預設整個模型：`elementIds`（2～500 個）或 `hostCategory`＋`targetCategories`（目前視圖，`columns`、`beams`、`walls`、`floors`，相關元素最多 500 個）。
- 牆與牆用上面的牆端接合流程；其他配對用幾何接合（`JoinGeometryUtils`）。候選以包圍盒（含 `toleranceMm`，預設 5、最大 50）重疊判斷，不保證實體相交；容差不會移動元素或填補空隙。
- `action=unjoin` 只取消幾何接合，不停用牆端接合。
- `floorPriority`：`preserve`（預設，保持既有順序）、`floorCuts`（樓板切割其他元素）、`otherCutsFloor`；檢查目前切割元素後才切換。
- 每組獨立交易，失敗只復原該組；`maxPairs` 預設 1000、上限 2000，超過時直接拒絕，不先處理一半。全部成功的操作合併為一筆 Undo，不自動儲存。
- 屬模型修改，AI助手會先顯示參數供確認。

### 門窗（`OpeningDetector.cs`、`OpeningPlacer.cs`）

建牆時明確要求才放置：門用 `placeDoors`、窗用 `placeWindows`（舊版 `placeOpenings` 代表兩者）。CAD 有門窗圖層不代表要放門窗。判斷只看幾何，不看圖層名稱：

1. 找開口：同牆厚、共線的兩段牆之間 40～360 cm 的缺口，以及開口圖層線條補齊的缺口。
2. 判斷種類：
   - 圓心在門樘、半徑約等於開口寬的圓弧 → 單開門；兩個半寬圓弧 → 雙開門
   - 牆厚範圍內有兩條以上橫跨開口的平行線 → 窗
   - 牆面外一個牆厚內有門片線 → 推拉門
   - 兩側門樘有伸出牆面的短邊 → 柱（不是開口，略過）
   - 其他 → 只回報位置，不放置
3. 只有要放置的種類會把缺口補成連續牆（例如只放窗時，門的缺口保留），再放置專案中寬度最接近的門窗類型（雙開門找名稱含 Double／雙、推拉門找 Sliding／推拉 的族群）；寬度相差超過 10 mm 時複製一個「原類型名 寬度mm」的類型。
4. 依族群在平面視圖中自己的開門弧線，翻轉開門方向與鉸鏈側以符合 CAD。窗台高度可用 `windowSillHeight` 指定。
5. 門窗只放在基準樓層的牆上（逐層建牆時亦同）。

### 結構柱（`StructureDetector.cs`、`StructurePlacer.cs`）

- 柱形狀：邊長 20～150 cm 的封閉矩形或圓。
- 柱圖層自動判定：柱形狀佔該圖層封閉輪廓 60% 以上、最常見尺寸佔 30% 以上，且一半以上貼著牆或梁（依據為模型中已建的牆梁；沒有時用 CAD 牆線；都沒有時只採名稱含 COL／柱 的圖層）。`0`、`DEFPOINTS` 與名稱含門窗、衛浴、標註等字樣的圖層不採用。少量柱或判定不到時，用 `columnLayers` 指定。
- 結構柱高度為基準樓層到頂部樓層，依矩形方向旋轉。
- **柱類型選擇**（`ColumnTypeChoice.cs`、`StructurePlacer.cs`）：
  - 未指定時自動選型：專案中 b×h（圓柱為直徑）相同（±5 mm）的類型優先，混凝土族群優先；沒有就以同族群複製「寬x深cm」（圓柱「D直徑cm」）。專案中沒有混凝土柱族群時從 Revit 族庫載入，載入不到時回報。
  - 使用者指定 `columnTypeId` 或 `columnTypeName`（類型名稱或「族群：類型」）時：`matchCadSize=true`（預設）以該類型的族群為準，選尺寸相同的類型，沒有就以指定類型複製出 CAD 尺寸；`matchCadSize=false` 全部柱直接使用指定類型。
  - 指定的類型找不到，或名稱對應多個類型時，在建模前回報可選清單（`get_cad_layers` 的 `columnTypes` 也會列出），不猜測、不建立任何元素。指定類型若沒有可調整的寬／深（或直徑）參數，直接使用該類型並提醒尺寸可能不同。
  - 寬、深參數名稱支援 b／B／Width／寬度／柱寬、h／H／Depth／深度／柱深 等。
- 建柱結果的 `elementIds` 可交給 `auto_join_elements` 與牆、梁、板接合。
- 結構柱設為房間邊界，建板時才能沿柱與牆圍出房間。

### 樓板（`FloorBuilder.cs`）

必須先有牆、柱等房間邊界。在交易中暫時自動放置房間、取得各房間在牆與柱內緣（面層）的邊界後撤銷這些暫時房間（專案原有房間保留並一併建板），每個房間建一塊樓板（專案預設樓板類型或 `floorTypeName`）。房間內有電梯井叉線，或 6 條以上間距 20～35 cm 的平行線（樓梯踏步）時不建板；視圖有裁剪範圍時，只處理完整位於範圍內的房間。單獨建板不會刪除既有樓板，重複執行會重複建立。

### 自動建梁（`create_beams_from_cad`）

用於結構平面圖：

1. 梁圖層：名稱有梁代號（B、J、G、SB、SJ、BEAM、梁、樑，例如 `0S-B`、`0S-J`）且有平行雙線的圖層，也可用 `layers` 指定。
2. 同一圖層內平行、重疊、間距 15～120 cm 的雙線取中心線，梁寬為間距；斷在柱位置（共線、間隔 150 cm 內）的兩段延伸到柱心，梁端延伸到相交的梁。
3. 梁尺寸由連結 DWG 的標註文字讀取（`DwgTextReader.cs`，以 ACadSharp 直接讀檔，因為 Revit API 不提供 CAD 文字；單檔上限 256 MB）：RC 梁「B23(50X70)」為寬×深 cm，鋼梁「H500X200」為深×翼寬 mm。沒有標註的沿用同一直線上的標註，仍無則梁深 = 梁寬 × 1.4。讀不到或對不準時會回報。
4. RC 梁使用混凝土矩形梁族群（b×h，沒有就複製類型、必要時從族庫載入）；鋼梁須專案中已有名稱含相同斷面的結構構架類型，否則略過並回報。梁頂齊指定樓層。
5. `placeColumns` 可要求一併放置結構柱（下一層到此樓層），自動判定時只採用名稱含 COL／柱 的圖層（例如 `0S-COL-H` 下層柱虛線），排除柱頭、擋土柱。

### 建牆方式（`WallPairBuilder.cs`）

直接配對平行雙線，**不要求牆輪廓封閉**（門口、T 字交接處的牆線常是開口的）：

1. 同圖層、同方向（0.5° 內）的線先接成連續線；旋轉過的圖面（例如斜放的車站）也適用。
2. 相鄰兩條平行線之間沒有其他線、間距 50 mm～`maxThickness`（預設 1500 mm）的部分視為牆；一條線兩側都有配對時屬於較薄的一側（較厚的多半是兩道牆之間的窄空間）。
3. 排除：等間距重複 3 次以上的線（樓梯踏步、填充線）、有對角線的矩形（開口、管道間符號）、不與其他牆相連的短小矩形（柱、設備）。
4. 同一道牆的牆段接起來：一側牆線連續、另一側斷口有往牆外延伸的交接牆線時（T 字交接）接成一道；窗戶處框線在牆厚內，不接。牆端延伸到相交牆的中心線（T 字與 L 形交接）。
5. 開口圖層（名稱含 `OPEN`、`DOOR`、`WINDOW`、`開口`、`門`、`窗`）的線只用來把門窗兩側的牆接成一道並記錄門窗位置，不會單獨產生牆；畫在連續牆厚內的開口框（例如帶狀窗）也記錄為門窗位置。
6. 只處理**直線**；圓弧會略過並計入「不支援的曲線」（以短直線組成的弧形牆可以建立）。

### 牆線圖層自動判定（`WallLayerDetector.cs`）

不依賴圖層名稱（MicroStation 轉出的圖層常是「圖層 27」這類編號），而是每個圖層先實際求牆再判斷：

- **主要牆圖層**：牆會互相連成牆網——5 道以上相連的牆總長達最多者的 20% 以上（至少 5 m），且平均每道牆長 2 m 以上。樓梯、填充線、開口符號、柱、設備、電扶梯等雖有雙線，但不成牆網或牆段很短。
- **次要牆圖層**：牆有三成以上接在主要牆上、平均牆長 1.5 m 以上、主要牆厚與某個主要牆圖層相同（例如窗邊牆墩另畫一層）；或名稱含 `WALL`／`牆`。
- 名稱含柱、樓梯、家具、標註、文字、填充、軸線、衛浴等字樣，以及梁圖層（B、J、G、SB、SJ、BEAM、梁、樑）不會被自動選入。
- 使用者指定圖層時以指定為準；`get_cad_layers` 會回報各圖層的 `wallLength`、`networkLength`、`meanWallLength` 供參考。

辨識規則：

- 勾選「逐層」（`byLevel`）時，會在基準與頂部樓層之間的每個樓層把牆分段。

## 資料夾結構

```
src/Sinotech.Revit.Extensions/
├─ Sinotech.Revit.Extensions.csproj   建置後部署到 revit_mcp_plugin\Commands\SinotechExtensions\
├─ command.json                       命令清單與開發者資訊（Revit 啟動時讀取）
├─ LICENSE.md                         授權聲明
├─ Common/                            共用：回傳格式、ElementId 跨版本相容
└─ AutoBuild/                         自動建牆
   ├─ CadLayerReader.cs               讀取連結 CAD 的線條與圖層
   ├─ WallPairBuilder.cs              平行雙線 → 牆中心線（不要求封閉）
   ├─ WallRegionGeometry.cs           封閉雙線區域 → 牆中心線（移植自 TYBIM，開口判斷共用）
   ├─ WallTypeSelector.cs             依 CAD 牆厚選用／複製牆類型
   ├─ ElementJoiner.cs                兩階段接合、逐組交易與失敗處理
   ├─ ColumnTypeChoice.cs             使用者指定的結構柱類型
   ├─ Models/CadWallInfo.cs           參數與回傳資料
   ├─ Commands/                       MCP 命令：解析參數、觸發外部事件
   └─ Services/                       Revit API 實作（在 Revit 主執行緒執行）

server/src/tools/extensions/          對應的 MCP 工具定義（TypeScript）
```

## 新增其他自行開發的功能

1. 在 `src/Sinotech.Revit.Extensions/<功能>/` 新增 `Commands/XxxCommand.cs`（繼承 `ExternalEventCommandBase`）與 `Services/XxxEventHandler.cs`。
2. 在本資料夾的 `command.json` 加入命令。
3. 在 `server/src/tools/extensions/` 新增 `xxx.ts`，命名規則為 `registerXxxTool`。唯讀工具請加上 `annotations: { readOnlyHint: true }`，AI 助手就不會在執行前要求確認。
4. 每個新檔案開頭都要保留著作權標頭。
5. 重新建置：`.\scripts\build.ps1 -Revit 2025 -Install`，以及 `cd server; npm run build`。
