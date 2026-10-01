/* VGM Loop player for jellyfin-web. SPDX-License-Identifier: GPL-3.0-or-later
 *
 * Registered as the window plugin `VgmLoopPlayer` (see pluginManager.loadPlugin). It claims all music
 * audio (item.Type === 'Audio') so the player never changes mid-queue, and plays each track with one of
 * two engines:
 *   - PlainEngine: an <audio> element, behaviour ported from jellyfin-web's htmlAudioPlayer minus hls.js.
 *   - LoopEngine (milestone 4): Web Audio, sample-accurate LOOPSTART/LOOPEND looping.
 *
 * In Node (unit tests) this file exports its pure helpers instead of touching the DOM.
 */
(function (root) {
    'use strict';

    const TAG = '[VgmLoop]';

    // ---------------------------------------------------------------- pure helpers (unit tested)

    /**
     * Adapts jellyfin-web's device profile for a plain <audio> element (window plugins can't load hls.js):
     *  - drops HLS audio transcoding profiles, so transcodes arrive as progressive HTTP;
     *  - undoes "always remux FLAC/MP3" (those remuxes are only playable through HLS) by restoring the plain
     *    direct-play profile when the browser can play the format natively.
     * Returns a new object; the input is not modified.
     */
    function adaptDeviceProfile(profile, canPlayFormat) {
        if (!profile) return profile;
        const out = Object.assign({}, profile);
        const canPlay = typeof canPlayFormat === 'function' ? canPlayFormat : () => false;

        if (Array.isArray(profile.TranscodingProfiles)) {
            out.TranscodingProfiles = profile.TranscodingProfiles.filter(
                (p) => !(p && p.Type === 'Audio' && String(p.Protocol || '').toLowerCase() === 'hls'));
        }

        if (Array.isArray(profile.DirectPlayProfiles)) {
            const direct = profile.DirectPlayProfiles.slice();
            const isAudio = (p) => p && p.Type === 'Audio';
            const hasPlain = (container) => direct.some((p) => isAudio(p) && p.Container === container && !p.AudioCodec);

            const flacRemux = direct.findIndex((p) => isAudio(p) && p.Container === 'mp4' && p.AudioCodec === 'flac');
            if (flacRemux >= 0 && !hasPlain('flac')) {
                direct.splice(flacRemux, 1, { Container: 'flac', Type: 'Audio' });
            }

            if (!hasPlain('mp3') && canPlay('mp3')) {
                direct.push({ Container: 'mp3', Type: 'Audio' });
            }

            out.DirectPlayProfiles = direct;
        }

        return out;
    }

    /** ReplayGain in dB for the user's normalization setting, or null when disabled / unknown. */
    function normalizationGainDb(mode, item, mediaSource) {
        const track = item ? item.NormalizationGain : null;
        const album = mediaSource ? mediaSource.albumNormalizationGain : null;
        if (mode === 'TrackGain') return track ?? album ?? null;
        if (mode === 'AlbumGain') return album ?? track ?? null;
        return null;
    }

    function dbToLinear(db) {
        return db ? Math.pow(10, db / 20) : 1;
    }

    /** Same curve as htmlAudioPlayer: UI volume 0-100 <-> element volume 0-1 (cubic). */
    function uiToElementVolume(val) {
        return Math.pow(Math.max(0, Math.min(100, val)) / 100, 3);
    }

    function elementToUiVolume(v) {
        return Math.min(Math.round(Math.pow(v, 1 / 3) * 100), 100);
    }

    function isValidDuration(d) {
        return !!d && !isNaN(d) && d !== Infinity && d !== -Infinity;
    }

    /** Maps HTMLMediaElement error codes to jellyfin-web MediaError types; null = ignore (as stock). */
    function mediaErrorType(code) {
        switch (code) {
            case 2: return 'NETWORK_ERROR';
            case 3: return 'MEDIA_DECODE_ERROR';
            case 4: return 'MEDIA_NOT_SUPPORTED';
            default: return null; // 1 = aborted (changing media), others = spurious
        }
    }

    function detectBrowser(nav) {
        const ua = (nav && nav.userAgent) || '';
        const iOS = /iP(hone|ad|od)/.test(ua) || (/Macintosh/.test(ua) && (nav.maxTouchPoints || 0) > 1);
        const safari = /Safari/.test(ua) && !/Chrome|Chromium|CriOS|FxiOS|EdgiOS|Edg\/|OPR\/|Android/.test(ua);
        const tv = /SmartTV|SMART-TV|Tizen|Web0S|WebOS|GoogleTV|AFT/i.test(ua);
        return { iOS, safari, tv };
    }

    // ---- loop engine math. Positions are in native-rate samples; loopEnd is exclusive.

    const LOOPABLE_CODECS = ['flac', 'vorbis', 'opus'];

    /** True when the loop engine should handle this track. */
    function isLoopable(info) {
        return !!info && info.hasLoop === true && LOOPABLE_CODECS.includes(info.codec)
            && info.sampleRate > 0 && info.loopEnd > info.loopStart && info.loopStart >= 0;
    }

    /** Loop only in Repeat One, and never once the playhead is past the loop (an outro). */
    function shouldLoop(repeatMode, pos, loopEnd) {
        return repeatMode === 'RepeatOne' && pos < loopEnd;
    }

    /**
     * File position for an anchor { ctxTime0, pos0, looping } at context time `now`.
     * Returns { pos, crossed } where `crossed` says the loop seam has been passed since the anchor.
     * When not looping the position runs linearly and is clamped to `total`.
     */
    function positionAt(anchor, now, rate, loopStart, loopEnd, total) {
        const elapsed = Math.max(0, now - anchor.ctxTime0);
        const p = anchor.pos0 + elapsed * rate;
        if (anchor.looping && p >= loopEnd) {
            return { pos: loopStart + ((p - loopEnd) % (loopEnd - loopStart)), crossed: true };
        }
        return { pos: total != null ? Math.min(p, total) : p, crossed: false };
    }

    /**
     * Repeat-mode change mid-track. Returns the new anchor, or null when nothing changes.
     * One -> other while looping: stop looping from the wrapped position (the source then runs to the end).
     * other -> One: start looping unless already past loopEnd.
     */
    function repeatTransition(anchor, now, rate, loopStart, loopEnd, total, repeatMode) {
        const { pos, crossed } = positionAt(anchor, now, rate, loopStart, loopEnd, total);
        const looping = shouldLoop(repeatMode, pos, loopEnd);
        if (looping === anchor.looping) return null;
        return { anchor: { ctxTime0: now, pos0: pos, looping }, crossed };
    }

    /**
     * Position reported to Jellyfin when a loop track stops (drives "played" status). A track that has
     * crossed the seam at least once reports the end of its first pass. Change the policy here.
     */
    function stopReportPosition(pos, crossedSeam, loopEnd) {
        return crossedSeam ? loopEnd : pos;
    }

    function prevDouble(x) {
        const f = new Float64Array([x]);
        const i = new BigInt64Array(f.buffer);
        i[0] += x > 0 ? -1n : 1n;
        return f[0];
    }

    /**
     * Sample index -> seconds for AudioBufferSourceNode.loopEnd such that seconds * rate never exceeds the
     * index. Plain n / rate can round up (3524459 / 48000 * 48000 = 3524459.0000000005); Chrome then
     * treats the loop end as past the buffer and does not wrap at the seam.
     */
    function loopEndSeconds(n, rate) {
        let s = n / rate;
        while (s > 0 && s * rate > n) s = prevDouble(s);
        return s;
    }

    /** Milliseconds -> integer sample index clamped into [0, total). */
    function msToSamples(ms, rate, total) {
        const s = Math.round((ms / 1000) * rate);
        return Math.max(0, Math.min(total > 0 ? total - 1 : 0, s));
    }

    /**
     * In Repeat One, jellyfin-web's Next replays the current track (the queue's "next item" is the current one).
     * Wraps playbackManager.nextTrack so a user-initiated Next moves on (wrapping at the end of the queue, like
     * Repeat All) while Repeat One stays selected. Automatic advances after a track ends (isAutoAdvance() true)
     * keep the stock behaviour, so a non-loop track still repeats. Returns false if already installed.
     */
    function installNextTrackOverride(playbackManager, isOurPlayer, isAutoAdvance) {
        if (!playbackManager || typeof playbackManager.nextTrack !== 'function' || playbackManager.__vgmLoopNextTrack) return false;
        const original = playbackManager.nextTrack;
        playbackManager.nextTrack = function (player) {
            const target = player || playbackManager._currentPlayer;
            const queue = playbackManager._playQueueManager;
            if (target && isOurPlayer(target) && !isAutoAdvance() && queue && typeof queue.getRepeatMode === 'function'
                && queue.getRepeatMode() === 'RepeatOne') {
                const ownRepeatMode = Object.prototype.hasOwnProperty.call(queue, 'getRepeatMode') ? queue.getRepeatMode : null;
                queue.getRepeatMode = () => 'RepeatAll'; // only for the synchronous next-item lookup
                try {
                    return original.apply(this, arguments);
                } finally {
                    if (ownRepeatMode) queue.getRepeatMode = ownRepeatMode;
                    else delete queue.getRepeatMode;
                }
            }
            return original.apply(this, arguments);
        };
        playbackManager.__vgmLoopNextTrack = true;
        return true;
    }

    /**
     * The queue item a user's Next (or the automatic advance in Repeat None / All) leads to: the following item,
     * wrapping to the first unless repeat is off. Null when there is none or it is the current item again.
     */
    function nextQueueItem(playlist, index, repeatMode) {
        if (!Array.isArray(playlist) || !(index >= 0) || index >= playlist.length) return null;
        let i = index + 1;
        if (i >= playlist.length) {
            if (repeatMode === 'RepeatNone' || !repeatMode) return null;
            i = 0;
        }
        const item = playlist[i];
        return item && i !== index ? item : null;
    }

    function isMusicQueue(items) {
        return Array.isArray(items) && items.length > 0 && items.every((i) => i && i.Type === 'Audio');
    }

    /**
     * jellyfin-web resets the repeat mode to RepeatNone whenever a new queue starts (PlayQueueManager.setPlaylist,
     * e.g. clicking another track in an album) or the queue ends (reset). Remember the mode last chosen for music
     * and re-apply it when a new music queue starts. Non-music queues (videos etc.) keep the stock reset.
     * `store` is { get(): string|null, set(mode) } for persistence; `onRestore(mode)` runs after a restore, because
     * jellyfin-web calls setPlaylist only after the first track has started. Returns false if already installed.
     */
    function installStickyRepeat(queue, store, onRestore) {
        if (!queue || typeof queue.setPlaylist !== 'function' || typeof queue.setRepeatMode !== 'function' || queue.__vgmLoopSticky) return false;
        const MODES = ['RepeatNone', 'RepeatAll', 'RepeatOne'];
        const setRepeatMode = queue.setRepeatMode;
        const setPlaylist = queue.setPlaylist;

        queue.setRepeatMode = function (value) {
            const result = setRepeatMode.apply(this, arguments);
            if (MODES.includes(value) && isMusicQueue(this.getPlaylist())) store.set(value);
            return result;
        };

        queue.setPlaylist = function (items) {
            const result = setPlaylist.apply(this, arguments);
            const saved = store.get();
            if (MODES.includes(saved) && isMusicQueue(items)) {
                setRepeatMode.call(this, saved);
                if (onRestore) onRestore(saved);
            }
            return result;
        };

        queue.__vgmLoopSticky = true;
        return true;
    }

    const core = {
        adaptDeviceProfile,
        normalizationGainDb,
        dbToLinear,
        uiToElementVolume,
        elementToUiVolume,
        isValidDuration,
        mediaErrorType,
        detectBrowser,
        isLoopable,
        shouldLoop,
        positionAt,
        repeatTransition,
        stopReportPosition,
        msToSamples,
        loopEndSeconds,
        installNextTrackOverride,
        installStickyRepeat,
        isMusicQueue,
        nextQueueItem
    };

    if (typeof module === 'object' && module.exports) {
        module.exports = core;
        return;
    }

    // ---------------------------------------------------------------- browser runtime

    const browser = detectBrowser(root.navigator);
    let corsCredentialsPromise;

    function getIncludeCorsCredentials() {
        if (!corsCredentialsPromise) {
            corsCredentialsPromise = fetch('config.json', { cache: 'no-cache' })
                .then((r) => r.json())
                .then((c) => !!c.includeCorsCredentials)
                .catch(() => false);
        }
        return corsCredentialsPromise;
    }

    function setAudioSessionPlayback() {
        // Keeps Safari/iOS audio alive in the background and ignores the ringer switch.
        if ('audioSession' in navigator) {
            try {
                navigator.audioSession.type = 'playback';
            } catch (e) {
                console.debug(TAG, 'audioSession.type not settable', e);
            }
        }
    }

    function currentUserId(deps) {
        try {
            const apiClient = deps.ServerConnections.currentApiClient();
            return apiClient ? apiClient.getCurrentUserId() : null;
        } catch {
            return null;
        }
    }

    function canPlayFormat(format) {
        const types = { mp3: 'audio/mpeg', flac: 'audio/flac', aac: 'audio/aac', opus: 'audio/ogg; codecs="opus"' };
        const a = document.createElement('audio');
        return !!(types[format] && a.canPlayType(types[format]).replace(/no/, ''));
    }

    /**
     * Plain <audio> playback. Port of htmlAudioPlayer (jellyfin-web v12.1) without hls.js.
     * All events are raised on the owning player.
     */
    class PlainEngine {
        constructor(player) {
            this.player = player;
            this.deps = player._deps;
            this.elem = null;
            this.gainNode = null;
            this.normalizationGain = 1;
            this._started = false;
            this._currentTime = null;
            this._currentSrc = null;
            this._currentPlayOptions = null;
            this._isFadingOut = false;
            this._fadeTimeout = null;

            const self = this;
            this._on = {
                timeupdate() {
                    if (!self._isFadingOut) {
                        self._currentTime = this.currentTime;
                        self.player._trigger('timeupdate');
                    }
                },
                ended() {
                    self.player._markAutoAdvance();
                    self._endedInternal();
                },
                volumechange() {
                    if (!self._isFadingOut) {
                        if (this.volume) self.deps.appSettings.set('volume', this.volume);
                        if (browser.safari && self.gainNode) self.gainNode.gain.value = this.volume * self.normalizationGain;
                        self.player._trigger('volumechange');
                    }
                },
                pause() {
                    self.player._trigger('pause');
                },
                play() {
                    self.player._trigger('unpause');
                },
                playing(e) {
                    if (!self._started) {
                        self._started = true;
                        this.removeAttribute('controls');
                        self._seekOnPlaybackStart(e.target, self._currentPlayOptions && self._currentPlayOptions.playerStartPositionTicks);
                    }
                    self.player._trigger('playing');
                },
                waiting() {
                    self.player._trigger('waiting');
                },
                error() {
                    const code = this.error ? (this.error.code || 0) : 0;
                    console.error(TAG, 'media element error: ' + code + ' ' + (this.error ? this.error.message || '' : ''));
                    const type = mediaErrorType(code);
                    if (type) self.player._trigger('error', [{ type }]);
                }
            };
        }

        _createElement() {
            if (this.elem) return this.elem;
            // Our own element: the stock player's `.mediaPlayerAudio` may already have a MediaElementSource.
            const elem = document.createElement('audio');
            elem.classList.add('vgmLoopAudio', 'hide');
            elem.setAttribute('playsinline', '');
            elem.setAttribute('x-webkit-airplay', 'allow');
            document.body.appendChild(elem);
            if (!this.deps.appHost.supports('physicalvolumecontrol')) {
                elem.volume = this.deps.appSettings.get('volume') || 1;
            }
            this.elem = elem;
            return elem;
        }

        _bind(elem) {
            for (const name of ['timeupdate', 'ended', 'volumechange', 'pause', 'playing', 'play', 'waiting']) {
                elem.addEventListener(name, this._on[name]);
            }
        }

        _unbind(elem) {
            if (!elem) return;
            for (const name of ['timeupdate', 'ended', 'volumechange', 'pause', 'playing', 'play', 'waiting', 'error']) {
                elem.removeEventListener(name, this._on[name]);
            }
        }

        _applyNormalization(elem, options) {
            if (browser.iOS) return; // createMediaElementSource breaks playbackRate and pitch on iOS WebKit
            const mode = this.deps.appSettings.get('selectAudioNormalization', currentUserId(this.deps)) || 'TrackGain';
            if (mode !== 'TrackGain' && mode !== 'AlbumGain') {
                console.debug(TAG, 'normalization disabled');
                if (this.gainNode) {
                    // Unlike stock, don't leave the previous track's gain applied.
                    this.normalizationGain = 1;
                    this.gainNode.gain.value = browser.safari ? elem.volume : 1;
                }
                return;
            }

            if (!this.gainNode) {
                try {
                    const Ctx = window.AudioContext || window.webkitAudioContext;
                    const ctx = new Ctx();
                    const source = ctx.createMediaElementSource(elem);
                    const gain = ctx.createGain();
                    source.connect(gain);
                    gain.connect(ctx.destination);
                    this.gainNode = gain;
                } catch (e) {
                    console.error(TAG, 'Web Audio API is not supported in this browser', e);
                    return;
                }
            }

            this.normalizationGain = dbToLinear(normalizationGainDb(mode, options.item, options.mediaSource));
            this.gainNode.gain.value = this.normalizationGain * (browser.safari ? elem.volume : 1);
            if (this.gainNode.context.state === 'suspended') {
                this.gainNode.context.resume().catch(() => {});
            }
            console.debug(TAG, 'gain: ' + this.normalizationGain);
        }

        async play(options) {
            this._started = false;
            this._currentTime = null;

            const elem = this._createElement();
            this._unbind(elem);
            // Volume/mute may have changed while the loop engine was playing.
            elem.volume = this.player._savedVolume();
            elem.muted = this.player._muted;
            this._bind(elem);

            let url = options.url;
            console.debug(TAG, 'plain engine playing url: ' + url);
            this._applyNormalization(elem, options);

            const seconds = (options.playerStartPositionTicks || 0) / 10000000;
            if (seconds) url += '#t=' + seconds;

            this._currentPlayOptions = options;
            elem.crossOrigin = options.mediaSource && options.mediaSource.IsRemote ? null : 'anonymous';
            setAudioSessionPlayback();

            elem.autoplay = true;
            if (await getIncludeCorsCredentials()) elem.crossOrigin = 'use-credentials'; // Safari won't send cookies otherwise

            elem.src = url;
            this._currentSrc = url;
            return this._playWithPromise(elem);
        }

        _playWithPromise(elem) {
            try {
                return elem.play()
                    .catch((e) => {
                        const name = (e.name || '').toLowerCase();
                        // Autoplay blocked / aborted: the user can still press play.
                        if (name === 'notallowederror' || name === 'aborterror') return;
                        throw e;
                    })
                    .then(() => {
                        elem.addEventListener('error', this._on.error);
                    });
            } catch (err) {
                console.error(TAG, 'error calling audio.play: ' + err);
                return Promise.reject(err);
            }
        }

        _seekOnPlaybackStart(element, ticks) {
            const seconds = (ticks || 0) / 10000000;
            if (!seconds) return;
            const setIfNeeded = () => {
                if (Math.abs((element.currentTime || 0) - seconds) >= 1) element.currentTime = seconds;
            };
            if (element.duration >= seconds) {
                setIfNeeded();
                return;
            }
            const events = ['durationchange', 'loadeddata', 'play', 'loadedmetadata'];
            const onChange = () => {
                if (element.currentTime === 0 && element.duration >= seconds) {
                    setIfNeeded();
                    events.forEach((n) => element.removeEventListener(n, onChange));
                }
            };
            events.forEach((n) => element.addEventListener(n, onChange));
        }

        _resetSrc(elem) {
            elem.src = '';
            elem.innerHTML = '';
            elem.removeAttribute('src');
        }

        /** Port of htmlMediaHelper.onEndedInternal. */
        _endedInternal() {
            const elem = this.elem;
            if (elem) {
                elem.removeEventListener('error', this._on.error);
                this._resetSrc(elem);
            }
            const stopInfo = { src: this._currentSrc };
            this.player._trigger('stopped', [stopInfo]);
            this._currentTime = null;
            this._currentSrc = null;
            this._currentPlayOptions = null;
        }

        _cancelFade() {
            if (this._fadeTimeout) {
                clearTimeout(this._fadeTimeout);
                this._fadeTimeout = null;
            }
        }

        _fade(elem, startingVolume) {
            this._isFadingOut = true;
            // Track the volume ourselves: iOS Safari ignores volume changes and always reports the system volume.
            const v = Math.max(0, startingVolume - 0.15);
            elem.volume = v;
            if (v <= 0) {
                this._isFadingOut = false;
                return Promise.resolve();
            }
            return new Promise((resolve, reject) => {
                this._cancelFade();
                this._fadeTimeout = setTimeout(() => this._fade(elem, v).then(resolve, reject), 100);
            });
        }

        stop(destroyPlayer) {
            this._cancelFade();
            const elem = this.elem;
            if (!elem || !this._currentSrc) return Promise.resolve();

            if (!destroyPlayer || browser.tv) {
                elem.pause();
                this._endedInternal();
                if (destroyPlayer) this.destroy();
                return Promise.resolve();
            }

            const originalVolume = elem.volume;
            return this._fade(elem, elem.volume).then(() => {
                elem.pause();
                elem.volume = originalVolume;
                this._endedInternal();
                this.destroy();
            });
        }

        destroy() {
            if (!this.elem) return;
            this._unbind(this.elem);
            this._resetSrc(this.elem);
        }

        currentSrc() { return this._currentSrc; }

        currentTime(val) {
            const elem = this.elem;
            if (!elem) return undefined;
            if (val != null) {
                elem.currentTime = val / 1000;
                return undefined;
            }
            if (this._currentTime) return this._currentTime * 1000;
            return (elem.currentTime || 0) * 1000;
        }

        duration() {
            const d = this.elem ? this.elem.duration : null;
            return isValidDuration(d) ? d * 1000 : null;
        }

        seekable() {
            const elem = this.elem;
            if (!elem) return undefined;
            const s = elem.seekable;
            if (s && s.length) {
                const start = isValidDuration(s.start(0)) ? s.start(0) : 0;
                const end = isValidDuration(s.end(0)) ? s.end(0) : 0;
                return end - start > 0;
            }
            return false;
        }

        getBufferedRanges() {
            const elem = this.elem;
            if (!elem) return [];
            const ranges = [];
            const b = elem.buffered || [];
            const offset = (this._currentPlayOptions && this._currentPlayOptions.transcodingOffsetTicks) || 0;
            for (let i = 0; i < b.length; i++) {
                const start = isValidDuration(b.start(i)) ? b.start(i) : 0;
                const end = b.end(i);
                if (!isValidDuration(end)) continue;
                ranges.push({ start: start * 10000000 + offset, end: end * 10000000 + offset });
            }
            return ranges;
        }

        pause() { if (this.elem) this.elem.pause(); }
        unpause() { if (this.elem) this.elem.play(); }
        paused() { return this.elem ? this.elem.paused : false; }
        setPlaybackRate(v) { if (this.elem) this.elem.playbackRate = v; }
        getPlaybackRate() { return this.elem ? this.elem.playbackRate : null; }
        setVolume(v) { if (this.elem) this.elem.volume = uiToElementVolume(v); }
        getVolume() { return this.elem ? elementToUiVolume(this.elem.volume) : undefined; }
        setMute(m) { if (this.elem) this.elem.muted = m; }
        isMuted() { return this.elem ? this.elem.muted : false; }

        isAirPlayEnabled() {
            return document.AirPlayEnabled ? !!document.AirplayElement : false;
        }

        setAirPlayEnabled(enabled) {
            const elem = this.elem;
            if (!elem) return;
            if (document.AirPlayEnabled) {
                if (enabled) {
                    elem.requestAirPlay().catch((e) => console.error(TAG, 'Error requesting AirPlay', e));
                } else {
                    document.exitAirPLay().catch((e) => console.error(TAG, 'Error exiting AirPlay', e));
                }
            } else if (elem.webkitShowPlaybackTargetPicker) {
                elem.webkitShowPlaybackTargetPicker();
            }
        }
    }

    const AudioContextClass = root.AudioContext || root.webkitAudioContext;
    const OfflineAudioContextClass = root.OfflineAudioContext || root.webkitOfflineAudioContext;
    const PREFETCH_DELAY_MS = 4000; // let the current track settle first
    const PREFETCH_DECODE_MAX_MB = 150; // decode ahead only below this (desktop); iOS preloads bytes only
    const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

    function withTimeout(promise, ms, what) {
        let t;
        return Promise.race([
            promise,
            new Promise((_, reject) => {
                t = setTimeout(() => reject(new Error(what + ' timed out after ' + ms + ' ms')), ms);
            })
        ]).finally(() => clearTimeout(t));
    }

    // ---- per-browser switches (localStorage), settable from a URL because phones have no console:
    //   /web/?vgmloop=off | on          disable / re-enable the player in this browser
    //   /web/?vgmloop=stream | direct   loop-engine output via <audio srcObject=MediaStream> | AudioContext.destination
    //   /web/?vgmloop=prefetch-off | prefetch-on   preloading of the next loop track
    const SETTINGS = { disabled: 'vgmloop.disabled', output: 'vgmloop.output', repeat: 'vgmloop.repeatMode', prefetch: 'vgmloop.prefetch' };

    function readSetting(key) {
        try {
            return root.localStorage.getItem(key);
        } catch {
            return null;
        }
    }

    function writeSetting(key, value) {
        try {
            if (value == null) root.localStorage.removeItem(key);
            else root.localStorage.setItem(key, value);
        } catch {
            // storage blocked; the switch just doesn't persist
        }
    }

    let pendingNotice = null;
    (function applyUrlSwitch() {
        let v;
        try {
            v = new URLSearchParams(root.location.search).get('vgmloop');
        } catch {
            return;
        }
        if (v === 'off') writeSetting(SETTINGS.disabled, '1');
        else if (v === 'on') writeSetting(SETTINGS.disabled, null);
        else if (v === 'stream' || v === 'direct') writeSetting(SETTINGS.output, v);
        else if (v === 'prefetch-off') writeSetting(SETTINGS.prefetch, 'off');
        else if (v === 'prefetch-on') writeSetting(SETTINGS.prefetch, null);
        else return;
        pendingNotice = 'VGM Loop: ' + (v === 'off' ? 'disabled in this browser' : v === 'on' ? 'enabled'
            : v.startsWith('prefetch') ? 'preloading ' + v.slice(9) : 'loop output = ' + v);
        console.info(TAG, pendingNotice);
    })();

    function outputMode() {
        return readSetting(SETTINGS.output) === 'stream' ? 'stream' : 'direct';
    }

    /**
     * Fetches the original file bytes (independent of playMethod: the user may remux or transcode FLAC) and drops
     * anything before the audio stream (Firefox can't decode FLAC with an ID3v2 prefix).
     */
    async function fetchOriginal(apiClient, itemId, mediaSourceId, audioDataOffset, signal) {
        const params = { static: true, ApiKey: apiClient.accessToken() };
        if (mediaSourceId) params.mediaSourceId = mediaSourceId;
        const resp = await fetch(apiClient.getUrl('Audio/' + itemId + '/stream', params), { signal });
        if (!resp.ok) throw new Error('HTTP ' + resp.status + ' fetching the original file');
        const bytes = await resp.arrayBuffer();
        return audioDataOffset > 0 ? bytes.slice(audioDataOffset) : bytes;
    }

    /**
     * One AudioContext at the current loop track's native sample rate. Reused while consecutive loop
     * tracks share a rate; replaced (old one closed) when the rate changes. Safari limits live contexts.
     */
    const contexts = {
        ctx: null,
        get(rate) {
            if (this.ctx && this.ctx.state !== 'closed' && this.ctx.sampleRate === rate) return this.ctx;
            this.close();
            setAudioSessionPlayback();
            const ctx = new AudioContextClass({ sampleRate: rate, latencyHint: 'playback' });
            if (ctx.sampleRate !== rate) {
                ctx.close().catch(() => {});
                throw new Error('AudioContext sampleRate ' + ctx.sampleRate + ' != ' + rate);
            }
            this.ctx = ctx;
            this.mode = outputMode();
            if (this.mode === 'stream' && ctx.createMediaStreamDestination) {
                // Route through a media element: gives the page a real "playing media" for lock-screen /
                // Now Playing controls and background playback where Web Audio alone doesn't.
                const dest = ctx.createMediaStreamDestination();
                this.streamElement().srcObject = dest.stream;
                this.output = dest;
            } else {
                this.mode = 'direct';
                this.output = ctx.destination;
            }
            console.debug(TAG, 'AudioContext created at ' + rate + ' Hz, output ' + this.mode);
            return ctx;
        },
        mode: 'direct',
        output: null,
        _el: null,
        streamElement() {
            // One persistent element: iOS unlocks playback per element, so reusing it lets later tracks
            // start without a new user gesture.
            if (!this._el) {
                const el = document.createElement('audio');
                el.classList.add('vgmLoopStreamAudio', 'hide');
                el.setAttribute('playsinline', '');
                document.body.appendChild(el);
                this._el = el;
            }
            return this._el;
        },
        startOutput() {
            if (this.mode !== 'stream' || !this._el) return Promise.resolve();
            return this._el.play();
        },
        pauseOutput() {
            if (this.mode === 'stream' && this._el) this._el.pause();
        },
        close() {
            const c = this.ctx;
            this.ctx = null;
            this.output = null;
            if (this._el) {
                this._el.pause();
                this._el.srcObject = null;
            }
            if (c && c.state !== 'closed') c.close().catch(() => {});
        }
    };

    /**
     * Sample-accurate looping with Web Audio. The original file is fetched and decoded at its
     * native rate; AudioBufferSourceNode.loopStart/loopEnd = n / rate is then exact.
     * Graph: AudioBufferSourceNode -> GainNode (volume x ReplayGain) -> destination
     * (or -> MediaStreamAudioDestinationNode -> hidden <audio> in 'stream' output mode).
     */
    class LoopEngine {
        constructor(player) {
            this.player = player;
            this.deps = player._deps;
            this._gen = 0;
            this._timer = null;
            this._clear();
        }

        _clear() {
            this.info = null;
            this.buffer = null;
            this.source = null;
            this.gain = null;
            this.anchor = null;
            this.crossed = false;
            this._paused = false;
            this._src = null;
            this._reportPos = null;
            this._abort = null;
            this._volume = 1;
            this._norm = 1;
        }

        get _ctx() { return contexts.ctx; }
        get _rate() { return this.info.sampleRate; }
        get _total() { return this.buffer ? this.buffer.length : this.info.totalSamples; }

        _repeatMode() {
            try {
                return this.deps.playbackManager.getRepeatMode();
            } catch {
                return 'RepeatNone';
            }
        }

        /**
         * Resolves true once audio is playing, 'aborted' if stop()/another play() superseded it.
         * Throws on any failure so the player can fall back to the plain engine.
         */
        /** `prefetched` (optional) is a promise of { bytes } or { buffer } prepared by the player's preloader. */
        async play(options, info, prefetched) {
            const gen = ++this._gen;
            this._teardown();
            this._clear();
            this.info = info;
            this._src = options.url;

            const item = options.item;
            const apiClient = this.deps.ServerConnections.getApiClient(item.ServerId);

            this.deps.loading.show();
            const t0 = performance.now();
            try {
                let buffer = null;
                let bytes = null;
                let from = 'network';
                if (prefetched) {
                    const got = await prefetched.catch(() => null);
                    if (gen !== this._gen) return 'aborted';
                    if (got && got.buffer && got.buffer.sampleRate === info.sampleRate) {
                        buffer = got.buffer;
                        from = 'preloaded+decoded';
                    } else if (got && got.bytes) {
                        bytes = got.bytes;
                        from = 'preloaded';
                    }
                }

                if (!buffer && !bytes) {
                    const abort = new AbortController();
                    this._abort = abort;
                    try {
                        bytes = await fetchOriginal(apiClient, item.Id, options.mediaSource.Id, info.audioDataOffset, abort.signal);
                    } catch (e) {
                        if (gen !== this._gen) return 'aborted';
                        throw e;
                    }
                    if (gen !== this._gen) return 'aborted';
                }
                const t1 = performance.now();

                const ctx = contexts.get(info.sampleRate);
                if (!buffer) buffer = await ctx.decodeAudioData(bytes);
                if (gen !== this._gen) return 'aborted';

                if (buffer.sampleRate !== info.sampleRate) {
                    throw new Error('decoded at ' + buffer.sampleRate + ' Hz, expected ' + info.sampleRate);
                }
                if (buffer.length !== info.totalSamples) {
                    console.warn(TAG, 'decoded length ' + buffer.length + ' != header total ' + info.totalSamples);
                }
                if (info.loopEnd > buffer.length) {
                    throw new Error('loopEnd ' + info.loopEnd + ' is past the decoded length ' + buffer.length);
                }
                const mb = (buffer.length * buffer.numberOfChannels * 4) / 1048576;
                if (mb > 300) console.warn(TAG, 'large decoded buffer: ' + mb.toFixed(0) + ' MB');

                this.buffer = buffer;
                this.gain = ctx.createGain();
                this.gain.connect(contexts.output);
                this._volume = this.player._savedVolume();
                this._norm = this._normalization(options);
                this._applyGain();

                const startMs = (options.playerStartPositionTicks || 0) / 10000;
                this._startSource(startMs ? msToSamples(startMs, info.sampleRate, buffer.length) : 0);

                if (ctx.state !== 'running') {
                    await withTimeout(ctx.resume(), 2000, 'AudioContext.resume');
                }
                await withTimeout(contexts.startOutput(), 2000, 'stream output play()');
                if (gen !== this._gen) return 'aborted';
                if (ctx.state !== 'running') throw new Error('AudioContext is ' + ctx.state + ' (autoplay policy?)');

                console.info(TAG, `loop engine (${contexts.mode}): ${info.codec} ${info.sampleRate} Hz, loop ${info.loopStart}..${info.loopEnd}` +
                    ` (${info.convention || ''}), ${from}: fetch ${Math.round(t1 - t0)} ms, decode ${Math.round(performance.now() - t1)} ms`);
                this._startTimer();
                this.player._trigger('playing');
                return true;
            } catch (e) {
                if (gen !== this._gen) return 'aborted';
                this._teardown();
                this._clear();
                throw e;
            } finally {
                if (gen === this._gen) this._abort = null;
                this.deps.loading.hide();
            }
        }

        _normalization(options) {
            if (browser.iOS) return 1; // parity with the plain engine / stock player on iOS
            const mode = this.deps.appSettings.get('selectAudioNormalization', currentUserId(this.deps)) || 'TrackGain';
            return dbToLinear(normalizationGainDb(mode, options.item, options.mediaSource));
        }

        _applyGain() {
            if (!this.gain) return;
            const g = this.player._muted ? 0 : this._volume * this._norm;
            const p = this.gain.gain;
            p.cancelScheduledValues(0);
            p.value = g;
        }

        _startSource(pos) {
            const ctx = this._ctx;
            const info = this.info;
            const src = ctx.createBufferSource();
            src.buffer = this.buffer;
            src.loopStart = info.loopStart / info.sampleRate;
            src.loopEnd = loopEndSeconds(info.loopEnd, info.sampleRate);
            const looping = shouldLoop(this._repeatMode(), pos, info.loopEnd);
            src.loop = looping;
            src.connect(this.gain);
            src.onended = () => {
                if (src !== this.source) return;
                this.player._markAutoAdvance();
                this._finish(this._total);
            };
            src.start(0, pos / info.sampleRate);
            this.source = src;
            this.anchor = { ctxTime0: ctx.currentTime, pos0: pos, looping };
        }

        _stopSource() {
            const s = this.source;
            this.source = null;
            if (!s) return;
            s.onended = null;
            try {
                s.stop();
            } catch {
                // never started
            }
            s.disconnect();
        }

        _pos() {
            if (!this.anchor || !this._ctx) return 0;
            const info = this.info;
            const r = positionAt(this.anchor, this._ctx.currentTime, info.sampleRate, info.loopStart, info.loopEnd, this._total);
            if (r.crossed) this.crossed = true;
            return r.pos;
        }

        _startTimer() {
            this._stopTimer();
            this._timer = setInterval(() => {
                this._pos();
                this.player._trigger('timeupdate');
            }, 250);
        }

        _stopTimer() {
            if (this._timer) {
                clearInterval(this._timer);
                this._timer = null;
            }
        }

        /** Stops audio and releases the buffer; the AudioContext stays for the next loop track. */
        _teardown() {
            this._stopTimer();
            this._stopSource();
            if (this.gain) this.gain.disconnect();
            this.gain = null;
            this.buffer = null;
        }

        /** Like htmlMediaHelper.onEndedInternal: fire 'stopped' while currentTime() still answers, then clear. */
        _finish(reportPos) {
            this._stopTimer();
            this._stopSource();
            contexts.pauseOutput(); // don't leave a silent stream "playing" if the next track is plain
            this._reportPos = reportPos;
            const src = this._src;
            this.player._trigger('stopped', [{ src }]);
            this._teardown();
            this._clear();
        }

        onRepeatModeChange() {
            if (!this.source || !this.anchor || !this._ctx) return;
            const info = this.info;
            const t = repeatTransition(this.anchor, this._ctx.currentTime, info.sampleRate, info.loopStart, info.loopEnd, this._total, this._repeatMode());
            if (!t) return;
            if (t.crossed) this.crossed = true;
            this.source.loop = t.anchor.looping;
            this.anchor = t.anchor;
            console.debug(TAG, 'looping ' + (t.anchor.looping ? 'on' : 'off') + ' at sample ' + Math.round(t.anchor.pos0));
        }

        async stop(destroyPlayer) {
            if (this._abort) this._abort.abort();
            const loading = !this.source;
            this._gen++;
            if (loading) {
                // Nothing audible yet (still fetching/decoding) or already finished; the pending play() sees
                // the new generation and returns 'aborted'.
                this._teardown();
                this._clear();
                if (destroyPlayer) this.destroy();
                return;
            }

            const report = stopReportPosition(this._pos(), this.crossed, this.info.loopEnd);
            const ctx = this._ctx;
            if (this.gain && ctx && ctx.state === 'running') {
                // Short ramp avoids a click; a longer fade when playback is being torn down (stock fades too).
                const fade = destroyPlayer ? 0.3 : 0.015;
                const p = this.gain.gain;
                p.cancelScheduledValues(ctx.currentTime);
                p.setValueAtTime(p.value, ctx.currentTime);
                p.linearRampToValueAtTime(0, ctx.currentTime + fade);
                await sleep(fade * 1000 + 10);
            }
            this._finish(report);
            if (destroyPlayer) this.destroy();
        }

        destroy() {
            this._gen++;
            if (this._abort) this._abort.abort();
            this._teardown();
            this._clear();
            contexts.close();
        }

        currentSrc() { return this._src; }

        currentTime(val) {
            if (val != null) {
                this._seek(val);
                return undefined;
            }
            if (!this.info) return 0;
            const pos = this._reportPos != null ? this._reportPos : this._pos();
            return (pos / this._rate) * 1000;
        }

        _seek(ms) {
            if (!this.buffer || !this.source) return;
            this._pos(); // record a seam crossing before re-anchoring
            this._stopSource();
            this._startSource(msToSamples(ms, this._rate, this._total));
            this.player._trigger('timeupdate');
        }

        duration() { return this.info ? (this._total / this._rate) * 1000 : null; }
        seekable() { return !!this.buffer; }
        getBufferedRanges() { return this.buffer ? [{ start: 0, end: (this._total / this._rate) * 10000000 }] : []; }

        pause() {
            const ctx = this._ctx;
            if (!ctx || !this.source || this._paused) return;
            this._paused = true;
            this._stopTimer();
            contexts.pauseOutput();
            ctx.suspend().then(() => this.player._trigger('pause'));
        }

        unpause() {
            const ctx = this._ctx;
            if (!ctx || !this.source || !this._paused) return;
            contexts.startOutput().catch((e) => console.error(TAG, 'stream output play() failed', e));
            ctx.resume().then(() => {
                this._paused = false;
                this._startTimer();
                this.player._trigger('unpause');
                this.player._trigger('playing');
            }, (e) => console.error(TAG, 'resume failed', e));
        }

        paused() { return this._paused; }
        setPlaybackRate() { /* Web Audio would change pitch; loop tracks stay at 1.0 */ }
        getPlaybackRate() { return 1; }

        setVolume(val) {
            this._volume = uiToElementVolume(val);
            if (this._volume) this.deps.appSettings.set('volume', this._volume);
            this._applyGain();
            this.player._trigger('volumechange');
        }

        getVolume() { return elementToUiVolume(this._volume); }

        setMute() {
            this._applyGain();
            this.player._trigger('volumechange');
        }

        isMuted() { return this.player._muted; }
        isAirPlayEnabled() { return false; }
        setAirPlayEnabled() { /* not available for Web Audio output */ }
    }

    function isDisabledLocally() {
        return readSetting(SETTINGS.disabled) === '1';
    }

    class VgmLoopPlayer {
        constructor(deps) {
            this.name = 'VGM Loop Player';
            this.type = 'mediaplayer';
            this.id = 'vgmloopplayer';
            this.priority = -10;
            this.isLocalPlayer = true;
            this._deps = deps;
            this._muted = false;
            this._playGen = 0;
            this._loopInfoCache = new Map();
            this._plain = new PlainEngine(this);
            this._loop = new LoopEngine(this);
            this._engine = this._plain;
            this._autoAdvanceAt = 0;
            this._prefetch = null;
            this._prefetchTimer = null;
            if (!isDisabledLocally()) {
                installNextTrackOverride(deps.playbackManager, (p) => p === this, () => this._isAutoAdvance());
                installStickyRepeat(deps.playbackManager && deps.playbackManager._playQueueManager, {
                    get: () => readSetting(SETTINGS.repeat),
                    set: (mode) => writeSetting(SETTINGS.repeat, mode)
                }, () => {
                    if (this._engine === this._loop) this._loop.onRepeatModeChange();
                });
            }
            deps.events.on(this, 'repeatmodechange', () => {
                if (this._engine === this._loop) this._loop.onRepeatModeChange();
            });
            console.info(TAG, 'player registered; loop output ' + outputMode() + (isDisabledLocally() ? '; DISABLED in this browser (vgmloop.disabled=1)' : ''));
            if (pendingNotice && deps.toast) {
                const msg = pendingNotice;
                pendingNotice = null;
                setTimeout(() => deps.toast(msg), 1500); // after the app shell has rendered
            }
        }

        // ---- preloading the next loop track (one slot)

        _dropPrefetch() {
            clearTimeout(this._prefetchTimer);
            this._prefetchTimer = null;
            const slot = this._prefetch;
            this._prefetch = null;
            if (slot) slot.abort.abort();
        }

        /** Hands over the preload for `itemId` (a promise of { bytes } / { buffer }), discarding any other. */
        _takePrefetch(itemId) {
            clearTimeout(this._prefetchTimer);
            this._prefetchTimer = null;
            const slot = this._prefetch;
            if (slot && slot.itemId === itemId) {
                this._prefetch = null;
                return slot.promise;
            }
            this._dropPrefetch();
            return null;
        }

        _schedulePrefetch(currentItem) {
            clearTimeout(this._prefetchTimer);
            if (!AudioContextClass || readSetting(SETTINGS.prefetch) === 'off') return;
            const gen = this._playGen;
            this._prefetchTimer = setTimeout(() => {
                this._prefetchTimer = null;
                this._startPrefetch(currentItem, gen).catch((e) => console.debug(TAG, 'preload skipped:', e && e.message));
            }, PREFETCH_DELAY_MS);
        }

        async _startPrefetch(currentItem, gen) {
            const pm = this._deps.playbackManager;
            const queue = pm && pm._playQueueManager;
            if (!queue || gen !== this._playGen) return;
            const next = nextQueueItem(queue.getPlaylist(), queue.getCurrentPlaylistIndex(), queue.getRepeatMode());
            if (!next || next.Type !== 'Audio' || !next.Id || (currentItem && next.Id === currentItem.Id)) return;
            if (this._prefetch && this._prefetch.itemId === next.Id) return;
            this._dropPrefetch();

            const info = await this._getLoopInfo({ item: next, mediaSource: null });
            if (gen !== this._playGen || !isLoopable(info)) return;

            const apiClient = this._deps.ServerConnections.getApiClient(next.ServerId);
            const abort = new AbortController();
            const decodeAhead = !browser.iOS && !!OfflineAudioContextClass
                && (info.totalSamples * Math.max(1, info.channels) * 4) / 1048576 <= PREFETCH_DECODE_MAX_MB;
            const slot = { itemId: next.Id, abort };
            slot.promise = (async () => {
                const bytes = await fetchOriginal(apiClient, next.Id, null, info.audioDataOffset, abort.signal);
                if (!decodeAhead) return { bytes };
                // An AudioBuffer isn't tied to a context; decoding at the native rate here matches the engine's decode.
                const buffer = await new OfflineAudioContextClass(Math.max(1, info.channels), 1, info.sampleRate).decodeAudioData(bytes);
                return abort.signal.aborted ? null : { buffer };
            })();
            this._prefetch = slot;
            slot.promise.then(
                (r) => r && console.info(TAG, 'preloaded next loop track ' + (next.Name || next.Id) + (r.buffer ? ' (decoded)' : ' (bytes)')),
                (e) => {
                    if (!abort.signal.aborted) console.warn(TAG, 'preload failed (will load normally):', e && e.message);
                    if (this._prefetch === slot) this._prefetch = null;
                });
        }

        /** A track just ended by itself; the next nextTrack() call is playbackManager's auto-advance. */
        _markAutoAdvance() {
            this._autoAdvanceAt = Date.now();
        }

        _isAutoAdvance() {
            return Date.now() - this._autoAdvanceAt < 5000;
        }

        _savedVolume() {
            if (this._deps.appHost.supports('physicalvolumecontrol')) return 1;
            return this._deps.appSettings.get('volume') || 1;
        }

        /** GET /VgmLoop/Items/{id}/LoopInfo (3 s timeout). Any failure means "play it plainly". */
        async _getLoopInfo(options) {
            const item = options.item;
            const key = item.Id + '|' + ((options.mediaSource && options.mediaSource.ETag) || '');
            if (this._loopInfoCache.has(key)) return this._loopInfoCache.get(key);
            const apiClient = this._deps.ServerConnections.getApiClient(item.ServerId);
            const info = await withTimeout(apiClient.getJSON(apiClient.getUrl('VgmLoop/Items/' + item.Id + '/LoopInfo')), 3000, 'LoopInfo');
            if (this._loopInfoCache.size > 200) this._loopInfoCache.clear();
            this._loopInfoCache.set(key, info);
            return info;
        }

        _trigger(name, args) {
            this._deps.events.trigger(this, name, args);
        }

        canPlayMediaType(mediaType) {
            return (mediaType || '').toLowerCase() === 'audio';
        }

        canPlayItem(item) {
            // Music tracks only; audiobooks, radio and TV channels stay with the stock players.
            return !!item && item.Type === 'Audio' && !isDisabledLocally();
        }

        getDeviceProfile(item, options) {
            const appHost = this._deps.appHost;
            return Promise.resolve(appHost.getDeviceProfile(item, options))
                .then((profile) => adaptDeviceProfile(profile, canPlayFormat));
        }

        async play(options) {
            const gen = ++this._playGen;
            this._autoAdvanceAt = 0;
            const preloaded = options.item && options.item.Id ? this._takePrefetch(options.item.Id) : this._takePrefetch(null);
            let info = null;
            if (AudioContextClass && options.item && options.item.Id && options.mediaSource) {
                try {
                    info = await this._getLoopInfo(options);
                } catch (e) {
                    console.warn(TAG, 'LoopInfo unavailable, playing plainly:', e && e.message);
                }
            }
            if (gen !== this._playGen) return undefined; // stopped or replaced while waiting

            if (isLoopable(info)) {
                this._engine = this._loop;
                try {
                    const r = await this._loop.play(options, info, preloaded);
                    if (r === true) this._schedulePrefetch(options.item);
                    return undefined;
                } catch (e) {
                    if (gen !== this._playGen) return undefined;
                    console.warn(TAG, 'loop engine failed, playing without looping:', e);
                }
            } else if (info) {
                console.debug(TAG, 'plain engine: ' + (info.reason || info.codec));
            }

            this._engine = this._plain;
            const r = this._plain.play(options);
            r.then(() => {
                if (gen === this._playGen) this._schedulePrefetch(options.item);
            }, () => {});
            return r;
        }

        stop(destroyPlayer) {
            this._playGen++;
            if (destroyPlayer) this._dropPrefetch(); // a track change (stop(false)) keeps the preload for the next play()
            return Promise.resolve(this._engine.stop(destroyPlayer));
        }

        destroy() {
            this._playGen++;
            this._autoAdvanceAt = 0;
            this._dropPrefetch();
            this._plain.destroy();
            this._loop.destroy();
        }
        currentSrc() { return this._engine.currentSrc(); }
        currentTime(val) { return this._engine.currentTime(val); }
        duration() { return this._engine.duration(); }
        seekable() { return this._engine.seekable(); }
        getBufferedRanges() { return this._engine.getBufferedRanges(); }
        pause() { this._engine.pause(); }
        resume() { this.unpause(); } // retry after error
        unpause() { this._engine.unpause(); }
        paused() { return this._engine.paused(); }
        setPlaybackRate(v) { this._engine.setPlaybackRate(v); }
        getPlaybackRate() { return this._engine.getPlaybackRate(); }
        setVolume(v) { this._engine.setVolume(v); }
        getVolume() { return this._engine.getVolume(); }
        volumeUp() { this.setVolume(Math.min((this.getVolume() || 0) + 2, 100)); }
        volumeDown() { this.setVolume(Math.max((this.getVolume() || 0) - 2, 0)); }
        setMute(m) {
            this._muted = !!m;
            this._engine.setMute(this._muted);
        }

        isMuted() { return this._engine.isMuted(); }
        isAirPlayEnabled() { return this._engine.isAirPlayEnabled(); }
        setAirPlayEnabled(e) { this._engine.setAirPlayEnabled(e); }
        toggleAirPlay() { return this.setAirPlayEnabled(!this.isAirPlayEnabled()); }

        supports(feature) {
            const loop = this._engine === this._loop;
            if (feature === 'PlaybackRate') return !loop && typeof document.createElement('audio').playbackRate === 'number';
            if (feature === 'AirPlay') return !loop && browser.safari;
            return false;
        }
    }

    root.VgmLoopPlayer = async () => VgmLoopPlayer;
    console.info(TAG, 'player.js loaded');
})(typeof window !== 'undefined' ? window : globalThis);
