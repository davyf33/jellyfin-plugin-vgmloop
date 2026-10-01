// Repeat mode survives new music queues. Run: node --test 'tests/js/*.test.mjs'
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { installStickyRepeat, isMusicQueue } = require('../../Jellyfin.Plugin.VgmLoop/Web/player.js');

// Same reset behaviour as jellyfin-web 12.1 PlayQueueManager.
class Queue {
    constructor() { this._playlist = []; this._repeatMode = 'RepeatNone'; }
    getPlaylist() { return this._playlist.slice(0); }
    setPlaylist(items) { this._playlist = items.slice(0); this._repeatMode = 'RepeatNone'; }
    reset() { this._playlist = []; this._repeatMode = 'RepeatNone'; }
    setRepeatMode(v) {
        if (!['RepeatOne', 'RepeatAll', 'RepeatNone'].includes(v)) throw new TypeError('invalid');
        this._repeatMode = v;
    }
    getRepeatMode() { return this._repeatMode; }
}

const album = (n) => Array.from({ length: n }, (_, i) => ({ Id: 't' + i, Type: 'Audio', MediaType: 'Audio' }));
const movie = [{ Id: 'm', Type: 'Movie', MediaType: 'Video' }];

function setup(initial = null) {
    const q = new Queue();
    let saved = initial;
    const restored = [];
    installStickyRepeat(q, { get: () => saved, set: (m) => { saved = m; } }, (m) => restored.push(m));
    return { q, saved: () => saved, restored };
}

test('isMusicQueue', () => {
    assert.equal(isMusicQueue(album(3)), true);
    assert.equal(isMusicQueue([...album(2), ...movie]), false);
    assert.equal(isMusicQueue([{ Type: 'AudioBook' }]), false);
    assert.equal(isMusicQueue([]), false);
    assert.equal(isMusicQueue(null), false);
});

test('Repeat One survives picking another track in the album (new queue)', () => {
    const { q, saved, restored } = setup();
    q.setPlaylist(album(10));
    q.setRepeatMode('RepeatOne');
    assert.equal(saved(), 'RepeatOne');
    q.setPlaylist(album(10)); // user clicks track 5
    assert.equal(q.getRepeatMode(), 'RepeatOne');
    assert.deepEqual(restored, ['RepeatOne']);
});

test('Repeat All survives the queue ending and a new album starting', () => {
    const { q } = setup();
    q.setPlaylist(album(3));
    q.setRepeatMode('RepeatAll');
    q.reset(); // queue finished / stopped
    assert.equal(q.getRepeatMode(), 'RepeatNone');
    q.setPlaylist(album(5));
    assert.equal(q.getRepeatMode(), 'RepeatAll');
});

test('choosing off is remembered too', () => {
    const { q } = setup('RepeatOne');
    q.setPlaylist(album(2));
    assert.equal(q.getRepeatMode(), 'RepeatOne');
    q.setRepeatMode('RepeatNone');
    q.setPlaylist(album(2));
    assert.equal(q.getRepeatMode(), 'RepeatNone');
});

test('persisted mode is restored on the first queue after a reload', () => {
    const { q } = setup('RepeatOne');
    q.setPlaylist(album(4));
    assert.equal(q.getRepeatMode(), 'RepeatOne');
});

test('video queues keep the stock reset and do not change the saved music mode', () => {
    const { q, saved, restored } = setup('RepeatOne');
    q.setPlaylist(movie);
    assert.equal(q.getRepeatMode(), 'RepeatNone');
    q.setRepeatMode('RepeatAll'); // repeat chosen while watching a video
    assert.equal(saved(), 'RepeatOne');
    q.setPlaylist(album(3));
    assert.equal(q.getRepeatMode(), 'RepeatOne');
    assert.deepEqual(restored, ['RepeatOne']);
});

test('nothing saved or junk saved -> stock behaviour', () => {
    let { q } = setup(null);
    q.setPlaylist(album(2));
    assert.equal(q.getRepeatMode(), 'RepeatNone');
    ({ q } = setup('Bogus'));
    q.setPlaylist(album(2));
    assert.equal(q.getRepeatMode(), 'RepeatNone');
});

test('invalid setRepeatMode still throws and is not saved', () => {
    const { q, saved } = setup();
    q.setPlaylist(album(2));
    assert.throws(() => q.setRepeatMode('Nope'), TypeError);
    assert.equal(saved(), null);
});

test('installs once; tolerates missing queue', () => {
    const q = new Queue();
    const store = { get: () => null, set() {} };
    assert.equal(installStickyRepeat(q, store), true);
    assert.equal(installStickyRepeat(q, store), false);
    assert.equal(installStickyRepeat(null, store), false);
});
