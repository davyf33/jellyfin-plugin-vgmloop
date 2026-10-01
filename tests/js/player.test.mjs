// Unit tests for the pure helpers in player.js. Run: node --test tests/js
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const core = require('../../Jellyfin.Plugin.VgmLoop/Web/player.js');

// Shape of jellyfin-web 12.1 browserDeviceProfile output (Chrome, alwaysRemuxFlac on).
function chromeProfile({ remuxFlac = false, remuxMp3 = false } = {}) {
    const direct = [
        { Container: 'webm', Type: 'Video', VideoCodec: 'vp8,vp9,av1', AudioCodec: 'vorbis,opus' },
        { Container: 'opus', Type: 'Audio' },
        { Container: 'webm', AudioCodec: 'opus', Type: 'Audio' },
        { Container: 'ts', AudioCodec: 'mp3', Type: 'Audio' },
        remuxMp3 ? { Container: 'mp4', AudioCodec: 'mp3', Type: 'Audio' } : { Container: 'mp3', Type: 'Audio' },
        { Container: 'aac', Type: 'Audio' },
        { Container: 'm4a', AudioCodec: 'aac', Type: 'Audio' },
        remuxFlac ? { Container: 'mp4', AudioCodec: 'flac', Type: 'Audio' } : { Container: 'flac', Type: 'Audio' },
        { Container: 'ogg', Type: 'Audio' }
    ];
    return {
        MaxStreamingBitrate: 120000000,
        DirectPlayProfiles: direct,
        TranscodingProfiles: [
            { Container: 'mp4', Type: 'Audio', AudioCodec: 'aac', Context: 'Streaming', Protocol: 'hls' },
            { Container: 'aac', Type: 'Audio', AudioCodec: 'aac', Context: 'Streaming', Protocol: 'http' },
            { Container: 'mp3', Type: 'Audio', AudioCodec: 'mp3', Context: 'Streaming', Protocol: 'http' },
            { Container: 'opus', Type: 'Audio', AudioCodec: 'opus', Context: 'Static', Protocol: 'http' },
            { Container: 'mp4', Type: 'Video', AudioCodec: 'aac', VideoCodec: 'h264', Context: 'Streaming', Protocol: 'hls' }
        ],
        CodecProfiles: [{ Type: 'Video' }]
    };
}

test('adaptDeviceProfile drops only audio HLS transcoding', () => {
    const input = chromeProfile();
    const out = core.adaptDeviceProfile(input, () => true);
    assert.deepEqual(out.TranscodingProfiles.map((p) => `${p.Type}/${p.Protocol}/${p.Container}`),
        ['Audio/http/aac', 'Audio/http/mp3', 'Audio/http/opus', 'Video/hls/mp4']);
    assert.equal(input.TranscodingProfiles.length, 5, 'input not mutated');
    assert.equal(out.MaxStreamingBitrate, 120000000);
    assert.deepEqual(out.CodecProfiles, input.CodecProfiles);
});

test('adaptDeviceProfile is case-insensitive on Protocol', () => {
    const out = core.adaptDeviceProfile({ TranscodingProfiles: [{ Type: 'Audio', Protocol: 'HLS' }] }, () => true);
    assert.deepEqual(out.TranscodingProfiles, []);
});

test('adaptDeviceProfile undoes alwaysRemuxFlac', () => {
    const out = core.adaptDeviceProfile(chromeProfile({ remuxFlac: true }), () => true);
    const audio = out.DirectPlayProfiles.filter((p) => p.Type === 'Audio');
    assert.ok(audio.some((p) => p.Container === 'flac' && !p.AudioCodec));
    assert.ok(!audio.some((p) => p.Container === 'mp4' && p.AudioCodec === 'flac'));
});

test('adaptDeviceProfile leaves plain flac profile alone', () => {
    const input = chromeProfile();
    const out = core.adaptDeviceProfile(input, () => true);
    assert.deepEqual(out.DirectPlayProfiles, input.DirectPlayProfiles);
});

test('adaptDeviceProfile restores mp3 direct play after alwaysRemuxMp3 only if playable', () => {
    const yes = core.adaptDeviceProfile(chromeProfile({ remuxMp3: true }), (f) => f === 'mp3');
    assert.ok(yes.DirectPlayProfiles.some((p) => p.Container === 'mp3' && p.Type === 'Audio' && !p.AudioCodec));
    const no = core.adaptDeviceProfile(chromeProfile({ remuxMp3: true }), () => false);
    assert.ok(!no.DirectPlayProfiles.some((p) => p.Container === 'mp3' && !p.AudioCodec));
});

test('adaptDeviceProfile tolerates missing lists', () => {
    assert.equal(core.adaptDeviceProfile(null), null);
    assert.deepEqual(core.adaptDeviceProfile({ Name: 'x' }, () => true), { Name: 'x' });
});

test('normalizationGainDb follows stock precedence', () => {
    const item = { NormalizationGain: -6 };
    const ms = { albumNormalizationGain: -3 };
    assert.equal(core.normalizationGainDb('TrackGain', item, ms), -6);
    assert.equal(core.normalizationGainDb('AlbumGain', item, ms), -3);
    assert.equal(core.normalizationGainDb('TrackGain', {}, ms), -3);
    assert.equal(core.normalizationGainDb('AlbumGain', item, {}), -6);
    assert.equal(core.normalizationGainDb('Off', item, ms), null);
    assert.equal(core.normalizationGainDb('TrackGain', {}, {}), null);
});

test('dbToLinear', () => {
    assert.equal(core.dbToLinear(null), 1);
    assert.equal(core.dbToLinear(0), 1);
    assert.ok(Math.abs(core.dbToLinear(-6) - 0.501187) < 1e-6);
});

test('volume curve round-trips like htmlAudioPlayer', () => {
    for (const v of [0, 1, 2, 25, 50, 73, 99, 100]) {
        assert.equal(core.elementToUiVolume(core.uiToElementVolume(v)), v);
    }
    assert.equal(core.uiToElementVolume(150), 1);
    assert.equal(core.uiToElementVolume(-5), 0);
});

test('mediaErrorType mapping', () => {
    assert.equal(core.mediaErrorType(1), null);
    assert.equal(core.mediaErrorType(2), 'NETWORK_ERROR');
    assert.equal(core.mediaErrorType(3), 'MEDIA_DECODE_ERROR');
    assert.equal(core.mediaErrorType(4), 'MEDIA_NOT_SUPPORTED');
    assert.equal(core.mediaErrorType(0), null);
});

test('detectBrowser', () => {
    const chrome = core.detectBrowser({ userAgent: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/154.0.0.0 Safari/537.36' });
    assert.deepEqual(chrome, { iOS: false, safari: false, tv: false });
    const safari = core.detectBrowser({ userAgent: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.5 Safari/605.1.15', maxTouchPoints: 0 });
    assert.deepEqual(safari, { iOS: false, safari: true, tv: false });
    const ipad = core.detectBrowser({ userAgent: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.6 Safari/605.1.15', maxTouchPoints: 5 });
    assert.equal(ipad.iOS, true);
    const iphoneChrome = core.detectBrowser({ userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 26_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/154.0 Mobile/15E148 Safari/604.1' });
    assert.deepEqual(iphoneChrome, { iOS: true, safari: false, tv: false });
    const firefox = core.detectBrowser({ userAgent: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10.15; rv:154.0) Gecko/20100101 Firefox/154.0' });
    assert.deepEqual(firefox, { iOS: false, safari: false, tv: false });
});

test('isValidDuration', () => {
    assert.equal(core.isValidDuration(12.5), true);
    for (const d of [0, NaN, Infinity, -Infinity, null, undefined]) assert.equal(core.isValidDuration(d), false);
});
