import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { analyzeCommits } from '@semantic-release/commit-analyzer';
import { generateNotes } from '@semantic-release/release-notes-generator';
const config = JSON.parse(readFileSync(new URL('../.releaserc.json', import.meta.url), 'utf8'));
const optionsFor = name => config.plugins.find(plugin => Array.isArray(plugin) && plugin[0] === name)[1];
const commit = message => ({ hash: 'a'.repeat(40), message });
const context = messages => ({
  cwd: process.cwd(),
  env: {},
  logger: console,
  commits: messages.map(commit),
  options: { repositoryUrl: 'https://github.com/I-Sarma-I/Nothing-desktop.git' },
  lastRelease: {},
  nextRelease: { version: '1.0.0', gitTag: 'v1.0.0', gitHead: 'b'.repeat(40) },
});
for (const [message, expected] of [
  ['fix: clear panel after device removal', 'patch'],
  ['feat: add device sidebar', 'minor'],
  ['feat!: replace device configuration', 'major'],
  ['docs: clarify Windows setup', null],
]) {
  test(`configured analyzer maps ${message.split(':')[0]} to ${expected ?? 'no release'}`, async () => {
    assert.equal(await analyzeCommits(optionsFor('@semantic-release/commit-analyzer'), context([message])), expected);
  });
}
test('configured preset generates first-release notes without missing helpers', async () => {
  const notes = await generateNotes(optionsFor('@semantic-release/release-notes-generator'), context([
    'feat: add device sidebar',
    'fix: clear panel after device removal',
  ]));
  assert.match(notes, /1\.0\.0/);
  assert.match(notes, /Features/);
  assert.match(notes, /add device sidebar/);
  assert.match(notes, /Bug Fixes/);
  assert.match(notes, /clear panel after device removal/);
  assert.doesNotMatch(notes, /Missing helper|requires conventional-changelog-writer/);
});
test('configured preset generates links comparing successive releases', async () => {
  const input = context(['fix: improve light mode']);
  input.lastRelease = { version: '1.0.0', gitTag: 'v1.0.0', gitHead: 'c'.repeat(40) };
  input.nextRelease = { version: '1.0.1', gitTag: 'v1.0.1', gitHead: 'd'.repeat(40) };
  const notes = await generateNotes(optionsFor('@semantic-release/release-notes-generator'), input);
  assert.ok(notes.includes('compare/v1.0.0...v1.0.1'));
  assert.match(notes, /improve light mode/);
});
test('configured preset includes explicit breaking-change notes', async () => {
  const notes = await generateNotes(optionsFor('@semantic-release/release-notes-generator'), context([
    `feat!: replace device configuration
BREAKING CHANGE: Saved device files must be recreated.`,
  ]));
  assert.match(notes, /BREAKING CHANGES/);
  assert.match(notes, /Saved device files must be recreated/);
});