# 合成縮圖測試圖

無使用者檔案、外部圖像或新執行期依賴。2026-11-09 本機使用 Pillow 12.3.0 產生固定 RGB 色塊，再由 macOS `sips -s format heic tiny.png --out tiny.heic` 產生 HEIC。

- PNG／JPEG／BMP／lossless WebP：64×48，紅色 (220,60,30)。
- GIF：64×48，兩格，第一格紅色，第二格藍色 (20,80,220)，100ms；TIFF：同尺寸兩頁，依序紅色／藍色，raw compression。
- ICO：16×16 紅色。
- HEIC：由相同 PNG 轉換，64×48。

原生測試檢查縮圖中央為紅色，以驗證 GIF／TIFF 沒選到第二格／頁；不是只測副檔名或有無 Image 元件。所有圖內嵌到 Console／Windows smoke assembly，測試時不需要 Pillow、sips 或網路。截斷、壞 header、偽副檔名、內容校驗失敗由測試從這些 bytes 衍生，不覆寫原始資源。


## r14 獨立解碼驗證（2026-11-09）

相同既有 bytes 未重新產生或替換。Mac Python 的 Pillow12.3.0／libwebp1.6.0 對 `tiny.webp` 執行 `Image.open(...); image.load()`，實際解碼成功，為 VP8L lossless、RGB64×48、中央像素(220,60,30)；44 bytes 與RIFF宣告長度44吻合。SHA-256：`8ec8c864537626bfe31cbb6269bc8c47bea7c6184086c850e97864c0247e03e6`。此為單色色塊有效壓縮，不是截斷檔，也不宣稱是全世界最小合法WebP。

可重現（由repo root執行，不寫入測試圖）：

```python
from PIL import Image, features
from pathlib import Path
p = Path("windows-lite/TestFixtures/AttachmentPreview/tiny.webp")
b = p.read_bytes()
assert b[12:16] == b"VP8L" and int.from_bytes(b[4:8], "little") + 8 == len(b) == 44
with Image.open(p) as image:
    image.load()
    assert image.format == "WEBP" and image.size == (64, 48)
    assert image.convert("RGB").getpixel((32, 24)) == (220, 60, 30)
print(features.version("webp"))
```

同次也實際解碼PNG/JPEG/GIF/TIFF/ICO/BMP；尺寸正確，GIF/TIFF各兩格／頁，第一格紅色、第二格藍色。結果與每檔SHA記於 `build/r14-fix/fixture-validation.json`。HEIC仍用原macOS sips產物；其Windows縮圖依實際WIC解碼器，不以Pillow缺外掛當作檔案損壞或Windows缺codec的證據。

同次另以 `sips -s format png tiny.heic --out <worktree>/build/r14-fix/heic-decoded.png` 經 macOS ImageIO 實際解碼HEIC，再由Pillow讀出64×48、中央(218,60,30)。轉換輸出只在build，不修改內嵌圖；不等於Windows HEIF codec已安裝或驗收。
