NWNCX:EE (CX) -- client extender for Windows -- @VERSION@, for client build(s) @BUILDS@

Optional add-on for the Neverwinter Nights: Enhanced Edition client. Run the stock client and you
play vanilla -- nothing here is required.

INSTALL
  1. Unpack this archive into the folder holding nwmain.exe:
       Steam: <Steam>\steamapps\common\Neverwinter Nights\bin\win32\
       GOG:   <GOG Games>\Neverwinter Nights Enhanced Edition\bin\win32\
  2. Start the game as usual (Steam, GOG Galaxy or nwmain.exe). No launch options needed.

  That is the whole install: version.dll, which the game loads, and beside it the audio runtime
  it uses -- cx_openal.dll (OpenAL Soft) and phonon.dll (Steam Audio). The game's own files are
  not touched.

UNINSTALL
  Delete version.dll, cx_openal.dll and phonon.dll from bin\win32\. README.txt and LICENSES.txt
  can go too.

WHAT WORKING LOOKS LIKE
  You'll see an attribution text in top left corner in the main menu.

REPORTING A PROBLEM
  The extender is silent by default. To get a log, create a file named cx.cfg next to nwn.ini -
  usually Documents\Neverwinter Nights\. Add these two lines:

     general.logs=1
     general.hookdebug=1

  Play again, then send back Documents\Neverwinter Nights\cx.log plus what you did right before 
  anything went wrong.

  Defender may quarantine version.dll - it's a false alarm, add to Defender's exceptions.
