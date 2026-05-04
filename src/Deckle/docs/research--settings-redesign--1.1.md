# Research — refonte navigation Settings Deckle

Document de recherche pré-implémentation. **Version 1.1 — 2026-05-04**.

Mise à jour mineure depuis 1.0 :
- Clarification section Hotkeys : pas de renommage en « General hotkeys ».
  Section « Hotkeys », transcribe hotkey nommé **Principal hotkey** (le
  hotkey général qui active tout le module de transcription).
- Page extraite nommée **Diagnostics**, pas « Telemetry » ni « Privacy ».
  Telemetry devient une sous-section, en prévision de futures
  sous-sections (log settings temps réel notamment).
- Ajout §12 — checklist de validation visuelle WinUI 3 Gallery.
- Ajout §13 — plan d'implémentation par slices.
- Questions §9.1 résolue par décision user (Diagnostics) ; §9.2 / §9.3 /
  §9.4 actées sur recommandation par confiance.

Le doc canon
[reference--settings-architecture--1.0.md](reference--settings-architecture--1.0.md)
sera bumpé en 2.0 après livraison ; le nom « Diagnostics » du canon
était déjà bon, c'était l'UI 1.0 qui dérivait sur « Telemetry ».

## 1. Contexte et portée

### 1.1 Pourquoi ce chantier

Settings v0.2.0 (post-umbrella, post-slice C1 et C2b) navigue par trois
pages dans `SettingsWindow.xaml` :

- **General** — 28 réglages, 7 sections, 4 modules backend mélangés
  (Shell, Capture, Logging, Settings.Backup) : Shortcuts, Appearance,
  Startup, Recording, Telemetry, Backup & restore, Application data.
- **Transcription** — 22 réglages, 7 sections, monomodule Whisp.
- **Rewriting** — 5 UserControls composables, monomodule Llm.

La page General concentre la douleur. Elle absorbe tout ce qui n'a pas
trouvé sa propre page, en croisant 4 modules dans une seule surface.
Microsoft pose explicitement « Try to keep the total number of settings
to a maximum of four or five » par section, et l'observation des pages
les plus denses de PowerToys (General : 6 sections, ~20-25 cards)
confirme un seuil pratique autour de 25 cards avant qu'un split soit
souhaitable.

### 1.2 Objectif de la phase recherche

Avant de re-splitter et de bouger des contrôles, établir **un
référentiel canonique** « type de paramètre → pattern d'affichage »
applicable à tout Deckle, et ensuite seulement appliquer. Sinon on
déplace les incohérences au lieu de les résoudre. Le pattern de
sélection de dossier en est l'illustration : trois implémentations
différentes coexistent aujourd'hui pour Telemetry storage, Backup
location et Models directory.

### 1.3 Source de vérité interne

Le doc canon `reference--settings-architecture--1.0.md` décrit GeneralPage
avec « 4 sections câblées » et nomme la page extraite « Diagnostics ».
L'état actuel UI est à 7 sections et la section concernée est labellée
« Telemetry ». Le canon était cohérent dès l'origine sur le nom
« Diagnostics » — c'est l'UI v0.1.0 qui a dérivé. La refonte revient
au nom canon. Le canon sera bumpé en 2.0 pour refléter les pages
post-split.

## 2. Décisions actées (rappel, non négociables)

Captées dans la conversation préalable au document. Pas de recherche
nécessaire dessus, listées ici pour la cohérence du raisonnement
ultérieur.

- **Hotkeys** restent dans General, section nommée simplement « Hotkeys ».
  Le transcribe hotkey est **Principal hotkey** — c'est le hotkey
  général qui active tout le module de transcription. Les deux autres
  sont **Primary rewrite hotkey** et **Secondary rewrite hotkey**. Note :
  Principal vs Primary sont distincts (Principal seul = transcription,
  Primary rewrite = premier slot de réécriture), mais si l'ambiguïté
  visuelle gêne, alternatives valides : « Main hotkey » ou « General
  hotkey ».
- **Appearance** et **Startup** (incluant warm-up on launch) restent
  dans General.
- **Recording** → page dédiée extraite de General : audio device,
  auto-paste, voice level window, overlay HUD.
- **Diagnostics** → page dédiée extraite de General. Vocabulaire :
  *log* = temps réel (LogWindow), *telemetry* = persisté sur disque
  (JSONL). Diagnostics est l'ombrelle qui couvre les deux. La page
  contiendra au moins une section **Telemetry** (les opt-ins disque
  actuels) et est structurée pour accueillir des sections futures
  (settings de log temps réel : niveaux, filtrage, capacité du buffer
  LogWindow). Ordre acté : Application log en haut de Telemetry, puis
  le reste.
- **Backup & restore** : reste dans General, placement validé.
- **Rewriting** (`LlmPage` et ses 5 UserControls) : intouchable, sert de
  référence interne pour la cohérence à viser ailleurs.
- **TextBox éditable Telemetry storage folder** : à supprimer (personne
  ne tape un chemin à la main).
- **Page Transcription** : reste, mais layout des sliders denses
  (Decoding + Confidence) à revoir pour cohérence avec Speech filtering.

## 3. Référentiel — type de paramètre × pattern d'affichage

Cadre normatif pour tout futur paramètre Deckle. Source primaire :
[Guidelines for app settings (Microsoft Learn)](https://learn.microsoft.com/en-us/windows/apps/design/app-settings/guidelines-for-app-settings).
Vérifié contre PowerToys, WinUI 3 Gallery et NN/G.

### 3.1 Matrice

| Type de paramètre | Pattern retenu | Container | Notes |
|---|---|---|---|
| Booléen, effet immédiat | `ToggleSwitch` | `SettingsCard` | Auto-save = toggle. Si nécessite un Apply explicite : `CheckBox`. |
| Set fermé ≤5 options | `ComboBox` | `SettingsCard` | Bien que la doc Microsoft suggère `RadioButtons` ≤5, PowerToys et WinUI Gallery utilisent quasi systématiquement `ComboBox` (alignement à droite naturel). `RadioButtons` justifiées seulement quand chaque option mérite une description longue. |
| Set ouvert / suggestions | `AutoSuggestBox` ou `ComboBox IsEditable=True` | `SettingsCard` | Pas de précédent canonique en Settings page chez Microsoft — cas rare. Modèle Whisper et langue Whisper sont les seuls cas Deckle, déjà en `AutoSuggestBox`. |
| Numérique plage continue | `Slider` | `SettingsCard` | `MinWidth` cohérent. Pattern « affixes visuels » (icone min + slider + icone max) pertinent quand la magnitude se voit. Ne pas combiner Slider + NumberBox pour un même setting (règle Microsoft). |
| Numérique plage discrète | `NumberBox` | `SettingsCard` | `SpinButtonPlacementMode="Compact"`. Largeur 96-100px. |
| Texte court | `TextBox` | `SettingsCard` | `MinWidth` cohérent, `PlaceholderText` pour orienter. |
| Texte long multiligne | `TextBox` `AcceptsReturn=True` `TextWrapping=Wrap` | `SettingsCard` `ContentAlignment=Vertical` | `MinHeight` fixé, `MaxHeight` fixé, scroll vertical. Microsoft : « don't let your text input controls grow in height while users type ». |
| Chemin de dossier | `FolderPickerCard` (UserControl à créer, cf. §5) | propre | Source : pattern PowerToys General → Settings Backup. Path en `TextBlock` read-only + bouton glyph FolderOpen `&#xE8DA;`. |
| Action discrète | `Button` ou `SettingsCard IsClickEnabled=True` avec `ActionIcon` | `SettingsCard` | Pour navigation : `ActionIcon` glyph `&#xE8A7;`. Pour external link : `&#xE8C8;`. |
| Information read-only | `TextBlock` | `SettingsCard` | `Foreground={ThemeResource TextFillColorSecondaryBrush}`, `IsTextSelectionEnabled=True` quand utile (chemins). |

### 3.2 Sliders avec description longue

Cas Deckle : sliders Confidence (entropy / logprob / no-speech) et
Decoding (temperature / temperature increment) ont chacun 3-5 lignes
de description en `Description` du `SettingsCard`. Cumulés, la page
Transcription devient très haute.

**Pattern retenu** : `SettingsCard ContentAlignment="Vertical"`. Le
control documente explicitement l'état `Vertical` qui place
Header + Description en haut et le content (slider full-width) en
dessous. Source :
[SettingsExpander / SettingsCard docs](https://learn.microsoft.com/dotnet/communitytoolkit/windows/settingscontrols/settingsexpander).

**Variante retenue pour Decoding et Confidence** : groupement dans
un `SettingsExpander` parent (« Decoding parameters » /
« Confidence thresholds ») pour appliquer le **dévoilement
progressif** — sliders cachés derrière l'expander, visibles seulement
quand le user les cherche. Cohérent avec NN/G : *« Use staged
disclosure: displaying advanced parameters or settings only after a
related field is checked »*
([8 Design Guidelines for Complex Applications](https://www.nngroup.com/articles/complex-application-design/)).

**Patterns écartés** :

- Description courte + tooltip ou InfoBadge pour la longue : NN/G met
  en garde — quand l'explication est essentielle à la compréhension,
  la cacher dégrade l'IA.
- Slider isolé dans un `SettingsExpander` dédié : valide seulement
  quand plusieurs sliders liés justifient le regroupement (ex :
  Speech filtering, FancyZones Zone Appearance). Pour un slider
  unique, `ContentAlignment=Vertical` suffit.

### 3.3 Pattern de section de page

Confirmé par WinUI Gallery, PowerToys, et déjà en place dans Deckle
via le doc canon :

- Wrapper : `Grid` avec `StackPanel MaxWidth=1000` à l'intérieur
  (workaround bug `microsoft-ui-xaml#3842`).
- Spacing : `StaticResource SettingsCardSpacing` (4px).
- Header de section : `TextBlock` style `BodyStrongTextBlockStyle` +
  `Margin 1,30,0,6` (style nommé `SettingsSectionHeaderTextBlockStyle`
  dans Deckle).
- Pas de `SettingsExpander` nesté sous un autre `SettingsExpander`
  (règle Microsoft : « Avoid nesting expanders deeper than one level »).

## 4. Inventaire des incohérences actuelles

Audit comparé au référentiel §3.

### 4.1 Folder pickers — trois implémentations divergentes

| Lieu | Pattern actuel | Diagnostic |
|---|---|---|
| `GeneralPage.xaml` Telemetry storage | TextBox éditable + bouton « Set » + bouton « Open » | TextBox inutile (acté), libellé « Set » non canonique, icône inadaptée flaggée. |
| `GeneralPage.xaml` Backup expander | TextBlock + bouton « Change folder » | Plus proche du canon PowerToys, mais texte sur le bouton au lieu d'une icône. |
| `WhisperPage.xaml` Storage Models directory | TextBox éditable dans un `SettingsExpander` | TextBox éditable accepté ici (chemin parfois tapé). Variante moins prioritaire. |

Cible unique : `FolderPickerCard` (cf. §5).

### 4.2 Sliders denses Decoding + Confidence

Cinq sliders avec description 3-5 lignes, en `SettingsCard
ContentAlignment` par défaut (Right). Le slider est aligné à droite,
serré contre la valeur, le label et la description prennent toute la
largeur restante à gauche. La page devient verticalement très haute.

Cible : `ContentAlignment=Vertical` à l'intérieur d'un `SettingsExpander`
parent par groupe (cf. §3.2).

### 4.3 Wording UI

- Section actuelle « Shortcuts » → renommer « Hotkeys » + transcribe
  label devient « Principal hotkey ».
- Section actuelle « Telemetry » → devient une sous-section dans une
  page extraite « Diagnostics ».
- Section « Recording » disparaît de General (extraction page dédiée).

### 4.4 Densité par page après split

| Page | Réglages estimés | Verdict |
|---|---|---|
| General (post-split) | ~12 (3 hotkeys + 1 theme + 2 startup + ~4 backup + 2 app data) | OK, en-dessous du seuil 20-25. |
| Recording (nouvelle) | ~10 (1 device + 1 paste + 4 voice level + 4 overlay) | OK. |
| Transcription (existante) | 22 | OK, à la limite haute. Compactage Decoding + Confidence en expander réduit la hauteur perçue. |
| Rewriting (intouchable) | ~16 + N profils | OK. |
| Diagnostics (nouvelle) | ~5 réglages dans une section Telemetry, croissance prévue | OK, page minimaliste assumée à V1. |

Cinq pages principales + footer Logs. Toutes sous le seuil Microsoft.

## 5. Spec `FolderPickerCard`

UserControl réutilisable, à poser sous `src/Deckle.Settings/Controls/`.
Réutilisé par Telemetry storage, Backup location, Models directory et
future Data folder éditable si Axe F est retenu (hors scope refonte).

### 5.1 Signature

Propriétés exposées (DependencyProperty) :

- `Header` (string) — label du card, x:Uid pour localisation.
- `Description` (string) — sous-texte du card, optionnel.
- `HeaderIcon` (IconElement) — glyph FontIcon optionnel.
- `Path` (string, TwoWay) — chemin actuel, affiché read-only.
- `DefaultPath` (string) — chemin par défaut affiché en placeholder
  quand `Path` est vide.

Événements :

- `PathChanged` (event) — déclenché après PickFolder réussi.

### 5.2 Layout XAML cible

Pattern PowerToys General Settings Backup, transposé en UserControl.
Décision actée (cf. §9.2) : Deckle ajoute un second bouton « Open in
Explorer » à droite du bouton Pick, parce que les fichiers JSONL
(corpus) et les backups sont du contenu inspectable.

```xml
<controls:SettingsCard Header="{x:Bind Header}"
                       Description="{x:Bind Description}"
                       HeaderIcon="{x:Bind HeaderIcon}">
    <Grid ColumnSpacing="8">
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="Auto" />
            <ColumnDefinition Width="Auto" />
            <ColumnDefinition Width="Auto" />
        </Grid.ColumnDefinitions>
        <TextBlock x:Name="PathTextBlock"
                   Grid.Column="0"
                   MaxWidth="286"
                   VerticalAlignment="Center"
                   Foreground="{ThemeResource TextFillColorSecondaryBrush}"
                   IsTextSelectionEnabled="True"
                   Style="{StaticResource CaptionTextBlockStyle}"
                   Text="{x:Bind Path, Mode=OneWay}"
                   TextTrimming="CharacterEllipsis"
                   TextWrapping="NoWrap">
            <ToolTipService.ToolTip>
                <ToolTip IsEnabled="{Binding IsTextTrimmed,
                                             ElementName=PathTextBlock,
                                             Mode=OneWay}">
                    <TextBlock Text="{x:Bind Path, Mode=OneWay}" />
                </ToolTip>
            </ToolTipService.ToolTip>
        </TextBlock>
        <Button Grid.Column="1"
                x:Uid="FolderPickerCardPickButton"
                Click="PickButton_Click">
            <FontIcon Glyph="&#xE8DA;" FontSize="14" />
        </Button>
        <Button Grid.Column="2"
                x:Uid="FolderPickerCardOpenButton"
                Click="OpenButton_Click">
            <FontIcon Glyph="&#xE8A7;" FontSize="14" />
        </Button>
    </Grid>
</controls:SettingsCard>
```

Notes de conformité :

- Icône **`&#xE8DA;`** Segoe Fluent (FolderOpen) pour Pick, **`&#xE8A7;`**
  (OpenInNewWindow) pour Open in Explorer. Aucun label texte sur les
  boutons, tooltip x:Uid pour localisation.
- Path en `TextBlock` `CaptionTextBlockStyle` + `TextFillColorSecondaryBrush`,
  `IsTextSelectionEnabled=True` permet le copier-coller manuel.
- Tooltip qui s'affiche seulement quand le texte est tronqué via
  `IsTextTrimmed`.

### 5.3 API picker

Stack actuel : WinUI 3 + WindowsAppSDK 1.8 → utiliser
`Microsoft.Windows.Storage.Pickers.FolderPicker` (et **non**
`Windows.Storage.Pickers.FolderPicker` qui est l'API UWP héritée
nécessitant `WinRT.Interop.InitializeWithWindow` et cassant en
élévation). La nouvelle API prend un `WindowId` en constructeur :

```csharp
private async void PickButton_Click(object sender, RoutedEventArgs e)
{
    var window = (Application.Current as App)?.SettingsWindow;
    if (window is null) return;
    var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(
        window.AppWindow.Id);
    var result = await picker.PickSingleFolderAsync();
    if (result is not null)
    {
        Path = result.Path;
        PathChanged?.Invoke(this, EventArgs.Empty);
    }
}

private void OpenButton_Click(object sender, RoutedEventArgs e)
{
    if (string.IsNullOrEmpty(Path)) return;
    Process.Start(new ProcessStartInfo
    {
        FileName = Path,
        UseShellExecute = true,
    });
}
```

Source :
[Tutorial: Open files and folders with pickers in WinUI](https://learn.microsoft.com/en-us/windows/apps/develop/files/using-file-folder-pickers).

### 5.4 Cas Models directory (Whisper) — variante éditable

Décision actée (cf. §9.4) : variante `FolderPickerEditableCard` pour
Models directory uniquement. Précédent canonique : PowerToys
AdvancedPaste (chemin de modèle ML). Pattern : remplacer le
`TextBlock` read-only par un `TextBox` éditable, conserver les deux
boutons Pick / Open. Permet à un user qui a cloné un dossier de
modèles depuis un autre poste de taper le chemin.

## 6. Décision Axe E — page « Folders » consolidée

**Décision retenue : NON.**

Trois sources convergent contre une page « Folders » consolidée
agrégeant Models directory + Telemetry storage + Backup location +
Data folder :

- **Skill `settings-system-design`** : « Organisation par but
  utilisateur, pas par module technique. » Un groupement « tous les
  chemins » est exactement le contre-modèle prohibé (groupement par
  type de contrôle, pas par sujet utilisateur).
- **NN/G** : « Rather than spend time upfront to develop several
  intuitive and logical top-level categories, teams rush through this
  important process, creating numerous weak categories. »
  ([Top 10 IA Mistakes](https://www.nngroup.com/articles/top-10-ia-mistakes/)).
- **Microsoft + PowerToys** : pattern non observé. Quand Win11 Settings
  agrège des trucs cross-cutting (page « Storage » dans System), c'est
  par sujet utilisateur (« où vont mes fichiers ») et pas par type de
  contrôle.

**Pattern alternatif retenu** : chaque chemin reste dans la page de sa
fonction, en bas de page, dans un `SettingsExpander` « Storage » qui
applique le **dévoilement progressif**.

Conséquence pour le mapping cible (§7) :

- Models directory → reste sous Transcription, dans un expander Storage.
- Telemetry storage → reste sous Diagnostics → Telemetry, en expander.
- Backup location → reste sous General → Backup expander.
- Data folder éditable → si retenu (Axe F, hors scope), reste sous
  General → Application data en expander.

## 7. Mapping pages cible

### 7.1 General

Niveau « shell » et configuration globale.

1. **Hotkeys** — 3 read-only display :
   - **Principal hotkey** (active la transcription)
   - **Primary rewrite hotkey**
   - **Secondary rewrite hotkey**
2. **Appearance** — 1 ComboBox (System / Light / Dark).
3. **Startup** — 2 toggles (Autostart, Warmup on launch).
4. **Backup & restore** — `SettingsExpander` (Create, Restore, Change
   location, Backup info). Pattern PowerToys conservé.
5. **Application data** — 2 lignes (Data folder display + Open button,
   Re-run setup button).

Total : ~12 réglages, 5 sections.

### 7.2 Recording (nouvelle)

Tout ce qui touche à la capture audio et son rendu visuel HUD.

1. **Microphone** — `ComboBox` Audio input device.
2. **Auto-paste** — `ToggleSwitch`.
3. **Voice level window** — `SettingsExpander` master (Auto-calibration
   toggle dans header) + 3 sliders enfants (Floor, Ceiling, Curve
   exponent), pattern Speech filtering reproduit.
4. **Overlay HUD** — `SettingsExpander` master (Enabled toggle dans
   header) + 3 enfants (Fade on proximity, Animations, Position).

Total : ~10 réglages, 4 sections.

### 7.3 Transcription (existante, layout à revoir)

Inchangée structurellement, mais :

- **Decoding** (Temperature + Temperature increment) → regroupé dans un
  `SettingsExpander` parent avec sliders en `ContentAlignment=Vertical`.
- **Confidence thresholds** (Entropy + Logprob + No-speech) → regroupé
  dans un `SettingsExpander` parent avec sliders en `ContentAlignment=Vertical`.
- **Storage** (Models directory) → reste, pattern `FolderPickerEditableCard`.

Total : 22 réglages, 7 sections.

### 7.4 Rewriting (intouchable)

Pas de changement. Sert de référence interne.

### 7.5 Diagnostics (nouvelle)

Page extraite. Vocabulaire : *log* = temps réel (LogWindow),
*telemetry* = persisté sur disque (JSONL). À V1, la page contient une
seule section Telemetry. Structurée pour accueillir d'autres sections
au fur et à mesure.

#### 7.5.1 Section Telemetry (V1)

Ordre acté : Application log d'abord, puis le reste.

1. **Application log** — `ToggleSwitch` (+ consent dialog).
2. **Microphone telemetry** — `ToggleSwitch` (+ consent).
3. **Latency telemetry** — `ToggleSwitch`.
4. **Corpus** — `SettingsExpander` master (Corpus toggle dans header,
   + consent) + Audio corpus enfant (toggle + consent).
5. **Storage folder** — `FolderPickerCard` (TextBox éditable
   supprimée).

#### 7.5.2 Sections futures (anticipées, pas implémentées V1)

- **Logs (real-time)** — niveau de log par défaut, capacité du buffer
  LogWindow, filtres par défaut, pattern coloration.
- Autres si pertinent (instrumentation conditionnelle, tracing
  pipeline).

Total V1 : ~5 réglages, 1 section. La page reste minimaliste à
V1 mais a un nom qui couvre la croissance attendue.

## 8. Audit IsEnabled — états dépendants

Audit exhaustif (4 cas connus) : **tous les bindings sont corrects et
cohérents**. Aucune correction nécessaire.

| Cas | Master | Enfants | Statut |
|---|---|---|---|
| VAD (Whisp) | `WhisperPage.xaml:189` ToggleSwitch `VadEnabledToggle` → `ViewModel.VadEnabled` | 6 SettingsCard (VadThreshold, VadMinSpeech, VadMinSilence, VadMaxSpeech, VadSpeechPad, VadOverlap) avec `IsEnabled={x:Bind ViewModel.VadEnabled, Mode=OneWay}` | OK |
| Corpus (Logging) | `GeneralPage.xaml:283` ToggleSwitch `CorpusLoggingToggle` → `ViewModel.TelemetryCorpusEnabled` | 1 enfant `AudioCorpusCard` avec binding correct | OK |
| Overlay (Shell) | `GeneralPage.xaml:224` ToggleSwitch → `ViewModel.OverlayEnabled` | 3 enfants (Fade, Animations, Position) avec bindings corrects | OK |
| Rewriting (Llm) | `LlmGeneralSection.xaml:24` `ToggleSwitch` exposé via DP `IsRewritingEnabled` | 4 sections enfants + endpoint expander, tous bindés sur `GeneralSection.IsRewritingEnabled` | OK |

Pattern Llm intéressant : le master est un `ToggleSwitch` dans un
UserControl enfant qui expose son état via `DependencyProperty`, lu en
OneWay par les sections sœurs depuis `LlmPage.xaml`. C'est le pattern
canonique « parent qui orchestre via le contrat des enfants », à
reproduire si Recording ou Diagnostics deviennent composites.

**Recommandation** : pas de modification du code IsEnabled. À noter au
moment du split (Recording, Diagnostics) pour préserver les bindings
quand les sections changent de page.

## 9. Questions résolues

Toutes fermées dans la 1.1. Listées ici pour traçabilité.

### 9.1 Wording « Telemetry » vs « Privacy » → **Diagnostics**

Décision Louis. Cohérent avec le doc canon (« Diagnostics » dès
l'origine). Telemetry devient une sous-section.

### 9.2 Bouton « Open in Explorer » dans `FolderPickerCard` → **oui**

Décision par confiance. Spec en §5.2 reflète le second bouton glyph
`&#xE8A7;`. Justification : les JSONL et backups sont du contenu
inspectable, pas juste un dossier opaque.

### 9.3 SettingsExpander pour Decoding et Confidence → **oui**

Décision par confiance. Pattern décrit en §3.2 et §7.3. Justification :
staged disclosure NN/G + ce sont des paramètres expert.

### 9.4 Models directory — TextBox éditable → **oui (variante éditable)**

Décision par confiance. `FolderPickerEditableCard` spec en §5.4.
Justification : cas réaliste (cloner un dossier de modèles depuis un
autre poste).

## 10. Hors scope (à traiter séparément)

- **Refonte de la page Rewriting** — décision actée, intouchable.
- **Data folder éditable** (Axe F) — chantier connexe nécessitant une
  migration `AppPaths.UserDataRoot` pour tous les sous-chemins
  (`models/`, `telemetry/`, `corpus/`, `app.jsonl`). À traiter après
  refonte navigation, en chantier propre.
- **Recherche live dans Settings** — système >15-20 paramètres
  (Deckle ~66 cumulés), recherche live recommandée par le skill et
  par NN/G. Le doc canon mentionne déjà un `AutoSuggestBox` dans
  `NavigationView.AutoSuggestBox` — à vérifier ce qu'elle indexe.
  Hors scope refonte, à noter pour le futur.
- **Sections futures de Diagnostics** (logs real-time) — anticipées
  dans la structure de la page, pas implémentées en V1.
- **Promotion en `reference--*--2.0.md`** : après livraison, ce
  document de recherche sera supprimé et le canon
  `reference--settings-architecture--1.0.md` sera bumpé en 2.0 pour
  refléter l'état post-refonte.

## 11. Sources

### Microsoft Learn

- [Guidelines for app settings](https://learn.microsoft.com/en-us/windows/apps/design/app-settings/guidelines-for-app-settings)
- [Toggle switch guidelines](https://learn.microsoft.com/en-us/windows/apps/design/controls/toggles)
- [TextBox guidelines](https://learn.microsoft.com/en-us/windows/apps/design/controls/text-box)
- [Sliders, design basics](https://learn.microsoft.com/en-us/windows/win32/uxguide/ctrl-sliders)
- [NavigationView guidance](https://learn.microsoft.com/en-us/windows/apps/design/controls/navigationview)
- [SettingsCard / SettingsExpander (Community Toolkit)](https://learn.microsoft.com/dotnet/communitytoolkit/windows/settingscontrols/settingsexpander)
- [Tutorial: Open files and folders with pickers in WinUI](https://learn.microsoft.com/en-us/windows/apps/develop/files/using-file-folder-pickers)

### Code de référence

- [WinUI 3 Gallery — SettingsPage.xaml](https://github.com/microsoft/WinUI-Gallery/blob/main/WinUIGallery/Pages/SettingsPage.xaml)
- [PowerToys — GeneralPage.xaml](https://github.com/microsoft/PowerToys/blob/main/src/settings-ui/Settings.UI/SettingsXAML/Views/GeneralPage.xaml) (folder picker pattern)
- [PowerToys — PowerLauncherPage.xaml](https://github.com/microsoft/PowerToys/blob/main/src/settings-ui/Settings.UI/SettingsXAML/Views/PowerLauncherPage.xaml) (slider patterns)
- [PowerToys — FancyZonesPage.xaml](https://github.com/microsoft/PowerToys/blob/main/src/settings-ui/Settings.UI/SettingsXAML/Views/FancyZonesPage.xaml)
- [PowerToys — AwakePage.xaml](https://github.com/microsoft/PowerToys/blob/main/src/settings-ui/Settings.UI/SettingsXAML/Views/AwakePage.xaml) (NumberBox time pattern)
- [PowerToys — AdvancedPastePage.xaml](https://github.com/microsoft/PowerToys/blob/main/src/settings-ui/Settings.UI/SettingsXAML/Views/AdvancedPastePage.xaml) (path TextBox + browse button)

### NN/G

- [Toggle-Switch Guidelines](https://www.nngroup.com/articles/toggle-switch-guidelines/)
- [The Power of Defaults](https://www.nngroup.com/articles/the-power-of-defaults/)
- [Customization of UIs and Products](https://www.nngroup.com/articles/customization-of-uis-and-products/)
- [8 Design Guidelines for Complex Applications](https://www.nngroup.com/articles/complex-application-design/)
- [Top 10 IA Mistakes](https://www.nngroup.com/articles/top-10-ia-mistakes/)

### Interne Deckle

- [reference--settings-architecture--1.0.md](reference--settings-architecture--1.0.md) — canon partiellement obsolète, à bumper en 2.0 post-refonte.
- `src/Deckle.Settings/SettingsWindow.xaml` — NavigationView principale.
- `src/Deckle.Settings/GeneralPage.xaml` — page surchargée à éclater.
- `src/Deckle.Whisp/WhisperPage.xaml` — sliders denses à reorganiser.
- `src/Deckle.Llm/LlmPage.xaml` + 5 UserControls — référence interne de cohérence.

## 12. Validation visuelle WinUI 3 Gallery

Liste des contrôles et patterns à valider visuellement dans WinUI 3
Gallery avant ou pendant l'implémentation. Pour chacun : le sample
Gallery, ce qu'il contient, et la décision Deckle qui sera appliquée.

Ouvrir la Gallery → `WinUI 3 Gallery` (Microsoft Store) ou
`microsoft.com/store/productId/9P3JFPWWDZRC`.

### 12.1 SettingsCard — alignement Vertical

- **Sample** : Design guidance → All controls → SettingsCard, onglet
  *Layout examples*.
- **À voir** : un `SettingsCard` avec `ContentAlignment="Vertical"`
  (Header + Description en haut, contrôle en dessous full-width).
- **Décision Deckle** : tous les sliders Decoding et Confidence
  passent à `ContentAlignment=Vertical`, à l'intérieur d'un
  `SettingsExpander` parent.

### 12.2 SettingsExpander avec contrôle dans header

- **Sample** : Design guidance → All controls → SettingsExpander,
  exemple par défaut (toggle dans le header + items dépliables en
  dessous).
- **À voir** : pattern master toggle / sub-cards révélés au déploiement.
- **Décision Deckle** : pattern reproduit pour Voice level window,
  Overlay HUD, Decoding parent, Confidence parent.

### 12.3 ToggleSwitch standard

- **Sample** : Inputs → ToggleSwitch.
- **À voir** : taille, alignement, états On/Off.
- **Décision Deckle** : tous les boolean settings utilisent ce contrôle,
  aligné à droite du `SettingsCard`.

### 12.4 ComboBox dans SettingsCard

- **Sample** : Inputs → ComboBox + Design guidance → All controls →
  SettingsCard (sample « App theme »).
- **À voir** : ComboBox aligné à droite du card, MinWidth cohérent.
- **Décision Deckle** : Theme (3 items, MinWidth 160) et Audio device
  (N items, MinWidth 240) sur ce pattern.

### 12.5 Slider standard

- **Sample** : Inputs → Slider.
- **À voir** : layout horizontal, valeur affichée, ticks, range marks.
- **Décision Deckle** : style `SettingSliderStyle` actuel conservé
  (MinWidth 180, IsThumbToolTipEnabled=True). En `ContentAlignment
  Vertical`, le slider devient full-width — vérifier que la valeur
  affichée à droite est lisible dans ce mode.

### 12.6 NumberBox compact

- **Sample** : Inputs → NumberBox, mode `SpinButtonPlacementMode=Compact`.
- **À voir** : largeur, spinner buttons.
- **Décision Deckle** : VadMinSpeech, VadMinSilence, VadSpeechPad,
  Max tokens — tous en NumberBox compact, MinWidth 100.

### 12.7 Folder picker pattern (PowerToys, pas Gallery)

- **Sample** : pas dans WinUI 3 Gallery directement. Voir PowerToys
  source (référence en §11) ou observer Win11 Settings → Personalization
  → Themes → Custom theme, qui utilise un pattern proche.
- **À voir** : path read-only + bouton icône seul `&#xE8DA;`.
- **Décision Deckle** : `FolderPickerCard` UserControl (§5.2).

### 12.8 NavigationView Left avec footer items

- **Sample** : Design guidance → All controls → NavigationView,
  exemple « PaneDisplayMode=Auto ».
- **À voir** : l'adaptive Left / LeftCompact / LeftMinimal, les
  FooterMenuItems en bas de pane.
- **Décision Deckle** : pattern déjà en place dans `SettingsWindow.xaml`,
  juste ajout de 2 items (Recording, Diagnostics).

### 12.9 ItemsRepeater pour profils

- **Sample** : Design guidance → All controls → ItemsRepeater,
  exemple data-binding.
- **À voir** : pattern de DataTemplate, recyclage des items.
- **Décision Deckle** : déjà en place dans `LlmProfilesSection`. Pas
  de changement, mais sert de référence si Recording ou Diagnostics
  acquièrent des items multiples.

### 12.10 InfoBar (warning post-restart)

- **Sample** : Notifications → InfoBar.
- **À voir** : Severity=Warning, IsOpen toggle, layout.
- **Décision Deckle** : déjà utilisé sur `WhisperTemperatureIncrementWarning`.
  Pas de changement, mais à reproduire si un setting déplacé hors de sa
  page habituelle nécessite un avertissement de migration.

## 13. Plan d'implémentation par slices

Chaque slice est une branche Git dédiée, mergeable indépendamment
dans `main`. Pause possible entre slices. Suit la convention Deckle
(slice C1, C2b, etc.).

### Slice S1 — Création `FolderPickerCard` UserControl

**Branche** : `refactor/folder-picker-card`

**Scope** :
- Créer `src/Deckle.Settings/Controls/FolderPickerCard.xaml` + `.cs`
  selon spec §5.2 (avec second bouton Open in Explorer).
- Créer `FolderPickerEditableCard.xaml` + `.cs` (variante avec TextBox
  éditable) pour Models directory.
- Localisation : entrées x:Uid pour `FolderPickerCardPickButton`
  (« Pick a folder ») et `FolderPickerCardOpenButton` (« Open in
  Explorer ») dans `Strings/en-US/Resources.resw`.
- Remplacer les 3 implémentations actuelles :
  - `GeneralPage.xaml` Telemetry storage → `FolderPickerCard`.
  - `GeneralPage.xaml` Backup expander → `FolderPickerCard`.
  - `WhisperPage.xaml` Models directory → `FolderPickerEditableCard`.

**Acceptance** :
- 3 folder pickers visuellement identiques (sauf Models éditable).
- Pick fonctionne sur les 3.
- Open in Explorer ouvre le bon dossier sur les 3.
- Pas de régression de persistance.

### Slice S2 — Extraction page Diagnostics

**Branche** : `refactor/diagnostics-page`

**Scope** :
- Créer `src/Deckle.Settings/DiagnosticsPage.xaml` + `.cs` +
  `DiagnosticsViewModel.cs`.
- Migrer la section Telemetry depuis `GeneralPage`/`GeneralViewModel`
  (5 réglages : Application log, Microphone, Latency, Corpus + Audio
  corpus, Storage folder).
- Conserver les consent dialogs (`MicrophoneTelemetryConsentDialog`,
  `CorpusConsentDialog`, `AudioCorpusConsentDialog`,
  `ApplicationLogConsentDialog`).
- Ordre acté : Application log d'abord, puis le reste.
- Référencer la page dans `SettingsWindow.xaml` `NavigationView.MenuItems`
  (Tag = `Deckle.Settings.DiagnosticsPage`).
- Strings dans `Strings/en-US/Resources.resw` pour la nouvelle page.

**Acceptance** :
- Nouvelle entrée NavigationView « Diagnostics ».
- Tous les 5 réglages opérationnels et persistants.
- Consent dialogs déclenchés correctement.
- Section vide (placeholder) dans GeneralPage à l'emplacement
  Telemetry, qui sera supprimé en S4.

### Slice S3 — Extraction page Recording

**Branche** : `refactor/recording-page`

**Scope** :
- Créer `src/Deckle.Settings/RecordingPage.xaml` + `.cs` +
  `RecordingViewModel.cs`.
- Migrer 4 sections depuis `GeneralPage` :
  - Microphone (`AudioInputDeviceId`)
  - Auto-paste (`AutoPasteEnabled`)
  - Voice level window (4 réglages, expander)
  - Overlay HUD (4 réglages, expander)
- Préserver les bindings IsEnabled (Voice level auto-calibration toggle,
  Overlay enabled toggle).
- Préserver `SettingsHost.ApplyLevelWindow` et autres callbacks.
- Référencer dans NavigationView (Tag = `Deckle.Settings.RecordingPage`).
- Strings dans `Resources.resw`.

**Acceptance** :
- Nouvelle entrée NavigationView « Recording ».
- 4 sections opérationnelles.
- Voice level mapper continue d'être mis à jour live (HUD chrono).
- Overlay applique les changements live.
- Section vide dans GeneralPage à l'emplacement Recording, supprimée
  en S4.

### Slice S4 — Cleanup `GeneralPage`

**Branche** : `refactor/general-page-cleanup`

**Scope** :
- Supprimer les sections vides de `GeneralPage.xaml` (Telemetry,
  Recording).
- Renommer la section « Shortcuts » en « Hotkeys ».
- Renommer le label transcribe en « Principal hotkey ».
- Reorder sections finales : Hotkeys, Appearance, Startup, Backup &
  restore, Application data.
- Cleanup `GeneralViewModel.cs` : retirer les properties migrées vers
  `DiagnosticsViewModel` et `RecordingViewModel`.
- Update strings.

**Acceptance** :
- `GeneralPage` à 5 sections, ~12 réglages.
- `GeneralViewModel` réduit aux propriétés General-only.
- Pas de régression sur les 5 sections restantes.

### Slice S5 — Refonte sliders Whisper Decoding + Confidence

**Branche** : `refactor/whisper-sliders-expander`

**Scope** :
- Wrap Decoding (Temperature + Temperature increment) dans un
  `SettingsExpander` parent.
- Wrap Confidence (Entropy + Logprob + No-speech) dans un autre
  `SettingsExpander` parent.
- Sliders enfants en `ContentAlignment=Vertical`.
- Conserver les reset buttons et leur visibilité hover.
- Strings ajoutées pour les headers d'expander parent.

**Acceptance** :
- Page Whisper compactée verticalement (2 expanders au lieu de 5
  cards plates).
- Sliders restent fonctionnels, persistants, resetables.
- Sliders en Vertical : valeur affichée toujours lisible.

### Slice S6 — Bump du canon

**Branche** : `docs/settings-architecture-2.0`

**Scope** :
- Bumper `reference--settings-architecture--1.0.md` → `2.0.md`.
- Refléter post-refonte : 5 pages (General, Recording, Transcription,
  Rewriting, Diagnostics) + footer Logs.
- Documenter le pattern `FolderPickerCard` comme canon.
- Documenter le pattern `SettingsExpander` parent pour groupes de
  sliders denses.
- Supprimer ce fichier `research--settings-redesign--1.1.md`.

**Acceptance** :
- Canon à jour, lisible en isolation.
- Doc de recherche supprimé (transition done).

### Considérations transverses

- Chaque slice prévoit une étape de mise à jour des x:Uid /
  Resources.resw pour les nouvelles strings.
- Pas de modification des modèles de persistance (settings.json) —
  les ViewModel migrent, les POCO restent.
- Build local entre chaque slice (script `build-run.ps1` géré par
  Louis — Claude ne build jamais directement, cf.
  [CLAUDE.md racine](../../../CLAUDE.md)).
- Test runtime à chaque slice : kill toute instance Deckle, build,
  lancer, parcourir les pages, valider qu'aucune régression visuelle
  ou fonctionnelle.
- Les slices S2 et S3 peuvent être permutées sans dépendance.
- Slice S5 indépendante des autres, peut être faite en premier ou en
  dernier au choix.
