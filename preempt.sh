#!/bin/sh
# Who takes the core from the game's main thread, from the kernel's own scheduler trace (ftrace, no perf needed).
#   sudo ./preempt.sh [seconds]      -- default 5; run while the game is in-game and stationary
# Prints, per preempter: how many times it took the core off nwmain while nwmain was RUNNABLE (prev_state=R,
# i.e. an involuntary switch), and the total ms nwmain waited before getting a CPU back. Also the CPU
# frequency transitions seen, so a clock drop shows beside a preemption.
set -e
T=/sys/kernel/tracing
PID=$(pidof nwmain-linux | cut -d' ' -f1) || { echo "nwmain-linux not running" >&2; exit 1; }
[ -w "$T/tracing_on" ] || { echo "need root: sudo $0" >&2; exit 1; }
echo 0 > $T/tracing_on
echo nop > $T/current_tracer
echo 16384 > $T/buffer_size_kb   # per CPU; the default 1.4MB overflowed in 8s and paired switches across the gap
echo > $T/trace
echo 1 > $T/events/sched/sched_switch/enable
echo 1 > $T/events/power/cpu_frequency/enable
echo 1 > $T/tracing_on
sleep "${1:-5}"
echo 0 > $T/tracing_on
echo 0 > $T/events/sched/sched_switch/enable
echo 0 > $T/events/power/cpu_frequency/enable
awk -v pid="$PID" '
  /sched_switch/ {
    match($0, /[0-9]+\.[0-9]+: sched_switch/); ts = substr($0, RSTART, RLENGTH - 14);
    match($0, /prev_pid=[0-9]+/); pp = substr($0, RSTART+9, RLENGTH-9);
    match($0, /prev_state=[^ ]+/); ps = substr($0, RSTART+11, RLENGTH-11);
    match($0, /next_comm=.* next_pid=/); nc = substr($0, RSTART+10, RLENGTH-20);
    match($0, /next_pid=[0-9]+/); np = substr($0, RSTART+9, RLENGTH-9);
    match($0, /next_prio=[0-9]+/); npr = substr($0, RSTART+10, RLENGTH-10);
    if (pp == pid && ps ~ /^R/) { t0 = ts; who = nc " (prio " npr ")"; n[who]++ }
    else if (np == pid && t0 != "") { w[who] += (ts - t0) * 1000; t0 = "" }
    if (pp == pid) total++
  }
  /cpu_frequency/ { match($0, /state=[0-9]+/); f = substr($0, RSTART+6, RLENGTH-6); match($0, /cpu_id=[0-9]+/); c = substr($0, RSTART+7, RLENGTH-7);
    if (!(c in lo) || f < lo[c]) lo[c] = f; if (f > hi[c]) hi[c] = f; nf[c]++ }
  END {
    printf "nwmain main thread %s: %d switches out, of which preempted while runnable:\n", pid, total;
    for (k in n) printf "  %6d x %8.1f ms  %s\n", n[k], w[k], k | "sort -k3 -n -r";
    close("sort -k3 -n -r");
    for (c in nf) printf "cpu%s: %d frequency changes, %.2f-%.2f GHz\n", c, nf[c], lo[c]/1e6, hi[c]/1e6 | "sort -V";
  }' $T/trace
