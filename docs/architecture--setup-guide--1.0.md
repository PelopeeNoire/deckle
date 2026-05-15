# Architecture — First-run setup wizard

Doc canonique du wizard de setup affiché au premier lancement (et sur
demande depuis Settings). Couvre les décisions tranchées, la structure
UX, les composants WinUI 3, l'organisation du code, et les anti-patterns.

Lié à
[reference--dependencies--1.0.md](reference--dependencies--1.0.md) qui
inventorie *quoi* installer ; cette page-ci couvre *comment*
l'utilisateur le fait.

---

## Context

Deckle ne peut pas transcrire sans trois familles d'artefacts
post-install : un runtime natif whisper.cpp (8 DLLs, ~50 MB), un modèle
Whisper (~150 MB pour `base`, ~3 GB pour `large-v3`), et un VAD Silero
(~700 KB). Le binaire `Deckle.exe` ship vide de ces trois pièces — le
first-run wizard les provisionne sous `%LOCALAPPDATA%\Deckle\`.

Pas de mode dégradé : sans modèle on ne fait rien d'utile. Le wizard
est donc **bloquant** au premier launch, et accessible à la demande
depuis Settings une fois passé (pour swap de modèle, changement de
location, ou ré-import des natives).

---

## Décisions tranchées

- **Wizard linéaire bloquant** dans une `Window` Mica dédiée. Pas
  `ContentDialog` (trop léger pour > 3 étapes), pas `InfoBar` persistant
  (l'app serait semi-fonctionnelle), pas catalogue PowerToys (notre flow
  n'est pas exploratoire).
- **Frame stepper** comme conteneur des étapes. L'`Orchestrator` ViewModel
  + `ContentControl/DataTemplateSelector` de Dev Home est plus testable
  mais demande 3-4 VMs + un selector — disproportionné pour 3 pages.
  Refactor possible plus tard si le wizard grossit.
- **3 étapes** : Choix global → Installation en bloc → Résumé/erreurs.
  L'utilisateur fait tous ses choix avant de lancer une seule série de
  téléchargements ; les erreurs remontent à la fin, on ne propose pas
  de retry inline.
- **Auto-download des natives par défaut**, fallback Browse local.
  Le bundle est publié comme release GitHub Deckle taggée
  `native-vX.Y.Z` ; le wizard fait un GET non-authentifié sur l'asset.
  La référence du bundle vit dans `Setup/NativeRuntime.cs` :
  `CurrentBundle = NativeRuntimeBundle(Version, Url, Sha256, SizeBytes,
  DisplayName)`. Mode dégradé : si le bouton Browse pointe sur un
  dossier valide, l'utilisateur saute le download.
- **Encapsulation native forte** : un seul module `Setup/NativeRuntime`
  expose la connaissance des DLLs whisper. Le reste de l'app n'en sait
  rien. Idem pour les modèles via `Setup/SpeechModels`.
- **Source d'inspiration** : Dev Home `SetupFlow` pour la structure ;
  PowerToys `OobeWindow` pour les conventions visuelles (Mica, TitleBar
  Tall, drag region).

---

## Structure UX

Window 720×520 centrée, Mica, TitleBar Tall sans back button. `Grid` 3
rangées : header, body (Frame), footer fixe.

```
┌────────────────────────────────────────────────────────┐
│  Setup                                       _ ▢ ✕     │ ← TitleBar Tall
├────────────────────────────────────────────────────────┤
│  [Step title — h2]                                     │
│  [Step subtitle — body secondary]                      │
│                                                        │
│  [Frame body — varie par étape]                        │
│                                                        │
├────────────────────────────────────────────────────────┤
│  Cancel                       Back     Install         │ ← Footer fixe
└────────────────────────────────────────────────────────┘
```

### Étape 1 — Choices

Pattern combiné Dev Home `RepoConfigView` + VS Installer "Locations".
L'utilisateur fait *tous* ses choix sur une page : où installer + status
du runtime natif + quel modèle. Ces choix conditionnent la taille
totale, qui s'affiche en bas de page comme feedback continu.

```xaml
<StackPanel Spacing="20">

  <!-- Section 1 : Install location -->
  <controls:SettingsCard Header="Install location"
                          Description="Where the app stores its data" />

  <!-- Section 2 : Speech runtime -->
  <controls:SettingsCard Header="Speech runtime">
    <StackPanel Orientation="Horizontal" Spacing="8">
      <TextBlock Text="{x:Bind ViewModel.NativeStatus, Mode=OneWay}" />
      <Button Content="{x:Bind ViewModel.BrowseButtonLabel, Mode=OneWay}"
              Click="OnBrowseNativeClick" />
    </StackPanel>
  </controls:SettingsCard>

  <!-- Section 3 : Speech model -->
  <controls:SettingsCard Header="Speech model">
    <RadioButtons SelectedIndex="{x:Bind ViewModel.SelectedModelIndex, Mode=TwoWay}" />
  </controls:SettingsCard>

  <!-- Section 4 : Total estimate -->
  <InfoBar Severity="Informational"
           Message="{x:Bind ViewModel.TotalSizeText, Mode=OneWay}" />

</StackPanel>
```

Le runtime natif a trois états visuels :

| État               | Status text                | Bouton           |
|--------------------|----------------------------|------------------|
| Déjà installé      | `Installed`                | `Replace...`     |
| À auto-download    | `Will be downloaded (~XX MB)` | `Use local copy...` |
| Placeholder URL    | `Missing N file(s)`        | `Browse...`      |

Le 3ᵉ cas n'est qu'un filet pour les builds dev où le repo Deckle n'a
pas encore publié de release native — `BundleUrlIsPlaceholder` revient
à `true`, le gate Next force Browse.

Footer : Cancel | Back (disabled) | **Install** (accent).

### Étape 2 — Installing

Pattern Dev Home `LoadingView`. Tout est lancé d'un bloc à l'arrivée
sur la page. Pas d'interaction utilisateur en cours d'opération sauf
Cancel.

```
ProgressBar global (Maximum=3, Value=tasksDone)
"Step 2 of 3 — downloading Whisper large-v3..."

┌─────────────────────────────────────────┐
│  ✓  Whisper.cpp + Vulkan runtime        │
│     Done — 18 MB                        │
└─────────────────────────────────────────┘
┌─────────────────────────────────────────┐
│  ▒▒▒▒▒▒░░░░░  Whisper large-v3          │
│  1.2 GB / 3.0 GB · 40%                  │
└─────────────────────────────────────────┘
┌─────────────────────────────────────────┐
│  ⟳  Silero VAD                          │
│     Connecting...                       │
└─────────────────────────────────────────┘
```

Séquentiel : runtime natif → modèle Whisper → Silero VAD. Le runtime
est court (DL ~18 MB + extract), les modèles peuvent prendre des
minutes. Cancel = `CancellationTokenSource.Cancel()`, fichiers
`.partial` supprimés. `SHA-256` vérifié pour le bundle natif (hash
hardcodé dans `CurrentBundle.Sha256`) — pas pour les modèles
HuggingFace (pas de hash canonique côté upstream).

Footer : Cancel only. Back disabled, Install caché.

### Étape 3 — Summary

```
┌─────────────────────────────────────────┐
│  ✓  All set.                            │
│                                         │
│  Location :              %LOCALAPPDATA%\…│
│  Whisper.cpp + Vulkan :  installed      │
│  Whisper large-v3 :      installed (3.0 GB)│
│  Silero VAD :            installed      │
│                                         │
│  [Get started]                          │
└─────────────────────────────────────────┘
```

Si une erreur a eu lieu en étape 2 :

```
┌─────────────────────────────────────────┐
│  !  Some items could not be installed.  │
│                                         │
│  ✓ Whisper.cpp + Vulkan : installed     │
│  ✗ Whisper large-v3 : checksum failed   │
│                                         │
│  [Retry]   [Quit]                       │
└─────────────────────────────────────────┘
```

Pas de partial-success boot. Soit tout est OK et l'app boot, soit
l'utilisateur Retry l'étape 2 ou Quit.

---

## Composants WinUI 3 par rôle

| Rôle | Contrôle | Source canonique |
|---|---|---|
| Window racine | `Window` + `MicaBackdrop` | Dev Home `SetupFlowPage`, PowerToys `OobeWindow` |
| TitleBar | `Microsoft.UI.Xaml.Controls.TitleBar` (Tall, no back button) | Settings Win11 |
| Stepper | `Frame` | — V1 retenu volontairement plus simple que Dev Home |
| Footer | `Grid` 2-col + `Button` Back / `AccentButton` Next | Dev Home `SetupFlowNavigation` |
| Choix path | `TextBox(IsReadOnly=True)` + `Button` → `FolderPicker` | Dev Home `RepoConfigView`, VS Installer |
| Choix modèle | `RadioButtons` (contrôle composite WinUI 3) | Settings Win11 |
| Card de choix | `controls:SettingsCard` (CommunityToolkit) | Dev Home `MainPageView` |
| Total estimé | `InfoBar Severity=Informational` | MS Learn Notifications |
| Progress global | `ProgressBar` déterminé (Min=0, Max=3) | Dev Home `LoadingView` |
| Progress download | `ProgressBar` déterminé (Min=0, Max=ContentLength) | MS Learn Progress controls |
| Status par item | `TextBlock` Body / Caption + `TextFillColorSecondaryBrush` | Doctrine theme resources |
| Erreurs récap | `InfoBar Severity=Error` + `TextBlock` détail | MS Learn |
| Theme resources | `MicaBackdrop`, `OverlayCornerRadius`, `CardBackgroundFillColorDefaultBrush`, `TextFillColor*Brush` | Doctrine projet |

---

## Architecture code

Découpage en 3 couches verticales : modules métier (`Setup/`), shell UI
(`Shell/Setup/`), wire-up (`App.xaml.cs`).

### `Setup/` — modules métier (réutilisables)

Logique pure, pas de référence WinUI. Testable headless.

```
src/Deckle/Setup/
├── NativeRuntime.cs           # encapsule TOUTE la connaissance des DLLs whisper
│   ├── const string EntryDll
│   ├── IReadOnlyList<string> RequiredDllNames        (8 entries)
│   ├── record NativeRuntimeBundle                    (Version, Url, Sha256, SizeBytes, DisplayName)
│   ├── NativeRuntimeBundle CurrentBundle             (single source of truth)
│   ├── bool BundleUrlIsPlaceholder                   (gate the live behavior)
│   ├── bool IsInstalled()
│   ├── int CopyFromFolder(string source) → count
│   ├── Task<int> InstallFromZipAsync(string zipPath, CancellationToken)
│   └── IReadOnlyList<string> GetMissing()
├── SpeechModels.cs            # catalogue + résolution des modèles
│   ├── record ModelEntry(Id, FileName, Url, SizeBytes, Sha256?)
│   ├── IReadOnlyList<ModelEntry> WhisperModels
│   ├── ModelEntry VadModel
│   └── bool IsInstalled(ModelEntry)
├── Downloader.cs              # primitive HttpClient + IProgress + SHA-256 + .partial
└── SetupContext.cs            # état partagé entre les pages du wizard
    ├── string Location
    ├── ModelEntry SelectedModel
    └── List<InstallResult> Results
```

**Encapsulation native** : `NativeRuntime` est le **seul** module qui
nomme `libwhisper.dll`, `ggml-*.dll`, `libgcc_s_seh-1.dll`, etc. Tous
les autres consommateurs (wizard, settings, debug) passent par ses
méthodes publiques. Si demain on bascule sur une autre stack (Vulkan
→ DirectML, MinGW → MSVC), seul ce fichier change.

`NativeMethods.cs` (Interop/) reste isolé côté `[DllImport]` et
`SetDllImportResolver` — il connaît `"libwhisper"` comme identifiant
P/Invoke mais c'est `NativeRuntime` qui orchestre l'install.

Le bundle versionné est produit par `scripts/publish-native-runtime.ps1`
(maintainer-only) ; recette de recompilation dans
[reference--native-runtime--1.0.md](reference--native-runtime--1.0.md).

### `Shell/Setup/` — pages WinUI 3

Une fenêtre, trois pages, navigation par `Frame`.

```
src/Deckle/Shell/Setup/
├── SetupWindow.xaml{.cs}       # racine, Mica, Frame, footer Back/Next/Cancel
├── ChoicesPage.xaml{.cs}       # étape 1 — location + native + modèle + estimate
├── InstallingPage.xaml{.cs}    # étape 2 — ProgressBar global + 3 items
└── SummaryPage.xaml{.cs}       # étape 3 — recap success ou erreurs
```

Chaque page reçoit le `SetupContext` partagé via
`Frame.Navigate(typeof(X), context)` + `OnNavigatedTo` qui le récupère
depuis `e.Parameter`. Les pages mutent le contexte (sélection user,
résultats install) ; la `SetupWindow` observe le contexte pour
activer/désactiver Next, ou conclure.

### Wire-up

```csharp
// App.OnLaunched (gate first-run)
if (!NativeRuntime.IsInstalled() || !SpeechModels.IsDefaultInstalled())
{
    var setup = new SetupWindow();
    setup.Body.Navigate(typeof(ChoicesPage), setup);
    setup.Activate();
    bool success = await setup.Completion;
    if (!success) { Environment.Exit(0); return; }
}

// Settings — bouton "Run setup again..."
private void OnReRunSetup(...) { new SetupWindow().Activate(); }
```

---

## Logging

Toute trace passe par `LogService.Instance` (facade vers
`TelemetryService.Instance`, cf.
[reference--logging-inventory--1.0.md](reference--logging-inventory--1.0.md)).
Source key dédiée `LogSource.Setup`.

Événements émis :

| Quand | Niveau | Message |
|---|---|---|
| Wizard ouvert | Info | `setup window opened \| reason={first-run\|user-request} \| missing_natives={n} \| missing_default_model={bool}` |
| Choix de modèle | Info | `setup choices confirmed \| location={...} \| model={id}` |
| Native auto-DL ok | Info | `setup native ok \| bundle=native-vX.Y.Z \| bytes={...} \| dur_ms={...} \| sha256={...}` |
| Native échec | Warning | `setup native download failed \| error={...}` |
| Native bundle incomplet | Error | `setup native incomplete \| extracted={n} \| expected={m}` |
| Native placeholder URL | Error | `setup native runtime aborted \| reason=placeholder_url` |
| Item modèle ok | Info | `setup item ok \| id={...} \| bytes={...} \| dur_ms={...} \| sha256={...}` |
| Item modèle échec | Warning | `setup item failed \| id={...} \| error={...}` |
| Cancel par user | Info | `setup item cancelled \| id={...}` |

Pas de `File.AppendAllText`, pas de `Console.WriteLine` parallèle.

---

## Anti-patterns à éviter

1. **`FolderPicker` inline** — pattern Dev Home/PowerToys = `TextBox(IsReadOnly) + Button`. L'inline embarrasse la composition de la page.
2. **`ProgressBar` indéterminé** quand le total est connu. HuggingFace renvoie `Content-Length` → bar déterminée + ratio bytes/total.
3. **`DesktopAcrylicBackdrop`** sur la fenêtre setup. Réservé aux transient (HUD, popups). Setup est persistante = `MicaBackdrop`.
4. **`NavigationView` left-pane** pour un wizard linéaire. Le pane suggère une nav libre, faux signal pour notre cas.
5. **`ContentDialog` sans `XamlRoot`** — crash WinUI 3.
6. **`Frame.GoBack()` avec history visible** — Back doit annuler le commit de l'étape précédente, pas naviguer dans une stack.
7. **UI Element créé hors thread UI** — `HttpClient` callbacks → `DispatcherQueue.TryEnqueue` pour toute mise à jour de Progress.
8. **Hardcoder des noms de DLL ailleurs que dans `Setup/NativeRuntime`** — viole l'encapsulation native. Le catalogue est dupliqué côté PowerShell (`scripts/setup-assets.ps1`, `scripts/publish-native-runtime.ps1`) avec un commentaire de traçabilité ; toute autre duplication est un bug.
9. **Hardcoder des `#xxxxxx` ou des `CornerRadius` numériques** dans la XAML. Theme resources only.
10. **Bypass de `LogService` / `TelemetryService`** pour logger l'install — pas de `File.AppendAllText` parallèle.
11. **Confondre `CopyFromFolder` et `InstallFromZipAsync`** — le premier est sync, lit un dossier dont l'utilisateur garantit le contenu (Browse) ; le second est async, lit un zip dont l'intégrité est garantie en amont par `Downloader.DownloadAsync` (SHA-256). Les deux convergent sur `NativeDirectory` mais ne sont pas interchangeables.

---

## Out of scope V1

Encore reportés explicitement :

- Téléchargement parallèle native + modèle (séquentiel suffit, le
  goulot c'est le modèle de toute façon).
- Reprise d'un download interrompu (le `.partial` est supprimé,
  l'user recommence depuis zéro).
- Vérification SHA-256 des modèles HuggingFace (pas de hash canonique
  publié — à ajouter quand on fixe les hashes côté upstream ou côté
  catalog).
- Migration auto des settings/télémétrie depuis l'ancien layout
  `<exe>/config/` (clean break, l'utilisateur recopie s'il veut).
- Sélection runtime de la langue dans Settings (override du
  ResourceContext) — V1 résout sur la langue d'affichage Windows
  uniquement.
