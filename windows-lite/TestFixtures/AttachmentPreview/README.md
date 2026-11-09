# 合成縮圖測試圖

無使用者檔案、外部圖像或新執行期依賴。2026-11-09 本機使用 Pillow 12.3.0 產生固定 RGB 色塊，再由 macOS `sips -s format heic tiny.png --out tiny.heic` 產生 HEIC。

- PNG／JPEG／BMP／lossless WebP：64×48，紅色 (220,60,30)。
- GIF：64×48，兩格，第一格紅色，第二格藍色 (20,80,220)，100ms；TIFF：同尺寸兩頁，依序紅色／藍色，raw compression。
- ICO：16×16 紅色。
- HEIC：由相同 PNG 轉換，64×48。

原生測試檢查縮圖中央為紅色，以驗證 GIF／TIFF 沒選到第二格／頁；不是只測副檔名或有無 Image 元件。所有圖內嵌到 Console／Windows smoke assembly，測試時不需要 Pillow、sips 或網路。截斷、壞 header、偽副檔名、內容校驗失敗由測試從這些 bytes 衍生，不覆寫原始資源。
