// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// Strings of the desktop app (Kopru.App). Numeric parameters are double so the reflection tests can call every method.
public static partial class Text
{
	// Navigation
	public static string NavOverview(Language? l = null) => Pick(l, $"Genel Bakış", $"Overview");
	public static string NavSupportedGames(Language? l = null) => Pick(l, $"Desteklenen Oyunlar", $"Supported Games");
	public static string NavOtherGames(Language? l = null) => Pick(l, $"Diğer Oyunlar", $"Other Games");
	public static string NavLight(Language? l = null) => Pick(l, $"Işık", $"Light");
	public static string NavSettings(Language? l = null) => Pick(l, $"Ayarlar", $"Settings");

	// Device card
	public static string DeviceConnectedBluetooth(Language? l = null) => Pick(l, $"Bluetooth ile Bağlı", $"Via Bluetooth");
	public static string DeviceNotConnected(Language? l = null) => Pick(l, $"Bağlı Değil", $"Not Connected");
	public static string DeviceNoController(Language? l = null) => Pick(l, $"Kumanda Bulunamadı", $"No Controller Found");
	public static string BatteryCharging(double percent, Language? l = null) => Pick(l, $"Şarj Oluyor %{percent:F0}", $"Charging {percent:F0}%");
	public static string BatteryUnknown(Language? l = null) => Pick(l, $"Pil Bilgisi Yok", $"Battery Unknown");

	// Overview
	public static string OverviewSubtitle(Language? l = null) => Pick(l,
		$"Başlatınca oyunlar Bluetooth kumandayı kablolu kumanda olarak görür.",
		$"When started, games see the Bluetooth controller as a wired one.");
	public static string CardConnection(Language? l = null) => Pick(l, $"Bağlantı", $"Connection");
	public static string CardLatency(Language? l = null) => Pick(l, $"Gecikme / Giriş Hızı", $"Latency / Input Rate");
	public static string CardBattery(Language? l = null) => Pick(l, $"Pil", $"Battery");
	public static string CardHaptics(Language? l = null) => Pick(l, $"Haptik", $"Haptics");
	public static string ConnectionBluetooth(Language? l = null) => Pick(l, $"Bağlı", $"Connected");
	public static string InputRate(double rate, Language? l = null) => Pick(l, $"{rate:F0} rapor/sn", $"{rate:F0} reports/s");
	public static string BatteryValue(double percent, Language? l = null) => Pick(l, $"%{percent:F0}", $"{percent:F0}%");
	public static string Charging(Language? l = null) => Pick(l, $"Şarj Oluyor", $"Charging");
	public static string OnBattery(Language? l = null) => Pick(l, $"Pilde", $"On Battery");
	public static string HapticsActive(Language? l = null) => Pick(l, $"Etkin", $"Active");
	public static string HapticsIdle(Language? l = null) => Pick(l, $"Beklemede", $"Idle");
	public static string HapticsFromGame(Language? l = null) => Pick(l, $"Oyunun Kendi Haptiği", $"The Game's Own Haptics");
	public static string BridgeStart(Language? l = null) => Pick(l, $"Sanal Kumandayı Başlat", $"Start Virtual Controller");
	public static string BridgeStop(Language? l = null) => Pick(l, $"Sanal Kumandayı Durdur", $"Stop Virtual Controller");
	public static string BridgeStatusRunning(Language? l = null) => Pick(l, $"Sanal kumanda çalışıyor: oyunlar kablolu kumanda görüyor.", $"The virtual controller is running: games see a wired controller.");
	public static string BridgeStatusStopped(Language? l = null) => Pick(l, $"Sanal kumanda kapalı.", $"The virtual controller is off.");
	public static string QuickTests(Language? l = null) => Pick(l, $"Hızlı Testler", $"Quick Tests");
	public static string QuickSettings(Language? l = null) => Pick(l, $"Hızlı Ayarlar", $"Quick Settings");
	public static string TestHapticsLeft(Language? l = null) => Pick(l, $"Sol Haptik", $"Left Haptics");
	public static string TestHapticsRight(Language? l = null) => Pick(l, $"Sağ Haptik", $"Right Haptics");
	public static string TestTrigger(Language? l = null) => Pick(l, $"Tetik", $"Trigger");
	public static string TestLight(Language? l = null) => Pick(l, $"Işık", $"Light");
	public static string GameHapticsLevel(Language? l = null) => Pick(l, $"Oyun Haptiği", $"Game Haptics");
	public static string RumbleLevel(Language? l = null) => Pick(l, $"Titreşim", $"Rumble");
	public static string PercentValue(double percent, Language? l = null) => Pick(l, $"%{percent:F0}", $"{percent:F0}%");
	public static string TestSent(Language? l = null) => Pick(l, $"Test kumandaya gönderildi.", $"Test sent to the controller.");
	public static string ComingSoon(Language? l = null) => Pick(l, $"Bu özellik yakında geliyor.", $"This feature is coming soon.");

	// Haptics & rumble
	public static string HapticsIntro(Language? l = null) => Pick(l,
		$"Oyunların haptiği Bluetooth'ta zayıf kalabilir; seviyeyi buradan artır.",
		$"Game haptics can feel weak over Bluetooth; raise the level here.");
	public static string GameHapticsHint(Language? l = null) => Pick(l,
		$"Oyunun gönderdiği haptik sesin gücü. Önerilen: %150.",
		$"Strength of the haptics the game sends. Recommended: 150%.");
	public static string RumbleHint(Language? l = null) => Pick(l,
		$"Klasik titreşim, Bluetooth'ta haptik olarak çalınır.",
		$"Classic rumble is played as haptics over Bluetooth.");
	public static string TestBoth(Language? l = null) => Pick(l, $"İki Taraf", $"Both Sides");
	public static string TestRumble(Language? l = null) => Pick(l, $"Titreşimi Dene", $"Try Rumble");
	public static string Tests(Language? l = null) => Pick(l, $"Testler", $"Tests");

	// Triggers
	public static string TriggerStrength(Language? l = null) => Pick(l, $"Tetik Gücü", $"Trigger Strength");
	public static string TriggerStrengthHint(Language? l = null) => Pick(l,
		$"Desteklenen oyunların tetik efektlerinin ve test efektlerinin gücü. %0 tetikleri kapatır.",
		$"Strength of supported games' trigger effects and of the test effects. 0% turns the triggers off.");
	public static string EffectOff(Language? l = null) => Pick(l, $"Kapalı", $"Off");
	public static string EffectFeedback(Language? l = null) => Pick(l, $"Direnç", $"Feedback");
	public static string EffectWeapon(Language? l = null) => Pick(l, $"Silah", $"Weapon");
	public static string EffectVibration(Language? l = null) => Pick(l, $"Titreşim", $"Vibration");

	// Light
	public static string LightIntro(Language? l = null) => Pick(l,
		$"Bir oyun kendi rengini gönderirse o renk kullanılır.",
		$"If a game sends its own colour, that colour is used.");
	public static string LightbarColour(Language? l = null) => Pick(l, $"Işık Çubuğu Rengi", $"Light Bar Colour");
	public static string Brightness(Language? l = null) => Pick(l, $"Parlaklık", $"Brightness");
	public static string PlayerLeds(Language? l = null) => Pick(l, $"Oyuncu LED'leri", $"Player LEDs");
	public static string PlayerLedsHint(Language? l = null) => Pick(l, $"Dokunmatik yüzeyin altındaki küçük LED'ler.", $"The small LEDs below the touchpad.");
	public static string ColourBlue(Language? l = null) => Pick(l, $"Mavi", $"Blue");
	public static string ColourRed(Language? l = null) => Pick(l, $"Kırmızı", $"Red");
	public static string ColourGreen(Language? l = null) => Pick(l, $"Yeşil", $"Green");
	public static string ColourPurple(Language? l = null) => Pick(l, $"Mor", $"Purple");
	public static string ColourOrange(Language? l = null) => Pick(l, $"Turuncu", $"Orange");
	public static string ColourWhite(Language? l = null) => Pick(l, $"Beyaz", $"White");

	// Settings
	public static string LanguageLabel(Language? l = null) => Pick(l, $"Dil", $"Language");
	public static string LanguageFollowSystem(Language? l = null) => Pick(l, $"Sistemi İzle", $"Follow System");
	public static string StartWithWindows(Language? l = null) => Pick(l, $"Windows ile Başlat", $"Start with Windows");
	public static string StartWithWindowsHint(Language? l = null) => Pick(l, $"Oturum açınca pencere açmadan arka planda başlar.", $"Starts in the background when you sign in, without opening the window.");
	public static string IdlePowerOff(Language? l = null) => Pick(l, $"Boşta Kapatma", $"Idle Power-Off");
	public static string IdlePowerOffHint(Language? l = null) => Pick(l,
		$"Bu kadar dakika boşta kalınca kapanır (0 = asla).",
		$"Turns off after this many idle minutes (0 = never).");
	public static string Minutes(double minutes, Language? l = null) => Pick(l, $"{minutes:F0} dk", $"{minutes:F0} min");
	public static string Never(Language? l = null) => Pick(l, $"Asla", $"Never");
	public static string General(Language? l = null) => Pick(l, $"Genel", $"General");
	public static string Tools(Language? l = null) => Pick(l, $"Araçlar", $"Tools");
	public static string About(Language? l = null) => Pick(l, $"Hakkında", $"About");
	public static string AboutVersion(string version, Language? l = null) => Pick(l, $"Sürüm {version}", $"Version {version}");
	public static string AboutTagline(Language? l = null) =>
		Pick(l, $"DualSense haptikleri ve uyarlanabilir tetikleri, kablosuz.", $"DualSense haptics and adaptive triggers, wirelessly.");
	public static string RunSetupWizard(Language? l = null) => Pick(l, $"Kurulum Sihirbazı", $"Setup Wizard");
	public static string TestBluetoothAdapter(Language? l = null) => Pick(l, $"Bluetooth Adaptör Testi", $"Bluetooth Adapter Test");
	public static string ExportDiagnostics(Language? l = null) => Pick(l, $"Tanılama Raporu", $"Diagnostics Report");

	// Redesigned UI: bridge state, stats, cards and setting rows
	public static string BridgeStateStopped(Language? l = null) => Pick(l, $"Sanal Kumanda Kapalı", $"Virtual Controller Off");
	public static string BridgeStateWaiting(Language? l = null) => Pick(l, $"Oyun Bekleniyor", $"Waiting for a Game");
	public static string BridgeStateRunning(Language? l = null) => Pick(l, $"Çalışıyor", $"Running");
	public static string BridgeHeadlineStopped(Language? l = null) => Pick(l, $"Sanal Kumanda Kapalı", $"Virtual Controller Is Off");
	public static string BridgeHeadlineWaiting(Language? l = null) => Pick(l, $"Sanal Kumanda Hazır", $"Virtual Controller Is Ready");
	public static string BridgeHeadlineRunning(Language? l = null) => Pick(l, $"Sanal Kumanda Çalışıyor", $"Virtual Controller Is Running");
	public static string BridgeDetailWaiting(Language? l = null) => Pick(l,
		$"Sanal kablolu kumanda takıldı. Bir oyun başlatın.",
		$"The virtual wired controller is plugged in. Start a game.");
	public static string BridgeDetailRunning(Language? l = null) => Pick(l,
		$"Oyun kablolu kumanda görüyor; haptik ve tetikler aktarılıyor.",
		$"The game sees a wired controller; haptics and triggers are relayed.");
	public static string StatLatency(Language? l = null) => Pick(l, $"Gecikme", $"Latency");
	public static string StatInputRate(Language? l = null) => Pick(l, $"Giriş Hızı", $"Input Rate");
	public static string LiveOutput(Language? l = null) => Pick(l, $"Canlı Haptik Çıkışı", $"Live Haptics Output");
	public static string LeftShort(Language? l = null) => Pick(l, $"Sol", $"Left");
	public static string RightShort(Language? l = null) => Pick(l, $"Sağ", $"Right");
	public static string EffectOffHint(Language? l = null) => Pick(l, $"Direnç yok, serbest tetik", $"No resistance, free travel");
	public static string EffectFeedbackHint(Language? l = null) => Pick(l, $"Bir noktadan sonra sabit direnç", $"Steady resistance past a point");
	public static string EffectWeaponHint(Language? l = null) => Pick(l, $"Artan direnç, sonra kırılma", $"Builds up, then snaps");
	public static string EffectVibrationHint(Language? l = null) => Pick(l, $"Basılıyken titreşir", $"Pulses while pressed");
	public static string LanguageHint(Language? l = null) => Pick(l, $"Arayüzün dili.", $"The language of the interface.");
	public static string RunSetupWizardHint(Language? l = null) => Pick(l,
		$"Sürücüleri ve eşleşmeyi denetler.",
		$"Checks drivers and pairing again.");
	public static string TestBluetoothAdapterHint(Language? l = null) => Pick(l,
		$"Haptik için yeterli mi, ölçer.",
		$"Checks it keeps up with haptics.");
	public static string ExportDiagnosticsHint(Language? l = null) => Pick(l,
		$"Hata bildirimine eklenecek dosya.",
		$"A file to attach to bug reports.");
	public static string ActionRun(Language? l = null) => Pick(l, $"Çalıştır", $"Run");
	public static string ActionTest(Language? l = null) => Pick(l, $"Test Et", $"Test");
	public static string ActionExport(Language? l = null) => Pick(l, $"Dışa Aktar", $"Export");

	// Real controller service
	public static string TestNoController(Language? l = null) => Pick(l,
		$"Test için kumanda bulunamadı. Kumandanın Bluetooth ile bağlı olduğundan emin olun.",
		$"No controller found for the test. Make sure the controller is connected via Bluetooth.");
	public static string AdapterTestRunning(Language? l = null) => Pick(l, $"Adaptör test ediliyor, yaklaşık 10 saniye sürer…", $"Testing the adapter, this takes about 10 seconds…");

	// Setup wizard page
	public static string SetupPageTitle(Language? l = null) => Pick(l, $"Kurulum", $"Setup");
	public static string SetupPageIntro(Language? l = null) => Pick(l,
		$"DS5 Untethered'ın çalışması için gerekenler. Eksik adımları tamamlayıp yeniden kontrol edin.",
		$"What DS5 Untethered needs to work. Complete the missing steps, then check again.");
	public static string SetupClose(Language? l = null) => Pick(l, $"Kapat", $"Close");

	// Overview dashboard
	public static string OverviewIntro(Language? l = null) => Pick(l,
		$"Kumandanın, sanal kumandanın ve ayarların anlık durumu.",
		$"At-a-glance status of your controller, the virtual controller and settings.");
	public static string OverallOk(Language? l = null) => Pick(l, $"Her Şey Yolunda", $"All Systems Normal");
	public static string OverallAttention(Language? l = null) => Pick(l, $"Dikkat Gerekiyor", $"Needs Attention");
	public static string OverallNoController(Language? l = null) => Pick(l, $"Kumanda Bağlı Değil", $"No Controller Connected");
	public static string CardBridge(Language? l = null) => Pick(l, $"Sanal Kumanda", $"Virtual Controller");
	public static string CardController(Language? l = null) => Pick(l, $"Kumanda", $"Controller");
	public static string RowLink(Language? l = null) => Pick(l, $"Bluetooth", $"Link");
	public static string RowStatus(Language? l = null) => Pick(l, $"Durum", $"Status");
	public static string RowGame(Language? l = null) => Pick(l, $"Oyun", $"Game");
	public static string RowMode(Language? l = null) => Pick(l, $"Mod", $"Mode");
	public static string RowVirtualPad(Language? l = null) => Pick(l, $"Sanal Kumanda", $"Virtual Controller");
	public static string RowModel(Language? l = null) => Pick(l, $"Tür", $"Model");
	public static string RowPower(Language? l = null) => Pick(l, $"Güç", $"Power");
	public static string ValuePluggedIn(Language? l = null) => Pick(l, $"Takılı", $"Plugged in");
	public static string ValueUnplugged(Language? l = null) => Pick(l, $"Takılı Değil", $"Not Plugged In");
	public static string ValueGameConnected(Language? l = null) => Pick(l, $"Bağlandı", $"Connected");
	public static string ValueNoGame(Language? l = null) => Pick(l, $"Yok", $"None");
	public static string QuickActions(Language? l = null) => Pick(l, $"Hızlı İşlemler", $"Quick Actions");
	public static string QuickControls(Language? l = null) => Pick(l, $"Hızlı Kontroller", $"Quick Controls");

	// Overview by state: the status pill, the one main action, the hero, the live panel and the health chips.
	public static string OverviewReady(Language? l = null) => Pick(l, $"Hazır", $"Ready");
	public static string OverviewWaitingController(Language? l = null) => Pick(l, $"Kumanda Bekleniyor", $"Waiting for Controller");
	public static string OverviewPlaying(string game, Language? l = null) => Pick(l, $"Çalışıyor · {game}", $"Running · {game}");
	public static string PairController(Language? l = null) => Pick(l, $"Kumandayı Eşleştir", $"Pair Controller");
	public static string FinishSetup(Language? l = null) => Pick(l, $"Kurulumu Tamamla", $"Finish Setup");
	public static string SetupNeeded(Language? l = null) => Pick(l, $"Kurulum Gerekli", $"Setup Needed");
	public static string HeroGame(Language? l = null) => Pick(l, $"Oyun", $"Game");
	public static string HeroNoController(Language? l = null) => Pick(l, $"Kumanda Bağlı Değil", $"No Controller");
	public static string ReasonRadioOff(Language? l = null) => Pick(l, $"Bluetooth kapalı.", $"Bluetooth is off.");
	public static string ReasonRadioMissing(Language? l = null) => Pick(l, $"Bluetooth adaptörü bulunamadı.", $"No Bluetooth adapter found.");
	public static string ReasonUsbOnly(Language? l = null) => Pick(l, $"Kumanda USB kablosuyla bağlı.", $"The controller is on a USB cable.");
	public static string ReasonControllerOff(Language? l = null) => Pick(l, $"Kumanda kapalı ya da eşleşmemiş.", $"The controller is off or not paired.");
	public static string HintPressPs(Language? l = null) => Pick(l, $"PS tuşuna basın; ilk kez bağlıyorsanız eşleştirin.", $"Press the PS button, or pair it if this is the first time.");
	public static string LiveTitle(Language? l = null) => Pick(l, $"Canlı", $"Live");
	public static string LiveIdle(Language? l = null) => Pick(l, $"Sanal kumanda çalışırken burada görünür.", $"Shows here while the virtual controller runs.");
	public static string LiveLast30s(Language? l = null) => Pick(l, $"Son 30 sn", $"Last 30 s");
	public static string LiveRateRange(double min, double avg, Language? l = null) => Pick(l, $"en düşük {min:F0} · ort. {avg:F0}", $"min {min:F0} · avg {avg:F0}");
	public static string LiveBridgeUptime(string duration, Language? l = null) => Pick(l, $"Sanal kumanda açık · {duration}", $"Virtual controller on · {duration}");
	public static string LiveConnectedFor(string duration, Language? l = null) => Pick(l, $"Bağlı · {duration}", $"Connected · {duration}");
	public static string LiveSpeaker(Language? l = null) => Pick(l, $"Hoparlör", $"Speaker");
	public static string LiveMic(Language? l = null) => Pick(l, $"Mikrofon", $"Microphone");
	public static string LiveMuted(Language? l = null) => Pick(l, $"Sessizde", $"Muted");
	public static string LiveOnVolume(double percent, Language? l = null) => Pick(l, $"Açık · %{percent:F0}", $"On · {percent:F0}%");
	public static string LatencyShort(double p50, double p99, Language? l = null) => Pick(l, $"{p50:F1} ms · en kötü {p99:F1}", $"{p50:F1} ms · worst {p99:F1}");
	public static string ControlsNeedController(Language? l = null) => Pick(l, $"Kumanda bağlanınca kullanılabilir", $"Available once the controller connects");
	public static string TryAction(Language? l = null) => Pick(l, $"Dene", $"Try");
	public static string HealthDriver(Language? l = null) => Pick(l, $"Sürücü", $"Driver");
	public static string HealthAdapter(Language? l = null) => Pick(l, $"Adaptör", $"Adapter");
	public static string HealthInstalled(Language? l = null) => Pick(l, $"Kurulu", $"Installed");
	public static string HealthMissing(Language? l = null) => Pick(l, $"Kurulu Değil", $"Not Installed");
	public static string HealthUnknown(Language? l = null) => Pick(l, $"Bilinmiyor", $"Unknown");
	public static string HealthNoIssue(Language? l = null) => Pick(l, $"Sorun Yok", $"No Issue");
	public static string HealthOn(Language? l = null) => Pick(l, $"Açık", $"On");
	public static string HealthOff(Language? l = null) => Pick(l, $"Kapalı", $"Off");
	public static string HealthSuitable(Language? l = null) => Pick(l, $"Uygun", $"Suitable");
	public static string HealthBorderline(Language? l = null) => Pick(l, $"Sınırda", $"Borderline");
	public static string HealthUnsuitable(Language? l = null) => Pick(l, $"Uygun Değil", $"Unsuitable");
	public static string HealthNotTested(Language? l = null) => Pick(l, $"Test Edilmedi", $"Not Tested");
	public static string HealthError(Language? l = null) => Pick(l, $"Hata", $"Error");
	public static string HealthFix(Language? l = null) => Pick(l, $"Düzelt", $"Fix");
	public static string HealthStart(Language? l = null) => Pick(l, $"Başlat", $"Start");
	public static string HealthRestart(Language? l = null) => Pick(l, $"Yeniden Başlat", $"Restart");
	public static string GroupStartup(Language? l = null) => Pick(l, $"Başlangıç", $"Startup");
	public static string GroupPowerSaving(Language? l = null) => Pick(l, $"Güç Tasarrufu", $"Power Saving");

	// Game modes: supported (native DualSense) and other games
	public static string SupportedGamesIntro(Language? l = null) => Pick(l,
		$"DualSense'i doğrudan destekleyen oyunlar kendi haptiğini, tetik efektlerini ve ışığını gönderir.",
		$"Games that support the DualSense natively send their own haptics, trigger effects and light.");
	public static string OtherGamesIntro(Language? l = null) => Pick(l,
		$"Yalnızca klasik titreşim gönderen oyunlar. Titreşim haptik olarak çalınır, tetiklere yedek efekt verilebilir.",
		$"Games that only send classic rumble. Rumble is played as haptics, and the triggers can get a fallback effect.");
	public static string ProfileSettings(Language? l = null) => Pick(l, $"Bu Oyunlar için Ayarlar", $"Settings for These Games");
	public static string LightFromGame(Language? l = null) => Pick(l, $"Oyunun Işığını Kullan", $"Use the Game's Light");
	public static string LightFromGameHint(Language? l = null) => Pick(l,
		$"Kapalıyken ışık çubuğu sizin renginizde kalır.",
		$"When off, the light bar keeps your colour.");
	public static string SupportedGameList(Language? l = null) => Pick(l, $"Desteklenen Oyun Listesi", $"Supported Games List");
	public static string SearchGames(Language? l = null) => Pick(l, $"Oyun Ara", $"Search Games");
	public static string GameCount(double count, Language? l = null) => Pick(l, $"{count:F0} oyun", $"{count:F0} games");
	public static string NoMatchingGames(Language? l = null) => Pick(l, $"Eşleşen oyun yok.", $"No matching games.");

	// Online update of the supported games list.
	public static string UpdateGameList(Language? l = null) => Pick(l, $"Listeyi Güncelle", $"Update List");
	public static string UpdatingGameList(Language? l = null) => Pick(l, $"Güncelleniyor…", $"Updating…");
	public static string GameListUpToDate(Language? l = null) => Pick(l, $"Zaten güncel", $"Already up to date");
	public static string GameListAdded(double added, Language? l = null) => Pick(l, $"{added:F0} yeni oyun eklendi", $"{added:F0} new games added");
	public static string GameListFailed(string reason, Language? l = null) => Pick(l, $"Güncellenemedi: {reason}", $"Couldn't update: {reason}");
	public static string GameListOffline(Language? l = null) => Pick(l, $"sunucuya ulaşılamadı, internet bağlantını kontrol et.", $"the server couldn't be reached, check your internet connection.");
	public static string GameListTimeout(Language? l = null) => Pick(l, $"sunucu zamanında yanıt vermedi.", $"the server didn't answer in time.");
	public static string GameListInvalid(Language? l = null) => Pick(l, $"indirilen liste geçersiz.", $"the downloaded list is invalid.");
	public static string GameListUnsupported(Language? l = null) => Pick(l, $"liste bu sürüm için çok yeni, uygulamayı güncelle.", $"the list is too new for this version, update the app.");
	public static string GameListSaveFailed(Language? l = null) => Pick(l, $"liste kaydedilemedi.", $"the list couldn't be saved.");
	public static string AutoUpdateGameList(Language? l = null) => Pick(l, $"Oyun Listesini Otomatik Güncelle", $"Update Game List Automatically");
	public static string NotifyGameListTitle(Language? l = null) => Pick(l, $"Liste Güncellendi", $"Game List Updated");
	public static string NotifyGameListBody(double added, Language? l = null) => Pick(l, $"{added:F0} yeni oyun eklendi", $"{added:F0} new games added");

	// "72 oyun · güncellendi 9 Kas 2026"; without the date when updated is not an ISO date.
	public static string GameListInfo(double count, string updated, Language? l = null) =>
		DateTime.TryParseExact(updated, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime date)
			? Pick(l, $"{count:F0} oyun · güncellendi {date:d MMM yyyy}", $"{count:F0} games · updated {date:d MMM yyyy}")
			: Pick(l, $"{count:F0} oyun", $"{count:F0} games");
	public static string BadgeHaptics(Language? l = null) => Pick(l, $"Haptik", $"Haptics");
	public static string BadgeTriggers(Language? l = null) => Pick(l, $"Tetik", $"Triggers");
	public static string MarkAlways(Language? l = null) => Pick(l, $"Otomatik Aç", $"Auto");
	public static string MarkAlwaysTip(Language? l = null) => Pick(l, $"Bu oyun açılınca mod sormadan açılır.", $"The mode turns on without asking when this game starts.");
	public static string MarkNeverAsk(Language? l = null) => Pick(l, $"Sorma", $"Don't Ask");
	public static string MarkNeverAskTip(Language? l = null) => Pick(l, $"Bu oyun açılınca soru gösterilmez.", $"No question when this game starts.");
	public static string MarkUserAdded(Language? l = null) => Pick(l, $"Eklendi", $"Added");
	public static string RemoveMark(Language? l = null) => Pick(l, $"İşareti Kaldır", $"Remove Mark");
	public static string AddGame(Language? l = null) => Pick(l, $"Oyun Ekle", $"Add Game");
	public static string AddGameFromFile(Language? l = null) => Pick(l, $"Dosyadan Seç…", $"Choose File…");
	public static string AddGameRunningHeader(Language? l = null) => Pick(l, $"Açık Uygulamalar", $"Running Apps");
	public static string AddGameNoRunning(Language? l = null) => Pick(l, $"Açık uygulama yok", $"No running apps");
	public static string RemoveGame(Language? l = null) => Pick(l, $"Kaldır", $"Remove");
	public static string GameListHint(Language? l = null) => Pick(l,
		$"Listedeki ya da eklediğiniz bir oyun açılınca sorulur. Otomatik Aç işaretli oyunlarda mod sormadan açılır.",
		$"You're asked when a listed or added game starts. Games marked Auto turn the mode on without asking.");

	// The supported mode switch and the question when a known game starts.
	public static string SupportedMode(Language? l = null) => Pick(l, $"DualSense Modu", $"DualSense Mode");
	public static string SupportedModeShort(Language? l = null) => Pick(l, $"DualSense Modu", $"DualSense Mode");
	// One line under the switch where there is room.
	public static string SupportedModeHelper(Language? l = null) => Pick(l, $"Oyunun haptiği, tetikleri, ışığı ve hoparlör sesleri", $"The game's haptics, triggers, light and speaker sounds");
	public static string SupportedModeHint(Language? l = null) => Pick(l,
		$"Açıkken oyunun kendi haptiği, tetik efektleri, ışığı ve kumanda hoparlörü kullanılır; kapalıyken klasik titreşim (diğer oyunlar profili).",
		$"When on, the game's own haptics, trigger effects, light and the controller speaker are used; when off, classic rumble (the other games profile).");
	public static string ModeShortSupported(Language? l = null) => Pick(l, $"DualSense Modu", $"DualSense Mode");
	public static string AskGameBody(Language? l = null) => Pick(l, $"DualSense modu açılsın mı?", $"Turn on DualSense mode?");
	public static string AskTurnOn(Language? l = null) => Pick(l, $"Aç", $"Turn On");
	public static string AskNotNow(Language? l = null) => Pick(l, $"Şimdi Değil", $"Not Now");
	public static string AskAlways(Language? l = null) => Pick(l, $"Her Zaman", $"Always");
	public static string AskOnKnownGame(Language? l = null) => Pick(l, $"Tanınan Oyunda Sor", $"Ask When a Known Game Starts");
	public static string AskOnKnownGameHint(Language? l = null) => Pick(l,
		$"Mod kapalıyken listedeki bir oyun açılınca bildirimle sorar.",
		$"Asks with a notification when a listed game starts while the mode is off.");
	public static string TraySupportedMode(Language? l = null) => Pick(l, $"DualSense Modu", $"DualSense Mode");
	public static string ModeSupported(Language? l = null) => Pick(l, $"DualSense Modu", $"DualSense Mode");
	public static string ModeUnsupported(Language? l = null) => Pick(l, $"Klasik Titreşim", $"Classic Rumble");
	public static string ModeShortClassic(Language? l = null) => Pick(l, $"Klasik Titreşim", $"Classic Rumble");
	public static string ReasonGame(Language? l = null) => Pick(l, $"bu oyun için açıldı", $"turned on for this game");
	public static string ReasonAlways(Language? l = null) => Pick(l, $"her zaman açılır", $"always on for this game");
	public static string ReasonUser(Language? l = null) => Pick(l, $"sizin seçiminiz", $"your choice");
	public static string ReasonDefault(Language? l = null) => Pick(l, $"varsayılan", $"default");
	public static string NoGameYet(Language? l = null) => Pick(l, $"Tanınan oyun açık değil", $"No known game running");
	public static string ActiveNow(Language? l = null) => Pick(l, $"Şu An Etkin", $"Active Now");
	public static string RumbleFeelLabel(Language? l = null) => Pick(l, $"Titreşim Hissi", $"Rumble Feel");
	public static string RumbleFeelSoft(Language? l = null) => Pick(l, $"Yumuşak", $"Soft");
	public static string RumbleFeelPunchy(Language? l = null) => Pick(l, $"Sert", $"Punchy");
	public static string RumbleFeelHint(Language? l = null) => Pick(l,
		$"Yumuşak: pes, akıcı tonlar. Sert: daha tiz, kısa ve keskin vuruşlar.",
		$"Soft: low, smooth tones. Punchy: higher, shorter, sharper hits.");
	public static string FallbackTriggerLabel(Language? l = null) => Pick(l, $"Yedek Tetik Efekti", $"Fallback Trigger Effect");
	public static string FallbackTriggerHint(Language? l = null) => Pick(l,
		$"Oyun tetik efekti göndermediği için L2 ve R2'ye uygulanır.",
		$"Applied to L2 and R2, since the game sends no trigger effects.");
	public static string FallbackStrength(Language? l = null) => Pick(l, $"Efekt Gücü", $"Effect Strength");
	public static string TestFallbackTrigger(Language? l = null) => Pick(l, $"Tetik Efektini Dene", $"Try Trigger Effect");

	// The controller page: test, sticks, triggers, remap & gyro
	public static string NavController(Language? l = null) => Pick(l, $"Kumanda", $"Controller");
	public static string ControllerIntro(Language? l = null) => Pick(l,
		$"Kumandanı test et, gördüğünü hemen düzelt. Analog, tetik ve tuş ayarları da burada.",
		$"Test your controller and fix what you see right away. Stick, trigger and button settings are here too.");
	public static string LivePreviewNeedsBridge(Language? l = null) => Pick(l,
		$"Canlı önizleme sanal kumanda çalışırken görünür.",
		$"The live preview shows while the virtual controller is running.");
	public static string DriftCalibration(Language? l = null) => Pick(l, $"Çubuk Kayması Kalibrasyonu", $"Stick Drift Calibration");
	public static string CalibrateAction(Language? l = null) => Pick(l, $"Kalibre Et", $"Calibrate");
	public static string ResetToDefault(Language? l = null) => Pick(l, $"Varsayılana Dön", $"Reset to Default");

	// Power, latency, diagnostics
	public static string PowerOffWhenIdleHint(Language? l = null) => Pick(l,
		$"Açıkken, boşta kapatma süresi dolunca kumanda da kapatılır.",
		$"When on, the controller is also turned off once the idle time has passed.");
	public static string TurnOffController(Language? l = null) => Pick(l, $"Kumandayı Kapat", $"Turn Off Controller");
	public static string PowerOffRunning(Language? l = null) => Pick(l, $"Kumanda kapatılıyor…", $"Turning off the controller…");
	public static string LatencyHint(Language? l = null) => Pick(l,
		$"Bluetooth bağlantısının ek gecikmesi: ortanca / en kötü %1. Sanal kumanda çalışırken ölçülür.",
		$"Extra delay of the Bluetooth link: median / worst 1%. Measured while the virtual controller runs.");
	public static string DiagnosticsRunning(Language? l = null) => Pick(l, $"Tanılama dosyası hazırlanıyor…", $"Preparing the diagnostics file…");

	// Notifications (toasts) and the tray icon
	public static string NotifyConnectedTitle(Language? l = null) => Pick(l, $"Kumanda Bağlandı", $"Controller Connected");
	public static string NotifyConnectedBody(string name, Language? l = null) => Pick(l, $"{name} hazır", $"{name} is ready");
	public static string NotifyDisconnectedTitle(Language? l = null) => Pick(l, $"Bağlantı Kesildi", $"Disconnected");
	public static string NotifyDisconnectedBody(Language? l = null) => Pick(l, $"Açınca yeniden bağlanır", $"Reconnects when turned on");
	public static string NotifyBatteryLowTitle(Language? l = null) => Pick(l, $"Pil Azaldı", $"Battery Low");
	public static string NotifyBatteryLowBody(double percent, Language? l = null) => Pick(l, $"%{percent:F0} kaldı, şarja takın", $"{percent:F0}% left, plug it in");
	public static string NotifyBatteryCriticalTitle(Language? l = null) => Pick(l, $"Pil Bitiyor", $"Battery Critical");
	public static string NotifyBatteryCriticalBody(double percent, Language? l = null) => Pick(l, $"%{percent:F0} kaldı, yakında kapanır", $"{percent:F0}% left, turning off soon");
	public static string NotifyChargedTitle(Language? l = null) => Pick(l, $"Şarj Doldu", $"Fully Charged");
	public static string NotifyChargedBody(Language? l = null) => Pick(l, $"Kabloyu çıkarabilirsiniz", $"You can unplug it");
	public static string NotifyGameSupported(Language? l = null) => Pick(l, $"DualSense modu açık", $"DualSense mode is on");
	public static string NotifySteamInputTitle(Language? l = null) => Pick(l, $"Steam Input Açık", $"Steam Input Is On");
	public static string NotifySteamInputBody(Language? l = null) => Pick(l, $"Haptik çalışmayabilir", $"Haptics may not work");
	public static string NotifyPoweredOffTitle(Language? l = null) => Pick(l, $"Kumanda Kapandı", $"Controller Off");
	public static string NotifyPoweredOffBody(Language? l = null) => Pick(l, $"Boşta kaldığı için", $"Idle for too long");
	public static string NotifyBridgeErrorTitle(Language? l = null) => Pick(l, $"Sanal Kumanda Hatası", $"Virtual Controller Error");
	// Short reasons for the bridge error toast (the full text is on the Overview page).
	public static string NotifyBridgeNoIdentity(Language? l = null) => Pick(l, $"Kumanda kimliği yok", $"No controller identity");
	public static string NotifyBridgeIdentityUnreadable(Language? l = null) => Pick(l, $"Kimlik dosyası okunamadı", $"Identity file unreadable");
	public static string NotifyBridgeUsbIpMissing(Language? l = null) => Pick(l, $"usbip-win2 kurulu değil", $"usbip-win2 not installed");
	public static string NotifyBridgeAttachFailed(Language? l = null) => Pick(l, $"Sanal kumanda takılamadı", $"Virtual controller not attached");
	public static string NotifyBridgeOpenFailed(Language? l = null) => Pick(l, $"Kumanda açılamadı", $"Controller won't open");
	public static string NotifyBridgeLost(Language? l = null) => Pick(l, $"Kumanda bağlantısı koptu", $"Controller connection lost");
	public static string NotifyBridgeOther(Language? l = null) => Pick(l, $"Ayrıntılar Genel Bakış'ta", $"Details on the Overview page");
	public static string NotifyHidHideTitle(Language? l = null) => Pick(l, $"Sanal Kumanda Başlamadı", $"Virtual Controller Not Started");
	public static string NotifyHidHideBody(Language? l = null) => Pick(l, $"Önce HidHide'ı kurun", $"Install HidHide first");
	public static string NotifyTrayTitle(Language? l = null) => Pick(l, $"Arka Planda Çalışıyor", $"Running in Background");
	public static string NotifyTrayBody(Language? l = null) => Pick(l, $"Simgeyi saat yanında bulun", $"Find the icon by the clock");
	public static string NotifyClose(Language? l = null) => Pick(l, $"Bildirimi Kapat", $"Dismiss");
	public static string GroupNotifications(Language? l = null) => Pick(l, $"Bildirimler", $"Notifications");
	public static string NotificationsShow(Language? l = null) => Pick(l, $"Bildirimleri Göster", $"Show Notifications");
	public static string NotificationsShowHint(Language? l = null) => Pick(l, $"Ekranın sağ altında kısa bilgi kartları.", $"Small cards in the bottom-right corner.");
	public static string NotifyConnectionSetting(Language? l = null) => Pick(l, $"Bağlantı", $"Connection");
	public static string NotifyConnectionHint(Language? l = null) => Pick(l, $"Kumanda bağlanınca ve bağlantısı kesilince.", $"When a controller connects or drops.");
	public static string NotifyBatterySetting(Language? l = null) => Pick(l, $"Pil", $"Battery");
	public static string NotifyBatteryHint(Language? l = null) => Pick(l, $"%20 ve %10'da birer kez, şarj bitince.", $"At 20% and 10%, and when fully charged.");
	public static string NotifyGameSetting(Language? l = null) => Pick(l, $"Oyun Algılama", $"Game Detection");
	public static string NotifyGameHint(Language? l = null) => Pick(l, $"Desteklenen bir oyun DualSense modu açıkken başlayınca.", $"When a supported game starts with DualSense mode on.");
	public static string NotifySteamInputSetting(Language? l = null) => Pick(l, $"Steam Input Uyarısı", $"Steam Input Warning");
	public static string NotifySteamInputHint(Language? l = null) => Pick(l, $"Oyun oturumu başına bir kez.", $"Once per game session.");
	public static string NotifyPowerOffSetting(Language? l = null) => Pick(l, $"Boşta Kapatma", $"Idle Power-Off");
	public static string NotifyPowerOffHint(Language? l = null) => Pick(l, $"Kumanda kullanılmadığı için kapatılınca.", $"When it turns off for being idle.");
	public static string NotifyErrorsSetting(Language? l = null) => Pick(l, $"Sanal Kumanda Hataları", $"Virtual Controller Errors");
	public static string NotifyErrorsHint(Language? l = null) => Pick(l, $"Bir sorunla durunca, nedeniyle birlikte.", $"When it stops on a problem, with the reason.");
	public static string NotifyHidHideSetting(Language? l = null) => Pick(l, $"HidHide Eksik", $"HidHide Missing");
	public static string NotifyHidHideHint(Language? l = null) => Pick(l, $"Sanal kumanda HidHide kurulu olmadığı için başlayamazsa.", $"If the virtual controller can't start because HidHide isn't installed.");
	public static string QuietWhilePlaying(Language? l = null) => Pick(l, $"Oyun Sırasında Bildirim Gösterme", $"Don't Notify While Playing");
	public static string QuietWhilePlayingHint(Language? l = null) => Pick(l, $"Oyunda ve tam ekranda sessiz kalır.", $"Silent in games and full screen apps.");
	public static string KeepInTray(Language? l = null) => Pick(l, $"Kapatınca Arka Planda Çalış", $"Keep Running in Background");
	public static string KeepInTrayHint(Language? l = null) => Pick(l, $"Simge, saat yanındaki gizli simgeler bölümünde durur.", $"The icon stays in the hidden icons area next to the clock.");
	public static string TrayOpen(Language? l = null) => Pick(l, $"Aç", $"Open");
	public static string TrayNotificationsOff(Language? l = null) => Pick(l, $"Bildirimleri Kapat", $"Turn Off Notifications");
	public static string TrayNotificationsOn(Language? l = null) => Pick(l, $"Bildirimleri Aç", $"Turn On Notifications");
	public static string TrayExit(Language? l = null) => Pick(l, $"Çıkış", $"Exit");
	public static string TrayNoController(Language? l = null) => Pick(l, $"Kumanda Yok", $"No Controller");
	public static string TrayBattery(double percent, Language? l = null) => Pick(l, $"Pil %{percent:F0}", $"Battery {percent:F0}%");
}
