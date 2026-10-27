// Browser checks against a real Jellyfin: the real player.js (loop engine, play-through, disc names, loop icons)
// with the server's own LoopInfo / Discs / LoopStatus answers and audio streams. jellyfin-web itself is not loaded;
// its pluginManager deps are stand-ins, the album list uses the markup jellyfin-web 12.2 renders.
//   node tools/e2e/live.mjs http://localhost:8096      (after tools/e2e/e2e.py has set the server up)
// Needs Playwright (PLAYWRIGHT=/path/to/playwright/index.mjs if it isn't resolvable) and a Chromium.
import { createServer } from 'node:http';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const SERVER = (process.argv[2] || 'http://localhost:8096').replace(/\/$/, '');
const HERE = dirname(fileURLToPath(import.meta.url));
const PLAYER = join(HERE, '..', '..', 'Jellyfin.Plugin.VgmLoop', 'Web', 'player.js');
const PLUGIN = 'f19dceed-5dce-4a6e-b77c-1b0ccdf8709a';
const AUTH = 'MediaBrowser Client="vgmloop-live", Device="e2e", DeviceId="vgmloop-live", Version="1.0"';
const { chromium } = await import(process.env.PLAYWRIGHT || 'playwright');

async function api(path, { method = 'GET', body, token } = {}) {
    const r = await fetch(SERVER + path, {
        method,
        headers: { Authorization: AUTH + (token ? `, Token="${token}"` : ''), 'Content-Type': 'application/json' },
        body: body === undefined ? undefined : JSON.stringify(body)
    });
    if (!r.ok) throw new Error(`${method} ${path} -> ${r.status}`);
    return r.status === 204 ? null : r.json();
}

const login = await api('/Users/AuthenticateByName', { method: 'POST', body: { Username: 'admin', Pw: 'e2e' } });
const token = login.AccessToken;
const user = login.User.Id;
const albums = (await api(`/Items?userId=${user}&Recursive=true&IncludeItemTypes=MusicAlbum`, { token })).Items;
const tracksOf = async (name) => (await api(`/Items?userId=${user}&ParentId=${albums.find((a) => a.Name === name).Id}&IncludeItemTypes=Audio&SortBy=ParentIndexNumber,IndexNumber&Fields=MediaSources`, { token })).Items;
const library = {};
for (const name of ['Cut Album', 'Tail Album', 'MP3 Album', 'Big Album']) library[name] = await tracksOf(name);

// Play-through short enough to watch: 1 loop pass, 1 s fade (cut tracks: 1 s intro + 2 s loop + 1 s fade = 4 s).
const cfg = await api(`/Plugins/${PLUGIN}/Configuration`, { token });
await api(`/Plugins/${PLUGIN}/Configuration`, { method: 'POST', token, body: { ...cfg, PlaythroughLoops: 1, PlaythroughFadeSeconds: 1 } });

const page = '<!doctype html><meta charset="utf-8"><script src="player.js"></script><body><div id="listChildrenCollapsible"><div class="itemsContainer"></div></div>';
const http = createServer((q, r) => {
    if (q.url.startsWith('/player.js')) { r.setHeader('Content-Type', 'application/javascript'); r.end(readFileSync(PLAYER)); return; }
    r.setHeader('Content-Type', 'text/html'); r.end(page);
}).listen(0);
const origin = `http://localhost:${http.address().port}/`;

const browser = await chromium.launch({ executablePath: process.env.CHROMIUM || undefined, args: ['--autoplay-policy=no-user-gesture-required'] });
const tab = await browser.newPage();
tab.on('console', (m) => { if (['error', 'warning'].includes(m.type())) console.log('  console.' + m.type() + ': ' + m.text()); });
tab.on('pageerror', (e) => console.log('  pageerror: ' + e.message));
await tab.goto(origin);

const results = await tab.evaluate(async ({ SERVER, token, user, AUTH, library }) => {
    const results = [];
    const check = (name, ok, extra) => { results.push({ name, ok: !!ok, extra }); };
    const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
    const near = (a, b, tol) => Math.abs(a - b) <= tol;
    const authFetch = (url, init = {}) => fetch(url, { ...init, headers: { ...(init.headers || {}), Authorization: AUTH + `, Token="${token}"` } });
    const apiClient = {
        accessToken: () => token,
        getCurrentUserId: () => user,
        getUrl: (path, params) => SERVER + '/' + path + (params ? '?' + new URLSearchParams(params) : ''),
        getJSON: (url) => authFetch(url).then((r) => { if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); }),
        ajax: (o) => authFetch(o.url, { method: o.type, headers: { 'Content-Type': o.contentType }, body: o.data })
            .then((r) => { if (!r.ok) throw new Error('HTTP ' + r.status); return o.dataType === 'json' ? r.json() : r; }),
        getItems: (userId, q) => {
            itemRequests++;
            return apiClient.getJSON(apiClient.getUrl('Items', { ...q, userId }));
        }
    };
    let itemRequests = 0;
    const plays = [];
    const listeners = new Map();
    const events = {
        on(o, n, f) { if (!listeners.has(o)) listeners.set(o, []); listeners.get(o).push({ n, f }); },
        off(o, n, f) { listeners.set(o, (listeners.get(o) || []).filter((x) => !(x.n === n && x.f === f))); },
        trigger(o, n, a) { for (const x of (listeners.get(o) || []).slice()) if (x.n === n) x.f({ type: n }, ...(a || [])); }
    };
    const store = new Map();
    let repeatMode = 'RepeatNone';
    const deps = {
        events,
        appSettings: { get: (k) => store.get(k) ?? null, set: (k, v) => store.set(k, v) },
        appHost: { supports: () => false, getDeviceProfile: () => Promise.resolve({}) },
        ServerConnections: { currentApiClient: () => apiClient, getApiClient: () => apiClient },
        playbackManager: { getRepeatMode: () => repeatMode, play: async (o) => { plays.push(o); } },
        globalize: { translate: (k) => ({ Play: 'Play', Shuffle: 'Shuffle' })[k] || k },
        loading: { show() {}, hide() {} }
    };
    const waitFor = (p, name, ms) => new Promise((resolve) => {
        const t = setTimeout(() => { events.off(p, name, fn); resolve(null); }, ms);
        function fn(e, ...a) { clearTimeout(t); events.off(p, name, fn); resolve(a.length ? a : true); }
        events.on(p, name, fn);
    });
    const byName = {};
    for (const n of Object.keys(library)) for (const t of library[n]) byName[t.Name] = t;
    const opts = (t) => {
        const ms = t.MediaSources[0];
        return {
            item: { Id: t.Id, ServerId: 's1', Type: 'Audio', MediaType: 'Audio', Name: t.Name },
            mediaSource: { Id: ms.Id, RunTimeTicks: ms.RunTimeTicks, Container: ms.Container },
            url: apiClient.getUrl('Audio/' + t.Id + '/stream', { static: true, mediaSourceId: ms.Id, ApiKey: token })
        };
    };

    const Cls = await window.VgmLoopPlayer();
    const p = new Cls(deps);
    store.set('volume', 0.5);
    const e = p._loop;

    // --- real cut rip from the server: play-through, ends by itself
    repeatMode = 'RepeatNone';
    let o = opts(byName['Cut 1-1']);
    const originalTicks = o.mediaSource.RunTimeTicks;
    let playing = waitFor(p, 'playing', 10000);
    await p.play(o);
    check('cut rip: loop engine from real LoopInfo', p._engine === e && await playing);
    check('cut rip: play-through 1 loop + 1 s fade = 4 s', e.pt && e.pt.end === 128000 && o.mediaSource.RunTimeTicks === 40000000, e.pt);
    let stopAt = null;
    const onStop = () => { stopAt = p.currentTime(); };
    events.on(p, 'stopped', onStop);
    const t0 = performance.now();
    const stopped = await waitFor(p, 'stopped', 8000);
    events.off(p, 'stopped', onStop);
    const took = performance.now() - t0;
    check('cut rip: ends by itself after ~4 s', stopped && took > 3200 && took < 5200, Math.round(took));
    check('cut rip: reported at the file end, runtime restored', near(stopAt, 3000, 1) && o.mediaSource.RunTimeTicks === originalTicks, [stopAt, o.mediaSource.RunTimeTicks]);

    // --- real Opus, Repeat One: 48 kHz context, sample-exact seam
    repeatMode = 'RepeatOne';
    o = opts(byName['Opus']);
    await p.play(o);
    check('Opus: loop engine at 48 kHz, looping, no play-through', p._engine === e && e._ctx.sampleRate === 48000 && e.source.loop && !e.pt);
    const buf = e.buffer, node = e.source;
    const ls = 48000, le = 144000, pre = 1000, n = 4000;
    const oc = new OfflineAudioContext(buf.numberOfChannels, n, buf.sampleRate);
    const s = oc.createBufferSource();
    s.buffer = buf; s.loop = true; s.loopStart = node.loopStart; s.loopEnd = node.loopEnd;
    s.connect(oc.destination); s.start(0, (le - pre) / buf.sampleRate);
    const out = await oc.startRendering();
    let maxErr = 0;
    for (let ch = 0; ch < buf.numberOfChannels; ch++) {
        const src = buf.getChannelData(ch), dst = out.getChannelData(ch);
        for (let i = 0; i < n; i++) maxErr = Math.max(maxErr, Math.abs(dst[i] - (i < pre ? src[le - pre + i] : src[ls + i - pre])));
    }
    check('Opus: seam is sample-exact (offline render)', maxErr < 1e-6, maxErr);
    p.currentTime(2900);
    await sleep(400);
    check('Opus: wraps back into the loop', p.currentTime() > 1000 && p.currentTime() < 2000, p.currentTime());
    await p.stop(false);

    // --- real Vorbis with a 3 s ending: plays once, natural end
    repeatMode = 'RepeatNone';
    o = opts(byName['Vorbis']);
    await p.play(o);
    check('Vorbis with an ending: no play-through, plays once', p._engine === e && !e.pt && e.source.loop === false && o.mediaSource.RunTimeTicks === opts(byName['Vorbis']).mediaSource.RunTimeTicks);
    p.currentTime(5600);
    check('Vorbis: natural end', await waitFor(p, 'stopped', 3000));

    // --- MP3: no loop -> plain <audio> engine streaming from the server
    o = opts(byName['MP3 1']);
    playing = waitFor(p, 'playing', 10000);
    await p.play(o);
    check('MP3: plain engine plays the server stream', p._engine === p._plain && await playing, p._engine === p._plain);
    await p.stop(true);

    // --- album page: disc names and loop icons from the real API (cold if the server was just started)
    const list = document.querySelector('#listChildrenCollapsible .itemsContainer');
    const item = (t) => `<div class="listItem listItem-border" data-action="playallfromhere" data-isfolder="false" data-id="${t.Id}" data-serverid="s1" data-type="Audio" data-mediatype="Audio"><div class="listItemBody"><div class="listItemBodyText"><bdi>${t.Name}</bdi></div></div></div>`;
    const render = (tracks) => {
        let html = '', disc = null;
        tracks.forEach((t, i) => {
            if (t.ParentIndexNumber !== disc) {
                disc = t.ParentIndexNumber;
                html += `<h2 class="listGroupHeader${i === 0 ? ' listGroupHeader-first' : ''}">Disc ${disc}</h2><div>`;
            }
            html += item(t);
        });
        list.innerHTML = html;
    };
    const until = async (cond, ms) => { const t = performance.now(); while (!cond() && performance.now() - t < ms) await sleep(10); return Math.round(performance.now() - t); };

    render(library['Cut Album']);
    await until(() => document.querySelectorAll('.vgmloop-discName').length === 2 && document.querySelectorAll('.vgmloop-loopIcon').length === 5, 5000);
    check('Cut Album: "Disc 1 - Town Music", "Disc 2 - Hourly Music"', [...list.querySelectorAll('h2')].map((h) => h.textContent).join('|') === 'Disc 1 - Town Music|Disc 2 - Hourly Music',
        [...list.querySelectorAll('h2')].map((h) => h.textContent));
    const iconed = [...list.querySelectorAll('.listItem')].filter((r) => r.querySelector('.vgmloop-loopIcon')).map((r) => r.textContent);
    check('Cut Album: icons on the 5 loop tracks, not "No Loop"', iconed.length === 5 && !iconed.includes('No Loop'), iconed);

    const big = library['Big Album'];
    const tBig = performance.now();
    render(big);
    const firstIcon = await until(() => document.querySelector('.vgmloop-loopIcon'), 10000);
    const namesAt = await until(() => document.querySelectorAll('.vgmloop-discName').length === 10, 10000) + firstIcon;
    await until(() => document.querySelectorAll('.vgmloop-loopIcon').length === 475, 20000);
    const allIcons = Math.round(performance.now() - tBig);
    const rows = [...list.querySelectorAll('.listItem')];
    const wrong = rows.filter((r, i) => !!r.querySelector('.vgmloop-loopIcon') !== (i % 2 === 0)).length;
    check('Big Album: 475 icons on exactly the looping rows', document.querySelectorAll('.vgmloop-loopIcon').length === 475 && wrong === 0, { wrong });
    check('Big Album: 10 disc names', [...list.querySelectorAll('h2')].every((h, i) => h.textContent === `Disc ${i + 1} - Big Disc ${i + 1}`));
    check('Big Album: first icon < 1.5 s, all icons < 5 s', firstIcon < 1500 && allIcons < 5000, { firstIcon, namesAt, allIcons });
    render(big);
    const redraw = await until(() => document.querySelectorAll('.vgmloop-loopIcon').length === 475 && document.querySelectorAll('.vgmloop-discName').length === 10, 5000);
    check('Big Album redraw: icons and names back from the page cache', redraw < 500, redraw);

    // --- disc Play / Shuffle buttons
    const waitPlays = async (n) => { await until(() => plays.length >= n, 10000); return plays[n - 1]; };
    const ids = (items) => items.map((i) => i.Id);
    render(library['Cut Album']);
    await until(() => list.querySelectorAll('.vgmloop-discButtons').length === 2 && list.querySelectorAll('.vgmloop-discName').length === 2, 5000);
    const headers = [...list.querySelectorAll('h2.listGroupHeader')];
    check('buttons: one Play + one Shuffle on each disc header', headers.every((h) => h.querySelector('.vgmloop-discPlay') && h.querySelector('.vgmloop-discShuffle')));
    check('buttons: header text unchanged ("Disc 1 - Town Music")', headers.map((h) => h.textContent).join('|') === 'Disc 1 - Town Music|Disc 2 - Hourly Music', headers.map((h) => h.textContent));
    const hr = headers[0].getBoundingClientRect(), br = headers[0].querySelector('.vgmloop-discButtons').getBoundingClientRect(),
        lr = headers[0].querySelector('.vgmloop-discLabel').getBoundingClientRect();
    check('buttons: on the same line as the name, at the right edge', near(br.right, hr.right, 2) && lr.left <= hr.left + 2 && br.top < lr.bottom && br.bottom > lr.top,
        { header: [hr.left, hr.right], label: [lr.left, lr.right], buttons: [br.left, br.right] });
    headers[1].querySelector('.vgmloop-discPlay').click();
    let call = await waitPlays(1);
    const disc2 = library['Cut Album'].filter((t) => t.ParentIndexNumber === 2).map((t) => t.Id);
    check('Play on disc 2: exactly disc 2, in order, as full items', call && JSON.stringify(ids(call.items)) === JSON.stringify(disc2) && call.serverId === 's1'
        && call.items.every((i) => i.MediaSources && i.MediaSources.length), call && ids(call.items));

    const bigDisc3 = big.filter((t) => t.ParentIndexNumber === 3).map((t) => t.Id);
    render(big);
    await until(() => list.querySelectorAll('.vgmloop-discButtons').length === 10, 5000);
    list.querySelectorAll('h2.listGroupHeader')[2].querySelector('.vgmloop-discShuffle').click();
    call = await waitPlays(2);
    const got = call ? ids(call.items) : [];
    check('Shuffle on disc 3: the same 95 tracks, shuffled', got.length === 95 && JSON.stringify([...got].sort()) === JSON.stringify([...bigDisc3].sort())
        && JSON.stringify(got) !== JSON.stringify(bigDisc3), got.length);

    // One header over all 950 tracks: more than jellyfin-web's 300-item cap when it fetches ids itself.
    list.innerHTML = '<h2 class="listGroupHeader listGroupHeader-first">Disc 1</h2><div>' + big.map(item).join('');
    await until(() => list.querySelector('.vgmloop-discButtons'), 5000);
    itemRequests = 0;
    list.querySelector('.vgmloop-discPlay').click();
    call = await waitPlays(3);
    check('Play on a 950-track disc: all 950, in order (fetched in chunks of 100)', call && call.items.length === 950
        && JSON.stringify(ids(call.items)) === JSON.stringify(big.map((t) => t.Id)) && itemRequests === 10, { n: call && call.items.length, itemRequests });

    // For a look at the layout: icon glyphs stand in for jellyfin-web's Material Icons font.
    const style = document.createElement('style');
    style.textContent = 'body{background:#101010;color:#fff;font-family:sans-serif;width:640px;padding:12px}h2.listGroupHeader{font-weight:500;font-size:1.3em}'
        + '.paper-icon-button-light{background:none;border:0;color:inherit;font-size:1.2em;padding:.3em .4em;cursor:pointer}'
        + '.material-icons.play_arrow::before{content:"\\25B6"}.material-icons.shuffle::before{content:"\\21C4"}.listItem{padding:.4em 0;border-bottom:1px solid #222}';
    document.head.appendChild(style);
    render(library['Cut Album']);
    await until(() => list.querySelectorAll('.vgmloop-discButtons').length === 2, 5000);
    return results;
}, { SERVER, token, user, AUTH, library });

if (process.env.SCREENSHOT) await tab.screenshot({ path: process.env.SCREENSHOT, fullPage: true });
await api(`/Plugins/${PLUGIN}/Configuration`, { method: 'POST', token, body: cfg });
await browser.close();
http.close();
let failed = 0;
for (const r of results) {
    if (!r.ok) failed++;
    console.log((r.ok ? 'PASS ' : 'FAIL ') + r.name + (r.extra !== undefined ? '  ' + JSON.stringify(r.extra) : ''));
}
console.log(`\n${results.length - failed}/${results.length} passed`);
process.exit(failed ? 1 : 0);
