using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using UnityEngine;

// Tách ra từ PlagueVnMod (Main.cs) — gom phần load font: ưu tiên AssetBundle
// (fonts.json) nếu có, nếu không thì load trực tiếp .ttf/.otf theo manifest
// (fonts.txt), tự sinh manifest nếu thiếu/lệch.
//
// KHÁC với WindowsFontRegistrar/FontFamilyReader/TranslationFileLoader (hoàn
// toàn tự chứa), nhóm này ĐỌC/GHI state dùng chung của PlagueVnMod — FontBd/
// FontMd/FontLt, FontsReady, CustomFonts — những field bị đọc/ghi ở 16-23 chỗ
// khác trong Main.cs, và FontBd/FontMd/FontLt còn bị FontRestore.cs đụng vào
// từ ngoài. Vì vậy các field KHÔNG được move theo — vẫn là public static trên
// PlagueVnMod (vốn đã là public static sẵn, không cần đổi gì) — ở đây CHỈ
// move method, mọi chỗ đọc/ghi field đều qua PlagueVnMod.<field> tường minh.
// Đây là MOVE thuần túy: không đổi logic/hành vi so với bản gốc trong Main.cs.
public static class FontLoader
{
    public static void LoadFontsFromJson(string fontDir)
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

        PlagueVnMod.FontBd = LoadSlot(ab, json, "Bd");
        PlagueVnMod.FontMd = LoadSlot(ab, json, "Md");
        PlagueVnMod.FontLt = LoadSlot(ab, json, "Lt");

        PlagueVnMod.FontsReady = PlagueVnMod.FontBd != null || PlagueVnMod.FontMd != null || PlagueVnMod.FontLt != null;
        Debug.Log("[Localizer] Bundle OK Bd=" + (PlagueVnMod.FontBd != null ? PlagueVnMod.FontBd.name : "null")
            + " Md=" + (PlagueVnMod.FontMd != null ? PlagueVnMod.FontMd.name : "null")
            + " Lt=" + (PlagueVnMod.FontLt != null ? PlagueVnMod.FontLt.name : "null"));

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
                    int cp = TranslationFileLoader.ParseHex4(json, j + 2);
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

    public static void EnsureFontManifest(string fontDir)
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

            string family = FontFamilyReader.TryReadFontFamily(fontPath); // name table
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

    public static void LoadFontsFromManifest(string fontDir, string manifestPath)
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
                int n = WindowsFontRegistrar.TryAddFontResourceEx(path, WindowsFontRegistrar.FONT_ADD_FLAGS);
                if (n > 0)
                {
                    WindowsFontRegistrar.PrivateFontPaths.Add(path);
                    WindowsFontRegistrar.BroadcastFontChange();
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
            if (gameKey.Equals("Bd", StringComparison.OrdinalIgnoreCase)) PlagueVnMod.FontBd = font;
            else if (gameKey.Equals("Md", StringComparison.OrdinalIgnoreCase)) PlagueVnMod.FontMd = font;
            else if (gameKey.Equals("Lt", StringComparison.OrdinalIgnoreCase)) PlagueVnMod.FontLt = font;

            PlagueVnMod.CustomFonts[fullKey] = font;
            PlagueVnMod.CustomFonts[PlagueVnMod.NormalizeFontName(fullKey)] = font;
            PlagueVnMod.CustomFonts[PlagueVnMod.NormalizeFontName(osName)] = font;
            PlagueVnMod.CustomFonts[PlagueVnMod.NormalizeFontName(used)] = font;
            if (font.fontNames != null)
            {
                for (int i = 0; i < font.fontNames.Length; i++)
                    PlagueVnMod.CustomFonts[PlagueVnMod.NormalizeFontName(font.fontNames[i])] = font;
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
}
