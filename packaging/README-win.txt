NWNCX:EE (CX) -- client extender for Windows -- @VERSION@, pinned to nwmain.exe build @STAMP@

Optional add-on for the Neverwinter Nights: Enhanced Edition client. Run the stock client and you
play vanilla -- nothing here is required.

INSTALL
  1. Unpack this archive into the folder holding nwmain.exe:
       Steam: <Steam>\steamapps\common\Neverwinter Nights\bin\win32\
       GOG:   <GOG Games>\Neverwinter Nights Enhanced Edition\bin\win32\
  2. Start the game as usual (Steam, GOG Galaxy or nwmain.exe). No launch options needed.

  That is the whole install: one file, version.dll. The game loads it, and it carries everything
  else inside it.

UNINSTALL
  Delete version.dll from bin\win32\. README.txt and LICENSES.txt can go too.

WHAT WORKING LOOKS LIKE
  You'll see an attribution text in top left corner in the main menu.

REPORTING A PROBLEM
  The extender is silent by default. To get a log, create a file named cx.cfg next to nwn.ini -
  usually Documents\Neverwinter Nights\. Add these two lines:

     logs=1
     hookdebug=1

  Play again, then send back Documents\Neverwinter Nights\cx.log plus what you did right before 
  anything went wrong.

  Defender may quarantine version.dll - it's a false alarm, add to Defender's exceptions.
