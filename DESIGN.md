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

## Navigation

Menu bar global menyediakan **Terminal**, **Tools**, **Settings**, dan **Help**. **Terminal** memuat **Scan terminals** dan **Register terminal**; **Tools** memuat **Scheduler** dan **Install EA / Indicator**; **Settings** memuat **Telegram**; **Help** memilih tab **Getting started**.
- Workspace tabs are **Terminals**, **Batch results**, and **Getting started**. Switching tabs changes the visible surface only: terminal selection, batch targets, and dialog state remain intact.
- **Terminals** is the default operational view. **Batch results** reports per-target outcomes after a session batch. **Getting started** remains available as a collapsible orientation surface for first-run guidance.

## Components

- Buttons: tinggi minimum 40, radius 6, transisi state melalui warna dan opacity. Primary hanya satu per action group.
- Cards: radius 8 sampai 10; gunakan border atau shadow kecil, bukan keduanya. Kartu hanya untuk batas workspace utama.
- Panels: permukaan subtle tanpa shadow untuk pengelompokan informasi.

## Novice UX

- Keep the header and action controls wrapping at the minimum window width; results live in a bounded scroll region rather than expanding the window.
- Persist a collapsible getting-started explanation. List rows remain scannable (name, state, account); detailed readiness guidance belongs in the selected-terminal detail view.
- Preserve visible keyboard focus in lists and support Escape to cancel registration. Bulk confirmation names every target and describes EA impact.
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

## Usability density guidance

- Keep the fleet toolbar focused on search, state filtering, and selection. Global scan, registration, Telegram, Scheduler, and package installation actions belong in the menu bar and retain their existing handlers.
- Terminals remains the initial tab. Batch results and Getting started have their own tabs rather than expanding the terminal workspace; secondary terminal detail sections start collapsed so name, state, readiness, account, and session controls remain the first scan targets.
- Keep **Batch operations** permanently beside the state filter so checkbox changes never add a row or shift the workspace vertically. Enable its Start/Stop/Restart/Clear selection dropdown only with checked targets and no scan or batch in progress. Names select details only; separate checkboxes change batch targets. Preserve hidden checked targets, keyboard access, fixed confirmation snapshots, and destructive-operation safety confirmations.
- Minimal motion: perubahan state perlu terasa responsif, tetapi operasi terminal tidak mendapat manfaat dari animasi dekoratif.
