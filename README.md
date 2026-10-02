# PlagueLib-Dich2000s (EN)

An **unofficial** language mod for **Plague Inc: Evolved**, made by the Dịch 2000s team with the assistance of AI. It lets the game load custom language packs (such as Vietnamese) on top of the official languages.

> This project is a fan-made mod and is not affiliated with or endorsed by Ndemic Creations.

## Installation

### Requirements
- Plague Inc: Evolved (PC, Steam)
- [BepInEx](https://github.com/BepInEx/BepInEx/releases) (pick the build that matches your game, usually Windows x64)

### Steps
1. **Install BepInEx**
   - Download BepInEx from the [releases page](https://github.com/BepInEx/BepInEx/releases).
   - Extract the contents into your Plague Inc game folder, next to `PlagueIncEvolved.exe`.
   - Run the game once, then close it. BepInEx will generate its folders (`BepInEx/plugins`, `BepInEx/config`, ...).
2. **Install the mod**
   - Download the latest files from the [Releases page](https://github.com/nhantc-skibidi/PlagueLibDich2000s/releases).
   - Copy them into `[Plague Inc folder]/BepInEx/plugins`.
3. **Launch the game** and choose the custom language in the language options.

For more detailed instructions, see [INSTALL.md](INSTALL.md) and [SETUP.md](SETUP.md).

### Uninstall
Delete the mod files from `[Plague Inc folder]/BepInEx/plugins`. The game returns to its official languages.

## Troubleshooting
- **The game starts but the mod does not load:** check that BepInEx was installed in the same folder as the game's `.exe`, and look at `BepInEx/LogOutput.log` for errors.
- **The custom language is missing from the list:** make sure the mod file is directly inside `BepInEx/plugins`, not in a nested subfolder.
- **Still stuck?** Open an [issue](https://github.com/nhantc-skibidi/PlagueLibDich2000s/issues) and attach your `LogOutput.log`.

## Create your own language
Want to add another language? See [CREATE_LANGUAGE.md](CREATE_LANGUAGE.md).

## Project structure
[![Architecture diagram of nhantc-skibidi/plaguelibdich2000s](https://gitdiagram.com/nhantc-skibidi/plaguelibdich2000s/diagram.png)](https://gitdiagram.com/nhantc-skibidi/plaguelibdich2000s?utm_source=readme&utm_medium=picture)

## Changelog
See [CHANGELOG.md](CHANGELOG.md) - VI only.

## License
See [LICENSE.txt](LICENSE.txt).

---

# PlagueLib-Dich2000s (VI)

Một bản mod ngôn ngữ tùy chỉnh **không chính thức** dành cho game **Plague Inc: Evolved**, được thực hiện bởi nhóm Dịch2000s dưới sự trợ giúp của AI. 

> Đây là dự án mod fan-made không thuộc bởi Ndemic Creations.

## Cài đặt

### Yêu cầu
- Plague Inc: Evolved (PC, Steam)
- [BepInEx](https://github.com/BepInEx/BepInEx/releases)

### Các bước
1. **Cài đặt BepInEx**
   - Tải BepInEx từ [trang phát hành](https://github.com/BepInEx/BepInEx/releases).
   - Giải nén tệp vào bên trong thư mục game Plague Inc, cùng với `PlagueIncEvolved.exe`.
   - Chạy game lần đầu, và đóng lại. BepInEx sẽ tạo ra các thư mục (`BepInEx/plugins`, `BepInEx/config`, ...).
2. **Cài đặt mod**
   - Tải mod mới nhất từ [trang phát hành](https://github.com/nhantc-skibidi/PlagueLibDich2000s/releases).
   - Sao chép nó vào `[thư mục Plague Inc]/BepInEx/plugins`.
3. **Chạy game** và chọn ngôn ngữ tùy chỉnh có trong cài đặt ngôn ngữ.

Xem thông tin hướng dẫn chi tiết hơn tại tệp [INSTALL_vi.md](INSTALL_vi.md) - Dành cho bạn và [SETUP_vi.md](SETUP_vi.md) - Dành cho dev.

### Gỡ cài đặt
Xóa mod trong `[thư mục Plague Inc]/BepInEx/plugins`. Và game sẽ trở về với ngôn ngữ cũ.

## Chuẩn đoán & khắc phục sự cố
- **Game chạy nhưng mod không tải:** Kiểm tra BepInEx đã được cài đặt cùng thư mục với game có file `.exe`, và xem trong `BepInEx/LogOutput.log` để tìm lỗi.
- **Không thấy ngôn ngữ tùy chỉnh:** Hãy chắc chắn rằng mod đã nằm bên trong thư mục `BepInEx/plugins`, chứ không phải là các thư mục lồng nhau khác.
- **Vẫn không biết làm sao?** Báo cáo [vấn đề](https://github.com/nhantc-skibidi/PlagueLibDich2000s/issues) và tải kèm tệp `LogOutput.log`.

## Tạo ngôn ngữ riêng
Muốn có thêm ngôn ngữ khác? Xem [CREATE_LANGUAGE_vi.md](CREATE_LANGUAGE_vi.md).

## Cấu trúc repo
[![Sơ đồ repo của nhantc-skibidi/plaguelibdich2000s](https://gitdiagram.com/nhantc-skibidi/plaguelibdich2000s/diagram.png)](https://gitdiagram.com/nhantc-skibidi/plaguelibdich2000s?utm_source=readme&utm_medium=picture)

## Nhật ký thay đổi
Xem [CHANGELOG.md](CHANGELOG.md).

## Giấy phép
Xem [LICENSE.txt](LICENSE.txt) - chỉ có EN.