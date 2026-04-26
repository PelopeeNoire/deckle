# brief — packaging-foundation

## Identite

- **Branche** : `claude/eloquent-raman-29019b`
- **Worktree** : `D:\worktrees\transcription\eloquent-raman-29019b`
- **Base** : `main` au commit `bf2be79` (mergé en fast-forward au début de session — les 3 commits benchmark/rewrite-profiles de main sont intégrés).
- **Tete de branche** : `37e2f83`
- **Date de session** : 2026-04-26

## Resume en une phrase

Centralisation de la résolution filesystem dans un service `AppPaths` (préparation MSIX), introduction d'un système de backup/restore des settings à la PowerToys, réorganisation des sections de la page General, et alignement des tooltips tray sur l'état live du pipeline.

## Commits empilés (du plus ancien au plus récent)

```
c81d084  refactor(paths): centralize filesystem resolution in AppPaths
a12b21e  feat(settings): add SettingsBackupService and BackupDirectory
b23a2e6  refactor(settings/general): reorder sections - Appearance / Startup before Recording
8001564  feat(settings/general): add Backup section (snapshot and restore)
37e2f83  tune(tray,engine): align tray tooltip with the live pipeline state
```

5 commits, additif et incrémentaux. Chacun apporte de la valeur isolément et est revertable seul si besoin.

## Fichiers touchés (par commit)

### c81d084 — AppPaths

| Fichier | Statut | Notes |
|---|---|---|
| `src/WhispUI/AppPaths.cs` | NOUVEAU | Service static, ~135 lignes commentées |
| `src/WhispUI/App.xaml.cs` | MODIFIÉ | +1 log de boot AppPaths après `AddSink(JsonlFileSink)` |
| `src/WhispUI/Logging/CorpusPaths.cs` | MODIFIÉ | `ResolveDefaultBaseDir` supprimé, default délégué à `AppPaths.TelemetryDirectory` |
| `src/WhispUI/Settings/AppSettings.cs` | MODIFIÉ | Commentaire stale mis à jour (mention AppPaths) |
| `src/WhispUI/Settings/SettingsService.cs` | MODIFIÉ | Constructeur consomme `AppPaths.ConfigDirectory`, `ResolveModelsDirectory` délègue à `AppPaths.ModelsDirectory` |
| `src/WhispUI/docs/settings.md` | MODIFIÉ | Doc stale mise à jour |

### a12b21e — SettingsBackupService

| Fichier | Statut | Notes |
|---|---|---|
| `src/WhispUI/Settings/SettingsBackupService.cs` | NOUVEAU | Service static + record `BackupInfo` |
| `src/WhispUI/Settings/AppSettings.cs` | MODIFIÉ | Ajout `PathsSettings.BackupDirectory` (string, défaut `""`) |
| `src/WhispUI/Settings/SettingsService.cs` | MODIFIÉ | +`ConfigPath` (internal getter), +`ResolveBackupDirectory()`, +`Reload()` |

### b23a2e6 — Réorganisation sections General

| Fichier | Statut | Notes |
|---|---|---|
| `src/WhispUI/Settings/GeneralPage.xaml` | MODIFIÉ | 65 ins / 65 del — pure permutation de blocs, aucun changement de binding ni de handler |

### 8001564 — Section Backup UI

| Fichier | Statut | Notes |
|---|---|---|
| `src/WhispUI/Settings/GeneralPage.xaml` | MODIFIÉ | +3 SettingsCards en bas (Backup folder, Create backup, Restore from backup) |
| `src/WhispUI/Settings/GeneralPage.xaml.cs` | MODIFIÉ | +5 handlers : Create / Restore (avec ContentDialog confirmation) / Refresh / ChangeFolder / OpenFolder. +`using System.Linq` |
| `src/WhispUI/Settings/ViewModels/GeneralViewModel.cs` | MODIFIÉ | +`BackupDirectory` (TwoWay), +`Backups` (ObservableCollection), +`RefreshBackups()`. Ajouts dans Load / PushToSettings / constructeur |
| `src/WhispUI/Settings/SettingsBackupService.cs` | MODIFIÉ | `BackupInfo` gagne `DisplayName` calculé pour la ComboBox |

### 37e2f83 — Tooltips tray

| Fichier | Statut | Notes |
|---|---|---|
| `src/WhispUI/Shell/TrayIconManager.cs` | MODIFIÉ | "Redémarrer"/"Quitter" → "Restart"/"Quit". `UpdateStatus` passe en `StartsWith("Recording")`. Commentaire enrichi documentant la centralisation |
| `src/WhispUI/WhispEngine.cs` | MODIFIÉ | 3 statuts transitoires en ellipses : `Loading model…`, `Recording…`, `Transcribing…` |
| `src/WhispUI/App.xaml.cs` | MODIFIÉ | Dispatcher `StatusChanged` passe en `StartsWith` partout. Ancienne match `Réécriture` (FR) supprimée |

## Recap fichiers par "hot zone" (pour repérer les conflits)

Ces fichiers sont les zones les plus susceptibles d'être touchées par d'autres branches en parallèle. Pour chaque zone, je donne la nature du diff et les hooks à vérifier.

- **`src/WhispUI/App.xaml.cs`** — touché par `c81d084` (insertion log boot) et `37e2f83` (modif dispatcher StatusChanged). Si une branche parallèle a refondu `OnLaunched` ou les handlers d'events engine, conflits probables. Le bloc `_engine.StatusChanged += status => { ... }` est notamment réécrit en utilisant `StartsWith` au lieu de `==` — toute modif concurrente sur le même bloc nécessite résolution manuelle.
- **`src/WhispUI/Settings/AppSettings.cs`** — touché par `c81d084` (commentaire) et `a12b21e` (ajout `PathsSettings.BackupDirectory`). Conflit possible si une autre branche a aussi étendu `PathsSettings`. Si conflit : garder les deux propriétés.
- **`src/WhispUI/Settings/SettingsService.cs`** — touché par `c81d084` (constructeur, `ResolveModelsDirectory`) et `a12b21e` (ajout `ConfigPath`, `ResolveBackupDirectory`, `Reload`). Probabilité de conflit modérée : c'est un fichier central. La section `Reload()` est entièrement nouvelle et non-conflictuelle ; `ConfigPath` est un getter ajouté au sommet — collision possible si une autre branche a modifié ce bloc.
- **`src/WhispUI/Settings/GeneralPage.xaml`** — réorganisation massive en `b23a2e6` puis ajout en `8001564`. **Très conflictuel** si une autre branche a touché ce fichier. Recommandation : merger d'abord les autres branches qui touchent `GeneralPage.xaml`, puis cette branche, et résoudre la réorganisation à la main si besoin.
- **`src/WhispUI/Settings/ViewModels/GeneralViewModel.cs`** — ajout d'une section Backup (BackupDirectory + Backups + RefreshBackups). Couple avec le constructeur, Load, PushToSettings. Conflits possibles si une autre branche a aussi étendu le VM.
- **`src/WhispUI/Settings/GeneralPage.xaml.cs`** — ajout de 5 handlers à la fin. Peu de risque sauf si quelqu'un d'autre a aussi ajouté à la fin.
- **`src/WhispUI/Logging/CorpusPaths.cs`** — refacto léger : suppression de `ResolveDefaultBaseDir`. Si une autre branche utilisait cette méthode (qui n'était pas exposée publiquement à part), conflit ou breakage compile.
- **`src/WhispUI/Shell/TrayIconManager.cs`** — méthode `UpdateStatus` réécrite + commentaire. Si autre branche a touché `UpdateStatus`, conflit.
- **`src/WhispUI/WhispEngine.cs`** — 3 lignes uniquement (les RaiseStatus). Très peu de risque.

## Couplage avec autres worktrees Claude

À la date de cette session, les autres worktrees présentes :

- `D:/worktrees/transcription/clever-heisenberg-da5e9c` (`claude/clever-heisenberg-da5e9c`, sur `6dccc11`)
- `D:/worktrees/transcription/mystifying-lamarr-23a19a` (`claude/mystifying-lamarr-23a19a`, sur `6dccc11`)

Au moment de la session, ces deux worktrees n'avaient aucun commit au-dessus de leur base ni de modif uncommitted. Mais Louis a indiqué avoir "lancé pas mal de trucs en parallèle" — donc d'autres branches peuvent exister ou avoir progressé depuis. **Vérifier `git log` de chaque branche claude/* avant merge pour identifier les conflits.**

## Ordre de merge recommandé

Si plusieurs briefs existent, voici l'ordre proposé pour celui-ci :

1. **Mergerer en premier** les branches qui touchent UNIQUEMENT le code engine/whisper bas niveau (peu de conflit avec cette branche).
2. **Mergerer en avant-dernier** cette branche (`packaging-foundation`) car elle restructure les paths et la page General — autant qu'elle soit dessus quand on résout les conflits.
3. **Mergerer en dernier** les branches UI qui touchent la même surface General/Settings, en résolvant les conflits XAML à la main (la réorganisation est l'aspect le plus fragile).

Si cette branche est la seule en cours côté Settings/General, l'ordre n'a pas vraiment d'importance — merge direct.

**Commande type** :

```
git checkout main
git merge --no-ff claude/eloquent-raman-29019b -m "merge: packaging foundations - AppPaths + Backup section + tray polish"
```

Ou en plusieurs merges progressifs si on veut séparer les sujets — chaque commit tient debout seul.

## Tests pré-merge (validation runtime requise)

Aucun build n'a été lancé côté Claude (règle CLAUDE.md). Avant merge dans `main`, faire au minimum :

1. **Build dev** via le script habituel `scripts/build-run.ps1` ou MSBuild VS 2026. Doit compiler sans warning nouveau.
2. **Boot smoke test** : lancer l'app, vérifier dans `app.jsonl` la présence d'une ligne au format :
   ```
   AppPaths initialized | packaged=False | config=...\config | models=...\models | telemetry=...\benchmark\telemetry
   ```
3. **Settings UI** : ouvrir Settings → General, vérifier l'ordre des sections (Shortcuts → Appearance → Startup → Recording → Telemetry → Backup) et que la nouvelle section Backup est en bas avec ses 3 cards.
4. **Backup smoke test** : cliquer Create, vérifier qu'un fichier `settings-YYYYMMDD-HHmmss.json` apparaît dans `<exe>/config/backups/`. Changer le Theme. Cliquer Restore avec le snapshot pré-sélectionné, confirmer le ContentDialog. Vérifier que le Theme revient à la valeur d'avant et que la fenêtre Settings n'a pas eu besoin d'être fermée/rouverte.
5. **Tray tooltip** : démarrer l'app, tooltip doit dire `Whisp - Ready` (icône grise). Win+\` → tooltip transitoire `Whisp - Loading model…` (au premier hotkey), puis `Whisp - Recording…` (icône rouge), puis `Whisp - Transcribing…`, puis `Whisp - Ready` (icône grise). Clic droit sur l'icône : menu en anglais (Restart / Quit).
6. **Pipeline transcription complet** : faire une transcription qui passe par tous les états (record, transcribe, rewrite). Vérifier que le HUD bascule correctement sur chaque transition (Recording / Transcribing / Rewriting). C'est le test critique du commit 37e2f83 (le passage `==` → `StartsWith` aurait pu silencieusement casser le routing HUD si mal fait).

## Risques connus / pieges

- **Comportement strict iso-dev** côté chemins : la Phase 1 (AppPaths) ne change rien pour ton workflow dev. Si quelque chose se comporte différemment côté path, c'est un bug à signaler.
- **Mode packagé MSIX non testé** dans cette session : `IsPackaged=True` n'a pas été exercé runtime (pas de manifest MSIX à ce stade). C'est l'objet de la Phase 4 du plan packaging global. Le code branche correctement sur `IsPackaged` mais le chemin packagé reste théorique jusqu'à la Phase 4.
- **`_isSyncing` dans GeneralViewModel.RefreshBackups** : la méthode est appelée à la fin de `Load()` *en dehors* du bloc `_isSyncing`. C'est volontaire (l'observable collection ne déclenche pas de PushToSettings). Mais si quelqu'un ajoute des side-effects dans `RefreshBackups`, à reconsidérer.
- **Reload après Restore** : la méthode `SettingsService.Reload()` bypass volontairement le debounce timer. Si un Save() est en flight au même moment (slider drag), il est écrasé par le Reload. C'est acceptable car Restore est explicite, mais à avoir en tête si on ajoute des automations.
- **Confusion `Recording` vs `Recording…`** : tout matchage strict (`status == "Recording"`) cassé après ce commit. Le sweep a été fait dans App.xaml.cs et TrayIconManager.cs ; à surveiller s'il existe des matchs ailleurs (le grep n'en a pas trouvé d'autres au moment du commit).

## Etat post-merge — ce qui change pour les autres branches

- Toute branche qui résout des chemins via `AppContext.BaseDirectory + walk-up` doit migrer vers `AppPaths`. La compilation passe (rien n'est devenu obsolète au sens API), mais les nouvelles features doivent utiliser `AppPaths`.
- `PathsSettings.BackupDirectory` existe maintenant ; toute branche qui ajoute une nouvelle propriété à `PathsSettings` doit le savoir pour ne pas régresser.
- Le format de status engine inclut désormais des ellipses (`…`) pour les phases transitoires. Toute logique extérieure qui consomme `StatusChanged` doit utiliser `StartsWith` (ou un pattern matching plus robuste) au lieu de `==`.
- La nomenclature `brief--xxx--0.1.md` est introduite par ce fichier — type `brief` à ajouter à la liste des types valides documentés dans `CLAUDE.md` racine si on confirme cette convention.

## Reference plan global

Cette session livre la **Phase 1** complète et un morceau hors-plan du chantier packaging. Le plan global vit dans `C:\Users\Louis\.claude\plans\j-aimerais-que-tu-r-fl-chisses-shimmying-pebble.md` et liste 5 phases : path centralization (✓), fiche dépendances + health-check, first-run wizard, MSIX manifest + build pipeline, AppInstaller / auto-update.
