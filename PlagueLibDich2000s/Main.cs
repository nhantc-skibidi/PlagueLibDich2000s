using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

[BepInPlugin(PluginVersion.Guid, PluginVersion.Name, PluginVersion.VersionString)]
public class PlagueVnMod : BaseUnityPlugin
{
    public static PlagueVnMod Instance;

    // Tăng mỗi lần đổi ngôn ngữ — coroutine teardown/font cũ tự hủy nếu bị thay thế.
    static int _langEpoch;
    public static int BumpLangEpoch() { return ++_langEpoch; }
    public static int LangEpoch { get { return _langEpoch; } }

    static readonly HashSet<string> TranslatedValues =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string> ValueToEnglish =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // FIX P-01: cache UILabel list 1 giây.
    static UILabel[] _cachedLabels;
    static float _cachedLabelsAt = -100f;
    const float LABEL_CACHE_TTL = 1.0f;

    // FIX v2.7: clear cache khi cần lấy danh sách UILabel mới nhất (vd: sau scene change)
    public static void ClearLabelCache()
    {
        _cachedLabels = null;
        _cachedLabelsAt = -100f;
    }

    static UILabel[] GetAllLabels()
    {
        if (Time.unscaledTime - _cachedLabelsAt < LABEL_CACHE_TTL && _cachedLabels != null)
            return _cachedLabels;
        // FIX v2.7: dùng Resources.FindObjectsOfTypeAll thay vì FindObjectsOfType
        // — tìm cả UILabel inactive (trong prefab ẩn, menu chưa mở). FindObjectsOfType
        // chỉ tìm object active trong hierarchy → miss nhiều label.
        _cachedLabels = Resources.FindObjectsOfTypeAll(typeof(UILabel)) as UILabel[];
        _cachedLabelsAt = Time.unscaledTime;
        return _cachedLabels;
    }

    public static void ApplyFontsToAllLabels()
    {
        if (!FontsReady || !IsCustomLanguageActive()) return;
        UILabel[] labels = GetAllLabels();  // FIX P-01: dùng cache
        if (labels == null) return;
        int n = 0;
        for (int i = 0; i < labels.Length; i++)
        {
            UILabel lab = labels[i];
            if (lab == null || lab.gameObject == null) continue;
            if (!FontRestore.IsLiveSceneObject(lab)) continue;
            if (IsPopupRelated(lab)) continue; // FIX v0.29.3: không đụng dropdown/popup
            FontRestore.Capture(lab);
            TryApplyFontToLabelHard(lab);
            n++;
        }
        // FIX CR-18: gate bằng VerboseLogging — tránh alloc chuỗi + ghi log trong
        // hot path (hàm chạy mỗi lần apply font cho toàn bộ label).
        if (VerboseLogging != null && VerboseLogging.Value)
            Debug.Log("[Localizer] HARD APPLY labels=" + n
                + " Bd=" + (FontBd != null) + " Md=" + (FontMd != null) + " Lt=" + (FontLt != null));
    }
    // ===================== CONFIG =====================
    public static ConfigEntry<string> LastLanguage;
    public static ConfigEntry<bool> VerboseLogging;


    // ===================== DICT =====================
    // FIX v2.16: Tách 2 dict riêng:
    //   - TranslationDict: key gốc (FE_*) → Vietnamese — cho menu UI
    //   - EnglishTextDict: English text (vd "Single Player") → Vietnamese — cho kịch bản Board Game
    // Trước đây trộn lẫn vào 1 dict → có thể xung đột.
    public static Dictionary<string, string> TranslationDict =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // Dict cho English text keys (không phải FE_*) — vd "Single Player" = "1 Người"
    public static Dictionary<string, string> EnglishTextDict =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // FIX v2.9: Reverse lookup — English text → Vietnamese text.
    // Build khi LoadAllLanguages chạy. Dùng để re-localize UILabel có text English
    // (không phải key gốc) khi custom language active.
    // Key: English text (vd "Multiplayer"), Value: Vietnamese text (vd "Nhiều người chơi")
    public static Dictionary<string, string> EnglishToCustomDict =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public static Dictionary<string, string> ScenarioDict =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // FIX v0.28.3: Cache text gốc của UILabel trước khi translate.
    // Khi switch language, text đã translate (vd "Ngôn ngữ:") không match key
    // trong dict mới → cần restore text gốc trước khi tra lại.
    public static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UILabel, string> OriginalTextCache =
        new System.Runtime.CompilerServices.ConditionalWeakTable<UILabel, string>();

    // ===== [TRACE] TẠM — chẩn đoán feedback loop RefreshAllLabels, XOÁ sau khi xác định root cause =====
    // Chỉ trace các label nghi vấn (theo text hiện tại hoặc tên GameObject chứa 1
    // trong các từ khoá) để không spam log / không tốn hiệu năng trên 3000+ label.
    public static readonly HashSet<string> TraceWatchList =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Simple", "Đơn giản", "Single Player", "1 Người", "Chơi Đơn",
            "Reset All Progress", "Tiến trình", "Progress",
            "Xóa hết tiến trình", "FE_Reset_All_Progress", "AUTHORITY", "Tín nhiệm"
        };
    static readonly string[] TraceWatchNameParts = { "Reset", "Single", "Progress", "Button", "Caption" };
    static bool ContainsAnyWatchName(string goName)
    {
        if (string.IsNullOrEmpty(goName)) return false;
        for (int i = 0; i < TraceWatchNameParts.Length; i++)
            if (goName.IndexOf(TraceWatchNameParts[i], StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        return false;
    }

    // ===================== FIX LANG-40/41/42 (đổi ngôn ngữ khi đang chơi bị lộn xộn) =====================
    // 4 nguyên nhân gốc (xác minh qua decompile UILabelAutotranslate / CMainOptionsSubScreen.LanguageChange /
    // CLocalisationManager.GetTextInternal + log + video):
    //   RC1 — Game refresh Options bằng vòng translateLabels[i].AutoTranslate() chạy SAU khi set_ActiveLanguage
    //         trả về; mod restore/refresh SỚM HƠN (bên trong setter) → game stamp ĐÈ text từ
    //         originalLabelText ngay sau khi mod sửa xong.
    //   RC2 — AutoTranslate() lần ĐẦU (label==null) tự originalLabelText = label.text: nếu label đã bị mod
    //         stamp bản dịch custom (label inactive lúc RefreshAllLabels) → component bị "poison" vĩnh viễn,
    //         mỗi lần đổi ngữ GetText(bản dịch custom) miss → game trả nguyên chuỗi đó → dính lại.
    //   RC3 — Restore của mod đi theo English-text nhưng menu UI có key thật là FE_* (English chỉ là VALUE)
    //         → GetText("General") miss → label kẹt English. Dữ liệu tra ngược có SẴN trong dict English
    //         của game (mpLocalisedTexts) — chỉ cần build map ngược (data-driven, không hardcode).
    //   RC4 — Repack file ngôn ngữ có value RỖNG → GetTextFromDictionary trả nguyên tag → game tự stamp
    //         key thô "FE_Options_*" (thấy trong video khi chuyển qua Italiano/English).

    // LANG-40: Cache KEY NGUỒN (authoritative) của label — capture 1 lần khi label còn nguyên vẹn
    // (trước khi component bị poison). Ưu tiên tuyệt đối khi restore về official / heal component.
    public static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UILabel, string> LabelSourceKeyCache =
        new System.Runtime.CompilerServices.ConditionalWeakTable<UILabel, string>();

    // LANG-41: Index value ĐÃ CHUẨN HOÁ → nguồn. Bản dịch trong pack có ký tự vô hình
    // (U+180E, U+2800, zero-width...) và biến thể ':' khiến lookup exact miss → text custom
    // kẹt trên label sau khi đổi official. Chuẩn hoá: bỏ ký tự vô hình + gập whitespace +
    // bỏ ':' cuối + upper invariant.
    public static readonly Dictionary<string, string> ValueToEnglishNormalized =
        new Dictionary<string, string>(StringComparer.Ordinal);

    // ===== MỚI: dict scoped theo (rowKey, optionEnumValue) → bản dịch =====
    // Populate khi load file dịch NẾU file dịch của bạn có entry dạng
    // "FE_Options_InterfaceType|SIMPLE=Đơn giản" (bạn cần xác nhận format thật
    // của file .txt hiện tại, tôi không tự bịa cú pháp).
    public static readonly Dictionary<string, string> OptionValueScopedDict =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public static string BuildScopedOptionKey(string rowFeKey, string optionEnumValue)
    {
        if (string.IsNullOrEmpty(rowFeKey) || string.IsNullOrEmpty(optionEnumValue)) return null;
        return rowFeKey.Trim() + "|" + optionEnumValue.Trim();
    }

    public static bool TryMapAnyOfficialValueToKey(string text, out string feKey)
    {
        feKey = null;
        if (string.IsNullOrEmpty(text)) return false;
        string t = text.Trim();
        if (t.Length == 0) return false;
        if (ConfigManager.IsFeKey(t)) { feKey = t; return true; }

        if (AnyOfficialValueToKey.TryGetValue(t, out feKey) && !string.IsNullOrEmpty(feKey))
            return true;

        if (t.EndsWith(":") || t.EndsWith("\uFF1A"))
        {
            string nc = t.TrimEnd(':', '\uFF1A').TrimEnd();
            if (AnyOfficialValueToKey.TryGetValue(nc, out feKey) && !string.IsNullOrEmpty(feKey))
                return true;
        }

        string norm = NormalizeForMatch(t);
        if (norm.Length > 0
            && AnyOfficialValueToKeyNorm.TryGetValue(norm, out feKey)
            && !string.IsNullOrEmpty(feKey))
            return true;

        return false;
    }
    // LANG-41b: Tra ngược value → nguồn (exact trước, normalized sau).
    public static bool TryMapValueToEnglish(string text, out string eng)
    {
        eng = null;
        if (string.IsNullOrEmpty(text)) return false;
        string t = text.Trim();
        if (t.Length == 0) return false;
        if (ValueToEnglish.TryGetValue(t, out eng) && !string.IsNullOrEmpty(eng)) return true;
        string norm = NormalizeForMatch(t);
        if (norm.Length == 0) return false;
        return ValueToEnglishNormalized.TryGetValue(norm, out eng) && !string.IsNullOrEmpty(eng);
    }

    // LANG-41: Chuẩn hoá chuỗi để so khớp value đã dịch (bỏ mọi ký tự vô hình/hack).
    public static string NormalizeForMatch(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            // Ký tự vô hình thường gặp trong file dịch: soft hyphen, Mongolian vowel sep,
            // zero-width, bidi controls, Braille blank (U+2800), BOM, variation selectors.
            if (c == '\u00AD'
                || (c >= '\u180B' && c <= '\u180F')
                || (c >= '\u200B' && c <= '\u200F')
                || (c >= '\u202A' && c <= '\u202E')
                || (c >= '\u2060' && c <= '\u2064')
                || c == '\u2800'
                || c == '\uFEFF'
                || (c >= '\uFFF9' && c <= '\uFFFB')
                || (c >= '\uFE00' && c <= '\uFE0F'))
                continue;
            if (char.IsWhiteSpace(c)) { sb.Append(' '); continue; }
            sb.Append(c);
        }
        // Bỏ đuôi ':' / '：' / space (label thường "Quá trình:" còn dict "Quá trình").
        int len = sb.Length;
        while (len > 0)
        {
            char c = sb[len - 1];
            if (c == ':' || c == '\uFF1A' || c == ' ') { len--; continue; }
            break;
        }
        return len == sb.Length ? sb.ToString().ToUpperInvariant() : sb.ToString(0, len).ToUpperInvariant();
    }

    // LANG-41c: Map ngược English VALUE → FE KEY, build từ dict English CỦA GAME
    // (mpLocalisedTexts — đã đọc bằng reflection từ trước, 100% data-driven).
    // Cho phép restore về official đúng dù OriginalTextCache chỉ giữ English text.
    public static readonly Dictionary<string, string> GameEnglishValueToKey =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public static readonly Dictionary<string, string> GameEnglishValueToKeyNorm =
        new Dictionary<string, string>(StringComparer.Ordinal);


    // Value (mọi ngôn ngữ official) → FE key — heal Options khi đổi official↔official
    // Value (mọi ngôn ngữ official) → FE key — heal Options khi đổi official↔official
    public static readonly Dictionary<string, string> AnyOfficialValueToKey =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public static readonly Dictionary<string, string> AnyOfficialValueToKeyNorm =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public static void RebuildAnyOfficialValueToKey()
    {
        AnyOfficialValueToKey.Clear();
        AnyOfficialValueToKeyNorm.Clear();
        try
        {
            var all = _fiMpLocalisedTexts?.GetValue(null)
                as Dictionary<string, Dictionary<string, string>>;
            if (all == null) return;

            foreach (var langKv in all)
            {
                if (IsCustomLanguageFolder(langKv.Key)) continue;
                var d = langKv.Value;
                if (d == null) continue;

                foreach (var kv in d)
                {
                    if (!ConfigManager.IsFeKey(kv.Key)) continue;
                    string v = kv.Value;
                    if (string.IsNullOrEmpty(v)) continue;
                    string t = v.Trim();
                    if (t.Length == 0) continue;

                    if (!AnyOfficialValueToKey.ContainsKey(t))
                        AnyOfficialValueToKey[t] = kv.Key;

                    string norm = NormalizeForMatch(t);
                    if (norm.Length > 0 && !AnyOfficialValueToKeyNorm.ContainsKey(norm))
                        AnyOfficialValueToKeyNorm[norm] = kv.Key;
                }
            }
            if (VerboseLogging != null && VerboseLogging.Value)
                Debug.Log("[Localizer] AnyOfficialValueToKey: " + AnyOfficialValueToKey.Count);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[Localizer] RebuildAnyOfficialValueToKey: " + ex.Message);
        }
    }


    // LANG-42: Cache dict English của game (đọc reflection khi cần, tránh GetValue mỗi call).
    static Dictionary<string, string> _gameEnglishDict;
    static readonly FieldInfo _fiMpLocalisedTexts = typeof(CLocalisationManager).GetField(
        "mpLocalisedTexts", BindingFlags.NonPublic | BindingFlags.Static);

    public static Dictionary<string, string> GetGameEnglishDict()
    {
        if (_gameEnglishDict != null) return _gameEnglishDict;
        try
        {
            var all = _fiMpLocalisedTexts?.GetValue(null)
                as Dictionary<string, Dictionary<string, string>>;
            if (all == null) return null;
            foreach (var kv in all)
            {
                if (kv.Key.Equals(ConfigManager.ReferenceLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    _gameEnglishDict = kv.Value;
                    break;
                }
            }
        }
        catch { _gameEnglishDict = null; }
        return _gameEnglishDict;
    }

    // LANG-42: GetText trả nguyên tag khi miss HOẶC value rỗng (repack) — tra giá trị
    // English của key từ dict English của game để không bao giờ hiển thị key thô.
    // KHÔNG gọi GetText ở đây (tránh đệ quy qua Harmony postfix).
    public static string ResolveOfficialMiss(string tagName, string current)
    {
        try
        {
            var eng = GetGameEnglishDict();
            if (eng == null) return current;
            string t = tagName.Trim();
            if (t.Length == 0) return current;
            string v;
            if (eng.TryGetValue(t, out v) && !string.IsNullOrEmpty(v) && v.Trim().Length > 0) return v;
            if (eng.TryGetValue(t.ToLowerInvariant(), out v) && !string.IsNullOrEmpty(v) && v.Trim().Length > 0) return v;
            return current;
        }
        catch { return current; }
    }

    // ===================== /FIX LANG-40/41/42 =====================

    // ===================== FONT =====================
    public static Dictionary<string, Font> CustomFonts =
    new Dictionary<string, Font>(StringComparer.OrdinalIgnoreCase);

    // UIFont proxy tạo runtime (ý tưởng C)
    public static Dictionary<string, UIFont> ReplacementUIFonts =
        new Dictionary<string, UIFont>(StringComparer.OrdinalIgnoreCase);

    public static HashSet<string> SeenGameFonts =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public static HashSet<string> LoggedFontMaps =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    static readonly FieldInfo FiUIFontDynamic =
        typeof(UIFont).GetField("mDynamicFont", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly FieldInfo FiUIFontDynamicSize =
        typeof(UIFont).GetField("mDynamicFontSize", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly FieldInfo FiUIFontDynamicStyle =
        typeof(UIFont).GetField("mDynamicFontStyle", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly FieldInfo FiLabelFont =
        typeof(UILabel).GetField("mFont", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly FieldInfo FiUIFontReplacement =
        typeof(UIFont).GetField("mReplacement", BindingFlags.Instance | BindingFlags.NonPublic);

    static UIFont GetOrCreateProxyUIFont(string gameKey, Font unityFont, int defaultSize, FontStyle style)
    {
        // Không tạo proxy nữa — tránh mọi call sót.
        Debug.LogWarning("[Localizer] GetOrCreateProxyUIFont bị gọi nhưng đã disable (dropdown fix)");
        return null;
    }

    // FIX v0.29.3: helper dùng chung — nhận diện UILabel thuộc về UIPopupList
    // (dropdown item HOẶC control mở dropdown). Trước đây chỉ RefreshAllLabels()
    // có check này (v0.29.2) — các chỗ set mDynamicFont khác (Prefix OnEnable,
    // PrepareLabelFontBeforeEnable, TryApplyFontToLabelHard) không skip, nên vẫn
    // đụng vào UIFont/label của popup → gây lỗi render (xem IsPopupRelated dùng bên dưới).

    public static bool IsPopupRelated(UILabel lab)
    {
        if (lab == null) return false;
        if (lab.GetComponent<UIPopupList>() != null) return true;
        // FIX LANG-21 (dropdown trống): game Plague Inc dùng UIDropdownPopupList
        // (class riêng, KHÔNG phải UIPopupList của NGUI) cho mọi dropdown trong
        // options — kể cả dropdown chọn ngôn ngữ. Trước đây chỉ check UIPopupList
        // → mọi label của dropdown (item, title, header) bị coi là label thường:
        // mod áp font custom trực tiếp + RefreshAllLabels dịch đè text item.
        // Khi teardown (đổi sang ngôn ngữ không có font custom), các label này
        // giữ tham chiếu Font đã Destroy → dropdown render TRỐNG. Giờ nhận diện
        // đúng cả 2 loại dropdown — mod không bao giờ đụng label của dropdown.
        if (lab.GetComponent<UIDropdownPopupList>() != null) return true;
        Transform t = lab.transform;
        while (t != null)
        {
            if (t.GetComponent<UIPopupList>() != null) return true;
            if (t.GetComponent<UIDropdownPopupList>() != null) return true;
            t = t.parent;
        }
        return false;
    }

    // Cache optionCurrent labels — FindObjectsOfTypeAll mỗi label = treo game (log dừng
    // ở ForceRefreshAutotranslate ~1500 labels).
    static HashSet<UILabel> _optsSelectorLabelCache;
    static float _optsSelectorLabelCacheAt = -100f;
    const float OPTS_SELECTOR_CACHE_TTL = 0.5f;

    public static void ClearOptionsSelectorLabelCache()
    {
        _optsSelectorLabelCache = null;
        _optsSelectorLabelCacheAt = -100f;
    }

    static HashSet<UILabel> GetOptionsSelectorLabels()
    {
        if (_optsSelectorLabelCache != null
            && Time.unscaledTime - _optsSelectorLabelCacheAt < OPTS_SELECTOR_CACHE_TTL)
            return _optsSelectorLabelCache;

        var set = new HashSet<UILabel>();
        try
        {
            var sels = Resources.FindObjectsOfTypeAll(typeof(OptionsSelector)) as OptionsSelector[];
            if (sels != null)
            {
                for (int i = 0; i < sels.Length; i++)
                {
                    var s = sels[i];
                    if (s == null || s.optionCurrent == null) continue;
                    if (!FontRestore.IsLiveSceneObject(s)) continue;
                    UILabel lab = null;
                    try { lab = s.optionCurrent.GetComponent<UILabel>(); } catch { }
                    if (lab != null) set.Add(lab);
                }
            }
        }
        catch { }
        _optsSelectorLabelCache = set;
        _optsSelectorLabelCacheAt = Time.unscaledTime;
        return set;
    }

    public static bool IsOptionsSelectorRelated(UILabel lab)
    {
        if (lab == null) return false;
        try
        {
            if (lab.GetComponentInParent<OptionsSelector>() != null)
                return true;
            return GetOptionsSelectorLabels().Contains(lab);
        }
        catch { return false; }
    }

    /// <summary>
    /// OptionsSelector "chết": prefab clone trên nút Reset (UIButton), không phải
    /// hàng chọn < giá trị >. Vanilla OnEnable cố tình KHÔNG Set() khi
    /// mnCurrent==0 nên caption FE_Reset_All_Progress sống sót. Patch Set(0)
    /// của mod đè caption đó bằng optionLoc[0] = SIMPLE → "Đơn giản".
    /// </summary>
    public static bool IsDeadOptionsSelector(OptionsSelector s)
    {
        if (s == null || s.gameObject == null) return true;
        try
        {
            Transform t = s.transform;
            int hops = 0;
            while (t != null && hops < 8)
            {
                string n = t.name;
                if (!string.IsNullOrEmpty(n)
                    && n.IndexOf("Reset", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
                t = t.parent;
                hops++;
            }

            // Selector thật luôn có 2 nút mũi tên. Nút Reset chỉ là UIButton.
            if (s.optionNext == null || s.optionPrev == null) return true;
            if (s.optionEnum == null || s.optionEnum.Count < 2) return true;

            // Cùng GameObject vừa là UIButton (generalReset) vừa leftover OptionsSelector.
            if (s.GetComponent<UIButton>() != null
                && s.GetComponent<OptionsSelector>() != null
                && (s.optionNext == null || !s.optionNext.gameObject.activeSelf
                    || s.optionPrev == null || !s.optionPrev.gameObject.activeSelf))
                return true;
        }
        catch { return true; }
        return false;
    }

    /// <summary>
    /// Label đang mang FE key KHÔNG thuộc optionLoc của selector này
    /// (điển hình: FE_Reset_All_Progress trên optionCurrent leftover).
    /// </summary>
    public static bool OptionsSelectorWouldClobberForeignKey(OptionsSelector s, string currentText)
    {
        if (s == null || s.optionLoc == null || string.IsNullOrEmpty(currentText)) return false;
        string t = currentText.Trim();
        if (t.Length == 0) return false;
        if (!ConfigManager.IsFeKey(t) && t.IndexOf("FE_Reset", StringComparison.OrdinalIgnoreCase) < 0)
            return false;
        for (int i = 0; i < s.optionLoc.Count; i++)
        {
            if (string.Equals(s.optionLoc[i], t, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }
    public static bool FontsReady = false;
    public static bool ApplyingFont = false;
    static GameObject FontProxyHost;


    // FIX S-05 → CFG-08: danh sách font Windows critical chuyển sang
    // ConfigManager ([Fonts] CriticalSystemFonts) — không còn hardcode trong code.

    static void LoadFontsFromJson(string fontDir)
    {
        string jsonPath = Path.Combine(fontDir, ConfigManager.JsonConfigFileName); // CFG-08
        if (!File.Exists(jsonPath)) return;

        // parse tối giản, không cần Newtonsoft
        string json = File.ReadAllText(jsonPath);
        // CFG-08: bundle name mặc định lấy từ config ([Fonts] DefaultBundleName).
        string bundleName = ReadJsonString(json, "bundle") ?? ConfigManager.DefaultBundleName;

        // FIX S-04: sanitize bundleName — không cho phép path separator.
        if (string.IsNullOrEmpty(bundleName) ||
            bundleName.IndexOfAny(new[] { '/', '\\', ':', '*' }) >= 0 ||
            bundleName == ".." || bundleName.Contains(".."))
        {
            Debug.LogError("[Localizer] Bundle name invalid: " + bundleName);
            return;
        }

        string bundlePath = Path.Combine(fontDir, bundleName);
        if (!File.Exists(bundlePath))
            bundlePath = Path.Combine(Paths.PluginPath, bundleName);
        if (!File.Exists(bundlePath))
        {
            Debug.LogWarning("[Localizer] Không thấy bundle: " + bundlePath);
            return;
        }

        // FIX S-04: verify final path nằm trong fontDir hoặc Paths.PluginPath.
        try
        {
            string fullFontDir = Path.GetFullPath(fontDir);
            string fullPlugin = Path.GetFullPath(Paths.PluginPath);
            string fullBundle = Path.GetFullPath(bundlePath);
            string dirOfBundle = Path.GetDirectoryName(fullBundle) ?? "";
            if (!dirOfBundle.Equals(fullFontDir, StringComparison.OrdinalIgnoreCase)
                && !dirOfBundle.Equals(fullPlugin, StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogError("[Localizer] Bundle path escape: " + fullBundle);
                return;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[Localizer] Path resolve: " + ex.Message);
            return;
        }

        AssetBundle ab = AssetBundle.LoadFromFile(bundlePath);
        if (ab == null)
        {
            Debug.LogWarning("[Localizer] Load bundle fail: " + bundlePath);
            return;
        }

        FontBd = LoadSlot(ab, json, "Bd");
        FontMd = LoadSlot(ab, json, "Md");
        FontLt = LoadSlot(ab, json, "Lt");

        FontsReady = FontBd != null || FontMd != null || FontLt != null;
        Debug.Log("[Localizer] Bundle OK Bd=" + (FontBd != null ? FontBd.name : "null")
            + " Md=" + (FontMd != null ? FontMd.name : "null")
            + " Lt=" + (FontLt != null ? FontLt.name : "null"));

        ab.Unload(false); // giữ Font objects
    }

    // FIX LANG-09: scan có ý thức escape — giá trị "a\"b" phải kết thúc ở quote
    // sau b (quote bị escape không được tính là kết thúc chuỗi), đồng thời unescape
    // \" \\ \/ \n \t \r \uXXXX về giá trị thật. Trước đây trả substring thô:
    // mọi escape JSON hiển thị nguyên literal (vd asset "Font\u1ea2Bd" không match
    // asset thật trong bundle) và value chứa \" bị cắt sai vị trí.
    static string ReadJsonString(string json, string key)
    {
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return null;
        string pat = "\"" + key + "\"";
        int i = json.IndexOf(pat, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        int colon = json.IndexOf(':', i + pat.Length);
        if (colon < 0) return null;
        int q1 = json.IndexOf('"', colon + 1);
        if (q1 < 0) return null;
        var sb = new StringBuilder();
        int j = q1 + 1;
        while (j < json.Length)
        {
            char c = json[j];
            if (c == '\\' && j + 1 < json.Length)
            {
                char nx = json[j + 1];
                if (nx == 'n') sb.Append('\n');
                else if (nx == 't') sb.Append('\t');
                else if (nx == 'r') sb.Append('\n');
                else if (nx == 'u')
                {
                    int cp = ParseHex4(json, j + 2);
                    if (cp >= 0) { sb.Append((char)cp); j += 4; }
                    else sb.Append(nx);
                }
                else sb.Append(nx); // \" \\ \/ \b \f — trả ký tự sau backslash
                j += 2;
                continue;
            }
            if (c == '"') break;
            sb.Append(c);
            j++;
        }
        return sb.ToString();
    }
    static Font LoadSlot(AssetBundle ab, string json, string slot)
    {
        string assetName = ReadJsonString(json, slot);
        if (string.IsNullOrEmpty(assetName)) return null;
        Font f = ab.LoadAsset<Font>(assetName);
        if (f == null)
            Debug.LogWarning("[Localizer] Bundle thiếu asset '" + assetName + "' cho slot " + slot);
        return f;
    }

    // Unity 2018.2+ rasterize dynamic font qua DirectWrite. FR_PRIVATE (0x10)
    // chỉ hiện với GDI — DirectWrite không thấy → glyph fallback font hệ thống.
    // Flag 0 = đăng ký session Windows (DirectWrite thấy được). Gỡ bằng
    // RemoveFontResourceEx cùng flag ở OnDestroy / Application.quitting / ProcessExit.
    const uint FONT_ADD_FLAGS = 0;

    // FIX C-01: platform guard cho P/Invoke native.
    static readonly bool _isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    static extern int AddFontResourceEx(string lpszFilename, uint fl, IntPtr pdv);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    static extern bool RemoveFontResourceEx(string lpFileName, uint fl, IntPtr pdv);

    // FIX C-01: wrapper có platform guard.
    static int TryAddFontResourceEx(string path, uint flags)
    {
        if (!_isWindows)
        {
            Debug.LogWarning("[Localizer] AddFontResourceEx skip (not Windows): " + path);
            return 0;
        }
        try { return AddFontResourceEx(path, flags, IntPtr.Zero); }
        catch (Exception ex)
        {
            Debug.LogWarning("[Localizer] AddFontResourceEx error: " + ex.Message);
            return 0;
        }
    }

    static bool TryRemoveFontResourceEx(string path, uint flags)
    {
        if (!_isWindows) return false;
        try { return RemoveFontResourceEx(path, flags, IntPtr.Zero); }
        catch (Exception ex)
        {
            Debug.LogWarning("[Localizer] RemoveFontResourceEx error: " + ex.Message);
            return false;
        }
    }

    // Sau khi AddFontResourceEx thành công, Windows KHÔNG tự cập nhật bảng font nội bộ
    // cho các subsystem khác — theo tài liệu chính thức của Microsoft, ứng dụng PHẢI tự
    // gửi WM_FONTCHANGE broadcast thì các API tra cứu font theo tên (kể cả của Unity)
    // mới "thấy" font vừa thêm.
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    const int HWND_BROADCAST = 0xffff;
    const uint WM_FONTCHANGE = 0x001D;
    const uint SMTO_ABORTIFHUNG = 0x0002;

    static void BroadcastFontChange()
    {
        try
        {
            // FIX ST-03: giảm timeout 2000ms → 250ms.
            // FIX S-03: log audit trail.
            Debug.Log("[Localizer] Broadcasting WM_FONTCHANGE to all windows");
            SendMessageTimeout((IntPtr)HWND_BROADCAST, WM_FONTCHANGE,
                IntPtr.Zero, IntPtr.Zero,
                SMTO_ABORTIFHUNG, 250, out _);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[Localizer] BroadcastFontChange lỗi: " + ex.Message);
        }
    }

    static List<string> PrivateFontPaths = new List<string>();



    // ===================== STATE =====================
    // FIX CR-03 (race condition): cờ báo LoadAllLanguages() đã chạy xong ít nhất 1 lần.
    // WaitForGameUiThenReload phải chờ cờ này TRƯỚC khi gọi ForceCustomLanguage —
    // trước đây nếu ActiveLanguage được set rất sớm (máy nhanh), ForceCustomLanguage
    // chạy TRƯỚC LoadAllLanguages (Invoke 2s) → BindActiveLanguagePack load font xong
    // thì LoadAllLanguages lại wipe FontBd/Md/Lt + FontsReady=false → mọi UILabel
    // spawn sau đó không được áp font tiếng Việt (font "mất" sau khi vào game).
    public static bool LanguagesLoaded = false;

    public static Font FontBd;
    public static Font FontMd;
    public static Font FontLt;
    public static string LanguageFolder;
    public static string CurrentCustomLanguage = null;
    public static string CurrentScenarioId = null;

    static void EnsureFontManifest(string fontDir)
    {
        string path = Path.Combine(fontDir, ConfigManager.ManifestFileName); // CFG-08
        if (!Directory.Exists(fontDir)) return;

        // FIX P-05: enumerate 1 lần, cache kết quả.
        var currentFontFiles = new List<string>();
        var currentFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.GetFiles(fontDir, "*.*"))
        {
            string ext = Path.GetExtension(f);
            if (ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".otf", StringComparison.OrdinalIgnoreCase))
            {
                currentFontFiles.Add(f);
                currentFiles.Add(Path.GetFileName(f));
            }
        }

        if (File.Exists(path))
        {
            bool stale = false;
            try
            {
                var manifestFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = (raw ?? "").Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string rest = line.Substring(eq + 1).Trim();
                    int pipe = rest.IndexOf('|');
                    string fileName = (pipe >= 0 ? rest.Substring(0, pipe) : rest).Trim();
                    if (!string.IsNullOrEmpty(fileName)) manifestFiles.Add(fileName);
                }

                stale = !manifestFiles.SetEquals(currentFiles);

                if (!stale)
                {
                    DateTime manifestTime = File.GetLastWriteTimeUtc(path);
                    foreach (var fn in currentFiles)
                    {
                        if (File.GetLastWriteTimeUtc(Path.Combine(fontDir, fn)) > manifestTime)
                        {
                            stale = true;
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Localizer] Kiểm tra fonts.txt cũ lỗi: " + ex.Message);
            }

            if (!stale) return;

            Debug.LogWarning("[Localizer] fonts.txt không khớp với file .ttf/.otf hiện tại trong "
                + fontDir + " (đã đổi font mà chưa xoá manifest cũ) → tự tạo lại");
            try { File.Delete(path); } catch { }
        }

        var lines = new List<string>();
        lines.Add("# Auto-generated — sửa tay osFontName nếu Unity load sai");
        lines.Add("# gameKey = fileName | osFontName");

        // FIX P-05: dùng currentFontFiles thay vì Directory.GetFiles lần 2.
        foreach (var fontPath in currentFontFiles)
        {
            string fileName = Path.GetFileName(fontPath);
            string baseName = Path.GetFileNameWithoutExtension(fontPath);
            string key = GuessGameKey(baseName); // Bd / Md / Lt / null
            if (key == null) continue;

            string family = TryReadFontFamily(fontPath); // name table
            if (string.IsNullOrEmpty(family))
                family = baseName; // fallback

            lines.Add(key + " = " + fileName + " | " + family);
        }

        // FIX S-08: verify path không phải symlink + catch exception cụ thể.
        try
        {
            var fi = new FileInfo(path);
            if ((fi.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                Debug.LogWarning("[Localizer] fonts.txt là symlink, skip write: " + path);
                return;
            }
            File.WriteAllLines(path, lines.ToArray(), Encoding.UTF8);
            Debug.Log("[Localizer] Đã auto-generate " + path);
        }
        catch (UnauthorizedAccessException ex)
        {
            Debug.LogWarning("[Localizer] Không có quyền ghi fonts.txt: " + ex.Message);
        }
        catch (IOException ex)
        {
            Debug.LogWarning("[Localizer] IO error ghi fonts.txt: " + ex.Message);
        }
    }

    static string GuessGameKey(string baseName)
    {
        string n = baseName;
        if (n.IndexOf("Bd", StringComparison.OrdinalIgnoreCase) >= 0 ||
            n.IndexOf("_Bd", StringComparison.OrdinalIgnoreCase) >= 0 ||
            n.IndexOf("Bold", StringComparison.OrdinalIgnoreCase) >= 0)
            return "Bd";
        if (n.IndexOf("Md", StringComparison.OrdinalIgnoreCase) >= 0 ||
            n.IndexOf("_Md", StringComparison.OrdinalIgnoreCase) >= 0 ||
            n.IndexOf("Medium", StringComparison.OrdinalIgnoreCase) >= 0)
            return "Md";
        if (n.IndexOf("_Lt", StringComparison.OrdinalIgnoreCase) >= 0 ||
            n.IndexOf("-Lt", StringComparison.OrdinalIgnoreCase) >= 0 ||
            n.IndexOf("Light", StringComparison.OrdinalIgnoreCase) >= 0)
            return "Lt";
        return null;
    }

    // FIX S-02: verify file là TTF/OTF hợp lệ trước khi AddFontResourceEx.
    static bool IsValidFontFile(string path)
    {
        try
        {
            using (var fs = File.OpenRead(path))
            {
                byte[] header = new byte[4];
                if (fs.Read(header, 0, 4) != 4) return false;
                if (header[0] == 0x00 && header[1] == 0x01 &&
                    header[2] == 0x00 && header[3] == 0x00) return true;  // TrueType
                if (header[0] == (byte)'O' && header[1] == (byte)'T' &&
                    header[2] == (byte)'T' && header[3] == (byte)'O') return true;  // OpenType CFF
                if (header[0] == (byte)'t' && header[1] == (byte)'r' &&
                    header[2] == (byte)'u' && header[3] == (byte)'e') return true;  // Apple legacy
                return false;
            }
        }
        catch { return false; }
    }

    static void LoadFontsFromManifest(string fontDir, string manifestPath)
    {
        string[] lines;
        try { lines = File.ReadAllLines(manifestPath, Encoding.UTF8); }
        catch (Exception ex)
        {
            Debug.LogWarning("[Localizer] Đọc fonts.txt lỗi: " + ex.Message);
            return;
        }

        for (int li = 0; li < lines.Length; li++)
        {
            string line = (lines[li] ?? "").Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;

            int eq = line.IndexOf('=');
            if (eq < 0) continue;

            string gameKey = line.Substring(0, eq).Trim(); // Bd / Md / Lt
            string rest = line.Substring(eq + 1).Trim();
            string fileName = rest;
            string osName = rest;

            int pipe = rest.IndexOf('|');
            if (pipe >= 0)
            {
                fileName = rest.Substring(0, pipe).Trim();
                osName = rest.Substring(pipe + 1).Trim();
            }

            if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(osName))
                continue;

            string path = Path.Combine(fontDir, fileName);
            if (!File.Exists(path))
            {
                Debug.LogWarning("[Localizer] Manifest thiếu file: " + path);
                continue;
            }

            // FIX S-02: verify path không phải symlink và là font hợp lệ.
            try
            {
                var fi = new FileInfo(path);
                if ((fi.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    Debug.LogWarning("[Localizer] Skip symlink font: " + fileName);
                    continue;
                }
            }
            catch { continue; }

            if (!IsValidFontFile(path))
            {
                Debug.LogWarning("[Localizer] Not valid TTF/OTF: " + fileName);
                continue;
            }

            // Đăng ký đúng file này
            try
            {
                int n = TryAddFontResourceEx(path, FONT_ADD_FLAGS);
                if (n > 0)
                {
                    PrivateFontPaths.Add(path);
                    BroadcastFontChange();
                    Debug.Log("[Localizer] Private font OK: " + fileName);
                }
                else
                {
                    // FIX ST-04: AddFontResourceEx fail → skip slot.
                    Debug.LogError("[Localizer] AddFontResourceEx FAIL cho " + fileName
                        + " — skip slot " + gameKey);
                    continue;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Localizer] Private font error: " + ex.Message);
                continue;
            }

            // Hỏi đúng osName trong manifest (không đoán tên file)
            Font font = null;
            string used = null;

            // FIX v0.28.2: removed "Be Vietnam Pro" hardcoded fallback
            // — osName should come from fonts.txt manifest, not hardcoded.
            string[] tryOs = { osName, osName.Replace("-", " "), Path.GetFileNameWithoutExtension(fileName) };
            for (int t = 0; t < tryOs.Length; t++)
            {
                if (string.IsNullOrEmpty(tryOs[t])) continue;
                // FIX S-05: cảnh báo khi osName khớp system font critical.
                if (ConfigManager.CriticalSystemFonts.Contains(tryOs[t])) // CFG-08
                    Debug.LogWarning("[Localizer] osName '" + tryOs[t]
                        + "' là system font critical — mod sẽ override UI gốc");
                try
                {
                    Font f = Font.CreateDynamicFontFromOSFont(tryOs[t], 16);
                    if (f != null) { font = f; used = tryOs[t]; break; }
                }
                catch { }
            }

            if (font == null)
            {
                Debug.LogWarning("[Localizer] Manifest không tạo Font: key=" + gameKey
                    + " file=" + fileName + " os='" + osName + "'");
                continue;
            }

            font.RequestCharactersInTexture("ABCĐĂÂÊÔƠƯMNmn", 24, FontStyle.Normal);
            Texture tex = font.material != null ? font.material.mainTexture : null;
            Debug.Log("[Localizer] FONT TEX " + used
                + " tex=" + (tex != null ? tex.width + "x" + tex.height + " id=" + tex.GetInstanceID() : "NULL"));

            // Map Bd/Md/Lt → key game quen thuộc
            string fullKey = gameKey;
            if (gameKey.Equals("Bd", StringComparison.OrdinalIgnoreCase)) FontBd = font;
            else if (gameKey.Equals("Md", StringComparison.OrdinalIgnoreCase)) FontMd = font;
            else if (gameKey.Equals("Lt", StringComparison.OrdinalIgnoreCase)) FontLt = font;

            CustomFonts[fullKey] = font;
            CustomFonts[NormalizeFontName(fullKey)] = font;
            CustomFonts[NormalizeFontName(osName)] = font;
            CustomFonts[NormalizeFontName(used)] = font;
            if (font.fontNames != null)
            {
                for (int i = 0; i < font.fontNames.Length; i++)
                    CustomFonts[NormalizeFontName(font.fontNames[i])] = font;
            }

            // FIX v0.28.2: Removed hardcoded font aliases (HelveticaNeueLTStd_*).
            // Font mapping should come entirely from fonts.txt manifest — data-driven.

            string faces = font.fontNames != null ? string.Join("|", font.fontNames) : "?";
            Debug.Log("[Localizer] Manifest OK " + gameKey
                + " file=" + fileName
                + " → OS '" + used + "'"
                + " id=" + font.GetInstanceID()
                + " fontNames=[" + faces + "]");
        }
    }
    /// <summary>
    /// Đọc family name từ bảng 'name' trong TTF/OTF (không cần OS).
    /// Ưu tiên Windows platform (3), nameID 1 = Family, 4 = Full.
    /// </summary>
    static string TryReadFontFamily(string fontPath)
    {
        try
        {
            // FIX S-06: skip file > 50MB — TTF/OTF hợp lệ hiếm khi > 10MB.
            var fi = new FileInfo(fontPath);
            if (fi.Length > 50L * 1024 * 1024)
            {
                Debug.LogWarning("[Localizer] Font quá lớn, skip: " + fontPath);
                return null;
            }
            byte[] data = File.ReadAllBytes(fontPath);
            if (data.Length < 12) return null;

            // TTC không hỗ trợ ở đây
            if (data[0] == (byte)'t' && data[1] == (byte)'t' && data[2] == (byte)'c' && data[3] == (byte)'f')
                return null;

            ushort numTables = ReadU16BE(data, 4);
            int offset = 12;
            int nameOff = -1;
            int nameLen = 0;

            for (int i = 0; i < numTables; i++)
            {
                if (offset + 16 > data.Length) break;
                string tag = Encoding.ASCII.GetString(data, offset, 4);
                // skip checkSum
                int off = ReadU32BE(data, offset + 8);
                int len = ReadU32BE(data, offset + 12);
                if (tag == "name")
                {
                    nameOff = off;
                    nameLen = len;
                    break;
                }
                offset += 16;
            }

            // FIX LANG-07: guard tràn số nguyên khi font hỏng/hostile khai offset
            // hoặc length lớn (nameOff + nameLen có thể wrap thành số âm lọt check).
            if (nameOff < 0 || nameLen < 0 || nameOff > data.Length - nameLen) return null;

            int baseOff = nameOff;
            ushort format = ReadU16BE(data, baseOff);
            ushort count = ReadU16BE(data, baseOff + 2);
            ushort stringOffset = ReadU16BE(data, baseOff + 4);

            string family = null;
            string full = null;
            string postscript = null;

            for (int i = 0; i < count; i++)
            {
                // FIX LANG-07: guard biên an toàn tràn số nguyên cho từng record.
                int rec = baseOff + 6 + i * 12;
                if (rec < 0 || rec > data.Length - 12) break;

                ushort platformID = ReadU16BE(data, rec);
                ushort nameID = ReadU16BE(data, rec + 6);
                ushort length = ReadU16BE(data, rec + 8);
                ushort so = ReadU16BE(data, rec + 10);

                int strPos = baseOff + stringOffset + so;
                if (strPos < 0 || strPos > data.Length - length) continue;

                string s;
                // FIX LANG-07: trước đây Encoding.GetEncoding(1252) nằm TRỰC TIẾP trong
                // vòng lặp — trên runtime thiếu codepage nó ném NotSupportedException và
                // exception này văng ra ngoài, làm MẤT family name của mọi record sau
                // (kể cả record Windows UTF-16 hoàn toàn hợp lệ). Giờ mỗi record được
                // try/catch riêng + có fallback decode khi không lấy được codepage 1252.
                try
                {
                    if (platformID == 3) // Windows: UTF-16 BE
                    {
                        s = Encoding.BigEndianUnicode.GetString(data, strPos, length);
                    }
                    else if (platformID == 1) // Mac: often Roman
                    {
                        Encoding mac = GetMacRomanEncodingOnce();
                        s = mac != null
                            ? mac.GetString(data, strPos, length)
                            : DecodeBytesLatinish(data, strPos, length);
                    }
                    else
                        continue;
                }
                catch
                {
                    // record này hỏng — bỏ qua, không làm hỏng cả bảng name.
                    continue;
                }

                s = (s ?? "").Trim();
                if (s.Length == 0) continue;

                if (nameID == 1) family = s;      // Font Family
                else if (nameID == 4) full = s;   // Full name
                else if (nameID == 6) postscript = s;
            }

            if (!string.IsNullOrEmpty(family)) return family;
            if (!string.IsNullOrEmpty(full)) return full;
            if (!string.IsNullOrEmpty(postscript)) return postscript;
            return null;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[Localizer] TryReadFontFamily: " + ex.Message);
            return null;
        }
    }

    static Encoding _macRomanEncoding;
    static bool _macRomanTried;

    /// <summary>
    /// Lấy codepage 1252 đúng 1 lần (cache). Trả null nếu runtime không có
    /// codepage này — caller dùng DecodeBytesLatinish làm fallback.
    /// </summary>
    static Encoding GetMacRomanEncodingOnce()
    {
        if (_macRomanTried) return _macRomanEncoding;
        _macRomanTried = true;
        try { _macRomanEncoding = Encoding.GetEncoding(1252); }
        catch { _macRomanEncoding = null; }
        return _macRomanEncoding;
    }

    /// <summary>
    /// Fallback decode Mac-Roman-ish: byte → char (ánh xạ Latin-1). Đủ đúng cho
    /// family name font (gần như ASCII) khi codepage 1252 không khả dụng.
    /// </summary>
    static string DecodeBytesLatinish(byte[] data, int pos, int len)
    {
        var sb = new StringBuilder(len);
        for (int i = 0; i < len; i++) sb.Append((char)data[pos + i]);
        return sb.ToString();
    }

    /// <summary>
    /// FIX LANG-07 (hoàn tất mục audit C-06 từng defer): cố gắng đăng ký
    /// CodePagesEncodingProvider qua reflection nếu assembly
    /// System.Text.Encoding.CodePages có mặt trên máy — cho phép
    /// Encoding.GetEncoding(1252) hoạt động trên runtime mặc định thiếu codepage.
    /// Không có assembly đó → no-op an toàn (không thêm reference build mới).
    /// </summary>
    public static void TryRegisterCodePages()
    {
        try
        {
            // FIX LANG-17: Type.GetType thay cho AccessTools.TypeByName — TypeByName
            // log Warning HarmonyX ("Could not find type") mỗi lần khởi động trên
            // mọi máy không có assembly System.Text.Encoding.CodePages (mặc định
            // của Unity Mono runtime — log thật 0.31.0 cho thấy vậy). Type.GetType
            // trả null im lặng — hành vi giữ nguyên (no-op an toàn), log sạch.
            var t = Type.GetType("System.Text.CodePagesEncodingProvider, System.Text.Encoding.CodePages");
            if (t == null)
                t = Type.GetType("System.Text.CodePagesEncodingProvider");
            if (t == null) return;
            var prop = t.GetProperty("Instance",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            var reg = typeof(Encoding).GetMethod("RegisterProvider");
            if (prop == null || reg == null) return;
            object provider = prop.GetValue(null, null);
            if (provider == null) return;
            reg.Invoke(null, new object[] { provider });
            Debug.Log("[Localizer] CodePagesEncodingProvider đã đăng ký — codepage 1252 khả dụng.");
        }
        catch { }
    }

    static ushort ReadU16BE(byte[] d, int i)
    {
        return (ushort)((d[i] << 8) | d[i + 1]);
    }

    static int ReadU32BE(byte[] d, int i)
    {
        return (d[i] << 24) | (d[i + 1] << 16) | (d[i + 2] << 8) | d[i + 3];
    }
    public static string LocalizeHardcodedUi(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        string key = s.Trim();

        if (IsCustomLanguageActive())
        {
            // Ưu tiên 1: text là key gốc (vd "FE_MainMenu_Play")
            if (TryGetTranslation(key, out string t) &&
                !string.IsNullOrEmpty(t) &&
                !string.Equals(t, key, StringComparison.OrdinalIgnoreCase))
                return t;

            // Chuỗi English cứng (vd title "Options"): ưu tiên text-key trong pack
            // ("Tùy chọn") — KHÔNG lấy FE reverse ("Cài Đặt") vì cùng English value
            // của FE_* khác nghĩa với title màn hình.
            if (EnglishTextDict.TryGetValue(key, out string packT) &&
                !string.IsNullOrEmpty(packT) &&
                !string.Equals(packT, key, StringComparison.Ordinal))
                return packT;

            if (EnglishToCustomDict.Count > 0 &&
                EnglishToCustomDict.TryGetValue(key, out string customT) &&
                !string.IsNullOrEmpty(customT))
                return customT;
        }

        try
        {
            string g = CLocalisationManager.GetText(key);
            if (!string.IsNullOrEmpty(g))
                return g;
        }
        catch { }

        return key; // fallback English key
    }

    /// <summary>
    /// LANG-23: dựng chỉ mục value→English sau mỗi lần bind dict.
    /// Dùng để cache LUÔN khóa nguồn (English/key), không bao giờ cache bản dịch.
    /// </summary>
    public static void RebuildTranslationIndex()
    {
        TranslatedValues.Clear();
        ValueToEnglish.Clear();
        // LANG-41: xoá + dựng lại index normalized cùng lúc (dùng cho lookup chịu
        // được ký tự vô hình / biến thể ':' trong file dịch).
        ValueToEnglishNormalized.Clear();
        AddIndexMap(EnglishToCustomDict);
        AddIndexMap(EnglishTextDict);
        AddIndexMap(TranslationDict);
        AddIndexMap(ScenarioDict);
    }

    static void AddIndexMap(Dictionary<string, string> map)
    {
        if (map == null) return;
        foreach (var kv in map)
        {
            if (string.IsNullOrEmpty(kv.Key) || string.IsNullOrEmpty(kv.Value)) continue;
            if (string.Equals(kv.Key, kv.Value, StringComparison.OrdinalIgnoreCase)) continue;
            TranslatedValues.Add(kv.Value);
            if (!ValueToEnglish.ContainsKey(kv.Value))
                ValueToEnglish[kv.Value] = kv.Key;
            // LANG-41: index normalized — value có thể chứa ký tự vô hình (U+180E,
            // U+2800...) khiến text trên label (đã bị NGUI/Unity xử lý) không khớp
            // exact. Chuẩn hoá cả 2 phía mới tra được ngược.
            string norm = NormalizeForMatch(kv.Value);
            if (norm.Length > 0 && !ValueToEnglishNormalized.ContainsKey(norm))
                ValueToEnglishNormalized[norm] = kv.Key;
        }
    }

    /// <summary>
    /// Nếu text đang là bản dịch custom, trả về English/key nguồn; nếu đã là key thì giữ nguyên.
    /// </summary>
    public static void CacheOriginalTextIfAbsent(UILabel lab, string sourceEnglish)
    {
        if (lab == null || string.IsNullOrEmpty(sourceEnglish)) return;
        try
        {
            if (OriginalTextCache.TryGetValue(lab, out string existing)
                && !string.IsNullOrEmpty(existing))
                return;
            OriginalTextCache.Add(lab, sourceEnglish);
        }
        catch
        {
            // ConditionalWeakTable: Add throws nếu đã có — bỏ qua
        }
    }

    public static string ResolveSourceEnglish(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        string t = text.Trim();
        // Đã là key / English
        if (EnglishToCustomDict.ContainsKey(t) || TranslationDict.ContainsKey(t)
            || EnglishTextDict.ContainsKey(t))
            return t;
        // Bản dịch custom → English — LANG-41: dùng lookup chịu được ký tự vô hình
        // (exact miss khi label/dict chênh nhau U+180E, U+2800, ':' cuối...).
        if (TryMapValueToEnglish(t, out string eng) && !string.IsNullOrEmpty(eng))
            return eng;
        return t;
    }

    // LANG-40: Lưu KEY NGUỒN của label (chỉ lần đầu — key là dữ liệu ổn định).
    public static void CacheLabelSourceKey(UILabel lab, string key)
    {
        if (lab == null || string.IsNullOrEmpty(key)) return;
        try
        {
            if (LabelSourceKeyCache.TryGetValue(lab, out string existing)
                && !string.IsNullOrEmpty(existing))
                return;
            LabelSourceKeyCache.Remove(lab);
            LabelSourceKeyCache.Add(lab, key);
        }
        catch { }
    }

    // LANG-40: Đọc KEY NGUỒN THÔ từ component UILabelAutotranslate (không dịch).
    // Trả null khi field đang giữ bản dịch custom / English value (bị poison) —
    // tuyệt đối KHÔNG cache giá trị đó làm key.
    static string TryGetLabelRawKey(UILabel lab)
    {
        if (lab == null) return null;
        try
        {
            if (_autotranslateType == null) ProbeAutoTranslateType();
            if (_autotranslateType == null || _autotranslateStringFields == null) return null;
            var comp = lab.GetComponent(_autotranslateType);
            if (comp == null) return null;

            string raw = null;
            for (int i = 0; i < _autotranslateStringFields.Length; i++)
            {
                string v = _autotranslateStringFields[i].GetValue(comp) as string;
                if (string.IsNullOrEmpty(v)) continue;
                string tv = v.Trim();
                if (tv.Length == 0) continue;
                // FE_* → chắc chắn key gốc UI chính — thắng ngay lập tức.
                if (ConfigManager.IsFeKey(tv)) return tv;
                // Non-FE: chỉ tin khi KHÔNG phải English value của key FE_* nào
                // (không thì là menu source) VÀ KHÔNG phải bản dịch custom đang lưu
                // trong dict ngược (component đã bị poison).
                if (raw == null
                    && !EnglishToCustomDict.ContainsKey(tv)
                    && !TryMapValueToEnglish(tv, out _))
                    raw = tv;
            }
            return raw;
        }
        catch { return null; }
    }

    /// <summary>
    /// LANG-33: Ép mọi UILabelAutotranslate dịch lại theo ActiveLanguage hiện tại.
    /// Tránh chrome Options/Pause kẹt bản Việt đã stamp khi đổi official↔official.
    /// </summary>
    public static void ForceRefreshAutotranslateLabels()
    {
        // Boot / màn trắng: không chạy full sync 1500 label. Caller nên dùng
        // ForceRefreshAutotranslateBatched. Sync chỉ an toàn sau LanguagesLoaded.
        if (!LanguagesLoaded)
        {
            Debug.Log("[Localizer] ForceRefreshAutotranslate sync skip (boot)");
            return;
        }
        try
        {
            ClearOptionsSelectorLabelCache();
            var autos = Resources.FindObjectsOfTypeAll(typeof(UILabelAutotranslate))
                as UILabelAutotranslate[];
            if (autos == null || autos.Length == 0)
            {
                Debug.Log("[Localizer] ForceRefreshAutotranslate: 0 component");
                return;
            }

            var fiOrig = AccessTools.Field(typeof(UILabelAutotranslate), "originalLabelText");
            var fiLang = AccessTools.Field(typeof(UILabelAutotranslate), "currentLanguage");
            var miSet = AccessTools.Method(typeof(UILabelAutotranslate), "SetInitialText",
                new Type[] { typeof(string), typeof(bool) });
            var miAuto = AccessTools.Method(typeof(UILabelAutotranslate), "AutoTranslate",
                Type.EmptyTypes);

            int n = 0;
            for (int i = 0; i < autos.Length; i++)
            {
                UILabelAutotranslate a = autos[i];
                if (a == null) continue;
                try
                {
                    if (!FontRestore.IsLiveSceneObject(a)) continue;
                    UILabel lab = a.GetComponent<UILabel>();
                    if (lab != null && IsPopupRelated(lab)) continue;
                    try { if (IsPopupRelated(lab)) continue; } catch { }
                    try { if (IsOptionsSelectorRelated(lab)) continue; } catch { }
                    try { if (!FontRestore.IsLiveSceneObject(lab)) continue; } catch { }

                    string orig = null;
                    if (fiOrig != null)
                    {
                        try { orig = fiOrig.GetValue(a) as string; } catch { }
                    }
                    if (string.IsNullOrEmpty(orig) && lab != null)
                        orig = lab.text;
                    if (string.IsNullOrEmpty(orig)) continue;

                    // LANG-40: heal component bị poison — originalLabelText đang giữ bản
                    // dịch custom / English value mà GetText không phân giải được (mỗi lần
                    // game chạy translateLabels[i].AutoTranslate() sẽ stamp LẠI text cũ →
                    // undo mọi fix của mod). Thay bằng KEY nguồn đã cache khi label còn
                    // nguyên vẹn. Vẫn capture key trước (nếu component còn lành).
                    CacheLabelSourceKey(lab, TryGetLabelRawKey(lab));

                    // LANG-45c: key đã capture lúc lành (cache) — ưu tiên hơn orig hiện tại.
                    string safeSrc = null;
                    if (lab != null)
                    {
                        if (LabelSourceKeyCache.TryGetValue(lab, out string ck)
                            && !string.IsNullOrEmpty(ck)
                            && ConfigManager.IsFeKey(ck)
                            && TryResolveOfficialLabelText(ck, out _))
                            safeSrc = ck;
                        else if (OriginalTextCache.TryGetValue(lab, out string ok)
                            && !string.IsNullOrEmpty(ok)
                            && ConfigManager.IsFeKey(ok)
                            && TryResolveOfficialLabelText(ok, out _))
                            safeSrc = ok;
                    }
                    // orig đã là FE_* SAI (poison) nhưng cache còn key đúng → khôi phục field.
                    if (!string.IsNullOrEmpty(safeSrc)
                        && ConfigManager.IsFeKey(orig)
                        && !string.Equals(orig, safeSrc, StringComparison.OrdinalIgnoreCase))
                    {
                        if (VerboseLogging != null && VerboseLogging.Value)
                            Debug.Log("[Localizer][DEBUG-HEAL-RECOVER] lab="
                                + (lab != null ? lab.gameObject.name : "null")
                                + " poisonedOrig=" + orig + " → safeSrc=" + safeSrc);
                        orig = safeSrc;
                        if (fiOrig != null)
                        {
                            try { fiOrig.SetValue(a, safeSrc); } catch { }
                        }
                        if (lab != null) CacheLabelSourceKey(lab, safeSrc);
                    }

                    bool origResolvable = TryResolveOfficialLabelText(orig, out _);

                    if (!origResolvable)
                    {
                        // LANG-45: reverse-map value→key (AnyOfficialValueToKey) first-wins
                        // toàn cục — "Simple"/"シンプル" map nhầm → nút Reset = "Đơn giản"
                        // vĩnh viễn. Chỉ PERSIST khi healKey từ cache FE_* đã xác nhận.
                        string healKey = null;
                        bool healKeySafe = false;

                        if (!string.IsNullOrEmpty(safeSrc))
                        {
                            healKey = safeSrc;
                            healKeySafe = true;
                        }
                        else if (lab != null
                            && LabelSourceKeyCache.TryGetValue(lab, out string cachedKey)
                            && !string.IsNullOrEmpty(cachedKey)
                            && TryResolveOfficialLabelText(cachedKey, out _))
                        {
                            healKey = cachedKey;
                            healKeySafe = ConfigManager.IsFeKey(cachedKey);
                        }
                        else if (TryMapAnyOfficialValueToKey(orig, out string mapped)
                                 && !string.IsNullOrEmpty(mapped))
                        {
                            healKey = mapped; // display-only
                        }
                        else if (lab != null && !string.IsNullOrEmpty(lab.text)
                                 && TryMapAnyOfficialValueToKey(lab.text, out string mapped2)
                                 && !string.IsNullOrEmpty(mapped2))
                        {
                            healKey = mapped2; // display-only
                        }

                        if (!string.IsNullOrEmpty(healKey))
                        {
                            if (VerboseLogging != null && VerboseLogging.Value)
                                Debug.Log("[Localizer][DEBUG-HEAL] lab="
                                    + (lab != null ? lab.gameObject.name : "null")
                                    + " orig=" + orig
                                    + " text=" + (lab != null ? lab.text : "null")
                                    + " → healKey=" + healKey
                                    + " safe=" + healKeySafe);

                            orig = healKey;
                            if (healKeySafe)
                            {
                                if (fiOrig != null)
                                {
                                    try { fiOrig.SetValue(a, healKey); } catch { }
                                }
                                if (lab != null) CacheLabelSourceKey(lab, healKey);
                            }
                        }
                    }

                    string eng = ResolveSourceEnglish(orig);
                    if (string.IsNullOrEmpty(eng)) eng = orig;

                    if (fiOrig != null && !string.Equals(orig, eng, StringComparison.Ordinal))
                    {
                        try { fiOrig.SetValue(a, eng); } catch { }
                    }
                    if (fiLang != null)
                    {
                        try { fiLang.SetValue(a, null); } catch { }
                    }

                    // Ưu tiên GetText trực tiếp — ít đụng internal game hơn Invoke hàng loạt
                    // FIX OPT-04: chỉ ghi khi GetText phân giải được GIÁ TRỊ THẬT (khác
                    // nguồn). GetText trả nguyên `eng` khi mọi dict miss (file ngôn ngữ
                    // repack thiếu entry / value rỗng) — code cũ gán nguyên `eng` = ĐÓNH
                    // KEY THÔ vào label (nguyên nhân chính màn Options hiện
                    // "FE_Options_*" sau khi đổi official↔custom vài lần). Khi không
                    // phân giải được → chỉ revert nếu text hiện tại là bản dịch custom
                    // kẹt VÀ nguồn không phải key (key miss toàn cục → giữ nguyên text,
                    // tự lành khi quay lại custom).
                    if (lab != null)
                    {
                        try
                        {
                            string cur = lab.text;
                            if (TryResolveOfficialLabelText(eng, out string loc))
                            {
                                if (!string.Equals(cur, loc, StringComparison.Ordinal))
                                { lab.text = loc; n++; }
                            }
                            else if (!string.IsNullOrEmpty(cur)
                                && ValueToEnglish.TryGetValue(cur.Trim(), out string stuckEng)
                                && !string.IsNullOrEmpty(stuckEng)
                                && !ConfigManager.IsFeKey(eng)
                                && !string.Equals(cur, eng, StringComparison.Ordinal))
                            {
                                lab.text = eng; // nguồn English thật — revert stuck-custom
                                n++;
                            }
                        }
                        catch { }
                    }
                    else if (miSet != null)
                    {
                        try { miSet.Invoke(a, new object[] { eng, false }); n++; } catch { }
                    }
                    else if (miAuto != null)
                    {
                        try { miAuto.Invoke(a, null); n++; } catch { }
                    }
                }
                catch { }
            }
            Debug.Log("[Localizer] ForceRefreshAutotranslate: " + n + " labels");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[Localizer] ForceRefreshAutotranslate: " + ex.Message);
        }
    }

    /// <summary>Gọi sau mọi lần về official (kể cả official→official).</summary>
    public static void OnSwitchedToOfficialLanguage(string value)
    {
        // Boot: game còn màn trắng / loading — Restore + ForceRefresh 1500 label
        // trong 1–2 frame = đơ trắng. Chỉ restore nhẹ; ForceRefresh sau khi LanguagesLoaded.
        try { RestoreLabelTextsToOfficial(); }
        catch (Exception ex)
        {
            Debug.LogWarning("[Localizer] RestoreLabelTextsToOfficial: " + ex.Message);
        }

        try
        {
            if (AboutMod_InjectButton_Patch._btn != null)
                AboutMod_InjectButton_Patch.ApplyButtonCaption(
                    AboutMod_InjectButton_Patch._btn.gameObject);
        }
        catch { }

        if (Instance == null) return;
        // Chưa load pack xong → bỏ ForceRefresh (tránh treo boot).
        if (!LanguagesLoaded)
        {
            Debug.Log("[Localizer] Skip ForceRefresh (boot, LanguagesLoaded=false)");
            return;
        }
        Instance.StartCoroutine(ForceRefreshAutotranslateBatched());
    }

    static IEnumerator ForceRefreshAutotranslateNextFrame()
    {
        yield return null;
        yield return ForceRefreshAutotranslateBatched();
    }

    /// <summary>
    /// ForceRefresh chia nhỏ theo frame — 40 component/frame để không đơ main thread
    /// (màn trắng / Options khi 1500+ UILabelAutotranslate).
    /// </summary>
    static IEnumerator ForceRefreshAutotranslateBatched()
    {
        const int batch = 40;
        UILabelAutotranslate[] autos = null;
        try
        {
            ClearOptionsSelectorLabelCache();
            autos = Resources.FindObjectsOfTypeAll(typeof(UILabelAutotranslate))
                as UILabelAutotranslate[];
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[Localizer] ForceRefresh batch list: " + ex.Message);
            yield break;
        }
        if (autos == null || autos.Length == 0)
        {
            Debug.Log("[Localizer] ForceRefreshAutotranslate: 0 component");
            yield break;
        }

        var fiOrig = AccessTools.Field(typeof(UILabelAutotranslate), "originalLabelText");
        var fiLang = AccessTools.Field(typeof(UILabelAutotranslate), "currentLanguage");
        var miSet = AccessTools.Method(typeof(UILabelAutotranslate), "SetInitialText",
            new Type[] { typeof(string), typeof(bool) });
        var miAuto = AccessTools.Method(typeof(UILabelAutotranslate), "AutoTranslate",
            Type.EmptyTypes);

        int n = 0;
        int processed = 0;
        for (int i = 0; i < autos.Length; i++)
        {
            UILabelAutotranslate a = autos[i];
            if (a == null) continue;
            try
            {
                if (!FontRestore.IsLiveSceneObject(a)) continue;
                UILabel lab = a.GetComponent<UILabel>();
                if (lab != null)
                {
                    if (IsPopupRelated(lab)) continue;
                    if (IsOptionsSelectorRelated(lab)) continue;
                    if (!FontRestore.IsLiveSceneObject(lab)) continue;
                }

                string orig = null;
                if (fiOrig != null)
                {
                    try { orig = fiOrig.GetValue(a) as string; } catch { }
                }
                if (string.IsNullOrEmpty(orig) && lab != null)
                    orig = lab.text;
                if (string.IsNullOrEmpty(orig)) continue;

                CacheLabelSourceKey(lab, TryGetLabelRawKey(lab));
                // Gọi lại logic heal + AutoTranslate qua hàm sync đã có nếu có thể —
                // ở đây mirror phần quan trọng: reset currentLanguage + AutoTranslate.
                if (fiLang != null)
                {
                    try { fiLang.SetValue(a, null); } catch { }
                }
                if (miAuto != null)
                {
                    try { miAuto.Invoke(a, null); n++; } catch { }
                }
                else if (miSet != null && !string.IsNullOrEmpty(orig))
                {
                    try { miSet.Invoke(a, new object[] { orig, true }); n++; } catch { }
                }
            }
            catch { }

            processed++;
            if (processed % batch == 0)
                yield return null;
        }
        Debug.Log("[Localizer] ForceRefreshAutotranslate (batched): " + n + " labels");
    }

    public static void RestoreLabelTextsToOfficial()
    {
        // Boot màn trắng: đừng quét + ghi hàng nghìn UILabel.
        if (!LanguagesLoaded)
        {
            Debug.Log("[Localizer] RestoreLabelTextsToOfficial skip (boot)");
            return;
        }
        UILabel[] allLabels = Resources.FindObjectsOfTypeAll(typeof(UILabel)) as UILabel[];
        if (allLabels == null) return;
        int restored = 0;
        int stuckCleaned = 0;
        for (int i = 0; i < allLabels.Length; i++)
        {
            UILabel lab = allLabels[i];
            if (lab == null) continue;
            try { if (IsPopupRelated(lab)) continue; } catch { }
            try { if (IsOptionsSelectorRelated(lab)) continue; } catch { }
            try { if (!FontRestore.IsLiveSceneObject(lab)) continue; } catch { }
            try
            {
                if (!FontRestore.IsLiveSceneObject(lab)) continue;
                if (IsPopupRelated(lab)) continue;

                string source = null;
                string cur = lab.text;

                // LANG-40: KEY nguồn cache từ component luôn thắng — chính xác nhất
                // (OriginalTextCache chỉ giữ TEXT lần đầu nhìn thấy, có thể là English
                // value chứ không phải key FE_* thật của label).
                if (LabelSourceKeyCache.TryGetValue(lab, out string cachedKey)
                    && !string.IsNullOrEmpty(cachedKey))
                {
                    source = ResolveSourceEnglish(cachedKey);
                }

                string orig;
                if (string.IsNullOrEmpty(source)
                    && OriginalTextCache.TryGetValue(lab, out orig)
                    && !string.IsNullOrEmpty(orig))
                {
                    source = ResolveSourceEnglish(orig);
                }

                if (string.IsNullOrEmpty(source))
                {
                    // FIX LANG-32: label không có cache (Help & Info mở sau khi custom,
                    // nút inject, label spawn muộn...) vẫn có thể đang giữ bản dịch
                    // custom → dùng ValueToEnglish để nhận diện text kẹt và trả về
                    // nguồn English/key, rồi GetText theo ngôn ngữ official hiện tại.
                    // LANG-41: dùng lookup normalized (chịu được ký tự vô hình + ':').
                    if (!string.IsNullOrEmpty(cur) &&
                        TryMapValueToEnglish(cur, out string eng) &&
                        !string.IsNullOrEmpty(eng))
                    {
                        source = eng;
                        stuckCleaned++;
                    }
                    // LANG-42: label đang mang KEY THÔ bị stamp (repack value rỗng) —
                    // chính key đó là nguồn, phân giải trực tiếp phía dưới.
                    else if (!string.IsNullOrEmpty(cur) && ConfigManager.IsFeKey(cur.Trim()))
                    {
                        source = cur.Trim();
                    }
                    // LANG-44: text đang là bản dịch official (JP/IT/…) kẹt trên label
                    else if (!string.IsNullOrEmpty(cur)
                        && TryMapAnyOfficialValueToKey(cur, out string offKey)
                        && !string.IsNullOrEmpty(offKey))
                    {
                        source = offKey;
                        stuckCleaned++;
                    }
                }

                if (string.IsNullOrEmpty(source)) continue;

                // FIX OPT-03: KHÔNG BAO GIỜ ghi key thô vào label. GetText trả nguyên
                // `source` (== key) khi mọi dict của game miss (file ngôn ngữ repack
                // thiếu entry / value rỗng) — code cũ gán nguyên vào label → label mang
                // key "FE_Options_*" và GIỮ VẬY khi quay lại custom (đúng hiện tượng
                // trong video). Khi game không phân giải được → GIỮ text hiện tại
                // (vẫn hiển thị được; tự lành khi về custom qua RefreshAllLabels +
                // OnEnable sanitizer).
                if (TryResolveOfficialLabelText(source, out string restoreText))
                {
                    if (!string.Equals(lab.text, restoreText, StringComparison.Ordinal))
                    {
                        lab.text = restoreText;
                        restored++;
                    }
                }
                else
                {
                    // Không phân giải được (total-miss). Chỉ revert khi text hiện tại là
                    // bản dịch custom "kẹt" (LANG-32) VÀ nguồn là text English thật —
                    // nguồn là key mà game không có thì giữ nguyên (không stamp key).
                    // LANG-41: lookup normalized.
                    if (!string.IsNullOrEmpty(cur)
                        && TryMapValueToEnglish(cur, out string stuckEng)
                        && !string.IsNullOrEmpty(stuckEng)
                        && !ConfigManager.IsFeKey(source)
                        && !string.Equals(cur, source, StringComparison.Ordinal))
                    {
                        lab.text = source;
                        restored++;
                    }
                }
            }
            catch { }
        }
        Debug.Log("[Localizer] Restored " + restored + " UILabel to original text"
            + (stuckCleaned > 0 ? " (stuck-custom cleaned via ValueToEnglish: " + stuckCleaned + ")" : ""));
    }

    static void TeardownOfficialImmediate(string officialLang)
    {
        CMainAboutModSubScreen.LangFolder = null;
        FontsReady = false;
        CurrentCustomLanguage = null;

        try { FontRestore.RestoreAll(); }
        catch (Exception ex) { Debug.LogWarning("[Localizer] RestoreAll: " + ex.Message); }

        try { FontRestore.RestoreUIFonts(); }
        catch (Exception ex) { Debug.LogWarning("[Localizer] RestoreUIFonts: " + ex.Message); }

        try { DestroyCustomFonts(); }
        catch (Exception ex) { Debug.LogWarning("[Localizer] DestroyCustomFonts: " + ex.Message); }

        try { CustomFonts.Clear(); } catch { }

        try { UnregisterPrivateFonts(); }
        catch (Exception ex) { Debug.LogWarning("[Localizer] UnregisterPrivateFonts: " + ex.Message); }

        try { LangImageVault.Unload(); }
        catch (Exception ex) { Debug.LogWarning("[Localizer] LangImageVault.Unload: " + ex.Message); }

        ClearLabelCache();

        try { RestoreLabelTextsToOfficial(); }
        catch (Exception ex) { Debug.LogWarning("[Localizer] Restore label text: " + ex.Message); }

        try { PlagueVnMod.ForceRefreshAutotranslateLabels(); } catch { }
        try
        {
            if (AboutMod_InjectButton_Patch._btn != null)
                AboutMod_InjectButton_Patch.ApplyButtonCaption(
                    AboutMod_InjectButton_Patch._btn.gameObject);
        }
        catch { }

        try
        {
            if (AboutMod_InjectButton_Patch._btn != null)
                AboutMod_InjectButton_Patch.ApplyButtonCaption(
                    AboutMod_InjectButton_Patch._btn.gameObject);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[Localizer] AboutMod caption: " + ex.Message);
        }

        Debug.Log("[Localizer] ActiveLanguage official = " + officialLang
            + " (CurrentCustomLanguage=" + CurrentCustomLanguage + ")");
    }

    public IEnumerator TeardownOfficialDeferredPublic(string officialLang, int epoch)
    {
        return TeardownOfficialDeferred(officialLang, epoch);
    }

    public static void TeardownOfficialImmediatePublic(string officialLang)
    {
        TeardownOfficialImmediate(officialLang);
    }

    IEnumerator TeardownOfficialDeferred(string officialLang, int epoch)
    {
        // Đợi dropdown của game rebuild xong — RestoreAll 8000+ label trong
        // set_ActiveLanguage làm UIDropdownPopupList trống / crash.
        yield return null;
        if (epoch != _langEpoch) yield break;
        if (IsCustomLanguageFolder(CLocalisationManager.ActiveLanguage)) yield break;
        TeardownOfficialImmediate(officialLang);
    }

    // LANG-43: Sweep DEFERRED sau khi đổi về official. QUAN TRỌNG: game gọi
    // CLocalisationManager.set_ActiveLanguage TRƯỚC, rồi MỚI chạy vòng
    // translateLabels[i].AutoTranslate() (CMainOptionsSubScreen.LanguageChange /
    // các screen tương tự) — tức mọi label game stamp từ originalLabelText diễn ra
    // SAU khi postfix set_ActiveLanguage của mod chạy xong. Sweep đồng bộ bên trong
    // setter vì thế bị game ghi đè. Coroutine này đợi game chạy xong vòng của nó
    // (2 frame an toàn) rồi restore + heal lại LẦI CÙNG — epoch-guard để nhường
    // lượt cho lần đổi ngôn ngữ mới nhất nếu người dùng đổi liên tiếp.
    public IEnumerator TeardownOfficialAfterGameLoop(string officialLang, int epoch)
    {
        yield return null;
        yield return null;
        if (epoch != _langEpoch) yield break;
        if (IsCustomLanguageFolder(CLocalisationManager.ActiveLanguage)) yield break;

        try { RestoreLabelTextsToOfficial(); }
        catch (Exception ex)
        {
            Debug.LogWarning("[Localizer] Deferred restore: " + ex.Message);
        }
        try { ForceRefreshAutotranslateLabels(); }
        catch (Exception ex)
        {
            Debug.LogWarning("[Localizer] Deferred ForceRefreshAutotranslate: " + ex.Message);
        }
        try
        {
            if (AboutMod_InjectButton_Patch._btn != null)
                AboutMod_InjectButton_Patch.ApplyButtonCaption(
                    AboutMod_InjectButton_Patch._btn.gameObject);
        }
        catch { }
        Debug.Log("[Localizer] Deferred official sweep done ("
            + officialLang + ", epoch " + epoch + ")");
    }

    IEnumerator LoadFontsAndRefreshDeferred(string langName, int epoch)
    {
        yield return null;
        if (epoch != _langEpoch) yield break;
        if (!string.Equals(CurrentCustomLanguage, langName, StringComparison.OrdinalIgnoreCase))
            yield break;
        if (!IsCustomLanguageActive()) yield break;

        // Text trước — đừng chờ bundle font (trước đây RefreshAllLabels chạy SAU
        // RestoreAll 6000+ label + load bundle → title/options lệch nhịp).
        try { RefreshAllLabels(); }
        catch (Exception ex) { Debug.LogWarning("[Localizer] deferred RefreshAllLabels: " + ex.Message); }

        if (epoch != _langEpoch) yield break;
        try
        {
            TryLoadCustomFonts(Path.Combine(LanguageFolder, langName));
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[Localizer] deferred font load: " + ex.Message);
            FontsReady = false;
        }
    }

    void TryPatchAutotranslate(Harmony harmony)
    {
        try
        {
            var t = AccessTools.TypeByName("UILabelAutotranslate");
            if (t == null) return;

            string[] names = { "SetText", "SetInitialText", "SetTextLegacy" };
            foreach (var name in names)
            {
                // FIX LANG-18: AccessTools.Method log Warning HarmonyX
                // ("Could not find method") cho MỖI tên không khớp — log thật 0.31.0
                // cho thấy cả 3 tên đều miss → 3 dòng warning + hook chết. Dùng
                // Type.GetMethod trực tiếp — trả null im lặng, hành vi giữ nguyên.
                MethodInfo m = null;
                try
                {
                    m = t.GetMethod(name,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                        null, new Type[] { typeof(string) }, null);
                }
                catch (AmbiguousMatchException) { }
                if (m == null) continue;

                var prefix = new HarmonyMethod(typeof(PlagueVnMod), nameof(AutotranslateText_Prefix));
                harmony.Patch(m, prefix: prefix);
                Logger.LogInfo("Đã patch UILabelAutotranslate." + name + "(string)");
                return;
            }
            // FIX LANG-18: không khớp tên đoán → LOG 1 dòng liệt kê method (tên +
            // số tham số) và field string THẬT của UILabelAutotranslate để lần sửa
            // sau patch đúng chỗ thay vì đoán tiếp (cũng xác minh chỗ key gốc của
            // label nằm ở field nào cho LANG-13).
            Logger.LogWarning("UILabelAutotranslate: không thấy SetText/SetInitialText(string). "
                + DescribeTypeMembers(t));

            // FIX OPT-06: log thật (0.29.5) cho thấy SetInitialText thật là (string, bool)
            // → prefix 1-tham-số ở trên KHÔNG BAO GIỜ áp dụng. Thay vì đoán tiếp chữ ký
            // (dễ vỡ theo version game), patch OnEnable (0 tham số, ổn định mọi version)
            // với POSTFIX raw-key sanitizer — xem Autotranslate_OnEnable_Postfix.
            // LANG-40b: giờ đã BIẾT CHẮC chữ ký thật (decompile 1.24: SetInitialText
            //(string newText, bool useLocalisation = true)) → patch PREFIX trực tiếp để
            // chặn poison TẠI NGUỒN: mọi chỗ game gọi SetInitialText (OptionsSelector.Set,
            // screen setup...) sẽ lưu KEY NGUỒN vào originalLabelText thay vì bản dịch
            // custom/text đã dịch — thủ phạm khiến label dính ngôn ngữ cũ mãi mãi.
            MethodInfo miSetInit = null;
            try
            {
                miSetInit = t.GetMethod("SetInitialText",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new Type[] { typeof(string), typeof(bool) }, null);
            }
            catch (AmbiguousMatchException) { }
            if (miSetInit != null)
            {
                var setInitPrefix = new HarmonyMethod(typeof(PlagueVnMod),
                    nameof(AutotranslateSetInitialText_Prefix));
                harmony.Patch(miSetInit, prefix: setInitPrefix);
                Logger.LogInfo("Đã patch UILabelAutotranslate.SetInitialText(string,bool) (prefix chống poison)");
            }
            else
            {
                Logger.LogWarning("UILabelAutotranslate.SetInitialText(string,bool) không tìm thấy — chỉ còn OnEnable sanitizer");
            }
            MethodInfo miOnEnable = null;
            try
            {
                miOnEnable = t.GetMethod("OnEnable",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
            }
            catch (AmbiguousMatchException) { }
            if (miOnEnable != null)
            {
                var onEnablePostfix = new HarmonyMethod(typeof(PlagueVnMod),
                    nameof(Autotranslate_OnEnable_Postfix));
                harmony.Patch(miOnEnable, postfix: onEnablePostfix);
                Logger.LogInfo("Đã patch UILabelAutotranslate.OnEnable (postfix raw-key sanitizer)");
            }
            else
            {
                Logger.LogWarning("UILabelAutotranslate.OnEnable(0) không tìm thấy — bỏ qua postfix sanitizer");
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning("TryPatchAutotranslate: " + ex.Message);
        }
    }

    /// <summary>
    /// FIX LANG-18: liệt kê method (tên + số tham số) và field string của 1 type —
    /// dùng chẩn đoán khi patch reflection không khớp chữ ký mong đợi.
    /// </summary>
    static string DescribeTypeMembers(Type t)
    {
        try
        {
            var sb = new StringBuilder("Method: ");
            var ms = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            int n = 0;
            foreach (var m in ms)
            {
                if (n++ >= 12) { sb.Append("..., "); break; }
                sb.Append(m.Name).Append('(').Append(m.GetParameters().Length).Append("), ");
            }
            sb.Append("Field string: ");
            var fs = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            n = 0;
            foreach (var f in fs)
            {
                if (f.FieldType != typeof(string)) continue;
                if (n++ >= 8) { sb.Append("..., "); break; }
                sb.Append(f.Name).Append(", ");
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return "(không liệt kê được: " + ex.Message + ")";
        }
    }
    public static void AutotranslateText_Prefix(ref string text)
    {
        if (!IsCustomLanguageActive()) return;
        if (string.IsNullOrEmpty(text)) return;
        if (text.Equals("Outbreak", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("Disease", StringComparison.OrdinalIgnoreCase))
        {
            text = CLocalisationManager.GetText(text);
        }
        // CFG-09 (Zero-Hardcode): bỏ 2 key hardcode "Outbreak"/"Disease" — mọi
        // chuỗi đều tra data-driven qua dict của mod; phần mod không có bản dịch
        // được game tự localise (prefix void → hàm gốc LUÔN chạy: pass-through).
        if (!IsCustomLanguageActive()) return;
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            if (TryGetTranslation(text, out string t) &&
                !string.IsNullOrEmpty(t) &&
                !string.Equals(t, text, StringComparison.Ordinal))
            {
                text = t;
                return;
            }
            if (EnglishTextDict.TryGetValue(text, out string engT) &&
                !string.IsNullOrEmpty(engT) &&
                !string.Equals(engT, text, StringComparison.Ordinal))
            {
                text = engT;
            }
        }
        catch { }
    }

    /// <summary>
    /// LANG-40b: PREFIX UILabelAutotranslate.SetInitialText(string, bool) — chặn poison
    /// TẠI NGUỒN. AutoTranslate lần đầu (label==null) sẽ originalLabelText = label.text,
    /// và SetInitialText lưu nguyên tham số vào originalLabelText. Nếu giá trị đi vào là
    /// BẢN DỊCH custom (label inactive bị RefreshAllLabels stamp trước) hoặc text đã
    /// dịch của ngôn ngữ khác thì component sẽ mãi mãi coi đó là "nguồn" — mỗi lần đổi
    /// ngôn ngữ GetText(source) miss → trả nguyên source → label dính ngôn ngữ cũ
    /// (dạng "Quá trình:" / "Hiện Giúp Dở..." lẫn lộn trong ảnh user báo).
    /// Khi OFFICIAL đang active: nếu tham số là bản dịch custom (tra được trong index
    /// ngược exact/normalized) → thay bằng KEY/English nguồn để component lưu đúng.
    /// Khi custom active → không can thiệp (game path đã được mod hỗ trợ riêng).
    /// Không hardcode — toàn bộ tra cứu qua dict data-driven.
    /// </summary>
    public static void AutotranslateSetInitialText_Prefix(ref string newText)
    {
        try
        {
            if (string.IsNullOrEmpty(newText)) return;
            bool _resetWatch = TraceWatchList.Contains(newText.Trim());
            if (_resetWatch)
                Debug.Log("[TRACE-RESET] SetInitialText_Prefix CALLED — newText(before)=\"" + newText
                    + "\" IsCustomLanguageActive=" + IsCustomLanguageActive()
                    + (IsCustomLanguageActive() ? " → SKIP (guard, no-op — case E candidate: nếu sau đây lab.text đổi thì KHÔNG PHẢI do prefix này)" : " → xử lý tiếp (official-language branch)"));
            if (IsCustomLanguageActive()) return;
            string t = newText.Trim();
            if (t.Length == 0) return;
            if (ConfigManager.IsFeKey(t)) return; // key lành — game tự GetText theo key
            // Bản dịch custom/old-value kẹt → trả về nguồn để component lưu ĐÚNG key.
            if (TryMapValueToEnglish(t, out string eng) && !string.IsNullOrEmpty(eng))
            {
                if (_resetWatch)
                    Debug.Log("[TRACE-RESET] SetInitialText_Prefix REWROTE newText \"" + t + "\" → \"" + eng + "\" (official-language poison-detect branch)");
                newText = eng;
            }
        }
        catch { }
    }

    // FIX OPT-06: field "currentLanguage" của UILabelAutotranslate (log LANG-18 xác
    // nhận tên thật) — cache 1 lần trong ProbeAutoTranslateType.
    static FieldInfo _fiAutoCurrentLanguage;

    /// <summary>
    /// FIX OPT-06: postfix cho UILabelAutotranslate.OnEnable — lưới an toàn CHỐNG KEY THÔ.
    /// Game chạy SetInitialText(key) trong OnEnable; khi key miss trong dict ngôn
    /// ngữ hiện hành, game ghi nguyên key vào label. Prefix patch SetText(string)
    /// của mod KHÔNG BAO GIỜ áp dụng (method thật là SetInitialText(string,bool) —
    /// log LANG-18) nên trước đây không có điểm can thiệp nào sau khi game stamp.
    /// Postfix này: custom active + text đang là key FE_* → phân giải lại qua
    /// TryResolveLabelKeyText (key gốc từ component) / TryResolveLabelText (dict mod
    /// → fallback dữ liệu game), rồi reset currentLanguage = null để lần đổi ngôn
    /// ngữ kế tiếp component tự dịch lại. Gate rẻ (StartsWith FE_ từ config).
    /// </summary>
    // FIX LANG-43 (source-identity poisoning): trước đây nhánh "else" tra thẳng
    // TryResolveLabelText(trimmed, ...) trên `cur` = lab.text HIỆN TẠI — nếu label
    // đã từng bị set/dịch trước đó, `cur` có thể là 1 giá trị PHỔ BIẾN (vd "Simple",
    // "On"/"Off") va collision với EnglishTextDict/EnglishToCustomDict của MỘT
    // setting khác (bug "Reset All Progress" → "Simple", "Single Player" nhảy value).
    // Kiến trúc mới: SOURCE IDENTITY (đóng băng 1 lần, qua LabelSourceKeyCache /
    // OriginalTextCache) → TRANSLATE → DISPLAY ONLY. lab.text KHÔNG BAO GIỜ được
    // dùng làm nguồn tra cứu một khi source identity đã tồn tại cho label đó.
    public static void Autotranslate_OnEnable_Postfix(UILabelAutotranslate __instance)
    {
        try
        {
            if (__instance == null) return;
            UILabel lab = __instance.GetComponent<UILabel>();
            if (lab == null || IsPopupRelated(lab)) return;
            if (!IsCustomLanguageActive()) return;

            string cur = lab.text;
            if (string.IsNullOrEmpty(cur)) return;

            bool _resetTrace = lab.gameObject != null &&
                (lab.gameObject.name.IndexOf("Reset", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 TraceWatchList.Contains(cur));
            string _rrRawKey = null, _rrKeyText = null;
            if (_resetTrace)
            {
                _rrRawKey = TryGetLabelRawKey(lab);          // đọc field component TRỰC TIẾP, không qua cache
                _rrKeyText = TryResolveLabelKeyText(lab);     // kết quả dịch nếu component có FE key
            }

            bool _traceWatch = TraceWatchList.Count > 0 &&
                (TraceWatchList.Contains(cur) ||
                 (lab.gameObject != null && ContainsAnyWatchName(lab.gameObject.name)));
            string _identitySource = null; // "cache:key" / "cache:orig" / "first:rawkey" / "first:display"

            // ===== SOURCE IDENTITY =====
            string source;
            bool isFeSource;

            if (LabelSourceKeyCache.TryGetValue(lab, out string cachedKey) && !string.IsNullOrEmpty(cachedKey))
            {
                // Ưu tiên tuyệt đối — LabelSourceKeyCache chỉ chứa key đã xác thực
                // (CacheLabelSourceKey/TryGetLabelRawKey), đóng băng từ lần đầu.
                source = cachedKey;
                isFeSource = true;
                _identitySource = "cache:LabelSourceKeyCache";
            }
            else if (OriginalTextCache.TryGetValue(lab, out string cachedOrig) && !string.IsNullOrEmpty(cachedOrig))
            {
                // Text gốc English đã đóng băng từ lần RefreshAllLabels/OnEnable đầu tiên.
                source = cachedOrig;
                isFeSource = ConfigManager.IsFeKey(source);
                _identitySource = "cache:OriginalTextCache";
            }
            else
            {
                // Chưa từng capture cho label này — ĐÂY LÀ LẦN DUY NHẤT được phép
                // nhìn vào lab.text để suy ra nguồn. Ưu tiên đọc field component
                // (TryGetLabelRawKey — không đọc lab.text) trước khi rơi về display
                // text hiện tại. Sau bước này, kết quả bị khoá vĩnh viễn.
                string trimmed = cur.Trim();
                string rawKey = TryGetLabelRawKey(lab);
                if (!string.IsNullOrEmpty(rawKey))
                {
                    source = rawKey;
                    isFeSource = ConfigManager.IsFeKey(rawKey);
                    CacheLabelSourceKey(lab, rawKey);
                    _identitySource = "FIRST-SIGHT:TryGetLabelRawKey";
                }
                else
                {
                    source = ResolveSourceEnglish(trimmed);
                    isFeSource = ConfigManager.IsFeKey(source);
                    CacheOriginalTextIfAbsent(lab, source);
                    _identitySource = "FIRST-SIGHT:display-text(\"" + trimmed + "\")";
                }
            }

            if (string.IsNullOrEmpty(source))
            {
                if (_traceWatch)
                    Debug.Log("[TRACE] OnEnable " + (lab.gameObject != null ? lab.gameObject.name : "?")
                        + " — source rỗng, bỏ qua. identitySource=" + _identitySource);
                if (_resetTrace)
                    Debug.Log("[TRACE-RESET]"
                        + "\n  go = " + (lab.gameObject != null ? lab.gameObject.name : "?")
                        + "\n  text.before = \"" + cur + "\""
                        + "\n  rawKey = " + (_rrRawKey ?? "(null)")
                        + "\n  OriginalTextCache = (chưa capture — source rỗng)"
                        + "\n  LabelSourceKeyCache = (chưa capture — source rỗng)"
                        + "\n  TryResolveLabelKeyText = " + (_rrKeyText ?? "(null)")
                        + "\n  lookupText = (n/a — source rỗng)"
                        + "\n  resolver = (none — early return)"
                        + "\n  translated = (null)"
                        + "\n  text.after = \"" + cur + "\" (unchanged)");
                return;
            }

            // ===== TRANSLATE — luôn tra theo `source` đã đóng băng, KHÔNG BAO GIỜ theo `cur` =====
            string resolved = null;
            string _resolver = "none";
            if (isFeSource)
            {
                string k1 = TryResolveLabelKeyText(lab);
                if (!string.IsNullOrEmpty(k1) && !string.Equals(k1, cur, StringComparison.Ordinal))
                { resolved = k1; _resolver = "AutotranslateKey"; }
                else if (TryGetTranslation(source, out string feT) && !string.IsNullOrEmpty(feT))
                { resolved = feT; _resolver = "TryGetTranslation(FE)"; }
                else if (TryResolveLabelText(source, out string fb) && !string.IsNullOrEmpty(fb))
                { resolved = fb; _resolver = "TryResolveLabelText(FE-fallback)"; }
            }
            else
            {
                if (TryResolveLabelText(source, out resolved) && !string.IsNullOrEmpty(resolved))
                    _resolver = "TryResolveLabelText(non-FE, REVERSE-capable)";
            }

            if (_resetTrace)
            {
                LabelSourceKeyCache.TryGetValue(lab, out string _rrLskAfter);
                OriginalTextCache.TryGetValue(lab, out string _rrOtcAfter);
                Debug.Log("[TRACE-RESET]"
                    + "\n  go = " + (lab.gameObject != null ? lab.gameObject.name : "?")
                    + "\n  text.before = \"" + cur + "\""
                    + "\n  rawKey = " + (_rrRawKey ?? "(null)")
                    + "\n  OriginalTextCache = " + (_rrOtcAfter ?? "(none)")
                    + "\n  LabelSourceKeyCache = " + (_rrLskAfter ?? "(none)")
                    + "\n  TryResolveLabelKeyText = " + (_rrKeyText ?? "(null)")
                    + "\n  lookupText(=source) = \"" + source + "\" (isFeSource=" + isFeSource + ", identitySource=" + _identitySource + ")"
                    + "\n  resolver = " + _resolver
                    + "\n  translated = " + (resolved != null ? "\"" + resolved + "\"" : "(null)")
                    + "\n  text.after = " + ((!string.IsNullOrEmpty(resolved) && !string.Equals(resolved, cur, StringComparison.Ordinal)) ? "\"" + resolved + "\"" : "\"" + cur + "\" (unchanged)"));
            }

            if (_traceWatch)
            {
                Debug.Log("[TRACE] OnEnable " + (lab.gameObject != null ? lab.gameObject.name : "?")
                    + "\n  text.before = \"" + cur + "\""
                    + "\n  identitySource = " + _identitySource
                    + "\n  sourceKey/sourceEnglish = \"" + source + "\" (isFeSource=" + isFeSource + ")"
                    + "\n  resolver = " + _resolver
                    + "\n  translated = " + (resolved != null ? "\"" + resolved + "\"" : "(null)")
                    + "\n  text.after = " + ((!string.IsNullOrEmpty(resolved) && !string.Equals(resolved, cur, StringComparison.Ordinal)) ? "\"" + resolved + "\"" : "\"" + cur + "\" (unchanged)"));

                if (_identitySource != null && _identitySource.StartsWith("FIRST-SIGHT:display-text", StringComparison.Ordinal))
                    Debug.Log("[TRACE] REVERSE FALLBACK"
                        + "\n  input = \"" + cur + "\""
                        + "\n  ValueToEnglish => " + (TryMapValueToEnglish(cur, out string _v2e) ? "\"" + _v2e + "\"" : "(miss)")
                        + "\n  (lần đầu thấy label — không có identity trước đó, capture từ display text là hợp lệ theo thiết kế, nhưng CẦN xem 'sourceEnglish' ở trên có đúng bản chất setting này hay không)");
            }

            if (string.IsNullOrEmpty(resolved) || string.Equals(resolved, cur, StringComparison.Ordinal))
                return;

            // ===== DISPLAY ONLY =====
            lab.text = resolved;
            if (_fiAutoCurrentLanguage != null)
            {
                try { _fiAutoCurrentLanguage.SetValue(__instance, null); } catch { }
            }
        }
        catch { }
    }

    // ===================== LIFECYCLE =====================
    void Awake()
    {
        // FIX (ổn định/tương thích nhiều máy): Config.Bind/Directory.CreateDirectory
        // trước đây không có try/catch — nếu thư mục cài đặt bị hạn chế quyền ghi, bị
        // antivirus chặn, hoặc ổ đĩa read-only, Awake() dừng giữa chừng vì exception
        // không bắt → toàn bộ mod (kể cả PatchAll) không bao giờ chạy, không log rõ lý do.
        try
        {
            Instance = this;
            // CFG-08 (Zero-Hardcode Policy): toàn bộ tham số cấu hình của mod dồn
            // về ConfigManager — bind đủ entry ra BepInEx/config/<GUID>.cfg để
            // người dùng tự cá nhân hoá, KHÔNG cần build lại. Ba entry gốc
            // (LastLanguage / VerboseLogging / MaxImageBytes) vẫn bind đúng
            // section "General" + key cũ để NGƯỜI DÙNG NÂNG CẤP KHÔNG MẤT CONFIG.
            ConfigManager.Init(Config, Logger);
            // ConfigManager đã gán: PlagueVnMod.LastLanguage / VerboseLogging,
            // LangImageVault.MaxImageBytes / VerboseLogging, và LanguageFolder.
            LanguageFolder = ConfigManager.LanguageFolder;

            if (!string.IsNullOrEmpty(LanguageFolder) && !Directory.Exists(LanguageFolder))
            {
                try { Directory.CreateDirectory(LanguageFolder); }
                catch (Exception ex)
                {
                    Logger.LogWarning("[Localizer] Không tạo được LanguageFolder "
                        + LanguageFolder + ": " + ex.Message);
                }
            }

            // FIX LANG-07: đăng ký codepage provider (nếu có) trước khi load font.
            TryRegisterCodePages();
        }
        catch (Exception ex)
        {
            Logger.LogError("Awake: không khởi tạo được Config/LanguageFolder — mod sẽ không hoạt động: " + ex);
            return;
        }

        var harmony = new Harmony(PluginVersion.Guid); // FIX v0.29.4: dùng chung hằng số thay vì hardcode chuỗi riêng
        try
        {
            harmony.PatchAll();
            TryPatchAutotranslate(harmony);  // FIX MN-09.
            LangImageVault.BindHost(this);
        }
        catch (Exception ex)
        {
            Logger.LogError("PatchAll lỗi (một patch sai): " + ex);
        }

        Invoke(nameof(LoadAllLanguages), ConfigManager.LanguageLoadDelay); // CFG-08: delay tu config [Boot]
        // FIX v2.4: Bỏ fixed Invoke(ForceCustomLanguage, 3.2f).
        // Thay bằng polling coroutine — đợi game load UI xong rồi mới force reload.
        // Phát hiện UI loaded qua 2 signal: (1) CLocalisationManager.ActiveLanguage được set,
        // (2) có ít nhất 1 UILabelAutotranslate trong scene.
        StartCoroutine(WaitForGameUiThenReload());
        Invoke(nameof(ApplyFontsDelayed), ConfigManager.FontApplyDelay); // CFG-08
        // FIX CR-08: ScanGameFontsDelayed trước đây KHÔNG có nơi nào gọi (dead code) —
        // chức năng scan + log toàn bộ font có trong game không bao giờ chạy.
        Invoke(nameof(ScanGameFontsDelayed), ConfigManager.FontScanDelay); // CFG-08
        Application.quitting += OnAppQuitting;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        // FIX C-01: log warning rõ ràng nếu non-Windows.
        if (!_isWindows)
        {
            Logger.LogWarning("PlagueLib: không phải Windows, font custom sẽ KHÔNG hoạt động. "
                + "Image replacement vẫn hoạt động bình thường.");
        }
        Logger.LogInfo(PluginVersion.Name + " " + PluginVersion.VersionString + " đã sẵn sàng!");
    }

    static void OnAppQuitting()
    {
        UnregisterPrivateFonts();
    }

    static void OnProcessExit(object sender, EventArgs e)
    {
        // FIX ST-05: wrap try/catch.
        try
        {
            UnregisterPrivateFonts();
        }
        catch (Exception ex)
        {
            try { System.Console.Error.WriteLine("[PlagueLib] ProcessExit: " + ex.Message); } catch { }
        }
    }


    void OnDestroy()
    {
        try
        {
            if (LastLanguage != null && !string.IsNullOrEmpty(CLocalisationManager.ActiveLanguage))
            {
                // CFG-07: chỉ ghi LastLanguage khi đang ở pack CUSTOM (tra theo
                // IsCustomLanguageFolder — Zero-Hardcode, bỏ so sánh cứng "English").
                // Tránh ghi nhầm tên ngôn ngữ official vào LastLanguage khiến lần
                // boot sau ForceCustomLanguage cố ép một ngôn ngữ official của game.
                if (IsCustomLanguageFolder(CLocalisationManager.ActiveLanguage))
                    LastLanguage.Value = CLocalisationManager.ActiveLanguage;
            }
        }
        catch { }
        Application.quitting -= OnAppQuitting;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        UnregisterPrivateFonts();
        Instance = null;
        // FIX ST-01: chỉ gọi DestroyAll() — nó đã tự gọi Unload() bên trong.
        LangImageVault.DestroyAll();
    }

    void ScanGameFontsDelayed()
    {
        ScanAllGameFonts();
        LogSeenFonts();
    }

    void ApplyFontsDelayed()
    {
        if (!IsCustomLanguageActive() || !FontsReady) return;
        ApplyFontsToAllUIFonts();
        ApplyFontsToAllLabels();
        // FIX v2.1: Refresh labels thêm 1 lần sau khi font apply xong
        // — vì ApplyFontsToAllLabels có thể nullify text khi đổi font.
        RefreshAllLabels();
    }

    // ===================== LOAD NGÔN NGỮ =====================
    /// <summary>
    /// FIX v2.9: Build reverse lookup English → Vietnamese.
    /// Cho mỗi entry "key" = "english_value" trong mpLocalisedTexts["English"],
    /// tìm value tương ứng trong mpLocalisedTexts[CurrentCustomLanguage] cùng key.
    /// Nếu có → EnglishToCustomDict["english_value"] = vietnamese_value.
    /// </summary>
    // FIX LANG-14: chuyển sang static — BindActiveLanguagePack (static, gọi từ
    // patch set_ActiveLanguage) cần gọi hàm này để rebuild reverse lookup khi
    // người dùng đổi ngôn ngữ custom IN-GAME.
    static void BuildEnglishToCustomDict()
    {
        try
        {
            EnglishToCustomDict.Clear();
            var field = typeof(CLocalisationManager).GetField(
                "mpLocalisedTexts", BindingFlags.NonPublic | BindingFlags.Static);
            if (field == null) return;
            var all = field.GetValue(null) as Dictionary<string, Dictionary<string, string>>;
            if (all == null) return;

            // Lấy English dict (CFG-08: tên ngôn ngữ tham chiếu lấy từ config)
            Dictionary<string, string> engDict = null;
            foreach (var kv in all)
            {
                if (kv.Key.Equals(ConfigManager.ReferenceLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    engDict = kv.Value;
                    break;
                }
            }
            if (engDict == null) { Debug.LogWarning("[v2.9] English dict not found"); return; }

            // LANG-41c: build map ngược English VALUE → FE KEY từ dict English CỦA GAME
            // (data-driven, Zero-Hardcode). Phục vụ TryResolveOfficialLabelText: khi
            // OriginalTextCache của label chỉ giữ English text (vd "General") — English
            // text là VALUE của key FE_* chứ không phải key → GetText(English) miss.
            // Map ngược cho phép tìm lại đúng key FE_* rồi GetText theo ngôn ngữ mới.
            try
            {
                _gameEnglishDict = engDict;
                GameEnglishValueToKey.Clear();
                GameEnglishValueToKeyNorm.Clear();
                foreach (var kv in engDict)
                {
                    string key = kv.Key;
                    string engValue = kv.Value;
                    if (string.IsNullOrEmpty(engValue)) continue;
                    string evTrim = engValue.Trim();
                    if (evTrim.Length == 0) continue;
                    if (!ConfigManager.IsFeKey(key)) continue; // CFG-08
                    if (!GameEnglishValueToKey.ContainsKey(evTrim))
                        GameEnglishValueToKey[evTrim] = key;
                    string norm = NormalizeForMatch(evTrim);
                    if (norm.Length > 0 && !GameEnglishValueToKeyNorm.ContainsKey(norm))
                        GameEnglishValueToKeyNorm[norm] = key;
                }
            }
            catch (Exception exBuildRev)
            {
                Debug.LogWarning("[v2.9] Build GameEnglishValueToKey error: " + exBuildRev.Message);
            }

            // Lấy custom language dict
            Dictionary<string, string> custDict = null;
            string custLang = CurrentCustomLanguage;
            if (!string.IsNullOrEmpty(custLang) && all.ContainsKey(custLang))
                custDict = all[custLang];
            else
            {
                // Fallback: dùng TranslationDict (đã load từ file .txt)
                custDict = TranslationDict;
            }
            if (custDict == null) { Debug.LogWarning("[v2.9] Custom dict not found"); return; }

            // FIX v0.27.7: chỉ build reverse lookup từ FE_* keys (không dùng non-FE_*)
            // vì non-FE_* keys là cho kịch bản Board Game, không phải menu UI.
            // Trước đây dùng toàn bộ custDict → "Single Player" bị overwrite bằng "1 Người"
            // (từ entry "Single Player" = "1 Người") thay vì "Chơi đơn" (từ FE_Single_Player).

            // Build reverse lookup — FIX v0.27.5: prefer non-uppercase Vietnamese value
            // khi 2+ FE_ keys map cùng English value nhưng khác Vietnamese value.
            // vd: "FE_Multiplayer_Mode_Title" → "CHƠI NHIỀU NGƯỜI" (uppercase, title)
            //     "FE_Multi_Player" → "Chơi nhiều người" (regular, menu button)
            // → EnglishToCustomDict["Multiplayer"] = "Chơi nhiều người" (prefer regular)
            int count = 0;
            foreach (var kv in engDict)
            {
                string key = kv.Key;
                string engValue = kv.Value;
                if (string.IsNullOrEmpty(engValue)) continue;
                // FIX v0.27.7: chỉ xử lý FE_* keys — skip non-FE_* (Board Game scenario keys)
                if (!ConfigManager.IsFeKey(key)) continue; // CFG-08
                // Tìm value tiếng Việt cùng key
                string custValue;
                if (custDict.TryGetValue(key, out custValue) && !string.IsNullOrEmpty(custValue))
                {
                    // Chỉ thêm nếu engValue != custValue
                    if (!string.Equals(engValue, custValue, StringComparison.Ordinal))
                    {
                        // FIX v0.27.5: nếu đã có entry, chỉ overwrite nếu value mới KHÔNG phải all-caps
                        // (prefer "Chơi nhiều người" over "CHƠI NHIỀU NGƯỜI")
                        string existing;
                        if (EnglishToCustomDict.TryGetValue(engValue, out existing))
                        {
                            // Nếu existing là all-caps và custValue không phải all-caps → overwrite
                            // FIX CR-11: ToUpperInvariant (culture-safe) + so sánh Ordinal tường minh.
                            bool existingIsUpper = existing.Equals(existing.ToUpperInvariant(), StringComparison.Ordinal);
                            bool newIsUpper = custValue.Equals(custValue.ToUpperInvariant(), StringComparison.Ordinal);
                            if (existingIsUpper && !newIsUpper)
                            {
                                EnglishToCustomDict[engValue] = custValue;
                            }
                            // Nếu existing không phải all-caps → giữ nguyên (không overwrite)
                        }
                        else
                        {
                            // Chưa có entry → thêm mới
                            EnglishToCustomDict[engValue] = custValue;
                            count++;
                        }
                    }
                }
            }
            // FIX LANG-16: báo cáo xung đột 2 nguồn dịch cho CÙNG một chuỗi
            // English — vd "Single Player" vừa là English value của
            // FE_Single_Player = "Chơi đơn" (menu chính), vừa là text-key
            // "Single Player" = "1 Người" (kịch bản Board Game). Đây là dữ liệu
            // đa nghĩa chứ không phải lỗi code: mod ưu tiên bản FE cho UI chính,
            // bản text-key áp dụng khi game tra đúng key đó (label kịch bản).
            // Log ra để tác giả file dịch BIẾT rõ entry nào thắng ở đâu.
            int conflicts = 0;
            bool logConflicts = VerboseLogging != null && VerboseLogging.Value;
            foreach (var kv in EnglishTextDict)
            {
                string feVal;
                if (EnglishToCustomDict.TryGetValue(kv.Key, out feVal) &&
                    !string.IsNullOrEmpty(feVal) &&
                    !string.IsNullOrEmpty(kv.Value) &&
                    !string.Equals(feVal, kv.Value, StringComparison.Ordinal))
                {
                    conflicts++;
                    if (logConflicts && conflicts <= 20)
                        Debug.LogWarning("[Localizer] ⚠ Xung đột 2 nguồn dịch cho text \""
                            + Trunc(kv.Key, 60) + "\": FE (menu UI) = \"" + Trunc(feVal, 40)
                            + "\" ≠ text-key (kịch bản) = \"" + Trunc(kv.Value, 40)
                            + "\" — chuỗi English cứng dùng text-key; FE_* dùng bản FE.");
                }
            }
            if (logConflicts && conflicts > 20)
                Debug.LogWarning("[Localizer] Còn " + (conflicts - 20)
                    + " xung đột text-key/FE-key khác bị im lặng (giới hạn log).");
            Debug.Log("[v2.9] Built EnglishToCustomDict: " + count + " entries, "
                + conflicts + " text-key conflicts");
        }
        catch (Exception ex)
        {
            Debug.LogError("[v2.9] BuildEnglishToCustomDict error: " + ex.Message);
        }
        try { RebuildTranslationIndex(); } catch { }
    }

    void LoadAllLanguages()
    {
        try
        {
            var field = typeof(CLocalisationManager).GetField(
                "mpLocalisedTexts", BindingFlags.NonPublic | BindingFlags.Static);
            if (field == null) { Logger.LogError("Không tìm thấy mpLocalisedTexts"); return; }

            var mpLocalisedTexts = field.GetValue(null) as Dictionary<string, Dictionary<string, string>>;
            if (mpLocalisedTexts == null) { Logger.LogError("mpLocalisedTexts null"); return; }

            // CFG-03 (dropdown trống — bảo toàn dữ liệu ngôn ngữ gốc của game):
            // snapshot danh sách ngôn ngữ GAME đã load TRƯỚC khi mod inject pack
            // custom. Nếu người dùng đặt tên folder pack trùng tên ngôn ngữ
            // official (vd "Vietnamese"), nạp bình thường sẽ ĐÈ entry
            // mpLocalisedTexts["Vietnamese"] + AddLanguage trùng tên → hỏng danh
            // sách ngôn ngữ gốc (nguồn dữ liệu của dropdown chọn ngôn ngữ).
            // Folder trùng tên → bỏ qua + cảnh báo (đặt lại tên folder để nạp).
            var officialSnapshot = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvOfficial in mpLocalisedTexts)
                officialSnapshot.Add(kvOfficial.Key);

            TranslationDict.Clear();
            CustomFonts.Clear();
            FontBd = null; FontMd = null; FontLt = null;
            FontsReady = false;

            // FIX LANG-08: mỗi folder bọc try/catch riêng.
            string[] langDirs = new string[0];
            try { langDirs = Directory.GetDirectories(LanguageFolder); }
            catch (Exception ex)
            {
                Logger.LogWarning("[Localizer] Không enumerate được " + LanguageFolder + ": " + ex.Message);
            }
            foreach (string dir in langDirs)
            {
                string langName = null;
                try
                {
                    langName = Path.GetFileName(dir);
                    // CFG-03: bỏ folder trùng tên official (config + snapshot game).
                    if (ConfigManager.IsOfficialLanguage(langName) || officialSnapshot.Contains(langName))
                    {
                        Logger.LogWarning("[Localizer] Bỏ qua folder trùng tên ngôn ngữ official của game: "
                            + langName + " — đổi tên folder để pack được nạp");
                        continue;
                    }
                    if (!HasEnabledFile(dir, langName)) continue;

                    var dict = LoadLanguageFromFolder(dir, langName);
                    if (dict.Count == 0) continue;

                    // CFG-03: chỉ AddLanguage lần ĐẦU tên này xuất hiện — gọi trùng
                    // tên có thể tạo entry kép trong registry ngôn ngữ của game
                    // (dropdown hiển thị 2 dòng trùng).
                    if (!mpLocalisedTexts.ContainsKey(langName))
                        CLocalisationManager.AddLanguage(langName, langName, langName.ToLowerInvariant()); // FIX CR-11: culture-safe
                    mpLocalisedTexts[langName] = dict;

                    // FIX v0.27.6: thống kê cấu trúc pack (FE_*/non-FE_*) cho log.
                    // FIX LANG-15: KHÔNG merge vào TranslationDict/EnglishTextDict
                    // tại đây. Trước đây MỌI folder .enabled đều merge theo thứ tự
                    // filesystem — folder nạp sau ĐÈ folder nạp trước, kết quả phụ
                    // thuộc TÊN folder (thứ tự sắp xếp khác nhau Windows/Linux).
                    // VD thực tế từ log 0.31.0: pack English identity ("ai aiii
                    // aiiii") và pack "Tiếng Việt" cùng bật — nếu pack identity nạp
                    // sau, entry English đè mất bản dịch (hiện may mắn không xảy ra
                    // vì NTFS sort "ai..." trước "Tiếng Việt"). Giờ chỉ merge pack
                    // ĐƯỢC CHỌN — xem khối "FIX LANG-15" sau vòng lặp folder; mọi
                    // pack vẫn đăng ký vào mpLocalisedTexts ở trên để game/mod có
                    // thể chuyển sang sau (BindActiveLanguagePack).
                    {
                        int feCount = 0, engCount = 0;
                        foreach (var kv in dict)
                        {
                            if (ConfigManager.IsFeKey(kv.Key)) feCount++; // CFG-08
                            else engCount++;
                        }
                        if (feCount > 0 || engCount > 0)
                            Logger.LogInfo("  → FE_* keys: " + feCount + ", English text keys: " + engCount);
                    }

                    Logger.LogInfo("Đã load folder [" + langName + "] → " + dict.Count + " câu dịch");
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("[Localizer] Bỏ qua folder ngôn ngữ lỗi ["
                        + (langName ?? Path.GetFileName(dir)) + "]: " + ex.Message);
                }
            }

            // FIX LANG-08: file .txt lẻ ở gốc LanguageFolder cũng được bọc
            // try/catch riêng — 1 file lỗi không chặn các file còn lại.
            string[] rootTxtFiles = new string[0];
            try { rootTxtFiles = Directory.GetFiles(LanguageFolder, "*.txt"); }
            catch (Exception ex)
            {
                Logger.LogWarning("[Localizer] Không enumerate được *.txt trong " + LanguageFolder + ": " + ex.Message);
            }
            foreach (string txtPath in rootTxtFiles)
            {
                string langName = null;
                try
                {
                    langName = Path.GetFileNameWithoutExtension(txtPath);
                    // CFG-03: bỏ file trùng tên official (config + snapshot game).
                    if (ConfigManager.IsOfficialLanguage(langName) || officialSnapshot.Contains(langName))
                    {
                        Logger.LogWarning("[Localizer] Bỏ qua file trùng tên ngôn ngữ official của game: "
                            + langName + " — đổi tên file để pack được nạp");
                        continue;
                    }
                    if (mpLocalisedTexts.ContainsKey(langName)) continue;
                    if (!File.Exists(Path.Combine(LanguageFolder, langName + ".enabled"))) continue;

                    var dict = LoadTranslationFile(txtPath);
                    if (dict.Count == 0) continue;

                    // CFG-03: chỉ AddLanguage lần ĐẦU tên này xuất hiện (xem chú thích trên).
                    if (!mpLocalisedTexts.ContainsKey(langName))
                        CLocalisationManager.AddLanguage(langName, langName, langName.ToLowerInvariant()); // FIX CR-11: culture-safe
                    mpLocalisedTexts[langName] = dict;

                    Logger.LogInfo("Đã load file cũ [" + langName + "] → " + dict.Count + " câu dịch");
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("[Localizer] Bỏ qua file ngôn ngữ lỗi ["
                        + (langName ?? Path.GetFileName(txtPath)) + "]: " + ex.Message);
                }
            }

            // Chỉ gán CurrentCustomLanguage khi LastLanguage còn pack custom.
            // Trước đây firstEnabled = folder sort đầu (T.Việt Shizuna) dù game
            // đang English → merge nhầm pack + chậm boot.
            if (string.IsNullOrEmpty(CurrentCustomLanguage) ||
                !mpLocalisedTexts.ContainsKey(CurrentCustomLanguage))
            {
                string wantLang = null;
                if (LastLanguage != null && !string.IsNullOrEmpty(LastLanguage.Value) &&
                    mpLocalisedTexts.ContainsKey(LastLanguage.Value))
                    wantLang = LastLanguage.Value;
                CurrentCustomLanguage = wantLang;
            }

            // FIX LANG-15: chỉ merge pack ĐƯỢC CHỌN vào TranslationDict /
            // EnglishTextDict (dict tra cứu của mod) — không merge mọi folder
            // theo thứ tự filesystem nữa. Các pack khác vẫn nằm trong
            // mpLocalisedTexts để có thể chuyển sau qua BindActiveLanguagePack.
            if (!string.IsNullOrEmpty(CurrentCustomLanguage) &&
                mpLocalisedTexts.TryGetValue(CurrentCustomLanguage, out Dictionary<string, string> selDict) &&
                selDict != null)
            {
                int feSel = 0, engSel = 0;
                foreach (var kv in selDict)
                {
                    if (ConfigManager.IsFeKey(kv.Key))
                    {
                        TranslationDict[kv.Key] = kv.Value;
                        feSel++;
                    }
                    else
                    {
                        EnglishTextDict[kv.Key] = kv.Value;
                        engSel++;
                    }
                }
                Logger.LogInfo("Mod dict build từ pack [" + CurrentCustomLanguage
                    + "] → FE_* keys: " + feSel + ", English text keys: " + engSel);
            }

            // FIX LANG-02: đối chiếu placeholder {N} của bản dịch với English gốc
            // TRƯỚC khi build reverse lookup — entry dùng index vượt English sẽ bị
            // loại để tránh FormatException (crash game) khi game string.Format.
            ValidatePlaceholdersAgainstEnglish();

            // FIX v2.9: Build reverse lookup English → Vietnamese
            BuildEnglishToCustomDict();

            Logger.LogInfo("===== HOÀN TẤT LOAD NGÔN NGỮ CHÍNH =====");
        }
        catch (Exception ex)
        {
            Logger.LogError("Lỗi LoadAllLanguages: " + ex);
        }
        // FIX CR-03: đánh dấu đã load xong (kể cả khi có exception — tránh coroutine
        // chờ vô hạn; phần dict đã load tới đâu vẫn dùng được tới đó).
        try { RebuildAnyOfficialValueToKey(); } catch { }
        LanguagesLoaded = true;
        CMainAboutModSubScreen.InjectFallbackIntoAllLanguages();
    }

    bool HasEnabledFile(string dir, string langName)
    {
        if (File.Exists(Path.Combine(dir, langName + ".enabled"))) return true;
        return Directory.GetFiles(dir, "*.enabled").Length > 0;
    }

    /// <summary>
    /// FIX v2.13: Load TOÀN BỘ file trong language folder, không chỉ .txt.
    /// Hỗ trợ: .txt, .lang, .csv, .json (best-effort parse).
    /// Tối ưu: đọc song song, cache mtime, skip file không đổi.
    /// </summary>
    Dictionary<string, string> LoadLanguageFromFolder(string folderPath, string langName)
    {
        var finalDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // FIX v2.13: nhận nhiều extension — ưu tiên .txt > .lang > .csv > .json
        // FIX CR-07: doc comment nói hỗ trợ .json nhưng danh sách dưới đây thiếu
        // ".json" → file dịch .json bị bỏ qua hoàn toàn (im lặng). Đã bổ sung.
        // Lưu ý: parse vẫn là best-effort theo regex "key" = "value" — file JSON
        // chuẩn ("key": "value") sẽ không match và trả dict rỗng (an toàn).
        // FIX LANG-12: mảng này là THỨ TỰ NẠP — entry nạp sau sẽ ĐÈ entry nạp
        // trước khi trùng key. Doc nói "ưu tiên .txt > .lang > .csv > .json" nên
        // .txt phải nạp CUỐI CÙNG (mảng liệt kê theo thứ tự NGƯỢC ưu tiên).
        // Trước đây mảng theo đúng thứ tự ưu tiên → thực tế .json lại đè .txt —
        // ngược với doc khai báo.
        // CFG-08: extension + thứ tự nạp lấy từ config ([Language] LanguageFileExtensions)
        // — mảng config cũng theo quy ước cũ: file nạp SAU đè entry trùng key.
        var supportedExts = ConfigManager.LanguageFileExtensions;
        var allFiles = new List<string>();
        foreach (var ext in supportedExts)
        {
            try
            {
                var files = Directory.GetFiles(folderPath, "*" + ext, SearchOption.TopDirectoryOnly);
                if (files != null) allFiles.AddRange(files);
            }
            catch { }
        }

        allFiles.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (var path in allFiles)
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            if (IsPercentOverrideFile(stem))
            {
                Logger.LogInfo(" → Skip file override cũ " + Path.GetFileName(path)
                    + " (bỏ hệ thống %N)");
                continue;
            }
            Logger.LogInfo(" → Load " + Path.GetFileName(path));
            var dict = LoadTranslationFile(path);
            foreach (var kv in dict)
                finalDict[kv.Key] = kv.Value;
        }
        return finalDict;
    }

    static bool IsPercentOverrideFile(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return _priorityRegex.IsMatch(name);
    }

    static readonly Regex _priorityRegex = new Regex(@"%(\d+)$",
        RegexOptions.Compiled, TimeSpan.FromSeconds(2));

    /// <summary>
    /// FIX v2.5: Polling coroutine — đợi game load UI xong rồi force reload ngôn ngữ.
    /// Signal: CLocalisationManager.ActiveLanguage được set (không null/empty).
    /// (KHÔNG check UILabelAutotranslate vì type có thể không tồn tại trong game).
    /// Sau khi detect, chờ thêm 2s cho UI ổn định rồi mới force reload.
    /// </summary>
    IEnumerator WaitForGameUiThenReload()
    {
        Logger.LogInfo("[v2.7] WaitForGameUiThenReload bắt đầu polling...");

        // Phase 1: Poll cho tới khi ActiveLanguage được set
        // FIX CR-03: đồng thời chờ LanguagesLoaded (LoadAllLanguages đã chạy xong) —
        // tránh ForceCustomLanguage chạy trước rồi bị LoadAllLanguages wipe font/dict.
        // CFG-08: interval/timeout/stabilize lấy từ config ([Boot] section) —
        // không còn delay hardcode trong code.
        float timeout = ConfigManager.UiPollTimeout;
        float interval = ConfigManager.UiPollInterval;
        if (interval < 0.05f) interval = 0.05f; // clamp an toàn tránh vòng quay quá dày
        if (timeout < interval * 2f) timeout = interval * 2f;
        float elapsed = 0f;
        float nextLogAt = 2f;
        bool uiReady = false;
        while (elapsed < timeout)
        {
            bool langReady = false;
            string activeLang = null;
            try
            {
                activeLang = CLocalisationManager.ActiveLanguage;
                langReady = !string.IsNullOrEmpty(activeLang);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("[v2.7] Poll lỗi khi đọc ActiveLanguage: " + ex.Message);
            }

            // Log định kỳ (~2 giây) để biết mod vẫn đang poll
            if (elapsed >= nextLogAt)
            {
                nextLogAt = elapsed + 2f;
                Logger.LogInfo("[v2.7] Đang poll — elapsed=" + elapsed.ToString("F1")
                    + "s, langReady=" + langReady + ", languagesLoaded=" + LanguagesLoaded
                    + ", activeLang=" + (activeLang ?? "null"));
            }

            if (langReady && LanguagesLoaded)
            {
                uiReady = true;
                Logger.LogInfo("[v2.7] ✅ Game UI loaded detected sau " + elapsed.ToString("F1")
                    + "s — ActiveLanguage=" + activeLang + " — đợi "
                    + ConfigManager.UiStabilizeDelay.ToString("F1") + "s rồi reload");
                break;
            }

            yield return new WaitForSeconds(interval);
            elapsed += interval;
        }

        if (!uiReady)
        {
            Logger.LogWarning("[v2.7] ⚠️ Timeout đợi game UI loaded sau "
                + timeout.ToString("F0") + "s — skip force reload");
            yield break;
        }

        // Phase 2: Đợi thêm cho UI ổn định hoàn toàn (CFG-08: [Boot] UiStabilizeDelaySeconds)
        Logger.LogInfo("[v2.7] Đợi " + ConfigManager.UiStabilizeDelay.ToString("F1")
            + "s cho UI ổn định...");
        yield return new WaitForSeconds(ConfigManager.UiStabilizeDelay);

        // Phase 3: Force reload ngôn ngữ từ config
        Logger.LogInfo("[v2.7] Gọi ForceCustomLanguage...");
        ForceCustomLanguage();

        string act = null;
        try { act = CLocalisationManager.ActiveLanguage; } catch { }
        if (!string.IsNullOrEmpty(act) && ConfigManager.IsOfficialLanguage(act))
        {
            Debug.Log("[v2.7] UI ready — ActiveLanguage official = " + act + " → không ForceCustomLanguage");
        }


    }

    /// <summary>
    /// LANG-30: Không ép custom nếu game đang (hoặc vừa load) ngôn ngữ OFFICIAL.
    /// Trước đây luôn ActiveLanguage = LastLanguage (Tiếng Việt) → đè Nga/Anh đã lưu.
    /// </summary>
    public void ForceCustomLanguage()
    {
        try
        {
            string active = null;
            try { active = CLocalisationManager.ActiveLanguage; } catch { }

            // User đã chọn official khác English (Japanese, Russian...) → tôn trọng, cổ điển lol
            if (!string.IsNullOrEmpty(active)
                && ConfigManager.IsOfficialLanguage(active)
                && !active.Equals(ConfigManager.ReferenceLanguage, StringComparison.OrdinalIgnoreCase))
            {
                Debug.Log("[v2.7] Bỏ ForceCustomLanguage — user official = " + active);
                return;
            }

            string last = (LastLanguage != null) ? (LastLanguage.Value ?? "").Trim() : "";
            if (string.IsNullOrEmpty(last) || !IsCustomLanguageFolder(last))
            {
                Debug.Log("[v2.7] ForceCustomLanguage: LastLanguage trống/không custom — bỏ qua");
                return;
            }

            // English / empty + có LastLanguage custom → restore
            if (string.Equals(active, last, StringComparison.OrdinalIgnoreCase))
                return; // đã đúng

            Debug.Log("[v2.7] ForceCustomLanguage begin — LastLanguage=" + last);
            CLocalisationManager.ActiveLanguage = last;
            Debug.Log("[v2.7] ✅ ForceCustomLanguage xong");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[v2.7] ForceCustomLanguage lỗi: " + ex.Message);
        }
    }

    // ===================== KEY PROBE (UILabelAutotranslate) =====================
    // FIX LANG-13: 1 chuỗi English có thể đồng thời là:
    //   (a) English value của key FE_*  — "FE_Single_Player" = "Single Player"
    //       → bản dịch menu UI: "Chơi đơn"
    //   (b) key text thuần của kịch bản — "Single Player" = "1 Người"
    //       (Board Game scenario)
    // Tra thuần text (chuỗi ưu tiên 1-3 của RefreshAllLabels) KHÔNG THỂ phân biệt
    // (a) và (b) — chỉ KEY GỐC của label (game lưu trong component
    // UILabelAutotranslate) mới mang ngữ cảnh đúng. Bộ probe dưới đây đọc key đó
    // bằng reflection thuần (cache FieldInfo 1 lần, không phụ thuộc chữ ký method
    // của game — log thật cho thấy SetText/SetInitialText(string) đều không tồn
    // tại): mọi lỗi đều trả null → tự động fallback về chuỗi tra text cũ, KHÔNG
    // BAO GIỜ làm hành vi tệ hơn trước.

    static Type _autotranslateType;
    static FieldInfo[] _autotranslateStringFields;
    static bool _autotranslateProbed;

    static void ProbeAutoTranslateType()
    {
        if (_autotranslateProbed) return;
        _autotranslateProbed = true;
        try
        {
            var t = AccessTools.TypeByName("UILabelAutotranslate");
            if (t == null) return;
            _autotranslateType = t;
            var fields = new List<FieldInfo>();
            var all = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var f in all)
                if (f.FieldType == typeof(string)) fields.Add(f);
            _autotranslateStringFields = fields.ToArray();

            // FIX OPT-06: cache luôn field currentLanguage — Autotranslate_OnEnable_Postfix
            // reset nó sau khi sửa key thô để component tự dịch lại ở lần đổi ngôn ngữ sau.
            try
            {
                _fiAutoCurrentLanguage = t.GetField("currentLanguage",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
            catch { _fiAutoCurrentLanguage = null; }

            // Chẩn đoán 1 lần: liệt kê field string của UILabelAutotranslate — giúp
            // xác minh key gốc nằm ở field nào nếu cần tinh chỉnh lần sau.
            if (fields.Count > 0)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < fields.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(fields[i].Name);
                }
                Debug.Log("[Localizer] UILabelAutotranslate string fields: " + sb);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[Localizer] Probe UILabelAutotranslate lỗi: " + ex.Message);
        }
    }

    /// <summary>
    /// FIX LANG-13: đọc KEY GỐC của label từ component UILabelAutotranslate rồi tra
    /// key đó trong dict của mod — trả về bản dịch nếu tra được, null nếu không.
    /// Quy tắc an toàn:
    ///   - Field chứa giá trị có tiền tố FE_* → chắc chắn là KEY gốc UI chính →
    ///     tra TryGetTranslation (ScenarioDict ưu tiên, rồi TranslationDict).
    ///   - Field chứa chuỗi không phải FE_* → chỉ tin làm key khi chuỗi đó
    ///     KHÔNG phải English value của key FE_* nào — nếu trùng, chuỗi ưu tiên 2
    ///     của RefreshAllLabels sẽ lo phần UI (tránh lấy nhầm bản kịch bản cho
    ///     label UI chỉ lưu text English mà không lưu key).
    /// </summary>
    static string TryResolveLabelKeyText(UILabel lab)
    {
        if (lab == null) return null;
        try
        {
            if (_autotranslateType == null) ProbeAutoTranslateType();
            if (_autotranslateType == null || _autotranslateStringFields == null ||
                _autotranslateStringFields.Length == 0) return null;

            var comp = lab.GetComponent(_autotranslateType);
            if (comp == null) return null;

            string feValue = null, textValue = null;
            for (int i = 0; i < _autotranslateStringFields.Length; i++)
            {
                string v = _autotranslateStringFields[i].GetValue(comp) as string;
                if (string.IsNullOrEmpty(v)) continue;

                if (ConfigManager.IsFeKey(v)) // CFG-08
                {
                    // Key FE_* — chắc chắn là key gốc của UI chính.
                    if (feValue == null && TryGetTranslation(v, out string feT) && !string.IsNullOrEmpty(feT))
                        feValue = feT;
                }
                else if (textValue == null)
                {
                    // Chỉ tin chuỗi không-FE làm key khi nó KHÔNG trùng English
                    // value của key FE_* nào (xem doc) — nếu trùng, ưu tiên 2 lo.
                    if (!EnglishToCustomDict.ContainsKey(v))
                    {
                        if (TryGetTranslation(v, out string scT) && !string.IsNullOrEmpty(scT))
                            textValue = scT;
                        else if (EnglishTextDict.TryGetValue(v, out string txT) && !string.IsNullOrEmpty(txT))
                            textValue = txT;
                    }
                }
                if (feValue != null && textValue != null) break;
            }
            // Key FE_* thắng tuyệt đối — UI chính luôn theo bản FE.
            if (feValue != null) return feValue;
            return textValue;
        }
        catch { }
        return null;
    }

    /// <summary>
    /// FIX OPT-01: phân giải text hiển thị cho 1 UILabel theo chuỗi fallback ĐỒNG NHẤT
    /// (data-driven 100% — không có bất kỳ key/text hardcode nào):
    ///   1. Dict của mod (TryGetTranslation: ScenarioDict → TranslationDict FE_*)
    ///   2. EnglishTextDict (text-key kịch bản) + EnglishToCustomDict (English → custom)
    ///   3. Biến thể bỏ dấu ':' cuối (prefab thường serialize "FE_Options_Interface:"
    ///      trong khi key trong file dịch là "FE_Options_Interface") — giữ lại ':' khi trả
    ///   4. Key FE_* mà mod không có → tra DỮ LIỆU NGÔN NGỮ CỦA GAME qua GetText
    ///      (GetTextInternal tự fallback English nội bộ). CHỈ nhận khi game phân giải
    ///      được giá trị KHÁC key — tuyệt đối không bao giờ trả lại chính key thô,
    ///      vì key thô hiển thị trên UI chính là đúng hiện tượng lỗi cần sửa.
    /// Dùng chung cho RefreshAllLabels / UILabel.OnEnable / UILabelAutotranslate.OnEnable.
    /// </summary>
    public static bool TryResolveLabelText(string raw, out string resolved)
    {
        resolved = null;
        if (string.IsNullOrEmpty(raw)) return false;
        string t = raw.Trim();
        if (t.Length == 0) return false;

        // 1+2: dict của mod
        if (TryGetTranslation(t, out string v) && !string.IsNullOrEmpty(v))
        { resolved = v; return true; }
        if (EnglishTextDict.TryGetValue(t, out v) && !string.IsNullOrEmpty(v)
            && !string.Equals(v, t, StringComparison.Ordinal))
        { resolved = v; return true; }
        if (EnglishToCustomDict.TryGetValue(t, out v) && !string.IsNullOrEmpty(v)
            && !string.Equals(v, t, StringComparison.Ordinal))
        { resolved = v; return true; }

        // 3: biến thể bỏ ':' cuối
        string noColon = null;
        if (t.EndsWith(":"))
        {
            noColon = t.TrimEnd(':').TrimEnd();
            if (noColon.Length == 0) noColon = null;
        }
        if (noColon != null)
        {
            if (TryGetTranslation(noColon, out v) && !string.IsNullOrEmpty(v))
            { resolved = v + ":"; return true; }
            if (EnglishTextDict.TryGetValue(noColon, out v) && !string.IsNullOrEmpty(v)
                && !string.Equals(v, noColon, StringComparison.Ordinal))
            { resolved = v + ":"; return true; }
            if (EnglishToCustomDict.TryGetValue(noColon, out v) && !string.IsNullOrEmpty(v)
                && !string.Equals(v, noColon, StringComparison.Ordinal))
            { resolved = v + ":"; return true; }
        }

        // 4: key FE_* → dữ liệu game (GetText tự English-fallback; trả key khi miss toàn bộ)
        if (ConfigManager.IsFeKey(t))
        {
            try
            {
                string g = CLocalisationManager.GetText(t);
                if (!string.IsNullOrEmpty(g)
                    && !string.Equals(g, t, StringComparison.OrdinalIgnoreCase))
                { resolved = g; return true; }
            }
            catch { }
            if (noColon != null)
            {
                try
                {
                    string g2 = CLocalisationManager.GetText(noColon);
                    if (!string.IsNullOrEmpty(g2)
                        && !string.Equals(g2, noColon, StringComparison.OrdinalIgnoreCase))
                    { resolved = g2 + ":"; return true; }
                }
                catch { }
            }
        }
        return false;
    }

    /// <summary>
    /// FIX OPT-04: phân giải text CHO CHÍNH THỨC (khi đang ở official language) —
    /// chỉ dùng GetText của game + biến thể bỏ ':' cuối. Trả false khi game
    /// phân giải không được (kể cả English fallback) → caller GIỮ text hiện tại
    /// của label thay vì ghi key thô vào.
    /// </summary>
    public static bool TryResolveOfficialLabelText(string source, out string resolved)
    {
        resolved = null;
        if (string.IsNullOrEmpty(source)) return false;
        try
        {
            string g = CLocalisationManager.GetText(source);
            if (!string.IsNullOrEmpty(g)
                && !string.Equals(g, source, StringComparison.Ordinal))
            { resolved = g; return true; }
        }
        catch { return false; }

        string t = source.Trim();
        if (t.EndsWith(":"))
        {
            string noColon = t.TrimEnd(':').TrimEnd();
            if (noColon.Length > 0)
            {
                try
                {
                    string g2 = CLocalisationManager.GetText(noColon);
                    if (!string.IsNullOrEmpty(g2)
                        && !string.Equals(g2, noColon, StringComparison.Ordinal))
                    { resolved = g2 + ":"; return true; }
                }
                catch { }
            }
        }

        // LANG-41c: source có thể là English VALUE của key FE_* trong dict English của
        // game (menu UI: label text "General" = value của "FE_Options_General") —
        // GetText(English) miss vì nó không phải key. Tra ngược value → key rồi GetText
        // lại theo key (dữ liệu từ chính game, không hardcode).
        {
            string probe = t;
            string feKey = null;
            if (!GameEnglishValueToKey.TryGetValue(probe, out feKey)
                && probe.Length > 0 && (probe.EndsWith(":") || probe.EndsWith("\uFF1A")))
            {
                string noColon = probe.TrimEnd(':', '\uFF1A').TrimEnd();
                GameEnglishValueToKey.TryGetValue(noColon, out feKey);
            }
            // Official value (JP/IT/...) → FE key → GetText ngôn ngữ hiện tại
            if (TryMapAnyOfficialValueToKey(t, out string anyKey) && !string.IsNullOrEmpty(anyKey))
            {
                try
                {
                    string g4 = CLocalisationManager.GetText(anyKey);
                    if (!string.IsNullOrEmpty(g4)
                        && !string.Equals(g4, anyKey, StringComparison.Ordinal))
                    {
                        resolved = (t.EndsWith(":") || t.EndsWith("\uFF1A"))
                            && !g4.EndsWith(":") && !g4.EndsWith("\uFF1A")
                            ? g4 + ":" : g4;
                        return true;
                    }
                }
                catch { }
            }
            if (feKey == null)
            {
                string norm = NormalizeForMatch(probe);
                if (norm.Length > 0) GameEnglishValueToKeyNorm.TryGetValue(norm, out feKey);
            }
            if (!string.IsNullOrEmpty(feKey))
            {
                try
                {
                    string g3 = CLocalisationManager.GetText(feKey);
                    if (!string.IsNullOrEmpty(g3)
                        && !string.Equals(g3, feKey, StringComparison.Ordinal))
                    {
                        // Giữ ':' nếu nguồn có (mirror nhánh noColon phía trên).
                        resolved = (t.EndsWith(":") && !g3.EndsWith(":") && !g3.EndsWith("\uFF1A"))
                            ? g3 + ":" : g3;
                        return true;
                    }
                }
                catch { }
            }
        }

        // LANG-42: repack thiếu value → GetText trả nguyên tag (key thô). Fallback cuối:
        // lấy giá trị ENGLISH của key từ dict English của game — hiển thị English thay
        // vì key thô "FE_Options_*" (đúng hành vi English-fallback mà game LỠ xử lý sai
        // khi value rỗng trong GetTextFromDictionary).
        {
            var engDict = GetGameEnglishDict();
            if (engDict != null)
            {
                try
                {
                    string v;
                    if (engDict.TryGetValue(t, out v) && !string.IsNullOrEmpty(v) && v.Trim().Length > 0)
                    { resolved = v; return true; }
                    if (engDict.TryGetValue(t.ToLowerInvariant(), out v) && !string.IsNullOrEmpty(v) && v.Trim().Length > 0)
                    { resolved = v; return true; }
                }
                catch { }
            }
        }
        return false;
    }

    /// <summary>
    /// FIX v2.18: Force Reload UI — KHÔNG hardcode gì cả.
    /// Mọi dữ liệu đều từ file .txt trong Language/<LastLanguage>/.
    /// FIX LANG-20: cập nhật doc cho đúng thứ tự ưu tiên THẬT của code (doc cũ
    /// ghi ngược ưu tiên 2/3 so với hành vi từ thời FIX v0.27.2).
    /// Với mỗi UILabel, thử tra theo thứ tự:
    ///   0. KEY GỐC đọc từ component UILabelAutotranslate (FIX LANG-13) — phân
    ///      giải đúng ngữ cảnh khi 1 chuỗi English vừa là value của key FE_*
    ///      (menu) vừa là text-key kịch bản ("Single Player" / "Options"...)
    ///   1. TranslationDict (FE_* key gốc) — text label trùng key FE_*
    ///   2. EnglishToCustomDict (reverse lookup FE: English value → bản dịch FE) —
    ///      ưu tiên cho menu UI (an toàn cho UI chính)
    ///   3. EnglishTextDict (English text key) — cho kịch bản Board Game
    /// Nếu tìm thấy → set text. Không tìm thấy → skip.
    /// Không set uppercase, không build vietValues set trong code — tất cả từ file.
    /// </summary>
    public void RefreshAllLabels()
    {
        ClearOptionsSelectorLabelCache();
        // FIX v0.29: Chỉ translate khi custom language đang active.
        // Không đụng đến official languages (English, Chinese, etc.).
        if (!IsCustomLanguageActive())
        {
            Logger.LogInfo("[v0.29] RefreshAllLabels skip — custom language not active");
            return;
        }

        try
        {
            Logger.LogInfo("[v2.18] RefreshAllLabels begin — TranslationDict=" + TranslationDict.Count
                + ", EnglishTextDict=" + EnglishTextDict.Count
                + ", EnglishToCustomDict=" + EnglishToCustomDict.Count);

            UILabel[] labels = GetAllLabels();
            if (labels == null) return;

            // FIX v0.28.0: Detailed lookup trace for each ACTIVE UILabel
            // — trace qua 3 dict để biết chính xác text được dịch từ đâu
            // FIX v0.29.4: gate trace block bằng VerboseLogging — trước đây khối này
            // luôn chạy (chỉ check Logger != null, gần như luôn true) và với MỖI label
            // active lại duyệt TOÀN BỘ TranslationDict + EnglishTextDict để dò nguồn gốc
            // bản dịch (2 vòng lặp lồng nhau) — với vài trăm label × vài nghìn dòng dịch
            // có thể tốn hàng trăm nghìn phép so sánh string mỗi lần RefreshAllLabels()
            // chạy (mỗi lần đổi ngôn ngữ / mỗi lần force reload). Giờ chỉ chạy khi bật
            // VerboseLogging để chẩn đoán — không ảnh hưởng hiệu năng ở chế độ bình thường.
            if (Logger != null && VerboseLogging != null && VerboseLogging.Value)
            {
                Logger.LogInfo("[v0.28] === UILabel ACTIVE lookup trace ===");
                for (int k = 0; k < labels.Length; k++)
                {
                    UILabel labk = labels[k];
                    if (labk == null || labk.gameObject == null) continue;
                    try
                    {
                        if (labk.GetComponentInParent<OptionsSelector>() != null)
                            continue;
                    }
                    catch { }
                    if (!labk.gameObject.activeInHierarchy) continue;
                    string tk = labk.text;
                    if (string.IsNullOrEmpty(tk)) continue;

                    // Trace lookup
                    string traceResult = "NOT FOUND";
                    string traceSource = "none";
                    string v1;
                    // FIX v0.28.3: use lookupText for trace too
                    string traceLookup = tk;
                    try { if (OriginalTextCache.TryGetValue(labk, out string ot) && !string.IsNullOrEmpty(ot)) traceLookup = ot; } catch { }
                    // FIX LANG-13: trace cả nguồn ưu tiên 0 (key gốc từ component).
                    string traceKeyText = TryResolveLabelKeyText(labk);
                    if (traceKeyText != null && !string.IsNullOrEmpty(traceKeyText) && traceKeyText != tk)
                    { traceResult = traceKeyText; traceSource = "AutotranslateKey"; }
                    else if (TranslationDict.TryGetValue(traceLookup, out v1) && !string.IsNullOrEmpty(v1) && v1 != tk)
                    { traceResult = v1; traceSource = "TranslationDict"; }
                    else if (EnglishToCustomDict.TryGetValue(traceLookup, out v1) && !string.IsNullOrEmpty(v1))
                    { traceResult = v1; traceSource = "EnglishToCustomDict"; }
                    else if (EnglishTextDict.TryGetValue(traceLookup, out v1) && !string.IsNullOrEmpty(v1) && v1 != tk)
                    { traceResult = v1; traceSource = "EnglishTextDict"; }

                    // Also check: is this text a VALUE in any dict? (already translated)
                    bool isViValue = false;
                    foreach (var kv2 in TranslationDict) { if (kv2.Value == tk) { isViValue = true; break; } }
                    if (!isViValue) foreach (var kv2 in EnglishTextDict) { if (kv2.Value == tk) { isViValue = true; break; } }
                    if (isViValue) { traceResult = tk; traceSource = "already-VI"; }

                    Logger.LogInfo("[v0.28]   text=\"" + tk + "\" → \"" + traceResult + "\" [" + traceSource + "]");
                }
            }

            // FIX LANG-19: tách counter — trước đây "not in dict" gộp cả label
            // ĐÃ dịch xong (text hiện tại là value tiếng Việt, không còn match
            // key) → con số gây hiểu sai khi đọc log (log thật 0.31.0: "7994 not
            // in dict" trong khi phần lớn trong số đó là đã dịch).
            int refreshed = 0;
            int skippedNotFound = 0;
            int alreadyTranslated = 0;
            int keyResolved = 0;
            int fallbackResolved = 0; // FIX OPT-02

            // FIX LANG-19: dựng tập VALUE tiếng Việt 1 lần TRƯỚC vòng lặp — dùng
            // (1) phân loại label đã dịch ở nhánh else, (2) lọc danh sách "chưa
            // dịch" phía dưới (trước đây chỉ dựng khi skippedNotFound > 0 và
            // thiếu ScenarioDict).
            var viValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in TranslationDict)
                if (!string.IsNullOrEmpty(kv.Value)) viValues.Add(kv.Value);
            foreach (var kv in EnglishTextDict)
                if (!string.IsNullOrEmpty(kv.Value)) viValues.Add(kv.Value);
            foreach (var kv in EnglishToCustomDict)
                if (!string.IsNullOrEmpty(kv.Value)) viValues.Add(kv.Value);
            foreach (var kv in ScenarioDict)
                if (!string.IsNullOrEmpty(kv.Value)) viValues.Add(kv.Value);
            for (int i = 0; i < labels.Length; i++)
            {
                UILabel lab = labels[i];
                if (lab == null) continue;
                try { if (IsPopupRelated(lab)) continue; } catch { }
                try { if (IsOptionsSelectorRelated(lab)) continue; } catch { }
                try { if (!FontRestore.IsLiveSceneObject(lab)) continue; } catch { }
                string currentText = lab.text;
                if (string.IsNullOrEmpty(currentText)) continue;

                // FIX v0.28.3: Dùng text gốc (trước khi translate) cho lookup.
                // Nếu UILabel đã được translate trước đó, text hiện tại là Vietnamese value
                // — không match key trong dict mới. Dùng OriginalTextCache để lấy text gốc.
                // FIX v0.29.2/v0.29.3: Skip ALL popup/dropdown UILabels — dùng chung
                // helper IsPopupRelated() (cùng logic áp dụng ở OnEnable Prefix/Postfix,
                // PrepareLabelFontBeforeEnable, TryApplyFontToLabelHard, ApplyFontsToAllLabels
                // — tránh lệch logic giữa các nơi).
                try { if (IsPopupRelated(lab)) continue; }
                catch { }
                try { if (!FontRestore.IsLiveSceneObject(lab)) continue; }
                catch { }

                // ===== [TRACE] tạm — chụp trạng thái cache TRƯỚC khi call nào ghi đè =====
                bool _traceWatch = TraceWatchList.Count > 0 &&
                    (TraceWatchList.Contains(currentText) ||
                     (lab.gameObject != null && ContainsAnyWatchName(lab.gameObject.name)));
                bool _hadOrigBefore = OriginalTextCache.TryGetValue(lab, out string _preOrig) && !string.IsNullOrEmpty(_preOrig);
                bool _hadKeyBefore = LabelSourceKeyCache.TryGetValue(lab, out string _preKey) && !string.IsNullOrEmpty(_preKey);
                string _priorIdentity = _hadKeyBefore ? _preKey : (_hadOrigBefore ? _preOrig : null);

                bool _resetTrace = lab.gameObject != null &&
                    (lab.gameObject.name.IndexOf("Reset", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     TraceWatchList.Contains(currentText));
                string _rrRawKeyBefore = _resetTrace ? TryGetLabelRawKey(lab) : null;

                string lookupText = currentText;
                try
                {
                    CacheOriginalTextIfAbsent(lab, currentText);
                    // LANG-40: capture KEY nguồn từ component TRƯỚC khi stamp bản dịch —
                    // đây là thời điểm duy nhất component còn nguyên (chưa bị poison bởi
                    // lần AutoTranslate đầu chạy trên text đã dịch).
                    CacheLabelSourceKey(lab, TryGetLabelRawKey(lab));
                    if (OriginalTextCache.TryGetValue(lab, out string origText)
                        && !string.IsNullOrEmpty(origText))
                        lookupText = origText;
                    lookupText = ResolveSourceEnglish(lookupText);
                }
                catch { }

                string newText = null;
                string _resolver = "none";

                // FIX LANG-13 (ưu tiên 0): tra theo KEY GỐC của label — đọc từ
                // component UILabelAutotranslate nếu game gắn component đó cho
                // label. Đây là cách phân giải DUY NHẤT đúng ngữ cảnh khi 1 chuỗi
                // English vừa là value của key FE_* (menu UI: "FE_Single_Player"
                // = "Chơi đơn") vừa là key text thuần của kịch bản (Board Game:
                // "Single Player" = "1 Người") — tra thuần text không thể phân
                // biệt 2 ngữ cảnh này. Không đọc được key (component không có /
                // field không khớp) → tự rơi xuống chuỗi ưu tiên 1-3 như cũ.
                string keyText = TryResolveLabelKeyText(lab);
                if (keyText != null)
                {
                    // FIX RAL-01: keyText != null nghĩa là component có canonical FE
                    // identity VÀ đã phân giải thành công — bất kể kết quả có trùng
                    // currentText hay không. Trước đây khi keyText == currentText
                    // (đã đúng từ pass trước), code coi như "priority-0 không áp dụng"
                    // và rơi xuống EnglishTextDict/EnglishToCustomDict tra theo
                    // lookupText (= English source đã đóng băng) — English source đó
                    // có thể trùng 1 entry KHÔNG LIÊN QUAN trong EnglishTextDict (vd
                    // "Single Player" dùng cho đếm người chơi trong kịch bản, khác hẳn
                    // FE_Single_Player của menu) → ghi đè sai. Khi đã có canonical FE
                    // identity, KHÔNG BAO GIỜ được rơi xuống reverse lookup nữa — dừng
                    // tại đây, dù có đổi text hay không.
                    if (!string.Equals(keyText, currentText, StringComparison.Ordinal))
                    {
                        newText = keyText;
                        keyResolved++;
                        _resolver = "AutotranslateKey(priority0)";
                    }
                    else
                    {
                        newText = null; // đã đúng, không cần ghi lại — nhưng KHÔNG fallthrough
                        _resolver = "AutotranslateKey(priority0,unchanged)";
                    }
                }
                // Ưu tiên 1: TranslationDict (FE_* key gốc)
                else if (TranslationDict.TryGetValue(lookupText, out newText) &&
                    !string.IsNullOrEmpty(newText) &&
                    !string.Equals(newText, currentText, StringComparison.Ordinal))
                {
                    _resolver = "TranslationDict";
                }
                // Ưu tiên 2: EnglishTextDict — "Options" = "Tùy chọn" thắng FE reverse "Cài Đặt"
                else if (EnglishTextDict.TryGetValue(lookupText, out newText) &&
                         !string.IsNullOrEmpty(newText) &&
                         !string.Equals(newText, currentText, StringComparison.Ordinal))
                {
                    _resolver = "EnglishTextDict(REVERSE)";
                }
                else if (EnglishToCustomDict.Count > 0 &&
                         EnglishToCustomDict.TryGetValue(lookupText, out newText) &&
                         !string.IsNullOrEmpty(newText) &&
                         !string.Equals(newText, currentText, StringComparison.Ordinal))
                {
                    _resolver = "EnglishToCustomDict(REVERSE)";
                }
                // FIX OPT-02: ưu tiên 4 — text đang là KEY THÔ (bị stamp từ lần switch
                // official trước khi GetText miss, hoặc label spawn muộn) mà dict mod
                // không có → fallback dữ liệu ngôn ngữ game (GetText tự English-fallback
                // nội bộ). Label hiển thị giá trị của game thay vì key "FE_Options_*".
                // TryResolveLabelText KHÔNG BAO GIỜ trả lại chính key → hết hiện tượng
                // màn Options giữ nguyên key thô sau khi đổi ngôn ngữ qua lại.
                else if (ConfigManager.IsFeKey(lookupText) &&
                         TryResolveLabelText(lookupText, out string fbText) &&
                         !string.IsNullOrEmpty(fbText) &&
                         !string.Equals(fbText, currentText, StringComparison.Ordinal))
                {
                    newText = fbText;
                    fallbackResolved++;
                    _resolver = "GameFallback(FeKey)";
                }
                else
                {
                    newText = null;
                }

                if (_resetTrace)
                {
                    LabelSourceKeyCache.TryGetValue(lab, out string _rrLskAfter);
                    OriginalTextCache.TryGetValue(lab, out string _rrOtcAfter);
                    Debug.Log("[TRACE-RESET]"
                        + "\n  go = " + (lab.gameObject != null ? lab.gameObject.name : "?")
                        + "\n  text.before = \"" + currentText + "\""
                        + "\n  rawKey(before capture) = " + (_rrRawKeyBefore ?? "(null)")
                        + "\n  OriginalTextCache = " + (_rrOtcAfter ?? "(none)")
                        + "\n  LabelSourceKeyCache = " + (_rrLskAfter ?? "(none)")
                        + "\n  TryResolveLabelKeyText = " + (keyText ?? "(null)")
                        + "\n  lookupText = \"" + lookupText + "\""
                        + "\n  resolver = " + _resolver
                        + "\n  translated = " + (newText != null ? "\"" + newText + "\"" : "(null)")
                        + "\n  text.after = " + (newText != null ? "\"" + newText + "\"" : "\"" + currentText + "\" (unchanged)"));
                }

                if (_traceWatch)
                {
                    bool isReverse = _resolver.IndexOf("REVERSE", StringComparison.Ordinal) >= 0;
                    Debug.Log("[TRACE] UILabel " + (lab.gameObject != null ? lab.gameObject.name : "?")
                        + "\n  text.before = \"" + currentText + "\""
                        + "\n  priorIdentity(before pass) = " + (_priorIdentity ?? "(none)")
                        + "\n  sourceKey(LabelSourceKeyCache after capture) = " + (LabelSourceKeyCache.TryGetValue(lab, out string _skAfter) ? _skAfter : "(none)")
                        + "\n  sourceEnglish(lookupText) = \"" + lookupText + "\""
                        + "\n  resolver = " + _resolver
                        + "\n  translated = " + (newText != null ? "\"" + newText + "\"" : "(null)")
                        + "\n  text.after = " + (newText != null ? "\"" + newText + "\"" : "\"" + currentText + "\" (unchanged)"));

                    if (isReverse)
                    {
                        Debug.Log("[TRACE] REVERSE FALLBACK"
                            + "\n  input = \"" + lookupText + "\""
                            + "\n  ValueToEnglish => " + (TryMapValueToEnglish(lookupText, out string _v2e) ? "\"" + _v2e + "\"" : "(miss)")
                            + (_hadKeyBefore
                                // Chỉ cảnh báo khi identity trước đó là CANONICAL FE KEY
                                // (LabelSourceKeyCache) — đây mới là trường hợp nguy hiểm
                                // (bug Single Player). Nếu identity trước đó chỉ là
                                // OriginalTextCache (English text gốc, KHÔNG có FE key —
                                // case AUTHORITY/IG_Complete: label không có
                                // UILabelAutotranslate/field FE_* nào để bám vào), reverse
                                // lookup là CƠ CHẾ DUY NHẤT và ĐÚNG THIẾT KẾ cho các label
                                // này — không phải bug, không cảnh báo.
                                ? "\n  WARNING: label đã có CANONICAL FE identity = \"" + _preKey + "\" — reverse lookup này ĐANG GHI ĐÈ lên FE identity đã đóng băng! (đây là bug thật)"
                                : (_hadOrigBefore
                                    ? "\n  (label KHÔNG có FE key — chỉ có English text gốc \"" + _preOrig + "\" — reverse lookup lặp lại mỗi pass là ĐÚNG THIẾT KẾ cho loại label này, vd stat/live-value label)"
                                    : "\n  (label chưa có source identity trước pass này — reverse lookup ở đây là hợp lệ theo thiết kế)")));
                    }
                }

                if (newText != null)
                {
                    lab.text = newText;
                    refreshed++;
                }
                else
                {
                    // FIX LANG-19: phân loại rõ "đã dịch" (text hiện tại là value
                    // tiếng Việt) thay vì gộp hết vào "not in dict".
                    if (viValues.Contains(currentText)) alreadyTranslated++;
                    else skippedNotFound++;
                }
            }
            Logger.LogInfo("[v2.18] Refresh " + refreshed + " UILabel (by-key " + keyResolved
                + ", game-fallback " + fallbackResolved + ", already " + alreadyTranslated
                + ", " + skippedNotFound + " not in dict)");

            // Log các UILabel ACTIVE không tìm được — giúp user biết text nào cần thêm vào file
            // FIX: build set Vietnamese VALUES (không phải keys) để skip text đã dịch.
            // Trước đây chỉ check key → text đã dịch (Vietnamese value) bị log sai là "chưa dịch".
            // FIX LANG-19: viValues đã dựng trước vòng lặp — không dựng lại ở đây.
            if (skippedNotFound > 0)
            {
                var notFoundActive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int j = 0; j < labels.Length; j++)
                {
                    UILabel lab2 = labels[j];
                    if (lab2 == null || lab2.gameObject == null) continue;
                    if (!lab2.gameObject.activeInHierarchy) continue;
                    string t = lab2.text;
                    if (string.IsNullOrEmpty(t)) continue;
                    // Skip nếu text đã là Vietnamese value
                    if (viValues.Contains(t)) continue;
                    // Skip nếu text là key có trong dict
                    if (TranslationDict.ContainsKey(t)) continue;
                    if (EnglishTextDict.ContainsKey(t)) continue;
                    if (EnglishToCustomDict.ContainsKey(t)) continue;
                    notFoundActive.Add(t);
                }
                if (notFoundActive.Count > 0)
                {
                    Logger.LogInfo("[v2.18] === UILabel ACTIVE chưa dịch (" + notFoundActive.Count + ") ===");
                    foreach (var t in notFoundActive)
                        Logger.LogInfo("[v2.18]   text=\"" + t + "\"");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[v2.18] RefreshAllLabels error: " + ex.Message);
        }
    }

    /// <summary>
    /// true nếu đây là pack custom (có thư mục dưới Language/), không phải English/Spanish/...
    /// </summary>
    // CFG-05: cache tên pack custom ĐÃ XÁC NHẬN tồn tại — Directory.Exists là
    // IO call; IsCustomLanguageFolder được gọi từ các Harmony postfix (đổi ngôn
    // ngữ) và boot flow, cache tránh gọi IO lặp lại.
    static readonly object _customFolderCacheLock = new object();
    static readonly HashSet<string> _customFolderCache =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static bool IsCustomLanguageFolder(string langName)
    {
        if (string.IsNullOrEmpty(langName)) return false;
        // Awake chưa chạy / config hỏng → inert, không coi gì là custom.
        if (string.IsNullOrEmpty(LanguageFolder)) return false;

        // CFG-08 (Zero-Hardcode): danh sách ngôn ngữ official đọc từ config
        // ([Language] OfficialLanguages) — bỏ mảng 30 tên hardcode trong code.
        if (ConfigManager.IsOfficialLanguage(langName)) return false;

        lock (_customFolderCacheLock)
        {
            if (_customFolderCache.Contains(langName)) return true;
        }

        bool exists = false;
        try { exists = Directory.Exists(Path.Combine(LanguageFolder, langName)); }
        catch { }
        if (exists)
        {
            lock (_customFolderCacheLock) { _customFolderCache.Add(langName); }
        }
        return exists;
    }

    // ===================== FONT CORE =====================

    static void RegisterPrivateFonts(string fontDir)
    {
        // CFG-08: có thể tắt AddFontResourceEx qua config ([Fonts] RegisterPrivateFonts).
        if (!ConfigManager.RegisterPrivateFonts) return;
        if (!Directory.Exists(fontDir)) return;
        foreach (var path in Directory.GetFiles(fontDir, "*.*"))
        {
            if (!path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                int n = TryAddFontResourceEx(path, FONT_ADD_FLAGS);
                if (n > 0)
                {
                    PrivateFontPaths.Add(path);
                    BroadcastFontChange();
                    Debug.Log("[Localizer] Private font OK: " + Path.GetFileName(path));
                }
                else
                    Debug.LogWarning("[Localizer] Private font FAIL: " + Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Localizer] Private font error: " + ex.Message);
            }
        }
    }

    public static void UnregisterPrivateFonts()
    {
        if (PrivateFontPaths.Count == 0) return;
        foreach (var path in PrivateFontPaths)
        {
            try { TryRemoveFontResourceEx(path, FONT_ADD_FLAGS); } catch { }
        }
        PrivateFontPaths.Clear();
        BroadcastFontChange();
    }

    // FIX (rò rỉ bộ nhớ tiềm ẩn): trước đây có 2 hàm huỷ font gần giống nhau
    // (DestroyLoadedFonts không nơi nào gọi, DestroyCustomFonts thì được dùng nhưng
    // chỉ huỷ FontBd/Md/Lt chứ không huỷ hết CustomFonts.Values). Gộp lại 1 hàm duy
    // nhất, huỷ đầy đủ + dedup theo instance ID (một Font có thể được gán vào nhiều
    // key khác nhau trong CustomFonts).
    public static void DestroyCustomFonts()
    {
        var seen = new HashSet<int>();
        DestroyOneFont(FontBd, seen);
        DestroyOneFont(FontMd, seen);
        DestroyOneFont(FontLt, seen);
        foreach (var f in CustomFonts.Values)
            DestroyOneFont(f, seen);
        // FIX ST-02: huỷ UIFont proxy tạo runtime — tránh NRE khi đổi lại custom lang.
        foreach (var proxy in ReplacementUIFonts.Values)
        {

            {
                try
                {
                    if (FontProxyHost != null)
                    {
                        UnityEngine.Object.Destroy(FontProxyHost);
                        FontProxyHost = null;
                    }
                }
                catch { }
            }
        }
        ReplacementUIFonts.Clear();

        // Chỉ Destroy Font object SAU RestoreUIFonts (caller đảm bảo)
        try { if (FontBd != null) UnityEngine.Object.Destroy(FontBd); } catch { }
        try { if (FontMd != null) UnityEngine.Object.Destroy(FontMd); } catch { }
        try { if (FontLt != null) UnityEngine.Object.Destroy(FontLt); } catch { }

        FontBd = null;
        FontMd = null;
        FontLt = null;
        FontsReady = false;
    }

    static void DestroyOneFont(Font f, HashSet<int> seen)
    {
        if (f == null) return;
        int id = f.GetInstanceID();
        if (!seen.Add(id)) return;
        try { UnityEngine.Object.Destroy(f); } catch { }
    }

    public static void BindActiveLanguagePack(string langName)
    {
        // CFG-04: hàm này chạy BÊN TRONG set_ActiveLanguage của game (postfix khi
        // user đổi ngôn ngữ in-game) — exception phải được nuốt ở đây, KHÔNG văng
        // vào luồng game; fail-safe tắt gate font.
        try { BindActiveLanguagePackInner(langName); }
        catch (Exception ex)
        {
            Debug.LogWarning("[Localizer] BindActiveLanguagePack lỗi: " + ex.Message);
            try { FontsReady = false; } catch { }
        }
    }

    static void BindActiveLanguagePackInner(string langName)
    {
        if (string.IsNullOrEmpty(langName) || string.IsNullOrEmpty(LanguageFolder)) return;

        TranslationDict.Clear();
        EnglishTextDict.Clear();
        try
        {
            var field = typeof(CLocalisationManager).GetField(
                "mpLocalisedTexts", BindingFlags.NonPublic | BindingFlags.Static);
            var all = field != null
                ? field.GetValue(null) as Dictionary<string, Dictionary<string, string>>
                : null;
            Dictionary<string, string> dict = null;
            if (all != null) all.TryGetValue(langName, out dict);
            if (dict != null)
            {
                // FIX v0.28.1: split FE_* and non-FE_* — không trộn lẫn
                foreach (var kv in dict)
                {
                    if (ConfigManager.IsFeKey(kv.Key))
                        TranslationDict[kv.Key] = kv.Value;
                    else
                        EnglishTextDict[kv.Key] = kv.Value;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[Localizer] Bind dict: " + ex.Message);
        }

        CurrentCustomLanguage = langName;
        CMainAboutModSubScreen.LangFolder = Path.Combine(LanguageFolder, langName);

        // FIX LANG-14: rebuild reverse lookup English→custom cho pack VỪA bind.
        try { BuildEnglishToCustomDict(); } catch { }
        try { RebuildTranslationIndex(); } catch { }

        // LANG-25: KHÔNG RestoreAll/DestroyCustomFonts trong stack set_ActiveLanguage
        // (dropdown đang rebuild). Font + RefreshAllLabels chạy frame sau.
        FontsReady = false;
        int epoch = ++_langEpoch;
        if (Instance != null)
            Instance.StartCoroutine(Instance.LoadFontsAndRefreshDeferred(langName, epoch));
        else
        {
            try { TryLoadCustomFonts(Path.Combine(LanguageFolder, langName)); } catch { }
            try { if (Instance != null) Instance.RefreshAllLabels(); } catch { }
        }
    }

    static void TryLoadCustomFonts(string langFolder)
    {
        // CFG-04: toàn thân bọc try/catch — hàm chạy BÊN TRONG setter
        // set_ActiveLanguage của game (qua BindActiveLanguagePack khi user đổi
        // ngôn ngữ in-game); exception ở đây làm đứt luồng game.
        try
        {
            // CFG-08: tên folder font lấy từ config ([Fonts] FontFolderName).
            string fontDir = Path.Combine(langFolder, ConfigManager.FontFolderName);
            if (!Directory.Exists(fontDir))
            {
                Debug.LogWarning("[Localizer] Không có folder "
                    + ConfigManager.FontFolderName + ": " + fontDir);
                FontsReady = false;
                bool had = FontBd != null || FontMd != null || FontLt != null || CustomFonts.Count > 0;
                if (had)
                {
                    FontRestore.RestoreAll();
                    FontRestore.RestoreUIFonts();
                    DestroyCustomFonts();
                }
                CustomFonts.Clear();
                FontBd = null; FontMd = null; FontLt = null;
                UnregisterPrivateFonts();
                return;
            }

            FontsReady = false;
            bool hadCustomFonts = FontBd != null || FontMd != null || FontLt != null || CustomFonts.Count > 0;
            if (hadCustomFonts)
            {
                FontRestore.RestoreAll();
                FontRestore.RestoreUIFonts();
                DestroyCustomFonts();
            }
            CustomFonts.Clear();
            FontBd = null; FontMd = null; FontLt = null;
            UnregisterPrivateFonts();

            // FIX MN-08: ensure manifest trước khi load.
            EnsureFontManifest(fontDir);

            if (File.Exists(Path.Combine(fontDir, ConfigManager.JsonConfigFileName)))
                LoadFontsFromJson(fontDir);

            if (!FontsReady)
            {
                string manifestPath = Path.Combine(fontDir, ConfigManager.ManifestFileName);
                if (File.Exists(manifestPath))
                    LoadFontsFromManifest(fontDir, manifestPath);
            }

            FontsReady = FontBd != null || FontMd != null || FontLt != null || CustomFonts.Count > 0;
            Debug.Log("[Localizer] FontsReady=" + FontsReady
                + " Bd=" + (FontBd != null) + " Md=" + (FontMd != null) + " Lt=" + (FontLt != null));

            if (FontsReady)
            {
                ApplyFontsToAllUIFonts();
                ApplyFontsToAllLabels();
            }
            else
                FontRestore.RestoreAll();
        }
        catch (Exception ex)
        {
            // CFG-04 fail-safe: mọi đường lỗi đều kết thúc với gate tắt + restore —
            // tuyệt đối không để exception văng vào luồng của game.
            FontsReady = false;
            try { FontRestore.RestoreAll(); } catch { }
            try { FontRestore.RestoreUIFonts(); } catch { }
            Debug.LogWarning("[Localizer] TryLoadCustomFonts lỗi: " + ex.Message);
        }
    }

    // FIX MN-07: removed dead TryFallback method.

    /// <summary>
    /// Gán mDynamicFont trực tiếp + MarkAsDirty (path bitmap còn sót).
    /// Không dùng property dynamicFont để tránh đệ quy với Harmony getter.
    /// </summary>
    public static void ApplyFontsToAllUIFonts()
    {
        // BẮT BUỘC no-op. Nếu hàm này còn tạo proxy / ghi mDynamicFont /
        // mReplacement lên Font_HelveticaNeueStd_Med_40 → dropdown trống.
        if (VerboseLogging != null && VerboseLogging.Value)
            Debug.Log("[Localizer] ApplyFontsToAllUIFonts: no-op");
    }

    public static string NormalizeFontName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        string s = name.Trim();
        if (s.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
            s.EndsWith(".otf", StringComparison.OrdinalIgnoreCase) ||
            s.EndsWith(".fontsettings", StringComparison.OrdinalIgnoreCase))
            s = Path.GetFileNameWithoutExtension(s);
        s = s.Replace('-', '_').Replace(' ', '_');
        while (s.Contains("__")) s = s.Replace("__", "_");
        return s.Trim('_');
    }

    /// <summary>
    /// Tìm Font thay thế theo tên UIFont (không gọi property dynamicFont).
    /// </summary>
    public static Font FindFontForUIFont(UIFont uiFont)
    {
        if (!FontsReady) return null;
        if (uiFont == null) return FontMd ?? FontBd ?? FontLt;
        return FontForWeight(DetectWeight(uiFont.name) ?? "Md");
    }



    // FIX MN-01 + MN-02: delegate to FontRestore — tránh duplicate logic giữa 2 file.
    static string DetectWeight(string name) => FontRestore.DetectWeight(name);
    static Font FontForWeight(string w) => FontRestore.FontForWeight(w);



    /// <summary>
    /// Dùng bởi Harmony get_dynamicFont.
    /// </summary>
    public static Font ResolveDynamicFont(UIFont uiFont, Font original)
    {
        if (!IsCustomLanguageActive() || !FontsReady) return original;
        Font rep = FindFontForUIFont(uiFont);
        return rep ?? original;
    }

    /// <summary>
    /// Fallback nhẹ cho label đã snapshot trueTypeFont trước khi font load xong.
    /// Không gọi từ set_trueTypeFont / set_text (tránh đệ quy).
    /// </summary>
    // FIX MN-03: removed dead LabelWeightCache — logic nằm ở FontRestore.WeightTable.

    public static Font PickHardcodedFont(UILabel label)
    {
        return FontRestore.Pick(label);
    }

    // FIX MN-04: removed dead FieldInfo/MethodInfo (FiUseDyn, FiChanged, MiSetActive, MiProcess, MiPanelDirty).
    // FIX CR-13: removed tiếp FiMFont/FiMTtf — 2 FieldInfo khai báo trong PlagueVnMod
    // nhưng không có chỗ nào dùng (class UILabel_OnEnable_Combined_Patch có bản copy riêng).

    public static void TryApplyFontToLabelHard(UILabel lab)
    {
        if (lab == null || !FontsReady || !IsCustomLanguageActive()) return;
        if (IsPopupRelated(lab)) return;
        if (ApplyingFont) return;

        Font f = FontRestore.Pick(lab);
        if (f == null) return;

        ApplyingFont = true;
        try
        {
            FontRestore.Capture(lab);
            // CHỈ gán trên label — KHÔNG FiUIFontDynamic / KHÔNG proxy / KHÔNG mReplacement
            lab.trueTypeFont = f;
            // nếu cần clear bitmap để NGUI dùng TTF:
            // lab.bitmapFont = null;
        }
        catch { }
        finally { ApplyingFont = false; }
    }

    public static void TrackFontName(string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        string clean = name;
        if (clean.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
            clean.EndsWith(".otf", StringComparison.OrdinalIgnoreCase) ||
            clean.EndsWith(".fontsettings", StringComparison.OrdinalIgnoreCase))
            clean = Path.GetFileNameWithoutExtension(clean);
        SeenGameFonts.Add(clean);
    }

    public static void ScanAllGameFonts()
    {
        try
        {
            var labels = UnityEngine.Object.FindObjectsOfType<UILabel>();
            foreach (var label in labels)
            {
                if (label == null) continue;
                if (label.trueTypeFont != null)
                    TrackFontName(label.trueTypeFont.name);
                if (label.bitmapFont != null)
                    TrackFontName(label.bitmapFont.name);
            }

            var uifonts = Resources.FindObjectsOfTypeAll(typeof(UIFont)) as UIFont[];
            if (uifonts != null)
            {
                foreach (var f in uifonts)
                {
                    if (f != null) TrackFontName(f.name);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[Localizer] Scan font lỗi: " + ex.Message);
        }
    }

    public static void LogSeenFonts()
    {
        if (SeenGameFonts.Count == 0)
        {
            Debug.Log("[Localizer] Chưa thấy font nào trong game.");
            return;
        }
        Debug.Log("===== FONT CÓ TRONG GAME =====");
        foreach (var name in SeenGameFonts.OrderBy(x => x))
            Debug.Log(" - " + name);
        Debug.Log("===== TỔNG: " + SeenGameFonts.Count + " font =====");
    }

    // ===================== SCENARIO =====================
    // FIX S-01: sanitize scenarioId — chỉ giữ [a-zA-Z0-9._-].
    static string SanitizeScenarioId(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.')
                sb.Append(c);
        }
        return sb.ToString();
    }

    public static void ApplyScenarioTranslation(string scenarioId)
    {
        if (string.IsNullOrEmpty(scenarioId) || !IsCustomLanguageActive()) return;

        // FIX S-01: sanitize scenarioId.
        scenarioId = SanitizeScenarioId(scenarioId);
        if (string.IsNullOrEmpty(scenarioId)) return;

        ScenarioDict.Clear();
        CurrentScenarioId = scenarioId;

        // CFG-08: tên folder + đuôi file kịch bản lấy từ config
        // ([Language] ScenarioFolderName / ScenarioFileSuffixes).
        string scenariosRoot = Path.Combine(LanguageFolder, CurrentCustomLanguage, ConfigManager.ScenarioFolderName);
        string scenarioFile = null;
        string[] scenarioSuffixes = ConfigManager.ScenarioFileSuffixes;
        for (int sI = 0; sI < scenarioSuffixes.Length; sI++)
        {
            string candidate = Path.Combine(scenariosRoot, scenarioId + scenarioSuffixes[sI]);
            if (File.Exists(candidate)) { scenarioFile = candidate; break; }
        }

        if (scenarioFile == null)
        {
            Debug.Log("[Localizer] Không tìm thấy file scenario: " + scenarioId);
            return;
        }

        // FIX S-01: verify final path nằm trong scenariosRoot.
        try
        {
            string fullRoot = Path.GetFullPath(scenariosRoot);
            string fullFile = Path.GetFullPath(scenarioFile);
            if (!fullFile.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogError("[Localizer] Path traversal blocked: " + scenarioId);
                return;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[Localizer] Path resolve error: " + ex.Message);
            return;
        }

        var dict = LoadTranslationFileStatic(scenarioFile);
        if (dict.Count == 0) return;

        foreach (var kv in dict)
            ScenarioDict[kv.Key] = kv.Value;

        var field = typeof(CLocalisationManager).GetField(
            "diseaseLocalisationText", BindingFlags.NonPublic | BindingFlags.Static);
        if (field != null)
        {
            var diseaseDict = field.GetValue(null) as Dictionary<string, string>;
            if (diseaseDict == null)
            {
                diseaseDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                field.SetValue(null, diseaseDict);
            }
            foreach (var kv in dict)
            {
                diseaseDict[kv.Key] = kv.Value;
                // FIX ST-07: bỏ ToLower — dict đã OrdinalIgnoreCase.
            }
            var iterField = typeof(CLocalisationManager).GetField(
                "ActiveLocalisationIteration", BindingFlags.NonPublic | BindingFlags.Static);
            if (iterField != null)
            {
                int cur = (int)iterField.GetValue(null);
                iterField.SetValue(null, cur + 1);
            }
        }

        Debug.Log("[Localizer] Đã apply scenario [" + scenarioId + "] → " + dict.Count + " câu");
    }

    public static void ClearScenarioTranslation()
    {
        ScenarioDict.Clear();
        CurrentScenarioId = null;
    }

    // FIX ST-08: cache dict theo path + LastWriteTime.
    // CFG-11 (thread-safety): _fileCache đọc/ghi dưới lock — hiện toàn bộ load
    // chạy trên main thread (Invoke/coroutine/Harmony postfix), lock rẻ và an
    // toàn nếu sau này có đường load/reload từ thread khác.
    static readonly object _fileCacheLock = new object();
    static readonly Dictionary<string, (DateTime mtime, Dictionary<string, string> dict)> _fileCache
        = new Dictionary<string, (DateTime, Dictionary<string, string>)>(StringComparer.OrdinalIgnoreCase);

    // FIX S-07: static compiled Regex với timeout 5s tránh ReDoS.
    static readonly Regex _translationRegex = new Regex(
        @"""((?:[^""\\]|\\.)*)""\s*=\s*""((?:[^""\\]|\\.)*)""\s*;?",
        RegexOptions.Singleline,
        TimeSpan.FromSeconds(5));

    // ===================== PLACEHOLDER / FORMAT VALIDATION =====================
    // (FIX LANG-01/LANG-02) Bộ kiểm tra tính toàn vẹn placeholder của bản dịch.
    // Game dùng string.Format(GetText(...), args) cho rất nhiều chuỗi (kịch bản,
    // HUD, sự kiện...) — một bản dịch có brace hỏng cú pháp hoặc placeholder
    // {N} vượt số arg game truyền vào sẽ ném FormatException NGAY TRONG CODE GAME
    // (game không try/catch ở đó) → crash toàn game. Mọi entry đều được kiểm tra
    // tại thời điểm load file dịch — entry nguy hiểm bị BỎ (fallback English an
    // toàn) và cảnh báo rõ file + số dòng để tác giả sửa.

    // 64 arg "mồi" — đủ cho mọi chuỗi thực tế. string.Format ném FormatException
    // khi brace hỏng cú pháp (lone '{'/'}}', '{0' không đóng) hoặc index >= 64.
    static readonly object[] _formatProbeArgs = BuildFormatProbeArgs();

    static object[] BuildFormatProbeArgs()
    {
        var a = new object[64];
        for (int i = 0; i < a.Length; i++) a[i] = null;
        return a;
    }

    /// <summary>
    /// true nếu chuỗi đi qua string.Format mà KHÔNG ném FormatException bất kể
    /// số arg (chỉ phụ thuộc cú pháp brace). Dry-run với 64 arg mồi.
    /// </summary>
    static bool IsFormatSyntaxSafe(string s)
    {
        if (string.IsNullOrEmpty(s)) return true;
        if (s.IndexOf('{') < 0 && s.IndexOf('}') < 0) return true;
        try
        {
            string.Format(System.Globalization.CultureInfo.InvariantCulture, s, _formatProbeArgs);
            return true;
        }
        catch (FormatException) { return false; }
    }

    static readonly Regex _fmtIndexRegex = new Regex(@"\{\s*(\d+)",
        RegexOptions.Compiled, TimeSpan.FromSeconds(2));

    /// <summary>Index số cao nhất trong các placeholder {N}; -1 nếu không có.</summary>
    static int MaxFormatIndex(string s)
    {
        if (string.IsNullOrEmpty(s)) return -1;
        int max = -1;
        MatchCollection ms = _fmtIndexRegex.Matches(s);
        for (int i = 0; i < ms.Count; i++)
        {
            int v;
            if (int.TryParse(ms[i].Groups[1].Value, out v) && v > max) max = v;
        }
        return max;
    }

    /// <summary>
    /// FIX LANG-02: đối chiếu placeholder của MỌI ngôn ngữ custom với English gốc
    /// (mpLocalisedTexts["English"]). Entry có {N} với N vượt index cao nhất của
    /// English cùng key → game string.Format với đúng (engMax+1) arg sẽ ném
    /// FormatException → LOẠI entry đó khỏi dict (fallback English an toàn) +
    /// cảnh báo để tác giả sửa file. Chỉ áp dụng cho key CÓ trong English dict —
    /// key do mod tự thêm không bao giờ được game Format nên được giữ nguyên.
    /// Đồng thời cảnh báo (không loại) khi: bản dịch bỏ mất placeholder gốc,
    /// lệch số lượng %s/%d (printf-style), thiếu [TOKEN] hoa kiểu [NAME].
    /// </summary>
    static int ValidatePlaceholdersAgainstEnglish()
    {
        try
        {
            var field = typeof(CLocalisationManager).GetField(
                "mpLocalisedTexts", BindingFlags.NonPublic | BindingFlags.Static);
            if (field == null) return 0;
            var all = field.GetValue(null) as Dictionary<string, Dictionary<string, string>>;
            if (all == null) return 0;

            Dictionary<string, string> engDict = null;
            foreach (var kv in all)
            {
                if (kv.Key.Equals(ConfigManager.ReferenceLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    engDict = kv.Value;
                    break;
                }
            }
            if (engDict == null || engDict.Count == 0) return 0;

            int removed = 0;
            int warnings = 0;
            foreach (var langKv in all)
            {
                if (langKv.Key.Equals(ConfigManager.ReferenceLanguage, StringComparison.OrdinalIgnoreCase)) continue; // CFG-08
                var d = langKv.Value;
                if (d == null || d.Count == 0) continue;

                List<string> toRemove = null;
                foreach (var kv in d)
                {
                    string v = kv.Value;
                    if (string.IsNullOrEmpty(v)) continue;

                    string engV;
                    if (!engDict.TryGetValue(kv.Key, out engV) || engV == null)
                        continue; // key không có trong English — game không Format → giữ

                    int vMax = MaxFormatIndex(v);
                    int eMax = MaxFormatIndex(engV);

                    // Nguy cơ crash: index vượt quá English gốc → loại.
                    if (vMax > eMax)
                    {
                        if (toRemove == null) toRemove = new List<string>();
                        toRemove.Add(kv.Key);
                        continue;
                    }

                    if (warnings >= 20) continue; // giới hạn log tránh ngập console

                    // Cảnh báo (không loại): bản dịch bỏ mất placeholder gốc.
                    if (eMax >= 0 && vMax < 0)
                    {
                        Debug.LogWarning("[Localizer] [" + langKv.Key + "] key \"" + Trunc(kv.Key, 60)
                            + "\": English có placeholder {0.." + eMax + "} nhưng bản dịch không có"
                            + " — thông tin format sẽ thiếu khi hiển thị.");
                        warnings++;
                        continue;
                    }

                    // Cảnh báo: lệch số lượng %s/%d (printf-style) giữa 2 bản.
                    int engPf = CountPrintfTokens(engV);
                    if (engPf != CountPrintfTokens(v))
                    {
                        Debug.LogWarning("[Localizer] [" + langKv.Key + "] key \"" + Trunc(kv.Key, 60)
                            + "\": lệch số lượng %s/%d giữa English (" + engPf
                            + ") và bản dịch (" + CountPrintfTokens(v) + ").");
                        warnings++;
                        continue;
                    }

                    // Cảnh báo: thiếu [TOKEN] hoa (game Replace tay kiểu [NAME]).
                    string missingTok = FindMissingBracketToken(engV, v);
                    if (missingTok != null)
                    {
                        Debug.LogWarning("[Localizer] [" + langKv.Key + "] key \"" + Trunc(kv.Key, 60)
                            + "\": bản dịch thiếu token [" + missingTok + "] mà English có.");
                        warnings++;
                    }
                }

                if (toRemove != null)
                {
                    for (int i = 0; i < toRemove.Count; i++)
                    {
                        string k = toRemove[i];
                        Debug.LogWarning("[Localizer] [" + langKv.Key + "] LOẠI entry \""
                            + Trunc(k, 60) + "\": bản dịch dùng placeholder vượt English gốc"
                            + " → tránh FormatException (crash game). Hãy sửa file dịch.");
                        d.Remove(k);
                        TranslationDict.Remove(k);
                        EnglishTextDict.Remove(k);
                        removed++;
                    }
                }
            }
            if (warnings >= 20)
                Debug.LogWarning("[Localizer] Placeholder validation: đã đạt giới hạn 20 cảnh báo — các cảnh báo còn lại bị im lặng.");
            if (removed > 0)
                Debug.LogWarning("[Localizer] Placeholder validation: đã loại " + removed
                    + " entry nguy cơ crash — xem các cảnh báo [Localizer] phía trên để sửa file dịch.");
            return removed;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[Localizer] ValidatePlaceholdersAgainstEnglish lỗi: " + ex.Message);
            return 0;
        }
    }

    static int CountPrintfTokens(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        int n = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '%' && i + 1 < s.Length)
            {
                char nx = s[i + 1];
                if (nx == 's' || nx == 'd' || nx == 'f') n++;
                else if (nx == '%') i++; // %% literal — không tính
            }
        }
        return n;
    }

    static readonly Regex _bracketTokenRegex = new Regex(@"\[[A-Z][A-Z0-9_]{1,}\]",
        RegexOptions.Compiled, TimeSpan.FromSeconds(2));

    /// <summary>Tìm token [UPPER_CASE] có trong English mà bản dịch thiếu; null nếu không.</summary>
    static string FindMissingBracketToken(string eng, string custom)
    {
        if (string.IsNullOrEmpty(eng)) return null;
        foreach (Match m in _bracketTokenRegex.Matches(eng))
        {
            if (custom == null || custom.IndexOf(m.Value, StringComparison.OrdinalIgnoreCase) < 0)
                return m.Value;
        }
        return null;
    }

    static string Trunc(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
        return s.Substring(0, max) + "...";
    }

    /// <summary>FIX LANG-05: Replace chuỗi KHÔNG phân biệt hoa thường.</summary>
    static string ReplaceIgnoreCase(string s, string needle, string replacement)
    {
        if (string.IsNullOrEmpty(s) || string.IsNullOrEmpty(needle)) return s;
        int idx = s.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return s;
        var sb = new StringBuilder(s.Length + replacement.Length);
        int pos = 0;
        while (idx >= 0)
        {
            sb.Append(s, pos, idx - pos);
            sb.Append(replacement);
            pos = idx + needle.Length;
            idx = pos < s.Length ? s.IndexOf(needle, pos, StringComparison.OrdinalIgnoreCase) : -1;
        }
        sb.Append(s, pos, s.Length - pos);
        return sb.ToString();
    }

    static Dictionary<string, string> LoadTranslationFileStatic(string path)
    {
        // FIX v2.13: log timing để tối ưu hiệu năng
        var sw = System.Diagnostics.Stopwatch.StartNew();
        DateTime mtime;
        try { mtime = File.GetLastWriteTimeUtc(path); }
        catch { mtime = DateTime.MinValue; }

        lock (_fileCacheLock)
        {
            if (_fileCache.TryGetValue(path, out var entry) && entry.mtime == mtime)
                return new Dictionary<string, string>(entry.dict, StringComparer.OrdinalIgnoreCase);
        }

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // FIX LANG-06/LANG-11: thống kê chất lượng file để cảnh báo tác giả.
        int skippedBad = 0, dupKeys = 0, emptyValues = 0;
        int lineNo = 1, lineCursor = 0;
        try
        {
            // Encoding: File.ReadAllText tự nhận diện BOM (UTF-8, UTF-16 LE/BE) và
            // strip BOM — file dịch lưu từ Notepad/VS/Sublime đều đọc đúng, BOM
            // không bao giờ lọt vào key đầu tiên (LANG-10: đã verify, an toàn).
            string content = File.ReadAllText(path);
            foreach (Match match in _translationRegex.Matches(content))
            {
                if (match.Groups.Count < 3) continue;

                // FIX LANG-06: tính số dòng tăng tiến (O(n) cho cả file) để mọi
                // cảnh báo đều kèm vị trí chính xác cho tác giả file dịch.
                while (lineCursor <= match.Index)
                {
                    int nl = content.IndexOf('\n', lineCursor);
                    if (nl < 0 || nl > match.Index) break;
                    lineCursor = nl + 1;
                    lineNo++;
                }

                string key = Unescape(match.Groups[1].Value.Trim());
                string value = Unescape(match.Groups[2].Value);
                if (string.IsNullOrEmpty(key)) continue;

                // FIX LANG-01: value chứa brace '{'/'}' hỏng cú pháp composite-format
                // (thiếu '}', '{0' không đóng, brace đơn...) — game string.Format
                // chuỗi này sẽ ném FormatException (không try/catch phía game) →
                // crash toàn game. Bỏ entry (fallback English an toàn) + cảnh báo.
                if (value.IndexOf('{') >= 0 || value.IndexOf('}') >= 0)
                {
                    if (!IsFormatSyntaxSafe(value))
                    {
                        Debug.LogWarning("[Localizer] " + Path.GetFileName(path) + ":" + lineNo
                            + " — bỏ qua entry brace hỏng (gây FormatException nếu game format): key=\""
                            + Trunc(key, 60) + "\"");
                        skippedBad++;
                        continue;
                    }
                    // FIX LANG-02 (nửa in-parse): file scenario có key = chuỗi
                    // English gốc (key chứa sẵn {N}) — so sánh index cao nhất của
                    // value với của key, vượt là bỏ (FormatException chắc chắn).
                    int vMax = MaxFormatIndex(value);
                    int kMax = MaxFormatIndex(key);
                    if (kMax >= 0 && vMax > kMax)
                    {
                        Debug.LogWarning("[Localizer] " + Path.GetFileName(path) + ":" + lineNo
                            + " — bỏ qua entry dùng {" + vMax + "} vượt chuỗi gốc (tối đa {" + kMax
                            + "}) — nguy cơ FormatException: key=\"" + Trunc(key, 60) + "\"");
                        skippedBad++;
                        continue;
                    }
                }

                // Placeholder substitution — {VERSION} thay bằng version hiện tại.
                // Cho phép file .txt viết "Plague Inc Language Library {VERSION}" thay vì hardcode.
                // FIX LANG-05: trước đây detect KHÔNG phân biệt hoa thường nhưng
                // string.Replace thì CÓ — "{version}"/"{Version}" được phát hiện
                // mà KHÔNG được thay → hiện nguyên literal trong game. Giờ thay
                // theo OrdinalIgnoreCase.
                value = ReplaceIgnoreCase(value, "{VERSION}", PluginVersion.DisplayVersion);

                // FIX LANG-06: cảnh báo key trùng (kể cả khác hoa/thường — dict là
                // OrdinalIgnoreCase nên "Key" và "key" đè lên nhau im lặng trước đây).
                if (dict.ContainsKey(key)) dupKeys++;
                // FIX LANG-11: đếm value rỗng — gần như chắc chắn là lỗi thiếu string.
                // FIX OPT-05: value rỗng KHÔNG được nạp vào dict. Game (GetTextFromDictionary)
                // gặp value rỗng sẽ trả lại CHÍNH KEY (với success=true) và BỎ QUA English
                // fallback → label hiện key thô (đúng hiện tượng "FE_Options_*" trong video).
                // Bỏ entry rỗng → game tự rơi xuống English fallback của nó.
                if (value.Length == 0) { emptyValues++; continue; }
                dict[key] = value;
                // FIX ST-07: bỏ ToLower — dict đã OrdinalIgnoreCase.
            }
            lock (_fileCacheLock) { _fileCache[path] = (mtime, dict); }
            sw.Stop();
            if (sw.ElapsedMilliseconds > 100)
                Debug.Log("[v2.13] Parse " + Path.GetFileName(path) + ": " + dict.Count + " entries in " + sw.ElapsedMilliseconds + "ms");
            if (skippedBad > 0 || dupKeys > 0 || emptyValues > 0)
                Debug.LogWarning("[Localizer] " + Path.GetFileName(path) + ": " + dict.Count
                    + " entries — " + skippedBad + " bị bỏ (placeholder/brace lỗi), "
                    + dupKeys + " key trùng (đè nhau), " + emptyValues + " value rỗng");
        }
        catch (Exception ex)
        {
            // Exception khi đọc/parse (file bị khoá, regex timeout...) — dict trả
            // phần đã parse được, KHÔNG cache để lần sau đọc lại (m behaviour cũ).
            Debug.LogError("Lỗi đọc/parse file: " + ex.GetType().Name + ": " + ex.Message);
        }
        return dict;
    }

    Dictionary<string, string> LoadTranslationFile(string path)
    {
        return LoadTranslationFileStatic(path);
    }

    // FIX CR-05: viết lại single-pass. Trước đây Replace nối tiếp theo thứ tự
    // \n, \t, \", \\, \r — xử lý "\\" (backslash literal) SAU "\n" khiến chuỗi
    // "\\n" trong file dịch bị tách sai thành "\" + newline thay vì "\" + "n"
    // đúng. Single-pass xử lý đúng mọi tổ hợp escape.
    //
    // FIX LANG-03: giá trị multi-line (regex Singleline cho phép newline literal
    // nằm trong cặp "") mang theo \r\n của file Windows — NGUI UILabel chỉ hiểu
    // \n; \r sẽ hiển thị thành ký tự lỗi (□) trên từng dòng sau dòng đầu. Giờ mọi
    // \r\n và \r đơn đều được chuẩn hoá thành \n; escape \r cũng trả về \n (chọn
    // phía an toàn cho render NGUI).
    //
    // FIX LANG-04: hỗ trợ escape \uXXXX (JSON-style; surrogate pair cần 2 escape
    // liên tiếp — 2 char surrogate ghép tự nhiên trong StringBuilder). Công cụ
    // xuất file dịch thường sinh escape này cho ký tự Unicode — trước đây nó
    // hiển thị nguyên literal "\u1ea3" trong game.
    static string Unescape(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        if (s.IndexOf('\\') < 0 && s.IndexOf('\r') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                char nxt = s[i + 1];
                if (nxt == 'n') { sb.Append('\n'); i++; }
                else if (nxt == 't') { sb.Append('\t'); i++; }
                else if (nxt == 'r') { sb.Append('\n'); i++; }
                else if (nxt == '"') { sb.Append('"'); i++; }
                else if (nxt == '\\') { sb.Append('\\'); i++; }
                else if (nxt == 'u')
                {
                    int cp = ParseHex4(s, i + 2);
                    if (cp >= 0)
                    {
                        sb.Append((char)cp);
                        i += 5; // bỏ qua 4 hex digit (vòng for cộng thêm 1 nữa)
                    }
                    else sb.Append(c); // \u hỏng (thiếu hex) — giữ nguyên backslash
                }
                else sb.Append(c); // backslash đứng trước ký tự lạ — giữ nguyên
            }
            else if (c == '\r')
            {
                // LANG-03: CRLF → LF; CR đơn → LF (NGUI chỉ render \n).
                sb.Append('\n');
                if (i + 1 < s.Length && s[i + 1] == '\n') i++;
            }
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Đọc 4 hex digit tại s[start..start+3]; -1 nếu không hợp lệ.</summary>
    static int ParseHex4(string s, int start)
    {
        if (s == null || start < 0 || start + 4 > s.Length) return -1;
        int v = 0;
        for (int k = 0; k < 4; k++)
        {
            int h = HexVal(s[start + k]);
            if (h < 0) return -1;
            v = (v << 4) | h;
        }
        return v;
    }

    static int HexVal(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    }

    public static bool IsCustomLanguageActive()
    {
        if (string.IsNullOrEmpty(CurrentCustomLanguage)) return false;
        // CFG-08 (Zero-Hardcode + hot path): bỏ so sánh cứng "English" — thay bằng
        // kiểm tra theo config OfficialLanguages (HashSet O(1), KHÔNG IO). Hàm này
        // nằm trong Harmony gate chạy rất thường xuyên (UIFont.get_dynamicFont,
        // UILabel OnEnable...) nên tuyệt đối không gọi Directory.Exists ở đây.
        // CurrentCustomLanguage chỉ được gán từ các nơi đã kiểm tra
        // IsCustomLanguageFolder (LoadAllLanguages / ForceCustomLanguage /
        // BindActiveLanguagePack) nên kiểm tra danh sách official là đủ phòng hộ.
        if (ConfigManager.IsOfficialLanguage(CurrentCustomLanguage)) return false;
        return string.Equals(
            CLocalisationManager.ActiveLanguage,
            CurrentCustomLanguage,
            StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryGetTranslation(string key, out string value)
    {
        // FIX LANG-32: chỉ trả bản dịch custom khi đang ở custom language.
        // Trước đây TranslationDict vẫn giữ entry của pack cuối → GetLoc / AboutMod
        // button / một số path vẫn trả tiếng Việt sau khi đã chuyển về official
        // (Help & Info + "Về Bản Mod" kẹt VN như video).
        if (!IsCustomLanguageActive())
        {
            value = null;
            return false;
        }
        // FIX CR-11: bỏ các lookup key.ToLower() — cả 2 dict đã là OrdinalIgnoreCase
        // (lookup không phân biệt hoa thường) nên lookup thường đã đủ, và ToLower()
        // còn phụ thuộc culture của Windows (bug tiềm ẩn trên máy Turkish locale).
        // FIX OPT-05b: entry value rỗng coi như KHÔNG có — nếu trả true với value ""
        // thì GetText_Fallback_Patch sẽ đè __result = "" → label TRỐNG (tệ hơn key thô).
        if (ScenarioDict.TryGetValue(key, out value) && !string.IsNullOrEmpty(value))
            return true;
        if (TranslationDict.TryGetValue(key, out value) && !string.IsNullOrEmpty(value))
            return true;
        value = null;
        return false;
    }
}

// ====================== HARMONY PATCHES ======================

[HarmonyPatch(typeof(OptionsSelector), "OnEnable")]
public static class OptionsSelector_OnEnable_FixIndexZero_Patch
{
    // Field private "mnCurrent" của game — đọc qua reflection, không sửa game.
    static readonly System.Reflection.FieldInfo FiCurrent =
        HarmonyLib.AccessTools.Field(typeof(OptionsSelector), "mnCurrent");

    static void Postfix(OptionsSelector __instance)
    {
        try
        {
            if (!PlagueVnMod.IsCustomLanguageActive()) return;
            if (__instance == null || __instance.optionEnum == null) return;
            if (__instance.optionEnum.Count == 0) return;
            if (FiCurrent == null) return;

            // LANG-RESET: KHÔNG BAO GIỜ Set(0) trên leftover OptionsSelector của
            // nút Reset (Options_General_Reset/Button_Reset). Game vanilla bỏ
            // Set khi mnCurrent==0 CỐ TÌNH — caption inspector là
            // FE_Reset_All_Progress. Set(0) ghi optionLoc[0]=SIMPLE lên label
            // → UI hiện "Đơn giản" và khóa khi đổi ngôn ngữ (poison originalLabelText).
            if (PlagueVnMod.IsDeadOptionsSelector(__instance))
            {
                HealResetCaption(__instance);
                return;
            }

            int cur = (int)FiCurrent.GetValue(__instance);
            if (cur < 0) cur = 0;
            if (cur >= __instance.optionEnum.Count) return;

            // Không ép index 0 nếu label đang hiện FE key khác optionLoc
            // (Interface inspector = FE_Options_Normal, mnCurrent mặc định 0 —
            // SetActive sẽ SetByName sau; Set(0) ở đây flash + poison).
            try
            {
                if (__instance.optionCurrent != null)
                {
                    UILabel lab = __instance.optionCurrent.GetComponent<UILabel>();
                    if (lab != null
                        && PlagueVnMod.OptionsSelectorWouldClobberForeignKey(__instance, lab.text))
                        return;
                }
            }
            catch { }

            // Chỉ re-apply index HIỆN TẠI (dịch lại), không force 0. lol
            if (cur == 0)
                __instance.Set(0);
        }
        catch { }
    }

    static void HealResetCaption(OptionsSelector s)
    {
        try
        {
            if (s == null || s.optionCurrent == null) return;
            UILabel lab = s.optionCurrent.GetComponent<UILabel>();
            if (lab == null) return;

            const string resetKey = "FE_Reset_All_Progress";
            try
            {
                PlagueVnMod.LabelSourceKeyCache.Remove(lab);
                PlagueVnMod.LabelSourceKeyCache.Add(lab, resetKey);
            }
            catch { PlagueVnMod.CacheLabelSourceKey(lab, resetKey); }

            string shown = resetKey;
            if (PlagueVnMod.TryGetTranslation(resetKey, out string tr)
                && !string.IsNullOrEmpty(tr))
                shown = tr;
            else
            {
                try
                {
                    string g = CLocalisationManager.GetText(resetKey);
                    if (!string.IsNullOrEmpty(g) && !string.Equals(g, resetKey, StringComparison.Ordinal))
                        shown = g;
                }
                catch { }
            }

            UILabelAutotranslate at = lab.GetComponent<UILabelAutotranslate>();
            if (at != null)
            {
                try { at.SetInitialText(resetKey, true); } catch { lab.text = shown; }
            }
            else
                lab.text = shown;
        }
        catch { }
    }
}

[HarmonyPatch(typeof(UILabel), "set_trueTypeFont")]
public static class UILabel_SetTrueTypeFont_Track_Patch
{
    static void Postfix(Font value)
    {
        if (PlagueVnMod.ApplyingFont) return;
        if (value != null) PlagueVnMod.TrackFontName(value.name);
    }
}

[HarmonyPatch(typeof(UILabel), "set_bitmapFont")]
public static class UILabel_SetBitmapFont_Track_Patch
{
    static void Postfix(UIFont value)
    {
        if (PlagueVnMod.ApplyingFont) return;
        if (value != null) PlagueVnMod.TrackFontName(value.name);
    }
}

[HarmonyPatch(typeof(CLocalisationManager), "GetText",
    new Type[] { typeof(string), typeof(CLocalisationManager.ETextType), typeof(GameObject) })]
public static class GetText_Fallback_Patch
{
    public static void Postfix(string tagName, ref string __result)
    {
        if (string.IsNullOrEmpty(tagName) || tagName.Length < 2) return;

        if (PlagueVnMod.IsCustomLanguageActive())
        {
            if (__result != tagName && !string.Equals(__result, tagName, StringComparison.OrdinalIgnoreCase))
                return;
            if (PlagueVnMod.TryGetTranslation(tagName, out string translated))
                __result = translated;
            return;
        }

        // LANG-42: nhánh OFFICIAL — GetText trả nguyên tag khi miss HOẶC khi value
        // trong dict ngôn ngữ hiện hành RỖNG (repack thiếu dữ liệu: GetTextFromDictionary
        // gán text = tag). Game sẽ stamp key thô "FE_Options_*" lên label qua
        // SetInitialText/AutoTranslate (thấy trong video). Tra giá trị English của key
        // từ dict English của game (reflection, data-driven) để không bao giờ trả key thô.
        // Hot path (GetText chạy rất dày): 2 dạng fail thực tế của GetTextInternal là
        // trả `text2` (tag NGUYÊN VĂN) hoặc `tag` (tag đã ToLower khi value rỗng) →
        // check đủ 2 dạng này, KHÔNG alloc Trim/ToLower khi value đã phân giải thật
        // (phần lớn call) — length-gate trước khi ToLower.
        if (__result == null) return;
        bool isMiss = string.Equals(__result, tagName, StringComparison.Ordinal);
        if (!isMiss && __result.Length == tagName.Length)
            isMiss = string.Equals(__result, tagName.ToLowerInvariant(), StringComparison.Ordinal);
        if (!isMiss) return; // game đã phân giải giá trị thật — không đụng
        __result = PlagueVnMod.ResolveOfficialMiss(tagName, __result);
    }
}

[HarmonyPatch(typeof(IGame), "LoadScenarioStrings")]
public static class LoadScenarioStrings_Patch
{
    static void Postfix(IGame __instance)
    {
        if (!PlagueVnMod.IsCustomLanguageActive()) return;
        if (__instance.CurrentLoadedScenario == null)
        {
            PlagueVnMod.ClearScenarioTranslation();
            return;
        }
        string scenarioId = __instance.CurrentLoadedScenario.scenarioInformation != null
            ? __instance.CurrentLoadedScenario.scenarioInformation.id
            : null;
        if (string.IsNullOrEmpty(scenarioId))
        {
            PlagueVnMod.ClearScenarioTranslation();
            return;
        }
        PlagueVnMod.ApplyScenarioTranslation(scenarioId);
    }
}

[HarmonyPatch(typeof(CLocalisationManager), "ClearCustomLocalisation")]
public static class ClearCustomLocalisation_Patch
{
    static void Postfix()
    {
        PlagueVnMod.ClearScenarioTranslation();
    }
}

// CFG-10 (pass-through + Zero-Hardcode): PATCH RewardController.names ĐÃ XÓA.
// Trước đây Prefix return false + thay __result bằng mảng 3 chuỗi English
// hardcode ("Emergence Rewards"...) → vừa vi phạm Zero-Hardcode (chuỗi text nằm
// trong code), vừa CHẶN hàm gốc chạy — mọi caller khác của RewardController.names
// (logic game, không chỉ UI) đều nhận dữ liệu giả. Giờ getter gốc luôn chạy
// nguyên vẹn; bản dịch màn reward do GetRewardBarMenuTitle_Patch (Postfix,
// data-driven) đảm nhiệm.

// HARDCODE theo yêu cầu: names luôn là key ghép để GetText đúng mọi ngôn ngữ official + custom.
[HarmonyPatch(typeof(RewardController), nameof(RewardController.names), MethodType.Getter)]
public static class RewardController_Names_Patch
{
    static readonly string[] CombinedNames =
    {
        "Emergence Rewards",
        "Genotype Rewards",
        "Pathogenesis Rewards"
    };

    static bool Prefix(ref string[] __result)
    {
        __result = CombinedNames;
        return false;
    }
}

[HarmonyPatch(typeof(RewardController), nameof(RewardController.GetRewardBarMenuTitle))]
public static class RewardController_GetRewardBarMenuTitle_Patch
{
    // HARDCODE theo yêu cầu: title = GetText(names[index]) — không ghép "Rewards" nữa.
    // Custom: ưu tiên dict mod nếu có entry trùng key.
    static bool Prefix(int index, ref string __result)
    {
        try
        {
            string[] names = RewardController.names;
            if (names == null || index < 0 || index >= names.Length)
                return true;

            string key = names[index];
            if (PlagueVnMod.IsCustomLanguageActive()
                && PlagueVnMod.TryGetTranslation(key, out string custom)
                && !string.IsNullOrEmpty(custom)
                && !string.Equals(custom, key, StringComparison.OrdinalIgnoreCase))
            {
                __result = custom;
                return false;
            }

            __result = CLocalisationManager.GetText(key);
            return false;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[Localizer] GetRewardBarMenuTitle: " + ex.Message);
            return true;
        }
    }
}

[HarmonyPatch(typeof(CHUDContextSubScreen), "SetContextWorld")]
public static class CHUDContextSubScreen_SetContextWorld_Patch
{
    static void Postfix(CHUDContextSubScreen __instance)
    {
        if (!PlagueVnMod.IsCustomLanguageActive()) return;
        if (__instance.contextTitleText != null)
            __instance.contextTitleText.text = CLocalisationManager.GetText(ConfigManager.WorldTitleKey); // CFG-08
    }
}

[HarmonyPatch(typeof(CHUDContextSubScreen), "SetCountryTitleText")]
public static class CHUDContextSubScreen_SetCountryTitleText_Patch
{
    static void Postfix(CHUDContextSubScreen __instance, Country country)
    {
        if (!PlagueVnMod.IsCustomLanguageActive() || country == null || __instance.contextTitleText == null) return;
        __instance.contextTitleText.text = CLocalisationManager.GetText(country.name);
    }
}

[HarmonyPatch(typeof(CHUDContextSubScreen), "RefreshContextWorld")]
public static class CHUDContextSubScreen_RefreshContextWorld_Patch
{
    static void Postfix(CHUDContextSubScreen __instance, HudTextObject ___currentHumanText)
    {
        if (!PlagueVnMod.IsCustomLanguageActive() || ___currentHumanText == null) return;
        ___currentHumanText.SetInfectedText(ConfigManager.HudInfectedKey); // CFG-08
    }
}

[HarmonyPatch(typeof(CHUDContextSubScreen), "RefreshContextCountry")]
public static class CHUDContextSubScreen_RefreshContextCountry_Patch
{
    static void Postfix(CHUDContextSubScreen __instance, HudTextObject ___currentHumanText)
    {
        if (!PlagueVnMod.IsCustomLanguageActive() || ___currentHumanText == null) return;
        if (CGameManager.IsMultiplayerGame)
        {
            if (CGameManager.game != null && __instance.Country != null && CGameManager.game.CanSeeDots(__instance.Country))
                ___currentHumanText.SetInfectedText(ConfigManager.HudInfectedMultiplayerKey); // CFG-08
            else
                ___currentHumanText.SetInfectedText(ConfigManager.HudUnknownStatsKey); // CFG-08
        }
        else
            ___currentHumanText.SetInfectedText(ConfigManager.HudInfectedKey); // CFG-08
    }
}

[HarmonyPatch(typeof(CLocalisationManager), "set_ActiveLanguage")]
public static class ActiveLanguage_Save_Patch
{
    static int _depth; // re-entry guard

    static void Postfix(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (_depth > 0) return;
        _depth++;

        int epoch = PlagueVnMod.BumpLangEpoch();

        try
        {
            if (PlagueVnMod.IsCustomLanguageFolder(value))
            {
                if (PlagueVnMod.LastLanguage != null)
                {
                    PlagueVnMod.LastLanguage.Value = value;
                    Debug.Log("[Localizer] Saved LastLanguage (custom) = " + value);
                }

                PlagueVnMod.BindActiveLanguagePack(value);

                try
                {
                    if (!string.IsNullOrEmpty(PlagueVnMod.LanguageFolder))
                        LangImageVault.LoadFromLangFolder(
                            Path.Combine(PlagueVnMod.LanguageFolder, value));
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[Localizer] LoadFromLangFolder: " + ex.Message);
                }

                try { CMainAboutModSubScreen.InjectFallbackIntoAllLanguages(); } catch { }
                try
                {
                    if (AboutMod_InjectButton_Patch._btn != null)
                        AboutMod_InjectButton_Patch.ApplyButtonCaption(
                            AboutMod_InjectButton_Patch._btn.gameObject);
                }
                catch { }
            }
            else
            {
                bool wasCustom = !string.IsNullOrEmpty(PlagueVnMod.CurrentCustomLanguage);

                PlagueVnMod.FontsReady = false;
                PlagueVnMod.CurrentCustomLanguage = null;
                CMainAboutModSubScreen.LangFolder = null;

                // LANG-46: KHÔNG xoá LastLanguage khi sang official — giữ custom lần cuối
                // để ForceCustomLanguage restore được khi boot về English mặc định.

                if (!wasCustom)
                {
                    try { PlagueVnMod.OnSwitchedToOfficialLanguage(value); } catch { }
                    if (PlagueVnMod.Instance != null)
                        PlagueVnMod.Instance.StartCoroutine(
                            PlagueVnMod.Instance.TeardownOfficialAfterGameLoop(value, epoch));
                    Debug.Log("[Localizer] ActiveLanguage official = " + value
                        + " (official→official, deferred sweep, epoch=" + epoch + ")");
                    return;
                }

                if (PlagueVnMod.Instance != null)
                {
                    PlagueVnMod.Instance.StartCoroutine(
                        PlagueVnMod.Instance.TeardownOfficialDeferredPublic(value, epoch));
                    PlagueVnMod.Instance.StartCoroutine(
                        PlagueVnMod.Instance.TeardownOfficialAfterGameLoop(value, epoch));
                }
                else
                    PlagueVnMod.TeardownOfficialImmediatePublic(value);

                Debug.Log("[Localizer] ActiveLanguage official = " + value
                    + " (custom→official teardown, epoch=" + epoch + ")");
            }
        }
        catch (Exception ex)
        {
            try { PlagueVnMod.FontsReady = false; } catch { }
            Debug.LogWarning("[Localizer] set_ActiveLanguage postfix (đã nuốt): " + ex.Message);
        }
        finally
        {
            _depth--;
        }
    }
}

[HarmonyPatch(typeof(CGSScreen), "SetActiveSubScreen")]
public static class CGSScreen_SetActiveSubScreen_LocalizeTitle_Patch
{
    static void Postfix(CGSScreen __instance, IGameSubScreen screen)
    {
        if (__instance.titleLabel == null) return;
        if (!(screen is IGameSetupSubScreen setup)) return;
        if (string.IsNullOrEmpty(setup.title)) return;

        string key = setup.title.Trim();
        if (PlagueVnMod.VerboseLogging != null && PlagueVnMod.VerboseLogging.Value)
            Debug.Log("[Localizer] TITLE key=[" + key + "] before=[" + __instance.titleLabel.text + "]");

        // LANG-23/27: luôn cache English/key, không cache bản dịch (Nhật/Việt...).
        string source = PlagueVnMod.ResolveSourceEnglish(key);
        PlagueVnMod.CacheOriginalTextIfAbsent(__instance.titleLabel, source);
        if (PlagueVnMod.IsCustomLanguageActive())
            __instance.titleLabel.text = PlagueVnMod.LocalizeHardcodedUi(source);
        else
        {
            try
            {
                string g = CLocalisationManager.GetText(source);
                __instance.titleLabel.text = string.IsNullOrEmpty(g) ? source : g;
            }
            catch { __instance.titleLabel.text = source; }
        }

        if (PlagueVnMod.VerboseLogging != null && PlagueVnMod.VerboseLogging.Value)
            Debug.Log("[Localizer] TITLE after=[" + __instance.titleLabel.text + "]");
    }
}

[HarmonyPatch(typeof(LoadoutButton), "Setup")]
public static class LoadoutButton_Setup_LocalizeSelect_Patch
{
    static void Postfix(LoadoutButton __instance)
    {
        // Official language: để game + UILabelAutotranslate tự xử lý.
        // Trước đây luôn LocalizeHardcodedUi → stamp tiếng Việt / key lệch khi đổi ngôn ngữ.
        if (!PlagueVnMod.IsCustomLanguageActive()) return;
        if (__instance == null) return;

        UILabel[] labels = __instance.GetComponentsInChildren<UILabel>(true);
        for (int i = 0; i < labels.Length; i++)
        {
            UILabel lab = labels[i];
            if (lab == null) continue;

            if (lab == __instance.loadoutTitle) continue;
            if (lab == __instance.loadoutText) continue;
            if (lab.GetComponentInParent<UIButton>() == null) continue;

            string loadoutKey = lab.text;
            if (string.IsNullOrEmpty(loadoutKey)) continue;

            // LANG-23: "Chọn" → "Select" trước khi cache. Official không dịch "Select"
            // (đúng theo game). Custom mới stamp. Restore về official luôn ra "Select".
            string source = PlagueVnMod.ResolveSourceEnglish(loadoutKey);
            PlagueVnMod.CacheOriginalTextIfAbsent(lab, source);
            string loadoutLoc = PlagueVnMod.LocalizeHardcodedUi(source);
            if (!string.IsNullOrEmpty(loadoutLoc) && loadoutLoc != lab.text)
                lab.text = loadoutLoc;
        }
    }
}
[HarmonyPatch(typeof(UIFont), "get_dynamicFont")]
public static class UIFont_GetDynamicFont_Patch
{
    static void Postfix(UIFont __instance, ref Font __result)
    {
        // FIX dropdown trống: không override.
        // NGUI đọc dynamicFont của UIFont shared khi vẽ item UIPopupList.
        // Ép Font custom ở đây = cùng rủi ro mutate mDynamicFont.
        // Font custom chỉ qua UILabel.trueTypeFont (label không-popup).
        return;
    }
}
[HarmonyPatch(typeof(OptionsSelector), "Set")]
public static class OptionsSelector_Set_Localize_Patch
{
    static void Postfix(OptionsSelector __instance, int i)
    {
        if (!PlagueVnMod.IsCustomLanguageActive()) return;
        if (__instance == null || __instance.optionLoc == null || __instance.optionCurrent == null)
            return;
        if (i < 0 || i >= __instance.optionLoc.Count) return;

        string loc = __instance.optionLoc[i];
        if (string.IsNullOrEmpty(loc)) return;

        string t = null;

        // ƯU TIÊN 1 (có ngữ cảnh): "OPT|<enumName>|<optionEnum[i]>" — bạn có thể tự
        // thêm dòng override vào file .txt hiện có, VD:
        //   OPT|EInterfaceType|SIMPLE=Đơn giản
        // chỉ cần cho những setting bạn xác nhận đang bị dính collision.
        try
        {
            string enumVal = (__instance.optionEnum != null && i < __instance.optionEnum.Count)
                ? __instance.optionEnum[i] : null;
            if (!string.IsNullOrEmpty(__instance.enumName) && !string.IsNullOrEmpty(enumVal))
            {
                string scopedKey = "OPT|" + __instance.enumName.Trim() + "|" + enumVal.Trim();
                if (PlagueVnMod.EnglishTextDict.TryGetValue(scopedKey, out string scopedVal)
                    && !string.IsNullOrEmpty(scopedVal))
                {
                    t = scopedVal;
                }
            }
        }
        catch { }

        // ƯU TIÊN 2 (fallback — hành vi cũ, KHÔNG đổi): dict global hiện tại. s 
        if (t == null)
        {
            if (!PlagueVnMod.TryResolveLabelText(loc, out t)
                || string.IsNullOrEmpty(t)
                || string.Equals(t, loc, StringComparison.Ordinal))
                return;
        }

        try
        {
            UILabel lab = __instance.optionCurrent.GetComponent<UILabel>();
            if (lab != null) lab.text = t;
        }
        catch { }
    }
}