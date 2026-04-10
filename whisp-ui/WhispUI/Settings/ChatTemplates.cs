namespace WhispUI.Settings;

// ─── Présets de templates chat pour Modelfile Ollama ─────────────────────────
//
// Chaque template définit le format Go template utilisé par Ollama pour
// structurer les messages system/user/assistant avant de les envoyer au modèle.
//
// Ces présets couvrent les familles de modèles les plus courantes. Lors de
// l'import d'un GGUF brut, le bon template doit être sélectionné pour que
// le modèle interprète correctement les rôles de la conversation.

internal static class ChatTemplates
{
    /// <summary>Noms ordonnés des présets (pour ComboBox).</summary>
    public static readonly string[] Names =
    {
        "Mistral v0.3 (Ministral)",
        "Llama 3.x",
        "ChatML",
        "Phi-3",
        "Raw (no template)"
    };

    /// <summary>Template Go indexé par nom de préset.</summary>
    public static readonly Dictionary<string, string> Templates = new()
    {
        ["Mistral v0.3 (Ministral)"] =
            """
            {{- if .System }}[INST] {{ .System }}

            {{ end }}{{ .Prompt }} [/INST]{{ .Response }}
            """,

        ["Llama 3.x"] =
            """
            <|begin_of_text|>{{- if .System }}<|start_header_id|>system<|end_header_id|>

            {{ .System }}<|eot_id|>{{ end }}<|start_header_id|>user<|end_header_id|>

            {{ .Prompt }}<|eot_id|><|start_header_id|>assistant<|end_header_id|>

            {{ .Response }}<|eot_id|>
            """,

        ["ChatML"] =
            """
            {{- if .System }}<|im_start|>system
            {{ .System }}<|im_end|>
            {{ end }}<|im_start|>user
            {{ .Prompt }}<|im_end|>
            <|im_start|>assistant
            {{ .Response }}<|im_end|>
            """,

        ["Phi-3"] =
            """
            {{- if .System }}<|system|>
            {{ .System }}<|end|>
            {{ end }}<|user|>
            {{ .Prompt }}<|end|>
            <|assistant|>
            {{ .Response }}<|end|>
            """,

        ["Raw (no template)"] = "{{ .Prompt }}"
    };
}
