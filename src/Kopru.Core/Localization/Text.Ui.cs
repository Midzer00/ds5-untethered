// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// Strings of the desktop app (Kopru.App). Numeric parameters are double so the reflection tests can call every method.
public static partial class Text
{
	// Navigation
	public static string NavOverview(Language? l = null) => Pick(l, $"Genel bakış", $"Overview");
	public static string NavSupportedGames(Language? l = null) => Pick(l, $"Desteklenen oyunlar", $"Supported games");
	public static string NavOtherGames(Language? l = null) => Pick(l, $"Diğer oyunlar", $"Other games");
	public static string NavTriggers(Language? l = null) => Pick(l, $"Tetikler", $"Triggers");
	public static string NavLight(Language? l = null) => Pick(l, $"Işık", $"Light");
	public static string NavSettings(Language? l = null) => Pick(l, $"Ayarlar", $"Settings");

	// Device card
	public static string DeviceConnectedBluetooth(Language? l = null) => Pick(l, $"Bluetooth ile bağlı", $"Connected via Bluetooth");
	public static string DeviceNotConnected(Language? l = null) => Pick(l, $"Bağlı değil", $"Not connected");
	public static string DeviceNoController(Language? l = null) => Pick(l, $"Kumanda bulunamadı", $"No controller found");
	public static string BatteryCharging(double percent, Language? l = null) => Pick(l, $"Şarj oluyor %{percent:F0}", $"Charging {percent:F0}%");
	public static string BatteryUnknown(Language? l = null) => Pick(l, $"Pil bilgisi yok", $"Battery unknown");

	// Overview
	public static string OverviewSubtitle(Language? l = null) => Pick(l,
		$"Başlatınca oyunlar Bluetooth kumandayı kablolu kumanda olarak görür.",
		$"When started, games see the Bluetooth controller as a wired one.");
	public static string CardConnection(Language? l = null) => Pick(l, $"Bağlantı", $"Connection");
	public static string CardLatency(Language? l = null) => Pick(l, $"Gecikme / giriş hızı", $"Latency / input rate");
	public static string CardBattery(Language? l = null) => Pick(l, $"Pil", $"Battery");
	public static string CardHaptics(Language? l = null) => Pick(l, $"Haptik", $"Haptics");
	public static string ConnectionBluetooth(Language? l = null) => Pick(l, $"Bağlı", $"Connected");
	public static string InputRate(double rate, Language? l = null) => Pick(l, $"{rate:F0} rapor/sn", $"{rate:F0} reports/s");
	public static string BatteryValue(double percent, Language? l = null) => Pick(l, $"%{percent:F0}", $"{percent:F0}%");
	public static string Charging(Language? l = null) => Pick(l, $"Şarj oluyor", $"Charging");
	public static string OnBattery(Language? l = null) => Pick(l, $"Pilde", $"On battery");
	public static string HapticsActive(Language? l = null) => Pick(l, $"Etkin", $"Active");
	public static string HapticsIdle(Language? l = null) => Pick(l, $"Beklemede", $"Idle");
	public static string HapticsFromGame(Language? l = null) => Pick(l, $"Oyunun kendi haptiği", $"The game's own haptics");
	public static string BridgeStart(Language? l = null) => Pick(l, $"Köprüyü başlat", $"Start bridge");
	public static string BridgeStop(Language? l = null) => Pick(l, $"Köprüyü durdur", $"Stop bridge");
	public static string BridgeStatusRunning(Language? l = null) => Pick(l, $"Köprü çalışıyor: oyunlar kablolu kumanda görüyor.", $"Bridge is running: games see a wired controller.");
	public static string BridgeStatusStopped(Language? l = null) => Pick(l, $"Köprü kapalı.", $"Bridge is off.");
	public static string QuickTests(Language? l = null) => Pick(l, $"Hızlı testler", $"Quick tests");
	public static string QuickSettings(Language? l = null) => Pick(l, $"Hızlı ayarlar", $"Quick settings");
	public static string TestHapticsLeft(Language? l = null) => Pick(l, $"Sol haptik", $"Left haptics");
	public static string TestHapticsRight(Language? l = null) => Pick(l, $"Sağ haptik", $"Right haptics");
	public static string TestTrigger(Language? l = null) => Pick(l, $"Tetik", $"Trigger");
	public static string TestLight(Language? l = null) => Pick(l, $"Işık", $"Light");
	public static string GameHapticsLevel(Language? l = null) => Pick(l, $"Oyun haptiği", $"Game haptics");
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
	public static string TestBoth(Language? l = null) => Pick(l, $"İki taraf", $"Both sides");
	public static string TestRumble(Language? l = null) => Pick(l, $"Titreşimi dene", $"Try rumble");
	public static string Tests(Language? l = null) => Pick(l, $"Testler", $"Tests");

	// Triggers
	public static string TriggersIntro(Language? l = null) => Pick(l,
		$"Uyarlanabilir tetikler oyunun gönderdiği efektleri uygular.",
		$"Adaptive triggers apply the effects the game sends.");
	public static string TriggerStrength(Language? l = null) => Pick(l, $"Tetik gücü", $"Trigger strength");
	public static string TriggerStrengthHint(Language? l = null) => Pick(l,
		$"Desteklenen oyunların tetik efektlerinin ve test efektlerinin gücü. %0 tetikleri kapatır.",
		$"Strength of supported games' trigger effects and of the test effects. 0% turns the triggers off.");
	public static string TriggerTestEffects(Language? l = null) => Pick(l, $"Test efektleri", $"Test effects");
	public static string EffectOff(Language? l = null) => Pick(l, $"Kapalı", $"Off");
	public static string EffectFeedback(Language? l = null) => Pick(l, $"Direnç", $"Feedback");
	public static string EffectWeapon(Language? l = null) => Pick(l, $"Silah", $"Weapon");
	public static string EffectVibration(Language? l = null) => Pick(l, $"Titreşim", $"Vibration");

	// Light
	public static string LightIntro(Language? l = null) => Pick(l,
		$"Bir oyun kendi rengini gönderirse o renk kullanılır.",
		$"If a game sends its own colour, that colour is used.");
	public static string LightbarColour(Language? l = null) => Pick(l, $"Işık çubuğu rengi", $"Light bar colour");
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
	public static string LanguageFollowSystem(Language? l = null) => Pick(l, $"Sistemi izle", $"Follow system");
	public static string StartWithWindows(Language? l = null) => Pick(l, $"Windows ile başlat", $"Start with Windows");
	public static string StartWithWindowsHint(Language? l = null) => Pick(l, $"Oturum açınca pencere açmadan, tepside başlar.", $"Starts in the tray when you sign in, without opening the window.");
	public static string IdlePowerOff(Language? l = null) => Pick(l, $"Boşta kapatma", $"Idle power-off");
	public static string IdlePowerOffHint(Language? l = null) => Pick(l,
		$"Kumanda bu kadar dakika kullanılmazsa kapanır (0 = asla).",
		$"Turns the controller off after this many idle minutes (0 = never).");
	public static string Minutes(double minutes, Language? l = null) => Pick(l, $"{minutes:F0} dk", $"{minutes:F0} min");
	public static string Never(Language? l = null) => Pick(l, $"Asla", $"Never");
	public static string General(Language? l = null) => Pick(l, $"Genel", $"General");
	public static string Tools(Language? l = null) => Pick(l, $"Araçlar", $"Tools");
	public static string RunSetupWizard(Language? l = null) => Pick(l, $"Kurulum sihirbazını yeniden çalıştır", $"Run setup wizard again");
	public static string TestBluetoothAdapter(Language? l = null) => Pick(l, $"Bluetooth adaptörünü test et", $"Test Bluetooth adapter");
	public static string ExportDiagnostics(Language? l = null) => Pick(l, $"Tanılama bilgisini dışa aktar", $"Export diagnostics");

	// Redesigned UI: bridge state, stats, cards and setting rows
	public static string BridgeStateStopped(Language? l = null) => Pick(l, $"Durdu", $"Stopped");
	public static string BridgeStateWaiting(Language? l = null) => Pick(l, $"Oyun bekleniyor", $"Waiting for a game");
	public static string BridgeStateRunning(Language? l = null) => Pick(l, $"Çalışıyor", $"Running");
	public static string BridgeHeadlineStopped(Language? l = null) => Pick(l, $"Köprü kapalı", $"Bridge is off");
	public static string BridgeHeadlineWaiting(Language? l = null) => Pick(l, $"Köprü hazır", $"Bridge is ready");
	public static string BridgeHeadlineRunning(Language? l = null) => Pick(l, $"Köprü çalışıyor", $"Bridge is running");
	public static string BridgeDetailWaiting(Language? l = null) => Pick(l,
		$"Sanal kablolu kumanda takıldı. Bir oyun başlatın.",
		$"The virtual wired controller is plugged in. Start a game.");
	public static string BridgeDetailRunning(Language? l = null) => Pick(l,
		$"Oyun kablolu kumanda görüyor; haptik ve tetikler aktarılıyor.",
		$"The game sees a wired controller; haptics and triggers are relayed.");
	public static string StatLatency(Language? l = null) => Pick(l, $"Gecikme", $"Latency");
	public static string StatInputRate(Language? l = null) => Pick(l, $"Giriş hızı", $"Input rate");
	public static string LiveOutput(Language? l = null) => Pick(l, $"Canlı haptik çıkışı", $"Live haptics output");
	public static string LeftShort(Language? l = null) => Pick(l, $"Sol", $"Left");
	public static string RightShort(Language? l = null) => Pick(l, $"Sağ", $"Right");
	public static string EffectOffHint(Language? l = null) => Pick(l, $"Direnç yok, serbest tetik", $"No resistance, free travel");
	public static string EffectFeedbackHint(Language? l = null) => Pick(l, $"Bir noktadan sonra sabit direnç", $"Steady resistance past a point");
	public static string EffectWeaponHint(Language? l = null) => Pick(l, $"Artan direnç, sonra kırılma", $"Builds up, then snaps");
	public static string EffectVibrationHint(Language? l = null) => Pick(l, $"Basılıyken titreşir", $"Pulses while pressed");
	public static string LanguageHint(Language? l = null) => Pick(l, $"Arayüzün dili.", $"The language of the interface.");
	public static string RunSetupWizardHint(Language? l = null) => Pick(l,
		$"Sürücüleri ve eşleştirmeyi baştan kontrol eder.",
		$"Checks drivers and pairing from the start.");
	public static string TestBluetoothAdapterHint(Language? l = null) => Pick(l,
		$"Adaptörün haptik için yeterli olup olmadığını ölçer.",
		$"Measures whether the adapter keeps up with haptics.");
	public static string ExportDiagnosticsHint(Language? l = null) => Pick(l,
		$"Sorun bildirmek için bir rapor dosyası oluşturur.",
		$"Creates a report file to attach to bug reports.");
	public static string ActionRun(Language? l = null) => Pick(l, $"Çalıştır", $"Run");
	public static string ActionTest(Language? l = null) => Pick(l, $"Test et", $"Test");
	public static string ActionExport(Language? l = null) => Pick(l, $"Dışa aktar", $"Export");

	// Real controller service
	public static string TestNoController(Language? l = null) => Pick(l,
		$"Test için kumanda bulunamadı. Kumandanın Bluetooth ile bağlı olduğundan emin olun.",
		$"No controller found for the test. Make sure the controller is connected via Bluetooth.");
	public static string AdapterTestRunning(Language? l = null) => Pick(l, $"Adaptör test ediliyor, yaklaşık 10 saniye sürer…", $"Testing the adapter, this takes about 10 seconds…");
	public static string AdapterTestNoData(Language? l = null) => Pick(l,
		$"Ölçüm yapılamadı: kumanda bulunamadı ya da yeterli rapor gelmedi.",
		$"Could not measure: no controller found or too few reports arrived.");

	// Setup wizard page
	public static string SetupPageTitle(Language? l = null) => Pick(l, $"Kurulum", $"Setup");
	public static string SetupPageIntro(Language? l = null) => Pick(l,
		$"DS5 Untethered'ın çalışması için gerekenler. Eksik adımları tamamlayıp yeniden kontrol edin.",
		$"What DS5 Untethered needs to work. Complete the missing steps, then check again.");
	public static string SetupStatusDone(Language? l = null) => Pick(l, $"Tamam", $"Done");
	public static string SetupStatusNeeded(Language? l = null) => Pick(l, $"Gerekli", $"Needed");
	public static string SetupStatusWarning(Language? l = null) => Pick(l, $"Dikkat", $"Check");
	public static string SetupClose(Language? l = null) => Pick(l, $"Kapat", $"Close");

	// Overview dashboard
	public static string OverviewIntro(Language? l = null) => Pick(l,
		$"Kumandanın, köprünün ve ayarların anlık durumu.",
		$"At-a-glance status of your controller, the bridge and settings.");
	public static string OverallOk(Language? l = null) => Pick(l, $"Her şey yolunda", $"All systems normal");
	public static string OverallAttention(Language? l = null) => Pick(l, $"Dikkat gerekiyor", $"Needs attention");
	public static string OverallNoController(Language? l = null) => Pick(l, $"Kumanda bağlı değil", $"No controller connected");
	public static string CardBridge(Language? l = null) => Pick(l, $"Köprü", $"Bridge");
	public static string CardController(Language? l = null) => Pick(l, $"Kumanda", $"Controller");
	public static string RowLink(Language? l = null) => Pick(l, $"Bluetooth", $"Link");
	public static string RowStatus(Language? l = null) => Pick(l, $"Durum", $"Status");
	public static string RowGame(Language? l = null) => Pick(l, $"Oyun", $"Game");
	public static string RowMode(Language? l = null) => Pick(l, $"Mod", $"Mode");
	public static string RowVirtualPad(Language? l = null) => Pick(l, $"Sanal kumanda", $"Virtual pad");
	public static string RowModel(Language? l = null) => Pick(l, $"Tür", $"Model");
	public static string RowPower(Language? l = null) => Pick(l, $"Güç", $"Power");
	public static string ValuePluggedIn(Language? l = null) => Pick(l, $"Takılı", $"Plugged in");
	public static string ValueUnplugged(Language? l = null) => Pick(l, $"Takılı değil", $"Not plugged in");
	public static string ValueGameConnected(Language? l = null) => Pick(l, $"Bağlandı", $"Connected");
	public static string ValueNoGame(Language? l = null) => Pick(l, $"Yok", $"None");
	public static string QuickActions(Language? l = null) => Pick(l, $"Hızlı işlemler", $"Quick actions");
	public static string QuickControls(Language? l = null) => Pick(l, $"Hızlı kontroller", $"Quick controls");
	public static string GroupStartup(Language? l = null) => Pick(l, $"Başlangıç", $"Startup");
	public static string GroupPowerSaving(Language? l = null) => Pick(l, $"Güç tasarrufu", $"Power saving");

	// Game modes: supported (native DualSense) and other games
	public static string SupportedGamesIntro(Language? l = null) => Pick(l,
		$"DualSense'i doğrudan destekleyen oyunlar kendi haptiğini, tetik efektlerini ve ışığını gönderir.",
		$"Games that support the DualSense natively send their own haptics, trigger effects and light.");
	public static string OtherGamesIntro(Language? l = null) => Pick(l,
		$"Yalnızca klasik titreşim gönderen oyunlar. Titreşim haptik olarak çalınır, tetiklere yedek efekt verilebilir.",
		$"Games that only send classic rumble. Rumble is played as haptics, and the triggers can get a fallback effect.");
	public static string ProfileSettings(Language? l = null) => Pick(l, $"Bu oyunlar için ayarlar", $"Settings for these games");
	public static string LightFromGame(Language? l = null) => Pick(l, $"Oyunun ışığını kullan", $"Use the game's light");
	public static string LightFromGameHint(Language? l = null) => Pick(l,
		$"Kapalıyken ışık çubuğu sizin renginizde kalır.",
		$"When off, the light bar keeps your colour.");
	public static string SupportedGameList(Language? l = null) => Pick(l, $"Desteklenen oyun listesi", $"Supported games list");
	public static string SearchGames(Language? l = null) => Pick(l, $"Oyun ara", $"Search games");
	public static string GameCount(double count, Language? l = null) => Pick(l, $"{count:F0} oyun", $"{count:F0} games");
	public static string NoMatchingGames(Language? l = null) => Pick(l, $"Eşleşen oyun yok.", $"No matching games.");
	public static string BadgeHaptics(Language? l = null) => Pick(l, $"Haptik", $"Haptics");
	public static string BadgeTriggers(Language? l = null) => Pick(l, $"Tetik", $"Triggers");
	public static string DetectedHere(Language? l = null) => Pick(l, $"Bu bilgisayarda algılandı", $"Detected on this PC");
	public static string OverrideAuto(Language? l = null) => Pick(l, $"Otomatik", $"Auto");
	public static string OverrideSupported(Language? l = null) => Pick(l, $"Destekli", $"Supported");
	public static string OverrideUnsupported(Language? l = null) => Pick(l, $"Desteksiz", $"Unsupported");
	public static string OverrideHint(Language? l = null) => Pick(l,
		$"Otomatik: listeye ve oyunun gönderdiklerine göre seçilir.",
		$"Auto: chosen from the list and from what the game sends.");
	public static string ModeSupported(Language? l = null) => Pick(l, $"DualSense destekli", $"DualSense supported");
	public static string ModeUnsupported(Language? l = null) => Pick(l, $"Klasik titreşim", $"Classic rumble");
	public static string ModeShortClassic(Language? l = null) => Pick(l, $"Klasik", $"Classic");
	public static string ReasonCatalog(Language? l = null) => Pick(l, $"listeden", $"from the list");
	public static string ReasonDetected(Language? l = null) => Pick(l, $"algılandı", $"detected");
	public static string ReasonUser(Language? l = null) => Pick(l, $"sizin seçiminiz", $"your choice");
	public static string ReasonDefault(Language? l = null) => Pick(l, $"varsayılan", $"default");
	public static string NoGameYet(Language? l = null) => Pick(l, $"Henüz oyun algılanmadı", $"No game detected yet");
	public static string ActiveNow(Language? l = null) => Pick(l, $"Şu an etkin", $"Active now");
	public static string RumbleFeelLabel(Language? l = null) => Pick(l, $"Titreşim hissi", $"Rumble feel");
	public static string RumbleFeelSoft(Language? l = null) => Pick(l, $"Yumuşak", $"Soft");
	public static string RumbleFeelPunchy(Language? l = null) => Pick(l, $"Sert", $"Punchy");
	public static string RumbleFeelHint(Language? l = null) => Pick(l,
		$"Yumuşak: pes, akıcı tonlar. Sert: daha tiz, kısa ve keskin vuruşlar.",
		$"Soft: low, smooth tones. Punchy: higher, shorter, sharper hits.");
	public static string FallbackTriggerLabel(Language? l = null) => Pick(l, $"Yedek tetik efekti", $"Fallback trigger effect");
	public static string FallbackTriggerHint(Language? l = null) => Pick(l,
		$"Oyun tetik efekti göndermediği için L2 ve R2'ye uygulanır.",
		$"Applied to L2 and R2, since the game sends no trigger effects.");
	public static string FallbackStrength(Language? l = null) => Pick(l, $"Efekt gücü", $"Effect strength");
	public static string TestFallbackTrigger(Language? l = null) => Pick(l, $"Tetik efektini dene", $"Try trigger effect");

	// Sticks & triggers
	public static string NavSticks(Language? l = null) => Pick(l, $"Analog ve tetikler", $"Sticks & triggers");
	public static string SticksIntro(Language? l = null) => Pick(l,
		$"Ölü bölgeler, tepki eğrisi ve tetik aralığı oyuna giden girdiye hemen uygulanır.",
		$"Deadzones, response curve and trigger range apply to the input games receive right away.");
	public static string LeftStick(Language? l = null) => Pick(l, $"Sol çubuk", $"Left stick");
	public static string RightStick(Language? l = null) => Pick(l, $"Sağ çubuk", $"Right stick");
	public static string StickPreviewHint(Language? l = null) => Pick(l,
		$"Gri nokta çubuğun ham konumu, mavi nokta oyuna giden konum.",
		$"The grey dot is the raw stick position, the blue dot is what the game receives.");
	public static string LivePreviewNeedsBridge(Language? l = null) => Pick(l,
		$"Canlı önizleme köprü çalışırken görünür.",
		$"The live preview shows while the bridge is running.");
	public static string DriftCalibration(Language? l = null) => Pick(l, $"Çubuk kayması kalibrasyonu", $"Stick drift calibration");
	public static string DriftCalibrationHint(Language? l = null) => Pick(l,
		$"Kumandayı düz bir yere bırakın ve çubuklara 3 saniye dokunmayın. Merkez kayması düzeltilir, gerekirse iç ölü bölge büyütülür.",
		$"Put the controller down and don't touch the sticks for 3 seconds. The center offset is corrected and the inner deadzone raised if needed.");
	public static string CalibrateAction(Language? l = null) => Pick(l, $"Kalibre et", $"Calibrate");
	public static string TriggerRange(Language? l = null) => Pick(l, $"Tetik aralığı", $"Trigger range");
	public static string TriggerRangeHint(Language? l = null) => Pick(l,
		$"Başlangıcın altındaki basışlar yok sayılır; sona ulaşınca tetik tam basılmış sayılır.",
		$"Presses below the start are ignored; at the end the trigger counts as fully pressed.");
	public static string ResetToDefault(Language? l = null) => Pick(l, $"Varsayılana dön", $"Reset to default");

	// Power, latency, diagnostics
	public static string PowerOffWhenIdleHint(Language? l = null) => Pick(l,
		$"Açıkken, boşta kapatma süresi dolunca kumanda da kapatılır.",
		$"When on, the controller is also turned off once the idle time has passed.");
	public static string TurnOffController(Language? l = null) => Pick(l, $"Kumandayı kapat", $"Turn off controller");
	public static string PowerOffRunning(Language? l = null) => Pick(l, $"Kumanda kapatılıyor…", $"Turning off the controller…");
	public static string LatencyHint(Language? l = null) => Pick(l,
		$"Bluetooth bağlantısının ek gecikmesi: ortanca / en kötü %1. Köprü çalışırken ölçülür.",
		$"Extra delay of the Bluetooth link: median / worst 1%. Measured while the bridge runs.");
	public static string DiagnosticsRunning(Language? l = null) => Pick(l, $"Tanılama dosyası hazırlanıyor…", $"Preparing the diagnostics file…");

	// Notifications (toasts) and the tray icon
	public static string NotifyConnectedTitle(Language? l = null) => Pick(l, $"Kumanda bağlandı", $"Controller connected");
	public static string NotifyConnectedBody(string name, Language? l = null) => Pick(l, $"{name} Bluetooth ile bağlı.", $"{name} is connected via Bluetooth.");
	public static string NotifyConnectedBattery(string name, double percent, Language? l = null) => Pick(l, $"{name} · pil %{percent:F0}", $"{name} · battery {percent:F0}%");
	public static string NotifyDisconnectedTitle(Language? l = null) => Pick(l, $"Kumanda bağlantısı kesildi", $"Controller disconnected");
	public static string NotifyDisconnectedBody(Language? l = null) => Pick(l, $"Kumanda yeniden açılınca köprü onu bulur.", $"The bridge finds it again when it is turned back on.");
	public static string NotifyBatteryLowTitle(Language? l = null) => Pick(l, $"Pil azalıyor", $"Battery low");
	public static string NotifyBatteryLowBody(double percent, Language? l = null) => Pick(l, $"%{percent:F0} kaldı. Kumandayı şarja takın.", $"{percent:F0}% left. Plug the controller in to charge.");
	public static string NotifyBatteryCriticalTitle(Language? l = null) => Pick(l, $"Pil çok az", $"Battery very low");
	public static string NotifyBatteryCriticalBody(double percent, Language? l = null) => Pick(l, $"%{percent:F0} kaldı; kumanda yakında kapanabilir.", $"{percent:F0}% left; the controller may turn off soon.");
	public static string NotifyChargedTitle(Language? l = null) => Pick(l, $"Şarj tamamlandı", $"Charging complete");
	public static string NotifyChargedBody(Language? l = null) => Pick(l, $"Kumanda tam dolu, kabloyu çıkarabilirsiniz.", $"The controller is full; you can unplug it.");
	public static string NotifyGameTitle(Language? l = null) => Pick(l, $"Oyun algılandı", $"Game detected");
	public static string NotifyGameSupported(string game, Language? l = null) => Pick(l, $"{game} · DualSense destekli", $"{game} · DualSense supported");
	public static string NotifyGameClassic(string game, Language? l = null) => Pick(l, $"{game} · klasik titreşim", $"{game} · classic rumble");
	public static string NotifySteamInputTitle(Language? l = null) => Pick(l, $"Steam Input açık", $"Steam Input is on");
	public static string NotifySteamInputBody(Language? l = null) => Pick(l, $"Haptik ve tetikler için Steam'de kapatın.", $"Turn it off in Steam for haptics and triggers.");
	public static string NotifyPoweredOffTitle(Language? l = null) => Pick(l, $"Kumanda kapatıldı", $"Controller turned off");
	public static string NotifyPoweredOffBody(Language? l = null) => Pick(l, $"Bir süre kullanılmadığı için pil tasarrufuyla kapandı.", $"Turned off to save battery after a while without use.");
	public static string NotifyBridgeErrorTitle(Language? l = null) => Pick(l, $"Köprü hatası", $"Bridge error");
	public static string NotifyHidHideTitle(Language? l = null) => Pick(l, $"HidHide kurulu değil", $"HidHide is not installed");
	public static string NotifyHidHideBody(Language? l = null) => Pick(l, $"Oyunlar kumandayı iki kez görebilir. Sihirbazdan kurun.", $"Games may see the controller twice. Install it from setup.");
	public static string NotifyTrayTitle(Language? l = null) => Pick(l, $"Tepside çalışmaya devam ediyor", $"Still running in the tray");
	public static string NotifyTrayBody(Language? l = null) => Pick(l, $"Çıkmak için tepsi simgesine sağ tıklayın.", $"Right-click the tray icon to exit.");
	public static string NotifyClose(Language? l = null) => Pick(l, $"Bildirimi kapat", $"Dismiss");
	public static string GroupNotifications(Language? l = null) => Pick(l, $"Bildirimler", $"Notifications");
	public static string NotificationsShow(Language? l = null) => Pick(l, $"Bildirimleri göster", $"Show notifications");
	public static string NotificationsShowHint(Language? l = null) => Pick(l, $"Ekranın sağ altında kısa bilgi kartları.", $"Small cards in the bottom-right corner of the screen.");
	public static string NotifyConnectionSetting(Language? l = null) => Pick(l, $"Bağlantı", $"Connection");
	public static string NotifyConnectionHint(Language? l = null) => Pick(l, $"Kumanda bağlanınca ve bağlantısı kesilince.", $"When the controller connects or disconnects.");
	public static string NotifyBatterySetting(Language? l = null) => Pick(l, $"Pil", $"Battery");
	public static string NotifyBatteryHint(Language? l = null) => Pick(l, $"%20 ve %10'da birer kez, şarj bitince.", $"Once at 20% and 10%, and when charging completes.");
	public static string NotifyGameSetting(Language? l = null) => Pick(l, $"Oyun algılama", $"Game detection");
	public static string NotifyGameHint(Language? l = null) => Pick(l, $"Yeni bir oyun ve modu algılanınca.", $"When a new game and its mode are detected.");
	public static string NotifySteamInputSetting(Language? l = null) => Pick(l, $"Steam Input uyarısı", $"Steam Input warning");
	public static string NotifySteamInputHint(Language? l = null) => Pick(l, $"Oyun oturumu başına bir kez.", $"Once per game session.");
	public static string NotifyPowerOffSetting(Language? l = null) => Pick(l, $"Boşta kapatma", $"Idle power-off");
	public static string NotifyPowerOffHint(Language? l = null) => Pick(l, $"Kumanda kullanılmadığı için kapatılınca.", $"When the controller is turned off for being idle.");
	public static string NotifyErrorsSetting(Language? l = null) => Pick(l, $"Köprü hataları", $"Bridge errors");
	public static string NotifyErrorsHint(Language? l = null) => Pick(l, $"Köprü bir sorunla durunca, nedeniyle birlikte.", $"When the bridge stops on a problem, with the reason.");
	public static string NotifyHidHideSetting(Language? l = null) => Pick(l, $"HidHide eksik", $"HidHide missing");
	public static string NotifyHidHideHint(Language? l = null) => Pick(l, $"Bir kez, köprü HidHide olmadan başlarsa.", $"Once, if the bridge starts without HidHide.");
	public static string QuietWhilePlaying(Language? l = null) => Pick(l, $"Oyun sırasında bildirim gösterme", $"Don't notify while playing");
	public static string QuietWhilePlayingHint(Language? l = null) => Pick(l, $"Bir oyun bağlıyken ya da tam ekrandayken sessiz kalır.", $"Stays quiet while a game is connected or full screen.");
	public static string KeepInTray(Language? l = null) => Pick(l, $"Kapatınca tepside çalış", $"Keep running in tray");
	public static string KeepInTrayHint(Language? l = null) => Pick(l, $"Pencereyi kapatmak köprüyü durdurmaz.", $"Closing the window doesn't stop the bridge.");
	public static string TrayOpen(Language? l = null) => Pick(l, $"Aç", $"Open");
	public static string TrayNotificationsOff(Language? l = null) => Pick(l, $"Bildirimleri kapat", $"Turn off notifications");
	public static string TrayNotificationsOn(Language? l = null) => Pick(l, $"Bildirimleri aç", $"Turn on notifications");
	public static string TrayExit(Language? l = null) => Pick(l, $"Çıkış", $"Exit");
	public static string TrayNoController(Language? l = null) => Pick(l, $"Kumanda yok", $"No controller");
	public static string TrayBattery(double percent, Language? l = null) => Pick(l, $"Pil %{percent:F0}", $"Battery {percent:F0}%");
}
