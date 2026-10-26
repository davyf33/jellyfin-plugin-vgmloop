// Play-through for cut rips: loop N times, then fade. Run: node --test 'tests/js/*.test.mjs'
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const pt = require('../../Jellyfin.Plugin.VgmLoop/Web/player.js');

const RATE = 32000;
const LS = 197319;
const LE = 2349639;
const LEN = LE - LS;
const ON = { loops: 2, fade: 10 };

test('cut rip: less than 2 s after the loop end', () => {
    assert.equal(pt.isCutRip(LE, LE, RATE), true); // ends exactly at loopEnd
    assert.equal(pt.isCutRip(LE + 2 * RATE - 1, LE, RATE), true);
    assert.equal(pt.isCutRip(LE + 2 * RATE, LE, RATE), false); // a real ending or fade
    assert.equal(pt.isCutRip(LE, 0, RATE), false);
});

test('settings come from LoopInfo, clamped; the per-browser switch wins', () => {
    assert.deepEqual(pt.playthroughSettings({ playthroughLoops: 2, playthroughFadeSeconds: 10 }, false), { loops: 2, fade: 10 });
    assert.deepEqual(pt.playthroughSettings({ playthroughLoops: 9, playthroughFadeSeconds: 99 }, false), { loops: 5, fade: 30 });
    assert.deepEqual(pt.playthroughSettings({ playthroughLoops: 1, playthroughFadeSeconds: -3 }, false), { loops: 1, fade: 0 });
    assert.equal(pt.playthroughSettings({ playthroughLoops: 2, playthroughFadeSeconds: 10 }, true), null);
    assert.equal(pt.playthroughSettings({ hasLoop: true }, false), null); // server has it off (fields absent)
    assert.equal(pt.playthroughSettings(null, false), null);
});

test('applies outside Repeat One to cut rips only', () => {
    assert.equal(pt.playthroughApplies('RepeatNone', ON, LE, LS, LE, RATE), true);
    assert.equal(pt.playthroughApplies('RepeatAll', ON, LE, LS, LE, RATE), true);
    assert.equal(pt.playthroughApplies('RepeatOne', ON, LE, LS, LE, RATE), false);
    assert.equal(pt.playthroughApplies('RepeatNone', null, LE, LS, LE, RATE), false);
    assert.equal(pt.playthroughApplies('RepeatNone', ON, LE + 5 * RATE, LS, LE, RATE), false);
});

test('timeline: intro + N loops + fade', () => {
    const t = pt.playthroughTimeline(LS, LE, RATE, ON);
    assert.equal(t.fadeStart, LS + 2 * LEN);
    assert.equal(t.end, LS + 2 * LEN + 10 * RATE);
    assert.equal(pt.playthroughTimeline(LS, LE, RATE, { loops: 1, fade: 0 }).end, LE);
});

test('timeline positions past the first pass map back into the loop', () => {
    assert.equal(pt.timelineToFile(1000, LS, LE), 1000); // intro
    assert.equal(pt.timelineToFile(LE - 1, LS, LE), LE - 1);
    assert.equal(pt.timelineToFile(LE, LS, LE), LS); // seam
    assert.equal(pt.timelineToFile(LE + 500, LS, LE), LS + 500);
    assert.equal(pt.timelineToFile(LS + 2 * LEN + 7, LS, LE), LS + 7); // into the fade
});

test('fade is dB-linear down to -60 dB', () => {
    const t = pt.playthroughTimeline(LS, LE, RATE, ON);
    assert.equal(pt.fadeFactorAt(t.fadeStart - 1, t), 1);
    assert.equal(pt.fadeFactorAt(t.fadeStart, t), 1);
    const mid = pt.fadeFactorAt((t.fadeStart + t.end) / 2, t);
    assert.ok(Math.abs(20 * Math.log10(mid) + 30) < 1e-9, String(mid)); // halfway = -30 dB
    assert.equal(pt.fadeFactorAt(t.end, t), 0);
});

test('stop report maps the timeline onto the file, so only the fade counts as played', () => {
    const t = pt.playthroughTimeline(LS, LE, RATE, ON);
    assert.equal(pt.playthroughReportPosition(0, t, LE), 0);
    assert.equal(pt.playthroughReportPosition(t.end, t, LE), LE);
    assert.ok(pt.playthroughReportPosition(t.end / 2, t, LE) < 0.9 * LE);
    assert.ok(pt.playthroughReportPosition(t.end * 0.95, t, LE) >= 0.9 * LE);
});
