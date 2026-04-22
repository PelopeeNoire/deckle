# HudPlayground

Dev tool. Not shipped.

Standalone WinUI 3 WinExe that hosts four `HudChrono` instances side by
side — one per visual state (Charging, Recording, Transcribing,
Rewriting) — with sliders for every tunable so stroke geometry, hue
rotation, fade curves, swipe cadence, etc. can be explored without
relaunching `WhispUI.exe` between edits.

## Build

```powershell
& "D:\bin\visual-studio\visual-studio-2026\MSBuild\Current\Bin\amd64\MSBuild.exe" `
    dev\HudPlayground\HudPlayground.csproj `
    -t:Restore,Build -p:Configuration=Release -p:Platform=x64
```

`dotnet build` is still broken on WinUI 3 (see root `CLAUDE.md`).

## Run

```powershell
dev\HudPlayground\bin\x64\Release\net10.0-windows10.0.19041.0\HudPlayground.exe
```

## Sources

Linked (not copied) from `src/WhispUI/`:

- `Controls/HudChrono.xaml` + `HudChrono.xaml.cs`
- `Controls/HudState.cs`
- `Composition/HudComposition.cs`
- `Assets/Fonts/BitcountSingle.ttf`

Edits to those files in WhispUI propagate on next build.

Local to the playground:

- `App.xaml` / `App.xaml.cs` — minimal bootstrap (XamlControlsResources,
  `new MainWindow().Activate()` in `OnLaunched`).
- `MainWindow.xaml` / `MainWindow.xaml.cs` — 2-column layout: preview
  rail left, tunables right.
- `Stubs/SettingsServiceStub.cs` — minimum-surface `WhispUI.Settings.
  SettingsService` so the linked `HudChrono.xaml.cs` compiles (it reads
  `MaxRecordingDurationSeconds`).
