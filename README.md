# MT5 Manager

Aplikasi desktop Windows untuk mengelola banyak terminal MetaTrader 5 dalam satu tempat: pantau status, kontrol sesi, kelola penyimpanan data, dan kendalikan Algo Trading dari jarak jauh lewat Telegram.

## Fitur

- **Manajemen multi-terminal** — pemindaian otomatis (folder instalasi, shortcut, proses berjalan) dan pendaftaran manual; pencarian berdasarkan nama atau path.
- **Start / Stop / Restart terminal** — penghentian elegan terlebih dahulu (tutup jendela utama), dengan penegakan identitas proses: operasi destruktif hanya berjalan bila direktori data terminal terverifikasi dan identitas proses cocok persis.
- **Kontrol Global Algo Trading** — setara Ctrl+E di MT5. Catatan penting: ini **global per terminal** — memengaruhi *setiap EA* di terminal tersebut, bukan per-chart.
- **Informasi akun real-time** — login, nama akun, server, status Algo Trading, dan status koneksi, via Expert Advisor bridge (`Mt5ManagerBridge.mq5`).
- **Inspeksi & pembersihan penyimpanan** — ringkasan ukuran per kategori (logs, cache, tester, dsb.) dan pembersihan selektif.
- **Log operasi** — riwayat audit per terminal (start/stop/restart/algo/cleanup).
- **Bot Telegram** — dashboard status (`/start`, `/menu`, `/status`), aktifkan/nonaktifkan Algo Trading untuk semua atau terminal pilihan dengan konfirmasi dua langkah, plus tampilan IP publik VPS.

## Persyaratan

- Windows 10/11 x64
- Build *self-contained* (rekomendasi) — tidak perlu menginstal .NET
- Build *framework-dependent* — butuh [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)

## Unduh & Pasang

1. Buka halaman [**Releases**](../../releases).
2. Unduh `Mt5Manager.Wpf-win-x64.zip` (self-contained, ~70 MB) atau `Mt5Manager.Wpf-win-x64-framework-dependent.zip` (lebih kecil, butuh .NET 8).
3. Ekstrak ke folder mana saja dan jalankan `Mt5Manager.Wpf.exe`.
4. Saat pertama menjalankan, klik **Scan terminals** — terminal MT5 yang terinstal akan dideteksi otomatis.

Tidak ada instalasi; aplikasi berjalan portabel. Data aplikasi tersimpan di:

| Data | Lokasi |
|---|---|
| Registry terminal | `%PROGRAMDATA%\Mt5Manager\terminals.json` |
| Log audit | `%PROGRAMDATA%\Mt5Manager\audit.jsonl` |
| Pengaturan bot Telegram | `%LOCALAPPDATA%\Mt5Manager\telegram.json` (token terenkripsi DPAPI per pengguna Windows) |

## Memasang Bridge (agar akun & status Algo terbaca)

Tanpa bridge, MT5 Manager tetap bisa start/stop terminal, tetapi status akun dan kontrol Algo Trading tidak tersedia.

1. Salin `Mt5ManagerBridge.mq5` (ada di release dan di folder aplikasi) ke `<DataFolder MT5>\MQL5\Experts\` — dari MT5: **File → Open Data Folder**, lalu navigasikan ke `MQL5\Experts`.
2. Buka MetaEditor (F4), buka file tersebut, lalu **Compile** (F7).
3. Di MT5, seret EA `Mt5ManagerBridge` ke chart mana saja. Satu chart cukup; EA tidak melakukan trading, hanya menulis snapshot runtime tiap 3 detik ke folder Common.

Snapshot ditulis ke `%APPDATA%\MetaQuotes\Terminal\Common\Files\Mt5Manager\` dan dibaca oleh MT5 Manager untuk seluruh terminal.

## Menghubungkan Bot Telegram

1. Di Telegram, buat bot lewat [@BotFather](https://t.me/BotFather) — `/newbot`, lalu salin token.
2. Kirim pesan apa pun ke bot Anda (misal `/start`), lalu buka `https://api.telegram.org/bot<TOKEN>/getUpdates` untuk melihat `chat.id` Anda.
3. Di MT5 Manager, klik **Telegram Bot**, masukkan token dan Chat ID, lalu aktifkan.
4. Kirim `/start` ke bot — dashboard berisi jumlah terminal aktif, status koneksi, dan IP publik VPS akan muncul.

Perintah yang tersedia: `/start`, `/menu`, `/status` (dashboard), `/on_all` / `/off_all` (semua terminal), `/on` / `/off` (pilih terminal), dengan tombol konfirmasi untuk setiap operasi.

**Catatan keamanan:**

- Hanya Chat ID terdaftar yang dapat mengendalikan bot.
- Telegram hanya mengizinkan satu konsumen polling per token bot — jika Anda menjalankan MT5 Manager di dua mesin dengan token yang sama, yang kalah berhenti otomatis dengan status *Conflict* (HTTP 409), bukan saling berebut.
- Kontrol Algo Trading via Telegram juga bersifat **Global** per terminal — memengaruhi setiap EA di terminal tersebut.

## Keselamatan Operasi

- **Stop/Restart/Cleanup/Force gagal tertutup (fail-closed)** bila identitas proses tidak dapat diverifikasi persis atau direktori data terminal kosong/tidak terbaca — mencegah menghentikan atau membersihkan proses terminal yang salah.
- Terminal yang didaftarkan manual tanpa verifikasi direktori data tidak dapat di-stop, restart, atau dibersihkan.
- Setiap operasi destruktif meminta konfirmasi (WPF maupun Telegram).

## Build dari Source

```bash
git clone <repo-url>
cd MT5-Manager
dotnet publish src/Mt5Manager.Wpf -p:PublishProfile=win-x64          # self-contained, single-file
dotnet publish src/Mt5Manager.Wpf -p:PublishProfile=win-x64-framework-dependent
dotnet test Mt5Manager.sln --configuration Release                    # 297 test
```

Output ada di `artifacts/publish/`.

## Batasan

- Hanya Windows (proses, DPAPI, dan WPF spesifik Windows).
- Kontrol Algo Trading meniru Ctrl+E global terminal; tidak ada kontrol per-chart/per-EA.
- Status akun bergantung pada bridge EA terpasang di terminal.
