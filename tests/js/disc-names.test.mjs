// Disc names next to "Disc N" headers. Run: node --test 'tests/js/*.test.mjs'
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { discNamesForHeaders, normalizeItemId } = require('../../Jellyfin.Plugin.VgmLoop/Web/player.js');

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
