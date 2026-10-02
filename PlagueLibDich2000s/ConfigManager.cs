using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

/// <summary>
/// ConfigManager — trung tâm cấu hình BepInEx DUY NHẤT của mod (Chính sách Zero-Hardcode).
///
/// CFG-08: trước đây các tham số nằm RỐI trực tiếp trong code — đường dẫn thư mục
/// ngôn ngữ, tên file manifest/bundle/avatar, danh sách 30 ngôn ngữ official,
/// extension file dịch, prefix FE_*, tên folder Font/Images/scenarios, các delay
/// boot/poll, từ khoá weight font, danh sách font hệ thống critical, key HUD
/// ("Infected"/"World"...) và text fallback màn About. Toàn bộ được bóc tách ra
/// đây thành ConfigEntry chuẩn BepInEx — người dùng sửa trực tiếp trong
/// BepInEx/config/com.Dich2000s.PlagueLib.cfg (hoặc bằng trình quản lý config của
/// BepInEx) mà KHÔNG cần build lại mod.
///
/// Cơ chế: Init() bind toàn bộ entry đúng 1 lần trong Awake(); giá trị được CACHE
/// vào field static public (hot path đọc field thường — KHÔNG đụng ConfigEntry
/// mỗi lần gọi); handler SettingChanged tự refresh cache khi người dùng sửa .cfg
/// giữa game. Nếu Init() chưa chạy / chạy lỗi, các field giữ GIÁ TRỊ MẶC ĐỊNH AN
/// TOÀN (đúng bộ mặc định hiện hành của mod) → hành vi không tệ hơn bản trước.
///
/// Tương thích config cũ: 3 entry gốc (General.LastLanguage, General.VerboseLogging,
/// General.MaxImageBytes) vẫn bind đúng section + key cũ — người dùng nâng cấp từ
/// các bản trước KHÔNG mất cấu hình đã cài.
///
/// Lưu ý encoding: file dịch được đọc bằng File.ReadAllText/ReadAllLines (tự nhận
/// diện UTF-8/UTF-16 + strip BOM) — mod KHÔNG ép encoding nào cả; duy nhất
/// fonts.txt do CHÍNH MOD tự sinh ra được ghi UTF-8 (ASCII content).
/// </summary>
public static class ConfigManager
{
    static ConfigFile _cfg;
    static ManualLogSource _log;
    static bool _inited;
    static int _entryCount;

    // ============ GIÁ TRỊ CACHE (đọc ở Init, refresh qua SettingChanged) ============
    // Mặc định dưới đây chỉ là GIÁ TRỊ KHỞI TẠO AN TOÀN cho trường hợp Init() chưa
    // chạy được (Awake fail sớm) — khi đó mod inert nhưng không sai hành vi.

    // ----- [Language] -----
    /// <summary>Thư mục gốc chứa pack ngôn ngữ custom (đã resolve đường dẫn tuyệt đối).</summary>
    public static string LanguageFolder;
    /// <summary>Tiền tố key UI chính trong file dịch (mặc định "FE_" — theo format của game).</summary>
    public static string FeKeyPrefix = "FE_";
    /// <summary>Extension file dịch THEO THỨ TỰ NẠP (file nạp sau đè entry trùng key).</summary>
    public static string[] LanguageFileExtensions = new[] { ".json", ".csv", ".lang", ".txt" };
    /// <summary>Tên các ngôn ngữ OFFICIAL của game — pack custom trùng tên sẽ bị bỏ qua.</summary>
    public static readonly HashSet<string> OfficialLanguages = BuildDefaultOfficialLanguages();
    /// <summary>Tên ngôn ngữ THAM CHIẾU của game (mặc định "English") — dùng để
    /// đối chiếu placeholder và build reverse lookup English→custom.</summary>
    public static string ReferenceLanguage = "English";
    /// <summary>Tên folder kịch bản trong pack ngôn ngữ.</summary>
    public static string ScenarioFolderName = "scenarios";
    /// <summary>Đuôi file kịch bản theo thứ tự ưu tiên thử.</summary>
    public static string[] ScenarioFileSuffixes = new[] { ".strings.txt", ".txt" };

    // ----- [Boot] -----
    /// <summary>Giây sau boot mới LoadAllLanguages.</summary>
    public static float LanguageLoadDelay = 2f;
    /// <summary>Giây sau boot mới áp font toàn bộ UILabel.</summary>
    public static float FontApplyDelay = 5.5f;
    /// <summary>Giây sau boot mới scan + log font của game.</summary>
    public static float FontScanDelay = 6.5f;
    /// <summary>Chu kỳ poll chờ game UI load xong (giây).</summary>
    public static float UiPollInterval = 0.5f;
    /// <summary>Thời gian tối đa chờ game UI load (giây).</summary>
    public static float UiPollTimeout = 60f;
    /// <summary>Giây đợi UI ổn định sau khi phát hiện game load xong.</summary>
    public static float UiStabilizeDelay = 2f;

    // ----- [Fonts] -----
    /// <summary>Tên folder font trong pack ngôn ngữ.</summary>
    public static string FontFolderName = "Font";
    /// <summary>Tên file manifest font.</summary>
    public static string ManifestFileName = "fonts.txt";
    /// <summary>Tên file JSON khai báo asset-bundle font.</summary>
    public static string JsonConfigFileName = "fonts.json";
    /// <summary>Tên bundle mặc định khi fonts.json không khai báo "bundle".</summary>
    public static string DefaultBundleName = "plaguefonts";
    /// <summary>Bật đăng ký .ttf/.otf qua AddFontResourceEx (chỉ Windows).</summary>
    public static bool RegisterPrivateFonts = true;
    /// <summary>Từ khoá weight Bold trong tên font.</summary>
    public static readonly HashSet<string> WeightKeywordsBold = SplitCsvSet("Bd,Bold,Black,Heavy");
    /// <summary>Từ khoá weight Light trong tên font.</summary>
    public static readonly HashSet<string> WeightKeywordsLight = SplitCsvSet("Lt,Light,Thin");
    /// <summary>Từ khoá weight Medium trong tên font.</summary>
    public static readonly HashSet<string> WeightKeywordsMedium = SplitCsvSet("Md,Med,Medium,Regular");
    /// <summary>Font hệ thống Windows critical (chỉ dùng để cảnh báo khi manifest override).</summary>
    public static readonly HashSet<string> CriticalSystemFonts = SplitCsvSet(
        "Segoe UI,Segoe UI Light,Segoe UI Semibold,Segoe UI Black,Tahoma,Microsoft Sans Serif,"
        + "Arial,Courier New,Times New Roman,Calibri,Candara,Consolas");

    // ----- [Images] -----
    /// <summary>Tên folder ảnh thay thế trong pack ngôn ngữ.</summary>
    public static string ImagesFolderName = "Images";

    // ----- [UI] -----
    /// <summary>Key localisation nhãn nhiễm bệnh HUD (single player).</summary>
    public static string HudInfectedKey = "Infected";
    /// <summary>Key localisation nhãn nhiễm bệnh HUD (multiplayer).</summary>
    public static string HudInfectedMultiplayerKey = "IG_Infected";
    /// <summary>Key localisation nhãn thống kê không xác định (multiplayer).</summary>
    public static string HudUnknownStatsKey = "Stats_Unknown";
    /// <summary>Key localisation tiêu đề context World.</summary>
    public static string WorldTitleKey = "World";

    // ----- [About] -----
    /// <summary>Tiêu đề fallback màn About Mod.</summary>
    public static string AboutFallbackTitle = "About Mod";
    /// <summary>Nội dung fallback màn About Mod (mặc định = nội dung cũ, kèm version hiện tại).</summary>
    public static string AboutFallbackDescription = BuildAboutDefaultDescription();
    /// <summary>Chiều rộng logo About Mod (pixel) — chiều cao tự tính theo tỷ lệ ảnh.</summary>
    public static int AboutAvatarWidth = 320;
    /// <summary>Tên file avatar/logo trong <pack>/Images.</summary>
    public static string AboutAvatarFileName = "AboutMod.png";

    /// <summary>Danh sách tên ngôn ngữ official mặc định (giá trị config mặc định).</summary>
    const string DefaultOfficialLanguagesCsv =
        "English,Spanish,French,German,Italian,Portuguese,Russian,Japanese,Korean,Chinese,"
        + "Simplified Chinese,Traditional Chinese,Polish,Turkish,Dutch,Swedish,Norwegian,Danish,"
        + "Finnish,Hungarian,Czech,Romanian,Bulgarian,Greek,Thai,Indonesian,Vietnamese,"
        + "Español,Français,Deutsch,Português,简体中文,繁體中文";

    // ===================== HELPERS (dùng được cả trước Init) =====================

    /// <summary>true nếu key là key UI chính (FE_* — theo prefix cấu hình).</summary>
    public static bool IsFeKey(string key)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(FeKeyPrefix)) return false;
        return key.StartsWith(FeKeyPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>true nếu tên là ngôn ngữ OFFICIAL của game (theo config — HashSet O(1), không IO).</summary>
    /// <summary>
    /// Official linh hoạt: config exact → token-match config → có trong mpLocalisedTexts của game.
    /// "Chinese Traditional" ≡ "Traditional Chinese"; "Portuges" vẫn match nếu có trong game dict.
    /// </summary>
    public static bool IsOfficialLanguage(string langName)
    {

        if (string.IsNullOrEmpty(langName)) return false;
        // 1) Exact config
        if (OfficialLanguages.Contains(langName)) return true;

        // 2) Token-match config (Chinese Traditional ≡ Traditional Chinese, …)
        foreach (string o in OfficialLanguages)
        {
            if (LangNamesMatch(langName, o)) return true;
        }

        // 3) Có trong dict game — CHỈ khi KHÔNG có folder pack custom cùng tên
        //    (tránh "Tiếng Việt" / "T.Việt Shizuna" bị coi official vì đã có trong dropdown)
        if (GameHasLocalisation(langName))
        {
            try
            {
                if (!string.IsNullOrEmpty(LanguageFolder))
                {
                    string path = Path.Combine(LanguageFolder, langName);
                    if (Directory.Exists(path))
                        return false; // folder custom tồn tại → KHÔNG official
                }
            }
            catch { }
            return true;
        }

        return false;
    }

    /// <summary>Cùng bộ token chữ (không phân biệt hoa thường, bỏ khoảng/gạch).</summary>
    public static bool LangNamesMatch(string a, string b)
    {
        var ta = LangTokens(a);
        var tb = LangTokens(b);
        if (ta.Count == 0 || tb.Count == 0) return false;
        return ta.SetEquals(tb);
    }

    static HashSet<string> LangTokens(string name)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(name)) return set;
        string[] parts = name.Split(new[] { ' ', '-', '_', ',', '/', '(', ')' },
            StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            string t = parts[i].Trim();
            if (t.Length > 0) set.Add(t);
        }
        return set;
    }

    /// <summary>true nếu CLocalisationManager đã có dictionary cho tên này (hoặc token-match).</summary>
    public static bool GameHasLocalisation(string langName)
    {
        if (string.IsNullOrEmpty(langName)) return false;
        try
        {
            var field = typeof(CLocalisationManager).GetField(
                "mpLocalisedTexts",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (field == null) return false;
            var all = field.GetValue(null) as Dictionary<string, Dictionary<string, string>>;
            if (all == null || all.Count == 0) return false;
            if (all.ContainsKey(langName)) return true;
            foreach (var k in all.Keys)
            {
                if (LangNamesMatch(k, langName)) return true;
            }
        }
        catch { }
        return false;
    }

    // ===================== INIT =====================

    /// <summary>
    /// Bind toàn bộ cấu hình. GỌI ĐÚNG 1 LẦN từ PlagueVnMod.Awake() (sau khi BepInEx
    /// đã tạo ConfigFile cho plugin). An toàn gọi lại — lần sau bị bỏ qua.
    /// </summary>
    public static void Init(ConfigFile cfg, ManualLogSource log)
    {
        if (_inited) return;
        _inited = true;
        _cfg = cfg;
        _log = log;
        try
        {
            BindAll();
        }
        catch (Exception ex)
        {
            if (_log != null)
                _log.LogError("[Config] Bind lỗi — mod dùng giá trị mặc định an toàn: " + ex.Message);
        }
        // Fail-safe: Bind lỗi giữa chừng vẫn phải có LanguageFolder hợp lệ.
        if (string.IsNullOrEmpty(LanguageFolder))
        {
            try { LanguageFolder = Path.Combine(Paths.ConfigPath, "Language"); }
            catch { LanguageFolder = null; }
        }
        if (_log != null)
            _log.LogInfo("[Config] ConfigManager đã nạp " + _entryCount
                + " mục cấu hình → BepInEx/config/" + PluginVersion.Guid + ".cfg");
    }

    static void BindAll()
    {
        // ===== Legacy — GIỮ NGUYÊN section + key của các bản trước (không mất config cũ) =====
        PlagueVnMod.LastLanguage = _cfg.Bind("General", "LastLanguage", "",
            "Ngôn ngữ custom lần trước (để trống = tự chọn folder .enabled)");
        _entryCount++;

        PlagueVnMod.VerboseLogging = _cfg.Bind("General", "VerboseLogging", false,
            "Bật log chi tiết (debug) — có thể làm chậm nhẹ trên máy yếu, chỉ bật khi cần chẩn đoán lỗi");
        _entryCount++;
        LangImageVault.VerboseLogging = PlagueVnMod.VerboseLogging.Value;
        PlagueVnMod.VerboseLogging.SettingChanged +=
            (s, e) => LangImageVault.VerboseLogging = PlagueVnMod.VerboseLogging.Value;

        var cMaxImageBytes = _cfg.Bind("General", "MaxImageBytes", 30L * 1024 * 1024,
            "Kích thước ảnh tối đa (bytes) — file lớn hơn sẽ bị skip");
        _entryCount++;
        LangImageVault.MaxImageBytes = cMaxImageBytes.Value;
        cMaxImageBytes.SettingChanged += (s, e) => LangImageVault.MaxImageBytes = cMaxImageBytes.Value;

        // ===== [Language] =====
        var cLangFolder = _cfg.Bind("Language", "LanguageFolder", "",
            "Thư mục gốc chứa pack ngôn ngữ custom. Để trống = <BepInEx>/config/Language. "
            + "Chấp nhận đường dẫn tuyệt đối, hoặc tương đối tính từ thư mục gốc BepInEx. "
            + "Đổi xong cần restart game.");
        _entryCount++;
        ApplyLanguageFolder(cLangFolder.Value);
        cLangFolder.SettingChanged += (s, e) => ApplyLanguageFolder(cLangFolder.Value);

        BindString("Language", "FeKeyPrefix", "FE_",
            "Tiền tố key UI chính trong file dịch (mặc định FE_ — theo format file của game).",
            v => FeKeyPrefix = v);

        BindStringList("Language", "LanguageFileExtensions", ".json,.csv,.lang,.txt",
            "Extension file dịch được nạp (cách nhau bằng dấu phẩy), THEO THỨ TỰ NẠP — "
            + "file nạp sau sẽ ĐÈ entry trùng key của file nạp trước, vì vậy .txt đứng cuối "
            + "để có ưu tiên cao nhất.",
            v => LanguageFileExtensions = v);

        BindSet("Language", "OfficialLanguages", DefaultOfficialLanguagesCsv,
            "Danh sách tên ngôn ngữ OFFICIAL của game (cách nhau bằng dấu phẩy). Pack custom "
            + "đặt tên trùng một mục bất kỳ sẽ bị BỎ qua để bảo vệ danh sách ngôn ngữ gốc "
            + "(nguồn dữ liệu của dropdown chọn ngôn ngữ). Game thêm ngôn ngữ mới trong bản "
            + "update thì bổ sung tên vào đây (không cần build lại mod).",
            OfficialLanguages);

        BindString("Language", "ReferenceLanguage", "English",
            "Tên ngôn ngữ THAM CHIẾU của game (nguồn text gốc để đối chiếu placeholder "
            + "và build reverse lookup). Chỉ đổi khi game đổi tên ngôn ngữ gốc.",
            v => ReferenceLanguage = v);

        BindString("Language", "ScenarioFolderName", "scenarios",
            "Tên folder chứa file dịch kịch bản (trong pack ngôn ngữ).",
            v => ScenarioFolderName = v);

        BindStringList("Language", "ScenarioFileSuffixes", ".strings.txt,.txt",
            "Đuôi file kịch bản theo thứ tự ưu tiên thử (cách nhau bằng dấu phẩy).",
            v => ScenarioFileSuffixes = v);

        // ===== [Boot] =====
        BindFloat("Boot", "LanguageLoadDelaySeconds", 2f, 0f, 120f,
            "Số giây sau khi game khởi động mới LoadAllLanguages (đợi game tự nạp ngôn ngữ gốc xong).",
            v => LanguageLoadDelay = v);
        BindFloat("Boot", "FontApplyDelaySeconds", 5.5f, 0f, 300f,
            "Số giây sau khi game khởi động mới áp font cho toàn bộ UILabel.",
            v => FontApplyDelay = v);
        BindFloat("Boot", "FontScanDelaySeconds", 6.5f, 0f, 300f,
            "Số giây sau khi game khởi động mới scan + log danh sách font có trong game.",
            v => FontScanDelay = v);
        BindFloat("Boot", "UiPollIntervalSeconds", 0.5f, 0.05f, 10f,
            "Chu kỳ (giây) poll chờ game UI load xong trước khi force ngôn ngữ custom.",
            v => UiPollInterval = v);
        BindFloat("Boot", "UiPollTimeoutSeconds", 60f, 5f, 600f,
            "Thời gian (giây) tối đa chờ game UI load xong — quá hạn thì bỏ qua force reload.",
            v => UiPollTimeout = v);
        BindFloat("Boot", "UiStabilizeDelaySeconds", 2f, 0f, 30f,
            "Số giây đợi UI ổn định sau khi phát hiện game load xong rồi mới force ngôn ngữ.",
            v => UiStabilizeDelay = v);

        // ===== [Fonts] =====
        BindString("Fonts", "FontFolderName", "Font",
            "Tên folder chứa font trong pack ngôn ngữ.",
            v => FontFolderName = v);
        BindString("Fonts", "ManifestFileName", "fonts.txt",
            "Tên file manifest font (gameKey = fileName | osFontName) trong folder font.",
            v => ManifestFileName = v);
        BindString("Fonts", "JsonConfigFileName", "fonts.json",
            "Tên file JSON khai báo asset-bundle font trong folder font.",
            v => JsonConfigFileName = v);
        BindString("Fonts", "DefaultBundleName", "plaguefonts",
            "Tên asset-bundle font mặc định khi fonts.json không khai báo trường \"bundle\".",
            v => DefaultBundleName = v);
        BindBool("Fonts", "RegisterPrivateFonts", true,
            "Đăng ký file .ttf/.otf của pack bằng AddFontResourceEx (chỉ Windows). "
            + "Tắt nếu muốn dùng riêng manifest font.",
            v => RegisterPrivateFonts = v);
        BindSet("Fonts", "WeightKeywordsBold", "Bd,Bold,Black,Heavy",
            "Từ khoá trong tên font nhận diện weight Bold (cách nhau bằng dấu phẩy).",
            WeightKeywordsBold);
        BindSet("Fonts", "WeightKeywordsLight", "Lt,Light,Thin",
            "Từ khoá trong tên font nhận diện weight Light (cách nhau bằng dấu phẩy).",
            WeightKeywordsLight);
        BindSet("Fonts", "WeightKeywordsMedium", "Md,Med,Medium,Regular",
            "Từ khoá trong tên font nhận diện weight Medium (cách nhau bằng dấu phẩy).",
            WeightKeywordsMedium);
        BindSet("Fonts", "CriticalSystemFonts",
            "Segoe UI,Segoe UI Light,Segoe UI Semibold,Segoe UI Black,Tahoma,Microsoft Sans Serif,"
            + "Arial,Courier New,Times New Roman,Calibri,Candara,Consolas",
            "Danh sách font hệ thống Windows quan trọng — mod cảnh báo khi manifest muốn "
            + "override các font này (chỉ để cảnh báo, không chặn).",
            CriticalSystemFonts);

        // ===== [Images] =====
        BindString("Images", "ImagesFolderName", "Images",
            "Tên folder chứa ảnh thay thế trong pack ngôn ngữ.",
            v => ImagesFolderName = v);

        // ===== [UI] =====
        BindString("UI", "HudInfectedKey", "Infected",
            "Key localisation cho nhãn nhiễm bệnh của HUD (single player).",
            v => HudInfectedKey = v);
        BindString("UI", "HudInfectedMultiplayerKey", "IG_Infected",
            "Key localisation cho nhãn nhiễm bệnh của HUD (multiplayer).",
            v => HudInfectedMultiplayerKey = v);
        BindString("UI", "HudUnknownStatsKey", "Stats_Unknown",
            "Key localisation cho nhãn thống kê không xác định (multiplayer).",
            v => HudUnknownStatsKey = v);
        BindString("UI", "WorldTitleKey", "World",
            "Key localisation cho tiêu đề màn hình context World.",
            v => WorldTitleKey = v);

        // ===== [About] =====
        BindString("About", "FallbackTitle", "About Mod",
            "Tiêu đề fallback của màn About Mod khi file dịch không có key FE_About_Mod.",
            v => AboutFallbackTitle = v);
        BindString("About", "FallbackDescription", BuildAboutDefaultDescription(),
            "Nội dung fallback của màn About Mod khi file dịch không có key FE_About_Mod_Desc. "
            + "Hỗ trợ xuống dòng (ghi \\n trong file config).",
            v => AboutFallbackDescription = v);
        BindInt("About", "AvatarWidth", 320, 16, 2048,
            "Chiều rộng (pixel) của logo About Mod — chiều cao tự tính theo tỷ lệ ảnh thật.",
            v => AboutAvatarWidth = v);
        BindString("About", "AvatarFileName", "AboutMod.png",
            "Tên file ảnh avatar/logo đặt trong <pack>/Images (ghi đè ảnh nhúng trong DLL).",
            v => AboutAvatarFileName = v);
    }

    // ===================== BIND HELPERS =====================

    static void BindString(string section, string key, string def, string desc, Action<string> apply)
    {
        var e = _cfg.Bind(section, key, def, desc);
        _entryCount++;
        SafeApply(key, e.Value, apply);
        e.SettingChanged += (s, ev) => SafeApply(key, e.Value, apply);
    }

    static void BindStringList(string section, string key, string def, string desc, Action<string[]> apply)
    {
        var e = _cfg.Bind(section, key, def, desc);
        _entryCount++;
        SafeApply(key, SplitCsv(e.Value), apply);
        e.SettingChanged += (s, ev) => SafeApply(key, SplitCsv(e.Value), apply);
    }

    static void BindSet(string section, string key, string def, string desc, HashSet<string> target)
    {
        var e = _cfg.Bind(section, key, def, desc);
        _entryCount++;
        SafeApplySet(key, e.Value, target);
        e.SettingChanged += (s, ev) => SafeApplySet(key, e.Value, target);
    }

    static void BindBool(string section, string key, bool def, string desc, Action<bool> apply)
    {
        var e = _cfg.Bind(section, key, def, desc);
        _entryCount++;
        SafeApply(key, e.Value, apply);
        e.SettingChanged += (s, ev) => SafeApply(key, e.Value, apply);
    }

    static void BindFloat(string section, string key, float def, float min, float max, string desc, Action<float> apply)
    {
        var e = _cfg.Bind(section, key, def,
            new ConfigDescription(desc, new AcceptableValueRange<float>(min, max)));
        _entryCount++;
        SafeApply(key, ClampFloat(e.Value, min, max), apply);
        e.SettingChanged += (s, ev) => SafeApply(key, ClampFloat(e.Value, min, max), apply);
    }

    static void BindInt(string section, string key, int def, int min, int max, string desc, Action<int> apply)
    {
        var e = _cfg.Bind(section, key, def,
            new ConfigDescription(desc, new AcceptableValueRange<int>(min, max)));
        _entryCount++;
        SafeApply(key, ClampInt(e.Value, min, max), apply);
        e.SettingChanged += (s, ev) => SafeApply(key, ClampInt(e.Value, min, max), apply);
    }

    /// <summary>Áp giá trị vào cache — exception khi áp (vd user sửa sai) được log, không văng.</summary>
    static void SafeApply<T>(string key, T value, Action<T> apply)
    {
        try { apply(value); }
        catch (Exception ex)
        {
            if (_log != null)
                _log.LogWarning("[Config] Giá trị không áp dụng được cho " + key + ": " + ex.Message);
        }
    }

    static void SafeApplySet(string key, string csv, HashSet<string> target)
    {
        try
        {
            target.Clear();
            string[] parts = SplitCsv(csv);
            for (int i = 0; i < parts.Length; i++) target.Add(parts[i]);
        }
        catch (Exception ex)
        {
            if (_log != null)
                _log.LogWarning("[Config] Giá trị không áp dụng được cho " + key + ": " + ex.Message);
        }
    }

    static float ClampFloat(float v, float min, float max)
    {
        if (v < min) return min;
        if (v > max) return max;
        return v;
    }

    static int ClampInt(int v, int min, int max)
    {
        if (v < min) return min;
        if (v > max) return max;
        return v;
    }

    static string[] SplitCsv(string csv)
    {
        if (string.IsNullOrEmpty(csv)) return new string[0];
        string[] parts = csv.Split(',');
        var list = new List<string>(parts.Length);
        for (int i = 0; i < parts.Length; i++)
        {
            string p = parts[i].Trim();
            if (p.Length > 0) list.Add(p);
        }
        return list.ToArray();
    }

    static HashSet<string> SplitCsvSet(string csv)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(csv)) return set;
        string[] parts = SplitCsv(csv);
        for (int i = 0; i < parts.Length; i++) set.Add(parts[i]);
        return set;
    }

    static HashSet<string> BuildDefaultOfficialLanguages()
    {
        return SplitCsvSet(DefaultOfficialLanguagesCsv);
    }

    /// <summary>
    /// Resolve thư mục ngôn ngữ: trống → &lt;BepInEx&gt;/config/Language; tương đối →
    /// tính từ thư mục gốc BepInEx; tuyệt đối → dùng nguyên. An toàn mọi input.
    /// </summary>
    static void ApplyLanguageFolder(string configured)
    {
        string folder = (configured ?? "").Trim();
        try
        {
            if (folder.Length == 0)
                folder = Path.Combine(Paths.ConfigPath, "Language");
            else if (!Path.IsPathRooted(folder))
                folder = Path.Combine(Paths.BepInExRootPath, folder);
        }
        catch
        {
            try { folder = Path.Combine(Paths.ConfigPath, "Language"); }
            catch { folder = null; }
        }
        LanguageFolder = folder;
    }

    /// <summary>Nội dung fallback mặc định của màn About (giữ nguyên nội dung cũ, kèm version hiện tại).</summary>
    static string BuildAboutDefaultDescription()
    {
        return "Plague Inc Language Library v" + PluginVersion.DisplayVersion + "\n\n" +
        "Created by Dich 2000s Team with A.I Support\n\n" +
        "Include: Grok 4.6, Claude Sonnet 4.6 & 5, Gemini NotebookLLM, GLM 5.2\n\n" +
        "About Team:\n Đi Ẻ Game Chơi(@fifaifaccau) (Youtube)\n Nhựa Inox (@nhuainox) (Youtube)\n Megapixel (@megapixel-real) (Youtube)\n\n" +
        "This is the first mod of our team, and we dont know how to code C#, ummm, yeah, that's all, thanks for download our mod! (I'm bad at English)\n\n" +
        "A BepInEx localisation pack: Localization Mod.\n\n" +
        "Override this text in your language folder:\n" +
        "\"FE_About_Mod\" / \"FE_About_Mod_Desc\"";
    }
}
