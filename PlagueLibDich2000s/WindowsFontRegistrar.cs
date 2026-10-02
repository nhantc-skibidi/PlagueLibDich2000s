using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

// Tách ra từ PlagueVnMod (Main.cs) — gom toàn bộ phần đăng ký font qua Win32
// (AddFontResourceEx/RemoveFontResourceEx + broadcast WM_FONTCHANGE) vào một chỗ.
// Đây là MOVE thuần túy: chữ ký (signature) và logic của từng hàm giữ NGUYÊN so với
// bản gốc trong Main.cs — chỉ đổi noi chứa (class) và độ truy cập (private -> public
// cho những gì PlagueVnMod cần gọi tới), không đổi hành vi.
public static class WindowsFontRegistrar
{
    // Unity 2018.2+ rasterize dynamic font qua DirectWrite. FR_PRIVATE (0x10)
    // chỉ hiện với GDI — DirectWrite không thấy → glyph fallback font hệ thống.
    // Flag 0 = đăng ký session Windows (DirectWrite thấy được). Gỡ bằng
    // RemoveFontResourceEx cùng flag ở OnDestroy / Application.quitting / ProcessExit.
    public const uint FONT_ADD_FLAGS = 0;

    // FIX C-01: platform guard cho P/Invoke native.
    public static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    static extern int AddFontResourceEx(string lpszFilename, uint fl, IntPtr pdv);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    static extern bool RemoveFontResourceEx(string lpFileName, uint fl, IntPtr pdv);

    // FIX C-01: wrapper có platform guard.
    public static int TryAddFontResourceEx(string path, uint flags)
    {
        if (!IsWindows)
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

    public static bool TryRemoveFontResourceEx(string path, uint flags)
    {
        if (!IsWindows) return false;
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

    public static void BroadcastFontChange()
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

    public static readonly List<string> PrivateFontPaths = new List<string>();

    // Lưu ý: hàm Register (đối lập với Unregister) ở bản gốc Main.cs — static void
    // RegisterPrivateFonts(string fontDir) tại dòng 3674 — không được MOVE sang đây.
    // Lý do: grep toàn repo cho thấy hàm đó KHÔNG có nơi nào gọi tới (dead code) —
    // việc đăng ký font thật sự đang nằm trong PlagueVnMod.LoadSlot(), lặp lại gần như
    // y hệt logic của hàm chết này. Mình để nguyên LoadSlot (không đụng), xoá bỏ phần
    // dead code thay vì move nó sang đây, và báo lại ở review bên dưới để bạn xác nhận.
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
}
