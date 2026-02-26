# Cursed-Comp-Linux-PhotonMicFix
Works on any Linux distro with PulseAudio or PipeWire (which includes Steam Deck, Ubuntu, Fedora, Arch, and virtually all modern desktop distros). Requires Proton/Wine.

**How to use:**
- Download the zip
- Open the zip
- Drag and drop the contents into the "Cursed Companions" folder in steam.
- Add these launch options to the game in steam: `SteamDeck=1 PROTON_ENABLE_WAYLAND=1 PROTON_USE_NTSYNC=1 WINEDLLOVERRIDES="winhttp=n,b" %command%`.
- Play the game.

***!!NOTICE!!***
I may fine tune this, but I will not be trying to update it further.  This is known to be working as of game version 0.9.18.  It is vibe coded to hell, and I am no coder.  I simply wanted to make a fix that was functional.  If someone who actually knows what they are doing wants to pick this up and create a more elegant implementation/solution.  You have full permission to do so without needing to ask me.  I just wanted to help lay some groundwork for this to make the game work for people on Linux.
