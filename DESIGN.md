# MT5 Manager Design System

## Design Read

Utility Windows modern untuk operator MT5 lokal. Kepadatan seimbang. ENERGY 1 / RHYTHM 2 / MOTION 1.

## Theme

Light-first karena utility ini digunakan bersama aplikasi desktop lain dan harus tetap terbaca di lingkungan kerja terang. Gunakan permukaan datar berlapis, bukan glass atau glow.

## Palette

- Canvas: `#F5F6F8`, area kerja netral agar panel putih tetap terbaca.
- Surface: `#FFFFFF`, konten dan dialog.
- Subtle surface: `#F0F2F5`, toolbar, selected-neutral, dan grup detail.
- Primary ink: `#18202C`; secondary ink: `#4D5A6C`; muted ink: `#667386`.
- Accent: `#0F62FE`, hanya untuk tindakan utama, fokus, dan pilihan aktif.
- Success: `#147D64`; warning: `#8A5B00`; danger: `#B42318`.
- Borders: `#D5DAE2`; stronger divider: `#BCC4D0`.

## Typography

Segoe UI mengikuti lingkungan Windows dan mengurangi beban belajar. Skala tetap: 28 untuk judul layar, 20 untuk judul objek, 16 untuk judul bagian, 14 untuk body/control, dan 12 untuk metadata. Gunakan SemiBold hanya untuk hierarchy dan tindakan.

## Layout

Main window memakai header kompak, command bar datar, dan split workspace. Daftar terminal adalah navigator operasional. Detail terminal mengurutkan health summary, tindakan sesi, kontrol Algo/bridge, detail teknis, penyimpanan, lalu log. Breakpoint ditentukan oleh `MinWidth=900`; panel detail menggulir tanpa overflow horizontal.

## Components

- Buttons: tinggi minimum 40, radius 6, transisi state melalui warna dan opacity. Primary hanya satu per action group.
- Cards: radius 8 sampai 10; gunakan border atau shadow kecil, bukan keduanya. Kartu hanya untuk batas workspace utama.
- Panels: permukaan subtle tanpa shadow untuk pengelompokan informasi.
- Status: selalu teks plus bentuk/warna. Success, warning, danger, dan neutral memiliki style terpisah.
- Inputs: tinggi minimum 40, border kuat saat fokus, label selalu terlihat.
- Empty/loading/error: setiap data surface menjelaskan kondisi dan tindakan berikutnya.

## Motion

MOTION 1. Tidak ada animasi dekoratif atau entrance choreography. Hover, pressed, selection, dan progress cukup. Durasi state visual 150 sampai 200 ms jika implementasi platform mendukungnya tanpa kompleksitas.

## Decision Reasons

- Light-first: utility dipakai lama dan berdampingan dengan aplikasi Windows di lingkungan terang.
- Restrained blue accent: biru membedakan aksi dan fokus tanpa menyerupai indikator profit/loss.
- Split workspace: pemilihan terminal dan konteks detail harus tetap terlihat bersamaan.
- Flat command bar: kontrol global tidak boleh terlihat seperti satu kartu data.
- Semantic status rhythm: pengguna memindai masalah lebih cepat melalui teks, bentuk, dan warna yang konsisten.
- Minimal motion: perubahan state perlu terasa responsif, tetapi operasi terminal tidak mendapat manfaat dari animasi dekoratif.
