# LogWindow — structure et modele de donnees

## Structure generale

Fenetre classique (`OverlappedPresenter`, min/max/resize), Close->Cancel+Hide, theme systeme, `SystemBackdrop = MicaBackdrop`. `PreferredMinimumWidth=400`, `PreferredMinimumHeight=300`.

## Title bar

`Microsoft.UI.Xaml.Controls.TitleBar` natif (Windows App SDK 1.8). Caption buttons **Tall** via `AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall`. `ExtendsContentIntoTitleBar=true` + `SetTitleBar(AppTitleBar)`.

Icone app : beacon enregistrement (rouge) / idle (gris) via `SetRecordingState`, reconstruit un `ImageIconSource` complet a chaque bascule (muter `ImageSource` in-place ne propage pas visuellement). Icone fenetre (`AppWindow.SetIcon`) suit le meme etat.

La recherche (`AutoSuggestBox`) est dans `TitleBar.Content`, pas dans un slot custom. Pattern Win11 Settings. Pas de swap icone/field, l'AutoSuggestBox retrecit naturellement jusqu'a `MinWidth=240`.

## Command bar

Sous la title bar, 2 zones :
- **Gauche** — `SelectorBar` Steps / All / Activity / Alerts. Selection initiale = **Steps** (vue par defaut, narration UX du pipeline).
- **Droite** — `CommandBar` avec `IsDynamicOverflowEnabled` (true par defaut) + `DynamicOverflowOrder` pour l'overflow responsif. Groupe 2 (Copy/Save/Clear/Sep) migre en premier, puis groupe 1 (AutoScroll/Wrap).

Actions : Copy (E8C8) / Save (E74E) / Clear (E74D) + Auto scroll (EC8F toggle, on par defaut) / Wrap (E751 toggle).

## Modele de donnees

`enum LogLevel { Verbose, Info, Success, Warning, Error, Narrative }` (public). API thread-safe (marshal via `DispatcherQueue`).

`Success` = ancien `Step` (jalons verifies, vert). `Narrative` = niveau a part, hors hierarchie technique : phrase claire en langue naturelle decrivant ce que fait le pipeline (Recording -> VAD -> Transcribe -> LLM -> Paste -> Done). API : `_log.Success(...)` et `_log.Narrative(...)`.

Deux collections :
- `_entries` — `List<LogEntry>`, tampon complet
- `_visible` — `ObservableCollection<LogEntry>` bindee a `LogItems.ItemsSource`

Filtre = `Matches()` qui combine selector + recherche live (`IndexOf` case-insensitive). `ApplyFilter()` rebuild `_visible`. Hierarchie selector :
- **Steps** — `Narrative` uniquement (vue par defaut)
- **All** — tout passe (y compris Verbose et Narrative)
- **Activity** — Info + Success + Warning + Error (Verbose ET Narrative masques)
- **Alerts** — Warning + Error uniquement (label "Alerts" mais les deux LogLevel restent distincts cote code)

Cap 5000 entrees. Sur overflow, retire la plus vieille des deux collections (ref equality, `LogEntry` est une classe). Copy/Save operent sur `_visible` — l'utilisateur copie ce qu'il voit.

## Couleurs — ThemeDictionaries XAML

Bindees via `ThemeResource` dans les DataTemplates XAML (`Grid.Resources > ThemeDictionaries`). Theme switch runtime automatique.

- **Verbose** — `TextFillColorSecondaryBrush` (texte secondaire, lisible mais en retrait)
- **Info** — `LogInfoBrush` custom (bleu semantique Fluent `#005FB7` light / `#60CDFF` dark, pas l'accent systeme)
- **Success** — `SystemFillColorSuccessBrush` (vert) — ancien `Step`, jalons verifies (model loaded, hotkey start/stop, page ready, nav OK)
- **Warning** — `SystemFillColorCautionBrush` (orange)
- **Error** — `SystemFillColorCriticalBrush` (rouge)
- **Narrative** — `TextFillColorPrimaryBrush` (texte primaire pleine intensite) — la narration UX se lit comme du contenu principal, pas comme un niveau colore

## Templates et selector

12 DataTemplates (6 niveaux x 2 dimensions wrap). `LogLevelTemplateSelector` (classe C#, herite `DataTemplateSelector`) instancie deux fois en XAML (NoWrapSelector / WrapSelector).

Piege WinUI 3 : `ItemsControl.ItemTemplateSelector` n'est pas honore a l'execution (seul `ListViewBase` le respecte). Contournement : `ItemTemplate` pointe sur un `ContentControl` wrapper dont le `ContentTemplateSelector` est le bon selector. Le toggle Wrap swap `ItemTemplate` entre `NoWrapRoot` et `WrapRoot`.

## Wrap — piege scroll horizontal

Le toggle swap le template **et** bascule `HorizontalScrollBarVisibility` entre `Auto` et `Disabled`. Sans ca, `TextWrapping="Wrap"` ne s'applique pas — le `ScrollViewer` mesure son contenu en largeur infinie tant que le scroll horizontal est autorise.

**Shift+molette = comportement natif WinUI 3 assume**. Le ScrollViewer interne du ListView scrolle verticalement (pas horizontalement), parce que WinUI 3 n'expose pas de routing Tunnel/Preview pour intercepter `PointerWheelChanged` avant que le SV interne ne le consomme. Toute tentative custom (re-injection horizontale via `ChangeView`, baseline sync via `ViewChanged`) produit un effet visuel saccade/inverse a chaque cran de molette — pire qu'un simple comportement natif. Pour parcourir une longue ligne sans wrap : utiliser la scrollbar horizontale, ou activer le toggle Wrap.

## Padding bas du ListView

`Padding="12,4,12,24"` au ListView : 24px de marge sous la derniere ligne. La scrollbar horizontale flottante (~12px) recouvrirait sinon la derniere entree, qui est exactement la ou les nouvelles lignes apparaissent (auto-scroll on).

## Filets de diagnostic globaux

Dans `App` : `Application.UnhandledException`, `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException` -> `DebugLog` (prefixes `CRASH` / `CRASH-AD` / `CRASH-TS`).
