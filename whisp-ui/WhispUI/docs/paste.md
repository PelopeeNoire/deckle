# Paste — re-capture Stop, race fix, filet PID

## Cible paste — re-capture au Stop avec filet PID

`_pasteTarget` est captée au Start puis **re-capturée au Stop** via `GetForegroundWindow()`. Permet de coller dans le champ texte courant si l'utilisateur a basculé d'app pendant l'enregistrement.

**Filet** : si le foreground au Stop appartient au process WhispUI (HUD/LogWindow activé par un clic), on garde la cible Start — évite le faux positif « collé dans nos propres logs ». Voir [WhispEngine.cs:169](../WhispEngine.cs#L169).

## Fix race paste / Hide HUD — rendez-vous synchrone

**Avant** : `HudWindow.Hide()` était déclenché en async via `TranscriptionFinished` après `PasteFromClipboard`. `SendInput` étant asynchrone (il enfile les frappes dans la queue d'input du thread cible), le `SW_HIDE` async pouvait redistribuer l'activation pendant que le Ctrl+V était encore en vol — la LogWindow ouverte à côté pouvait récupérer le focus, le collage atterrissait là, et le log vert « Bout en bout OK » mentait.

**Fix** : nouveau callback `WhispEngine.OnReadyToPaste` invoqué **synchronement** entre `CopyToClipboard` et `PasteFromClipboard` ; câblé dans `App.xaml.cs` à `HudWindow.HideSync()` qui marshalle le `Hide` sur le thread UI via `DispatcherQueue` et **bloque l'appelant** sur un `ManualResetEventSlim` jusqu'à ce que `SW_HIDE` soit effectif.

Plus rien dans WhispUI ne touche à l'activation entre `SetForegroundWindow(target)` et la livraison des frappes. Pas de sleep, pas de polling — rendez-vous explicite par signal OS.

Voir [HudWindow.xaml.cs:255](../HudWindow.xaml.cs#L255), [WhispEngine.cs:36](../WhispEngine.cs#L36), [WhispEngine.cs:494](../WhispEngine.cs#L494), [App.xaml.cs:81](../App.xaml.cs#L81).

## `PasteFromClipboard` retourne `bool` — refus explicites

`PasteFromClipboard` retourne `bool` et refuse de coller (Warning avec mode opératoire « le presse-papier contient le texte — colle manuellement avec Ctrl+V ») dans tous ces cas :

- `_pasteTarget == 0`
- Cible appartient au process WhispUI lui-même (`GetWindowThreadProcessId == GetCurrentProcess().Id`) — filet contre le faux positif « collé dans WhispUI logs »
- `SetForegroundWindow` n'a pas réellement ramené la cible au foreground (vérifié via `GetForegroundWindow()` après sleep, pas via le retour bool)
- `GetFocusedClass == null`
- `SendInput` partiel

Si tout passe, le récap final devient le Step vert de bout en bout ; sinon `[DONE]` Verbose timings + le Warning orange explicatif.

## Tâche ouverte — paste « fantôme » intermittent

**Symptôme** : le pipeline log vert « Bout en bout OK » + PASTE « Ctrl+V envoyé à <cible> », mais rien n'apparaît dans le champ cible. Récurrent, pas systématique.

**Hypothèse** : la transcription n'a peut-être pas eu lieu (chunks capturés mais pas de texte recollé final), donc le clipboard contiendrait l'ancienne valeur ou rien — et le `SendInput` Ctrl+V ne ferait « rien » côté cible.

**À investiguer au prochain occurrence** : capturer les logs complets de la session fautive. Vérifier présence de la ligne TRANSCRIBE « texte recollé » et de la ligne CLIPBOARD « Texte copié (N chars) » avec `N > 0`. Si `N = 0` ou ligne absente → bug en amont (Whisper / pipeline). Si `N > 0` mais paste vide → bug `SendInput` ou délivrance, malgré le réordonnancement.
