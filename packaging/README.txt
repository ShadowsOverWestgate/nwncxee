NWNCX:EE (CX) -- client extender for Linux -- @VERSION@

Optional add-on for the Neverwinter Nights: Enhanced Edition client. Run the
stock client and you play vanilla -- nothing here is required.


INSTALL
  1. Find the game folder.
       Steam: right-click "Neverwinter Nights" -> Manage -> Browse local files.
       GOG:   wherever you unpacked the game.
     Open "bin/linux-x86" inside it. It holds nwmain-linux.
  2. Unpack this archive into that folder, beside nwmain-linux.
  3. Steam: right-click "Neverwinter Nights" -> Properties -> General ->
     Launch Options, and paste this line exactly as written:

       ./cx-launch %command%

     Not on Steam: run ./cx-launch from bin/linux-x86. With no arguments it
     starts the nwmain-linux beside it.
  4. Start the game.

  The launch line is the same for everyone. It is a path relative to
  bin/linux-x86, which Steam uses as the working folder.

UNINSTALL
  Clear the Launch Options line. That alone is enough: the files do nothing
  without it. To remove them too, delete cx-launch, libcx.so,
  cx_managed.so, README.txt and LICENSES.txt from bin/linux-x86.

WHAT WORKING LOOKS LIKE
  The extender's own chatbox, bottom-left, instead of the stock one.

REPORTING A PROBLEM
  The extender is silent by default. To get a log, create a file named cx.cfg
  next to nwn.ini -- usually ~/.local/share/Neverwinter Nights/ -- holding one
  line:

     logs=1

  Play again, then send back ~/cx.log along with what you did right before
  anything went wrong.
