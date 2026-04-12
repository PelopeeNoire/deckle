---
name: debug-logging-design
description: "Principes de conception du logging et de l'observabilité debug dans les applications. Utiliser ce skill AVANT de coder quoi que ce soit qui implique du logging, des fenêtres de debug, des panneaux de diagnostic, ou de la gestion d'erreurs. Déclencher aussi quand on structure une nouvelle fonctionnalité et qu'on veut penser son observabilité dès la conception, quand on définit ce qu'une application doit logger et comment, quand on conçoit une interface de visualisation de logs ou un panneau debug, ou quand on rencontre un problème de diagnostic difficile et qu'on veut améliorer l'instrumentation existante. Ce skill est agnostique de la technologie — il donne le cadre de pensée. La technique d'implémentation relève des skills spécialisés par stack."
---

# Logging et observabilité debug — principes de conception

Ce skill pose le cadre de réflexion pour rendre une application observable et debuggable. Il ne prescrit ni langage ni framework. Il se consulte avant d'écrire du code, au moment où on structure une fonctionnalité, et quand on conçoit un outil de visualisation de logs.

## Penser l'observabilité avant d'écrire le code

Le logging n'est pas une couche qu'on ajoute après coup. Une fonctionnalité mal instrumentée reste opaque même avec la meilleure infrastructure de collecte. La question à se poser au moment du design n'est pas "qu'est-ce que je vais logger" mais "si cette fonctionnalité plante dans trois mois, de quoi aurai-je besoin pour comprendre pourquoi en moins de cinq minutes".

Trois questions à se poser pour chaque fonctionnalité ou composant avant de coder :

**Quels sont les changements d'état critiques ?** Identifier les moments où le système passe d'un état à un autre : démarrage, arrêt, bascule, changement de configuration, transitions dans une machine à états. Ce sont les points de log obligatoires.

**Quels sont les points de décision ?** Partout où le code prend un branchement non trivial (retry, fallback, sélection d'un chemin parmi plusieurs), le log doit capturer la décision prise et pourquoi. Un branchement silencieux est une dette d'observabilité.

**Quel est le périmètre d'impact d'un échec ?** Si une opération échoue, qui est touché ? Un utilisateur, une session, tout le système ? La réponse dicte le niveau de sévérité et la granularité du contexte à capturer.

Cette réflexion en amont produit une "carte d'instrumentation" implicite du composant. Elle guide ensuite le placement des logs dans le code.


## Quoi logger — le signal utile

Un log n'a de valeur que s'il permet une décision ou une action. Le critère central : "est-ce qu'un opérateur peut comprendre quoi faire en lisant ce message, sans ouvrir le code source ?"

### Logger systématiquement

Les changements d'état du système ou du composant (démarrage, arrêt, connexion/déconnexion à une ressource, bascule de mode).

Les échecs d'opérations métier, avec la raison précise. "Échec du paiement" ne suffit pas — "Échec du paiement : timeout après 3 retries vers le provider X, dernière réponse HTTP 503" est exploitable.

Les points de branchement logique non triviaux, surtout dans les algorithmes complexes ou les machines à états.

Les traces d'audit quand le contexte l'exige : actions utilisateur privilégiées, accès à des données sensibles, modifications de permissions ou de configuration.

### Ne jamais logger

Les secrets, tokens, mots de passe, clés API — même dans des objets sérialisés "pour debug". C'est l'anti-pattern de sécurité le plus courant. Mettre en place un filtrage ou un masquage systématique avant émission.

Les données personnelles identifiantes (PII) en clair. Hasher, tokeniser ou masquer.

Le bruit de routine sans valeur décisionnelle : health checks, heartbeats, polling de succès répétitif. Si nécessaire, échantillonner agressivement.


## Niveaux de sévérité — usage discipliné

La taxonomie standard (RFC 5424) va de 0 (Emergency) à 7 (Debug). En pratique, pour des applications desktop et des projets à échelle individuelle ou petite équipe, quatre niveaux suffisent au quotidien.

**Error** — une opération a échoué et n'a pas pu aboutir. L'utilisateur ou le système est impacté. Chaque log Error doit contenir assez de contexte pour diagnostiquer sans reproduire.

**Warning** — une situation anormale qui n'est pas encore un échec mais qui pourrait le devenir. Retries successifs, ressource proche de la saturation, dépendance lente. C'est le signal d'alerte précoce.

**Info** — événements normaux mais significatifs. Démarrage de service, fin d'une opération métier importante, changement de configuration. C'est le fil narratif du fonctionnement normal.

**Debug** — détails granulaires pour le développement et le dépannage profond. Désactivé en utilisation normale, activé à la demande. Attention : un niveau Debug trop bavard en production sature les logs et masque le signal.

Anti-pattern fréquent : utiliser Error pour des conditions normales (ex : "utilisateur non trouvé" lors d'une recherche). Ça déclenche de fausses alertes et fatigue l'attention. Si c'est un résultat attendu, ce n'est pas une erreur.


## Structure des logs — rendre la donnée interrogeable

Le texte libre ("printf logging") est un anti-pattern. Chaque log doit être un objet structuré avec des champs nommés et stables, que ce soit en JSON ou dans tout format structuré adapté à la stack.

### Champs invariants d'un log exploitable

**timestamp** — UTC, précision milliseconde, format ISO 8601. Sans horodatage précis, la reconstitution chronologique d'une cascade de pannes est impossible.

**level** — niveau de sévérité normalisé.

**source** — identifiant du composant émetteur. Dans une app monolithique, c'est le module ou la classe. Dans un système distribué, c'est le service.

**correlation_id** — identifiant unique propagé à travers une chaîne d'opérations liées. C'est le "champ doré" : il transforme des logs isolés en récit cohérent. Même dans une app desktop, si une action utilisateur déclenche plusieurs opérations séquentielles ou parallèles, un ID de corrélation permet de filtrer tout ce qui s'est passé dans le cadre de cette action.

**message** — description concise et stable de l'événement. Éviter les chaînes dynamiques non paramétrées (concaténation de variables dans le texte libre) car elles empêchent l'agrégation automatique.

Le principe : un log sans contexte d'identification (qui, quoi, dans quel cadre) est un log qu'on ne retrouvera pas quand on en aura besoin.


## Conception d'une interface de debug / log viewer

L'interface de visualisation des logs est le cockpit de l'ingénieur pendant un incident ou une session de debug. Son design doit réduire la charge cognitive et accélérer le diagnostic. C'est un problème de design d'information autant que de développement.

### Patterns d'interface éprouvés

**Distribution temporelle.** Un histogramme en haut de la vue montrant le volume de logs par niveau de sévérité sur l'axe du temps. Ça permet de repérer visuellement le moment exact d'un incident (pic de rouge) et de cadrer la fenêtre temporelle d'investigation.

**Recherche par facettes.** Un volet latéral listant les attributs (source, level, correlation_id, etc.) avec leur fréquence. Filtrer en un clic plutôt qu'en tapant des requêtes. Chaque facette réduit l'espace de recherche sans perdre le contexte global.

**Timeline extensible.** Liste chronologique où chaque ligne affiche les champs essentiels. Un clic déploie le détail complet de l'entrée. Le compromis est entre densité d'information (voir beaucoup de lignes) et lisibilité (ne pas surcharger visuellement).

**Regroupement des répétitions.** Quand des milliers de logs identiques se répètent (erreurs en cascade, boucles), les regrouper en une entrée sommaire avec un compteur. Sans ça, les pages de répétitions noient le signal.

### Navigation critique

**Logs voisins (surrounding context).** À partir d'un log filtré, pouvoir afficher instantanément les logs qui se sont produits juste avant et après, sur le même composant ou pour la même corrélation — même s'ils ne correspondent pas aux filtres actifs. C'est souvent dans le voisinage immédiat qu'on trouve la cause.

**Passage au contexte.** Pouvoir naviguer d'un log vers les informations liées : la trace complète d'une opération, l'état du composant à ce moment, les métriques associées. L'objectif est de ne jamais avoir à changer d'outil ou reconstruire manuellement un contexte.

### Signaux visuels

Code couleur par niveau de sévérité (rouge/orange/bleu/gris est le standard). Mise en évidence des logs "nouveaux" — ceux qui n'ont jamais été vus avant, souvent signe d'un changement récent qui a introduit un bug. La couleur sert le triage, pas la décoration.


## Anti-patterns à connaître

**Logs non structurés.** Du texte libre qu'on ne peut interroger qu'avec des regex fragiles. Coût de maintenance élevé, automatisation impossible.

**Debug permanent en production.** Génère un volume ingérable qui sature le stockage et cache les erreurs réelles sous le bruit.

**Erreurs sans contexte.** "An error occurred" sans identifiant d'objet, d'utilisateur ou d'état. Inutile pour le diagnostic.

**Secrets dans les logs.** Sérialiser un objet entier "pour debug" sans vérifier qu'il ne contient pas de mot de passe ou de token.

**Logging synchrone sur le chemin critique.** Faire attendre l'utilisateur le temps qu'un log soit écrit sur disque ou envoyé sur le réseau. Le logging doit être non-bloquant par défaut.

**Niveaux mal utilisés.** Confondre un résultat attendu (pas d'erreur) avec une erreur (Error). Ça crée du bruit dans les alertes et de la fatigue d'astreinte.


## Checklist de conception

Avant de coder une fonctionnalité, vérifier :

Les changements d'état critiques sont identifiés et auront un log.
Les points de décision non triviaux sont identifiés et seront tracés.
Un identifiant de corrélation existe pour relier les opérations d'une même action.
Le format est structuré avec des champs stables et nommés.
Aucune donnée sensible ne peut se retrouver dans un log, même par sérialisation indirecte.
Les niveaux de sévérité sont attribués selon l'impact réel, pas selon l'intuition du développeur.
Le logging est non-bloquant et n'impacte pas le chemin critique de l'application.

Si une interface de debug est prévue :

La distribution temporelle est visible pour situer les incidents.
Le filtrage par facettes est disponible sur les champs structurés.
Les logs voisins sont accessibles depuis n'importe quelle entrée filtrée.
Les répétitions sont regroupées pour ne pas noyer le signal.
