// Next in Repeat One skips ahead; automatic advance still repeats. Run: node --test 'tests/js/*.test.mjs'
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { installNextTrackOverride } = require('../../Jellyfin.Plugin.VgmLoop/Web/player.js');

// Same logic as jellyfin-web 12.1 PlayQueueManager.getNextItemInfo / playbackManager.nextTrack.
class Queue {
    constructor(n, index, mode) { this.list = Array.from({ length: n }, (_, i) => ({ PlaylistItemId: 'p' + i })); this.index = index; this.mode = mode; }
    getPlaylist() { return this.list; }
    getCurrentPlaylistIndex() { return this.index; }
    getRepeatMode() { return this.mode; }
    getNextItemInfo() {
        const len = this.getPlaylist().length;
        let i;
        switch (this.getRepeatMode()) {
            case 'RepeatOne': i = this.getCurrentPlaylistIndex(); break;
            case 'RepeatAll': i = this.getCurrentPlaylistIndex() + 1; if (i >= len) i = 0; break;
            default: i = this.getCurrentPlaylistIndex() + 1;
        }
        return i < 0 || i >= len ? null : { item: this.list[i], index: i };
    }
}

function makePm(queue, player) {
    const pm = {
        _currentPlayer: player,
        _playQueueManager: queue,
        played: [],
        nextTrack(p) {
            const info = this._playQueueManager.getNextItemInfo();
            if (info) this.played.push(info.index);
        }
    };
    return pm;
}

const ours = { id: 'vgmloopplayer' };
const other = { id: 'htmlaudioplayer' };

function setup(n, index, mode, { player = ours, auto = false } = {}) {
    const q = new Queue(n, index, mode);
    const pm = makePm(q, player);
    let autoFlag = auto;
    installNextTrackOverride(pm, (p) => p === ours, () => autoFlag);
    return { q, pm, setAuto: (v) => { autoFlag = v; } };
}

test('Repeat One + user Next -> next track, Repeat One kept', () => {
    const { q, pm } = setup(4, 1, 'RepeatOne');
    pm.nextTrack();
    assert.deepEqual(pm.played, [2]);
    assert.equal(q.getRepeatMode(), 'RepeatOne');
    assert.ok(!Object.prototype.hasOwnProperty.call(q, 'getRepeatMode'), 'temporary override removed');
});

test('Repeat One + user Next on the last track wraps to the first', () => {
    const { pm } = setup(3, 2, 'RepeatOne');
    pm.nextTrack();
    assert.deepEqual(pm.played, [0]);
});

test('Repeat One + automatic advance (track ended) replays the same track', () => {
    const { pm } = setup(4, 1, 'RepeatOne', { auto: true });
    pm.nextTrack();
    assert.deepEqual(pm.played, [1]);
});

test('Repeat None / All unchanged', () => {
    let { pm } = setup(3, 2, 'RepeatNone');
    pm.nextTrack();
    assert.deepEqual(pm.played, [], 'end of queue, nothing next');
    ({ pm } = setup(3, 2, 'RepeatAll'));
    pm.nextTrack();
    assert.deepEqual(pm.played, [0]);
    ({ pm } = setup(3, 0, 'RepeatNone'));
    pm.nextTrack();
    assert.deepEqual(pm.played, [1]);
});

test('other players keep stock Repeat One behaviour', () => {
    const { pm } = setup(4, 1, 'RepeatOne', { player: other });
    pm.nextTrack();
    assert.deepEqual(pm.played, [1]);
    pm.nextTrack(other);
    assert.deepEqual(pm.played, [1, 1]);
});

test('explicit player argument is honoured', () => {
    const { pm } = setup(4, 1, 'RepeatOne', { player: other });
    pm.nextTrack(ours);
    assert.deepEqual(pm.played, [2]);
});

test('override restored even if nextTrack throws', () => {
    const q = new Queue(3, 0, 'RepeatOne');
    const pm = { _currentPlayer: ours, _playQueueManager: q, nextTrack() { this._playQueueManager.getNextItemInfo(); throw new Error('boom'); } };
    installNextTrackOverride(pm, (p) => p === ours, () => false);
    assert.throws(() => pm.nextTrack(), /boom/);
    assert.equal(q.getRepeatMode(), 'RepeatOne');
});

test('installs once', () => {
    const q = new Queue(3, 0, 'RepeatOne');
    const pm = makePm(q, ours);
    assert.equal(installNextTrackOverride(pm, () => true, () => false), true);
    assert.equal(installNextTrackOverride(pm, () => true, () => false), false);
    pm.nextTrack();
    assert.deepEqual(pm.played, [1], 'not double-wrapped');
    assert.equal(installNextTrackOverride(null, () => true, () => false), false);
});
