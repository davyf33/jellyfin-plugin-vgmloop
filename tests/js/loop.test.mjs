// Loop-engine math (docs/design.md). Run: node --test 'tests/js/*.test.mjs'
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const core = require('../../Jellyfin.Plugin.VgmLoop/Web/player.js');

// wii_menu fixture: 32 kHz, loop 197319 -> 2349639 (exclusive), total 2349639.
const R = 32000, LS = 197319, LE = 2349639, TOTAL = 2349639, LEN = LE - LS;
const at = (anchor, now) => core.positionAt(anchor, now, R, LS, LE, TOTAL);

test('isLoopable', () => {
    const ok = { hasLoop: true, codec: 'flac', sampleRate: 32000, loopStart: LS, loopEnd: LE };
    assert.equal(core.isLoopable(ok), true);
    assert.equal(core.isLoopable({ ...ok, codec: 'opus' }), true);
    assert.equal(core.isLoopable({ ...ok, codec: 'vorbis' }), true);
    assert.equal(core.isLoopable({ ...ok, codec: 'mp3' }), false);
    assert.equal(core.isLoopable({ ...ok, hasLoop: false }), false);
    assert.equal(core.isLoopable({ ...ok, loopEnd: LS }), false);
    assert.equal(core.isLoopable({ ...ok, sampleRate: 0 }), false);
    assert.equal(core.isLoopable(null), false);
    assert.equal(core.isLoopable({ hasLoop: true, codec: 'flac', sampleRate: 32000 }), false, 'missing points');
});

test('repeat-mode decision table', () => {
    for (const [mode, pos, want] of [
        ['RepeatOne', 0, true],
        ['RepeatOne', LS, true],
        ['RepeatOne', LE - 1, true],
        ['RepeatOne', LE, false], // outro / end
        ['RepeatOne', TOTAL + 5, false],
        ['RepeatAll', 0, false],
        ['RepeatNone', 1000, false],
        [undefined, 0, false]
    ]) {
        assert.equal(core.shouldLoop(mode, pos, LE), want, `${mode} @ ${pos}`);
    }
});

test('positionAt: linear before the seam', () => {
    const a = { ctxTime0: 10, pos0: 0, looping: true };
    assert.deepEqual(at(a, 10), { pos: 0, crossed: false });
    assert.deepEqual(at(a, 11), { pos: R, crossed: false });
    assert.deepEqual(at(a, 9), { pos: 0, crossed: false }, 'before the anchor (scheduled start) clamps to pos0');
});

test('positionAt: wraps at loopEnd exactly', () => {
    const a = { ctxTime0: 0, pos0: LE - R, looping: true }; // 1 s before the seam
    assert.deepEqual(at(a, 1), { pos: LS, crossed: true });
    assert.deepEqual(at(a, 1.5), { pos: LS + R / 2, crossed: true });
    // many passes later: 1 s before seam + 10 loops + 0.25 s
    const t = 1 + (10 * LEN) / R + 0.25;
    const r = at(a, t);
    assert.equal(r.crossed, true);
    assert.ok(Math.abs(r.pos - (LS + R / 4)) < 1e-3, r.pos);
});

test('positionAt: not looping runs to the end and clamps', () => {
    const a = { ctxTime0: 0, pos0: LE - R, looping: false };
    assert.deepEqual(at(a, 0.5), { pos: LE - R / 2, crossed: false });
    assert.deepEqual(at(a, 100), { pos: TOTAL, crossed: false });
});

test('positionAt: loop that ends before the file end (outro)', () => {
    const le = 2000000;
    const a = { ctxTime0: 0, pos0: le - 10, looping: false };
    const r = core.positionAt(a, 1, R, LS, le, TOTAL);
    assert.equal(r.pos, le - 10 + R, 'plays into the outro');
});

test('repeatTransition: One -> All while looping stops looping at the wrapped position', () => {
    const a = { ctxTime0: 0, pos0: LE - R, looping: true };
    const now = 2; // 1 s after the seam
    const t = core.repeatTransition(a, now, R, LS, LE, TOTAL, 'RepeatAll');
    assert.deepEqual(t, { anchor: { ctxTime0: now, pos0: LS + R, looping: false }, crossed: true });
    // afterwards the position continues linearly from there
    assert.deepEqual(at(t.anchor, now + 1), { pos: LS + 2 * R, crossed: false });
});

test('repeatTransition: None -> One before loopEnd starts looping', () => {
    const a = { ctxTime0: 0, pos0: 0, looping: false };
    const t = core.repeatTransition(a, 3, R, LS, LE, TOTAL, 'RepeatOne');
    assert.deepEqual(t, { anchor: { ctxTime0: 3, pos0: 3 * R, looping: true }, crossed: false });
});

test('repeatTransition: None -> One past loopEnd does nothing (outro)', () => {
    const le = 2000000;
    const a = { ctxTime0: 0, pos0: le + 100, looping: false };
    assert.equal(core.repeatTransition(a, 1, R, LS, le, TOTAL, 'RepeatOne'), null);
});

test('repeatTransition: no-op when the mode keeps the same looping state', () => {
    const looping = { ctxTime0: 0, pos0: 0, looping: true };
    assert.equal(core.repeatTransition(looping, 1, R, LS, LE, TOTAL, 'RepeatOne'), null);
    const plain = { ctxTime0: 0, pos0: 0, looping: false };
    assert.equal(core.repeatTransition(plain, 1, R, LS, LE, TOTAL, 'RepeatAll'), null);
    assert.equal(core.repeatTransition(plain, 1, R, LS, LE, TOTAL, 'RepeatNone'), null);
});

test('repeatTransition round trip keeps the position continuous', () => {
    let a = { ctxTime0: 0, pos0: 0, looping: true };
    let now = 80; // wrapped once (seam at ~73.4 s)
    const off = core.repeatTransition(a, now, R, LS, LE, TOTAL, 'RepeatNone');
    const before = at(a, now).pos;
    assert.equal(off.anchor.pos0, before);
    a = off.anchor;
    now += 2;
    const on = core.repeatTransition(a, now, R, LS, LE, TOTAL, 'RepeatOne');
    assert.equal(on.anchor.pos0, before + 2 * R);
    assert.equal(on.anchor.looping, true);
});

test('stopReportPosition', () => {
    assert.equal(core.stopReportPosition(12345, false, LE), 12345);
    assert.equal(core.stopReportPosition(12345, true, LE), LE);
});

test('msToSamples clamps into the buffer', () => {
    assert.equal(core.msToSamples(6166.21875, R, TOTAL), LS);
    assert.equal(core.msToSamples(-5, R, TOTAL), 0);
    assert.equal(core.msToSamples(1e9, R, TOTAL), TOTAL - 1);
});

test('loopEndSeconds never rounds past the sample index', () => {
    assert.equal(3524459 / 48000 * 48000 > 3524459, true, 'the float trap this guards against');
    const s = core.loopEndSeconds(3524459, 48000);
    assert.ok(s * 48000 <= 3524459);
    assert.ok(3524459 - s * 48000 < 1e-6);
    for (const [n, r] of [[2349639, 32000], [197319, 32000], [295979, 48000], [1, 44100], [44100 * 600 + 7, 44100], [123456789, 96000]]) {
        const x = core.loopEndSeconds(n, r);
        assert.ok(x * r <= n && n - x * r < 1e-6, `${n}/${r}`);
    }
    // exhaustive-ish sweep at common rates
    for (const r of [22050, 32000, 44100, 48000, 96000]) {
        for (let n = 1; n < 5e6; n += 9973) {
            const x = core.loopEndSeconds(n, r);
            if (!(x * r <= n && n - x * r < 1e-6)) assert.fail(`${n}/${r}`);
        }
    }
});
