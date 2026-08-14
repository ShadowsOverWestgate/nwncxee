SoW client extender -- Linux
============================

Optional add-on for the Neverwinter Nights: Enhanced Edition client. Attach it
for extra features (native chatbox and more). Run the stock client and you play
vanilla -- nothing here is required.

This folder holds:
  sowx-launch      the launcher (loads the extender, then starts the game)
  libsowx.so       native bootstrap
  sowx_managed.so  the C# feature code
  libfunchook.so   function-hook engine
  README.txt       this file

Keep all four files together in one folder. The launcher loads the .so files
that sit next to it.


Install (Steam)
---------------
1. Find the game folder: in Steam, right-click "Neverwinter Nights" ->
   Manage -> Browse local files.
2. Put this "sow-client-extender" folder inside the game's "bin/linux-x86"
   folder. You should end up with:
     .../Neverwinter Nights/bin/linux-x86/sow-client-extender/
3. In Steam: right-click "Neverwinter Nights" -> Properties -> General ->
   Launch Options, and paste this line EXACTLY (do not edit it):

     ./sow-client-extender/sowx-launch %command%

4. Launch the game from Steam as normal.

The line is the same for everyone -- it is a path relative to the bin/linux-x86
folder, which Steam uses as the working folder. To stop using it, clear the
Launch Options line. That is the full uninstall.


Install (no Steam)
------------------
Run the launcher and tell it where the client is:

     NWN_BIN="/path/to/bin/linux-x86/nwmain-linux" ./sowx-launch


Did it work?
------------
In game you should see the extender's native chatbox (bottom-left).

For details, open the log file the extender writes to your home folder:

     ~/sowx.log

Look for "window render installed" and "using font". If the chatbox is
missing, that file says why.
