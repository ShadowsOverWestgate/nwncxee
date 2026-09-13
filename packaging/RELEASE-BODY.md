SoW client extender @VERSION@. Optional add-on for Neverwinter Nights: Enhanced Edition. Without it you play vanilla.

Pick your platform, unpack the archive into the game's bin folder, done. Each archive holds a README.txt with the full install, uninstall and troubleshooting text.

**Windows** (`sowx-windows-x64-@VERSION@.zip`): unpack into `bin\win32\` beside nwmain.exe and start the game. Pinned to nwmain.exe build @STAMP@; after a Beamdog patch the extender stands down with a message box until a new release matches.

**Linux** (`sowx-linux-x64-@VERSION@.zip`): unpack into `bin/linux-x86/` beside nwmain-linux, then set the Steam launch option to exactly `./sowx-launch %command%`. Not on Steam: run `./sowx-launch`.

**Uninstall**: Windows, delete version.dll. Linux, clear the launch option.

It worked if you see an attribution text in top left corner 
in the main menu.
