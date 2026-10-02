using System;
using System.IO;
using System.Text;
using UnityEngine;

// Tách ra từ PlagueVnMod (Main.cs) — gom phần đọc family name từ bảng 'name' của
// file TTF/OTF (không cần cài font vào OS trước), cùng các helper encoding phục vụ
// riêng việc này (ReadU16BE/ReadU32BE, Mac-Roman decode, đăng ký CodePages provider).
// Đây là MOVE thuần túy: không đổi logic/hành vi so với bản gốc trong Main.cs.
//
// Nhóm này KHÔNG đụng tới state dùng chung của PlagueVnMod (FontBd/FontMd/FontLt,
// FontsReady, CustomFonts...) — khác với LoadFontsFromJson/LoadSlot/LoadFontsFromManifest,
// vốn đọc/ghi các field đó ở hàng chục chỗ khác trong Main.cs lẫn FontRestore.cs nên
// CHƯA tách ở đợt này (rủi ro cao hơn nhiều nếu làm mù, không có trình biên dịch để
// kiểm chứng — xem giải thích đầy đủ trong tin nhắn).
public static class FontFamilyReader
{
    /// <summary>
    /// Đọc family name từ bảng 'name' trong TTF/OTF (không cần OS).
    /// Ưu tiên Windows platform (3), nameID 1 = Family, 4 = Full.
    /// </summary>
    public static string TryReadFontFamily(string fontPath)
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
}
