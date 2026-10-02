using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

// Tách ra từ PlagueVnMod (Main.cs) — gom phần đọc + cache file dịch .txt
// ("key" = "value";), các helper validate cú pháp placeholder {N}/unescape
// chuỗi, VÀ ValidatePlaceholdersAgainstEnglish (đối chiếu placeholder giữa mọi
// ngôn ngữ custom với English gốc). Đây là MOVE thuần túy: không đổi logic/
// hành vi so với bản gốc trong Main.cs.
//
// ValidatePlaceholdersAgainstEnglish đọc/ghi TranslationDict/EnglishTextDict —
// 2 dict dùng chung của PlagueVnMod (bị đụng ở 28-33 chỗ khác trong Main.cs) —
// nên 2 dict đó KHÔNG move theo (vẫn public static trên PlagueVnMod như cũ),
// chỉ có method được move, đọc/ghi qua PlagueVnMod.<field> tường minh. Cùng lý
// do, Trunc() cũng ở lại Main.cs (internal) vì nó dùng chung ở nhiều chỗ khác
// ngoài phạm vi file dịch.
public static class TranslationFileLoader
{
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
    public static int MaxFormatIndex(string s)
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

    public static Dictionary<string, string> LoadTranslationFileStatic(string path)
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
                            + PlagueVnMod.Trunc(key, 60) + "\"");
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
                            + "}) — nguy cơ FormatException: key=\"" + PlagueVnMod.Trunc(key, 60) + "\"");
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
    public static string Unescape(string s)
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
    public static int ParseHex4(string s, int start)
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
    public static int ValidatePlaceholdersAgainstEnglish()
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
                        Debug.LogWarning("[Localizer] [" + langKv.Key + "] key \"" + PlagueVnMod.Trunc(kv.Key, 60)
                            + "\": English có placeholder {0.." + eMax + "} nhưng bản dịch không có"
                            + " — thông tin format sẽ thiếu khi hiển thị.");
                        warnings++;
                        continue;
                    }

                    // Cảnh báo: lệch số lượng %s/%d (printf-style) giữa 2 bản.
                    int engPf = CountPrintfTokens(engV);
                    if (engPf != CountPrintfTokens(v))
                    {
                        Debug.LogWarning("[Localizer] [" + langKv.Key + "] key \"" + PlagueVnMod.Trunc(kv.Key, 60)
                            + "\": lệch số lượng %s/%d giữa English (" + engPf
                            + ") và bản dịch (" + CountPrintfTokens(v) + ").");
                        warnings++;
                        continue;
                    }

                    // Cảnh báo: thiếu [TOKEN] hoa (game Replace tay kiểu [NAME]).
                    string missingTok = FindMissingBracketToken(engV, v);
                    if (missingTok != null)
                    {
                        Debug.LogWarning("[Localizer] [" + langKv.Key + "] key \"" + PlagueVnMod.Trunc(kv.Key, 60)
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
                            + PlagueVnMod.Trunc(k, 60) + "\": bản dịch dùng placeholder vượt English gốc"
                            + " → tránh FormatException (crash game). Hãy sửa file dịch.");
                        d.Remove(k);
                        PlagueVnMod.TranslationDict.Remove(k);
                        PlagueVnMod.EnglishTextDict.Remove(k);
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
}

