DS5 Untethered
==============

[English]  (Türkçe metin aşağıda)

DS5 Untethered makes a DualSense controller connected over Bluetooth appear to games as a wired one, so the game's
own haptics and adaptive triggers work without a cable.

This is a preview build. It is not signed.

1. Install or unzip
   - Installer (DS5-Untethered-...-Setup.exe): run it. It installs for your Windows user only, without administrator
     rights, into %LOCALAPPDATA%\Programs\DS5 Untethered and adds a Start menu shortcut.
   - Zip (DS5-Untethered-...-win-x64.zip): unzip the whole folder anywhere (for example Documents\DS5 Untethered) and
     run Kopru.App.exe. Keep all files together. Nothing else needs to be installed; .NET is included.

2. Windows SmartScreen warning
   Because the build is not signed, Windows may show "Windows protected your PC". Click "More info", then "Run anyway".
   If the zip's files are blocked, right-click the zip before unzipping, choose Properties, tick "Unblock", then OK.

3. First start: the Setup page
   The app opens its Setup page when it starts, until the required steps below have been done once. It checks, step
   by step:
   - Language (Turkish or English).
   - Drivers: DS5 Untethered needs two free drivers, usbip-win2 (the virtual wired controller) and HidHide (hides the
     real controller from games so they see only one). The app does not install them: its "Download" buttons open
     their official GitHub release pages in your browser. Install both (their installers ask for administrator
     rights), then click "Check Again". Both are required: the virtual controller does not start until both are
     installed. The app does not require a specific
     version; it looks for the installed program and its driver. If Windows reports a pending restart, the app tells
     you; restart the PC if a driver installer asks you to.
   - Pair the controller: with the controller off, hold PS and Create until the light bar flashes quickly, then add it
     in Windows Settings > Bluetooth & devices > Add device. Use Bluetooth, not the USB cable.
   - Bluetooth adapter test (optional): measures whether your adapter is fast enough for haptics.
   When the required steps are done, the virtual controller starts by itself whenever the controller connects, and
   the Setup page no longer opens by itself (it stays available in Settings). If a driver is removed later, the
   Overview page says so and its button opens the Setup page.

4. Settings and logs
   Settings, profiles and logs are stored in %LOCALAPPDATA%\Untethered (logs in its "logs" folder).

5. Removing everything
   - In the app, turn off "Start with Windows" first, then exit it from the tray icon (right-click > Exit).
   - Installer version: uninstall "DS5 Untethered" in Windows Settings > Apps > Installed apps. It asks whether to
     delete the settings too.
     Zip version: delete the folder you unzipped.
   - To remove settings and logs, delete %LOCALAPPDATA%\Untethered.
   - If no other program needs them, uninstall "USBip" and "HidHide" in Windows Settings > Apps > Installed apps.

DS5 Untethered is an unofficial project. It is not affiliated with, endorsed or sponsored by Sony Interactive
Entertainment. "DualSense", "DualSense Edge", "PlayStation" and "PS5" are trademarks of Sony Interactive Entertainment
Inc. and are used only to describe compatibility. Use at your own risk; the software comes with no warranty.
License: GPL-3.0-or-later (see LICENSE). Third-party components: see THIRD-PARTY-NOTICES.md.


[Türkçe]

DS5 Untethered, Bluetooth ile bağlı bir DualSense kumandayı oyunlara kablolu gibi gösterir; böylece oyunun kendi
titreşimleri ve adaptif tetikleri kablosuz çalışır.

Bu bir ön sürümdür. İmzalı değildir.

1. Kurun ya da zip'i açın
   - Kurulum (DS5-Untethered-...-Setup.exe): çalıştırın. Yalnızca sizin Windows kullanıcınız için, yönetici izni
     istemeden %LOCALAPPDATA%\Programs\DS5 Untethered klasörüne kurar ve Başlat menüsüne kısayol ekler.
   - Zip (DS5-Untethered-...-win-x64.zip): klasörün tamamını istediğiniz yere açın (ör. Belgeler\DS5 Untethered) ve
     Kopru.App.exe'yi çalıştırın. Dosyaları birlikte tutun. Başka bir şey kurmanız gerekmez; .NET içindedir.

2. Windows SmartScreen uyarısı
   Sürüm imzalı olmadığı için Windows "Windows bilgisayarınızı korudu" uyarısı gösterebilir. "Ek bilgi"ye, sonra
   "Yine de çalıştır"a tıklayın. Zip'teki dosyalar engellenirse zip'i açmadan önce sağ tıklayıp Özellikler'i seçin,
   "Engellemeyi kaldır"ı işaretleyip Tamam'a basın.

3. İlk açılış: Kurulum sayfası
   Uygulama, aşağıdaki gerekli adımlar bir kez tamamlanana kadar her açılışta Kurulum sayfasını açar ve adım adım
   kontrol eder:
   - Dil (Türkçe ya da İngilizce).
   - Sürücüler: DS5 Untethered iki ücretsiz sürücüye ihtiyaç duyar: usbip-win2 (sanal kablolu kumanda) ve HidHide
     (gerçek kumandayı oyunlardan gizler, oyun tek kumanda görür). Uygulama bunları kendisi kurmaz: "İndir" düğmeleri
     resmi GitHub sürüm sayfalarını tarayıcıda açar. İkisini de kurun (kurulumları yönetici izni ister), sonra
     "Yeniden Kontrol Et"e tıklayın. İkisi de gerekli: ikisi de kurulmadan sanal kumanda başlamaz. Uygulama belirli
     bir sürüm istemez; kurulu
     programı ve sürücüsünü arar. Windows yeniden başlatma bekliyorsa uygulama bunu söyler; bir sürücü kurulumu
     isterse bilgisayarı yeniden başlatın.
   - Kumandayı eşleştirin: kumanda kapalıyken PS ve Create tuşlarını ışık çubuğu hızlı yanıp sönene kadar basılı
     tutun, sonra Windows Ayarlar > Bluetooth ve cihazlar > Cihaz ekle ile ekleyin. USB kablosu değil, Bluetooth
     kullanın.
   - Bluetooth adaptör testi (isteğe bağlı): adaptörünüzün titreşim için yeterince hızlı olup olmadığını ölçer.
   Gerekli adımlar tamamlanınca sanal kumanda, kumanda her bağlandığında kendiliğinden başlar ve Kurulum sayfası
   artık kendiliğinden açılmaz (Ayarlar'dan açılabilir). Bir sürücü sonradan kaldırılırsa Genel Bakış bunu söyler ve
   düğmesi Kurulum sayfasını açar.

4. Ayarlar ve günlükler
   Ayarlar, profiller ve günlükler %LOCALAPPDATA%\Untethered klasöründedir (günlükler "logs" klasöründe).

5. Her şeyi kaldırma
   - Önce uygulamada "Windows ile Başlat"ı kapatın, sonra sistem tepsisindeki simgeden çıkın (sağ tık > Çıkış).
   - Kurulumla kurduysanız: Windows Ayarlar > Uygulamalar > Yüklü uygulamalar'dan "DS5 Untethered"ı kaldırın.
     Ayarların da silinip silinmeyeceğini sorar.
     Zip ile kullandıysanız: açtığınız klasörü silin.
   - Ayarları ve günlükleri silmek için %LOCALAPPDATA%\Untethered klasörünü silin.
   - Başka bir program kullanmıyorsa "USBip" ve "HidHide"ı Windows Ayarlar > Uygulamalar > Yüklü uygulamalar'dan
     kaldırın.

DS5 Untethered resmi olmayan bir projedir. Sony Interactive Entertainment ile bağlantılı değildir, onun tarafından
onaylanmamış veya desteklenmemiştir. "DualSense", "DualSense Edge", "PlayStation" ve "PS5", Sony Interactive
Entertainment Inc.'in ticari markalarıdır ve yalnızca uyumluluğu belirtmek için kullanılır. Kullanım riski size
aittir; yazılım hiçbir garanti olmadan sunulur.
Lisans: GPL-3.0-or-later (bkz. LICENSE). Üçüncü taraf bileşenler: bkz. THIRD-PARTY-NOTICES.md.
