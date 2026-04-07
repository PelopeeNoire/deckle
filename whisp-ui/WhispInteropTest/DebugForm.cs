using System.Windows.Forms;

// ─── Fenêtre de debug ────────────────────────────────────────────────────────
//
// Affiche en temps réel, chunk par chunk :
//   - la durée du chunk audio
//   - le texte brut produit par Whisper
//   - si le chunk est accepté ou filtré
//   - le buffer cumulé après chaque chunk propre
//
// Log() est thread-safe : appelable depuis n'importe quel thread.
// InvokeRequired vérifie si l'appel vient d'un thread différent du thread UI.
// Si oui, BeginInvoke poste le rappel sur le thread UI pour éviter
// les accès croisés aux contrôles WinForms.

class DebugForm : Form
{
    readonly RichTextBox _log;

    // ShowWithoutActivation : empêche la fenêtre de prendre le focus quand Show() est appelé
    // programmatiquement. Garantit que le Show() au démarrage d'un enregistrement ne vole
    // pas le focus à l'application cible. Après le Show(), un Activate() explicite peut
    // donner le focus à la fenêtre si souhaité.
    protected override bool ShowWithoutActivation => true;

    public DebugForm()
    {
        // AutoScaleDimensions : DPI de référence utilisé au moment de la conception.
        // 96f = 100% (valeur de base Windows). Doit être défini avant AutoScaleMode.
        // AutoScaleMode.Dpi   : WinForms calcule un facteur = DPI_réel / 96.
        //   À 125% (120 DPI) → 120/96 = 1.25 : la fenêtre et ses contrôles
        //   sont agrandis de 25 % automatiquement. Tailles et positions restent
        //   exprimées en pixels logiques @ 96 DPI dans ce code.
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode       = AutoScaleMode.Dpi;

        Text            = "Whisp — Debug";
        Size            = new System.Drawing.Size(960, 450); // pixels logiques @ 96 DPI
        StartPosition   = FormStartPosition.Manual;
        Location        = new System.Drawing.Point(50, 50);
        // Sizable : barre titre Windows 11 standard (coins arrondis, boutons minimize/maximize/close).
        // SizableToolWindow donnait une petite barre non standard et pixelisée.
        FormBorderStyle = FormBorderStyle.Sizable;

        // FormClosing intercepté pour masquer plutôt que détruire.
        // La form est réutilisée à chaque transcription (Clear + Show au prochain appui).
        // Le bouton X masque — Fichier > Quitter quitte l'application entière.
        FormClosing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };

        // ── Barre de menu ─────────────────────────────────────────────────────
        // MenuStrip : barre ancrée en haut (Dock = Top implicite).
        // WinForms réserve automatiquement sa hauteur — le contrôle Fill en dessous
        // occupe le reste sans chevauchement.
        //
        // Pour ajouter un menu de niveau 1 : créer un ToolStripMenuItem,
        // le remplir avec DropDownItems.Add(...), puis l'ajouter à menuStrip.Items.
        // Pour ajouter un séparateur entre deux items : DropDownItems.Add(new ToolStripSeparator()).

        var menuStrip = new MenuStrip();

        // ── Menu Fichier ──────────────────────────────────────────────────────
        // Actions liées au cycle de vie de l'application.
        // Ajouter ici les actions globales (exporter les logs, ouvrir les préférences, etc.)

        var fichierMenu = new ToolStripMenuItem("Fichier");

        fichierMenu.DropDownItems.Add(new ToolStripMenuItem("Quitter", null,
            (_, _) => Application.Exit()));
        // ↑ Quitter : ferme l'application entière (pas juste cette fenêtre).
        //   Le bouton X de la fenêtre, lui, masque sans quitter (voir FormClosing).

        menuStrip.Items.Add(fichierMenu);

        // ── Menu Vue ──────────────────────────────────────────────────────────
        // Actions liées à l'affichage des logs.
        // Ajouter ici les options de filtrage, de niveau de verbosité, etc.

        var vueMenu = new ToolStripMenuItem("Vue");

        vueMenu.DropDownItems.Add(new ToolStripMenuItem("Effacer les logs", null,
            (_, _) => Clear()));
        // ↑ Effacer : vide la zone de texte. Équivalent au Clear() appelé à chaque transcription.


        menuStrip.Items.Add(vueMenu);

        // MainMenuStrip : indique à WinForms quelle MenuStrip est la principale.
        // Nécessaire pour que la fenêtre alloue correctement l'espace vertical du menu.
        MainMenuStrip = menuStrip;

        // ── Zone de log ───────────────────────────────────────────────────────
        // La police en points (9f) est déjà une unité physique indépendante du DPI —
        // pas besoin de l'ajuster manuellement.

        _log = new RichTextBox
        {
            ReadOnly    = true,
            ScrollBars  = RichTextBoxScrollBars.Vertical,
            Dock        = DockStyle.Fill,
            Font        = new System.Drawing.Font("Consolas", 9f),
            BackColor   = System.Drawing.Color.FromArgb(30, 30, 30),
            ForeColor   = System.Drawing.Color.FromArgb(220, 220, 220),
            WordWrap    = true,
        };

        // Ordre d'ajout : _log en premier (z-order bas), menuStrip en dernier (z-order haut).
        // WinForms résout le docking dans cet ordre : menuStrip se positionne en haut,
        // _log occupe le reste.
        Controls.Add(_log);
        Controls.Add(menuStrip);
    }

    public void Log(string line)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => Log(line)); return; }
        _log.SelectionStart  = _log.TextLength;
        _log.SelectionLength = 0;
        _log.SelectionColor  = System.Drawing.Color.FromArgb(220, 220, 220);
        _log.AppendText(line + "\r\n");
        _log.ScrollToCaret();
    }

    // LogError : même comportement que Log, mais en rouge.
    // À utiliser pour les chunks rejetés par whisper_full, les hallucinations,
    // ou tout incident dans le pipeline recording/transcription.
    public void LogError(string line)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => LogError(line)); return; }
        _log.SelectionStart  = _log.TextLength;
        _log.SelectionLength = 0;
        _log.SelectionColor  = System.Drawing.Color.FromArgb(255, 100, 100);
        _log.AppendText(line + "\r\n");
        _log.SelectionColor  = _log.ForeColor; // restaurer la couleur par défaut
        _log.ScrollToCaret();
    }

    public void Clear()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(Clear); return; }
        _log.Clear();
    }
}
