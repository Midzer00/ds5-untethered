# Untethered — yol haritası

Amaç: Bluetooth ile bağlı DualSense / DualSense Edge'i oyunlara kablolu gibi göstermek (oyunun kendi haptiği,
adaptif tetikleri, ışıkları ve girdileri), sade bir arayüz ve adım adım ilk kurulumla. Tüm kod bu projede yazılır;
yalnızca herkese açık protokol bilgileri ve kendi ölçümlerimiz kullanılır.

## Faz 1 — Çekirdek (bitti)
- [x] Bluetooth/USB rapor biçimleri, CRC, haptik paketi (0x32), tetik efektleri, girdi ayrıştırma — 16 test
- [x] Windows HID katmanı (bulma, okuma, yazma, feature) — donanımda 734 rapor/s okundu
- [x] Bluetooth oturumu: tam rapor moduna geçiş, girdi okuma, 0x31 çıkışı, 10,67 ms'lik haptik akışı — testli
- [x] Adaptör testi: 5 sn boşta + 5 sn haptik altında ölçüm; Uygun / Sınırda / Uygun değil ve sebepleri — testli
      (arayüzde Ayarlar'da ve ilk kurulumda bir düğme olacak)
- [x] Gerçek kumandada denendi (2026-11-09): adaptör testi "Uygun" — 748/661 Hz, p99 3,5/4,5 ms, kayıp 0, haptik 93,8/sn

## Faz 2 — Sanal kablolu DualSense
- [x] Kumandanın kendi USB tanımlayıcıları ve feature raporları okundu (2026-11-09, `Kopru.Probe capture`): cihaz 18 B,
      yapılandırma 227 B, HID rapor tanımlayıcısı 289 B, okunabilen 12 feature raporu; `captures/` git dışında (MAC adresi içerir).
      Okunamayan 10 rapor (0x08, 0x0A, 0x0C, 0x21, 0x80, 0x82, 0x84, 0xA0, 0xF0, 0xF4) yalnızca yazılan raporlar.
- [x] USB/IP sunucusu (Linux çekirdeği USB/IP protokol belgesine göre): cihaz listesi, içe aktarma, URB gönderme/iptal — testli
- [x] Sanal DualSense: tanımlayıcılar, HID ve ses sınıfı istekleri, girdi raporları, oyun çıktısı, 1 ms çerçeve saatli ses — testli
- [x] usbip-win2'nin `usbip.exe` aracıyla takma/çıkarma — donanımda (2026-11-09): Windows kablolu DualSense olarak tanıdı,
      girdi uygulamaya ulaştı (L2 = 123), oyun sesi geldi (haptik kanalı tepe 16385 = gönderilen 0,5 seviye, hoparlör kanalı sessiz)
- [x] Girdi aktarımı (Bluetooth 0x31 → USB 0x01) ve oyun çıktısı (USB 0x02 → Bluetooth 0x31) — testli, donanımda denenmedi (Kopru.Probe bridge)

- [x] Köprü donanımda (2026-11-09, 60 sn): girdi 750 Hz aktarıldı, oyun simülatörü ek gecikme p50 0,53 / p99 3,3 ms, kayıp 0,
      L2/R2 255'e ulaştı; oyun çıktısı (tetik/ışık) iletildi; oyun sesi 2,96 MB → 742 haptik raporu. His kullanıcıdan bekleniyor.

## Faz 3 — Oyun haptiği
- [ ] İzokron ses akışı (1 ms çerçeve saati), 4 kanaldan 3–4. kanalları ayırma
- [x] 48 kHz → 3 kHz dönüşüm, alçak geçiren filtre, "Oyun haptiği" seviyesi (varsayılan %150) — testli, donanımda denenmedi
- [x] Desteklenmeyen oyunlar için titreşim → haptik dönüşümü ("Titreşim" seviyesi) — testli, donanımda denenmedi

## Faz 4 — Gizleme ve kurulum
- [x] HidHide CLI ile gerçek kumandayı gizleme ve uygulamaya izin verme (Hiding/, HidingSession) — kod bitti, testli, donanımda denenmedi
- [x] İlk kurulum kontrolleri ve sihirbaz modeli (Setup/: sürücü kontrolü, resmi indirme sayfaları, eşleme adımları, bitiş) — testli; arayüzü henüz yok
- [x] BridgeHost servisi (Bridge/): köprüyü başlat/durdur, durum, bağlantı kopunca yeniden bağlanma, pil uyarısı;
      Kopru.Probe bridge artık bunu kullanıyor — testli, donanımda denenmedi
- [x] Hareketsizlikte kapanma (Ayarlar'da "Kullanılmadığında kumandayı kapat") — testli, donanımda denenmedi

## Faz 5 — Arayüz
- [x] Avalonia arayüz iskeleti (src/Kopru.App): 5 menü (Genel bakış, Haptik ve titreşim, Tetikler, Işık, Ayarlar), cihaz kartı,
      durum kartları, hızlı testler, hızlı ayarlar; TR/EN ekran görüntüleri docs/screens,
      gerçek servislere bağlı (görüntüleri yenilemek: KOPRU_RENDER_SCREENS=1 dotnet test)
- [x] Kendi DualSense ve DualSense Edge çizimlerimiz
- [x] Tema: Semi.Avalonia seçildi (SukiUI denemesi bırakıldı); sade grafit görünüm — parlama/gradyan/gölge yok, tek mavi vurgu,
      küçük köşe yarıçapı; uygulama adı her dilde "DS5 Untethered"
- [ ] Tek dosyalık kurulum paketi

## Tamamlanan arayüz bağlantıları
- [x] Arayüzü BridgeHost, SetupWizard ve HidingSession'a bağlamak (RealControllerService; demo modu kaldırıldı, sahte servis
      yalnızca testlerde) — testli, donanımda denenmedi
- [x] Ayarların kalıcı saklanması (%LOCALAPPDATA%\Untethered\settings.json, gecikmeli kayıt) — testli
- [x] Windows ile başlatma (HKCU Run "Untethered", yalnızca kullanıcı değiştirince) — testli (sahte kayıt defteri)
- [x] Kurulum sihirbazı sayfası (ilk açılışta eksik adım varsa kendiliğinden açılır) ve Ayarlar'da adaptör testi
- [x] Çubuk/tetik ayarı, kumandayı kapatma, ölçülen gecikme, tanılama dışa aktarımı, HidHide erken kaydı ve Steam Input uyarısı arayüzde — testli (sahtelerle), donanımda denenmedi

## Geliştirme listesi (tasarım bittikten sonra)

### A. Önce yapılacaklar
- [ ] Donanım testi: köprü, gizleme/geri alma, yeniden bağlanma, pil, adaptör testi
- [ ] Oyunda uzun oturum (1–2 saat haptik kararlılığı)
- [ ] Titreşim→haptik his ayarı (80/160 Hz, boşta eşiği)
- [x] HidHide: başka araç gizlemişse köprü başlamadan da kumandayı görmek (açılışta erken kayıt, çıkışta geri alma)
- [x] Gerçek gecikme ölçümü (Genel bakış: bağlantı gecikmesi ortanca / en kötü %1)
- [ ] Tetik gücü oyunun efektlerine de uygulansın
- [x] Boşta kumandayı kapatma (BT bağlantısını kesme) ve Genel bakış'ta "Kumandayı kapat"
- [x] Tanılama dışa aktarımı (Ayarlar → zip; adaptör testi sonucu dahil)
- [ ] Sanal kumanda kimliğini programa gömme

### B. PS5 deneyimi için özellikler (öncelik sırasıyla)
1. [ ] Oyun sesi → kumanda hoparlörü/kulaklık; ses seviyesi; kulaklık takılınca otomatik geçiş (Orta)
2. [ ] Mikrofon oyuna; sessize alma tuşu ve ışığı (Büyük)
3. [ ] Oyuna göre otomatik profiller + kumanda kısayolları (PS+D-pad profil, PS+üçgen kapat) (Orta)
4. [x] Stick deadzone, drift kalibrasyonu, tepki eğrisi (Küçük) — "Analog ve tetikler" sayfası, canlı önizleme
5. [x] Tetik deadzone ve basma aralığı (Küçük)
6. [ ] PS5 tarzı yoğunluk ayarları: haptik, tetik efekti (kapalı dahil), ışık/oyuncu ışığı parlaklığı (Küçük)
7. [x] Steam Input açıksa uyarı (Küçük–Orta) — Genel bakış ve Desteklenen oyunlar
8. [ ] Pil ve güç: ~~tepsi simgesi, düşük pil bildirimi~~ (yapıldı), ışık çubuğunda pil rengi (Küçük)
   - [x] Bildirimler: uygulamaya özel toast kartları (sağ alt, en fazla 3, 200 ms kayma/solma, üzerindeyken durur, tıklayınca ilgili sayfa), bağlantı / pil %20-%10 / şarj bitti / oyun ve modu / Steam Input / boşta kapatma / köprü hatası / HidHide eksik; tür başına anahtar, tekrar ve hız sınırı, "Oyun sırasında bildirim gösterme"
   - [x] Tepsi simgesi (Aç, köprüyü başlat/durdur, kumandayı kapat, bildirimler aç/kapat, çıkış), kapatınca tepside çalışma, "Windows ile başlat" tepside açılır (--minimized)
   - [ ] Donanımda deneme: toast konumu çoklu ekran/DPI, tam ekran oyunda görünüm, tepsi menüsü
9. [ ] Tuş atama (DualSense raporunun içinde) (Orta)
10. [ ] Gyro → sağ çubuk (her zaman / L2 basılıyken) (Orta)

### C. Sonraya
- [ ] Touchpad/D-pad ile fare, gyro ile fare (masaüstü modu)
- [ ] PC sesini haptik olarak hissetme
- [ ] Işık efektleri (nefes, gökkuşağı)
- [ ] Özel tetik efekti editörü; oyun desteklemiyorsa hazır efektler
- [ ] DSX mod uyumluluğu
- [ ] DualSense Edge arka tuşlar ve profiller
- [ ] Birden fazla kumanda
- [ ] Xbox/DS4 taklidi (yalnızca oyun bazında, haptik/tetik/hoparlör kaybolur)

### D. Yayın
- [ ] Tek dosyalık kurulum programı (sürücü kontrolü dahil)
- [ ] Uygulama simgesi ve nihai logo
- [ ] Güncelleme kontrolü
- [ ] README ve ekran görüntüleri
