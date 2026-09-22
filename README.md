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
- **Instalasi EA/Indicator massal** — pilih satu file `.ex5`, tentukan jenis Expert Advisor atau Indicator, lalu pasang ke terminal terpilih atau seluruh terminal terdaftar dengan hasil per terminal.
- **Scheduler Algo Trading berulang** — tambahkan beberapa aturan ON/OFF berdasarkan waktu lokal VPS, hari aktif, dan satu atau beberapa terminal; state terbaru direkonsiliasi setelah aplikasi dibuka kembali.

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
| Jadwal Algo Trading & riwayat eksekusi | `%LOCALAPPDATA%\Mt5Manager\algo-schedules.json` |

## Memasang Bridge (agar akun & status Algo terbaca)

Tanpa bridge, MT5 Manager tetap bisa start/stop terminal, tetapi status akun dan kontrol Algo Trading tidak tersedia.

1. Pilih terminal yang data directory-nya sudah terverifikasi.
2. Klik **Install bridge**. MT5 Manager menyalin `Mt5ManagerBridge.mq5` terbaru ke `<DataFolder MT5>\MQL5\Experts\` dan mengompilasinya dengan `MetaEditor64.exe` milik terminal tersebut.
3. Di MT5, seret EA `Mt5ManagerBridge` ke chart mana saja. Satu chart cukup; EA tidak melakukan trading, hanya menulis snapshot runtime kira-kira setiap 3 detik ke folder Common.

Jika `MetaEditor64.exe` tidak tersedia di samping `terminal64.exe`, source tetap terpasang dan aplikasi meminta Anda membukanya di MetaEditor lalu menekan **F7**. Tombol yang sama dapat dipakai lagi untuk memperbarui bridge saat versi bawaan aplikasi berubah. Instalasi ditolak untuk data directory yang belum terverifikasi.

Snapshot ditulis ke `%APPDATA%\MetaQuotes\Terminal\Common\Files\Mt5Manager\` dan dibaca oleh MT5 Manager untuk seluruh terminal.

## Memasang EA atau Indicator ke Terminal

1. Klik **Install EA / Indicator** pada header aplikasi.
2. Pilih file hasil compile dengan ekstensi `.ex5`.
3. Pilih jenis target: **Expert Advisor** (`MQL5\Experts`) atau **Indicator** (`MQL5\Indicators`).
4. Pilih cakupan terminal terpilih atau seluruh terminal terdaftar, lalu jalankan instalasi.

File dengan nama yang sama pada folder target akan diganti. Setiap terminal menampilkan hasilnya sendiri; terminal dengan data directory yang belum terverifikasi ditolak tanpa penulisan file. Setelah instalasi, refresh Navigator MT5 atau restart terminal bila item belum terlihat.

## Menjadwalkan Algo Trading

1. Klik **Scheduler** pada header aplikasi.
2. Buat nama aturan, masukkan waktu dalam format `HH:mm`, lalu pilih **Turn Algo ON** atau **Turn Algo OFF**.
3. Pilih hari aktif serta satu atau beberapa terminal. Gunakan **Select all** bila aturan berlaku untuk semua terminal terdaftar.
4. Aktifkan **Schedule enabled**, lalu klik **Save schedule**.
5. Tambahkan aturan lain untuk setiap perubahan state yang dibutuhkan, misalnya Asia ON, London OFF, US OFF, dan tengah malam ON.

Waktu mengikuti timezone Windows pada VPS yang ditampilkan di dialog. Aturan baru atau hasil edit mulai pada occurrence berikutnya sehingga penyusunan jadwal tidak mengubah terminal secara mendadak. Setelah aturan pernah berlaku, MT5 Manager merekonsiliasi occurrence terbaru saat aplikasi dibuka kembali dan tidak menjalankan ulang occurrence yang sudah berhasil.

Scheduler hanya berjalan selama proses MT5 Manager aktif. Jika eksekusi gagal karena terminal, window MT5, atau bridge belum tersedia, scheduler mencoba sekali lagi setelah 5 menit. Kegagalan kedua dicatat di tab **Execution history** dan dikirim ke chat Telegram yang sudah dikonfigurasi; bila Telegram tidak aktif, status notifikasi tetap tercatat secara lokal.

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
- Instalasi `.ex5` hanya menulis ke `MQL5\Experts` atau `MQL5\Indicators` pada data directory yang sudah terverifikasi; hasil gagal pada satu terminal tidak disembunyikan oleh keberhasilan terminal lain.
- Dua aturan aktif tidak boleh menargetkan terminal, hari, dan waktu lokal yang sama; konflik ditolak saat penyimpanan.

## Build dari Source

```bash
git clone <repo-url>
cd MT5-Manager
dotnet publish src/Mt5Manager.Wpf -p:PublishProfile=win-x64          # self-contained, single-file
dotnet publish src/Mt5Manager.Wpf -p:PublishProfile=win-x64-framework-dependent
dotnet test Mt5Manager.sln --configuration Release                    # 407 test
```

Output publish ada di `artifacts/publish/Mt5Manager.Wpf-win-x64/` dan `artifacts/publish/Mt5Manager.Wpf-win-x64-framework-dependent/`, sesuai nama arsip release.

## Batasan

- Hanya Windows (proses, DPAPI, dan WPF spesifik Windows).
- Kontrol Algo Trading meniru Ctrl+E global terminal; tidak ada kontrol per-chart/per-EA.
- Status akun bergantung pada bridge EA terpasang di terminal.
- Scheduler berjalan di dalam proses aplikasi; menutup MT5 Manager menghentikan eksekusi jadwal sampai aplikasi dibuka kembali.
