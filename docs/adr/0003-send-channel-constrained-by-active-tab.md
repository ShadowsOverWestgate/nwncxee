# Send channel is constrained to the active tab's visible channels

The send-channel dropdown offers only the sendable channels the **active tab** actually
shows — you cannot send on a channel the tab hides (no shouting from a tab that doesn't
display Shout). This supersedes #25, which made the send channel a free per-window
dropdown independent of the tabs. We chose the coupling so that focusing a
purpose-built tab (a party tab, a per-player Tell tab) implies where your text goes,
which is the whole point of per-tab channel setups. Consequence: switching tabs can
change the available send channels and may reset an now-invalid selection; a tab that
shows no sendable channel shows no input line.
