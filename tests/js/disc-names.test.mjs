// Disc names next to "Disc N" headers. Run: node --test 'tests/js/*.test.mjs'
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { discNamesForHeaders, normalizeItemId, loopingIds, discNameSuffix } = require('../../Jellyfin.Plugin.VgmLoop/Web/player.js');

const A = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
const B = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';
const C = 'cccccccccccccccccccccccccccccccc';

test('normalizeItemId accepts dashed and upper-case ids', () => {
    assert.equal(normalizeItemId('AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA'), A);
    assert.equal(normalizeItemId(null), '');
});

test('headers are matched by their first track, not by text', () => {
    const response = {
        albumId: 'x',
        discs: [
            { discNumber: 2, name: 'Arrange', itemIds: [B, 'b2'] },
            { discNumber: 3, name: 'Bonus', itemIds: [C] }
        ]
    };
    assert.deepEqual(discNamesForHeaders([A, B, C], response), [null, 'Arrange', 'Bonus']);
});

test('dashed ids in the DOM still match', () => {
    const dashed = 'BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB';
    assert.deepEqual(discNamesForHeaders([dashed], { discs: [{ discNumber: 1, name: 'One', itemIds: [B] }] }), ['One']);
});

test('missing or malformed responses give no names', () => {
    assert.deepEqual(discNamesForHeaders([A, null], null), [null, null]);
    assert.deepEqual(discNamesForHeaders([A], { discs: 'nope' }), [null]);
    assert.deepEqual(discNamesForHeaders([A], { discs: [null, { name: '', itemIds: [A] }, { name: 'x' }] }), [null]);
    assert.deepEqual(discNamesForHeaders(undefined, { discs: [] }), []);
});

test('disc names read "Disc N - name"', () => {
    assert.equal('Disc 2' + discNameSuffix('Arrange Album'), 'Disc 2 - Arrange Album');
});

test('loopingIds normalizes ids and tolerates bad responses', () => {
    assert.deepEqual([...loopingIds({ loopItemIds: ['AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA', B, null] })], [A, B]);
    assert.equal(loopingIds(null).size, 0);
    assert.equal(loopingIds({ loopItemIds: 'nope' }).size, 0);
});

const { chunk, runLimited } = require('../../Jellyfin.Plugin.VgmLoop/Web/player.js');

test('chunk keeps order and sizes', () => {
    assert.deepEqual(chunk([1, 2, 3, 4, 5], 2), [[1, 2], [3, 4], [5]]);
    assert.deepEqual(chunk([], 100), []);
});

test('runLimited starts jobs in order, caps concurrency, survives failures', async () => {
    let running = 0;
    let peak = 0;
    const started = [];
    await runLimited([1, 2, 3, 4, 5, 6, 7], 3, async (n) => {
        started.push(n);
        running++;
        peak = Math.max(peak, running);
        await new Promise((r) => setTimeout(r, 5));
        running--;
        if (n === 2) throw new Error('boom');
    });
    assert.deepEqual(started, [1, 2, 3, 4, 5, 6, 7]);
    assert.equal(peak, 3);
});

const { discGroups, shuffleList } = require('../../Jellyfin.Plugin.VgmLoop/Web/player.js');

test('discGroups: rows belong to the header above them', () => {
    const H = { header: true };
    const r = (id, audio = true) => ({ id, audio });
    assert.deepEqual(discGroups([r('x'), H, r('a'), r('b'), H, r('c'), r('v', false), H]), [['a', 'b'], ['c'], []]);
    assert.deepEqual(discGroups([]), []);
    assert.deepEqual(discGroups(null), []);
});

test('shuffleList: a permutation, input untouched, uses the given random source', () => {
    const list = Array.from({ length: 50 }, (_, i) => i);
    let seed = 7;
    const rnd = () => ((seed = (seed * 16807) % 2147483647) / 2147483647);
    const out = shuffleList(list, rnd);
    assert.deepEqual([...out].sort((a, b) => a - b), list);
    assert.notDeepEqual(out, list);
    assert.deepEqual(list, Array.from({ length: 50 }, (_, i) => i));
    assert.deepEqual(shuffleList([]), []);
    assert.deepEqual(shuffleList(['only']), ['only']);
});
