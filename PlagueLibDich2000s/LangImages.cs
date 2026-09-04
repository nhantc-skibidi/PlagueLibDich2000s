using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

public static class LangImageVault
{
    // FIX C-07: MaxImageBytes configurable.
    public static long MaxImageBytes { get; set; } = 30L * 1024 * 1024;

    static readonly Dictionary<string, Dictionary<string, Texture2D>> Cache =
        new Dictionary<string, Dictionary<string, Texture2D>>(StringComparer.OrdinalIgnoreCase);

    public static Dictionary<string, Texture2D> Map =
        new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

    static readonly HashSet<int> Ours = new HashSet<int>();
    static readonly Dictionary<string, Texture> OriginalByName =
        new Dictionary<string, Texture>(StringComparer.OrdinalIgnoreCase);

    // FIX CR-06: theo dõi MỌI Material đã bị Material-patch thay mainTexture (không
    // chỉ UIAtlas/UITexture). Patch Material.set_mainTexture chặn TOÀN BỘ material
    // của game — nếu 1 material ngoài UI (3D, hiệu ứng...) có texture trùng tên ảnh
    // custom thì nó cũng bị thay, nhưng RestoreUi() cũ chỉ quét UIAtlas + UITexture
    // → các material đó KHÔNG BAO GIỜ được restore (texture lệch vĩnh viễn cho tới
    // khi thoát game). Bảng này đảm bảo restore đủ mọi material đã đụng tới.
    static readonly Dictionary<Material, Texture> ReplacedMaterials =
        new Dictionary<Material, Texture>();

    static string AppliedFolder;
    static bool Restoring;
    static Coroutine Running;
    static MonoBehaviour Host;
    // FIX MN-05: implement tracking metric.
    internal static long FileTooLargeCount = 0;

    // Được Main.cs gán ("VerboseLogging" trong config) — dùng để bật/tắt log chi tiết
    // khi load/áp ảnh theo ngôn ngữ. Hiện chưa có Debug.Log nào đọc field này bên
    // trong LangImages.cs; nếu muốn log chi tiết hoạt động thật, bọc các Debug.Log
    // quan trọng (ví dụ trong DecodeRoutine, ApplyPack) trong "if (VerboseLogging)".
    public static bool VerboseLogging;

    public static bool IsRestoring { get { return Restoring; } }

    public static void BindHost(MonoBehaviour host) { Host = host; }

    public static void Unload()
    {
        StopLoad();
        RestoreUi();
        Map = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        Ours.Clear();
        AppliedFolder = null;
        // FIX P-06: disable patches khi không còn custom image.
        LangImage_MaterialMainTex_Patch.Disable();
        LangImage_UITextureMainTex_Patch.Disable();  // FIX BUG: gate UITexture patch too
        Debug.Log("[LangImage] Unload — trả gốc (cache PNG vẫn giữ)"
            + (FileTooLargeCount > 0 ? " — " + FileTooLargeCount + " file đã skip >" + (MaxImageBytes / (1024 * 1024)) + "MB" : ""));
    }

    public static void DestroyAll()
    {
        Unload();
        foreach (var pack in Cache.Values)
            foreach (var t in pack.Values)
                if (t != null) UnityEngine.Object.Destroy(t);
        Cache.Clear();
    }

    static void StopLoad()
    {
        if (Running != null && Host != null)
        {
            Host.StopCoroutine(Running);
            Running = null;
        }
    }

    static string CleanName(string n)
    {
        if (string.IsNullOrEmpty(n)) return "";
        if (n.EndsWith(" (Instance)")) n = n.Substring(0, n.Length - 11);
        int hash = n.LastIndexOf(" #");
        if (hash >= 0) n = n.Substring(0, hash);
        return n.Trim();
    }

    static void RememberOriginal(Texture orig)
    {
        if (orig == null || IsOurs(orig)) return;
        string n = CleanName(orig.name);
        if (n.Length == 0) return;
        if (!OriginalByName.ContainsKey(n))
            OriginalByName[n] = orig;
    }

    static bool HasMainTex(Material m)
    {
        return m != null && m.HasProperty("_MainTex");
    }

    static void RestoreUi()
    {
        Restoring = true;
        try
        {
            UIAtlas[] atlases = Resources.FindObjectsOfTypeAll(typeof(UIAtlas)) as UIAtlas[];
            if (atlases != null)
            {
                for (int i = 0; i < atlases.Length; i++)
                {
                    UIAtlas a = atlases[i];
                    if (a == null || !HasMainTex(a.spriteMaterial)) continue;
                    Texture cur = a.spriteMaterial.mainTexture;
                    if (!IsOurs(cur)) continue;
                    Texture orig;
                    if (OriginalByName.TryGetValue(CleanName(cur.name), out orig) && orig != null)
                    {
                        a.spriteMaterial.mainTexture = orig;
                        a.MarkAsDirty();
                    }
                }
            }
            UITexture[] uis = Resources.FindObjectsOfTypeAll(typeof(UITexture)) as UITexture[];
            if (uis == null) return;
            for (int i = 0; i < uis.Length; i++)
            {
                UITexture u = uis[i];
                if (u == null) continue;
                Texture cur = u.mainTexture;
                if (!IsOurs(cur)) continue;
                Texture orig;
                if (OriginalByName.TryGetValue(CleanName(cur.name), out orig) && orig != null)
                    u.mainTexture = orig;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[LangImage] Restore: " + ex.Message);
        }
        finally
        {
            RestoreReplacedMaterials(); // FIX CR-06: restore mọi material đã bị patch thay texture
            Restoring = false;
        }
    }

    // FIX CR-06: trả mainTexture gốc cho các material nằm ngoài tầm quét
    // UIAtlas/UITexture nhưng đã bị Material patch thay texture.
    static void RestoreReplacedMaterials()
    {
        if (ReplacedMaterials.Count == 0) return;
        int n = 0;
        var snapshot = new List<KeyValuePair<Material, Texture>>(ReplacedMaterials);
        ReplacedMaterials.Clear();
        for (int i = 0; i < snapshot.Count; i++)
        {
            try
            {
                Material m = snapshot[i].Key;
                Texture orig = snapshot[i].Value;
                if (m == null || orig == null) continue; // material đã bị destroy
                if (!HasMainTex(m)) continue;
                Texture cur = m.mainTexture;
                if (!IsOurs(cur)) continue; // game đã tự gán texture khác — không đụng
                m.mainTexture = orig;
                n++;
            }
            catch { }
        }
        if (n > 0)
            Debug.Log("[LangImage] RestoreReplacedMaterials: đã trả " + n + " material về texture gốc");
    }

    // FIX CR-06: ghi nhận cặp (material, texture gốc) TRƯỚC khi patch thay texture.
    public static void RememberReplacedMaterial(Material m, Texture orig)
    {
        if (m == null || orig == null || IsOurs(orig)) return;
        if (!ReplacedMaterials.ContainsKey(m))
            ReplacedMaterials[m] = orig;
    }

    static Texture2D DecodeOne(string path)
    {
        // FIX ST-06: FileInfo.Length + File.ReadAllBytes có thể throw.
        long size;
        try { size = new FileInfo(path).Length; }
        catch (Exception ex)
        {
            Debug.LogWarning("[LangImage] skip (cannot stat): " + path + " — " + ex.Message);
            return null;
        }
        if (size > MaxImageBytes)
        {
            // FIX v0.29.4: log đúng giới hạn config thực tế thay vì hardcode "30MB"
            // (từ khi C-07 cho phép cấu hình MaxImageBytes qua config).
            Debug.LogWarning("[LangImage] skip >" + (MaxImageBytes / (1024 * 1024)) + "MB: " + path);
            FileTooLargeCount++;  // FIX MN-05: track metric
            return null;
        }
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (Exception ex)
        {
            Debug.LogWarning("[LangImage] skip (read fail): " + path + " — " + ex.Message);
            return null;
        }
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!tex.LoadImage(data, true))
        {
            UnityEngine.Object.Destroy(tex);
            return null;
        }
        tex.name = CleanName(Path.GetFileNameWithoutExtension(path));
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        return tex;
    }

    // FIX S-10: helper enumerate an toàn cho netstandard2.0 — try/catch từng file,
    // skip symlink (ReparsePoint) và file inaccessible (đang lock bởi process khác).
    static void AddFilesSafe(List<string> result, string dir, string pattern)
    {
        string[] found;
        try { found = Directory.GetFiles(dir, pattern, SearchOption.AllDirectories); }
        catch (UnauthorizedAccessException) { return; }
        catch (DirectoryNotFoundException) { return; }
        catch (IOException) { return; }
        if (found == null) return;
        foreach (var f in found)
        {
            try
            {
                var fi = new FileInfo(f);
                // skip symlink/ReparsePoint — tránh symlink loop vô hạn
                if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            }
            catch { continue; }
            result.Add(f);
        }
    }

    static List<string> CollectImageFiles(string dir)
    {
        var files = new List<string>();
        // FIX S-10: enumerate an toàn, skip symlink, ignore inaccessible.
        // Lưu ý: EnumerationOptions chỉ có từ .NET Standard 2.1+ — project này target
        // netstandard2.0 nên phải dùng SearchOption.AllDirectories + check symlink thủ công.
        try
        {
            AddFilesSafe(files, dir, "*.png");
            AddFilesSafe(files, dir, "*.jpg");
            AddFilesSafe(files, dir, "*.jpeg");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[LangImage] enumerate error: " + ex.Message);
            return new List<string>();
        }
        var best = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < files.Count; i++)
        {
            string key = CleanName(Path.GetFileNameWithoutExtension(files[i]));
            if (key.Length == 0) continue;
            string ext = Path.GetExtension(files[i]);
            string old;
            if (!best.TryGetValue(key, out old))
            {
                best[key] = files[i];
                continue;
            }
            bool png = ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
                && !Path.GetExtension(old).Equals(".png", StringComparison.OrdinalIgnoreCase);
            if (png) best[key] = files[i];
        }
        return new List<string>(best.Values);
    }

    public static void LoadFromLangFolder(string langFolder)
    {
        if (string.IsNullOrEmpty(langFolder)) { Unload(); return; }

        if (string.Equals(AppliedFolder, langFolder, StringComparison.OrdinalIgnoreCase)
            && Map.Count > 0)
            return;

        string dir = Path.Combine(langFolder, ConfigManager.ImagesFolderName); // CFG-08
        if (!Directory.Exists(dir))
        {
            Unload();
            Debug.Log("[LangImage] Không có Images — gốc: " + dir);
            return;
        }

        // FIX BUG (texture không restore khi switch sang L2 không có image):
        // Disable patch + clear Map TRƯỚC khi RestoreUi để tránh race condition.
        // Trước đây, giữa RestoreUi() và ApplyPack() mới có khoảng thời gian mà Map vẫn
        // còn entry cũ + patch vẫn enable. Nếu UIAtlas mới spawn trong khoảng đó, patch
        // sẽ thay texture cho atlas mới bằng texture cũ → không bao giờ restore.
        StopLoad();
        LangImage_MaterialMainTex_Patch.Disable();  // ← Disable patch trước
        LangImage_UITextureMainTex_Patch.Disable();  // ← Disable UITexture patch too
        RestoreUi();  // ← Restore với Ours còn entry, patch disabled → không race
        // Clear state cũ SAU khi RestoreUi xong
        Map = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        Ours.Clear();

        Dictionary<string, Texture2D> pack;
        if (Cache.TryGetValue(langFolder, out pack) && pack != null && pack.Count > 0)
        {
            ApplyPack(pack, langFolder);
            Debug.Log("[LangImage] cache HIT " + pack.Count + " ảnh (không decode)");
            return;
        }

        if (Host != null)
        {
            Running = Host.StartCoroutine(DecodeRoutine(langFolder, dir));
            return;
        }
        pack = DecodeAll(dir);
        Cache[langFolder] = pack;
        ApplyPack(pack, langFolder);
    }

    static Dictionary<string, Texture2D> DecodeAll(string dir)
    {
        var pack = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        List<string> files = CollectImageFiles(dir);
        for (int i = 0; i < files.Count; i++)
        {
            try
            {
                Texture2D tex = DecodeOne(files[i]);
                if (tex != null) pack[tex.name] = tex;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[LangImage] " + files[i] + " : " + ex.Message);
            }
        }
        return pack;
    }

    static IEnumerator DecodeRoutine(string langFolder, string dir)
    {
        var pack = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        List<string> files = CollectImageFiles(dir);
        // FIX MN-06: gate log với VerboseLogging
        if (VerboseLogging)
            Debug.Log("[LangImage] decode async " + files.Count + " file (1/frame)");
        for (int i = 0; i < files.Count; i++)
        {
            try
            {
                Texture2D tex = DecodeOne(files[i]);
                if (tex != null)
                {
                    pack[tex.name] = tex;
                    ApplyOneTexture(tex);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[LangImage] " + files[i] + " : " + ex.Message);
            }
            yield return null;
        }
        Cache[langFolder] = pack;
        ApplyPack(pack, langFolder);
        Running = null;
        if (VerboseLogging)
            Debug.Log("[LangImage] cache mới " + pack.Count + " ảnh: " + dir);
    }

    static void ApplyPack(Dictionary<string, Texture2D> pack, string langFolder)
    {
        Map = pack;
        Ours.Clear();
        foreach (var t in pack.Values)
            if (t != null) Ours.Add(t.GetInstanceID());
        // FIX P-06: enable patches khi có custom image.
        LangImage_MaterialMainTex_Patch.Enable();
        LangImage_UITextureMainTex_Patch.Enable();  // FIX BUG: gate UITexture patch too
        ApplyToLoadedAssets();
        AppliedFolder = langFolder;
    }

    static void ApplyOneTexture(Texture2D tex)
    {
        if (tex == null) return;
        Ours.Add(tex.GetInstanceID());
        Map[tex.name] = tex;
        UIAtlas[] atlases = Resources.FindObjectsOfTypeAll(typeof(UIAtlas)) as UIAtlas[];
        if (atlases == null) return;
        for (int i = 0; i < atlases.Length; i++)
        {
            UIAtlas a = atlases[i];
            if (a == null || !HasMainTex(a.spriteMaterial)) continue;
            Texture cur = a.spriteMaterial.mainTexture;
            if (cur == null) continue;
            if (!CleanName(cur.name).Equals(tex.name, StringComparison.OrdinalIgnoreCase))
                continue;
            RememberOriginal(cur);
            a.spriteMaterial.mainTexture = tex;
            a.MarkAsDirty();
        }
    }

    public static bool IsOurs(Texture t)
    {
        return t != null && Ours.Contains(t.GetInstanceID());
    }

    public static bool TryReplace(Texture orig, out Texture2D custom)
    {
        custom = null;
        if (Restoring || Map.Count == 0) return false;
        if (orig == null || IsOurs(orig)) return false;
        string n = CleanName(orig.name);
        if (n.Length == 0) return false;
        if (n.IndexOf("Font", StringComparison.OrdinalIgnoreCase) >= 0) return false;
        if (!Map.TryGetValue(n, out custom) || custom == null) return false;
        // FIX v0.29.5: log rõ MỌI lần thay texture khi VerboseLogging bật — để chẩn
        // đoán trường hợp tên ảnh custom (trong Images/) trùng tên 1 texture/atlas
        // dùng chung cho nhiều thành phần UI khác nhau (ví dụ atlas chứa cả nền
        // dropdown lẫn ảnh nền menu) → thay nhầm làm vỡ UI không liên quan.
        if (VerboseLogging)
            Debug.Log("[LangImage] TryReplace: '" + orig.name + "' (clean='" + n
                + "') → custom '" + custom.name + "'");
        RememberOriginal(orig);
        return true;
    }

    public static void ApplyToLoadedAssets()
    {
        if (Map.Count == 0) return;
        UIAtlas[] atlases = Resources.FindObjectsOfTypeAll(typeof(UIAtlas)) as UIAtlas[];
        if (atlases != null)
        {
            for (int i = 0; i < atlases.Length; i++)
            {
                UIAtlas a = atlases[i];
                if (a == null || !HasMainTex(a.spriteMaterial)) continue;
                if (TryReplace(a.spriteMaterial.mainTexture, out Texture2D rep))
                {
                    a.spriteMaterial.mainTexture = rep;
                    a.MarkAsDirty();
                }
            }
        }
        UITexture[] uis = Resources.FindObjectsOfTypeAll(typeof(UITexture)) as UITexture[];
        if (uis == null) return;
        for (int i = 0; i < uis.Length; i++)
        {
            UITexture u = uis[i];
            if (u == null) continue;
            if (TryReplace(u.mainTexture, out Texture2D rep))
                u.mainTexture = rep;
        }
    }
}

[HarmonyPatch(typeof(Material), "set_mainTexture")]
public static class LangImage_MaterialMainTex_Patch
{
    // FIX P-06: gate static — skip Prefix nhanh nếu không có custom image.
    static bool _enabled = false;

    static void Prefix(Material __instance, ref Texture value)
    {
        if (!_enabled) return;
        if (LangImageVault.IsRestoring) return;
        if (__instance == null || !__instance.HasProperty("_MainTex")) return;
        if (LangImageVault.TryReplace(value, out Texture2D t))
        {
            // FIX CR-06: nhớ texture gốc của material này để còn đường restore —
            // material ngoài UI (không thuộc UIAtlas/UITexture nào) cũng bị thay
            // texture bởi patch này nên phải tự theo dõi.
            LangImageVault.RememberReplacedMaterial(__instance, value);
            value = t;
        }
    }

    public static void Enable() { _enabled = true; }
    public static void Disable() { _enabled = false; }
}

[HarmonyPatch(typeof(UITexture), "set_mainTexture")]
public static class LangImage_UITextureMainTex_Patch
{
    // FIX BUG: thêm _enabled gate giống Material patch — tránh thay texture
    // khi không có custom image (sau khi switch sang L2 không có Images).
    static bool _enabled = false;

    static void Prefix(UITexture __instance, ref Texture value)
    {
        if (!_enabled) return;
        if (LangImageVault.IsRestoring) return;
        // FIX v0.29.5: bỏ qua UITexture thuộc UIPopupList (nền/highlight dropdown) —
        // tránh trường hợp tên ảnh custom trong Images/ trùng tên texture nội bộ của
        // popup (vd người dịch đặt tên file "Background.png"/"Highlight.png"...) khiến
        // TryReplace() thay nhầm texture nền dropdown bằng ảnh custom không liên quan
        // → hộp đen / vỡ hình popup.
        if (__instance != null && __instance.GetComponentInParent<UIPopupList>() != null)
        {
            if (LangImageVault.VerboseLogging)
                Debug.Log("[LangImage] Skip replace (popup-owned UITexture): " + __instance.name);
            return;
        }
        if (LangImageVault.TryReplace(value, out Texture2D t))
            value = t;
    }

    public static void Enable() { _enabled = true; }
    public static void Disable() { _enabled = false; }
}