# Paste — doctrine UI Automation au Stop

## Politique

**Le clipboard est le défaut sûr. Le paste n'a lieu que si UIA confirme un champ texte.** Plus rien n'est capté au Start : pas de cible, pas de HWND focus, pas de filet anti-drift. On fait confiance à l'état du système au moment du Stop — l'utilisateur a eu tout le temps de l'enregistrement + de la transcription + de la réécriture LLM pour placer son curseur où il veut.

Renversement explicite par rapport à l'implémentation précédente (capture au Start, `SetForegroundWindow`, comparaison HWND exacte au Stop) — cf. section historique en bas.

## Ce qui se passe dans `PasteFromClipboard`

Ordre des checks, tous refusent en clipboard-seul si faux :

1. `GetForegroundWindow()` ≠ 0.
2. Foreground n'appartient pas au process WhispUI (filet contre le faux positif « collé dans nos propres logs »).
3. **`UIAutomation.IsFocusedElementTextEditable(out diag)` renvoie `true`**. La probe lit `CUIAutomation.GetFocusedElement()` puis `IUIAutomationElement.GetCurrentPropertyValue(UIA_ControlTypePropertyId)` et ne valide que `Edit` (50004) ou `Document` (50030). Toute autre issue — UIA refuse, exception COM, ControlType différent, process protégé — est traitée comme « pas sûr ».
4. `SendInput` complet (4 events : `VK_CONTROL↓ VK_V↓ VK_V↑ VK_CONTROL↑`).

Si (1-4) passent → HUD `ShowPasted()` (flash vert « Pasted », 500 ms). Sinon → HUD `ShowCopied()` (« Copied to clipboard — Ctrl+V where you want it », 3 s).

## Pourquoi UIA plutôt qu'un match sur `class name`

UIA est l'API canonique d'accessibilité Windows et répond à la bonne question : *cet élément accepte-t-il de la saisie ?*. Fonctionne à travers Win32 classique, WinForms, WPF, WinUI, Chromium (input HTML, contenteditable), Qt, Electron, UWP. Un match sur `class name` (comme `"Edit"`, `"RichEdit50W"`, `"Chrome_RenderWidgetHostHWND"`) rate systématiquement les frameworks modernes et produit des faux positifs sur des contrôles non éditables qui réutilisent une classe Edit.

## Rendez-vous synchrone `HideSync` (conservé)

Juste avant `PasteFromClipboard`, `OnReadyToPaste` est invoqué synchronement, câblé à `HudWindow.HideSync()`. Le HUD est caché de façon **bloquante** (marshal `DispatcherQueue` + `ManualResetEventSlim`) avant que `SendInput` parte. Sans ce verrou, le `Hide` async pouvait redistribuer l'activation pendant que Ctrl+V était encore en vol dans la queue du thread cible. Rien dans WhispUI ne touche à l'activation entre le Hide effectif et la délivrance des frappes.

Voir [HudWindow.xaml.cs](../HudWindow.xaml.cs), [WhispEngine.cs](../WhispEngine.cs), [App.xaml.cs](../App.xaml.cs), [Interop/UIAutomation.cs](../Interop/UIAutomation.cs).

## Thread COM pour UIA

`PasteFromClipboard` tourne sur le worker thread de l'engine (thread de fond, MTA par défaut sous .NET Core). UIA client supporte MTA depuis Windows 7 — pas d'init COM explicite nécessaire. L'instance `IUIAutomation` est lazy-instanciée une fois puis réutilisée (thread-safe, cache global dans `UIAutomation`).

## Historique — ce qui a été retiré

Jusqu'à 2026-04-15 inclus, la logique était radicalement différente et vit maintenant comme dette retirée.

**Capture au Start** : `GetForegroundWindow()` captait le HWND top-level à la 1ʳᵉ hotkey, puis `GUITHREADINFO.hwndFocus` captait le HWND focus précis. Les deux étaient stockés volatiles (`_pasteTarget`, `_pasteFocusHwnd`).

**Restauration forcée au Stop** : `SetForegroundWindow(_pasteTarget)` + `Thread.Sleep(50)` + vérif `GetForegroundWindow() == _pasteTarget`. Si KO → refus.

**Comparaison sub-window** : `GetFocusedHwnd` comparé avec `_pasteFocusHwnd`. Si divergence → refus (scénario « l'utilisateur a cliqué dans un autre champ de la même fenêtre entre Start et Stop »).

**Ce qui ne marchait pas** : même avec ces filets, le paste pouvait atterrir dans une fenêtre voisine (`AltMenuSuppressor` avait été tenté puis retiré — cf. roadmap R8.1 fermé abandonné). Pire, la restauration `SetForegroundWindow` était intrusive : elle ramenait une fenêtre que l'utilisateur avait peut-être volontairement laissée en arrière-plan, juste pour y coller du texte automatiquement. Le flash vert « succès » pouvait mentir quand la cible avait changé de nature entre Start et Stop (champ détruit, fenêtre modale superposée…).

**Retiré** : `_pasteTarget`, `_pasteFocusHwnd`, paramètres `pasteTarget` / `pasteFocusHwnd` de `StartRecording`, `SetForegroundWindow` + sleep + vérif foreground, check sub-window focus, helpers `Win32Util.GetFocusedClass` et `Win32Util.GetFocusedHwnd` (plus aucun appelant), hook `AltMenuSuppressor` (déjà retiré avant cette refonte).
