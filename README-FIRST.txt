AR BLOOM — Unity 6 / Android / ARCore
=====================================

KONSEP YANG SUDAH JADI
- Scan lantai/meja horizontal dengan ARCore.
- Reticle kuning muncul di titik tengah kamera.
- TAHAN BAGIAN BAWAH layar.
- Gerakkan / putar HP sehingga reticle menyapu lantai.
- Bunga tumbuh di jalur reticle.
- Saat sapuan makin jauh dari titik awal, area bagian dalam otomatis ikut diisi.
  Jadi hasilnya garden/bloom penuh, BUKAN lingkaran kosong seperti donat.
- RESET tersedia di kanan atas.
- Maks default 450 bunga, low-poly dan 1 renderer per bunga agar lebih aman di Android mid/low.

CARA RUN PALING CEPAT
1. Extract ZIP ini.
2. Unity Hub > Open > pilih folder ARBloom_Unity6.
3. Buka memakai Unity 6 (6000.0 atau lebih baru).
4. Tunggu Package Manager/import selesai. Pertama kali bisa beberapa menit.
5. Di HP Android:
   - aktifkan Developer options + USB debugging
   - sambungkan USB
   - pastikan "Google Play Services for AR / Layanan Google Play untuk AR" terpasang
6. Di Unity klik menu:
       AR Bloom > 2. Build & Run Android
   Script otomatis build APK development (IL2CPP ARM64) dan AutoRun ke HP.

Kalau menu build bilang Android module belum ada:
Unity Hub > Installs > gear pada Unity 6 > Add modules:
- Android Build Support
- Android SDK & NDK Tools
- OpenJDK

KALAU ARCORE LOADER BELUM AKTIF
Project mencoba mengaktifkannya otomatis. Kalau Console memperingatkan gagal:
Edit > Project Settings > XR Plug-in Management > Android > centang ARCore.
Setelah itu pilih lagi AR Bloom > Build & Run Android.

DEVICE TEST
Pada device yang Play Store mengizinkan "Layanan Google Play untuk AR" terpasang, app tetap melakukan runtime availability check.
Kalau ARSession melaporkan Unsupported, UI app akan menampilkan pesan unsupported.

KONTROL
- Scan: gerakkan HP perlahan sambil arahkan kamera ke lantai.
- Bloom: tahan jari di bagian BAWAH layar supaya jari tidak menutupi reticle.
- Gerakkan HP secara fisik. Reticle di tengah kamera adalah "ujung tangan/kuas".
- Makin jauh reticle dari titik awal, bloom mengisi area sampai radius default 2.6 meter.
- RESET: tombol kanan atas.

TUNING
Pilih GameObject "AR Bloom App" di scene dan ubah:
- Max Flowers
- Flowers Per Square Meter
- Fill Step
- Brush Radius
- Minimum Flower Spacing
- Growth Speed
- Max Bloom Radius

CATATAN
Project sengaja tidak menyertakan asset bunga eksternal atau musik. Bunganya dibuat procedural low-poly saat runtime, jadi project langsung bisa dicoba tanpa import asset tambahan.
