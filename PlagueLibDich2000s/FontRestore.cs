using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

public static class FontRestore
{
    static readonly ConditionalWeakTable<UILabel, Font> OrigTtf =
        new ConditionalWeakTable<UILabel, Font>();
    static readonly ConditionalWeakTable<UILabel, UIFont> OrigBitmap =
        new ConditionalWeakTable<UILabel, UIFont>();
    static readonly ConditionalWeakTable<UILabel, string> WeightTable =
        new ConditionalWeakTable<UILabel, string>();

    static readonly ConditionalWeakTable<UIFont, Font> OrigUiDyn =
        new ConditionalWeakTable<UIFont, Font>();

    static readonly System.Reflection.FieldInfo FiMFont =
        AccessTools.Field(typeof(UILabel), "mFont");

    static readonly System.Reflection.FieldInfo FiUiDyn =
        AccessTools.Field(typeof(UIFont), "mDynamicFont");

    static readonly System.Reflection.FieldInfo FiUiRep =
        AccessTools.Field(typeof(UIFont), "mReplacement");

    static bool IsOurs(Font f)
    {
        if (f == null) return false;
        return ReferenceEquals(f, PlagueVnMod.FontBd)
            || ReferenceEquals(f, PlagueVnMod.FontMd)
            || ReferenceEquals(f, PlagueVnMod.FontLt);
    }

    static bool IsProxy(UIFont ui)
    {
        return ui != null && ui.name != null
            && ui.name.IndexOf("UIFontProxy", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static bool IsLiveSceneObject(Component c)
    {
        if (c == null) return false;
        try
        {
            GameObject go = c.gameObject;
            if (go == null) return false;
            return go.scene.IsValid();
        }
        catch { return false; }
    }

    public static void Capture(UILabel lab)
    {
        if (lab == null) return;
        try
        {
            if (PlagueVnMod.IsPopupRelated(lab)) return;
            if (!IsLiveSceneObject(lab)) return;
            if (WeightTable.TryGetValue(lab, out string _existingWeight)) return;

            UIFont bm = lab.bitmapFont;
            Font ttf = lab.trueTypeFont;
            if (FiMFont != null && bm == null)
            {
                try { bm = FiMFont.GetValue(lab) as UIFont; } catch { }
            }

            if (bm != null && !IsProxy(bm)) OrigBitmap.Add(lab, bm);
            if (ttf != null && !IsOurs(ttf)) OrigTtf.Add(lab, ttf);

            string src = bm != null ? bm.name : (ttf != null ? ttf.name : null);
            WeightTable.Add(lab, DetectWeight(src) ?? "Md");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[Localizer] FontRestore.Capture lỗi (skip label này): " + ex.Message);
        }
    }

    public static string DetectWeight(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        string s = PlagueVnMod.NormalizeFontName(name);
        string[] parts = s.Split(new[] { '_', '-', ' ', '.' },
            StringSplitOptions.RemoveEmptyEntries);
        bool bd = false, md = false, lt = false;
        for (int i = 0; i < parts.Length; i++)
        {
            string p = parts[i];
            if (ConfigManager.WeightKeywordsBold.Contains(p)) bd = true;
            else if (ConfigManager.WeightKeywordsLight.Contains(p)) lt = true;
            else if (ConfigManager.WeightKeywordsMedium.Contains(p)) md = true;
        }
        if (bd) return "Bd";
        if (lt) return "Lt";
        if (md) return "Md";
        return null;
    }

    public static Font FontForWeight(string w)
    {
        if (w == "Bd") return PlagueVnMod.FontBd ?? PlagueVnMod.FontMd ?? PlagueVnMod.FontLt;
        if (w == "Lt") return PlagueVnMod.FontLt ?? PlagueVnMod.FontMd ?? PlagueVnMod.FontBd;
        return PlagueVnMod.FontMd ?? PlagueVnMod.FontBd ?? PlagueVnMod.FontLt;
    }

    public static Font Pick(UILabel label)
    {
        if (label == null)
            return PlagueVnMod.FontMd ?? PlagueVnMod.FontBd ?? PlagueVnMod.FontLt;
        Capture(label);
        string w;
        if (!WeightTable.TryGetValue(label, out w) || w == null)
            w = "Md";
        return FontForWeight(w);
    }

    public static void RestoreAll()
    {
        UILabel[] labels = Resources.FindObjectsOfTypeAll(typeof(UILabel)) as UILabel[];
        if (labels == null) return;
        int n = 0;
        for (int i = 0; i < labels.Length; i++)
        {
            UILabel lab = labels[i];
            if (lab == null) continue;
            try
            {
                if (!IsLiveSceneObject(lab)) continue;
                if (PlagueVnMod.IsPopupRelated(lab)) continue;
            }
            catch { continue; }

            UIFont bm;
            Font ttf;
            OrigBitmap.TryGetValue(lab, out bm);
            OrigTtf.TryGetValue(lab, out ttf);
            try
            {
                if (bm != null) lab.bitmapFont = bm;
                else if (ttf != null) lab.trueTypeFont = ttf;
                else continue;
                n++;
            }
            catch { }
        }
        Debug.Log("[Localizer] Restore original fonts on " + n + " labels");
    }

    public static void CaptureUIFont(UIFont ui)
    {
        if (ui == null || FiUiDyn == null) return;
        if (OrigUiDyn.TryGetValue(ui, out _)) return;
        try
        {
            Font cur = FiUiDyn.GetValue(ui) as Font;
            if (cur != null && !IsOurs(cur))
                OrigUiDyn.Add(ui, cur);
        }
        catch { }
    }

    public static bool IsUiFontUsedByPopup(UIFont ui)
    {
        if (ui == null) return false;
        try
        {
            UILabel[] labels = Resources.FindObjectsOfTypeAll(typeof(UILabel)) as UILabel[];
            if (labels == null) return false;

            for (int i = 0; i < labels.Length; i++)
            {
                UILabel lab = labels[i];
                if (lab == null) continue;
                if (!PlagueVnMod.IsPopupRelated(lab)) continue;

                if (ReferenceEquals(lab.bitmapFont, ui)) return true;

                if (FiMFont != null)
                {
                    try
                    {
                        UIFont mf = FiMFont.GetValue(lab) as UIFont;
                        if (ReferenceEquals(mf, ui)) return true;
                    }
                    catch { }
                }
            }
        }
        catch { }
        return false;
    }

    public static void RestoreUIFonts()
    {
        try
        {
            UIFont[] all = Resources.FindObjectsOfTypeAll(typeof(UIFont)) as UIFont[];
            if (all == null) return;

            int nDyn = 0;
            int nRep = 0;

            for (int i = 0; i < all.Length; i++)
            {
                UIFont ui = all[i];
                if (ui == null) continue;
                if (IsProxy(ui)) continue;

                try
                {
                    if (FiUiDyn != null && OrigUiDyn.TryGetValue(ui, out Font orig) && orig != null)
                    {
                        Font cur = FiUiDyn.GetValue(ui) as Font;
                        if (!ReferenceEquals(cur, orig))
                        {
                            FiUiDyn.SetValue(ui, orig);
                            nDyn++;
                        }
                        OrigUiDyn.Remove(ui);
                    }

                    if (FiUiRep != null)
                    {
                        UIFont rep = FiUiRep.GetValue(ui) as UIFont;
                        bool present = !ReferenceEquals(rep, null);
                        bool alive = rep != null;
                        if (present && (!alive || IsProxy(rep)))
                        {
                            FiUiRep.SetValue(ui, null);
                            nRep++;
                        }
                    }

                    try { ui.MarkAsDirty(); } catch { }
                }
                catch { }
            }

            Debug.Log("[Localizer] RestoreUIFonts: dyn=" + nDyn + " clearReplacement=" + nRep);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[Localizer] RestoreUIFonts lỗi: " + ex.Message);
        }
    }
}
