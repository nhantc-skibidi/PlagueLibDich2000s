using System.Reflection;

/// <summary>
/// Single source of truth cho phiên bản mod.
/// </summary>
public static class PluginVersion
{
    /// <summary>
    /// BUILD 9 (2026-10-03): tách Main.cs + fix build — KHÔNG đổi hành vi runtime.
    /// Tái cấu trúc (mỗi hàm move nguyên văn, verify bằng diff từng hàm so với
    /// Main.cs gốc — xem commit/PR để đối chiếu):
    /// — Main.cs 5186 → 4106 dòng, tách 4 file mới: WindowsFontRegistrar.cs (đăng ký
    ///   font Win32 AddFontResourceEx/RemoveFontResourceEx), FontFamilyReader.cs (đọc
    ///   family name từ bảng 'name' TTF/OTF), TranslationFileLoader.cs (parse/cache
    ///   file dịch .txt + validate placeholder {N} chống FormatException),
    ///   FontLoader.cs (load font từ AssetBundle hoặc .ttf/.otf + manifest). Field dùng
    ///   chung (FontBd/Md/Lt, FontsReady, CustomFonts, TranslationDict,
    ///   EnglishTextDict) giữ nguyên trên PlagueVnMod — chỉ method được move.
    /// — Xoá dead code: RegisterPrivateFonts(string fontDir) (không nơi nào gọi tới;
    ///   đăng ký font thật sự nằm trong LoadSlot/LoadFontsFromManifest).
    /// — FIX PERF-01: BuildEnglishToCustomDict/LoadAllLanguages/1 hàm build dict ngôn
    ///   ngữ tự GetField("mpLocalisedTexts",...) riêng thay vì dùng field đã cache sẵn
    ///   (_fiMpLocalisedTexts) — gộp lại dùng chung, bớt vài lần tra cứu reflection.
    /// Fix build (không đụng code C# — chỉ .csproj):
    /// — FIX BUILD-01: 9 dòng &lt;HintPath&gt; trong .csproj hardcode cứng đường dẫn
    ///   tương đối riêng của máy tác giả gốc, mâu thuẫn với chính comment trong file
    ///   nói là lấy từ Directory.Build.props — trên máy khác, dù
    ///   Directory.Build.props.user đã đúng, build vẫn ra 213 lỗi CS0246 vì HintPath
    ///   chưa từng thực sự dùng $(BepInExDir)/$(GameManagedDir). Sửa cả 9 dòng dùng
    ///   đúng 2 biến đó.
    /// — FIX BUILD-02: TargetFramework netstandard2.0 → netstandard2.1 — DLL thật của
    ///   game (Assembly-CSharp, UnityEngine.CoreModule...) build nhắm netstandard 2.1,
    ///   gây CS1705 khi project nhắm 2.0 (thấp hơn những gì assembly tham chiếu cần).
    /// BUILD 8 (2026-09-02): OPT-01..07 — sửa lỗi "màn Options lâu lâu hiện key thô
    /// FE_Options_*" (không gắn hoàn chỉnh value của game, xem video 2026-09-02).
    /// GỐC RỄ (từ log + video): sau khi đổi official↔custom vài lần,
    /// ForceRefreshAutotranslateLabels / RestoreLabelTextsToOfficial gán nguyên giá
    /// trị GetText trả về vào label — khi key miss trong mọi dict của game (file
    /// ngôn ngữ repack thiếu entry / value rỗng), GetText trả lại CHÍNH KEY → key
    /// thô bị "đóng dấu" vào UILabel và giữ vậy cả khi quay lại custom (label đó
    /// không nằm trong pack nên RefreshAllLabels bỏ qua). Ngoài ra
    /// TryPatchAutotranslate patch hụt chữ ký từ trước (SetInitialText thật là
    /// (string,bool) — warning LANG-18 trong log) nên không có lưới an toàn lúc
    /// label enable. Mọi fix đều DATA-DRIVEN (zero-hardcode):
    /// — OPT-01/02: fallback dữ liệu ngôn ngữ game (GetText English-fallback nội bộ)
    ///   cho key FE_* thiếu trong pack — label hiển thị giá trị game thay vì key thô
    /// — OPT-03/04: CẤM ghi giá trị == key thô vào label khi restore/force (total-miss
    ///   → giữ text hiện tại, tự lành khi quay lại custom)
    /// — OPT-05/05b: value rỗng trong file dịch bị bỏ qua (game trả key khi gặp
    ///   value rỗng; TryGetTranslation cũng không trả entry rỗng)
    /// — OPT-06: patchUILabelAutotranslate.OnEnable (0 tham số — ổn định mọi version)
    ///   với postfix sanitizer + reset currentLanguage; tra biến thể bỏ ':' cuối
    ///   (prefab serialize "FE_X:" còn key trong file là "FE_X")
    /// — OPT-07: UILabel.OnEnable postfix — lưới an toàn cuối cùng cho mọi label
    ///   bật lên với text là key FE_* khi custom active (gate StartsWith, chi phí ~0)
    /// BUILD 7 (2026-09-01): LANG-32
    /// — Help & Info / "Về Bản Mod" kẹt tiếng Việt khi chuyển sang official
    ///   (Russian/English/...) sau khi từng dùng pack custom
    /// — TryGetTranslation không gate IsCustomLanguageActive → GetLoc/About button
    ///   vẫn trả bản dịch pack cũ
    /// — RestoreLabelTextsToOfficial bỏ sót label không có OriginalTextCache
    ///   (màn Help mở muộn, nút inject) → bổ sung nhận diện qua ValueToEnglish
    /// — official→official cũng chạy light cleanup stuck-custom + cập nhật caption
    /// </summary>
    public const string VersionString = "1.0.1";

    public const string Guid = "com.Dich2000s.PlagueLib";

    public const string Name = "Plague Inc Language Library";

    public static string DisplayVersion
    {
        get { return VersionString; }
    }
}