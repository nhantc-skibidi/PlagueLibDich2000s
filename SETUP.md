# Setup build — PlagueLibDich2000s

> **File này dành cho DEV** — người sẽ mở source code, sửa `.cs` và tự biên dịch
> lại DLL. Nếu bạn chỉ muốn **cài mod để chơi**, đọc `INSTALL.md` thay vì file này —
> người chơi không cần .NET SDK, không cần cấu hình gì trong repo cả.

Project build được trên nhiều máy khác nhau mà **không cần sửa file trong repo**,
vì đường dẫn tới BepInEx và tới game được tách ra khỏi `.csproj` / Git.

## 1. Cấu hình đường dẫn máy bạn (chỉ làm 1 lần)

**Cách A — file cấu hình riêng (khuyên dùng):**

1. Copy `Directory.Build.props.user.example` → `Directory.Build.props.user`
   (cùng thư mục gốc repo, cạnh `Directory.Build.props`).
2. Mở file vừa copy, sửa 2 đường dẫn:
   - `BepInExDir` → thư mục BepInEx của game (chứa `core\0Harmony.dll`, `core\BepInEx.dll`)
   - `GameManagedDir` → thư mục `Managed` của game (chứa `Assembly-CSharp.dll`, `UnityEngine*.dll`)
3. File `Directory.Build.props.user` đã nằm trong `.gitignore` — không bao giờ bị commit,
   nên mỗi người trong nhóm tự có bản riêng.

**Cách B — biến môi trường (nếu không thích tạo file):**

Windows PowerShell (chạy 1 lần, rồi mở lại terminal/IDE):
```powershell
setx BepInExDir "D:\SteamLibrary\steamapps\common\PlagueInc\BepInEx"
setx GameManagedDir "D:\SteamLibrary\steamapps\common\PlagueInc\PlagueIncEvolved_Data\Managed"
```

## 2. Build

```
dotnet build PlagueLibDich2000s.slnx
```

Nếu chưa cấu hình bước 1, build sẽ báo lỗi rõ ràng ngay từ đầu (thay vì hàng trăm lỗi
CS0246 khó hiểu như trước):
```
error : BepInExDir chưa được cấu hình. Xem SETUP.md...
```

## 3. Cài vào game (tự động)

Sau khi build thành công, `PlagueLibDich2000s.dll` sẽ **tự động được copy** vào:
```
<BepInExDir>\plugins\PlagueLibDich2000s\PlagueLibDich2000s.dll
```

Mở game lên là mod đã có sẵn — không cần copy tay.

Nếu muốn build mà KHÔNG tự cài (ví dụ build trên máy CI không có BepInEx thật):
```
dotnet build /p:SkipInstallToBepInEx=true
```

## 4. Ảnh AboutMod.png

`Resources/AboutMod.png` hiện đang là **file placeholder rỗng** trong bản này (file
gốc bạn upload bị lỗi 0 byte). Thay file `PlagueLibDich2000s/Resources/AboutMod.png`
bằng ảnh avatar thật của bạn (PNG, khuyên dùng vuông, dưới vài trăm KB) rồi build lại
— ảnh sẽ được nhúng thẳng vào DLL (`EmbeddedResource`), không cần copy file .png riêng
khi phát hành mod.

## Cấu trúc repo

```
PlagueLibDich2000s/
├── PlagueLibDich2000s.slnx
├── Directory.Build.props            (commit — đọc cấu hình máy)
├── Directory.Build.props.user.example (commit — mẫu)
├── Directory.Build.props.user       (KHÔNG commit — bạn tự tạo)
├── .gitignore
├── SETUP.md                         (dành cho dev build)
├── INSTALL.md                       (dành cho người chơi cài mod)
└── PlagueLibDich2000s/
    ├── PlagueLibDich2000s.csproj
    ├── Main.cs
    ├── AboutModScreen.cs
    ├── LangImages.cs
    ├── FontRestore.cs
    └── Resources/
        └── AboutMod.png
```
