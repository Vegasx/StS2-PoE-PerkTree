#!/usr/bin/env node
// Checks the perk-tree data without opening the editor. Usage: node tools/validate.mjs [repo-root]
// Exit code 1 when any error is found. Warnings never fail the run.
import {readFileSync, existsSync} from 'node:fs';
import {join, dirname} from 'node:path';
import {fileURLToPath} from 'node:url';

const root = process.argv[2] || join(dirname(fileURLToPath(import.meta.url)), '..');
const errors = [], warnings = [], notes = [];
const err = m => errors.push(m), warn = m => warnings.push(m);
const load = p => JSON.parse(readFileSync(join(root, p), 'utf8'));
const read = p => { try { return load(p); } catch (e) { err(`${p}: ${e.message}`); return null; } };

const iconFile = read('data/icons.json');
const perksFile = read('data/perks.json'), kwFile = read('data/keywords.json'), pairsFile = read('data/pairs.json'), modFile = read('data/modules.json');
if (!perksFile || !kwFile || !pairsFile || !modFile) finish();

const TOKENS = new Set(['v', 'u', 'm', 'x', 'd', 'h', 'c']);
const SCOPES = new Set(['S', 'E', 'X']), KINDS = new Set(['add', 'pct', 'flag']);
const NODE_TYPES = new Set(['minor', 'notable', 'cap', 'socket', 'start']);

// perks
const P = {};
for (const p of perksFile.perks) {
  if (P[p.id]) err(`perks.json: duplicate perk id ${p.id}`);
  P[p.id] = p;
  if (!SCOPES.has(p.scope)) err(`perks.json: ${p.id} has scope ${p.scope}, expected S, E or X`);
  if (!KINDS.has(p.kind)) err(`perks.json: ${p.id} has kind ${p.kind}, expected add, pct or flag`);
  if (!Number.isFinite(p.unit)) err(`perks.json: ${p.id} unit is not a number`);
  if (p.icon != null && iconFile && !iconFile.icons.includes(p.icon)) err(`perks.json: ${p.id} has icon ${p.icon}, which is not in data/icons.json`);
  if (p.cap != null && !Number.isFinite(p.cap)) err(`perks.json: ${p.id} cap is not a number`);
  if (p.chance != null && !(p.chance > 0 && p.chance <= 10000)) err(`perks.json: ${p.id} chance must be above 0 and at most 10000 (percent; above 100 gives sure successes plus a roll)`);
  if (p.kind === 'pct') warn(`perks.json: ${p.id} uses the legacy pct kind: make it kind add with a chance field`);
  if (p.chance != null && p.kind === 'pct') warn(`perks.json: ${p.id} is a legacy pct perk, its chance field is ignored`);
  for (const t of (p.text.match(/\{(\w+)\}/g) || [])) if (!TOKENS.has(t.slice(1, -1))) err(`perks.json: ${p.id} text uses unknown token ${t}`);
}
const chars = modFile.characters, mods = modFile.modules;
const groups = new Set(['general', 'vigor', ...Object.keys(chars)]);
for (const p of perksFile.perks) if (!groups.has(p.group || 'general')) err(`perks.json: ${p.id} has unknown group ${p.group}`);

// keywords
const KW = [];
for (const [k, v] of Object.entries(kwFile.keywords)) {
  try { KW.push([k, new RegExp(v.match || k), !!v.gap]); } catch (e) { err(`keywords.json: ${k} has a bad match pattern`); }
}
const gaps = new Set();
for (const p of perksFile.perks) for (const [k, re, gap] of KW) if (re.test(p.text) && gap) gaps.add(k);
if (gaps.size) warn(`keyword tooltip source still missing for: ${[...gaps].join(', ')}`);

// pairs
for (const key of ['synergy', 'conflicts']) for (const g of pairsFile[key] || []) for (const id of [...g.perks, ...g.with]) if (!P[id]) err(`pairs.json: ${key} mentions unknown perk ${id}`);
for (const id of pairsFile.luckModifiers || []) if (!P[id]) err(`pairs.json: luckModifiers mentions unknown perk ${id}`);

// modules
const need = {S: 2, M: 2, LL: 2, LR: 2, OL: 6, OR: 6, ring: 3, F: 1, L0: 1, T: 1, K: 1};
for (const [id, m] of Object.entries(mods)) {
  for (const [k, n] of Object.entries(need)) {
    if (!Array.isArray(m[k]) || m[k].length !== n) { err(`modules.json: ${id}.${k} needs ${n} perk ids`); continue; }
    for (const p of m[k]) if (!P[p]) err(`modules.json: ${id}.${k} uses unknown perk ${p}`);
  }
}
for (const [ck, c] of Object.entries(chars)) {
  if (!P[c.start]) err(`modules.json: ${ck} start perk ${c.start} does not exist`);
  if (!Array.isArray(c.slots) || c.slots.length !== 8) err(`modules.json: ${ck}.slots needs 8 entries`);
  else for (const s of c.slots) if (s && !mods[s]) err(`modules.json: ${ck} slot uses unknown module ${s}`);
}

// trees
const supportFile = existsSync(join(root, 'data/mod-support.json')) ? load('data/mod-support.json') : null;
const general = perksFile.perks.filter(p => (p.group || 'general') === 'general').map(p => p.id);
const summary = [];
for (const ck of Object.keys(chars)) {
  const file = `data/trees/${ck}.json`;
  if (!existsSync(join(root, file))) { err(`${file} is missing`); continue; }
  const L = read(file); if (!L) continue;
  const where = m => `${file}: ${m}`;
  const foreign = [];
  const byId = new Map();
  for (const n of L.nodes) {
    if (byId.has(n.id)) err(where(`duplicate node id ${n.id}`));
    byId.set(n.id, n);
    if (!NODE_TYPES.has(n.type)) err(where(`${n.id} has type ${n.type}`));
    if (!Number.isFinite(n.x) || !Number.isFinite(n.y)) err(where(`${n.id} has no position`));
    if (n.type !== 'socket' && !P[n.fx]) err(where(`${n.id} uses unknown perk ${n.fx}`));
    else if (n.type !== 'socket' && !['general', 'vigor', ck].includes(P[n.fx].group)) foreign.push(`${n.id}=${n.fx} (${P[n.fx].group})`);
  }
  if (foreign.length) warn(where(`${foreign.length} perk(s) from another character (allowed, but they may do nothing if ${ck} cannot trigger them): ${foreign.slice(0, 5).join(', ')}${foreign.length > 5 ? ' ...' : ''}`));
  const nb = new Map([...byId.keys()].map(k => [k, new Set()]));
  const seen = new Set();
  for (const e of L.edges) {
    if (!byId.has(e.a) || !byId.has(e.b)) { err(where(`edge ${e.a} to ${e.b} points at a missing node`)); continue; }
    if (e.a === e.b) { err(where(`edge ${e.a} joins a node to itself`)); continue; }
    const k = [e.a, e.b].sort().join('|');
    if (seen.has(k)) { warn(where(`duplicate edge ${e.a} to ${e.b}`)); continue; }
    seen.add(k); nb.get(e.a).add(e.b); nb.get(e.b).add(e.a);
  }
  const starts = L.nodes.filter(n => n.type === 'start');
  if (starts.length < 1) { err(where('needs at least one start node')); continue; }
  // cost to reach: each perk counts 1, sockets are free joints. Every start is an entry point, so costs count from the nearest one.
  const cost = new Map(starts.map(s => [s.id, 1])), todo = starts.map(s => [1, s.id]);
  while (todo.length) {
    todo.sort((a, b) => a[0] - b[0]);
    const [d, u] = todo.shift();
    if (d > cost.get(u)) continue;
    for (const v of nb.get(u)) {
      const nd = d + (byId.get(v).type === 'socket' ? 0 : 1);
      if (!cost.has(v) || nd < cost.get(v)) { cost.set(v, nd); todo.push([nd, v]); }
    }
  }
  const perks = L.nodes.filter(n => n.type !== 'socket');
  const lost = perks.filter(n => !cost.has(n.id));
  if (lost.length) err(where(`${lost.length} perk(s) cannot be reached from the start, e.g. ${lost.slice(0, 4).map(n => n.id).join(', ')}`));
  for (const n of perks) if (n.amount != null) {
    const pf = P[n.fx];
    if (!Number.isFinite(n.amount) || n.amount === 0) err(where(`${n.id} has amount ${n.amount}, which must be a number other than 0`));
    else if (pf && pf.kind === 'flag') err(where(`${n.id} has its own amount (${n.amount}), but ${n.fx} is an on/off perk and has no amount`));
  }
  for (const n of perks) if (n.chance != null) {
    if (!(n.chance > 0 && n.chance <= 10000)) err(where(`${n.id} has chance ${n.chance}%, which must be above 0 and at most 10000`));
    else if (n.chance !== 100 && supportFile && supportFile.effects[n.fx] && !supportFile.effects[n.fx].rollsChance) err(where(`${n.id} has a ${n.chance}% chance, but the mod never rolls ${n.fx} (always 100%)`));
  }
  const count = {};
  for (const n of perks) count[n.fx] = (count[n.fx] || 0) + 1;
  // A perk left out of a tree is fine (leaving things out is a design choice); it is only listed, never an error or a warning.
  const unplaced = perksFile.perks.filter(p => ((p.group || 'general') === 'general' || p.group === ck) && !count[p.id]).map(p => p.id);
  if (unplaced.length) notes.push(where(`${unplaced.length} perk(s) not placed: ${unplaced.join(', ')}`));
  for (const [id, c] of Object.entries(count)) {
    const p = P[id]; if (!p) continue;
    if (p.kind === 'flag' && c > 1) err(where(`on/off perk ${id} is placed ${c} times`));
    // Placing more than the cap allows is a design choice (a luck perk with many nodes and a low cap, say), and the mod's own checker only warns,
    // so this is a note too. It adds up each node's real amount (its own override, else the perk's number), like the editor and the exporter.
    if (p.cap != null) {
      const total = perks.filter(n => n.fx === id).reduce((a, n) => a + (n.amount != null ? n.amount : p.unit), 0);
      if (total > p.cap) notes.push(where(`${id} reaches ${Math.round(total * 1e6) / 1e6} across the tree but is capped at ${p.cap}; anything past the cap is wasted`));
    }
  }
  const keys = perks.filter(n => P[n.fx] && P[n.fx].tier === 'key' && cost.has(n.id)).map(n => [P[n.fx].name, n.fx, cost.get(n.id)]).sort((a, b) => b[2] - a[2]);
  for (const [name, id, c] of keys) {
    const t = modFile.targets && modFile.targets[id];
    if (t != null && Math.abs(c - t) > 3) warn(`${ck}: ${name} is ${c} points from the start, target is about ${t}`);
  }
  summary.push(`${ck.padEnd(12)} ${String(perks.length).padStart(3)} perks, ${String(L.nodes.length - perks.length).padStart(2)} sockets, ${String(L.edges.length).padStart(3)} links, keystones: ${keys.map(k => `${k[0]} ${k[2]}`).join(', ')}`);
}
// ---- how the mod runs these perks (data/mod-support.json, made by tools/scan-mod.mjs)
if (existsSync(join(root, 'data/mod-support.json'))) {
  const sup = load('data/mod-support.json').effects;
  const noCode = [];
  for (const p of perksFile.perks) {
    const s = sup[p.id];
    if (!s) { noCode.push(p.id); continue; }
    if (p.chance != null && p.chance !== 100 && !s.rollsChance) err(`perks.json: ${p.id} has a ${p.chance}% trigger chance, but the mod never rolls it (always 100%), so the chance would be ignored in game`);
  }
  if (noCode.length) warn(`${noCode.length} perk(s) have no mod code yet: ${noCode.join(', ')}`);
}
finish(summary);

function finish(summary = []) {
  for (const s of summary) console.log(s);
  for (const n of notes) console.log('note: ' + n);
  for (const w of warnings) console.log('warning: ' + w);
  for (const e of errors) console.log('error: ' + e);
  console.log(errors.length ? `${errors.length} error(s), ${warnings.length} warning(s)` : `OK, ${warnings.length} warning(s)`);
  process.exit(errors.length ? 1 : 0);
}
