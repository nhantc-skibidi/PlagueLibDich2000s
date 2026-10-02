# Changelog — PlagueLibDich2000s

Bản ghi nhận các thay đổi giữa phiên bản.

---

## [Build 5 — 2026-08-31] Zero-Hardcode + ConfigManager + fix dropdown về ngôn ngữ gốc (0.29.5-build5)

Vòng fix thứ 5 theo yêu cầu: **sửa dứt điểm lỗi dropdown ngôn ngữ bị trống khi
chuyển về ngôn ngữ gốc (Vanilla)**, **bóc tách 100% tham số hardcode ra BepInEx
Config** (`ConfigManager.cs` — file mới), và **bảo chứng pass-through cho luồng
vanilla** (mọi Harmony Prefix hoặc là `void`, hoặc luôn `return true`; hai patch
`RewardController` duy nhất từng chặn hàm gốc đã chuyển sang Postfix). Các fix
đánh mã `CFG-01…CFG-11` ngay trong code.

### Nghiêm trọng (CRITICAL) — dropdown trống
- **CFG-01** `Main.cs ActiveLanguage_Save_Patch` (patch `set_ActiveLanguage`):
  thân postfix **không có try/catch** — exception từ font teardown văng thẳng
  vào caller của setter trong game (options screen đang rebuild dropdown) →
  dropdown trống khi chuyển về ngôn ngữ gốc. Ngoài ra postfix còn `return` sớm
  khi `LastLanguage == null` → **không restore font** nếu config hỏng. Fix: bọc
  toàn thân try/catch (nuốt mọi exception + fail-safe `FontsReady=false`), bỏ
  return sớm, tách từng bước I/O (LoadFromLangFolder/Unload/caption) try/catch riêng.
- **CFG-02** `Main.cs` (3 đường teardown `TryLoadCustomFonts` ×2 nhánh + else-branch
  của `set_ActiveLanguage`): `DestroyCustomFonts()` chạy **TRƯỚC** khi
  `FontsReady=false` — nếu destroy throw giữa chừng, patch `UIFont.get_dynamicFont`
  vẫn trả Font **đã bị Destroy** cho mọi UILabel (label + item dropdown render
  rỗng). Fix: đặt `FontsReady = false` TRƯỚC mọi teardown.
- **CFG-03** `Main.cs LoadAllLanguages`: folder pack đặt tên trùng ngôn ngữ
  official (vd "Vietnamese") sẽ **đè** `mpLocalisedTexts` + `AddLanguage` trùng
  tên → hỏng danh sách ngôn ngữ gốc (nguồn dropdown). Fix: snapshot danh sách
  game đã load + đối chiếu `ConfigManager.OfficialLanguages`; chỉ `AddLanguage`
  lần đầu tên xuất hiện.
- **CFG-04** `Main.cs TryLoadCustomFonts` + `BindActiveLanguagePack`: chạy BÊN
  TRONG setter `set_ActiveLanguage` của game khi đổi ngôn ngữ in-game —
  exception làm đứt luồng game. Fix: bọc toàn thân + fail-safe.

### Zero-Hardcode Policy (CFG-05…CFG-10)
- **CFG-08** Tạo **`ConfigManager.cs`** (file mới, ~35 `ConfigEntry`): Language
  folder, OfficialLanguages, LanguageFileExtensions, FeKeyPrefix, Scenario
  folder/suffixes, 6 delay boot/poll, Font folder/manifest/bundle, 3 bộ weight
  keyword, CriticalSystemFonts, RegisterPrivateFonts, ImagesFolderName, 4 key
  HUD, About fallback title/description/avatar — tất cả cấu hình được, sửa nóng
  qua `SettingChanged`, bảo toàn 3 entry config cũ (`General.*`).
- **CFG-09** `AutotranslateText_Prefix`: bỏ 2 key hardcode "Outbreak"/"Disease"
  — tra data-driven qua dict.
- **CFG-10** Xoá patch `RewardController.names` (Prefix `return false` + mảng
  3 chuỗi English hardcode) → getter gốc chạy nguyên vẹn; `GetRewardBarMenuTitle`
  chuyển Prefix→Postfix dịch data-driven qua `LocalizeHardcodedUi`.
- `IsCustomLanguageFolder`: bỏ mảng 30 ngôn ngữ hardcode → config + cache
  Directory.Exists; `IsCustomLanguageActive`: bỏ so sánh cứng "English", bỏ IO
  khỏi hot path; `OnDestroy`: chỉ ghi `LastLanguage` khi thật sự là pack custom.
- `AboutModScreen`: DefaultTitle/DefaultDesc/AvatarTargetWidth/"Images/AboutMod.png"
  → ConfigManager; `LangImages`: "Images" → config; `FontRestore.DetectWeight`:
  keyword weight → config.

### Ổn định / Thread (CFG-05, CFG-11)
- **CFG-05** cache kết quả `IsCustomLanguageFolder` (tránh IO lặp trong Harmony
  postfix) + lock bảo vệ.
- **CFG-11** `_fileCache` đọc/ghi dưới lock (phòng đường reload từ thread khác);
  toàn bộ load vẫn chạy main thread.

**File đổi so với build 4:** `ConfigManager.cs` (mới) + `Main.cs` +
`FontRestore.cs` + `AboutModScreen.cs` + `LangImages.cs` + `Version.cs` +
`CHANGELOG.md`. Phiên bản: `0.29.5-build5` (BepInEx 5 lưu config theo GUID —
đổi version không mất config người dùng).

---

## [Code Review 2026-08-31] — Bộ fix rà soát toàn diện (áp dụng lên source 0.29.5)

Rà soát độc lập toàn bộ mã nguồn (5 file .cs + build config + docs). Tổng cộng **19 phát hiện**
được đánh mã CR-01…CR-19; các fix có đánh dấu `// FIX CR-xx` ngay tại chỗ sửa trong code.
**Version giữ nguyên `0.29.5`** (không đổi `VersionString` — tránh mất config `LastLanguage`
của người dùng theo cơ chế `GUID.Version` của BepInEx).

### Nghiêm trọng (CRITICAL)
- **CR-02** `FontRestore.cs` + `Main.cs`: trạng thái font **mức UIFont** (`mDynamicFont`,
  `mReplacement`) bị mod ghi đè trực tiếp lên các UIFont *dùng chung* của game nhưng
  **không bao giờ được khôi phục** khi tắt custom language — `DestroyCustomFonts()` Destroy()
  Font/proxy thay thế để lại **dangling reference** → sau khi đổi về English, các UIFont vốn
  dynamic bị coi là bitmap (isDynamic=false) → text vỡ/hộp đen. Fix: thêm
  `FontRestore.CaptureUIFont()` / `RestoreUIFonts()`, gọi `RestoreUIFonts()` **trước**
  `DestroyCustomFonts()` ở cả 3 đường teardown; mọi chỗ ghi `mDynamicFont` giờ đều capture font gốc.
- **CR-03** `Main.cs`: **race condition** giữa `Invoke(LoadAllLanguages, 2.0s)` và
  `WaitForGameUiThenReload → ForceCustomLanguage`. Nếu game set `ActiveLanguage` rất sớm
  (máy nhanh), ForceCustomLanguage bind font xong thì `LoadAllLanguages` lại wipe
  `FontBd/Md/Lt` + `FontsReady=false` → UILabel spawn sau đó mất font tiếng Việt.
  Fix: thêm cờ `PlagueVnMod.LanguagesLoaded`; coroutine chờ đủ cả 2 điều kiện;
  ForceCustomLanguage từ chối chạy khi chưa load xong.
- **CR-05** `Main.cs Unescape()`: thứ tự `Replace` sai (xử lý `\\n` trước `\\\\`) làm
  **unescape sai chuỗi chứa backslash** trong file dịch. Viết lại single-pass.

### Cao (HIGH)
- **CR-04** `Main.cs` (UILabel OnEnable Prefix): font gán vào `mDynamicFont` của UIFont
  **dùng chung** được chọn theo weight của *label đang enable* → "last-write-wins",
  label weight Lt có thể bị ép hiển thị weight Bd. Fix: dùng `FindFontForUIFont(ui)`
  (theo tên UIFont — nhất quán với proxy mechanism).
- **CR-06** `LangImages.cs`: patch `Material.set_mainTexture` chặn **mọi** material của game
  (kể cả material ngoài UI), nhưng `RestoreUi()` chỉ quét UIAtlas + UITexture → các material
  khác bị thay texture **không bao giờ được restore**. Fix: bảng theo dõi `ReplacedMaterials`
  + `RememberReplacedMaterial()` + `RestoreReplacedMaterials()` (chạy trong `finally` của RestoreUi).
- **CR-07** `Main.cs LoadLanguageFromFolder()`: doc comment khai báo hỗ trợ `.json` nhưng
  `supportedExts` thiếu `".json"` → file dịch `.json` bị bỏ qua im lặng. Đã bổ sung
  (best-effort, parse vẫn theo regex `"key" = "value"`).
- **CR-08** `Main.cs`: `ScanGameFontsDelayed()` **không có nơi nào gọi** (dead code) —
  chức năng scan/log toàn bộ font trong game không bao giờ chạy. Đã wire
  `Invoke(nameof(ScanGameFontsDelayed), 6.5f)` trong `Awake()`.

### Trung bình (MEDIUM)
- **CR-09** `Main.cs RefreshAllLabels()`: nhánh `else` gọi `OriginalTextCache.Add()` cả khi
  `TryGetValue` trả true nhưng text rỗng → `ArgumentException` (key tồn tại) bị nuốt bởi
  `catch {}` → cache không được ghi. Fix logic tường minh (Remove + Add khi cache rỗng).
- **CR-10** `Main.cs RefreshAllLabels()`: nhánh EnglishToCustomDict thiếu check
  "bằng text hiện tại" (2 nhánh kia có) → `set_text` trùng giá trị gây mark-dirty/
  rebuild draw call không cần thiết. Đã bổ sung check.
- **CR-11** Nhiều chỗ `ToLower()/ToUpper()` **phụ thuộc culture** (bug Turkish-I: "I"→"ı")
  trong `AddLanguage`, `InjectFallbackIntoAllLanguages`, `BuildEnglishToCustomDict`,
  `TryGetTranslation`. Chuyển sang `ToLowerInvariant()/ToUpperInvariant()`; bỏ các lookup
  `key.ToLower()` thừa (dict đã `OrdinalIgnoreCase`).
- **CR-12** `Main.cs RewardController_GetRewardBarMenuTitle_Patch`: truy cập
  `RewardController.names[index]` **không kiểm tra biên** → `IndexOutOfRangeException`
  văng vào code game khi index ngoài [0..2]. Đã thêm bounds check + fallback về logic gốc.
- **CR-14** `AboutModScreen.cs EnsureScreenInner()`: `AccessTools.Field(...)` có thể trả
  `null` (field `subScreenLookup` không khai báo trên `IGameScreen`) → `.GetValue()` trên
  FieldInfo null ném NRE làm **hỏng toàn bộ màn hình About Mod**. Đã null-check an toàn.

### Thấp (LOW) / vệ sinh repo
- **CR-13** Xoá 2 FieldInfo chết `FiMFont`/`FiMTtf` trong `PlagueVnMod` (patch class có bản copy riêng).
- **CR-15** `AboutModScreen.cs`: xoá toán tử chết `h * 0f` trong tính vị trí nút About.
- **CR-16** **Xung đột version giữa các file**: `CHANGELOG.md` ghi `1.9.4`/`1.9.3` trong khi
  `Version.cs` = `"0.29.5"` (comment cũ còn ghi "BETA-v0.27.0"). Không đổi giá trị — chỉ
  ghi chú sự lệch tại `Version.cs` để thống nhất ở release kế tiếp.
- **CR-17** `Directory.Build.props`: xoá 2 dòng gán property về chính nó (no-op, gây hiểu lầm).
- **CR-18** Gate log `[Localizer] HARD APPLY` bằng `VerboseLogging` (tránh alloc trong hot path).
- **CR-19** **File thiếu**: `SETUP.md` tham chiếu `.gitignore` và
  `Directory.Build.props.user.example` nhưng cả 2 không tồn tại trong repo → đã tạo.
  Đồng thời loại `bin/`, `obj/` (build artifacts) khỏi bản phân phối source.

### Ghi chú tương thích
- Không đổi GUID, không đổi `VersionString`, không đổi schema config — người dùng cuối
  không cần làm gì khác ngoài build lại DLL.

### Build fix sau build thật trên Windows — lỗi CS1061
- `FontRestore.cs:227` — `OrigUiDyn.Clear()` trên `ConditionalWeakTable<UIFont, Font>`
  **không tồn tại trên .NET Standard 2.0** (API chỉ có từ .NET Core 3.0 / .NET Standard
  2.1) → `error CS1061` khi `dotnet build PlagueLibDich2000s.slnx`. Đã thay bằng
  `OrigUiDyn.Remove(ui)` gọi ngay trong vòng lặp restore của `RestoreUIFonts()` sau khi
  khôi phục từng UIFont: `Remove(key)` có trên mọi phiên bản .NET, xử lý đúng từng entry
  (entry nào restore lỗi sẽ được giữ lại và retry ở lần sau — an toàn hơn Clear() toàn
  bảng), còn entry của UIFont đã bị destroy thì ConditionalWeakTable tự dọn theo GC vì
  key là weak reference. Các lời gọi `.Clear()` khác trong dự án đều thuộc
  `Dictionary`/`HashSet`/`List` — hợp lệ trên netstandard2.0, không đổi.

### Language Data Hardening — rà soát vòng 3 (encoding / placeholder / parse)
Tiêu chí: **tương thích tuyệt đối — ổn định cao — không gây crash game**. Trọng tâm
là toàn bộ đường parse dữ liệu ngôn ngữ. Các fix đánh mã `LANG-xx`, có comment
`// FIX LANG-xx` ngay tại chỗ sửa:

- **LANG-01 (CRITICAL)** `Main.cs LoadTranslationFileStatic`: value chứa brace hỏng
  cú pháp composite-format (`{0` không đóng, `}` đơn, `{text}`) — game gọi
  `string.Format(GetText(...), args)` cho hàng loạt chuỗi → `FormatException` ném
  thẳng trong code game (không try/catch) → **crash toàn game**. Giờ mọi value có
  `{`/`}` được dry-run qua `string.Format` (64 arg mồi); entry hỏng bị BỎ (fallback
  English an toàn) + cảnh báo kèm **số dòng**.
- **LANG-02 (CRITICAL)** placeholder `{N}` của bản dịch vượt index cao nhất của
  chuỗi English gốc → game format với số arg theo English → `FormatException` →
  crash. Thêm `ValidatePlaceholdersAgainstEnglish()` chạy sau load: đối chiếu
  MỌI ngôn ngữ custom với `mpLocalisedTexts["English"]`, loại entry vượt chỉ số
  (log rõ key để tác giả sửa). File scenario (key = chuỗi English gốc) được đối
  chiếu key↔value ngay lúc parse. Cảnh báo thêm (không loại) khi: bản dịch bỏ mất
  placeholder gốc, lệch số lượng `%s/%d`, thiếu `[TOKEN]` hoa kiểu `[NAME]`.
- **LANG-03 (HIGH)** value multi-line chứa `\r\n`/`\r` literal (regex Singleline
  cho phép newline trong chuỗi) — NGUI chỉ render `\n`, `\r` thành ký tự lỗi (□).
  `Unescape()` giờ chuẩn hoá CRLF/CR → LF và escape `\r` → `\n`.
- **LANG-04 (MEDIUM)** hỗ trợ escape `\uXXXX` (JSON-style, surrogate pair) — công
  cụ xuất file dịch hay sinh escape này; trước đây hiển thị literal `\u1ea3`.
- **LANG-05 (MEDIUM)** `{VERSION}` được detect KHÔNG phân biệt hoa thường nhưng
  `string.Replace` thì CÓ — `{version}` được phát hiện mà không được thay → hiện
  literal. Thay bằng `ReplaceIgnoreCase()`.
- **LANG-06 (LOW)** key trùng (kể cả khác hoa/thường, dict là OrdinalIgnoreCase)
  bị đè im lặng — giờ cảnh báo kèm số dòng; value rỗng (LANG-11) được đếm và
  tổng hợp thành 1 dòng cảnh báo cho tác giả file dịch.
- **LANG-07 (MEDIUM)** `TryReadFontFamily`: `Encoding.GetEncoding(1252)` nằm trực
  tiếp trong vòng lặp record — runtime thiếu codepage ném `NotSupportedException`
  làm MẤT family name của mọi record sau (kể cả Windows UTF-16 hợp lệ). Giờ:
  đăng ký `CodePagesEncodingProvider` qua reflection (no-op nếu không có), cache
  encoding + per-record try/catch + fallback decode Latin-1; thêm guard tràn số
  nguyên cho offset/length đọc từ font hỏng.
- **LANG-08 (MEDIUM)** `LoadAllLanguages`: exception từ 1 folder ngôn ngữ (access
  denied khi dò `.enabled`, file bị khoá...) văng lên outer try → ABORT toàn bộ,
  các folder sau không load (mất hẳn các ngôn ngữ khác). Giờ mỗi folder/file được
  bọc try/catch riêng.
- **LANG-09 (LOW)** `ReadJsonString` (fonts.json): không ý thức escape — value
  chứa `\"` bị cắt sai, `\u00e9` trả nguyên literal. Viết lại scan có skip escape
  + unescape `\" \\ \/ \n \t \r \uXXXX`.
- **LANG-12 (LOW)** `LoadLanguageFromFolder`: doc nói ưu tiên `.txt` cao nhất
  nhưng thứ tự nạp khiến `.json` thực tế ĐÈ `.txt` (ngược doc). Đảo thứ tự nạp
  để `.txt` nạp cuối cùng (ưu tiên thật sự).
- **LANG-10 (INFO — đã verify, không cần sửa)**: `File.ReadAllText` /
  `ReadAllLines(path, Encoding.UTF8)` tự nhận diện + strip BOM (UTF-8,
  UTF-16 LE/BE) — file dịch lưu từ Notepad/VS/Sublime đều đọc đúng; BOM không
  lọt vào key đầu tiên. key rỗng đã được skip từ vòng 2; chuỗi thiếu
  (missing string) fallback về English qua TryGetTranslation — không crash.

### Vòng 4 — Xung đột text-key ↔ FE-key (Single Player / Options) + phân giải theo key gốc
Phát hiện từ build thật 0.31.0: file dịch chứa entry text-key trùng English value
của key FE_* — `"Single Player" = "1 Người"` (kịch bản Board Game) trong khi menu
chính dùng `"FE_Single_Player" = "Chơi đơn"`; tương tự `"FE_Options" = "Cài Đặt"`
/ `"Options" = "Tùy chọn"`.

- **LANG-13 (HIGH — vấn đề chính)** `Main.cs RefreshAllLabels()`: tra thuần text
  KHÔNG THỂ phân biệt "Single Player" của menu (cần "Chơi đơn") và của Board Game
  (cần "1 Người") — 2 entry cùng khớp 1 chuỗi, tag nào thắng do thứ tự ưu tiên
  quyết định, luôn sai 1 phía. Thêm **ưu tiên 0**: đọc KEY GỐC của label từ
  component `UILabelAutotranslate` (reflection thuần + cache FieldInfo, không phụ
  thuộc chữ ký method — log thật cho thấy SetText/SetInitialText/SetTextLegacy
  (string) đều không tồn tại) rồi tra key đó. Key FE_* thắng tuyệt đối (UI chính
  luôn đúng bản FE); key text chỉ được tin khi chuỗi đó KHÔNG trùng English value
  của key FE_* nào (nếu trùng, ưu tiên 2 lo — tránh lấy nhầm bản kịch bản cho
  label UI chỉ lưu text English). Mọi lỗi reflection → trả null → fallback nguyên
  chuỗi ưu tiên 1-3 cũ — không bao giờ tệ hơn trước. Trace (VerboseLogging) hiển
  thị thêm nguồn `AutotranslateKey`.
- **LANG-14 (HIGH)** `Main.cs BindActiveLanguagePack()`: `EnglishToCustomDict`
  (reverse lookup) KHÔNG được rebuild khi đổi ngôn ngữ custom IN-GAME — chỉ được
  build lúc boot (LoadAllLanguages / ForceCustomLanguage) → sau khi chuyển pack
  trong game, ưu tiên 2 của RefreshAllLabels tra ra bản dịch của ngôn ngữ CŨ.
  Fix: chuyển `BuildEnglishToCustomDict()` sang static + gọi cuối
  BindActiveLanguagePack.
- **LANG-15 (MEDIUM)** `Main.cs LoadAllLanguages()`: MỌI folder .enabled đều
  merge vào TranslationDict/EnglishTextDict theo thứ tự filesystem → folder nạp
  sau ĐÈ folder nạp trước, kết quả phụ thuộc TÊN folder. VD từ log thật: pack
  English identity ("ai aiii aiiii") + "Tiếng Việt" cùng bật — nếu pack identity
  nạp sau, entry English đè mất bản dịch (hiện không xảy ra chỉ vì NTFS sort
  "ai..." trước "Tiếng Việt"). Fix: chọn pack ưu tiên theo config `LastLanguage`
  (fallback firstEnabled) và CHỈ merge pack được chọn; các pack khác vẫn đăng ký
  `mpLocalisedTexts` để chuyển sau.
- **LANG-16 (diagnostic)** `Main.cs BuildEnglishToCustomDict()`: thêm báo cáo —
  log cảnh báo cho MỖI text có 2 bản dịch khác nhau (FE vs text-key), kèm giải
  thích cơ chế ưu tiên ngay trong log để tác giả file dịch biết rõ entry nào áp
  dụng ở đâu.
- **LANG-17 (LOW)** `Main.cs TryRegisterCodePages()`: `AccessTools.TypeByName`
  log Warning HarmonyX "Could not find type System.Text.CodePagesEncodingProvider"
  mỗi lần khởi động (máy không có assembly CodePages — mặc định Unity Mono). Đổi
  sang `Type.GetType` (null im lặng) — hành vi no-op giữ nguyên, log sạch.
- **LANG-18 (LOW)** `Main.cs TryPatchAutotranslate()`: 3 lần `AccessTools.Method`
  đoán mò tên (SetText/SetInitialText/SetTextLegacy) → 3 dòng Warning HarmonyX +
  hook chết. Đổi sang `Type.GetMethod` im lặng; khi không khớp, log 1 dòng liệt
  kê method (tên + số tham số) + field string THẬT của UILabelAutotranslate
  (helper `DescribeTypeMembers`) để lần sửa sau patch đúng chỗ thay vì đoán tiếp.
- **LANG-19 (LOW)** `Main.cs RefreshAllLabels()`: đếm "not in dict" gộp cả label
  ĐÃ dịch (text hiện tại là value tiếng Việt) → con số gây hiểu sai (log thật:
  "7994 not in dict" trong khi phần lớn đã dịch). Tách counter
  by-key/already/not-in-dict + dựng viValues 1 lần trước vòng lặp (bổ sung
  ScenarioDict vào tập value).
- **LANG-20 (doc)** Doc comment của `RefreshAllLabels` ghi ngược thứ tự ưu tiên
  2/3 so với hành vi thật (từ thời FIX v0.27.2) — đã sửa lại cho khớp code và mô
  tả thêm ưu tiên 0.

**Hướng dẫn file Language cho cặp entry xung đột** (giữ cả 2 entry — không cần
sửa file):
- Menu chính đọc theo key FE_* → hiển thị "Chơi đơn" / "Cài Đặt" (bản FE luôn
  thắng cho UI chính — kể cả trường hợp reverse lookup hụt, ưu tiên 0 đọc thẳng
  key gốc của label).
- Label kịch bản Board Game được game tra theo đúng key text → hiển thị
  "1 Người" / "Tùy chọn".
- Muốn đổi từ hiển thị: chỉ cần sửa value của entry tương ứng; KHÔNG xoá entry
  FE_* (menu sẽ mất dịch). Log khởi động giờ in danh sách "Xung đột 2 nguồn
  dịch" để đối chiếu.

---

## [1.9.4] — 29/08/2026 — Security & Stability Hardening

Bản vá dựa trên báo cáo audit toàn diện (42 phát hiện: 3 Critical, 9 High, 18 Medium, 12 Low).

### Quan trọng — Single source of truth cho version
- Toàn bộ version hiện được khai báo trong **`PlagueLibDich2000s/Version.cs`** (file mới).
- `Main.cs` (`BepInPlugin` attribute) + `AboutModScreen.cs` (About dialog) đều reference từ file này.
- Khi release mới, **chỉ cần sửa 1 dòng duy nhất** trong `Version.cs`:
  ```csharp
  public const string VersionString = "1.9.4";  // ← sửa đây
  ```
- Version hiện tại: `"1.9.3"` (giữ nguyên để BepInEx nhận config cũ).

### Quan trọng — Giữ nguyên BepInPlugin version 1.9.3 + bỏ BepInProcess
- `BepInPlugin` attribute GIỮ NGUYÊN version `"1.9.3"` để BepInEx nhận config file cũ
  (BepInEx key config theo `GUID.Version`, nếu đổi version sẽ tạo file config mới
  → mất `LastLanguage` đã cài đặt).
- Patch C-02 (thêm `[BepInProcess]`) đã REVERT — BepInEx 5.4.23 có bug/strict filter
  làm skip mod nếu process name không match chính xác `PlagueIncEvolved.exe` (tên
  thực tế của game trên Steam).
- Patch MN-10 (đọc version từ assembly) đã REVERT — không cần thiết cho fix.
- `<Version>` trong csproj đã REVERT — không generate AssemblyInfo mới.

### Critical (3)
- **S-01** Path traversal trong `ApplyScenarioTranslation` (Main.cs:1331) — thêm `SanitizeScenarioId` + verify final path nằm trong `scenariosRoot`. CWE-22.
- **S-02** `AddFontResourceEx` native P/Invoke với path user-controlled — thêm `IsValidFontFile` (magic bytes check) + skip symlink. CWE-78/426.
- **C-01** P/Invoke `gdi32.dll`/`user32.dll` không có platform guard — thêm `_isWindows` check + `TryAddFontResourceEx`/`TryRemoveFontResourceEx` wrappers. Log warning rõ ràng trên Linux/macOS.

### High (9)
- **ST-02** `DestroyCustomFonts` giờ cũng huỷ `ReplacementUIFonts` proxy — tránh NullReferenceException khi user đổi lại sang custom language.
- **S-03** `BroadcastFontChange` thêm audit log rõ ràng.
- **S-04** `LoadFontsFromJson` sanitize `bundleName` (no path separator / absolute path) + verify final path trong `fontDir` hoặc `Paths.PluginPath`. CWE-22.
- **S-06** `TryReadFontFamily` check size font trước khi `File.ReadAllBytes` (skip > 50MB) — tránh OOM. CWE-400.
- **P-01** Cache `UILabel[]` 1 giây (`GetAllLabels()`) — tránh `Resources.FindObjectsOfTypeAll` lặp lại mỗi thao tác.
- **P-06** `Material.set_mainTexture` patch có `Enable()`/`Disable()` gate — chỉ active khi có custom image. Trước đây overhead ~5% CPU.
- **C-02** _(REVERTED — `[BepInProcess]` làm BepInEx 5.4.23 skip mod vì process name thực tế là `PlagueIncEvolved.exe`, không match `PlagueInc.exe`)_
- **C-03** _(deferred — patch thủ công từng method đã để ở báo cáo, chưa apply trong bản này)_
- **MN-01 + MN-02** `DetectWeight` / `FontForWeight` gộp về `FontRestore.DetectWeight` / `FontRestore.FontForWeight` — xóa duplicate logic giữa Main.cs và FontRestore.cs.

### Medium (18)
- **ST-01** `OnDestroy` chỉ gọi `DestroyAll()` (đã tự gọi `Unload()` bên trong).
- **ST-03** `BroadcastFontChange` timeout 2000ms → 250ms.
- **ST-04** `LoadFontsFromManifest` — khi `AddFontResourceEx` fail, skip slot thay vì tiếp tục tạo Font từ OS (trả fallback Arial).
- **ST-05** `OnProcessExit` wrap try/catch + log via `Console.Error` (Unity đã shutdown).
- **ST-06** `LangImages.DecodeOne` wrap `FileInfo.Length` + `File.ReadAllBytes` trong try/catch riêng.
- **ST-07** Bỏ `dict[key.ToLower()] = value` trong `LoadTranslationFileStatic` và `ApplyScenarioTranslation` — dict đã `OrdinalIgnoreCase`.
- **ST-08** Cache dict theo path + LastWriteTime trong `_fileCache` — tránh parse lặp khi load scenario lại.
- **S-05** Cảnh báo khi `osName` khớp system font critical (Segoe UI, Arial, Tahoma, ...).
- **S-07** Static compiled Regex với timeout (`_translationRegex` 5s, `_priorityRegex` 2s) — tránh ReDoS.
- **S-08** `EnsureFontManifest` verify `fonts.txt` không phải symlink + catch `UnauthorizedAccess`/`IOException`.
- **S-10** `CollectImageFiles` thay `EnumerationOptions` (chỉ có .NET Standard 2.1+) bằng helper `AddFilesSafe` dùng `SearchOption.AllDirectories` + check `ReparsePoint` thủ công cho từng file. Compatible netstandard2.0.
- **P-02** `GetPriority` static compiled Regex `_priorityRegex`.
- **P-03** Gate `Debug.Log` trong `ApplyFontsToAllUIFonts` với `VerboseLogging` — tránh string alloc trong hot path.
- **P-05** `EnsureFontManifest` enumerate directory 1 lần, cache kết quả (trước đây 2 lần).
- **P-07** _(minor — TryGetValue đã optimal)_
- **C-04** _(deferred — static constructor logging chưa apply)_
- **C-06** _(deferred — CodePagesProvider register chưa apply)_
- **C-07** `MaxImageBytes` từ `const` thành `static property` + bind từ Config (`General.MaxImageBytes`).
- **MN-03** Xóa dead `LabelWeightCache` field.
- **MN-04** Xóa dead `FiUseDyn`, `FiChanged`, `MiSetActive`, `MiProcess`, `MiPanelDirty`.
- **MN-05** Implement `FileTooLargeCount` tracking metric (trước đây dead field).
- **MN-06** Gate `DecodeRoutine` + cache log với `VerboseLogging`.
- **MN-08** `EnsureFontManifest` được gọi trong `TryLoadCustomFonts` (trước đây declared nhưng không caller).
- **MN-09** `TryPatchAutotranslate` được gọi trong `Awake` (trước đây declared nhưng không caller).

### Low (12)
- **MN-07** Xóa dead `TryFallback` method.
- **MN-10** _(REVERTED — giữ BepInPlugin version 1.9.3 để BepInEx nhận config cũ)_
- **S-09** _(deferred — PatchAll allowlist chưa apply)_
- _10 Low khác_ — cosmetic/minor, không ảnh hưởng functionality.

### Breaking changes
- KHÔNG có breaking changes — `BepInPlugin` version giữ nguyên 1.9.3, config file `PlagueLibDich2000s.cfg` được BepInEx nhận và giữ nguyên `LastLanguage`.
- Thêm 1 config entry mới: `General.MaxImageBytes` (default 30MB) — entry này sẽ tự xuất hiện trong config file cũ, không ảnh hưởng các entry khác.
- Trên Linux/macOS, font custom KHÔNG hoạt động (trước đây fail silent, giờ log warning rõ ràng).

### Migration
1. Backup thư mục `BepInEx/config/` (đề phòng).
2. Thay 4 file `.cs` (`Main.cs`, `FontRestore.cs`, `LangImages.cs`, `AboutModScreen.cs`) và `.csproj` bằng bản 1.9.4.
3. Build lại: `dotnet build PlagueLibDich2000s.slnx`
4. Config `LastLanguage` được BepInEx tự động nhận (do version `1.9.3` không đổi).

---

## [1.9.3] — Bản gốc (đã audit)
- Codebase ban đầu. Điểm audit tổng hợp: **C**.
