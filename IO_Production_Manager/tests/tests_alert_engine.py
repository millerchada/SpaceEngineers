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
  // Three ways to drive one tick, because delivery is now part of the contract:
  //   SC = step, then commit  -> the send succeeded
  //   SN = step, no commit    -> the send threw, or transport refused it
  //   SB = silent baseline    -> what startup does
  // A test that only ever used SC could not tell the difference between "announced" and
  // "observed", which is exactly the confusion that produced the defects this suite now pins.
  const double W = 85, C = 95, H = 2, CD = 300;
  int fails = 0, checks = 0;
  void Eq(string what, string got, string want) {
    checks++;
    bool ok = got == want;
    if (!ok) fails++;
    Console.WriteLine("  " + (what + new string(' ', Math.Max(1, 58 - what.Length))) + (ok ? "PASS" : "FAIL  got [" + got + "] want [" + want + "]"));
  }
  void EqI(string what, int got, int want) { Eq(what, got.ToString(), want.ToString()); }
  string Step(string key, double pct, double dt, bool silent) {
    return AlertStep(key, "WAREHOUSE", "Ingots", pct, W, C, H, CD, dt, silent);
  }
  string SC(string key, double pct, double dt) {
    string m = Step(key, pct, dt, false);
    if (m != "") AlertCommit(key, m);
    return m;
  }
  string SN(string key, double pct, double dt) { return Step(key, pct, dt, false); }
  string SB(string key, double pct, double dt) { return Step(key, pct, dt, true); }

  public int Run() {
    Console.WriteLine("-- threshold transitions");
    Eq("84 on a fresh key is silent", SC("A", 84, 5), "");
    Eq("84 -> 91 sends one WARNING", SC("A", 91, 5), "WAREHOUSE WARNING | Ingots 91%");
    Eq("91 -> 92 says nothing", SC("A", 92, 5), "");
    Eq("92 -> 91 says nothing", SC("A", 91, 5), "");
    Eq("91 -> 96 escalates to CRITICAL", SC("A", 96, 5), "WAREHOUSE CRITICAL | Ingots 96%");
    Eq("critical -> critical is silent", SC("A", 97, 5), "");
    Eq("critical -> critical is silent again", SC("A", 96, 5), "");

    Console.WriteLine("-- cooldown is a flap guard, never a reminder timer");
    Eq("an hour of Critical produces nothing", SC("A", 96, 3600), "");
    Eq("another hour produces nothing", SC("A", 99, 3600), "");
    Eq("escalation is NOT suppressed inside the cooldown", SC("B", 91, 1), "WAREHOUSE WARNING | Ingots 91%");
    Eq("  ... and Critical still lands 1s later", SC("B", 96, 1), "WAREHOUSE CRITICAL | Ingots 96%");
    int supp0 = _alSuppressed;
    Eq("downgrade inside the cooldown is suppressed", SC("B", 90, 10), "");
    EqI("  ... and is counted as suppressed", _alSuppressed - supp0, 1);
    Eq("flapping back up is absorbed, not re-announced", SC("B", 96, 10), "");
    Eq("downgrade once the cooldown has elapsed", SC("B", 90, 301), "WAREHOUSE WARNING | Ingots 90%");

    Console.WriteLine("-- hysteresis");
    Eq("85.0 -> 84.9 does NOT recover", SC("C", 86, 5) + SC("C", 84.9, 5), "WAREHOUSE WARNING | Ingots 86%");
    Eq("83.1 is still inside the warning band", SC("C", 83.1, 400), "");
    Eq("82.9 clears it and recovers", SC("C", 82.9, 400), "RECOVERED | Ingots capacity back to 83%");
    Eq("critical entry", SC("D", 96, 5), "WAREHOUSE CRITICAL | Ingots 96%");
    Eq("94 is inside the critical hysteresis band", SC("D", 94, 400), "");
    Eq("93 is still inside it", SC("D", 93, 400), "");
    Eq("92.9 downgrades to Warning", SC("D", 92.9, 400), "WAREHOUSE WARNING | Ingots 93%");
    int supp1 = _alSuppressed;
    for (int i = 0; i < 20; i++) { SC("E", 84.5, 1); SC("E", 85.5, 1); }
    EqI("20 oscillations around 85 acknowledge one level", _alerts["E"].Ack, 1);
    EqI("  ... and suppress nothing (never crossed back down)", _alSuppressed - supp1, 0);

    Console.WriteLine("-- recovery only for what was ANNOUNCED");
    Eq("healthy key stays quiet at 84", SC("F", 84, 5), "");
    Eq("falling to 10 announces no recovery", SC("F", 10, 400), "");
    Eq("  ... and nothing was ever announced", _alerts["F"].Ann ? "y" : "n", "n");

    Console.WriteLine("-- startup baseline is OBSERVED, never ANNOUNCED");
    Eq("start Healthy, stay Healthy: silent", SB("B1", 50, 5) + SC("B1", 50, 5), "");
    Eq("start Warning, stay Warning: silent", SB("B2", 91, 5) + SC("B2", 91, 5), "");
    Eq("baseline records the observed level", ALN[_alerts["B2"].Ack], "Warning");
    Eq("  ... but does NOT mark it announced", _alerts["B2"].Ann ? "y" : "n", "n");
    Eq("start Warning -> Healthy: NO recovery", SB("B3", 91, 5) + SC("B3", 50, 5), "");
    Eq("start Critical -> Healthy: NO recovery", SB("B4", 97, 5) + SC("B4", 50, 5), "");
    Eq("start Warning -> Critical: escalation IS sent",
       SB("B5", 91, 5) + SC("B5", 96, 5), "WAREHOUSE CRITICAL | Ingots 96%");
    Eq("start Critical -> Warning: silent, nothing was announced",
       SB("B6", 97, 5) + SC("B6", 92, 5), "");
    Eq("  ... and on down to Healthy, still silent", SC("B6", 50, 400), "");
    Eq("a real Warning after a Healthy baseline is sent",
       SB("B7", 50, 5) + SC("B7", 91, 5), "WAREHOUSE WARNING | Ingots 91%");
    Eq("  ... and ITS recovery is announced normally",
       SC("B7", 50, 400), "RECOVERED | Ingots capacity back to 50%");

    Console.WriteLine("-- a transition is consumed only by a SUCCESSFUL send");
    int ev0 = _alEvents;
    string last0 = _alLastMsg;
    Eq("the transition is offered", SN("R", 91, 5), "WAREHOUSE WARNING | Ingots 91%");
    EqI("  ... a failed send counts no alert", _alEvents - ev0, 0);
    Eq("  ... and does not become LastMessage", _alLastMsg, last0);
    Eq("  ... and nothing is marked announced", _alerts["R"].Ann ? "y" : "n", "n");
    Eq("the SAME transition is re-offered next cycle", SN("R", 91, 5), "WAREHOUSE WARNING | Ingots 91%");
    Eq("and again, indefinitely", SN("R", 92, 5), "WAREHOUSE WARNING | Ingots 92%");
    Eq("it commits when the send finally succeeds", SC("R", 91, 5), "WAREHOUSE WARNING | Ingots 91%");
    EqI("  ... now it counts", _alEvents - ev0, 1);
    Eq("  ... and becomes LastMessage", _alLastMsg, "WAREHOUSE WARNING | Ingots 91%");
    Eq("  ... and stops repeating", SC("R", 91, 5), "");
    Eq("an escalation past a stuck transition wins",
       SN("R2", 91, 5) + "|" + SC("R2", 96, 5),
       "WAREHOUSE WARNING | Ingots 91%|WAREHOUSE CRITICAL | Ingots 96%");
    Eq("  ... and the stuck Warning is not replayed afterwards", SC("R2", 96, 5), "");
    Eq("a failed recovery is retried, not lost",
       SC("R3", 91, 5) + "|" + SN("R3", 50, 400) + "|" + SC("R3", 50, 5),
       "WAREHOUSE WARNING | Ingots 91%|RECOVERED | Ingots capacity back to 50%|RECOVERED | Ingots capacity back to 50%");

    Console.WriteLine("-- what an evaluation is allowed to do");
    EqI("first run, AlertOnStartup=false, transport up   -> baseline", AlertPass(false, false, true), 1);
    EqI("first run, AlertOnStartup=false, transport DOWN -> baseline", AlertPass(false, false, false), 1);
    EqI("first run, AlertOnStartup=true,  transport up   -> evaluate", AlertPass(false, true, true), 2);
    EqI("first run, AlertOnStartup=true,  transport DOWN -> skip", AlertPass(false, true, false), 0);
    EqI("running, transport up   -> evaluate", AlertPass(true, false, true), 2);
    EqI("running, transport DOWN -> skip", AlertPass(true, false, false), 0);

    Console.WriteLine("-- transport down at startup does not swallow a condition");
    // Enabled while the controller is offline and Ingots is ALREADY Critical. The baseline
    // still runs - it records what was true when alerting started - but announces nothing.
    Eq("baseline runs even with transport down", SB("T1", 97, 5), "");
    Eq("  ... and the pre-existing condition stays silent on restore", SC("T1", 97, 5), "");
    // Enabled while the controller is offline and Ingots is Healthy. The pool then goes
    // Critical WHILE TRANSPORT IS DOWN, so no evaluation runs at all (AlertPass == 0).
    Eq("healthy baseline with transport down", SB("T2", 50, 5), "");
    EqI("  ... evaluations are skipped while down", AlertPass(true, false, false), 0);
    Eq("  ... and the condition alerts once transport returns",
       SC("T2", 97, 5), "WAREHOUSE CRITICAL | Ingots 97%");

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
    Eq("alerts off",              AlertTransport(false, "", 1, true, true), "Disabled");
    Eq("bad threshold config",    AlertTransport(true, "bad", 1, true, true), "ConfigError");
    Eq("controller missing",      AlertTransport(true, "", 0, false, false), "ControllerNotFound");
    Eq("two blocks, same name",   AlertTransport(true, "", 2, true, true), "AmbiguousName");
    Eq("  ... even if both work", AlertTransport(true, "", 3, true, true), "AmbiguousName");
    Eq("controller not working",  AlertTransport(true, "", 1, false, false), "ControllerNotWorking");
    Eq("component unavailable",   AlertTransport(true, "", 1, true, false), "ComponentUnavailable");
    Eq("everything present",      AlertTransport(true, "", 1, true, true), "OK");
    Eq("sendable: OK",            AlertCanSend("OK") ? "y" : "n", "y");
    Eq("NOT sendable: missing",   AlertCanSend("ControllerNotFound") ? "y" : "n", "n");
    Eq("NOT sendable: ambiguous", AlertCanSend("AmbiguousName") ? "y" : "n", "n");
    Eq("NOT sendable: no comp",   AlertCanSend("ComponentUnavailable") ? "y" : "n", "n");
    Eq("NOT sendable: not working", AlertCanSend("ControllerNotWorking") ? "y" : "n", "n");
    Eq("NOT sendable: disabled",  AlertCanSend("Disabled") ? "y" : "n", "n");
    // Antenna state is advisory and must never become a transport verdict again. Pinned here
    // so reintroducing the string cannot quietly make it sendable.
    Eq("antenna state is NOT a transport verdict", AlertCanSend("DegradedNoAntenna") ? "y" : "n", "n");

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
