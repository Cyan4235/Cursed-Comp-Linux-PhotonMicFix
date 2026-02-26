# PhotonMicFix - Voice Chat Fix for Cursed Companions on Linux

## The Problem

Cursed Companions uses two audio plugins that both access the microphone through
Unity's `Microphone` API:

- **Recognissimo** - for speech recognition (spell casting, curse word detection)
- **Photon Voice** - for VOIP (proximity chat with teammates)

Under Proton/Wine, both plugins share a single PulseAudio capture stream. 
Recognissimo reads the audio buffer first and clears it, leaving Photon Voice 
with an empty buffer. Result: spell casting works, but your voice is never 
transmitted to other players.

## The Fix

This BepInEx plugin forces Photon Voice to use its **native microphone capture** 
(`MicType.Photon`) instead of Unity's `Microphone` API (`MicType.Unity`). This 
creates a second, independent audio capture stream that Recognissimo can't 
interfere with.

## Prerequisites

1. **BepInEx 6 IL2CPP** installed in the game folder  
   Download: `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.753+0d275a4.zip`  
   From: https://builds.bepinex.dev/projects/bepinex_be

2. **Game launched once** with BepInEx to generate interop assemblies

3. **Steam launch options** must include:  
   ```
   SteamDeck=1 PROTON_ENABLE_WAYLAND=1 PROTON_USE_NTSYNC=1 WINEDLLOVERRIDES="winhttp=n,b" %command%
   ```

4. **.NET 6 SDK** for building  
   ```
   sudo apt install dotnet-sdk-6.0      # Ubuntu/Debian
   sudo dnf install dotnet-sdk-6.0      # Fedora  
   sudo pacman -S dotnet-sdk-6.0        # Arch
   ```

## Building and Installing

```bash
chmod +x build_and_install.sh
./build_and_install.sh
```

Or manually:

```bash
dotnet build -c Release
cp bin/PhotonMicFix.dll ~/.local/share/Steam/steamapps/common/Cursed\ Companions/BepInEx/plugins/
```

## Verifying It Works

1. Launch the game and join a multiplayer session
2. Check the BepInEx log:
   ```bash
   tail -f ~/.local/share/Steam/steamapps/common/Cursed\ Companions/BepInEx/LogOutput.log
   ```
   Look for: `Switching Recorder MicrophoneType from Unity to Photon (native)`

3. Check for a second audio capture stream:
   ```bash
   pactl list source-outputs | grep -B2 -A10 "cursedcompanions"
   ```
   If the fix works, you should see **two** source outputs from the game.

## Uninstalling

```bash
rm ~/.local/share/Steam/steamapps/common/Cursed\ Companions/BepInEx/plugins/PhotonMicFix.dll
```

To remove BepInEx entirely, delete `winhttp.dll`, `doorstop_config.ini`, and 
the `BepInEx/` folder from the game directory, and remove 
`WINEDLLOVERRIDES="winhttp=n,b"` from your launch options.

## Troubleshooting

**Plugin loads but Photon native mic fails under Wine:**  
The Photon native audio plugin uses Windows audio APIs which may not work 
properly under Wine. Check the BepInEx log for errors. If this happens, the 
problem requires a different approach (e.g., a Wine/PipeWire level fix).

**No Recorders found:**  
The Recorder component is only created when you join a multiplayer session, not 
in the main menu. Make sure you're in a game lobby or match.

**Game crashes on startup:**  
Try a different BepInEx version, or remove the plugin DLL from the plugins folder.
