SoW client extender -- Linux
============================

Optional add-on for the Neverwinter Nights: Enhanced Edition client. Attach it
for extra features (native chatbox and more). Run the stock client and you play
vanilla -- nothing here is required.


Install
-------
1. Find the game folder: in Steam, right-click "Neverwinter Nights" ->
   Manage -> Browse local files, then open "bin/linux-x86".
2. Unpack this archive straight into that folder, beside "nwmain-linux".
3. In Steam: right-click "Neverwinter Nights" -> Properties -> General ->
   Launch Options, and paste this line EXACTLY (do not edit it):

     ./sowx-launch %command%

4. Launch the game from Steam as normal.

The line is the same for everyone -- it is a path relative to the bin/linux-x86
folder, which Steam uses as the working folder.

Not on Steam? Unpack the same way and run ./sowx-launch -- with no arguments it
starts the nwmain-linux sitting next to it.


Uninstall
---------
Clear the Launch Options line. That alone is enough -- the files are inert
without it. To remove them too, delete sowx-launch, libsowx.so,
sowx_managed.so, libfunchook.so and README.txt from bin/linux-x86.


Did it work?
------------
In game you should see the extender's native chatbox (bottom-left).


Reporting a problem
-------------------
The extender is silent by default. To get a log, create a file named sowx.cfg
NEXT TO nwn.ini -- usually ~/.local/share/Neverwinter Nights/ -- holding one
line:

     logs=1

Play again, then send back ~/sowx.log along with what you did right before
anything went wrong.
