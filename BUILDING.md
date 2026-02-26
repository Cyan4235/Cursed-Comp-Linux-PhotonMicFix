# Building PhotonMicFix from Source

## Prerequisites

- .NET 6.0 SDK (`sudo apt install dotnet-sdk-6.0` or equivalent)
- Cursed Companions installed via Steam
- BepInEx 6 IL2CPP installed in the game folder (included in the release zip)
- The game must have been launched at least once with BepInEx so the `interop/` DLLs are generated

## Project Structure

- `PhotonMicFix.cs` — All plugin source code (single file)
- `PhotonMicFix.csproj` — Project file referencing DLLs from BepInEx
- `build_and_install.sh` — Build script that compiles and copies the DLL to the plugins folder

## How It Works

The `.csproj` references DLLs directly from your game's BepInEx installation:

- `BepInEx/core/` — BepInEx framework and IL2CPP interop runtime
- `BepInEx/unity-libs/` — Unity engine assemblies
- `BepInEx/interop/` — IL2CPP generated interop assemblies (Photon Voice, etc.)

The default game path is `~/.local/share/Steam/steamapps/common/Cursed Companions`. If yours differs, edit the `GameDir` property in the `.csproj`.

## Building

```bash
# Option 1: Use the build script
chmod +x build_and_install.sh
./build_and_install.sh

# Option 2: Manual
dotnet build -c Release
cp bin/Release/net6.0/PhotonMicFix.dll \
  ~/.local/share/Steam/steamapps/common/Cursed\ Companions/BepInEx/plugins/
```

## Key Architecture Notes

- **Capture**: `parec` (PulseAudio) writes raw float32 PCM to `/dev/shm` ring buffer
- **Bridge**: Background thread reads raw file → writes to shared memory ring buffer
- **Injection**: MonoBehaviour reads ring buffer → writes to Unity AudioClip
- **Pipeline**: AudioClip → Photon AudioClipWrapper → Opus encoder → network
- **SamplingRate fix**: Game defaults to 24kHz under Wine; plugin forces 48kHz
- **FrameDuration fix**: Opus encoder crashes with delay=0 under Wine; plugin sets 20ms
- Config file: `BepInEx/config/com.linuxfix.photonmicfix.cfg`
