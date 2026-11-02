#!/usr/bin/env python3
"""Cross-check handler ids against the audited effect catalog (no game DLLs needed).

Fails if a handler's Id isn't a real audited effect, if a handler class isn't registered, or if an id is registered twice.
"""
import json, pathlib, re, sys

root = pathlib.Path(__file__).resolve().parent.parent
fx = root / "src/PerkTree.Mod/Effects"
raw = json.load(open(root / "data/audit/effects.draft.json"))
raw = raw if isinstance(raw, list) else raw.get("effects", raw)
catalog = {e["id"] for e in raw}
# Perks added after the audit live in their own file, so regenerating the audit (tools/audit/build_audit.py) can never drop them.
added = root / "data/audit/added-effects.json"
if added.exists():
    catalog |= {e["id"] for e in json.load(open(added))}

classes = {}  # class name -> id
for f in fx.glob("*.cs"):
    text = f.read_text()
    headers = list(re.finditer(r'\bclass\s+(\w+)(?:<[^>]*>)?\s*:\s*[\w<>, ]+', text))
    for i, m in enumerate(headers):
        end = headers[i + 1].start() if i + 1 < len(headers) else len(text)
        idm = re.search(r'string Id\s*=>\s*"([^"]+)"', text[m.end():end])
        if idm: classes[m.group(1)] = idm.group(1)

reg_text = (fx / "HandlerRegistry.cs").read_text()
registry = re.findall(r'new (\w+)\(\)', reg_text.split("PatchHandled")[0])
handled_elsewhere = set(re.findall(r'"(\w+)"', reg_text.split("PatchHandled")[1].split("};")[0]))
problems = []
ids = [classes.get(c) for c in registry]
for c in registry:
    if c not in classes: problems.append(f"registered class {c} has no Id")
for c in set(classes) - set(registry):
    problems.append(f"handler class {c} ({classes[c]}) is not registered")
for i in ids:
    if i and i not in catalog: problems.append(f"handler id '{i}' is not an audited effect")
dups = {i for i in ids if ids.count(i) > 1}
for i in dups: problems.append(f"id '{i}' registered twice")

# Co-op safety: nothing that gives a different answer on different machines may appear in effect code. All randomness
# goes through Roll()/PerkRng (the game's seeded RNG); work must be awaited, never detached.
BANNED = {
    r"\bSystem\.Random\b|\bnew Random\(|\bRandom\.Shared\b": "System.Random (unseeded)",
    r"\.Chaotic\b": "Rng.Chaotic (explicitly non-deterministic)",
    r"\bGD\.Rand": "GD.Rand* (Godot's own unsynced RNG)",
    r"\bGuid\.NewGuid\b": "Guid.NewGuid",
    r"\bDateTime(Offset)?\.(Now|UtcNow|Today)\b|\bEnvironment\.TickCount\b|\bStopwatch\b": "wall clock",
    r"\.GetHashCode\(\)": "GetHashCode() (randomized per process for strings)",
    r"\bLocalContext\b": "LocalContext (differs per client)",
    r"\bTask\.Run\b|\bRunSafely\b": "detached task (must be awaited in order)",
}
scanned = list(fx.glob("*.cs")) + [root / "src/PerkTree.Mod/PerkHostModel.cs"]
for f in scanned:
    for n, line in enumerate(f.read_text().splitlines(), 1):
        code = line.split("//")[0]
        for pat, why in BANNED.items():
            if re.search(pat, code):
                problems.append(f"{f.relative_to(root)}:{n}: {why}")

todo = catalog - set(ids) - handled_elsewhere
print(f"{len(registry)} handlers registered, {len(handled_elsewhere)} handled by patches or read by other handlers, "
      f"{len(catalog)} audited effects, {len(todo)} still without code")
if "--missing" in sys.argv:
    print("still without code:", ", ".join(sorted(todo)))
for p in problems: print("PROBLEM:", p)
sys.exit(1 if problems else 0)
