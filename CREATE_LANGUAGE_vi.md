# Hướng dẫn tạo Language Pack cho Plague Inc: Evolved (dùng PlagueLib)

Tài liệu này mô tả cách tạo một **gói ngôn ngữ** (hoặc chỉnh sửa gói có sẵn) cho mod
PlagueLib — không cần sửa code C#, chỉ cần tạo đúng cấu trúc thư mục + file text.
Mọi quy ước dưới đây được lấy trực tiếp từ hành vi thật của `ConfigManager.cs`,
`Main.cs`, `FontLoader.cs`, `TranslationFileLoader.cs`, `LangImages.cs`,
`AboutModScreen.cs` trong repo — không phải suy đoán.

---

## 1. Thư mục gốc chứa ngôn ngữ

Mặc định, mod quét thư mục:

```
BepInEx/config/Language/
```

Có thể đổi qua config: mở `BepInEx/config/<GUID mod>.cfg`, mục `[Language] LanguageFolder`.
Nếu điền đường dẫn **tương đối**, nó được tính từ thư mục gốc BepInEx; để trống thì dùng
mặc định ở trên.

Mỗi **ngôn ngữ** = 1 thư mục con trực tiếp bên trong `Language/`:

```
BepInEx/config/Language/
├── TiengViet/              ← 1 ngôn ngữ custom
│   ├── TiengViet.enabled   ← BẮT BUỘC, nếu không có thư mục này bị bỏ qua
│   ├── main.txt            ← file dịch chính
│   ├── ui.txt              ← có thể chia nhiều file .txt tuỳ ý
│   ├── Font/
│   └── Images/
└── AnotherLanguage/
    └── ...
```

**Quan trọng:**
- Tên thư mục **không được trùng** tên một ngôn ngữ chính thức của game (English,
  French, German...) hay bất kỳ ngôn ngữ nào game đã nạp sẵn — trùng tên sẽ bị
  **bỏ qua hoàn toàn** kèm cảnh báo trong log, để tránh ghi đè dữ liệu gốc.
- Tên thư mục chính là tên hiển thị trong dropdown chọn ngôn ngữ của game.

### File `.enabled`
Bên trong thư mục ngôn ngữ, phải có **một trong hai**:
- Một file tên đúng `<TenThuMuc>.enabled` (ví dụ `TiengViet.enabled`), hoặc
- Bất kỳ file nào có đuôi `.enabled` (mod chỉ cần tìm thấy ≥ 1 file là đủ).

Nội dung file này không quan trọng — có thể để trống. Đây chỉ là công tắc bật/tắt
nhanh một ngôn ngữ mà không cần xoá thư mục.

---

## 2. File dịch — cú pháp

### 2.1. Định dạng cơ bản

Mỗi dòng dịch có dạng:

```
"key" = "value";
```

- `key`: đúng key mà game dùng nội bộ (ví dụ `FE_MainMenu_Play`, hoặc chính câu
  tiếng Anh gốc nếu bạn dịch kiểu thay thế trực tiếp văn bản tiếng Anh).
- `value`: bản dịch của bạn.
- Dấu `;` cuối dòng là tuỳ chọn (có hay không đều được).
- Hỗ trợ **nhiều dòng** trong 1 value (xuống dòng thật trong file, không cần `\n`) —
  file được parse ở chế độ cho phép newline nằm trong cặp `"..."`.

```
"FE_MainMenu_Play" = "Chơi";
"FE_About_Mod" = "Giới thiệu Mod";
```

### 2.2. Ký tự escape được hỗ trợ

| Escape | Kết quả |
|---|---|
| `\n` | xuống dòng |
| `\t` | tab |
| `\r` | xuống dòng (tự chuẩn hoá thành `\n`) |
| `\"` | dấu `"` |
| `\\` | dấu `\` |
| `\uXXXX` | ký tự Unicode theo mã hex 4 chữ số (ví dụ `\u1ea3` = `ả`) |

### 2.3. Placeholder `{N}` — RẤT QUAN TRỌNG

Nhiều chuỗi gốc của game dùng `string.Format` với placeholder kiểu `{0}`, `{1}`...
Nếu bản dịch của bạn:
- Dùng số **{N} lớn hơn** số placeholder mà bản tiếng Anh gốc có cho đúng key đó →
  entry sẽ **tự động bị loại bỏ** (fallback về tiếng Anh) kèm cảnh báo trong log,
  để tránh crash game (`FormatException`).
- Viết brace hỏng cú pháp (`{` hoặc `}` lẻ, `{0` không đóng) → cũng bị loại tương tự.
- Bỏ mất placeholder mà bản gốc có → **không bị loại** nhưng có cảnh báo (thông tin
  sẽ thiếu khi hiển thị).

→ Nguyên tắc an toàn: **copy nguyên các `{0}`, `{1}`... từ câu tiếng Anh gốc**, chỉ
dịch phần chữ xung quanh, không tự thêm số index mới.

### 2.4. Token `[TOKEN]`

Một số chuỗi game có token dạng `[TEN_IN_HOA]` (ví dụ `[DISEASE_NAME]`) được game tự
thay bằng tay (không qua string.Format). Nếu bản dịch thiếu token này so với bản gốc,
mod chỉ **cảnh báo**, không loại bỏ — nhưng bạn nên giữ nguyên token để hiển thị đúng.

### 2.5. Placeholder đặc biệt `{VERSION}`

Viết `{VERSION}` (không phân biệt hoa thường) trong bất kỳ value nào — mod sẽ tự thay
bằng số phiên bản hiện tại của mod. Hữu ích cho màn About Mod.

### 2.6. Key trùng / value rỗng

- Key trùng (kể cả khác hoa/thường) trong cùng 1 file hoặc giữa nhiều file → entry
  nạp **sau** đè entry nạp **trước**, có cảnh báo trong log.
- Value rỗng (`"key" = "";`) → entry bị bỏ qua hoàn toàn, game tự fallback tiếng Anh.

---

## 3. Nhiều file dịch & thứ tự ưu tiên

Bạn có thể chia nhỏ bản dịch thành nhiều file trong cùng thư mục ngôn ngữ. Thứ tự ưu
tiên khi trùng key (file nạp **sau** đè file nạp **trước**):

```
.json  (nạp đầu tiên — ưu tiên thấp nhất)
.csv
.lang
.txt   (nạp cuối cùng — ưu tiên cao nhất)
```

Các file cùng đuôi được sắp xếp theo tên (bảng chữ cái) trước khi nạp. Có thể đổi
danh sách đuôi/thứ tự qua config `[Language] LanguageFileExtensions`.

> Lưu ý: dù tài liệu nội bộ nói "hỗ trợ .json", parser **không** đọc JSON chuẩn
> (`"key": "value"`) — nó chỉ hiểu đúng cú pháp `"key" = "value";` ở mục 2.1, bất kể
> đuôi file là gì. Thực tế hầu như luôn dùng `.txt`.

---

## 4. Font

Thư mục con `Font/` bên trong thư mục ngôn ngữ. Có **2 cách**, mod tự ưu tiên AssetBundle
nếu có, không thì dùng font `.ttf`/`.otf` trực tiếp:

### Cách A — Font rời (.ttf/.otf), khuyên dùng vì đơn giản nhất

```
TiengViet/Font/
├── MyFont-Bold.ttf
├── MyFont-Medium.ttf
├── MyFont-Light.ttf
└── fonts.txt          ← tự động sinh ra nếu chưa có / lệch với thực tế
```

Đặt file `.ttf`/`.otf` vào thư mục — mod **tự nhận diện** Bold/Medium/Light qua tên
file (chứa `Bd`/`Bold`, `Md`/`Medium`, `Lt`/`Light`...) và **tự sinh `fonts.txt`**.
Bạn có thể sửa tay `fonts.txt` nếu Unity nhận sai tên font hệ điều hành:

```
# gameKey = fileName | osFontName
Bd = MyFont-Bold.ttf | My Font Bold
Md = MyFont-Medium.ttf | My Font Medium
Lt = MyFont-Light.ttf | My Font Light
```

Nếu bạn đổi file font (thêm/xoá/sửa), **xoá `fonts.txt` cũ** để mod sinh lại — mod có
tự phát hiện lệch (so file hiện tại với manifest) nhưng để chắc ăn thì xoá tay vẫn an
toàn hơn.

### Cách B — AssetBundle (`fonts.json` + bundle Unity)

Dùng khi bạn đã có sẵn AssetBundle chứa `Font` object của Unity:

```
TiengViet/Font/
├── plaguefonts       ← file AssetBundle (tên tuỳ ý, khai trong fonts.json)
└── fonts.json
```

```json
{
  "bundle": "plaguefonts",
  "Bd": "MyFont_Bold",
  "Md": "MyFont_Medium",
  "Lt": "MyFont_Light"
}
```

Cách này phức tạp hơn (cần build AssetBundle bằng Unity Editor) — chỉ dùng nếu font
`.ttf` trực tiếp gặp vấn đề hiển thị.

---

## 5. Thay ảnh (Images)

Thư mục con `Images/` — bỏ `.png`/`.jpg`/`.jpeg` vào đây (có thể để trong thư mục con
lồng nhau, mod quét đệ quy), **đặt tên file trùng với tên texture gốc** mà game dùng
(không phân biệt hoa/thường, không cần đuôi):

```
TiengViet/Images/
├── AboutMod.png          ← avatar cho màn About Mod (xem mục 6)
└── HUD/
    └── SomeOriginalTextureName.png
```

Nếu trùng tên nhưng khác đuôi (ví dụ cả `.jpg` và `.png`), `.png` được ưu tiên.

---

## 6. Màn "About Mod"

Override tiêu đề + mô tả bằng 2 key trong file dịch:

```
"FE_About_Mod" = "Dịch 2000s - Tiếng Việt";
"FE_About_Mod_Desc" = "Bản dịch tiếng Việt cho Plague Inc.\n\nNhóm dịch: ...";
```

Avatar hiển thị: `Images/AboutMod.png` (tên file đổi được qua config
`[About] AboutAvatarFileName`).

---

## 7. Dịch kịch bản (Scenario)

Thư mục con `scenarios/` bên trong thư mục ngôn ngữ:

```
TiengViet/scenarios/
└── <scenarioId>.strings.txt    (hoặc <scenarioId>.txt)
```

`<scenarioId>` phải khớp đúng ID nội bộ game dùng cho kịch bản đó (xem trong file
kịch bản gốc của game/Workshop). Cú pháp bên trong file giống hệt mục 2.1.
Đổi tên thư mục/đuôi file qua config `[Language] ScenarioFolderName` /
`ScenarioFileSuffixes`.

---

## 8. Checklist nhanh — tạo 1 language pack tối thiểu

1. Tạo `BepInEx/config/Language/<TenNgonNgu>/`
2. Tạo file rỗng `<TenNgonNgu>.enabled` bên trong
3. Tạo `main.txt` với vài dòng `"key" = "value";` để test
4. (Tuỳ chọn) Thêm `Font/` với vài file `.ttf`
5. (Tuỳ chọn) Thêm `Images/AboutMod.png`
6. Khởi động game, mở log BepInEx (`LogOutput.log`) để xem cảnh báo/lỗi — mọi entry
   bị loại hoặc file lỗi đều được log rõ ràng kèm lý do.
7. Vào Options trong game, chọn ngôn ngữ của bạn trong dropdown.

---

## 9. Lỗi thường gặp

| Triệu chứng | Nguyên nhân thường gặp |
|---|---|
| Ngôn ngữ không xuất hiện trong dropdown | Thiếu file `.enabled`, hoặc tên thư mục trùng ngôn ngữ official |
| Game crash khi vào 1 màn hình cụ thể | Bản dịch dùng `{N}` vượt số placeholder gốc — kiểm tra log, mod thường đã tự loại entry này trước khi crash xảy ra, nhưng kiểm tra lại nếu vẫn crash |
| Chữ hiện ký tự lỗi `□` | Font không hỗ trợ ký tự đó — đổi font hoặc kiểm tra `fonts.txt` map đúng osFontName |
| Text hiện nguyên key thô (`FE_Options_xxx`) | Value trong file dịch bị rỗng |
| Ảnh không đổi | Sai tên file trong `Images/` (phải khớp tên texture gốc) |
| Font cũ vẫn hiện sau khi đổi file `.ttf` | Xoá `fonts.txt` cũ để mod sinh lại manifest |

---

*Tài liệu này mô tả hành vi của mod tại thời điểm viết — nếu `ConfigManager.cs` có
thay đổi tên config/đường dẫn mặc định, hãy đối chiếu lại file đó.*
