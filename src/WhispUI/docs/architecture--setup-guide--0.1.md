# Architecture — Setup guidé first-run

Structure UX et architecture code du wizard de setup affiché au premier
lancement (et sur demande depuis Settings). Couvre les décisions tranchées,
les composants WinUI 3 retenus, les anti-patterns, et le plan d'implémentation.

Lié à [reference--dependencies--0.1.md](reference--dependencies--0.1.md) qui
inventorie *quoi* installer ; ce doc-ci couvre *comment* l'utilisateur le
fait.

## Context

L'app (working title WhispUI, `<AppName>` final TBD) ne peut pas transcrire
sans deux artefacts post-install : un runtime natif whisper.cpp (8 DLLs,
~50 MB) et un modèle Whisper (~150 MB ou ~3 GB). Le binaire ship vide de
ces deux pièces — le first-run wizard les installe sous `<UserDataRoot>\`.

Pas de mode dégradé : sans modèle on ne fait rien d'utile. Le wizard est
donc **bloquant** au premier launch, et accessible à la demande depuis
Settings une fois passé (pour swap de modèle, changement de location).

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
  téléchargements ; les erreurs remontent à la fin, on ne propose pas de
  retry inline en V1.
- **Encapsulation native forte** : un seul module `Setup/NativeRuntime`
  expose la connaissance des DLLs whisper. Le reste de l'app n'en sait
  rien. Idem pour les modèles via `Setup/SpeechModels`.
- **Source d'inspiration directe** : Dev Home `SetupFlow` pour la
  structure ; PowerToys `OobeWindow` pour les conventions visuelles
  (Mica, TitleBar Tall, drag region).

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
L'utilisateur fait *tous* ses choix sur une page : où installer + quel
modèle. Ces deux choix conditionnent la taille totale, qui s'affiche en
bas de page comme feedback continu.

```xaml
<StackPanel Spacing="20">

  <!-- Section 1 : Install location -->
  <controls:SettingsCard Header="Install location"
                          Description="Where the app stores its data"
                          IconSource="...">
    <StackPanel Orientation="Horizontal" Spacing="8">
      <TextBox IsReadOnly="True"
               Text="{x:Bind ViewModel.LocationPath, Mode=OneWay}"
               Width="320" />
      <Button Content="Change..." Click="OnBrowseLocation"/>
    </StackPanel>
  </controls:SettingsCard>

  <!-- Section 2 : Speech model -->
  <controls:SettingsCard Header="Speech model"
                          Description="Pick the Whisper model the engine will run">
    <RadioButtons SelectedIndex="{x:Bind ViewModel.SelectedModelIndex, Mode=TwoWay}">
      <RadioButton Content="Whisper base — multilingual, fast (~150 MB)" />
      <RadioButton Content="Whisper large-v3 — multilingual, best (~3 GB)" />
    </RadioButtons>
  </controls:SettingsCard>

  <!-- Section 3 : Total estimate -->
  <InfoBar IsOpen="True" Severity="Informational"
           Title="Total install size"
           Message="{x:Bind ViewModel.TotalSizeText, Mode=OneWay}" />

</StackPanel>
```

Footer : Cancel | Back (disabled) | **Install** (accent).

### Étape 2 — Installing

Pattern Dev Home `LoadingView`. Tout est lancé d'un bloc à l'arrivée sur
la page. Pas d'interaction utilisateur en cours d'opération sauf Cancel.

```
ProgressBar global (Maximum=2, Value=tasksDone)
"Installing 1 of 2..."

┌─────────────────────────────────────────┐
│  ⟳  Native runtime                      │
│     Copying 8 DLLs...                   │
└─────────────────────────────────────────┘
┌─────────────────────────────────────────┐
│  ▒▒▒▒▒▒░░░░░  Whisper large-v3          │
│  1.2 GB / 3.0 GB · 40% · ETA 2m         │
└─────────────────────────────────────────┘
```

Séquentiel : natives d'abord (browse local = quasi-instantané), modèle
ensuite (download long). Cancel = `CancellationTokenSource.Cancel()`,
fichiers `.partial` supprimés.

Footer : Cancel only. Back disabled, Install caché.

### Étape 3 — Summary

```
┌─────────────────────────────────────────┐
│  ✓  All set.                            │
│                                         │
│  Location :         %LOCALAPPDATA%\...  │
│  Native runtime :   installed           │
│  Whisper large-v3 : installed (3.0 GB)  │
│                                         │
│  [Get started]                          │
└─────────────────────────────────────────┘
```

Si une erreur a eu lieu en étape 2 :

```
┌─────────────────────────────────────────┐
│  !  Some items could not be installed.  │
│                                         │
│  ✓ Native runtime : installed           │
│  ✗ Whisper large-v3 : checksum failed   │
│                                         │
│  [Retry]   [Quit]                       │
└─────────────────────────────────────────┘
```

V1 : pas de partial-success boot. Soit tout est OK et l'app boot, soit
l'utilisateur Retry l'étape 2 ou Quit.

Footer : pas de Back/Cancel. Seul `Get started` (success) ou `Retry`/
`Quit` (échec).

## Composants WinUI 3 par rôle

| Rôle | Contrôle | Source canonique |
|---|---|---|
| Window racine | `Window` + `MicaBackdrop` | Dev Home `SetupFlowPage`, PowerToys `OobeWindow` |
| TitleBar | `Microsoft.UI.Xaml.Controls.TitleBar` (Tall, no back button) | Settings Win11 |
| Stepper | `Frame` (V1 simple) | — V1 retenu volontairement plus simple que Dev Home |
| Footer | `Grid` 2-col + `Button` Back / `AccentButton` Next | Dev Home `SetupFlowNavigation` |
| Choix path | `TextBox(IsReadOnly=True)` + `Button "Change..."` → `FolderPicker` | Dev Home `RepoConfigView`, VS Installer |
| Choix modèle | `RadioButtons` (contrôle composite WinUI 3, pas `StackPanel` de `RadioButton`) | Settings Win11 |
| Card de choix | `controls:SettingsCard` (CommunityToolkit) | Dev Home `MainPageView` |
| Total estimé | `InfoBar Severity=Informational` (mis à jour OneWay) | MS Learn Notifications |
| Progress global | `ProgressBar` déterminé (Min=0, Max=NbTasks) | Dev Home `LoadingView` |
| Progress download | `ProgressBar` déterminé (Min=0, Max=ContentLength) | MS Learn Progress controls |
| Progress copie courte | `ProgressRing` indéterminé (animé) | Dev Home |
| Status par item | `TextBlock` Body / Caption + `TextFillColorSecondaryBrush` | Doctrine theme resources |
| Erreurs récap | `InfoBar Severity=Error` + `TextBlock` détail | MS Learn |
| Theme resources | `MicaBackdrop`, `OverlayCornerRadius`, `CardBackgroundFillColorDefaultBrush`, `TextFillColor*Brush` | Doctrine projet |

## Architecture code

Découpage en 3 couches verticales : modules métier (Setup/), shell UI
(Shell/Setup/), wire-up (App.xaml.cs).

### Setup/ — modules métier (réutilisables)

Logique pure, pas de référence WinUI. Testable headless.

```
src/WhispUI/Setup/
├── NativeRuntime.cs           # encapsule TOUTE la connaissance des DLLs whisper
│   ├── const string[] RequiredDllNames
│   ├── bool IsInstalled()
│   ├── int CopyFromFolder(string source) → count
│   └── (V2) Task<DownloadResult> DownloadFromRedistAsync(...)
├── SpeechModels.cs            # catalogue + résolution des modèles
│   ├── record ModelEntry(Id, FileName, Url, SizeBytes, Sha256?)
│   ├── IReadOnlyList<ModelEntry> All
│   ├── bool IsInstalled(ModelEntry)
│   └── Task<DownloadResult> DownloadAsync(ModelEntry, IProgress, CancellationToken)
├── Downloader.cs              # primitive HttpClient + IProgress + SHA-256 + .partial
└── SetupContext.cs            # état partagé entre les pages du wizard
    ├── string SelectedLocation
    ├── ModelEntry SelectedModel
    ├── List<InstallResult> Results
    └── Action<int> GoToStep      # navigation injectée par la Window
```

**Encapsulation native** : `NativeRuntime` est le **seul** module qui
nomme `libwhisper.dll`, `ggml-*.dll`, `libgcc_s_seh-1.dll`, etc. Tous
les autres consommateurs (wizard, settings, debug) passent par ses
méthodes publiques. Si demain on bascule sur une autre voix de stack
(Vulkan → DirectML, MinGW → MSVC), seul ce fichier change.

`NativeMethods.cs` (Interop/) reste isolé côté `[DllImport]` et
`SetDllImportResolver` — il connaît `"libwhisper"` comme identifiant
P/Invoke mais c'est `NativeRuntime` qui orchestre l'install.

### Shell/Setup/ — pages WinUI 3

Une fenêtre, trois pages, navigation par `Frame`.

```
src/WhispUI/Shell/Setup/
├── SetupWindow.xaml{.cs}       # racine, Mica, Frame, footer Back/Next/Cancel
├── ChoicesPage.xaml{.cs}       # étape 1 — location + modèle + estimate
├── InstallingPage.xaml{.cs}    # étape 2 — ProgressBar global + 2 items
└── SummaryPage.xaml{.cs}       # étape 3 — recap success ou erreurs
```

Chaque page reçoit le `SetupContext` partagé via `Frame.Navigate(typeof(X), context)`
+ `OnNavigatedTo` qui le récupère depuis `e.Parameter`. Les pages mutent
le contexte (sélection user, résultats install) ; la `SetupWindow`
observe le contexte pour activer/désactiver Next, ou conclure.

### Wire-up

```csharp
// App.OnLaunched (gate first-run)
if (!NativeRuntime.IsInstalled() || !SpeechModels.IsDefaultInstalled())
{
    var setup = new SetupWindow();
    setup.Activate();
    bool success = await setup.Completion;
    if (!success) { Environment.Exit(0); return; }
}

// Settings — bouton "Run setup again..."
private void OnReRunSetup(...) { new SetupWindow().Activate(); }
```

## Logging

Toute trace passe par `TelemetryService.Instance` (cf.
[reference--logging-inventory--0.1.md](reference--logging-inventory--0.1.md)).
Source key dédiée `LogSource.Setup` (à ajouter à `LogSource.cs`).

Événements à émettre :

| Quand | Niveau | Message |
|---|---|---|
| Wizard ouvert | Info | `setup window opened \| reason={first-run\|user-request} \| missing_natives={n} \| missing_default_model={bool}` |
| Choix de location | Info | `setup location chosen \| path={...} \| disk_free_gb={...}` |
| Choix de modèle | Info | `setup model chosen \| id={...} \| size_mb={...}` |
| Début install | Info | `setup install started \| location={...} \| items=[...]` |
| Item installé | Info | `setup item ok \| id={...} \| dur_ms={...} \| sha256={...}` |
| Item échoué | Warning | `setup item failed \| id={...} \| error={...}` |
| Cancel par user | Info | `setup cancelled by user \| at={step}` |
| Wizard finished | Info | `setup complete \| success={bool} \| dur_total_ms={...}` |

Pas de `File.AppendAllText`, pas de `Console.WriteLine` parallèle.

## Anti-patterns à éviter

1. **`FolderPicker` inline** — pattern Dev Home/PowerToys = `TextBox(IsReadOnly) + Button "Change..."`. L'inline embarrasse la composition de la page.
2. **`ProgressBar` indéterminé** quand le total est connu. HuggingFace renvoie `Content-Length` → bar déterminée + ratio bytes/total.
3. **`DesktopAcrylicBackdrop`** sur la fenêtre setup. Réservé aux transient (HUD, popups). Setup est persistante = `MicaBackdrop`.
4. **`NavigationView` left-pane** pour un wizard linéaire. Le pane suggère une nav libre, faux signal pour notre cas.
5. **`ContentDialog` sans `XamlRoot`** — crash WinUI 3.
6. **`Frame.GoBack()` avec history visible** — Back doit annuler le commit de l'étape précédente, pas naviguer dans une stack. En V1 simple : Back = retour à étape 1 sans préserver l'état d'étape 2 (qui n'existe pas encore puisqu'on y est).
7. **UI Element créé hors thread UI** — `HttpClient` callbacks → `DispatcherQueue.TryEnqueue` pour toute mise à jour de Progress.
8. **Hardcoder des noms de DLL ailleurs que dans `Setup/NativeRuntime`** — viole l'encapsulation native.
9. **Hardcoder des `#xxxxxx` ou des `CornerRadius` numériques** dans la XAML. Theme resources only.
10. **Bypass de `TelemetryService`** pour logger l'install — pas de `File.AppendAllText` parallèle.

## Plan d'implémentation par lots

À exécuter sur une branche dédiée `feature/setup-wizard` (worktree séparé).

- **B.1** — Couche métier `Setup/` : `NativeRuntime.cs`, `SpeechModels.cs`,
  `Downloader.cs`, `SetupContext.cs`. Tests unitaires possibles sans WinUI.
- **B.2** — `SetupWindow.xaml{.cs}` : shell, Mica, TitleBar, Frame, footer.
  Affiche une page placeholder pour valider le rendu.
- **B.3** — `ChoicesPage` (étape 1) : `SettingsCard` × 2 + `RadioButtons` +
  `InfoBar` total. Navigation vers étape 2.
- **B.4** — `InstallingPage` (étape 2) : `ProgressBar` global + items
  séquentiels, IProgress wiring depuis `NativeRuntime` et `SpeechModels`.
- **B.5** — `SummaryPage` (étape 3) : recap success ou erreurs avec Retry.
- **B.6** — Wire-up `App.OnLaunched` : gate sur `NativeRuntime.IsInstalled()
  && SpeechModels.IsDefaultInstalled()`. Async `OnLaunched`, await `Completion`.
- **B.7** — Settings : bouton "Run setup again..." qui réouvre le wizard
  (utile pour swap de modèle ou changement de location).
- **B.8** — Sweep telemetry : `LogSource.Setup` ajouté, événements émis
  selon la table ci-dessus. Vérification `app.jsonl` après run complet.

Branchement de la fenêtre dans `App.OnLaunched` est le point de contact
unique avec le reste de l'app — tout le reste vit dans `Setup/` et
`Shell/Setup/`.

## Out of scope V1

- Téléchargement parallèle natives + modèle (séquentiel suffit, natives
  sont quasi-instantané).
- Reprise d'un download interrompu (le `.partial` est supprimé, l'user
  recommence depuis zéro).
- Vérification SHA-256 des modèles HuggingFace (pas de hash canonique
  publié — à ajouter quand on fixe les hashes).
- Download des natives depuis un repo redist GitHub (browse local en
  attendant que le repo `<AppName>-redist` soit créé).
- Migration auto des settings/télémétrie depuis l'ancien layout
  `<exe>/config/` (clean break, l'utilisateur recopie s'il veut).
- Localisation : tous les strings sont en anglais, hardcodés. Migration
  vers `Resources.resw` au lot C (rename + wording).
