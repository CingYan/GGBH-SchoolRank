# 🏯 宗門菁英整編（GGBH School Rank）

鬼谷八荒宗門職位自動整編 MOD。

玩家不必是宗主；只要玩家目前有宗門，MOD 會在進入世界後與每個遊戲月份自動掃描同宗門 NPC，排除玩家本人，依照境界 / 階段、天驕狀態與戰力候選排序，重新分配宗門職位。

## ✨ 功能

- 🧭 **不限宗主觸發**：玩家是宗主、長老、真傳、內門、外門都可執行
- 🚫 **排除玩家本人**：只調整同宗門 NPC，不改玩家職位
- 🥇 **戰力排序**：境界 / 階段優先，其次參考天驕與戰力欄位
- 🏯 **自動整編職位**：大長老 → 長老 → 真傳弟子 → 內門弟子 → 外門弟子
- 🕒 **每月執行一次**：同一遊戲月份只整編一次，避免切場景或開 UI 時反覆洗職位
- 🔍 **診斷輸出**：MelonLoader console 會輸出 enum、名冊欄位與排序結果，方便校準不同遊戲版本

## 📌 預設配額

| 職位 | 配額 |
|------|------|
| 大長老 | 2 |
| 長老 | 5 |
| 真傳弟子 | 10 |
| 內門弟子 | 20 |
| 外門弟子 | 其餘 NPC |

> 配額依單一宗門實際上限設定。若後續確認分舵有獨立名冊，需另外針對分舵資料結構處理。

## 🧩 運作方式

目前版本優先改寫宗門 `buildData` 內的 NPC 名冊清單：

- `npcBigElders` / `bigElders`
- `npcElders` / `elders`
- `npcInherit` / `npcTrueDisciple`
- `npcIn` / `npcInner`
- 若遊戲版本有 `npcOut` / `npcOuter`，則會分開寫外門；沒有時會把內門後的 NPC 合併寫入 `npcIn`

如果找不到宗門名冊資料，會退回舊版 per-unit 職位探測流程，並在 log 裡輸出可用欄位供下一版修正。

### 分舵限制

目前邏輯以玩家 `schoolID` 找同宗門 NPC，並改寫玩家當前宗門的主 `buildData` 名冊。若遊戲的「分舵」使用獨立 `buildData`、獨立名冊欄位或另一組分舵 ID，目前版本不會同步改寫分舵名冊；需要先取得分舵 UI / log 裡的資料欄位後再支援。

## 🧪 目前狀態

- 版本：`v10`
- 命名空間 / AssemblyName：`MOD_SNs4Ii`
- 已建立 MelonLoader MOD 架構
- 已實作同宗門 NPC 掃描、玩家排除、排序與名冊寫入流程
- 已加入宗門 UI / buildData 資料來源探測
- 已避免強制刷新宗門 UI，降低遊戲 UI lifecycle NRE 風險

## ⚠️ 測試重點

第一次測試請打開 MelonLoader console，確認是否出現：

- `[SchoolRank v10] === Init done ===`
- `[ENUM] SchoolPostType=...`
- `[ROSTER] found schoolData ...`
- `[ROSTER] rewrite ... ok=... counts=...`
- `[RANK] done ...`
- `[TOP]` 前幾名排序是否合理

進遊戲後建議測試流程：

1. 進入有宗門的存檔
2. 等待進世界後自動整編
3. 關閉並重新打開宗門 UI，或切場景後再查看名冊
4. 確認大長老、長老、真傳、內門排序是否符合預期
5. 若結果不對，把 MelonLoader console 中 `[ROSTER]`、`[RANK]`、`[TOP]` 相關 log 貼回來

## 🔧 開發備註

- 主要程式：`ModMain.cs`
- 授權：MIT License
- 本 MOD 目前仍屬實測校準階段；不同鬼谷八荒版本可能有不同欄位名稱，因此保留大量診斷 log。
