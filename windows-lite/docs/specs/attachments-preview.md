# 圖片縮圖（D-35）

範圍：只擴充聊天附件及本機附件草稿的靜態縮圖。沒有點圖放大、開啟或複製圖片的新入口；原有另存流程保留。不改資料庫、wire 格式或附件傳輸狀態。

## AP-01 支援格式、只取第一格

- Given 已完成並校驗的 PNG、JPEG、BMP、GIF、WebP、TIFF 或 HEIC／HEIF，When 本機 WIC 有對應解碼器，Then 在卡片顯示靜態縮圖。
- Given GIF 多格或 TIFF 多頁，When 建立縮圖，Then 只使用解碼器的第一格／第一頁，不播放動畫、不解碼後續格。
- WebP 與 HEIC／HEIF 的實際可用性取決於這台 Windows 安裝的 WIC 解碼器；缺少時依 AP-04 顯示提示。
- **D-36：ICO 不支援預覽。** Given 內容辨識為 ICO，When 請求縮圖，Then 回明確 `NotSupported`，不查詢產品解碼器、不嘗試像素解碼，顯示固定「此圖片格式不提供預覽（可另存後開啟）」。即使 ICO 內嵌 PNG 也一樣；檔案仍可依原流程另存。

## AP-02 依內容辨識

- Given 圖片副檔名不正確，When 前最多 64 KiB 的 bounded header 可辨識為支援格式，Then 仍可進入解碼；副檔名不決定解碼資格。
- Given PDF／HTML 偽裝成 .jpg、截斷的格式標頭或不合理的容器長度，When 檢查內容，Then 不交給 WIC。標頭通過但壓縮內容損壞時，由 WIC 拒絕並依 AP-04 提示。
- 格式探測不宣稱完整驗證壓縮資料；解碼器仍須驗證第一格。副檔名僅協助決定未知內容是否應顯示「無法預覽圖片」提示。

## AP-03 資源上限與背景工作

- Given 檔案超過 25 MiB，When 請求預覽，Then 不解碼。實際檔案長度與 metadata 都要檢查。
- Given 尺寸不符合既有 `ValidDimensions`（寬高各 1～16384，且乘積不超過 32,000,000），When header 已知尺寸或解碼器讀出第一格尺寸，Then 在取像素前拒絕。此上限不放寬。
- Given 合格圖片，When 校驗、讀 header、WIC 解碼及縮放，Then 全在背景執行，單次只容許一項解碼；只把最多 320×200 的獨立凍結 bitmap 交回 UI。卡片顯示上限仍為 300×200。
- Given 卡片卸載、重建或草稿切換，When 舊工作完成，Then 不得把結果寫到新的卡片／草稿。

## AP-04 無解碼器及失敗提示

- Given WIC 明確回報無解碼器，When 該格式不能預覽，Then 卡片顯示「這台電腦無法預覽此圖片格式（可另存後用看圖程式開啟）」。HEIC／HEIF 改提示「需要 Windows 的 HEIF 影像延伸模組」。
- Given 校驗失敗、格式損壞、讀取或解碼失敗，When 建立預覽，Then 使用固定小字說明，不顯示例外原文、不使 App 崩潰。大小及尺寸超限另用固定提示。
- r14 PM 要求：支援的格式先以 WIC `CreateDecoder(ContainerFormat)` 查詢實際註冊／可建立的解碼器，不用 OS 版本、圖片副檔名或 decode exception 猜測。盤點記錄 ContainerFormat、decoder CLSID、狀態與固定 HRESULT。D-36 後 smoke 保留 ICO 的獨立環境盤點作資訊，產品預覽不使用它。
- Given 該格式已註冊，When native smoke 解碼合成圖，Then 必須 Ready 並驗證第一格紅色像素；一般解碼錯誤不能冒充缺解碼器而通過。
- Given 該格式明確未註冊，When 產品預覽，Then 狀態必須為 DecoderUnavailable、實際像素解碼不啟動、提示可見。
- r15 PM 裁決：`0x88982F8B`（SDK `winerror.h` 的 `WINCODEC_ERR_COMPONENTINITIALIZEFAILURE`）單列為 `InitializeFailed`，表示元件初始化失敗、本機不可用；產品回 DecoderUnavailable、不嘗試像素解碼且顯示提示。observations 的 registration 必須為 InitializeFailed，保留原 HRESULT；不可寫成 NotRegistered 或虛構可用 CLSID。
- 其他未知 HRESULT（包括相鄰的 INVALIDREGISTRATION 0x88982F8A、INSUFFICIENTBUFFER 0x88982F8C）仍是 Unknown，smoke 必須失敗。PNG／JPEG／GIF／TIFF／BMP 在原生 smoke 必須 Ready 與第一格像素正確；WebP／HEIF 依盤點走 Ready 或明確不可用提示。ICO 依 D-36 走 NotSupported／固定提示／未解碼斷言，保留截圖，不算縮圖成功、不跳過。

## AP-05 完成與校驗邊界、草稿

- Given 接收附件尚非 `complete` 或沒有 AttachmentPath，When 卡片呈現，Then 不讀檔、不校驗、不解碼。
- Given 已完成附件，When 預覽，Then 從同一個拒絕寫入的檔案 handle 重新核對長度及 SHA-256，再辨識及解碼；校驗不符不得解碼。暫存清理導致檔案消失時顯示提示，傳輸資料狀態不受縮圖結果影響。
- Given 傳送端已暫存並有 SHA-256 的本機附件草稿，When 顯示草稿預覽，Then 沿用相同內容、大小、尺寸、校驗、背景與提示規則；這不代表對方已收到附件。

## 證據與界線

- 共用層：內嵌合成圖的格式／尺寸探測、假副檔名、截斷、超大 header、狀態與路徑 gate。GIF／TIFF 第二格刻意不同色。
- 原生 contacts smoke：真實 vault 暫存、真實 WPF 卡片與 sender draft；第一格像素、離開 UI 執行緒、校驗失敗不解碼、未完成不解碼、解碼失敗不崩潰。截圖 `attachment-preview-*.png`，逐格式結果 `attachment-preview-observations.json`，以及先於逐圖測試保存的完整八格式 `attachment-decoder-inventory.json`。
- Native fixture 透過 store 將合成接收紀錄設為完成，之後預覽自己重新計算 SHA；不宣稱這一段測到網路下載。附件原有 TCP／SQLite suite 獨立驗證傳輸。
- Mac 只能跑共用層與 Windows 交叉建置；原生 WPF 截圖、實體 Windows／教室驗收仍未完成，須以後續 runner／現場結果為準。

參考：[Microsoft WIC 與可擴充解碼器](https://learn.microsoft.com/en-us/windows/win32/wic/-wic-about-windows-imaging-codec)、[WIC 錯誤碼](https://learn.microsoft.com/en-us/windows/win32/wic/-wic-codec-error-codes)、[WebP 容器格式](https://developers.google.com/speed/webp/docs/riff_container)、[WIC CreateDecoder 查詢](https://learn.microsoft.com/en-us/windows/win32/api/wincodec/nf-wincodec-iwicimagingfactory-createdecoder)、[Microsoft Windows SDK GUID／介面宣告](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/wincodec.h)。
