# AtelierReslerianaRWFix

Inspired by [Lyall's AtelierYumiaFix](https://codeberg.org/Lyall/AtelierYumiaFix), this plugin hooks Unity's native IL2CPP camera pipeline in memory to let you customize the camera Field of View (FOV) during field exploration without breaking other game modes.

## Features

- **Field / Exploration FOV Multiplier:** Scales the 3D exploration camera (`Main Camera` in `FieldScene`) by a configurable multiplier (`0.50` to `2.50`).
- **Strict Mode Isolation:** Automatically ignores and protects **Battles** (`Battle Main Camera`), **Cutscenes / Events** (`EventCamera`), **Synthesis**, **2D Orthographic Cameras**, and **UI / Menu RenderTextures**.
- **Anti-Snowball Base FOV Caching:** Caches the unscaled base FOV so sprinting camera zooms and smooth camera transitions behave naturally without compounding across frames.
- **Live `.ini` Hot-Reloading:** Adjust `GameplayMultiplier` in `AtelierReslerianaRWFix.ini` and save while the game is running to see changes immediately.

---

## Installation

### Windows
1. Download the latest release `.zip` from the **Releases** page.
2. Extract the contents of the `.zip` directly into your game folder (where `AtelierReslerianaRW.exe` and `GameAssembly.dll` are located):
   ```text
   steamapps\common\Atelier Resleriana The Red Alchemist and the White Guardian\
   ```
3. Launch the game. Modify **`AtelierReslerianaRWFix.ini`** in the game folder to adjust your preferred FOV multiplier.

### Linux / Steam Deck (Proton)
1. Follow the Windows installation steps above.
2. In Steam, open the game's **Properties -> General -> Launch Options** and add:
   ```text
   WINEDLLOVERRIDES="winhttp=n,b" %command%
   ```

---

## Configuration

Open **`AtelierReslerianaRWFix.ini`** in the game directory with any text editor (if deleted, the plugin will automatically generate a fresh copy on launch):

```ini
[FOV]
; Enable the Field/Exploration FOV multiplier (true / false)
Enabled = true

; Field/Exploration FOV multiplier
; Default = 1.00 | Minimum = 0.50 | Maximum = 2.50
; Note: Any value set above 2.50 is automatically capped at 2.50 to prevent camera projection distortion.
GameplayMultiplier = 1.20

[Filter]
; Comma-separated keywords. Matches the Field camera ('Main Camera' in FieldScene).
IncludeKeywords = Main Camera,FieldScene,Field,Explore

; Comma-separated keywords. Excludes Battle ('Battle Main Camera'), Cutscenes ('EventCamera'), and Menus.
ExcludeKeywords = Battle,Btl,Combat,Cutscene,Event,Evt,Timeline,Demo,Movie,Synthesis,Mix,Alchemy,Shop,Menu,UI,CharaView,Portrait,Photo,Title,Result

; Ignore narrow cutscene zoom lenses below MinBaseFOV or wide lenses above MaxBaseFOV
MinBaseFOV = 35.0
MaxBaseFOV = 85.0

[Debug]
; Log detected 3D camera names to AtelierReslerianaRWFix.log (set to true only when debugging cameras)
LogCameraNames = false
```

> **Tip:** You can edit `GameplayMultiplier` and save `AtelierReslerianaRWFix.ini` while the game is open—the new FOV will apply within 1 second.

---

## Uninstallation

Delete the following files and folder from your game directory:
- `dotnet\` (folder)
- `winhttp.dll`
- `dobby.dll`
- `doorstop_config.ini`
- `.doorstop_version`
- `AtelierReslerianaRWFix.dll`
- `AtelierReslerianaRWFix.ini`
- `AtelierReslerianaRWFix.log`

---

## Building from Source

1. Install the **.NET 6.0 SDK** (or newer).
2. Clone this repository and run:
   ```bash
   dotnet build -c Release
   ```
3. Copy `bin/Release/net6.0/AtelierReslerianaRWFix.dll` into your game folder alongside [UnityDoorstop 4](https://github.com/NeighTools/UnityDoorstop) (`winhttp.dll`, `doorstop_config.ini`, and the `.NET 6` CoreCLR runtime folder from [BepInEx 6 IL2CPP](https://builds.bepinex.dev/projects/bepinex_be)) and `dobby.dll`.

---

## Credits & Disclaimer

- **Inspiration:** [Lyall](https://codeberg.org/Lyall) for the `AtelierYumiaFix` configuration design.
- **Runtime & Hooking:** [UnityDoorstop](https://github.com/NeighTools/UnityDoorstop), [BepInEx](https://github.com/BepInEx/BepInEx), and [Dobby](https://github.com/jmpews/Dobby).
- **AI Assistance Disclaimer:** The C# IL2CPP hooking code and documentation for this plugin were developed with the assistance of AI, and manually tested, calibrated, and verified in-game.
