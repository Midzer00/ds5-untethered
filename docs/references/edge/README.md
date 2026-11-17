# DualSense Edge tanımlayıcıları (geliştirme notu, uygulamaya girmez)

Elimizde Edge olmadığı için herkese açık paylaşılmış dökümler kullanılıyor:
- `usb_descriptor.txt`, `bt_descriptor.txt`: linux-input listesinde (Mayıs 2026) paylaşılan
  `/sys/kernel/debug/hid/` çıktıları — https://ratatoskr.run/linux-input/2026/05/17034585
  İlk satır ham HID rapor tanımlayıcısıdır.

Karşılaştırma (bizim standart DualSense okumamızla, 2026-11-09):
- Edge USB HID tanımlayıcısı 405 B, standart 289 B; ilk 109 bayt aynı.
- Girdi ve çıktı rapor kimlikleri aynı (0x01 / 0x02). Edge'de düğme sayısı 15 (standart 14): Fn ve arka tuşlar.
- 22 feature raporu ortak; Edge'de fazladan 19 tane var (0x60–0x65, 0x68, 0x70–0x7B): kumandanın içindeki profiller.
- USB cihaz/yapılandırma tanımlayıcısının dökümü yok. Sanal kumanda Edge'i oyunlara standart DualSense olarak gösterecek,
  bu yüzden Edge'in USB kimliği gerekmiyor; Edge yalnızca Bluetooth tarafında (girdi ayrıştırma, fazla tuşlar) ele alınıyor.
