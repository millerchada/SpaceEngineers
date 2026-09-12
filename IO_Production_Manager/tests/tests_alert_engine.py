"""Alert state-engine suite. Compiles and RUNS the shipped code, it does not model it.

    python IO_Production_Manager/tests/tests_alert_engine.py \
           IO_Production_Manager/IO_Production_Manager_vX.Y.Z.cs

The alert rules - hysteresis, the flap-guard cooldown, "recover only what was announced" -
are the kind of thing that is easy to state and easy to get subtly wrong, and none of it is
observable to a compiler. So this suite does NOT re-implement them in Python. It extracts the
two marked regions from the release source VERBATIM:

    // <alert-engine>   ... // </alert-engine>
    // <alert-transport> ... // </alert-transport>

wraps them in a bare class, compiles that with csc and executes the scenarios against the
real methods. If the extraction finds nothing, the suite FAILS rather than passing vacuously -
a test that silently stops testing is the failure mode this project has already shipped once.

That is only possible because the two regions were written with no game API in them. Keep it
that way: an IMyTerminalBlock reaching into either region makes this suite impossible.
"""
import io
import os
import re
import subprocess
import sys
import tempfile

# The alert subsystem arrived in 2.4.40. Running this suite against an older release - the
# release gate is still routinely run against the ACCEPTED version, not only the candidate -
# must report NOT APPLICABLE, never a failure and never a silent pass. The version is read
# from the source's own VERSION constant rather than from the filename, and a source at or
# above FIRST_VERSION with no regions is still a hard FAILURE: that is the case where the
# suite really has stopped testing something it should be testing.
FIRST_VERSION = (2, 4, 40)


def source_version(src):
    m = re.search(r'const string VERSION = "([0-9.]+)"', src)
    if not m:
        return None
    return tuple(int(x) for x in m.group(1).split('.'))

CSC = r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

PROJ = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT = None

HARNESS_HEAD = '''using System;
using System.Collections.Generic;
public class T {
  static readonly StringComparer SCI = StringComparer.OrdinalIgnoreCase;
'''

# The driver. Every expectation is a literal string, so a change to the message format is a
# test failure rather than a silent reformat of what the player sees in chat.
DRIVER = '''
  // ---- test driver -------------------------------------------------------
  const double W = 85, C = 95, H = 2, CD = 300;
  int fails = 0, checks = 0;
  void Eq(string what, string got, string want) {
    checks++;
    bool ok = got == want;
    if (!ok) fails++;
    Console.WriteLine("  " + (what + new string(' ', Math.Max(1, 56 - what.Length))) + (ok ? "PASS" : "FAIL  got [" + got + "] want [" + want + "]"));
  }
  void EqI(string what, int got, int want) { Eq(what, got.ToString(), want.ToString()); }
  string S(string key, double pct, double dt) { return AlertStep(key, "WAREHOUSE", "Ingots", pct, W, C, H, CD, dt, false); }
  string SB(string key, double pct, double dt) { return AlertStep(key, "WAREHOUSE", "Ingots", pct, W, C, H, CD, dt, true); }

  public int Run() {
    Console.WriteLine("-- threshold transitions");
    Eq("84 on a fresh key is silent", S("A", 84, 5), "");
    Eq("84 -> 91 sends one WARNING", S("A", 91, 5), "WAREHOUSE WARNING | Ingots 91%");
    Eq("91 -> 92 says nothing", S("A", 92, 5), "");
    Eq("92 -> 91 says nothing", S("A", 91, 5), "");
    Eq("91 -> 96 escalates to CRITICAL", S("A", 96, 5), "WAREHOUSE CRITICAL | Ingots 96%");
    Eq("critical -> critical is silent", S("A", 97, 5), "");
    Eq("critical -> critical is silent again", S("A", 96, 5), "");

    Console.WriteLine("-- cooldown is a flap guard, never a reminder timer");
    Eq("an hour of Critical produces nothing", S("A", 96, 3600), "");
    Eq("another hour produces nothing", S("A", 99, 3600), "");
    Eq("escalation is NOT suppressed inside the cooldown", S("B", 91, 1), "WAREHOUSE WARNING | Ingots 91%");
    Eq("  ... and Critical still lands 1s later", S("B", 96, 1), "WAREHOUSE CRITICAL | Ingots 96%");
    int supp0 = _alSuppressed;
    Eq("downgrade inside the cooldown is suppressed", S("B", 90, 10), "");
    EqI("  ... and is counted as suppressed", _alSuppressed - supp0, 1);
    Eq("flapping back up is absorbed, not re-announced", S("B", 96, 10), "");
    Eq("downgrade once the cooldown has elapsed", S("B", 90, 301), "WAREHOUSE WARNING | Ingots 90%");

    Console.WriteLine("-- hysteresis");
    Eq("85.0 -> 84.9 does NOT recover", S("C", 86, 5) + S("C", 84.9, 5), "WAREHOUSE WARNING | Ingots 86%");
    Eq("83.1 is still inside the warning band", S("C", 83.1, 400), "");
    Eq("82.9 clears it and recovers", S("C", 82.9, 400), "RECOVERED | Ingots capacity back to 83%");
    Eq("critical entry", S("D", 96, 5), "WAREHOUSE CRITICAL | Ingots 96%");
    Eq("94 is inside the critical hysteresis band", S("D", 94, 400), "");
    Eq("93 is still inside it", S("D", 93, 400), "");
    Eq("92.9 downgrades to Warning", S("D", 92.9, 400), "WAREHOUSE WARNING | Ingots 93%");
    int supp1 = _alSuppressed;
    for (int i = 0; i < 20; i++) { S("E", 84.5, 1); S("E", 85.5, 1); }
    EqI("20 oscillations around 85 emit at most one event", _alerts["E"].Sent, 1);
    EqI("  ... and suppress nothing (never crossed back down)", _alSuppressed - supp1, 0);

    Console.WriteLine("-- recovery only for what was announced");
    Eq("healthy key stays quiet at 84", S("F", 84, 5), "");
    Eq("falling to 10 announces no recovery", S("F", 10, 400), "");
    EqI("  ... and the key was never announced", _alerts["F"].Sent, 0);

    Console.WriteLine("-- startup baseline");
    Eq("baseline at 97 is silent", SB("G", 97, 5), "");
    Eq("still 97 after the baseline is silent", S("G", 97, 5), "");
    Eq("96 after the baseline is silent (same level)", S("G", 96, 5), "");
    EqI("  ... baseline recorded Critical as announced", _alerts["G"].Sent, 2);
    Eq("recovery from a baselined problem still fires", S("G", 50, 5), "RECOVERED | Ingots capacity back to 50%");
    Eq("baseline at 50 is silent", SB("H", 50, 5), "");
    Eq("rising to 91 after a healthy baseline warns", S("H", 91, 5), "WAREHOUSE WARNING | Ingots 91%");

    Console.WriteLine("-- message format");
    Eq("overflow band uses its own prefix",
       AlertStep("WH:OVERFLOW", "OVERFLOW", "Overflow", 97.4, W, C, H, CD, 5, false),
       "OVERFLOW CRITICAL | Overflow 97%");
    Eq("percent is whole, never fractional",
       AlertStep("WH:AMMO", "WAREHOUSE", "Ammo", 91.6666, W, C, H, CD, 5, false),
       "WAREHOUSE WARNING | Ammo 92%");

    Console.WriteLine("-- classification is a pure function of (level, pct, thresholds)");
    EqI("84 from Healthy  -> Healthy",  AlertLevel(0, 84, W, C, H), 0);
    EqI("85 from Healthy  -> Warning",  AlertLevel(0, 85, W, C, H), 1);
    EqI("84.9 from Warning-> Warning",  AlertLevel(1, 84.9, W, C, H), 1);
    EqI("82.9 from Warning-> Healthy",  AlertLevel(1, 82.9, W, C, H), 0);
    EqI("95 from Warning  -> Critical", AlertLevel(1, 95, W, C, H), 2);
    EqI("93 from Critical -> Critical", AlertLevel(2, 93, W, C, H), 2);
    EqI("92.9 from Critical-> Warning", AlertLevel(2, 92.9, W, C, H), 1);
    EqI("0 from Critical  -> Healthy",  AlertLevel(2, 0, W, C, H), 0);

    Console.WriteLine("-- transport failure states");
    Eq("alerts off",              AlertTransport(false, "", 1, true, true, false, 1), "Disabled");
    Eq("bad threshold config",    AlertTransport(true, "bad", 1, true, true, false, 1), "ConfigError");
    Eq("controller missing",      AlertTransport(true, "", 0, false, false, false, 1), "ControllerNotFound");
    Eq("two blocks, same name",   AlertTransport(true, "", 2, true, true, false, 1), "AmbiguousName");
    Eq("  ... even if both work", AlertTransport(true, "", 3, true, true, true, 5), "AmbiguousName");
    Eq("controller not working",  AlertTransport(true, "", 1, false, false, false, 1), "ControllerNotWorking");
    Eq("component unavailable",   AlertTransport(true, "", 1, true, false, false, 1), "ComponentUnavailable");
    Eq("UseAntenna=false is OK",  AlertTransport(true, "", 1, true, true, false, 0), "OK");
    Eq("UseAntenna, no antenna",  AlertTransport(true, "", 1, true, true, true, 0), "DegradedNoAntenna");
    Eq("UseAntenna, antenna up",  AlertTransport(true, "", 1, true, true, true, 1), "OK");
    Eq("sendable: OK",            AlertCanSend("OK") ? "y" : "n", "y");
    Eq("sendable: degraded",      AlertCanSend("DegradedNoAntenna") ? "y" : "n", "y");
    Eq("NOT sendable: missing",   AlertCanSend("ControllerNotFound") ? "y" : "n", "n");
    Eq("NOT sendable: ambiguous", AlertCanSend("AmbiguousName") ? "y" : "n", "n");
    Eq("NOT sendable: no comp",   AlertCanSend("ComponentUnavailable") ? "y" : "n", "n");
    Eq("NOT sendable: not working", AlertCanSend("ControllerNotWorking") ? "y" : "n", "n");
    Eq("NOT sendable: disabled",  AlertCanSend("Disabled") ? "y" : "n", "n");

    Console.WriteLine();
    Console.WriteLine(fails == 0 ? ("ALL " + checks + " CHECKS PASSED") : (fails + " of " + checks + " CHECKS FAILED"));
    return fails == 0 ? 0 : 1;
  }
}
public class R { public static int Main() { return new T().Run(); } }
'''


def extract(src, name):
    m = re.search(r'^\s*//\s*<%s>\s*$(.*?)^\s*//\s*</%s>\s*$' % (name, name),
                  src, re.S | re.M)
    if not m:
        return None
    return m.group(1)


def main():
    target = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
        PROJ, 'IO_Production_Manager_v2.4.40.cs')
    src = io.open(target, encoding='utf-8').read()

    ver = source_version(src)
    if ver is None:
        print('FAIL: no VERSION constant in %s - cannot tell whether alerts apply' % target)
        return 2
    if ver < FIRST_VERSION:
        print('NOT APPLICABLE: %s is v%s; the alert subsystem arrives in v%s.'
              % (os.path.basename(target), '.'.join(map(str, ver)),
                 '.'.join(map(str, FIRST_VERSION))))
        return 0

    regions = []
    for name in ('alert-engine', 'alert-transport'):
        body = extract(src, name)
        # A missing or empty region means the suite is testing NOTHING. Say so and fail.
        if body is None or not body.strip():
            print('FAIL: region <%s> not found in %s - nothing to test.' % (name, target))
            print('      The markers are load-bearing; do not delete them when editing.')
            return 2
        print('extracted <%s>: %d chars, %d lines'
              % (name, len(body), body.count(chr(10))))
        regions.append(body)

    if not os.path.exists(CSC):
        print('csc.exe not found at %s - cannot run this suite' % CSC)
        return 2

    tmp = tempfile.mkdtemp(prefix='alerttest_')
    cs = os.path.join(tmp, 'alerts.cs')
    exe = os.path.join(tmp, 'alerts.exe')
    io.open(cs, 'w', encoding='utf-8', newline='').write(
        HARNESS_HEAD + regions[0] + regions[1] + DRIVER)
    p = subprocess.run([CSC, '-nologo', '-out:' + exe, cs],
                       capture_output=True, text=True)
    if p.returncode != 0:
        print('FAIL: the extracted regions did not compile')
        print(p.stdout + p.stderr)
        return 2
    print()
    r = subprocess.run([exe], capture_output=True, text=True)
    sys.stdout.write(r.stdout + r.stderr)
    return r.returncode


if __name__ == '__main__':
    sys.exit(main())
