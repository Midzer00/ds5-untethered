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
- [x] Tetik gücü oyunun efektlerine de uygulansın — tüm bilinen modlar (0x01/0x02/0x06 basit, 0x21 direnç/eğim/çoklu, 0x22 yay, 0x25 silah, 0x26 titreşim/çoklu, 0x27 makine) kuvvet/genlikle ölçeklenir; konum, bölge maskesi, frekans dokunulmaz; %100 birebir, %0 kapalı, sıfır olmayan kuvvet en az 1; bilinmeyen mod değişmeden geçer ve sayılır; ayar değişince oyunun son efekti hemen yeniden gönderilir — testli, donanımda denenmedi
- [ ] Donanımda deneme: tetik gücü (Hafif/Normal/Güçlü) destekli bir oyunun silah/direnç/titreşim efektlerinde hissedilir mi; 0x22 yay, 0x27 makine ve 0x01/0x02/0x06 basit mod bayt düzenleri doğru mu; Hafif'te hafif efektler kaybolmuyor mu
- [x] Boşta kumandayı kapatma (BT bağlantısını kesme) ve Genel bakış'ta "Kumandayı kapat"
- [x] Tanılama dışa aktarımı (Ayarlar → zip; adaptör testi sonucu dahil)
- [ ] Sanal kumanda kimliğini programa gömme

### B. PS5 deneyimi için özellikler (öncelik sırasıyla)
1. [ ] Oyun sesi → kumanda hoparlörü/kulaklık; ses seviyesi; kulaklık takılınca otomatik geçiş (Orta)
   - [x] Araştırma ve prototip: Bluetooth hoparlör sesi (Audio/, 0x36 raporunda haptikle iç içe, `Kopru.Probe speaker`; docs/research/bt-audio.md) — varsayılan kapalı, testli, donanımda denenmedi
   - [x] Kodlayıcı: Concentus 2.2.2 (yönetilen Opus, BSD-3-Clause; opus.dll gerekmez; 10 ms çerçeve ~0,33 ms), libopus yalnızca KOPRU_OPUS=native ile isteğe bağlı
   - [x] Köprüye bağlandı: oyunun 1–2. kanalları hoparlöre, 3–4. kanallar haptik olarak aynen; 200 ms sessizlikte 0x32'ye dönüş;
         kulaklık girişine otomatik geçiş; Ayarlar → Kumanda Hoparlörü (deneysel anahtar, hoparlör/kulaklık sesi, "Kulaklık takılı"),
         settings.json ve profillerde saklanır — varsayılan kapalı, testli, donanımda denenmedi
   - [ ] Donanım denemesi: `Kopru.Probe speaker`, ardından bir oyunla uygulama içinde
2. [~] Mikrofon oyuna; sessize alma tuşu ve ışığı (Büyük) — sessize alma tuşu (oyuna iletilmez), LED "Mikrofonu İzle", isteğe bağlı Windows mikrofonunu sessize alma: testli. BT mikrofon aktarımı deneysel prototip (Opus 0xD4 düzeni varsayım), donanımda denenmedi; bkz. docs/research/bt-mic.md
3. [x] Oyuna göre otomatik profiller + kumanda kısayolları (PS+D-pad profil, PS+üçgen kapat) (Orta) — Ayarlar'da "Profiller" (yeni, yeniden adlandır, çoğalt, sil, .json içe/dışa aktar; "Varsayılan" hep var, eski ayarlar ona taşındı), Desteklenen oyunlar listesinde oyun başına profil seçici (tanınınca etkinleşir, yoksa Varsayılan), etkin profil Genel bakış ve kenar çubuğunda; PS basılıyken Sol/Sağ profil, Yukarı/Aşağı haptik ±%10, Kare destekli mod, Üçgen 1 sn kapat (oyuncu ışıkları profil numarası + bildirim; kısayolda PS ve tuş oyuna gitmez, tek PS dokunuşu gider), "Kumanda Kısayolları" anahtarı — testli (sahtelerle), donanımda denenmedi
4. [x] Stick deadzone, drift kalibrasyonu, tepki eğrisi (Küçük) — "Analog ve tetikler" sayfası, canlı önizleme
5. [x] Tetik deadzone ve basma aralığı (Küçük)
6. [ ] PS5 tarzı yoğunluk ayarları: haptik, tetik efekti (kapalı dahil), ışık/oyuncu ışığı parlaklığı (Küçük)
7. [x] Steam Input açıksa uyarı (Küçük–Orta) — Genel bakış ve Desteklenen oyunlar
8. [ ] Pil ve güç: ~~tepsi simgesi, düşük pil bildirimi~~ (yapıldı), ışık çubuğunda pil rengi (Küçük)
   - [x] Bildirimler: uygulamaya özel toast kartları (sağ alt, en fazla 3, 200 ms kayma/solma, üzerindeyken durur, tıklayınca ilgili sayfa), bağlantı / pil %20-%10 / şarj bitti / oyun ve modu / Steam Input / boşta kapatma / köprü hatası / HidHide eksik; tür başına anahtar, tekrar ve hız sınırı, "Oyun sırasında bildirim gösterme"
   - [x] Tepsi simgesi (Aç, köprüyü başlat/durdur, kumandayı kapat, bildirimler aç/kapat, çıkış), kapatınca tepside çalışma, "Windows ile başlat" tepside açılır (--minimized)
   - [ ] Donanımda deneme: toast konumu çoklu ekran/DPI, tam ekran oyunda görünüm, tepsi menüsü
   - [x] Işık efektleri (Işık sayfası): Sabit / Nefes / Gökkuşağı / Pil Rengi (yeşil → turuncu → kırmızı, %10 altında yanıp söner) / Oyun Kontrolünde; ~30 Hz, yalnızca değişince gönderilir; destekli oyun ışığı gönderirse oyun kazanır. Oyuncu LED deseni (kapalı / 1–4 / tümü) ve parlaklığı (düşük/orta/yüksek, çıkış bloğu bayt 42), mikrofon LED'i (açık/kapalı) — testli, donanımda denenmedi
   - [x] "DualSense Destekli Mod" anahtarı (Genel bakış köprü kartı, kenar çubuğu kartı, tepsi menüsü): kapalıyken (varsayılan) diğer oyunlar profili, açıkken destekli profil. Rastgele ön plan uygulamaları artık taranmıyor/öğrenilmiyor; yalnızca tanınan oyunlar: listedeki exe, Steam kütüphanesi (libraryfolders.vdf + appmanifest installdir → steamAppId) ya da kullanıcının eklediği oyun ("Oyun Ekle": açık uygulamalardan ya da dosyadan; "Kaldır"). Tanınan oyun açılınca, mod kapalıysa her açılışta bir kez butonlu bildirim (Aç / Şimdi Değil / Her Zaman, 20 sn, odak çalmaz); "Her Zaman" oyunu "Otomatik Aç" olarak işaretler (listeden kaldırılabilir); oyun için açılan mod oyun kapanınca ya da başka tanınan oyun gelince kapanır; elle değiştirmek oyun kapanana dek geçerli. "Tanınan Oyunda Sor" ayarı; eski exe seçimleri taşındı (Destekli → Her Zaman, Desteksiz → Sorma) — testli (sahtelerle), donanımda denenmedi
   - [ ] Donanımda deneme: Steam kütüphanesi eşlemesi, bildirim butonlarının oyun odağını almaması
   - [x] DualSense Modu tek anahtar: açıkken oyunun haptiği, tetik efektleri, ışığı (Oyunun Işığını Kullan, varsayılan açık) ve kumanda hoparlörü birlikte açık (Core `DualSenseMode`, Düşük Güç gibi üst katman; kullanıcının hoparlör anahtarı değişmez, mod kapanınca geri gelir). Mod açıkken hoparlörü elle kapatmak mod yeniden değişene kadar geçerli; Düşük Güç her zaman kazanır; güç değerleri ve mikrofon kullanıcının. Tek değişiklik tek gönderim, köprü yeniden başlamaz. Yeni kurulumda ve anahtarı olmayan eski ayar dosyalarında mod açık; kayıtlı "kapalı" korunur — testli (sahtelerle), donanımda denenmedi
   - [x] "Köprüyü Kendiliğinden Başlat" (Ayarlar → Genel, varsayılan açık): uygulama açılınca kurulum tamamsa (engel yok, kurulum sayfası kapalı) ve kumanda bağlıysa bir kez başlar; elle durdurmak geri alınmaz, kumanda yeniden bağlanınca yeniden kurulur (Core `BridgeAutoStart`) — testli (sahtelerle), donanımda denenmedi
   - [ ] Donanımda deneme: DualSense Modunda oyunun hoparlör sesleri (ör. Astro's Playroom/Returnal), açılışta kendiliğinden başlama, kumanda kapanıp açılınca yeniden başlama
9. [x] Tuş atama (DualSense raporunun içinde) (Orta) — "Analog ve tetikler" sayfasında ikinci sekme: tuş hedefleri, çubuk değiştirme, eksen ters çevirme; profille değişir — testli, donanımda denenmedi
10. [x] Gyro → sağ çubuk (her zaman / L2 basılıyken) (Orta) — her zaman / L2 / R1 (basılı ya da aç-kapa), hassasiyet, ölü bölge, yumuşatma, kalibrasyon; profille değişir — testli, donanımda denenmedi
11. [x] Kumanda testi sayfası (menüde Ayarlar'dan önce): canlı tuşlar, çubuklar, tetikler, dokunmatik yüzey, gyro, pil — testli (sahtelerle), donanımda denenmedi
12. [x] Köprü sağlamlığı: eski sanal kumandaların temizlenmesi (durum dosyası + `usbip port`), uykudan önce düzgün kapatma ve uyanınca yeniden başlatma, bekçi (USB/IP sunucusu yanıt vermez ya da haptik akışı durursa bağlantı yeniden kurulur) — testli, donanımda denenmedi
13. [x] Mikrofon tuşu ayarı (Orta) — "Mikrofon Tuşu" (Ayarlar → Kumanda Mikrofonu ve Hızlı Ayarlar, genel ayar, varsayılan "Tamamen Kapat"): tuş her zaman "Mikrofon" seçicisindeki mikrofona (Windows'un varsayılan kayıt aygıtı) etki eder. Kumanda mikrofonu seçiliyse "Tamamen Kapat" Mikrofon özelliğini açıp kapatır (bugünkü gibi), "Sessize Al" akışı açık tutup oyunlara sessizlik verir (kapalıyken ilk basış açar); başka bir PC mikrofonu seçiliyse tuş o mikrofonu Windows'ta sessize alır / açar (iki modda da, Düşük Güç'te de). Hızlı Ayarlar'daki Mikrofon kutucuğu tuşla aynı işi yapar ve seçili mikrofonun durumunu gösterir (Açık / Sessizde / Kapalı); Ayarlar'daki "Mikrofonu Oyuna Aktar" anahtarı kumanda mikrofonu özelliğinin anahtarı olarak kaldı. "Mikrofonu İzle" ışığı seçili mikrofon açık ve sessizde değilken yanar (PC mikrofonunun Windows'taki sessize alma durumu saniyede bir okunur). Basınca kısa bildirim. "Windows Mikrofonunu da Sessize Al" kaldırıldı (yeni model onu kapsıyor) — testli (sahtelerle), donanımda denenmedi
   - (a) tuşun ne yapacağı seçilebilsin: "Sessize Al" (mikrofon bağlı kalır, yalnızca ses kesilir) ya da "Tamamen Kapat" (mikrofon akışı durur; bugünkü davranış)
   - (b) bu seçim Ayarlar'da ve Hızlı Ayarlar'da olsun
   - (c) mikrofon seçimi: hangi mikrofonun kullanılacağı (kumanda mikrofonu / bilgisayardaki başka bir mikrofon) seçilebilsin
   - Önce: tuş Mikrofon özelliğini açıp kapatırdı, turuncu ışık = açık. Bilinen eksik (duruyor): kendi profili olan bir kumandada tuş genel ayarı değiştirir
14. [x] Kurulum sayfasında "Bilgisayarı Yeniden Başlat" düğmesi (Küçük) — Sürücüler adımında Windows yeniden başlatma bekliyorsa (`SystemCheck.IsRestartPending`; sürücüler bulunmuş olsa da) diğer düğmelerin yanında görünür, yoksa görünmez. Basınca satır kısa bir soruya döner ("Bilgisayar şimdi yeniden başlatılsın mı? Açık programlar kapanır; …", "Yeniden Başlat" / "Vazgeç"). Onaylanınca: ayarlar kaydedilir, "Windows ile Başlat" kapalıysa uygulama HKCU `RunOnce` "Untethered" = "exe yolu" ile bir kez başlatılmak üzere kaydedilir (başka değere dokunulmaz), Windows'tan 10 sn sonra yeniden başlatma istenir (`InitiateShutdownW`, SHUTDOWN_RESTART, zorla kapatma yok; yönetici izni gerekmez) ve uygulama normal yoldan kapanır (sanal kumanda çıkarılır, HidHide geri alınır). Windows reddederse düz bir hata satırı çıkar, `RunOnce` kaydı geri alınır, uygulama açık kalır — testli (sahtelerle), donanımda denenmedi
15. [x] PS tuşu uygulamayı açar (Küçük) — Ayarlar → Kumanda Kısayolları → "PS: Uygulamayı Aç" (varsayılan açık, ana "Kumanda Kısayolları" anahtarına bağlı, settings.json'da): tek kısa PS basışı (en çok 0,6 sn, kombinasyonsuz) oyun önde değilken DS5 Untethered penceresini o anki sayfasıyla öne getirir (arka planda çalışırken de; tepsi simgesiyle aynı `ShowMainWindow` yolu) ve sanal kumandaya gitmez. Oyun önde sayılır: son oyun yoklamasında (saniyede bir) ön plandaki pencere tanınan bir oyunsa ya da ön plandaki pencere tüm ekranı kaplıyorsa — o zaman PS her zamanki gibi oyuna gider. Pencere zaten öndeyse basış hiçbir şey yapmaz ve oyuna da gitmez. Uzun basış ve PS kombinasyonları değişmedi. Yalnızca sanal kumanda çalışırken (giriş ancak o zaman okunuyor) — testli (sahtelerle), donanımda denenmedi
16. [x] Windows varsayılan ses aygıtını DualSense yapmasın (Orta) — "Varsayılan Ses Aygıtımı Koru" (Ayarlar → mikrofon grubu, varsayılan açık, genel ayar): her bağlanmadan (tüm yuvalar, yeniden bağlanma, uyku, adaptör testi, tür değişimi) önce çıkış/kayıt varsayılanları (3 rol) hatırlanır; Windows bunları sanal kumandanınkine çevirirse geliş penceresinde (uç noktalar geldikten 10 sn sonra, en çok 90 sn) geri alınır; pencere dışında kumandayı seçmek kullanıcının tercihi sayılır ve akış başına saklanır, başka aygıt seçince unutulur. Uç nokta USB/IP sanal denetleyicisinin altındaki aygıttan tanınır (Core `DefaultAudioGuard`, `WindowsAudioDefaults`, `IPolicyConfig`; `Kopru.Probe audiodefaults [roundtrip]`) — testli (sahtelerle), donanımda denenmedi
   - Görülen: sanal kumanda bağlanınca Windows onu yeni bir USB ses aygıtı sanıp varsayılan yapıyor; temiz kurulumda "Hoparlör (DualSense Wireless Controller)" varsayılan çıkış oldu, daha önce "Kulaklık Mikrofon Bölümü (DualSense Wireless Controller)" varsayılan kayıt aygıtı olmuştu
   - İstenen: sanal kumanda bağlanmadan önce varsayılan çıkış ve kayıt aygıtlarını (tüm roller: konsol / multimedya / iletişim) hatırla; Windows bunları kumandanınkilerle değiştirdiyse geri al; kullanıcının sonradan bilerek yaptığı seçime asla dokunma; kumandayı varsayılan isteyenler için isteğe bağlı bir ayar
   - Varsayılan aygıtı ayarlamak için resmi bir yol yok, belgelenmemiş `IPolicyConfig` COM arayüzü gerekiyor. `src/Kopru.Core/Audio/MicMute.cs` içinde zaten CoreAudio arayüzleri var (`IMMDeviceEnumerator`, `IMMDevice`, `IAudioEndpointVolume`); bunlar genişletilebilir, `IPolicyConfig` henüz yok
   - Uykudan uyanınca ve yeniden bağlanmalarda, kumanda tekrar bağlandığında da çalışmalı
17. [x] Genel Bakış'taki "Canlı" gecikme grafiği küçüldü (Küçük) — grafik sabit 72 px (pencereyle büyümez; 1100×700'de ~147 px'ti), seviye çubukları (L2 / Sol Haptik / Sağ Haptik / R2) en az 56, en çok 128 px; aradaki boşluk 8–28 px büyür, büyük pencerede kalan yer kartın altında boş kalır. Dikey eksen en az 0–5 ms (önce 0–2,3 ms): en yüksek değer + %15 pay, 5 ms'nin altına inmez (`Sparkline.ScaleTop`) — testli
18. [x] Hızlı Ayarlar'da ses aygıtı seçimi (Orta) — "Ses Çıkışı" ve "Mikrofon" seçicileri (Hızlı Ayarlar ve Ayarlar'da aynı model): Windows'un açık ses aygıtları, sanal kumandanınkiler "Kumanda Hoparlörü" / "Kumanda Mikrofonu" (birkaç kumandada yuva numarasıyla), uzun adlar kısaltılır, tam ad ipucunda; seçili olan Windows'un varsayılanı ve Windows ayarlarından ya da varsayılan aygıt korumasından gelen değişiklikleri hemen izler. Seçim üç rolün hepsini (konsol, multimedya, iletişim) ayarlar ve geri okunur; Windows almazsa kısa hata, ayarlanamıyorsa seçiciler tek satır nedenle kapalı. Uygulamadan seçmek varsayılan aygıt koruması için bilinçli seçimdir (kumanda seçilirse hemen hatırlanır, başkası seçilirse unutulur). Hızlı Ayarlar kartı yeniden düzenlendi: kutucuklar ikişerli üç satır ("Kısayollar" kısa adıyla), ses satırları, sonra haptik / tetik / ışık / profil (Core `AudioDevices`, `MicButtonRules`) — testli (sahtelerle), donanımda denenmedi
19. [x] Genel "Kumanda Türü" seçimi (Orta) — Hızlı Ayarlar'da bir satır ve Desteklenen Oyunlar'da listenin üstünde aynı seçici (tek model, `SettingsState.PadType`): "Otomatik" (varsayılan; oyun başına seçim, oyun yoksa DualSense), "DualSense", "DualShock 4", "Xbox 360". Otomatik dışındaki her değer tüm oyunlarda ve tüm kumanda yuvalarında o türü zorlar; oyun başına seçimler saklanır ama uygulanmaz (listede kapalı görünürler, "Kumanda türü elle seçili; oyun başına seçimler Otomatik'te geçerli." satırıyla). Karar tek yerde: `GameModeController.ResolvePad` (zorlanan tür, yoksa öndeki oyunun seçimi); değişince oyun değişimindeki aynı yoldan tek çıkar-tak (`UsePad` → `SwapPadIfNeededAsync`). Genel ayar olarak settings.json'da `ForcedPadType` (eski dosyalarda yok = Otomatik). DualShock 4 / Xbox 360'tayken kenar çubuğunda "Açık · DS4" / "Açık · Xbox", Genel Bakış'ta oyunun altında neyin olup neyin olmadığı (ipuçlarında da); mevcut tür bildirimi elle seçilince "Kumanda türü elle seçildi: …" der. Ayarlar sayfasına eklenmedi (uygun bir grup yok) — testli (sahtelerle), donanımda denenmedi
20. [x] Bağlantı kalitesi ve uyarısı (Orta) — Core `LinkQualityMonitor` / `LinkLimits`: her saniye en kötü %1 gecikme (p99), uzun kesintiler (50 / 100 ms), kayıp rapor ve rapor hızı, son 10 saniyede (gecikmede medyan: tek sıçramalar sayılmaz) → İyi / Sınırda / Zayıf. Sınırlar (ilk ayar): gecikme 15 / 30 ms ("Hissedilir" çizgisi 30 ms), 10 sn'de 3 kesinti, kayıp %0,5 / %2, hız 250 / 125. Kötüleşme hemen, iyileşme 10 sn ve sınırın %80'inin altında; bağlanma / yeniden bağlanma / tür değişiminden sonra ilk 2 sn atlanır, 3 sn'den uzun örnek boşluğu (uyku, duraklatılmış program) ölçümü baştan başlatır. Genel Bakış "Canlı": durum renkli kelime (nokta yok), sayılar yanında, grafik sabit 0–60 ms ölçekli, kesikli sınır çizgisi ve sınırı aşan saniyelerde kırmızı çubuk, altında duruma göre bir cümle; kalın çizgi artık en kötü gecikme. "Bağlantı Zayıf · Adaptöre yaklaşın" bildirimi: Zayıf 15 sn sürünce, bağlandıktan sonraki ilk 20 sn'de asla, bölüm başına bir kez (İyi'ye dönünce yeni bölüm), en çok 10 dakikada bir; Ayarlar → Bildirimler → "Zayıf Bağlantı" (varsayılan açık); "Oyun Sırasında Bildirim Gösterme" açıksa oyunda susar, oyundan çıkınca hâlâ zayıfsa gösterilir. Durum değişimleri app.log'a yazılır — testli, donanımda denenmedi
21. [x] Adaptör testi sonucunu açıklıyor (Orta) — Kurulum adımında (ve Ayarlar → Test Et'te) sonuç cümlesi + karar veren değerler (ölçülen · gereken · Tamam / Sınırda / Kötü) + uygun değilse iki öneri (USB 2.0 / uzatma kablosu, 2,4 GHz cihazlar, BT 5, birkaç metre). Canlı göstergeyle aynı sınırlar (`LinkLimits`). Düzeltilen yanıltıcı sonuçlar: kumanda test ortasında susunca (raporlar fazın %80'ini kaplamıyor ya da fazda 100'den az rapor) "Uygun" demek yerine "Ölçüm yarım kaldı"; açılıştaki ilk 0,5 sn bağlantı kurulumu sayılmıyor; uzun kesintiler de değerlendiriliyor; "kayıp yok denecek kadar az" gibi ölçülmemiş iddialar kalktı. `adapter-check.json` (tanılama dosyası) aynı açıklamayı ve bulguları içeriyor — testli, donanımda denenmedi

### C. Sonraya
- [x] Touchpad/D-pad ile fare, gyro ile fare (masaüstü modu) — Ayarlar → Masaüstü Modu, PS+Options, tepsi öğesi; gyro hedefi fare — testli, donanımda denenmedi
- [ ] PC sesini haptik olarak hissetme
- [x] Işık efektleri (nefes, gökkuşağı) — bkz. 8. madde
- [ ] Özel tetik efekti editörü; oyun desteklemiyorsa hazır efektler
- [ ] DSX mod uyumluluğu
- [x] DualSense Edge kimliği (Edge Olarak Göster) ve arka tuş atama (Tuş atama → Edge Arka Tuşları) — testli, donanımda denenmedi
- [x] Birden fazla kumanda (en fazla 4; BridgeManager, slot başına bus id/adres, kumanda seçici, kumandaya özel profil) — testli, donanımda denenmedi. Kablo geçişi, pil yoklaması, sağlık ve uyku koruması şimdilik yalnızca 1. slotta
- [x] Xbox 360 taklidi (USB/IP, oyun başına "Kumanda Türü"; haptik/tetik/hoparlör/mikrofon kaybolur) — testli, donanımda denenmedi
- [x] DS4 taklidi (kablolu DualShock 4 v2, 054C:09CC, yalnızca HID; oyun başına "Kumanda Türü" → DualShock 4; titreşim haptik olarak, ışık çubuğu; adaptif tetik/oyun haptikleri/hoparlör/mikrofon yok) — testli, donanımda denenmedi
- [ ] Donanım testi (DS4 taklidi): Windows'ta "Wireless Controller" olarak görünmesi, Steam'in PS4 kumandası olarak tanıması, DS4 destekli bir oyun, titreşim, ışık çubuğu rengi, jiroskop/ivmeölçer ölçeği ve eksenleri, dokunmatik yüzey, pil/şarj göstergesi
- [x] Kumanda bilgisi (0x20/0x09: model, yazılım/donanım sürümü, adres; Kumanda testi kartı, Ayarlar → Hakkında) — testli, donanımda denenmedi
- [x] Kumandayı yeniden bağla (Kumanda testi, tepsi, sağlık düzeltmesi) — testli, donanımda denenmedi
- [x] USB↔BT kablo geçişi (kablo takılınca köprü durur, Bluetooth'ta sürer; bildirim) — testli, donanımda denenmedi

### D. Yayın
- [x] Kullanıcı başına kurulum programı ve zip (`tools/publish.ps1`, `tools/installer/DS5Untethered.iss`): self-contained win-x64 klasör, yönetici izni yok, `%LOCALAPPDATA%\Programs\DS5 Untethered`; kaldırırken "Windows ile Başlat" kaydı silinir, ayarların silinmesi sorulur; yapı makinesinin kullanıcı yolu taraması; tek kopya koruması (ikinci açılış ilk kopyanın penceresini öne getirir)
- [ ] Sürücüleri (usbip-win2, HidHide) kurulum programının kurması — yönetici izni ve yeniden başlatma gerekir; şimdilik uygulamanın Kurulum sayfası yönlendiriyor
- [ ] Kod imzalama (SmartScreen uyarısı)
- [ ] Güncelleme denetimi
- [ ] Uygulama simgesi ve nihai logo
- [ ] Güncelleme kontrolü
- [ ] README ve ekran görüntüleri
