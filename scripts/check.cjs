'use strict';
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const {spawnSync} = require('node:child_process');
const root = path.resolve(__dirname, '..');
const readJson = rel => JSON.parse(fs.readFileSync(path.join(root, rel), 'utf8').replace(/^\uFEFF/, ''));
const manifest = readJson('scripts/release-files.json');
const files = [...manifest.runtime, ...manifest.sourceOnly];
assert.equal(new Set(files).size, files.length, 'Duplicate release entry');
const known = new Set(files);
const ignored = new Set(['.git', 'dist', 'node_modules', 'coverage', '.vscode', '.idea']);
function inspectDirectory(relative = '') {
  for (const entry of fs.readdirSync(path.join(root, relative), {withFileTypes: true})) {
    if (!relative && ignored.has(entry.name)) continue;
    const rel = relative ? relative + '/' + entry.name : entry.name;
    assert.ok(!entry.isSymbolicLink(), 'Linked source entry: ' + rel);
    if (entry.isDirectory()) inspectDirectory(rel);
    else assert.ok(known.has(rel), 'Unlisted source file (review before release): ' + rel);
  }
}
inspectDirectory();
const sensitive = [
  /[A-Z]:[\\/]+Users[\\/]+(?!Public\b|Default\b|Example\b)[^\\/\s"'<>]+/i,
  /\bsk-(?:proj-|svcacct-)?[A-Za-z0-9_-]{24,}/,
  /\b(?:ghp|gho|ghu|ghs)_[A-Za-z0-9]{30,}/,
  /#token=[a-f0-9]{32,}/i,
  /-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----/
];
for (const rel of files) {
  assert.ok(typeof rel === 'string' && rel && !rel.includes('\\') && !path.isAbsolute(rel) && rel.split('/').every(part => part && part !== '.' && part !== '..' && !part.includes(':')), 'Unsafe release path');
  let target = root;
  for (const part of rel.split('/')) {
    target = path.join(target, part);
    assert.ok(!fs.lstatSync(target).isSymbolicLink(), 'Linked release path: ' + rel);
  }
  const st = fs.statSync(target);
  assert.ok(st.isFile() && st.nlink === 1, 'Release input must be an independent file: ' + rel);
  if (rel.endsWith('.png')) {
    assert.ok(fs.readFileSync(target).subarray(0, 8).equals(Buffer.from([137,80,78,71,13,10,26,10])), 'Invalid PNG');
    continue;
  }
  const text = fs.readFileSync(target, 'utf8');
  assert.ok(!text.includes('\u0000'), 'Unexpected binary content: ' + rel);
  for (const pattern of sensitive) assert.ok(!pattern.test(text), 'Possible private content in ' + rel + ' (review locally; do not paste it into an issue)');
  if (/\.(?:cjs|js)$/.test(rel)) {
    const result = spawnSync(process.execPath, ['--check', target], {encoding: 'utf8', windowsHide: true});
    assert.equal(result.status, 0, rel + ': ' + (result.stderr || result.error || 'Syntax check failed'));
  }
  if (/\.json$/.test(rel)) readJson(rel);
  if (rel.endsWith('.md')) {
    for (const match of text.matchAll(/\[[^\]]*\]\(([^)]+)\)/g)) {
      const link = match[1].split('#')[0];
      if (!link || /^[a-z]+:/i.test(link)) continue;
      assert.ok(fs.existsSync(path.resolve(path.dirname(target), decodeURIComponent(link))), 'Broken local link: ' + rel + ' -> ' + link);
    }
  }
}
const pkg = readJson('package.json'), lock = readJson('package-lock.json');
assert.equal(pkg.version, lock.version, 'Package lock version mismatch');
assert.equal(pkg.version, lock.packages[''].version, 'Package lock root version mismatch');
assert.equal(pkg.license, 'MIT');
assert.equal(pkg.private, true, 'Prevent accidental npm publication');
assert.match(pkg.version, /^\d+\.\d+\.\d+$/);
assert.equal(Object.keys(pkg.dependencies || {}).length, 0, 'Review dependency and license policy before adding runtime dependencies');
for (const definitions of Object.values(readJson('test/fixture-schema.json'))) {
  assert.ok(Array.isArray(definitions));
  for (const item of definitions) {
    assert.deepEqual(Object.keys(item).sort(), ['name', 'sql', 'type']);
    assert.ok(['table', 'index', 'trigger', 'view'].includes(item.type));
    assert.match(item.sql, /^CREATE\s+(?:UNIQUE\s+)?(?:TABLE|INDEX|TRIGGER|VIEW)\b/i);
  }
}
console.log(`Checked ${files.length} release inputs, JavaScript syntax, local links, metadata and high-confidence privacy patterns.`);
console.log('This automated scan does not replace review of screenshots, fixtures and new release inputs.');
