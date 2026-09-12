"""Planner yield math: a non-unity recipe yield must divide demand, not multiply ingredients.

    python IO_Production_Manager/tests/tests_yield_math.py

WHY THIS EXISTS. v2.4.37 shipped Gunpowder with output yield 1 because the live UI does not
display a yield for it. DEFAULTING TO 1 IS A GUESS, not a neutral choice, and it is a guess
that fails in the expensive direction: a demand for 10 asks for ten jobs, reserves ten times
the ingredients, and produces ten times the goods. One manual blueprint run in the Munitions
Factory produced 10 Gunpowder and proved the yield.

This models EnsureFeasible's job arithmetic line for line so the claim "10 Gunpowder needs ONE
job and 6/2/2" is asserted, not asserted-by-assertion. It also pins the queue-credit path,
because ScanQueues multiplies queued jobs by the same figure - a wrong yield mis-credits work
queued by hand as well as work the planner queues itself.
"""
import io
import math
import os
import re
import sys

FAIL = []


def check(label, got, want):
    ok = got == want
    print('  %-58s %s' % (label, 'PASS' if ok else '*** FAIL ***'))
    if not ok:
        print('       got  %r' % (got,))
        print('       want %r' % (want,))
        FAIL.append(label)


def plan(shortage, recipe_output, inputs):
    """Port of EnsureFeasible's job arithmetic:

        double recipeOutput = recipe.Output > 0 ? recipe.Output : 1.0;
        int jobsWanted = (int)Math.Ceiling(shortage / recipeOutput);
        ... EnsureFeasible(input, recipe.Inputs[i].Amount * jobsWanted, ...)
        AddTo(_plannedOutput, alias, feasibleJobs * recipeOutput);

    Returns (jobsWanted, {ingredient: reserved}, plannedOutput) assuming everything is
    feasible - this is about the arithmetic, not about scarcity.
    """
    out = recipe_output if recipe_output > 0 else 1.0
    jobs = int(math.ceil(shortage / out))
    reserved = {k: v * jobs for k, v in inputs.items()}
    return jobs, reserved, jobs * out


def queued_output(queued_jobs, recipe_output):
    """Port of ScanQueues: yield = r.Output, credited as jobs * yield."""
    return queued_jobs * (recipe_output if recipe_output > 0 else 1.0)


GUNPOWDER_IN = {'PotassiumNitrate': 6, 'Carbon': 2, 'Sulfur': 2}

print('=' * 72)
print('GUNPOWDER - live-proven yield 10')
jobs, res, made = plan(10, 10, GUNPOWDER_IN)
check('demand 10 -> exactly ONE blueprint job', jobs, 1)
check('demand 10 -> ingredients 6/2/2, not 60/20/20', res,
      {'PotassiumNitrate': 6, 'Carbon': 2, 'Sulfur': 2})
check('demand 10 -> planned output 10', made, 10)

print()
print('REGRESSION CONTROL - the same demand under the WRONG yield 1')
jobs1, res1, made1 = plan(10, 1, GUNPOWDER_IN)
check('wrong yield asks for TEN jobs', jobs1, 10)
check('wrong yield reserves 60/20/20', res1,
      {'PotassiumNitrate': 60, 'Carbon': 20, 'Sulfur': 20})
# TWO LAYERS, and conflating them is how this test first got written wrong.
# The PLANNER would believe it is making 10 (ten jobs x its assumed yield of 1).
# The GAME would actually make 100 (ten jobs x the real yield of 10).
# So the damage is not what IOPM predicts - it is the gap between prediction and reality:
# ten times the ingredients consumed and ten times the goods produced, with the planner's
# own arithmetic looking perfectly consistent throughout.
check('wrong yield: planner BELIEVES it produces 10', made1, 10)
check('wrong yield: game ACTUALLY produces 100 (jobs x true yield)', jobs1 * 10, 100)
check('...a 10x overproduction the planner cannot see', (jobs1 * 10) / made1, 10)
check('the two differ - this test can actually detect the bug', jobs == jobs1, False)

print()
print('PARTIAL AND ROUNDING BEHAVIOUR')
check('demand 1 still costs one whole job (cannot make a tenth)', plan(1, 10, GUNPOWDER_IN)[0], 1)
check('demand 1 still reserves a full 6/2/2', plan(1, 10, GUNPOWDER_IN)[1],
      {'PotassiumNitrate': 6, 'Carbon': 2, 'Sulfur': 2})
check('demand 1 overproduces to 10 - correct, and why targets settle above demand',
      plan(1, 10, GUNPOWDER_IN)[2], 10)
check('demand 11 rounds UP to two jobs', plan(11, 10, GUNPOWDER_IN)[0], 2)
check('demand 20 is exactly two jobs', plan(20, 10, GUNPOWDER_IN)[0], 2)
check('demand 0.5 still costs one job', plan(0.5, 10, GUNPOWDER_IN)[0], 1)

print()
print('QUEUE CREDIT - ScanQueues must use the same yield')
check('1 queued job credits 10 Gunpowder', queued_output(1, 10), 10)
check('under the wrong yield it would credit only 1', queued_output(1, 1), 1)
check('3 queued jobs credit 30', queued_output(3, 10), 30)

print()
print('LIGHTBULB - the pre-existing non-unity yield must not regress')
LB = {'Glass': 1, 'CopperWire': 10}
check('demand 30 Lightbulb -> 3 jobs', plan(30, 10, LB)[0], 3)
check('demand 30 Lightbulb -> Glass 3 (UAT-measured)', plan(30, 10, LB)[1]['Glass'], 3)
check('demand 30 Lightbulb -> CopperWire 30', plan(30, 10, LB)[1]['CopperWire'], 30)

print()
print('UNITY YIELDS ARE UNAFFECTED')
check('SolarCell demand 47 at yield 1 -> 47 jobs', plan(47, 1, {'IronIngot': 3})[0], 47)
check('SolarCell demand 47 -> IronIngot 141 (matches live UAT)',
      plan(47, 1, {'IronIngot': 3})[1]['IronIngot'], 141)

print()
print('MANUAL-QUEUE ATTRIBUTION - BlueprintReverseMap + ScanQueues')


def reverse_map(items, blueprints):
    """Port of BlueprintReverseMap: iterates _items (ALL ItemDefs, not just recipes) and
    keys blueprint-id -> alias. A loadout alias is NOT in _items and so can never attribute."""
    m = {}
    for alias in items:
        bp = blueprints.get(alias)
        if not bp:
            continue                      # TryGetBlueprint failed - skipped
        key = 'MyObjectBuilder_BlueprintDefinition/' + bp
        if key not in m:
            m[key] = alias      # first declaration wins, as in the C#
    return m


ITEMS = ['Gunpowder', 'SteelPlate', 'Lightbulb']
BPS = {'Gunpowder': 'Gunpowder', 'SteelPlate': 'POSteelPlate', 'Lightbulb': 'Lightbulb'}
rm = reverse_map(ITEMS, BPS)
GP_BP = 'MyObjectBuilder_BlueprintDefinition/Gunpowder'
check('Gunpowder blueprint id resolves back to the alias', rm.get(GP_BP), 'Gunpowder')
check('a hand-queued job is attributable at all', GP_BP in rm, True)
check('1 hand-queued job credits 10 finished output', queued_output(1, 10), 10)
check('2 hand-queued jobs credit 20', queued_output(2, 10), 20)
# Without a blueprint id the alias is skipped entirely - the v2.4.38 state.
rm_nobp = reverse_map(ITEMS, {k: v for k, v in BPS.items() if k != 'Gunpowder'})
check('without a blueprint id it was NOT attributable (v2.4.38 state)',
      GP_BP in rm_nobp, False)

print()
print('THE CATALOG MATCHES WHAT IS ASSERTED ABOVE')
src = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                   'IO_Production_Manager_v2.4.39.cs')
if os.path.exists(src):
    text = io.open(src, encoding='utf-8').read()
    m = re.search(r'"Gunpowder\|([0-9.]+)\|([^|"]*)\|([^"]*)"', text)
    check('Gunpowder recipe present', bool(m), True)
    if m:
        check('catalog yield is 10', m.group(1), '10')
        check('catalog machine is Munitions Factory', m.group(2), 'Munitions Factory')
        check('catalog inputs are 6/2/2', m.group(3),
              'PotassiumNitrate:6,Carbon:2,Sulfur:2')
    lb = re.search(r'"Lightbulb\|([0-9.]+)\|', text)
    check('Lightbulb yield still 10', lb.group(1) if lb else None, '10')
    check('Gunpowder blueprint id present in the catalog',
          'Gunpowder=Gunpowder' in text, True)
else:
    print('  (source not found - skipped)')

print()
if FAIL:
    print('FAILED: %d' % len(FAIL))
    for f in FAIL:
        print('  - ' + f)
    sys.exit(1)
print('ALL CHECKS PASSED')
