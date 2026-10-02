# Cài đặt PlagueLibDich2000s (dành cho người chơi)

> File này dành cho **người chơi muốn cài mod** — không cần biết lập trình, không
> cần .NET SDK, không cần đụng vào source code. Nếu bạn muốn tự sửa code và build
> lại mod, xem `SETUP.md` thay vì file này.

## Yêu cầu

- Đã cài **BepInEx** cho Plague Inc: Evolved (BepInEx 5.x, bản dành cho game Mono).
  Nếu chưa cài BepInEx, làm bước này trước — mod này chỉ chạy được khi có BepInEx.
- Đã tải file **`PlagueLibDich2000s.dll`** (bản build sẵn, do team Dịch 2000s phát hành).

## Các bước cài

1. Mở thư mục cài đặt game Plague Inc: Evolved (ví dụ trong Steam: chuột phải game
   → Manage → Browse local files).
2. Vào thư mục `BepInEx\plugins\`.
3. Tạo một thư mục con tên `PlagueLibDich2000s` (nếu chưa có).
4. Copy file `PlagueLibDich2000s.dll` vào trong thư mục `PlagueLibDich2000s\` vừa tạo.

Cấu trúc sau khi cài xong sẽ giống thế này:
```
Plague Inc Evolved\
└── BepInEx\
    └── plugins\
        └── PlagueLibDich2000s\
            └── PlagueLibDich2000s.dll
```

5. Vào thư mục `BepInEx\config\Language\` (mod sẽ tự tạo ra sau lần chạy game đầu
   tiên nếu chưa có), tạo thư mục con cho ngôn ngữ của bạn, copy các file dịch
   (`.txt`, folder `Font\`, folder `Images\`...) do team cung cấp vào đó, và tạo
   file `<tên-ngôn-ngữ>.enabled` (file rỗng) để bật ngôn ngữ đó.

6. (Tuỳ chọn, build 5 trở đi) Toàn bộ tham số của mod đã được bóc tách ra file cấu
   hình chuẩn BepInEx: `BepInEx\config\com.Dich2000s.PlagueLib.cfg` (game tự sinh
   sau lần chạy đầu tiên). Vài mục hay dùng:
   - `Language.LanguageFolder` — thư mục pack ngôn ngữ (trống = `BepInEx\config\Language`,
     có thể trỏ đi chỗ khác, chấp nhận đường dẫn tương đối/tuyệt đối).
   - `Language.OfficialLanguages` — danh sách ngôn ngữ gốc của game; pack đặt tên trùng
     sẽ bị bỏ qua để bảo vệ danh sách ngôn ngữ (dropdown) của game.
   - `Boot.UiPollTimeoutSeconds` / `Boot.LanguageLoadDelaySeconds` — các mốc thời gian
     khi game khởi động (máy chậm có thể tăng).
   - `Fonts.RegisterPrivateFonts`, `Fonts.WeightKeywords*` — cấu hình nạp font.
   Sửa file `.cfg` khi game không chạy, hoặc bằng trình quản lý config của BepInEx —
   phần lớn các mục áp dụng được ngay giữa game.

6. Mở game. Mod sẽ tự nạp — nếu ngôn ngữ custom đã bật, game sẽ tự chuyển sang
   ngôn ngữ đó (hoặc bạn tự chọn trong menu ngôn ngữ trong game).

## Gỡ mod

Xoá thư mục `BepInEx\plugins\PlagueLibDich2000s\` là xong — không ảnh hưởng gì tới
save game hay các mod khác.

## Gặp lỗi?

- Game không lên / crash ngay khi mở → kiểm tra đã cài đúng bản BepInEx (Mono, không
  phải IL2CPP) và đúng phiên bản game chưa.
- Mod không hiện ngôn ngữ mới → kiểm tra đã có file `<tên-ngôn-ngữ>.enabled` trong
  đúng thư mục ngôn ngữ chưa (bước 5).
- Font hiển thị sai/ô vuông → kiểm tra thư mục `Font\` trong gói ngôn ngữ có đủ file
  `.ttf`/`.otf` không.
