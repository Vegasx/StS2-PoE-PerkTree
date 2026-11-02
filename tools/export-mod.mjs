#!/usr/bin/env node
// Turns the editor's data into the files the mod loads: effects.json and one <character>.tree.json per character.
// Usage: node tools/export-mod.mjs [--out <folder>] [--root <repo-root>]
// Default output folder is mod/data/runtime. Copy the files next to PerkTree.dll (its data/ folder) to try them in game.
import {readFileSync, writeFileSync, mkdirSync, existsSync} from 'node:fs';
import {join, dirname, resolve} from 'node:path';
import {fileURLToPath} from 'node:url';

const args = process.argv.slice(2);
const opt = (name, dflt) => { const i = args.indexOf(name); return i >= 0 && args[i + 1] ? args[i + 1] : dflt; };
const root = resolve(opt('--root', join(dirname(fileURLToPath(import.meta.url)), '..')));
const outDir = resolve(root, opt('--out', 'mod/data/runtime'));
const errors = [], warnings = [];
const err = m => { if (!errors.includes(m)) errors.push(m); }, warn = m => { if (!warnings.includes(m)) warnings.push(m); };
const load = p => JSON.parse(readFileSync(join(root, p), 'utf8'));

const perksFile = load('data/perks.json'), modules = load('data/modules.json');
const support = existsSync(join(root, 'data/mod-support.json')) ? load('data/mod-support.json') : null;
if (!support) warn('data/mod-support.json is missing, so chances and amounts cannot be checked against the mod. Run node tools/scan-mod.mjs.');
const sup = id => (support && support.effects[id]) || null;
const nativeKw = existsSync(join(root, 'mod/data/native-keywords.txt'))
  ? new Set(readFileSync(join(root, 'mod/data/native-keywords.txt'), 'utf8').split(/\r?\n/).map(l => l.trim()).filter(l => l && !l.startsWith('#'))) : null;

const SCOPE = {S: 'self', E: 'enemyThroughMe', X: 'sharedEnemy'};
const TIER = {minor: 'small', notable: 'notable', cap: 'keystone', start: 'small'};
const chars = modules.characters;
const round = x => Math.round(x * 10000) / 10000;
// percent; 100 when unset, above 100 gives sure successes plus a roll (see mod/docs/chance-model.md)
const chanceOf = p => (p.chance == null ? 100 : p.chance);
const P = {};
for (const p of perksFile.perks) P[p.id] = p;

// Can the mod roll a chance for this perk? Unknown to the mod (no code yet) counts as yes: nothing is lost, it does nothing anyway.
const canRoll = p => { const s = sup(p.id); return !s || s.rollsChance; };

// ---- effects.json
const effects = perksFile.perks.map(p => {
  const e = {id: p.id, scope: SCOPE[p.scope]};
  const scale = p.percent ? 100 : 1;
  const cap = p.kind === 'flag' ? 1 : (p.cap != null ? round(p.cap / scale) : null);
  if (cap != null) e.cap = cap;
  if (p.keywords && p.keywords.length) {
    e.keywords = p.keywords;
    if (nativeKw) for (const k of p.keywords) if (!nativeKw.has(k)) err(`${p.id}: keyword ${k} is not in mod/data/native-keywords.txt`);
  }
  if (chars[p.group]) e.characters = [p.group.toUpperCase()];
  // default chance of every node of this perk as a fraction (1 = always, so it is left out); a node can override it
  if (!(chanceOf(p) > 0 && chanceOf(p) <= 10000)) err(`${p.id}: chance ${chanceOf(p)}% must be above 0 and at most 10000`);
  else if (chanceOf(p) !== 100) {
    e.chance = round(chanceOf(p) / 100);
    if (!canRoll(p)) err(`${p.id} has a ${chanceOf(p)}% chance, but the mod never rolls this perk (it is always 100%). Set it back to 100.`);
  }
  return e;
});

// ---- magnitude of one node of a perk, in the mod's units: the amount it gives each time it succeeds (the chance is separate)
function magnitude(p) { return round(p.percent ? p.unit / 100 : p.unit); }

// ---- trees
const summary = [];
mkdirSync(outDir, {recursive: true});
const generalTags = new Set(['strength_defense', 'tempo', 'gamble_overdraw', 'gamble_debt', 'offense', 'alchemy', 'travel', 'custom']);
for (const ck of Object.keys(chars)) {
  const file = `data/trees/${ck}.json`;
  if (!existsSync(join(root, file))) { err(`${file} is missing`); continue; }
  const L = load(file), byId = new Map(L.nodes.map(n => [n.id, n]));
  const nb = new Map(L.nodes.map(n => [n.id, new Set()]));
  for (const e of L.edges) if (byId.has(e.a) && byId.has(e.b) && e.a !== e.b) { nb.get(e.a).add(e.b); nb.get(e.b).add(e.a); }
  // sockets are free joints in the editor, the mod has none: join their neighbours directly
  const pairs = new Set();
  const add = (a, b) => { if (a !== b) pairs.add(a < b ? a + '|' + b : b + '|' + a); };
  for (const n of L.nodes) {
    if (n.type === 'socket') { const ns = [...nb.get(n.id)].filter(x => byId.get(x).type !== 'socket'); for (const a of ns) for (const b of ns) add(a, b); }
    else for (const m of nb.get(n.id)) if (byId.get(m).type !== 'socket') add(n.id, m);
  }
  const perks = L.nodes.filter(n => n.type !== 'socket');
  const starts = perks.filter(n => n.type === 'start').map(n => n.id);
  if (starts.length < 1) err(`${file}: needs at least one start node`);
  const branchOf = n => n.tag || 'travel';
  const branches = new Map();
  for (const n of perks) {
    const id = branchOf(n);
    if (!branches.has(id)) branches.set(id, {id, name: id, kind: generalTags.has(id) ? 'general' : 'character'});
  }
  const nodes = perks.map(n => {
    const p = P[n.fx];
    if (!p) { err(`${file}: ${n.id} uses unknown perk ${n.fx}`); return null; }
    if (p.group !== 'general' && p.group !== 'vigor' && p.group !== ck) warn(`${file}: ${n.id} uses ${p.id}, a ${p.group} perk, in the ${ck} tree (allowed, but it does nothing if ${ck} cannot trigger it)`);
    // the amount a node gives per success: its own override (in the perk's units, scaled like the perk's number) or the perk's number
    let mag = magnitude(p);
    if (n.amount != null && n.amount !== p.unit) {
      if (p.kind === 'flag') err(`${file}: ${n.id} has its own amount (${n.amount}), but ${p.id} is an on/off perk and has no amount`);
      else if (!Number.isFinite(n.amount) || n.amount === 0) err(`${file}: ${n.id} has amount ${n.amount}, which must be a number other than 0`);
      else mag = round(p.percent ? n.amount / 100 : n.amount);
    }
    if (!mag) err(`${file}: ${n.id} has magnitude 0 (${p.id})`);
    const node = {id: n.id, branch: branchOf(n), tier: TIER[n.type] || 'small', effectId: p.id, magnitude: mag, x: Math.round(n.x), y: Math.round(n.y)};
    // optional per-node chance (percent in the editor), only written when it differs from the perk's own
    if (n.chance != null && n.chance !== chanceOf(p)) {
      if (!(n.chance > 0 && n.chance <= 10000)) err(`${file}: ${n.id} has chance ${n.chance}%, which must be above 0 and at most 10000`);
      else if (n.chance !== 100 && !canRoll(p)) err(`${file}: ${n.id} has a ${n.chance}% chance, but the mod never rolls ${p.id} (always 100%)`);
      else node.chance = round(n.chance / 100);
    }
    return node;
  }).filter(Boolean);
  const edges = [...pairs].map(k => k.split('|'));
  // reachability from the start, the same rule the mod's validator uses
  const adj = new Map(nodes.map(n => [n.id, []]));
  for (const [a, b] of edges) { adj.get(a).push(b); adj.get(b).push(a); }
  const seen = new Set(starts), q = [...starts];
  while (q.length) for (const m of adj.get(q.shift()) || []) if (!seen.has(m)) { seen.add(m); q.push(m); }
  const lost = nodes.filter(n => !seen.has(n.id));
  if (lost.length) err(`${file}: ${lost.length} node(s) cannot be reached from the start, e.g. ${lost.slice(0, 3).map(n => n.id).join(', ')}`);
  const tree = {schemaVersion: 2, characterId: ck, starts, branches: [...branches.values()], nodes, edges};
  const dump = '{\n"schemaVersion":2,\n"characterId":' + JSON.stringify(ck) + ',\n"starts":' + JSON.stringify(starts) + ',\n"branches":[\n' + tree.branches.map(b => JSON.stringify(b)).join(',\n') + '\n],\n"nodes":[\n' + nodes.map(n => JSON.stringify(n)).join(',\n') + '\n],\n"edges":[\n' + edges.map(e => JSON.stringify(e)).join(',\n') + '\n]\n}\n';
  if (!errors.length) writeFileSync(join(outDir, `${ck}.tree.json`), dump);
  summary.push(`${ck.padEnd(12)} ${String(nodes.length).padStart(3)} nodes, ${String(edges.length).padStart(3)} edges, ${tree.branches.length} branches`);
}
if (!errors.length) {
  writeFileSync(join(outDir, 'effects.json'), '{\n"schemaVersion":2,\n"effects":[\n' + effects.map(e => JSON.stringify(e)).join(',\n') + '\n]\n}\n');
}

// perks the mod cannot run yet, so nobody is surprised in game
if (support) {
  const placed = new Set();
  for (const ck of Object.keys(chars)) if (existsSync(join(root, `data/trees/${ck}.json`))) for (const n of load(`data/trees/${ck}.json`).nodes) if (n.fx) placed.add(n.fx);
  const none = perksFile.perks.filter(p => placed.has(p.id) && !sup(p.id)).map(p => p.id);
  if (none.length) warn(`${none.length} placed perk(s) have no mod code yet and do nothing in game: ${none.join(', ')}`);
}

for (const s of summary) console.log(s);
for (const w of warnings) console.log('warning: ' + w);
for (const e of errors) console.log('error: ' + e);
console.log(errors.length ? `${errors.length} error(s), nothing written` : `Wrote effects.json and ${summary.length} tree file(s) to ${outDir}`);
process.exit(errors.length ? 1 : 0);
