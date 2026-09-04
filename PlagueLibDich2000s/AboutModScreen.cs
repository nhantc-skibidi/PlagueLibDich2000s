using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

public class CMainAboutModSubScreen : CMainSubScreen
{
    public const string ScreenName = "Menu_Sub_AboutMod";
    public const string KeyTitle = "FE_About_Mod";
    public const string KeyDesc = "FE_About_Mod_Desc";

    // CFG-08 (Zero-Hardcode): text fallback của màn About lấy từ ConfigManager
    // ([About] FallbackTitle / FallbackDescription) — người dùng tự sửa trong
    // BepInEx config, không cần đợi bản build mới. Giá trị mặc định (đặt trong
    // ConfigManager) giữ nguyên nội dung cũ, kèm version hiện tại của mod.
    public static string DefaultTitle { get { return ConfigManager.AboutFallbackTitle; } }
    public static string DefaultDesc { get { return ConfigManager.AboutFallbackDescription; } }
    public static string LangFolder;

    public UITable table;
    public UILabelAutotranslate h1Template;
    public UILabelAutotranslate textTemplate;
    public UISprite spacerTemplate;
    public UIScrollBar scrollBar;
    public UITexture avatar;

    static Texture2D _embeddedAvatar;
    Texture2D _fileAvatar;
    string _fileAvatarPath;
    long _fileAvatarWriteTicks;

    public static string GetLoc(string key)
    {
        if (PlagueVnMod.TryGetTranslation(key, out string t) &&
            !string.IsNullOrEmpty(t) &&
            !t.Equals(key, StringComparison.OrdinalIgnoreCase))
            return t;
        try
        {
            string g = CLocalisationManager.GetText(key);
            if (!string.IsNullOrEmpty(g) &&
                !g.Equals(key, StringComparison.OrdinalIgnoreCase))
                return g;
        }
        catch { }
        if (key == KeyTitle) return DefaultTitle;
        if (key == KeyDesc) return DefaultDesc;
        return key;
    }

    public static void InjectFallbackIntoAllLanguages()
    {
        try
        {
            var field = typeof(CLocalisationManager).GetField(
                "mpLocalisedTexts", BindingFlags.NonPublic | BindingFlags.Static);
            if (field == null) return;
            var all = field.GetValue(null) as Dictionary<string, Dictionary<string, string>>;
            if (all == null) return;

            foreach (var kv in all)
            {
                Dictionary<string, string> d = kv.Value;
                if (d == null) continue;
                if (!d.ContainsKey(KeyTitle)) d[KeyTitle] = DefaultTitle;
                if (!d.ContainsKey(KeyDesc)) d[KeyDesc] = DefaultDesc;
                // FIX CR-11: ToLowerInvariant (culture-safe) — ToLower() trên Windows
                // Turkish locale biến "I" thành "ı" (không dấu chấm) làm sai key tra cứu.
                if (!d.ContainsKey(KeyTitle.ToLowerInvariant())) d[KeyTitle.ToLowerInvariant()] = DefaultTitle;
                if (!d.ContainsKey(KeyDesc.ToLowerInvariant())) d[KeyDesc.ToLowerInvariant()] = DefaultDesc;
            }
            Debug.Log("[AboutMod] Inject fallback vào " + all.Count + " ngôn ngữ");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[AboutMod] Inject: " + ex.Message);
        }
    }

    public static Texture2D GetEmbeddedAvatar()
    {
        if (_embeddedAvatar != null) return _embeddedAvatar;
        try
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            string resName = null;
            string[] names = asm.GetManifestResourceNames();
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i].EndsWith("AboutMod.png", StringComparison.OrdinalIgnoreCase))
                {
                    resName = names[i];
                    break;
                }
            }
            if (resName == null)
            {
                Debug.LogWarning("[AboutMod] DLL không có embedded AboutMod.png");
                return null;
            }
            using (Stream s = asm.GetManifestResourceStream(resName))
            {
                if (s == null) return null;
                // FIX (ổn định): Stream.Read không đảm bảo đọc đủ trong 1 lần gọi — đọc
                // thiếu sẽ tạo ảnh hỏng/giải mã sai. Đọc theo vòng lặp tới khi đủ bytes.
                byte[] data = new byte[s.Length];
                int total = 0;
                while (total < data.Length)
                {
                    int read = s.Read(data, total, data.Length - total);
                    if (read <= 0) break;
                    total += read;
                }
                if (total != data.Length)
                {
                    Debug.LogWarning("[AboutMod] embed avatar: đọc thiếu byte (" + total + "/" + data.Length + ")");
                    return null;
                }
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!tex.LoadImage(data, true))
                {
                    UnityEngine.Object.Destroy(tex);
                    return null;
                }
                tex.name = "AboutMod_Embedded";
                _embeddedAvatar = tex;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[AboutMod] embed avatar: " + ex.Message);
        }
        return _embeddedAvatar;
    }

    public override void Initialise()
    {
        base.Initialise();
        BuildContent();
    }

    public override void SetActive(bool b)
    {
        base.SetActive(b);
        if (!b) return;
        BuildContent();
        if (scrollBar != null) scrollBar.value = 0f;
        if (table != null) table.Reposition();
    }

    public void BuildContent()
    {
        if (table == null || h1Template == null || textTemplate == null) return;

        ClearRows();
        EnsureAvatarWidget();
        AddLabel(h1Template, GetLoc(KeyTitle));
        AddSpacer();
        AddLabel(textTemplate, GetLoc(KeyDesc));
        table.Reposition();
    }

    void ClearRows()
    {
        List<GameObject> kill = new List<GameObject>();
        for (int i = 0; i < table.transform.childCount; i++)
        {
            Transform c = table.transform.GetChild(i);
            if (c == null) continue;
            if (h1Template != null && c == h1Template.transform) continue;
            if (textTemplate != null && c == textTemplate.transform) continue;
            if (spacerTemplate != null && c == spacerTemplate.transform) continue;
            if (avatar != null && c == avatar.transform) continue;
            kill.Add(c.gameObject);
        }
        for (int i = 0; i < kill.Count; i++)
            UnityEngine.Object.Destroy(kill[i]);
    }

    void AddSpacer()
    {
        if (spacerTemplate == null) return;
        UnityEngine.Object.Instantiate(spacerTemplate, table.transform);
    }

    void AddLabel(UILabelAutotranslate tmpl, string text)
    {
        UILabelAutotranslate row = UnityEngine.Object.Instantiate(tmpl, table.transform);
        row.gameObject.SetActive(true);
        UILabel lab = row.GetComponent<UILabel>();
        if (lab != null) lab.text = text;
        else row.SetInitialText(text, false);
    }

    void EnsureAvatarWidget()
    {
        Texture2D tex = TryLoadFileAvatar() ?? GetEmbeddedAvatar();
        if (tex == null)
        {
            Debug.LogWarning("[AboutMod] Không có avatar (embed + file)");
            return;
        }

        Transform host = table.transform.parent; // panel, không phải table
        if (host == null) host = table.transform;

        if (avatar == null)
        {
            avatar = NGUITools.AddWidget<UITexture>(host.gameObject);
            avatar.name = "AboutMod_Avatar";
            avatar.pivot = UIWidget.Pivot.Top;
            avatar.depth = 12;
        }

        // FIX (đúng tỷ lệ ảnh): trước đây ép cứng avatar.width = avatar.height = 230,
        // giả định ảnh vuông. AboutMod.png thực tế là wordmark ngang (ví dụ 1500x314,
        // tỷ lệ ~4.78:1) → ép vào khung vuông làm ảnh bị bóp méo/trông nhỏ bất thường.
        // Giờ chỉ khai báo TRƯỚC 1 chiều (chiều rộng mong muốn), chiều còn lại tự tính
        // theo đúng tỷ lệ thật của texture đang load — đổi ảnh khác tỷ lệ khác vẫn đúng.
        // CFG-08: chiều rộng logo lấy từ config ([About] AvatarWidth).
        int avatarWidth = ConfigManager.AboutAvatarWidth > 0 ? ConfigManager.AboutAvatarWidth : 320;
        int avatarHeight = avatarWidth;
        if (tex.width > 0 && tex.height > 0)
        {
            float aspect = (float)tex.width / tex.height;
            avatarHeight = Mathf.Max(1, Mathf.RoundToInt(avatarWidth / aspect));
        }

        avatar.mainTexture = tex;
        avatar.width = avatarWidth;
        avatar.height = avatarHeight;
        avatar.color = Color.white;
        avatar.transform.localPosition = new Vector3(-17.75f, 115f, 0f); // chỉnh Y ở đây
        avatar.gameObject.SetActive(true);
    }

    Texture2D TryLoadFileAvatar()
    {
        if (string.IsNullOrEmpty(LangFolder)) return null;
        // CFG-08: tên folder ảnh + tên file avatar lấy từ config ([Images]/[About]).
        string path = Path.Combine(LangFolder, ConfigManager.ImagesFolderName, ConfigManager.AboutAvatarFileName);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > LangImageVault.MaxImageBytes)
        {
            // FIX v0.29.4: log đúng giới hạn config thực tế thay vì hardcode "30MB".
            Debug.LogWarning("[AboutMod] AboutMod.png >" + (LangImageVault.MaxImageBytes / (1024 * 1024)) + "MB, skip");
            return null;
        }
        try
        {
            // FIX (mượt mà): BuildContent() chạy lại mỗi lần mở About-screen, trước đây
            // sẽ giải mã + upload GPU texture mới mỗi lần dù ảnh không đổi. Cache theo
            // path + thời gian sửa file, chỉ decode lại khi thật sự cần.
            long writeTicks = File.GetLastWriteTimeUtc(path).Ticks;
            if (_fileAvatar != null &&
                string.Equals(_fileAvatarPath, path, StringComparison.OrdinalIgnoreCase) &&
                _fileAvatarWriteTicks == writeTicks)
                return _fileAvatar;

            if (_fileAvatar != null)
            {
                UnityEngine.Object.Destroy(_fileAvatar);
                _fileAvatar = null;
            }
            byte[] data = File.ReadAllBytes(path);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(data, true))
            {
                UnityEngine.Object.Destroy(tex);
                return null;
            }
            tex.name = "AboutMod_File";
            _fileAvatar = tex;
            _fileAvatarPath = path;
            _fileAvatarWriteTicks = writeTicks;
            return tex;
        }
        catch { return null; }
    }

    void OnDestroy()
    {
        if (_fileAvatar != null)
            UnityEngine.Object.Destroy(_fileAvatar);
        _fileAvatar = null;
        _fileAvatarPath = null;
    }
}

[HarmonyPatch(typeof(CMainAboutSubScreen), "Initialise")]
public static class AboutMod_InjectButton_Patch
{
    internal static UIButton _btn;

    static UIWidget FindHelpHeader(Transform root)
    {
        if (root == null) return null;
        UILabel[] labs = root.GetComponentsInChildren<UILabel>(true);
        for (int i = 0; i < labs.Length; i++)
        {
            string t = labs[i].text ?? "";
            string n = labs[i].name ?? "";
            if (t.IndexOf("HELP", StringComparison.OrdinalIgnoreCase) >= 0
                || t.IndexOf("INFO", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Header", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Title", StringComparison.OrdinalIgnoreCase) >= 0)
                return labs[i];
        }
        return null;
    }

    static void StripAutotranslate(GameObject go)
    {
        UILabelAutotranslate[] autos = go.GetComponentsInChildren<UILabelAutotranslate>(true);
        for (int i = 0; i < autos.Length; i++)
            UnityEngine.Object.Destroy(autos[i]);
    }
    static IGameScreen GetHostScreen(IGameSubScreen sub)
    {
        if (sub == null) return null;
        Type t = sub.GetType();
        while (t != null && t != typeof(MonoBehaviour) && t != typeof(object))
        {
            var props = t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < props.Length; i++)
            {
                if (!typeof(IGameScreen).IsAssignableFrom(props[i].PropertyType)) continue;
                try
                {
                    IGameScreen s = props[i].GetValue(sub, null) as IGameScreen;
                    if (s != null) return s;
                }
                catch { }
            }
            var fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < fields.Length; i++)
            {
                if (!typeof(IGameScreen).IsAssignableFrom(fields[i].FieldType)) continue;
                try
                {
                    IGameScreen s = fields[i].GetValue(sub) as IGameScreen;
                    if (s != null) return s;
                }
                catch { }
            }
            t = t.BaseType;
        }
        return sub.GetComponentInParent<IGameScreen>();
    }

    static void SetHostScreen(IGameSubScreen sub, IGameScreen host)
    {
        if (sub == null || host == null) return;
        Type t = sub.GetType();
        while (t != null && t != typeof(MonoBehaviour) && t != typeof(object))
        {
            var props = t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < props.Length; i++)
            {
                if (!typeof(IGameScreen).IsAssignableFrom(props[i].PropertyType) || !props[i].CanWrite)
                    continue;
                try { props[i].SetValue(sub, host, null); return; } catch { }
            }
            var fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < fields.Length; i++)
            {
                if (!typeof(IGameScreen).IsAssignableFrom(fields[i].FieldType)) continue;
                try { fields[i].SetValue(sub, host); return; } catch { }
            }
            t = t.BaseType;
        }
    }

    static void Postfix(CMainAboutSubScreen __instance)
    {
        if (__instance == null || __instance.buttonCredits == null) return;
        if (_btn != null) return;

        UIButton src = __instance.buttonCredits;

        // Panel chứa tab HELP & INFO (thường là parent.parent của nút)
        Transform panel = src.transform.parent;
        if (panel != null && panel.parent != null)
            panel = panel.parent;

        UIWidget header = FindHelpHeader(panel);
        Transform attach = header != null ? header.transform.parent : panel;
        if (attach == null) attach = src.transform.parent;

        GameObject go = NGUITools.AddChild(attach.gameObject, src.gameObject);
        go.name = "Button_AboutMod";
        go.transform.localScale = src.transform.localScale;
        go.transform.localRotation = src.transform.localRotation;

        // KHÔNG SetParent vào table, KHÔNG Reposition — nếu không sẽ thành Credits thứ 2
        if (header != null)
        {
            Vector3 hp = attach.InverseTransformPoint(header.transform.position);
            float h = header.height > 1f ? header.height : 36f;
            go.transform.localPosition = new Vector3(
                src.transform.localPosition.x,
                hp.y + 86f, //x y z — FIX CR-15: bỏ "h * 0f" (toán tử chết, luôn = 0)
                4f);
        }
        else
            go.transform.localPosition = src.transform.localPosition + new Vector3(0f, 220f, 0f);

        _btn = go.GetComponent<UIButton>();
        _btn.onClick.Clear();
        EventDelegate.Set(_btn.onClick, () => OpenAboutMod(__instance));

        StripAutotranslate(go);
        ApplyButtonCaption(go);

        Debug.Log("[AboutMod] header=" + (header != null ? header.name + " y=" + header.transform.localPosition.y : "null")
            + " btnY=" + go.transform.localPosition.y);
    }

    internal static void ApplyButtonCaption(GameObject go)
    {
        string cap = CMainAboutModSubScreen.GetLoc(CMainAboutModSubScreen.KeyTitle);
        UILabel[] labs = go.GetComponentsInChildren<UILabel>(true);
        for (int i = 0; i < labs.Length; i++)
            labs[i].text = cap;
    }

    static void OpenAboutMod(CMainAboutSubScreen about)
    {
        CMainAboutModSubScreen screen = EnsureScreen(about);
        if (screen == null)
        {
            Debug.LogWarning("[AboutMod] Không clone Menu_Sub_Credits");
            return;
        }
        CUIManager.instance.SaveBreadcrumbCurrent();
        IGameScreen host = GetHostScreen(about);
        if (host != null)
            host.HideSubScreen("Start");
        CUIManager.instance.SetSubScreen(screen);
    }

    static CMainAboutModSubScreen EnsureScreen(CMainAboutSubScreen about)
    {
        try
        {
            return EnsureScreenInner(about);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[AboutMod] EnsureScreen: " + ex.Message);
            return null;
        }
    }

    static CMainAboutModSubScreen EnsureScreenInner(CMainAboutSubScreen about)
    {
        IGameScreen host = GetHostScreen(about);
        if (host == null) return null;

        IGameSubScreen existing = host.GetSubScreen(CMainAboutModSubScreen.ScreenName);
        CMainAboutModSubScreen ready = existing as CMainAboutModSubScreen;
        if (ready != null)
            return ready;

        CMainCreditsSubScreen src = FindCredits(host);
        if (src == null) return null;

        GameObject clone = UnityEngine.Object.Instantiate(src.gameObject, src.transform.parent);
        clone.name = CMainAboutModSubScreen.ScreenName;
        clone.SetActive(false);

        CMainCreditsSubScreen old = clone.GetComponent<CMainCreditsSubScreen>();
        UIButton back = old != null ? old.buttonBack : null;
        UITable table = old != null ? old.creditsTable : null;
        UILabelAutotranslate h1 = old != null ? old.h1Element : null;
        UILabelAutotranslate body = old != null ? old.textElement : null;
        UISprite spacer = old != null ? old.spacer : null;
        UIScrollBar bar = old != null ? old.scrollBar : null;
        string cat = old != null ? old.category : src.category;

        if (old != null)
            UnityEngine.Object.DestroyImmediate(old);

        CMainAboutModSubScreen ours = clone.AddComponent<CMainAboutModSubScreen>();
        ours.buttonBack = back;
        ours.table = table;
        ours.h1Template = h1;
        ours.textTemplate = body;
        ours.spacerTemplate = spacer;
        ours.scrollBar = bar;
        ours.category = cat;
        SetHostScreen(ours, host);

        if (h1 != null) h1.gameObject.SetActive(false);
        if (body != null) body.gameObject.SetActive(false);
        if (spacer != null) spacer.gameObject.SetActive(false);

        host.subScreens.Add(ours);
        // FIX CR-14: AccessTools.Field có thể trả về null nếu "subScreenLookup"
        // không được khai báo ngay trên IGameScreen (ví dụ nằm ở class dẫn xuất) —
        // gọi .GetValue() trên FieldInfo null sẽ ném NullReferenceException, làm
        // EnsureScreen fail toàn bộ (màn hình About Mod không mở được). Kiểm tra an toàn.
        var fiLookup = AccessTools.Field(typeof(IGameScreen), "subScreenLookup");
        var lookup = fiLookup != null
            ? fiLookup.GetValue(host) as IDictionary<string, IGameSubScreen>
            : null;
        if (lookup != null)
            lookup[CMainAboutModSubScreen.ScreenName] = ours;

        ours.Initialise();
        UILabel[] labs = clone.GetComponentsInChildren<UILabel>(true);
        string title = CMainAboutModSubScreen.GetLoc(CMainAboutModSubScreen.KeyTitle);
        for (int i = 0; i < labs.Length; i++)
        {
            string n = labs[i].name ?? "";
            string t = labs[i].text ?? "";
            if (n.IndexOf("Title", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Header", StringComparison.OrdinalIgnoreCase) >= 0
                || t.IndexOf("CREDIT", StringComparison.OrdinalIgnoreCase) >= 0)
                labs[i].text = title;
        }
        return ours;
    }

    static CMainCreditsSubScreen FindCredits(IGameScreen host)
    {
        IGameSubScreen s = host.GetSubScreen("Menu_Sub_Credits");
        CMainCreditsSubScreen c = s as CMainCreditsSubScreen;
        if (c != null) return c;
        for (int i = 0; i < host.subScreens.Count; i++)
        {
            CMainCreditsSubScreen c2 = host.subScreens[i] as CMainCreditsSubScreen;
            if (c2 != null) return c2;
        }
        return null;
    }


}