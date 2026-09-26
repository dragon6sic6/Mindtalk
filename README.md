# Mindtalk

Tala istället för att skriva. Håll in valfri tangent, prata, släpp – texten klistras in där
markören står, i vilket program som helst. Allt tolkas lokalt via FluidAudio på Neural Engine,
med den modell du väljer (byt med ett klick i menyraden):

| Språk | Modell | Storlek |
|-------|--------|---------|
| Svenska | **Klang Pianissimo** (Klang AI), vidareträning av NVIDIA Parakeet TDT 0.6B v3 | 688 MB |
| English + 24 språk | **Parakeet Ultra** (Moondream), vidareträning av samma v3 | 632 MB |

Båda CC BY 4.0. Ingen följer med appen – de laddas ned vid behov från en låst revision och varje
fil kontrolleras mot sin sha256 (`SpeechModel` i `SpeechModels.swift`). Krediter och länkar finns
under Inställningar → Om Mindtalk. Första starten visar en onboarding (välkommen → språk → behörigheter →
tangent → prova) med levande bakgrund, animerad logotyp och konfetti vid första dikteringen – lugn och
stilla med Minska rörelse påslaget. Den går att visa igen från menyraden.

- **Håll in** tangenten = push-to-talk. **Dubbeltryck** (eller mellanslag medan du håller in) = låst handsfree,
  tryck igen för att klistra in. **Esc** avbryter. Under Inställningar kan du i stället välja *Bara håll in* eller
  *Tryck för att starta och stoppa*.
- Ljust, mörkt eller system-utseende; valfritt ljud vid start/inklistring; valfri Dock-ikon.
- **Mikrofon:** inbyggd som standard (Bluetooth-headset tappar kvalitet när mikrofonen öppnas), eller
  Automatiskt/valfri enhet – med levande vågform och "hör vi dig?"-besked. Följer in- och urkoppling.
- **Textstädning (allt lokalt):** regler tar bort tvekljud (eh, öh, um) och gör "ny rad"/"nytt stycke"
  ("new line"/"new paragraph") till radbrytningar – men inte i "en ny rad i tabellen". Valfritt:
  **Putsa med AI** via Apple Intelligence på enheten (FoundationModels) rättar skiljetecken, upprepningar,
  uttalade skiljetecken och självrättelser ("tisdag, nej jag menar onsdag" → "onsdag"). Rad för rad,
  greedy, max 4 s, och svaret används bara om det enbart *tar bort* bokstäver ur originalet (samma
  ordning, minst halva texten kvar) – så AI:n kan aldrig svara på, lägga till eller skriva om det du sa.
- **Musik medan du dikterar** (Pausa / Tona ner / Låt vara): Core Audio visar vilka appar som spelar ljud
  (med bundle-ID). Mediaappar (Spotify, Musik, webbläsare …) pausas via MediaRemote och spelas igen först när
  deras ljud faktiskt tystnat (Spotify ≈ 2 s) – spelar inget skickas inget, så musik kan aldrig starta av sig själv.
  Annat ljud (samtal, spel) tonas ner mjukt och upp igen; volymen sparas och återställs även efter en krasch.
- **Ordlista:** namn stavas som du vill. Delade/felskrivna varianter ("Mind Talk") rättas automatiskt;
  egna varianter ("min dag" → Mindact) rättas bara om du lagt till dem.
- **Menyraden:** vänster- eller högerklick öppnar en panel i macOS egna menyspråk (som Wi-Fi/Ljud):
  status, dagens och veckans ord med sju små staplar, Språk (runda brickor, ifylld = vald, ⌘1/⌘2),
  Senaste (en rad var, tiden byts mot kopiera-ikon vid hover), Öppna Mindtalk, Inställningar … ⌘,,
  Avsluta ⌘Q. Esc eller klick utanför stänger. Om/licenser och "Visa introduktionen" finns i Inställningar.
- **Statistik:** ord per dag, sparad tid mot att skriva 40 ord/min, dagar i rad – bara på din Mac, aldrig texten.
- Valfri tangent: modifierare (höger ⌥ som standard, ⌘, ⌃, ⇧, fn) eller vanliga tangenter (F-tangenter, § …).
  Kortkommandon med tangenten (t.ex. ⌥2 → @) fungerar som vanligt.
- Ljudet ligger bara i minnet och kastas direkt efter tolkningen.

## Språk och ikon

- **Appens språk:** svenska och engelska via `Mindtalk/Resources/Localizable.xcstrings` (källspråk svenska).
  Följer datorns språk – allt utom svenska blir engelska – eller väljs under Inställningar → Appens språk
  (startar om appen). Ny text: skriv den på svenska i koden (`Text("…")`, `String(localized: "…")`),
  kör `make build` och sedan `python3 scripts/strings.py` – den hämtar in nya strängar, fyller i
  svenskan (krävs, annars faller en svensk Mac tillbaka på engelska) och listar vad som saknar engelska.
- **Färger:** svartvitt – svart accent i ljust läge, vit i mörkt (`DesignSystem.swift`, `AccentColor`).
  Rött (inspelning) och grönt (klart) används bara där de betyder något.
- **Märket:** "tal blir text" – en ljudvåg som planar ut till en textrad och slutar i en markör
  (`Sources/Mark.swift`). Samma geometri i appikonen, den levande logotypen och menyraden.
- **Ikon:** Icon Composer-format i `Mindtalk/Resources/AppIcon.icon` – svart gradient, två glaslager
  (våg + markör). macOS ritar Liquid Glass och de mörka/tonade varianterna själv.
  Lagren renderas med `scripts/make_icon_layers.swift`.

## Bygga

Kräver Xcode och XcodeGen (`brew install xcodegen`), Apple Silicon, macOS 14+.

```bash
make run       # Debug-bygge och start
make install   # Release-bygge till /Applications
make dmg       # Signerad + notariserad DMG i dist/ (scripts/release.sh)
```

Självtest av tangentlogiken (bara Debug – kör håll/tryck/dubbeltryck/mellanslag i alla lägen, klistrar inte in):

```bash
open -n -W --stdout /dev/stdout build/Build/Products/Debug/Mindtalk.app --args --simulate-keys
```

Självtest av taligenkänningen utan mikrofon (`--model swedish` eller `multilingual`):

```bash
say -v Alva -o /tmp/t.aiff "Hej, det här är ett test." && build/Build/Products/Debug/Mindtalk.app/Contents/MacOS/Mindtalk --transcribe /tmp/t.aiff --model swedish
# Med --clean visas även regler + AI-putsning; bara städningen: Mindtalk --clean "Eh, vi ses på på fredag ny rad tack"
```

Installera en modell från terminalen (samma kod som appen använder):

```bash
build/Build/Products/Debug/Mindtalk.app/Contents/MacOS/Mindtalk --install multilingual
```

## Filer

| Fil | Vad |
|-----|-----|
| `Sources/App.swift` | Menyradsikon, menyer, fönstret, Dock, inloggningsobjekt |
| `Sources/MainView.swift` | Fönstret: sidofält, Diktering, Senaste, Inställningar |
| `Sources/DesignSystem.swift` | Färger (ljust/mörkt), kort, chips, knappar, pillväljare, logotypen |
| `Sources/Mark.swift` | Märket: våg → textrad → markör |
| `Sources/Motion.swift` | Rörelse: ljusskenet, entréer, uppräkning, sidbyten |
| `Sources/Settings.swift` | Tangent, läge, utseende, Dock, ljud |
| `Sources/Microphones.swift`, `MicrophonePicker.swift` | Mikrofonval (Core Audio) och väljaren |
| `Sources/MediaControl.swift` | Pausa musik / tona ner ljud under diktering |
| `Sources/TextCleanup.swift` | Tvekljud, röstkommandon, AI-putsning med skyddsspärrar |
| `Sources/Vocabulary.swift`, `VocabularyPage.swift` | Ordlistan och dess sida |
| `Sources/Stats.swift`, `StatsCard.swift` | Statistik och veckodiagram |
| `Sources/StatusPanel.swift` | Menyradspanelen |
| `Sources/Dictation.swift` | Håll/dubbeltryck/lås-logik, inspelning → tolkning → inklistring |
| `Sources/Hotkey.swift` | Global tangentlyssnare (CGEvent tap) och tangentval |
| `Sources/SpeechModels.swift` | Modellerna: låsta filer, nedladdning, verifiering, körning |
| `Sources/OnboardingView.swift` | Första starten |
| `Sources/Recorder.swift` | Mikrofon → 16 kHz mono |
| `Sources/TextInserter.swift` | Klistrar in via ⌘V och återställer urklippet |
| `Sources/HUD.swift` | Den lilla pillern längst ned på skärmen |

Taligenkänning: "Klang Pianissimo" av Klang AI AB, CC BY 4.0.
