# Système de wording et localisation — WhispUI

## Contexte

Référence unique pour le système de strings user-facing. Sert deux objectifs
qui se renforcent :

- **Édition des wordings sans toucher au code** — modifier un titre, une
  description, un message d'erreur revient à éditer une cellule dans
  `Strings/en-US/Resources.resw` plutôt qu'à naviguer dans 15 fichiers
  XAML et C# disséminés.
- **Localisation préparée** — l'infra retenue est le mécanisme natif
  Windows App SDK : MRT/PRI + `.resw` + `ResourceLoader` + `x:Uid`. Une
  langue supplémentaire s'ajoute en posant un nouveau
  `Strings/<lang>/Resources.resw` à côté, sans changement de code.

L'app reste user-facing **anglais d'emblée** (cf. CLAUDE.md projet). Cette
itération produit uniquement le fichier `en-US`. Pas de FR, pas de
dropdown de sélection de langue dans Settings — le runtime résout sur la
langue d'affichage Windows et tombe sur `en-US` par défaut.

Version `0.1` : état initial après migration intégrale des 15 surfaces
décrites ci-dessous.

---

## Architecture

Trois pièces reliées par convention de fichiers et de noms.

**Fichier source des strings** :
`src/WhispUI/Strings/en-US/Resources.resw` (XML, format hérité ResX). Une
entrée par clé. Une seule langue présente aujourd'hui ; un futur
`Strings/fr-FR/Resources.resw` se posera à côté sans modification du
csproj.

**Génération PRI** : la PRI (Package Resource Index) est un fichier
binaire compilé à partir des `.resw` au build, lu par MRT au runtime.
Pour un projet unpackagé comme WhispUI
(`<WindowsPackageType>None</WindowsPackageType>`), le pipeline a besoin
de `<EnableMsixTooling>true</EnableMsixTooling>` dans le csproj pour
que `WhispUI.pri` soit copié à côté de `WhispUI.exe` au Publish. C'est
**déjà actif** dans `WhispUI.csproj` indépendamment de la localisation
(la PRI 1.8 embarque aussi les `.xbf` XAML — sans elle, l'app ne démarre
pas). Aucune modification supplémentaire de l'infra build n'est requise
pour les `.resw`.

**Langue neutre** : `<DefaultLanguage>en-US</DefaultLanguage>` dans le
csproj. Sans cette balise, le résolveur MRT trouve bien
`Strings/en-US/Resources.resw`, mais aucune langue n'est déclarée comme
fallback ; les `x:Uid` peuvent rester vides quand la langue système
diverge. La balise verrouille `en-US` comme fallback inconditionnel.

**Consommation** : deux modes côte à côte.

- `x:Uid="MyKey"` en XAML — résout automatiquement les propriétés
  `MyKey.Text`, `MyKey.Header`, `MyKey.Description`, `MyKey.Title`,
  `MyKey.Content`, `MyKey.PlaceholderText`,
  `MyKey.ToolTipService.ToolTip`. Le résolveur XAML lit
  `Strings/<lang>/Resources.resw` au runtime et applique les valeurs
  trouvées à l'élément. Mécanisme zero-code côté XAML.
- `Loc.Get("Key")` et `Loc.Format("Key", args...)` en C# — wrapper
  statique défini dans `src/WhispUI/Localization/Loc.cs`. Utilisé pour
  tout ce qui est construit programmatiquement : ConsentDialogs, status
  moteur, `UserFeedback`, HUD, tray, status dynamiques du setup wizard.

L'API utilisée est
`Microsoft.Windows.ApplicationModel.Resources.ResourceLoader` (Windows
App SDK), **pas** l'ancien
`Windows.ApplicationModel.Resources.ResourceLoader` (UWP). Les deux
existent dans `Microsoft.WindowsAppSDK 1.8` mais seul le premier
fonctionne en unpackagé.

---

## Convention de clés

Un seul fichier `Resources.resw` monolithique. Les préfixes structurent
la lecture humaine.

### x:Uid en XAML — `<UidValue>.<Property>`

L'`UidValue` est libre (à choisir clair) ; le `.<Property>` est résolu
automatiquement par le framework. Une même `UidValue` peut porter
plusieurs propriétés.

```
<TextBlock x:Uid="GeneralPageTranscribeCard" />
```

dans le `.resw` :

```
<data name="GeneralPageTranscribeCard.Header"><value>Transcribe</value></data>
<data name="GeneralPageTranscribeCard.Description"><value>Start and stop transcription. The text is copied to the clipboard.</value></data>
```

Convention `UidValue` : `<Surface><ElementRole>` en CamelCase, sans
underscore ni séparateur. Exemples retenus dans la base de code :

- `LogWindowSearchBox`
- `GeneralPageTranscribeCard`
- `LlmEnableCard`
- `SetupChoicesInstallLocation`

### Lookup direct C# — `<Surface>_<Purpose>`

Pour les strings consommées via `Loc.Get`. CamelCase pour `Surface`,
underscore pour le séparateur, CamelCase ou minuscule pour `Purpose`.
Exemples :

- `CorpusConsent_Title`
- `CorpusConsent_Body_Intro`
- `CorpusConsent_Body_WhatHeader`
- `CorpusConsent_PrimaryButton`
- `CorpusConsent_CloseButton`
- `Setup_StepTitle_Choices`

### Strings paramétrées — suffixe `_Format`

Pour les strings qui prennent des arguments runtime (noms de profils,
chemins, URLs). Placeholders de composite-format `{0}`, `{1}`, ...
consommées par `Loc.Format`. Le suffixe `_Format` est obligatoire et
visible dans le code consommateur.

Exemples :

- `Status_Rewriting_Format` = `Rewriting ({0})…`
- `Tray_Tooltip_Format` = `Whisp — {0}`
- `Llm_StartOllama_Format` = `Start Ollama or check the endpoint setting ({0}).`
- `Gguf_FileNotFound_Format` = `GGUF file not found: {0}`

### Strings réutilisables — préfixe `Common_`

Pour les boutons et statuts génériques qui apparaissent sur plusieurs
surfaces. Avant de créer une clé spécifique de surface, vérifier qu'il
n'existe pas déjà un `Common_*`. Exemples :

- `Common_Cancel`
- `Common_Back`
- `Common_Next`
- `Common_Enable`
- `Common_Reset`
- `Common_Remove`
- `Common_Keep`
- `Common_Browse`

Une clé `Common_*` ne contient jamais de paramètre. Les variantes
contextuelles (`Cancel install`, `Reset all`) gardent leur clé spécifique
de surface — `Common_*` reste l'expression courte canonique.

---

## Strings techniques non traduites

Liste fermée des chaînes qui restent **hardcodées** dans le code et ne
passent jamais par le `.resw` ni par `Loc`. Toute addition à cette liste
demande une justification documentée ici.

- **Noms de fichiers et extensions** — `app.jsonl`, `latency.jsonl`,
  `microphone.jsonl`, `corpus.jsonl`, `settings.json`, `WhispUI.pri`,
  `WhispUI.exe`. Les noms sont des contrats avec le filesystem et avec
  les outils de diagnostic ; les traduire casse les scripts et la
  télémétrie.
- **URLs et endpoints** — `http://localhost:11434/api/chat` (Ollama
  default), URLs de redist GitHub, schémas `ms-resource://`. Identifiants
  techniques.
- **Noms de produits et marques** — `WhispUI`, `Whisp`, `Ollama`,
  `Silero VAD`, `whisper.cpp`. Identité produit ; pas de traduction
  possible ni souhaitable.
- **Noms de modèles Whisper** — `base`, `small`, `medium`, `large-v3`,
  `tiny`. Tag d'identification du modèle, exposé tel quel dans l'UI.
- **Noms techniques de subsystèmes loggés** — entrées de
  `LogSource` (`App`, `Hotkey`, `Engine`, `Record`, `Transcribe`, ...).
  Vocabulaire interne, lu par les développeurs dans la LogWindow et le
  JSONL, pas par les utilisateurs au sens UX du terme.

Tout autre texte visible par l'utilisateur passe par le `.resw`.

---

## Ajouter une nouvelle string

1. Choisir le pattern qui correspond (cf. section précédente). Si la
   string apparaît dans un attribut XAML statique, viser `x:Uid`. Si elle
   est construite en C#, viser `Loc.Get` ou `Loc.Format`.
2. Avant d'inventer une clé spécifique, vérifier qu'un `Common_*` ne
   couvre pas déjà le besoin. Préférer la réutilisation à la duplication.
3. Ajouter l'entrée dans `Strings/en-US/Resources.resw`. L'ordre dans le
   fichier suit les sections — Common, puis par surface (Setup, Settings,
   Engine, Logging, ...). Garder le fichier groupé pour faciliter la
   relecture humaine.
4. Côté code consommateur :
   - XAML : ajouter `x:Uid="<UidValue>"` sur l'élément, retirer la
     valeur littérale de l'attribut concerné.
   - C# : remplacer le littéral par `Loc.Get("<key>")` ou
     `Loc.Format("<key>_Format", args...)`. Importer
     `WhispUI.Localization`.
5. Builder via `MSBuild.exe` (cf. CLAUDE.md projet — `dotnet build` est
   cassé). Vérifier au runtime que la string s'affiche bien (en DEBUG,
   une clé manquante apparaît comme `[!key]` à l'écran — assez voyant
   pour être détecté en quelques secondes).

---

## Ajouter une langue future

Quand le moment vient (FR, ES, ...) :

1. Créer `src/WhispUI/Strings/<lang>/Resources.resw` en copiant le
   fichier `en-US` puis en traduisant chaque `<value>`. Garder les clés
   strictement identiques.
2. **Ne pas toucher** aux strings techniques de la liste plus haut —
   chaînes de fichiers, URLs, noms de produit.
3. Pour les strings paramétrées `_Format`, garder le même nombre et le
   même ordre de placeholders `{0}`, `{1}`. La grammaire de la langue
   cible peut imposer un autre ordre — `string.Format` accepte les
   placeholders dans n'importe quel ordre dans la chaîne, c'est exactement
   à ça qu'ils servent.
4. Au runtime, MRT résout sur la **langue d'affichage Windows**. Pour
   exposer une sélection manuelle dans Settings (override), introduire
   un `ResourceContext` avec
   `QualifierValues["Language"] = "<lang>"` ou
   `Languages = new[] { "<lang>" }` et le câbler à un setting persistant.
   Hors scope `0.1`.

---

## Pièges et notes opérationnelles

- **API à utiliser** :
  `Microsoft.Windows.ApplicationModel.Resources.ResourceLoader` (Windows
  App SDK). L'ancien `Windows.ApplicationModel.Resources.ResourceLoader`
  (UWP) est encore référencé dans certains résultats de recherche
  obsolètes — il ne marche pas en unpackagé.
- **Création du `ResourceLoader`** : se fait après l'init runtime
  Windows App SDK (auto-bootstrap via `<WindowsPackageType>None</WindowsPackageType>`
  qui appelle l'API bootstrapper). `Loc._loader` est paresseux pour
  garantir cette commande temporelle ; n'utiliser `Loc.Get` qu'à partir
  du moment où `App.OnLaunched` a démarré.
- **Clé manquante** : `ResourceLoader.GetString` retourne **string vide**
  par contrat WinAppSDK, sans exception. En DEBUG, `Loc.Get` substitue
  `[!key]` pour rendre la régression visible. En RELEASE le
  comportement par défaut est conservé.
- **Inspection de la PRI** : `MakePri.exe dump <chemin>.pri` (SDK Windows
  10) liste les ressources embarquées et leurs clés. Utile pour vérifier
  que le pipeline build a bien embarqué un `.resw` après modification.
- **`x:Uid` invalide en XAML** : génère un avertissement `WMC*` au
  build mais n'empêche pas la compilation. Surveiller la sortie MSBuild
  pour rattraper les Uids cassés tôt.

---

## Surfaces couvertes en `0.1`

15 surfaces user-facing, ~200 strings au total. Liste de référence pour
les futures itérations.

| Surface | Fichier(s) | Mode |
|---|---|---|
| NavigationView Settings | `Settings/SettingsWindow.xaml` | x:Uid |
| GeneralPage | `Settings/GeneralPage.xaml(.cs)` | x:Uid + Loc |
| WhisperPage | `Settings/WhisperPage.xaml(.cs)` | x:Uid + Loc |
| LlmPage | `Settings/LlmPage.xaml(.cs)` | x:Uid + Loc |
| Llm sections | `Settings/Llm/Llm*Section.xaml(.cs)` | x:Uid + Loc |
| GGUF import | `Settings/Llm/GgufImport/GgufImport*.cs` | Loc |
| Consent dialogs | `Settings/{ApplicationLog,AudioCorpus,Corpus,MicrophoneTelemetry}ConsentDialog.cs` | Loc |
| LogWindow | `Logging/LogWindow.xaml(.cs)` | x:Uid + Loc |
| HUD | `Hud/HudWindow.xaml.cs` | Loc |
| Tray | `Tray/TrayIconManager.cs` | Loc |
| Engine status | `WhispEngine.cs` (RaiseStatus) | Loc |
| Engine UserFeedback | `WhispEngine.cs` (`new UserFeedback`) | Loc |
| Setup window shell | `Shell/Setup/SetupWindow.xaml(.cs)` | x:Uid + Loc |
| Setup choices | `Shell/Setup/ChoicesPage.xaml(.cs)` | x:Uid + Loc |
| Setup install + summary | `Shell/Setup/{Installing,Summary}Page.xaml(.cs)` | x:Uid + Loc |
