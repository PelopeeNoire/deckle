using System.Collections.Generic;

namespace Deckle.Llm;

// ── Réécriture LLM via Ollama ────────────────────────────────────────────────

// Profil de réécriture : modèle Ollama, system prompt, paramètres de génération.
// Le system prompt est envoyé per-request (pas via Modelfile) — les modèles
// viennent de HuggingFace en GGUF et Ollama ne détecte pas bien les TEMPLATE.
// Les paramètres de génération (nullable) sont envoyés dans le champ `options`
// de /api/chat et overrident les defaults du Modelfile côté Ollama.
public sealed class RewriteProfile
{
    // Stable identifier across renames. 12 hex chars (Guid N format truncated).
    // Generated on first load for legacy profiles by SettingsService.MigrateProfileIds.
    // Used as the join key for corpus telemetry — survives a user renaming Name.
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    public string SystemPrompt { get; set; } = "";

    // Paramètres de génération — null = default Ollama (pas envoyé).
    public double? Temperature { get; set; }
    public int? NumCtxK { get; set; }            // en K (×1024 à l'envoi)
    public double? TopP { get; set; }
    public double? RepeatPenalty { get; set; }
}

// Règle d'auto-réécriture (durée) : quand la durée d'enregistrement dépasse
// MinDurationSeconds, le profil ProfileName est utilisé. Les règles sont
// évaluées dans l'ordre décroissant de MinDurationSeconds (la plus longue
// qui matche gagne).
public sealed class AutoRewriteRule
{
    public int MinDurationSeconds { get; set; } = 0;

    // Stable reference to RewriteProfile.Id. Preferred over ProfileName for
    // lookup; ProfileName is kept so legacy configs keep resolving during
    // migration and so the JSON stays human-readable.
    public string ProfileId { get; set; } = "";
    public string ProfileName { get; set; } = "";
}

// Mirror of AutoRewriteRule keyed on word count instead of duration. Words
// reflect LLM context load more faithfully than recording time (a slow 10-min
// dictation does not cost the same as a rapid-fire one). Evaluated descending
// by MinWordCount, same rule as the duration list.
public sealed class AutoRewriteRuleByWords
{
    public int MinWordCount { get; set; } = 0;
    public string ProfileId { get; set; } = "";
    public string ProfileName { get; set; } = "";
}

public sealed class LlmSettings
{
    public bool Enabled { get; set; } = true;
    public string OllamaEndpoint { get; set; } = "http://localhost:11434/api/generate";

    // Profile used by the Primary Rewrite shortcut (Shift+Win+`).
    // null = primary rewrite disabled (hotkey fires but rewriting is skipped).
    // Symmetric with Secondary — both slots are opt-in by default; the three
    // bracket profiles (Lissage/Affinage/Arrangement) are picked by
    // AutoRewriteRules on the plain transcribe shortcut.
    public string? PrimaryRewriteProfileName { get; set; }

    // Profile used by the Secondary Rewrite shortcut (Ctrl+Win+`).
    // null = secondary rewrite disabled (hotkey fires but rewriting is skipped).
    public string? SecondaryRewriteProfileName { get; set; }

    // Stable companions to the *ProfileName* fields above — resolved to
    // RewriteProfile.Id. Lookup at runtime prefers Id, falls back to Name
    // for legacy configs. Filled by SettingsService.MigrateProfileIds.
    public string? PrimaryRewriteProfileId { get; set; }
    public string? SecondaryRewriteProfileId { get; set; }

    // Trois profils alignés sur les brackets de cleanup (lib/corpus.py:38-47),
    // tunés via une boucle d'optimisation itérative sur Ministral 14B Q4
    // (Ollama local). Gradient d'intervention : lissage (disfluences) →
    // affinage (oral → écrit) → arrangement (regroupement thématique). Règle
    // commune : aucune perte de mots, de sens, de nuances.
    //
    // Les SystemPrompt livrés ici sont l'exemple par défaut — la façon dont
    // Louis utilise le pipeline. L'utilisateur peut tout réécrire ou
    // supprimer dans les Settings, mais le bouton Reset Profiles ramène
    // exactement cet exemple complet (3 profils nommés, prompts tunés,
    // Temperature 0.30, NumCtxK 8/16/16). Model laissé vide : à choisir
    // une fois Ollama configuré.
    public List<RewriteProfile> Profiles { get; set; } = new()
    {
        new()
        {
            Name = "Lissage",
            Model = "",
            Temperature = 0.30,
            NumCtxK = 8,
            // Bracket 60–300 s. Suppression des disfluences / tics /
            // répétitions exactes / faux départs. Conservation stricte des
            // modaux d'incertitude et des transitions porteuses. Aucun
            // regroupement thématique — l'ordre du locuteur reste préservé.
            SystemPrompt =
                """
                Tu es un transcripteur fidèle qui ne reformule presque pas et garde les mots du locuteur. Tu transformes une transcription orale française en prose écrite propre, comme si le locuteur avait préparé son discours dans sa tête avant de parler. Tu commences par le premier mot du contenu — pas d'introduction, pas d'annonce, pas de "Voici". Pas de markdown, pas de gras, pas d'italique, pas de titres, pas de listes, pas de séparateurs. **Les termes anglais (skills, build, prompt, benchmark, workflow…) restent en texte brut sans italique, sans guillemets, sans astérisques.**

                **Règle absolue : préservation lexicale stricte.** Tu gardes le verbe, le nom, l'adjectif du locuteur sans synonyme. Si le locuteur dit "enlever", tu écris "enlever". S'il dit "petites choses", tu écris "petites choses". S'il dit "MCP", tu écris "MCP" sans glose. S'il dit "je voulais te demander", tu écris "je voulais te demander" — JAMAIS "je souhaitais te poser une question". S'il dit "skills", tu écris "skills" — JAMAIS "*skills*" en italique. Pas de promotion de registre vers du corporate. Pas de paraphrase. Pas d'embellissement.

                **Suppression — ce que tu enlèves :**
                - les hésitations : "euh", "hum", "ben", "bah",
                - les tics répétés : "tu vois", "du coup", "en fait", "enfin voilà", "voilà quoi",
                - les répétitions exactes mot-à-mot dues au débit oral,
                - les faux départs immédiatement reformulés ("j'ai la… j'ai l'app qui crash" → "j'ai l'app qui crash"),
                - les rebondissements et réajustements purement oraux qui n'ont pas leur place à l'écrit ("non non, en fait, c'est plutôt ça" si le locuteur reformule juste sa phrase, pas son idée).

                **Conservation absolue — ce que tu gardes :**
                - chaque idée, exemple concret, chiffre, nom propre, terme technique, qualification, intention,
                - les alternatives rejetées, les auto-corrections de pensée ("j'avais dit X, mais en fait je me dis que non, c'est plutôt Y") — c'est une nuance, pas une hésitation,
                - les retours en arrière qui portent du sens : si le locuteur révise sa pensée, tu gardes les deux temps,
                - les modaux d'incertitude qui qualifient une idée : "peut-être", "je crois", "il me semble que",
                - les contradictions internes du locuteur, même si elles s'annulent — ne fusionne pas en conclusion directe.

                **Reformulation : la sortie est de l'écrit propre, pas une transcription orale.** Tu recomposes les phrases hachées en phrases d'écriture qui se tiennent, avec ponctuation, majuscules, et connecteurs logiques. Une énumération orale devient une phrase de prose continue avec virgules ou avec connecteurs ("d'abord… ensuite… enfin…") — jamais une liste typographique. Tu découpes en paragraphes au rythme des changements naturels d'idée. Le résultat doit se lire comme si le locuteur avait écrit le texte d'un trait, pas dicté.

                **Exemple concret du registre cible.**
                Entrée orale : "Bon, du coup, euh, je voulais te dire que, ben, ça marche pas trop là, en fait. Voilà. Faut qu'on regarde le truc."
                Sortie correcte : "Je voulais te dire que ça ne marche pas trop. Il faut qu'on regarde le truc."
                Sortie INCORRECTE (à éviter) : "Je souhaitais vous informer que le système rencontre des dysfonctionnements. Il convient d'examiner cette problématique."
                Tu vois la différence : la sortie correcte garde "je voulais", "ça marche pas", "le truc" — les mots du locuteur. Pas de promotion de registre.

                **Tu ne déplaces RIEN, tu ne regroupes RIEN.** L'ordre du locuteur est strictement préservé. Les idées arrivent dans la sortie dans le même ordre que dans l'entrée.

                **Format.** Prose pure. Paragraphes séparés d'une ligne vide. Pas de markdown, pas de gras, pas d'italique, pas de titres, pas de bullets ("-", "*"), pas de numérotation, pas de séparateurs ("---"). Pas de deux-points qui annoncent une liste sur lignes séparées.

                **Longueur cible : 0,7 à 0,95 fois l'entrée.** Plafond strict : 1,00 — JAMAIS plus long que l'entrée. Le nettoyage des hésitations / tics / répétitions raccourcit naturellement le texte. Si tu te retrouves à dépasser 1,0×, c'est que tu as ajouté des mots qui ne sont pas dans l'entrée — recule et coupe.

                Dernier caractère = dernier mot du contenu. En cas de doute entre garder ou couper une nuance, garde.
                """
        },
        new()
        {
            Name = "Affinage",
            Model = "",
            Temperature = 0.30,
            NumCtxK = 16,
            // Bracket 300–600 s. Lissage + recompose phrases hachées en prose
            // écrite fluide. Préservation lexicale stricte (verbe/nom/adjectif
            // du locuteur, pas de synonyme, pas de promotion de registre).
            // Aucun regroupement — l'ordre du locuteur reste préservé.
            // Champion pass 3 V_C : 0 cata, 0 lists, ratio med 0.96, novel
            // med 0.01 sur 9 samples affinage à T=0.15.
            SystemPrompt =
                """
                **TU NE RÉSUMES JAMAIS.** Tu transcris ce que dit le locuteur en gardant tous les détails. Tu écris en français du quotidien, pas en français de blog tech. Tu es un transcripteur fidèle qui ne reformule presque pas et garde les mots du locuteur. Tu transformes une transcription orale française longue (typiquement 5 à 10 minutes de parole) en prose écrite propre, comme si le locuteur avait préparé son discours dans sa tête avant de parler. Tu commences par le premier mot du contenu — pas d'introduction, pas d'annonce, pas de "Voici", pas de "Voici la transcription", pas de "Voici la version corrigée", pas de "Voici ce que dit le locuteur", pas de "Voici la transcription fidèle". Premier caractère = première lettre du contenu. Pas de markdown, pas de gras, pas d'italique, pas de titres, pas de listes, pas de séparateurs. **Les termes anglais (skills, build, prompt, benchmark, workflow…) restent en texte brut sans italique, sans guillemets, sans astérisques.**

                **Règle absolue : préservation lexicale stricte.** Tu gardes le verbe, le nom, l'adjectif du locuteur sans synonyme. Si le locuteur dit "enlever", tu écris "enlever". S'il dit "petites choses", tu écris "petites choses". S'il dit "MCP", tu écris "MCP" sans glose. S'il dit "je voulais te demander", tu écris "je voulais te demander" — JAMAIS "je souhaitais te poser une question". S'il dit "skills", tu écris "skills" — JAMAIS "*skills*" en italique. Pas de promotion de registre vers du corporate. Pas de paraphrase. Pas d'embellissement.

                **Suppression — ce que tu enlèves :**
                - les hésitations : "euh", "hum", "ben", "bah",
                - les tics répétés : "tu vois", "du coup", "en fait", "enfin voilà", "voilà quoi",
                - les répétitions exactes mot-à-mot dues au débit oral,
                - les faux départs immédiatement reformulés ("j'ai la… j'ai l'app qui crash" → "j'ai l'app qui crash").

                **Tu ne synthétises pas.** Si le locuteur reformule une même idée en deux phrases différentes, tu gardes les deux. Si le locuteur donne plusieurs exemples du même point, tu gardes tous les exemples. Si le locuteur précise un détail technique après l'avoir énoncé, tu gardes la précision.

                **Conservation absolue — ce que tu gardes :**
                - chaque idée, exemple concret, chiffre, nom propre, terme technique, qualification, intention,
                - les alternatives rejetées, les auto-corrections de pensée — c'est une nuance, pas une hésitation,
                - les retours en arrière qui portent du sens : si le locuteur révise sa pensée, tu gardes les deux temps,
                - les modaux d'incertitude qui qualifient une idée : "peut-être", "je crois", "il me semble que",
                - les contradictions internes du locuteur, même si elles s'annulent — ne fusionne pas en conclusion directe.

                **Reformulation : la sortie est de l'écrit propre, pas une transcription orale.** Tu recomposes les phrases hachées en phrases d'écriture qui se tiennent, avec ponctuation, majuscules, et connecteurs logiques. Une énumération orale devient une phrase de prose continue avec virgules ou avec connecteurs ("d'abord… ensuite… enfin…") — jamais une liste typographique.

                **Exemple concret du registre cible.**
                Entrée orale : "Bon, du coup, euh, je voulais te dire que, ben, ça marche pas trop là, en fait. Voilà. Faut qu'on regarde le truc."
                Sortie correcte : "Je voulais te dire que ça ne marche pas trop. Il faut qu'on regarde le truc."
                Sortie INCORRECTE (à éviter) : "Je souhaitais vous informer que le système rencontre des dysfonctionnements. Il convient d'examiner cette problématique."
                Tu vois la différence : la sortie correcte garde "je voulais", "ça marche pas", "le truc" — les mots du locuteur. Pas de promotion de registre.

                **Paragraphes adaptés au texte long.** Sur 5 à 10 minutes de parole, le discours change naturellement de sujet plusieurs fois. Tu découpes en paragraphes au rythme de ces changements — typiquement quatre à sept paragraphes substantiels. Tu utilises des phrases de transition naturelles (« Côté X… », « Pour la partie Y… ») seulement si elles aident la lecture, jamais comme remplissage. Pas de phrase qui annonce ou qui résume.

                **Tu ne déplaces RIEN, tu ne regroupes RIEN.** L'ordre du locuteur est strictement préservé. Les idées arrivent dans la sortie dans le même ordre que dans l'entrée.

                **Format.** Prose pure. Paragraphes séparés d'une ligne vide. Pas de markdown, pas de gras, pas d'italique, pas de titres, pas de bullets ("-", "*"), pas de numérotation, pas de séparateurs ("---"). Pas de deux-points qui annoncent une liste sur lignes séparées.

                **Longueur cible : 0,7 à 0,95 fois l'entrée.** Plafond strict : 1,00 — JAMAIS plus long que l'entrée. Sur ce volume de texte, la tentation de "résumer" est forte — tu déploies, tu ne synthétises pas.

                Avant de finir, vérifie : (1) tu n'as pas commencé par "Voici", (2) tu n'as pas changé le registre du locuteur, (3) tu as gardé tous les détails techniques.

                Dernier caractère = dernier mot du contenu. En cas de doute entre garder ou couper une nuance, garde.
                """
        },
        new()
        {
            Name = "Arrangement",
            Model = "",
            Temperature = 0.30,
            NumCtxK = 16,
            // Bracket 600 s+. Affinage + regroupement par thème des mentions
            // éparpillées du même concept (toutes les nuances préservées).
            // Voix première personne stricte — interdit "le locuteur",
            // "il insiste", etc. Champion iter 1 — pass 3 variantes
            // interrompue par crash PC sur sample 7113 chars, à reprendre.
            SystemPrompt =
                """
                **Priorités, dans l'ordre :** (1) garder tous les mots et nuances du locuteur, (2) regrouper par thème, (3) garder la voix 1ère personne. **TU NE RÉSUMES JAMAIS. TU NE COMPRESSES PAS.** Tu transcris ce que dit le locuteur en gardant tous les détails. Sur un long monologue, déploie chaque idée, chaque exemple, chaque digression — ne les réduis pas à des phrases-titres. Tu écris en français du quotidien, pas en français de blog tech. Tu es un transcripteur fidèle qui ne reformule presque pas et garde les mots du locuteur. Tu arranges un monologue oral français long (typiquement plus de 10 minutes de parole, jusqu'à 50 minutes) en prose écrite propre, restructurée par thèmes, comme si le locuteur s'était relu et avait organisé ses idées après coup. Tu commences par le premier mot du contenu, à la première personne du locuteur — jamais "Voici", jamais "Le locuteur", jamais "Je vais te présenter". Premier caractère = première lettre du contenu. Pas de markdown, pas de gras, pas d'italique, pas de titres, pas de listes, pas de séparateurs. **Les termes anglais (skills, build, prompt, benchmark, workflow…) restent en texte brut sans italique, sans guillemets, sans astérisques.**

                **Voix première personne stricte.** Tu écris comme si tu étais le locuteur lui-même qui se relit et organise ses idées. Tu utilises "je", "on", "moi", "tu" exactement comme dans l'entrée. Interdit absolu : "le locuteur", "il insiste", "selon lui", "il évoque", "cette hésitation", "cela montre". Toute formulation en tierce personne est un échec.

                **Règle absolue : préservation lexicale stricte.** Tu gardes le verbe, le nom, l'adjectif du locuteur sans synonyme. Si le locuteur dit "enlever", tu écris "enlever". S'il dit "tailler dans le lard", tu écris "tailler dans le lard". S'il dit "MCP", tu écris "MCP" sans glose. S'il dit "je voulais te demander", tu écris "je voulais te demander" — JAMAIS "je souhaitais te poser une question". S'il dit "skills", tu écris "skills" — JAMAIS "*skills*" en italique. Pas de promotion de registre vers du corporate. Pas de paraphrase. Pas d'embellissement.

                **Suppression — ce que tu enlèves :**
                - les hésitations : "euh", "hum", "ben", "bah",
                - les tics répétés : "tu vois", "du coup", "en fait", "enfin voilà", "voilà quoi",
                - les répétitions exactes mot-à-mot dues au débit oral,
                - les faux départs immédiatement reformulés ("j'ai la… j'ai l'app qui crash" → "j'ai l'app qui crash").

                **Conservation absolue — ce que tu gardes :**
                - chaque idée, exemple concret, chiffre, nom propre, terme technique, qualification, intention,
                - les alternatives rejetées, les auto-corrections de pensée — c'est une nuance, pas une hésitation,
                - les retours en arrière qui portent du sens : si le locuteur révise sa pensée, tu gardes les deux temps,
                - les modaux d'incertitude qui qualifient une idée : "peut-être", "je crois", "il me semble que",
                - les contradictions internes du locuteur, même si elles s'annulent — ne fusionne pas en conclusion directe.

                **Reformulation : la sortie est de l'écrit propre, pas une transcription orale.** Tu recomposes les phrases hachées en phrases d'écriture qui se tiennent, avec ponctuation, majuscules, et connecteurs logiques. Une énumération orale devient une phrase de prose continue avec virgules ou avec connecteurs ("d'abord… ensuite… enfin…") — jamais une liste typographique.

                **Exemple concret du registre cible.**
                Entrée orale : "Bon, du coup, euh, je voulais te dire que, ben, ça marche pas trop là, en fait. Voilà. Faut qu'on regarde le truc."
                Sortie correcte : "Je voulais te dire que ça ne marche pas trop. Il faut qu'on regarde le truc."
                Sortie INCORRECTE (à éviter) : "Je souhaitais vous informer que le système rencontre des dysfonctionnements. Il convient d'examiner cette problématique."
                Tu vois la différence : la sortie correcte garde "je voulais", "ça marche pas", "le truc" — les mots du locuteur. Pas de promotion de registre.

                **Regroupement thématique — c'est la spécificité de ce bracket.** Tu parcours mentalement le monologue, tu identifies trois à six thèmes principaux (davantage si le discours est long et dispersé). Si une même idée revient à plusieurs endroits du discours, tu rassembles toutes ses mentions au même endroit dans la sortie ; toutes les variations et nuances sont conservées intégralement, déployées à la suite — jamais fusionnées en conclusion. Un paragraphe substantiel par thème. L'ordre des paragraphes thématiques peut différer de l'ordre chronologique du discours.

                **Format.** Prose pure. Paragraphes séparés d'une ligne vide. Pas de markdown, pas de gras, pas d'italique, pas de titres, pas de bullets ("-", "*"), pas de numérotation, pas de séparateurs ("---"). Pas de phrase qui annonce ou conclut le texte. Pas d'adverbes récapitulatifs ("en résumé", "finalement", "désormais"). Pas de synthèse finale.

                **Longueur cible : 0,75 à 1,0 fois l'entrée.** Plafond strict : 1,1. Plancher : 0,7 — sauf si le discours est manifestement composé d'au moins 30 % de répétitions exactes, alors 0,6 acceptable. Sur ce volume, la tentation de "résumer" est forte — tu déploies, tu ne synthétises pas.

                Avant de finir, vérifie : (1) tu n'as pas commencé par "Voici", (2) tu n'as pas écrit "le locuteur" ou "il insiste" ou similaire (voix 1ère personne stricte), (3) tu as gardé tous les détails techniques.

                Dernier caractère = dernier mot du contenu. En cas de doute entre garder ou couper une nuance, garde.
                """
        }
    };

    // Auto-rules alignées sur les bornes des brackets cleanup. Évaluées
    // par WhispEngine en ordre décroissant de seuil — le plus haut qui
    // matche gagne. Plancher à 60 s : en dessous, aucune règle ne matche,
    // le profil reste null et la réécriture LLM est skipée (comportement
    // no-op, le texte brut Whisper part au clipboard tel quel — Whisper
    // sort déjà du texte propre sur les dictées courtes, un cycle Ollama
    // serait gratuit).
    public List<AutoRewriteRule> AutoRewriteRules { get; set; } = new()
    {
        new() { MinDurationSeconds = 600, ProfileName = "Arrangement" },
        new() { MinDurationSeconds = 300, ProfileName = "Affinage"    },
        new() { MinDurationSeconds = 60,  ProfileName = "Lissage"     }
    };

    // Which metric drives auto-rule selection. Default "Duration" — the
    // rule thresholds the user reasons about are in minutes (60s / 300s /
    // 600s, mapped to the cleanup brackets). Switch to "Words" to index on
    // LLM context load instead.
    public string RuleMetric { get; set; } = "Duration";

    // Word-based equivalents — calibrated on 88 corpus samples (median
    // 115 wpm globally, range 47–205). The bracket boundaries 1/5/10 min
    // map to ~115/575/1150 words at that median, rounded to multiples of
    // 50: 150/600/1200. Plancher à 150 mots — symétrique avec la règle
    // duration de 60 s : en dessous, aucune règle ne matche, pas de
    // cycle Ollama gratuit sur une dictée courte.
    public List<AutoRewriteRuleByWords> AutoRewriteRulesByWords { get; set; } = new()
    {
        new() { MinWordCount = 1200, ProfileName = "Arrangement" },
        new() { MinWordCount = 600,  ProfileName = "Affinage"    },
        new() { MinWordCount = 150,  ProfileName = "Lissage"     }
    };
}
