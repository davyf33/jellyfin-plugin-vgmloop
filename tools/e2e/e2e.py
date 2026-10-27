#!/usr/bin/env python3
"""End-to-end checks for VGM Loop against a real Jellyfin server (no web client needed).

Usage:
  tools/e2e/gen_library.py /path/to/music            # 961 short test files
  # start a fresh Jellyfin with the plugin in <data>/plugins and --webdir pointing at a folder holding
  # jellyfin-web's index.html and config.json (tests/Jellyfin.Plugin.VgmLoop.Tests/TestData has copies)
  tools/e2e/e2e.py http://localhost:8096 /path/to/music [plugin_version]

It completes the startup wizard if needed (user admin / password e2e), adds the music library, scans, and then checks
the web injection, the LoopInfo / Discs / LoopStatus API, album tags across rescans (retagging files with ffmpeg),
a user without library access, and cold-cache timings after a server restart. Files in the music folder are edited.
Exits non-zero if any check fails.
"""
import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

BASE = sys.argv[1].rstrip('/')
MUSIC = sys.argv[2]
VERSION = sys.argv[3] if len(sys.argv) > 3 else '1.2.0.0'
PLUGIN = 'f19dceed-5dce-4a6e-b77c-1b0ccdf8709a'
AUTH = 'MediaBrowser Client="vgmloop-e2e", Device="e2e", DeviceId="vgmloop-e2e", Version="1.0"'
results = []


def check(name, cond, extra=None):
    results.append((name, bool(cond)))
    print(('PASS ' if cond else 'FAIL ') + name + ('' if extra is None else '  ' + json.dumps(extra, default=str)[:300]), flush=True)
    return cond


def req(method, path, body=None, token=None, raw=False, expect=None):
    url = BASE + path
    data = None if body is None else json.dumps(body).encode()
    headers = {'Content-Type': 'application/json'}
    if token is not False:
        headers['Authorization'] = AUTH + (f', Token="{token}"' if token else '')
    r = urllib.request.Request(url, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(r, timeout=120) as resp:
            payload = resp.read()
            status = resp.status
    except urllib.error.HTTPError as e:
        payload, status = e.read(), e.code
    if expect is not None and status != expect:
        raise RuntimeError(f'{method} {path} -> {status}: {payload[:300]!r}')
    if raw:
        return status, payload
    return status, (json.loads(payload) if payload and payload[:1] in b'{[' else payload)


def wait_ready(timeout=180):
    end = time.time() + timeout
    while time.time() < end:
        try:
            s, info = req('GET', '/System/Info/Public', token=False)
            if s == 200 and isinstance(info, dict) and 'Version' in info:  # not the startup placeholder
                return info
        except Exception:
            pass
        time.sleep(1)
    raise RuntimeError('server did not come up')


def wait_logged_in(timeout=240):
    """After a restart: wait until login and an authenticated request both work (the startup page answers first)."""
    end = time.time() + timeout
    while time.time() < end:
        try:
            wait_ready(timeout=max(1, end - time.time()))
            tok, uid = login('admin', 'e2e')
            if req('GET', '/Users/Me', token=tok)[0] == 200:
                return tok, uid
        except Exception:
            pass
        time.sleep(2)
    raise RuntimeError('server did not come back after the restart')


def login(name, password):
    s, r = req('POST', '/Users/AuthenticateByName', {'Username': name, 'Pw': password}, expect=200)
    return r['AccessToken'], r['User']['Id']


def scan(token):
    """Runs a library scan and waits for it (post-scan tasks included) to finish."""
    tasks = req('GET', '/ScheduledTasks?isHidden=false', token=token)[1]
    task = next(t for t in tasks if t['Key'] == 'RefreshLibrary')
    before = (task.get('LastExecutionResult') or {}).get('EndTimeUtc')
    req('POST', '/Library/Refresh', token=token, expect=204)
    t0 = time.time()
    while time.time() - t0 < 600:
        time.sleep(1)
        task = next(t for t in req('GET', '/ScheduledTasks?isHidden=false', token=token)[1] if t['Key'] == 'RefreshLibrary')
        end = (task.get('LastExecutionResult') or {}).get('EndTimeUtc')
        if task['State'] == 'Idle' and end and end != before:
            return time.time() - t0
    raise RuntimeError('scan did not finish')


def items(token, user, **q):
    q.setdefault('Recursive', 'true')
    q['userId'] = user
    return req('GET', '/Items?' + urllib.parse.urlencode(q), token=token, expect=200)[1]['Items']


def album(token, user, name):
    found = [a for a in items(token, user, IncludeItemTypes='MusicAlbum', Fields='Tags,ProviderIds') if a['Name'] == name]
    return found[0] if found else None


def tracks(token, user, album_id):
    return items(token, user, ParentId=album_id, IncludeItemTypes='Audio', Fields='Path', SortBy='ParentIndexNumber,IndexNumber')


def album_tags(token, user, name):
    a = album(token, user, name)
    return (a or {}).get('Tags', []), (a or {}).get('ProviderIds', {})


def update_item(token, user, item_id, change):
    s, item = req('GET', f'/Items/{item_id}?userId={user}', token=token, expect=200)
    change(item)
    req('POST', f'/Items/{item_id}', item, token=token, expect=204)


def plugin_config(token, change=None):
    cfg = req('GET', f'/Plugins/{PLUGIN}/Configuration', token=token, expect=200)[1]
    if change:
        change(cfg)
        req('POST', f'/Plugins/{PLUGIN}/Configuration', cfg, token=token, expect=204)
    return cfg


def retag(path, set_tags=None, clear=()):
    """Rewrites a file's tags with ffmpeg (stream copy); the new file gets a new mtime."""
    tmp = path + '.tmp' + os.path.splitext(path)[1]
    cmd = ['ffmpeg', '-loglevel', 'error', '-y', '-i', path, '-map', '0', '-map_metadata', '0', '-c', 'copy']
    for k, v in (set_tags or {}).items():
        cmd += ['-metadata', f'{k}={v}']
    for k in clear:
        cmd += ['-metadata', f'{k}=']
    if path.endswith('.mp3'):
        cmd += ['-id3v2_version', '4']
    subprocess.run(cmd + [tmp], check=True)
    os.replace(tmp, path)


def loop_status(token, ids):
    return req('POST', '/VgmLoop/LoopStatus', {'itemIds': ids}, token=token, expect=200)[1]['loopItemIds']


def main():
    info = wait_ready()
    check('server is Jellyfin 12.2', info['Version'].startswith('12.2'), info['Version'])
    if not info.get('StartupWizardCompleted'):
        req('POST', '/Startup/Configuration', {'UICulture': 'en-US', 'MetadataCountryCode': 'US', 'PreferredMetadataLanguage': 'en'}, token=False, expect=204)
        req('GET', '/Startup/User', token=False, expect=200)
        req('POST', '/Startup/User', {'Name': 'admin', 'Password': 'e2e'}, token=False, expect=204)
        req('POST', '/Startup/RemoteAccess', {'EnableRemoteAccess': True, 'EnableAutomaticPortMapping': False}, token=False, expect=204)
        req('POST', '/Startup/Complete', token=False, expect=204)
    token, user = login('admin', 'e2e')

    # ---------------------------------------------------------------- plugin + web injection
    plugins = req('GET', '/Plugins', token=token, expect=200)[1]
    me = [p for p in plugins if p['Id'].replace('-', '') == PLUGIN.replace('-', '')]
    check('plugin loaded and active', me and me[0]['Version'] == VERSION and me[0]['Status'] == 'Active', me)
    for path in ['/web/', '/web/index.html']:
        s, html = req('GET', path, token=False, raw=True)
        html = html.decode()
        tag = f'<script src="/VgmLoop/web/player.js?v={VERSION}"></script>'
        check(f'{path}: player.js injected right before </head>', s == 200 and tag + '</head>' in html, s)
    s, cfg = req('GET', '/web/config.json', token=False)
    check('/web/config.json: VgmLoopPlayer in plugins', s == 200 and 'VgmLoopPlayer' in cfg.get('plugins', []))
    s, js = req('GET', '/VgmLoop/web/player.js', token=False, raw=True)
    here = os.path.dirname(os.path.abspath(__file__))
    with open(os.path.join(here, '..', '..', 'Jellyfin.Plugin.VgmLoop', 'Web', 'player.js'), 'rb') as f:
        check('player.js served anonymously, same as the repo copy', s == 200 and js == f.read(), s)
    check('LoopInfo needs auth', req('GET', '/VgmLoop/Items/' + '0' * 32 + '/LoopInfo', token=False)[0] == 401)

    # ---------------------------------------------------------------- library
    folders = req('GET', '/Library/VirtualFolders', token=token, expect=200)[1]
    if not any(f['Name'] == 'Music' for f in folders):
        q = urllib.parse.urlencode({'name': 'Music', 'collectionType': 'music', 'refreshLibrary': 'false', 'paths': MUSIC})
        req('POST', '/Library/VirtualFolders?' + q, {'LibraryOptions': {'EnableRealtimeMonitor': False}}, token=token, expect=204)
    secs = scan(token)
    print(f'     first scan took {secs:.0f} s', flush=True)
    names = {a['Name'] for a in items(token, user, IncludeItemTypes='MusicAlbum')}
    check('albums found', {'Cut Album', 'Tail Album', 'MP3 Album', 'Big Album', 'Plain Album'} <= names, sorted(names))
    albums = {n: album(token, user, n) for n in ['Cut Album', 'Tail Album', 'MP3 Album', 'Big Album', 'Plain Album']}
    tr = {n: tracks(token, user, a['Id']) for n, a in albums.items()}
    check('track counts', [len(tr[n]) for n in albums] == [6, 2, 2, 950, 1], [len(tr[n]) for n in albums])
    by_title = {t['Name']: t for n in tr for t in tr[n]}

    # ---------------------------------------------------------------- LoopInfo
    li = lambda t: req('GET', f'/VgmLoop/Items/{t["Id"]}/LoopInfo', token=token)
    s, c = li(by_title['Cut 1-1'])
    check('LoopInfo cut rip: loop 32000..96000 of 96000 @ 32 kHz', s == 200 and c['hasLoop'] and c['codec'] == 'flac' and c['sampleRate'] == 32000
          and c['loopStart'] == 32000 and c['loopEnd'] == 96000 and c['totalSamples'] == 96000, c)
    check('LoopInfo carries play-through defaults (2 loops, 10 s)', c.get('playthroughLoops') == 2 and c.get('playthroughFadeSeconds') == 10, c)
    s, c = li(by_title['No Loop'])
    check('LoopInfo without loop tags: hasLoop false + reason', s == 200 and not c['hasLoop'] and c.get('reason'), c)
    s, c = li(by_title['Vorbis'])
    check('LoopInfo Vorbis: loop, length from the last Ogg page', s == 200 and c['hasLoop'] and c['codec'] == 'vorbis' and c['loopEnd'] == 96000 and c['totalSamples'] == 192000, c)
    s, c = li(by_title['Opus'])
    check('LoopInfo Opus: 48 kHz, pre-skip, loop', s == 200 and c['hasLoop'] and c['codec'] == 'opus' and c['sampleRate'] == 48000 and c.get('preSkip', 0) > 0 and c['loopEnd'] == 144000, c)
    s, c = li(by_title['MP3 1'])
    check('LoopInfo MP3: no loop engine (unsupported codec)', s == 200 and not c['hasLoop'], c)
    check('LoopInfo on an album: 400', req('GET', f'/VgmLoop/Items/{albums["Cut Album"]["Id"]}/LoopInfo', token=token)[0] == 400)
    check('LoopInfo on an unknown id: 404', req('GET', '/VgmLoop/Items/' + 'f' * 32 + '/LoopInfo', token=token)[0] == 404)
    check('LoopInfo / Discs on the empty id: 404, not 500', req('GET', '/VgmLoop/Items/' + '0' * 32 + '/LoopInfo', token=token)[0] == 404
          and req('GET', '/VgmLoop/Items/' + '0' * 32 + '/Discs', token=token)[0] == 404)
    check('LoopInfo on a malformed id: 400/404', req('GET', '/VgmLoop/Items/not-a-guid/LoopInfo', token=token)[0] in (400, 404))

    # ---------------------------------------------------------------- Discs
    def discs(item_id, tok=None):
        return req('GET', f'/VgmLoop/Items/{item_id}/Discs', token=tok or token)

    s, d = discs(albums['Cut Album']['Id'])
    cut_ids = {t['Id'].replace('-', '') for t in tr['Cut Album']}
    check('Discs (album id): Town Music / Hourly Music with all their tracks',
          s == 200 and [(x['discNumber'], x['name'], len(x['itemIds'])) for x in d['discs']] == [(1, 'Town Music', 3), (2, 'Hourly Music', 3)]
          and set(sum((x['itemIds'] for x in d['discs']), [])) == cut_ids, d)
    s, d2 = discs(by_title['Cut 2-1']['Id'])
    check('Discs (track id): same album answer', s == 200 and d2 == d)
    s, d = discs(albums['Tail Album']['Id'])
    check('Discs Ogg Vorbis + Opus', s == 200 and [x['name'] for x in d['discs']] == ['Vorbis Disc', 'Opus Disc'], d)
    s, d = discs(albums['MP3 Album']['Id'])
    check('Discs MP3 from ID3 TSST (read with ATL)', s == 200 and [x['name'] for x in d['discs']] == ['Red Side', 'Blue Side'], d)
    t0 = time.time()
    s, d = discs(albums['Big Album']['Id'])
    check('Discs big album: 10 named discs of 95', s == 200 and [x['name'] for x in d['discs']] == [f'Big Disc {i}' for i in range(1, 11)]
          and all(len(x['itemIds']) == 95 for x in d['discs']), [x['name'] for x in d.get('discs', [])])
    print(f'     Discs big album: {1000 * (time.time() - t0):.0f} ms', flush=True)
    s, d = discs(albums['Plain Album']['Id'])
    check('Discs untagged album: empty list', s == 200 and d['discs'] == [], d)
    music = next(f for f in req('GET', '/Library/VirtualFolders', token=token, expect=200)[1] if f['Name'] == 'Music')
    check('Discs on a library folder: 400', discs(music['ItemId'])[0] == 400)

    # ---------------------------------------------------------------- LoopStatus
    ids = [t['Id'] for t in tr['Cut Album']]
    got = set(loop_status(token, ids))
    want = {t['Id'].replace('-', '') for t in tr['Cut Album'] if t['Name'] != 'No Loop'}
    check('LoopStatus cut album: the 5 loop tracks, not "No Loop"', got == want, sorted(got))
    check('LoopStatus ignores garbage ids and non-audio items', set(loop_status(token, ids + ['nope', '', albums['Cut Album']['Id']])) == want)
    big = [t['Id'] for t in tr['Big Album']]
    t0 = time.time()
    looping = set()
    for i in range(0, len(big), 100):
        looping |= set(loop_status(token, big[i:i + 100]))
    warm_or_first = time.time() - t0
    check('LoopStatus big album: 475 of 950 loop (batches of 100)', len(looping) == 475, len(looping))
    print(f'     LoopStatus 950 tracks (first read after scan): {1000 * warm_or_first:.0f} ms', flush=True)
    check('LoopStatus caps one request at 500 ids', len(loop_status(token, big)) <= 250 + 1, len(loop_status(token, big)))
    plugin_config(token, lambda c: c.update(EnableLoopIcons=False))
    check('LoopStatus with loop icons off: empty', loop_status(token, ids) == [])
    plugin_config(token, lambda c: c.update(EnableLoopIcons=True))
    plugin_config(token, lambda c: c.update(EnableDiscNames=False))
    check('Discs with disc names off: empty, buttons still on', discs(albums['Cut Album']['Id'])[1] == {
        'albumId': albums['Cut Album']['Id'].replace('-', ''), 'discs': [], 'discButtons': True})
    plugin_config(token, lambda c: c.update(EnableDiscNames=True))
    check('Discs tells the page to show disc buttons by default', discs(albums['Cut Album']['Id'])[1].get('discButtons') is True)
    plugin_config(token, lambda c: c.update(EnableDiscButtons=False))
    check('Discs with disc buttons off: discButtons false, names still there',
          discs(albums['Cut Album']['Id'])[1].get('discButtons') is False and len(discs(albums['Cut Album']['Id'])[1]['discs']) == 2)
    plugin_config(token, lambda c: c.update(EnableDiscButtons=True))
    check('LoopInfo: "every loop track" off by default (field absent)', 'playthroughAllLoops' not in li(by_title['Cut 1-1'])[1])
    plugin_config(token, lambda c: c.update(PlaythroughAllLoops=True))
    check('LoopInfo: "every loop track" on', li(by_title['Vorbis'])[1].get('playthroughAllLoops') is True)
    plugin_config(token, lambda c: c.update(EnablePlaythrough=False))
    check('LoopInfo: play-through off hides "every loop track" too', 'playthroughAllLoops' not in li(by_title['Vorbis'])[1])
    plugin_config(token, lambda c: c.update(EnablePlaythrough=True, PlaythroughAllLoops=False))
    plugin_config(token, lambda c: c.update(EnablePlaythrough=False))
    check('LoopInfo with play-through off: no play-through fields', 'playthroughLoops' not in li(by_title['Cut 1-1'])[1])
    plugin_config(token, lambda c: c.update(EnablePlaythrough=True, PlaythroughLoops=9, PlaythroughFadeSeconds=99))
    c = li(by_title['Cut 1-1'])[1]
    check('LoopInfo clamps play-through settings to 5 loops / 30 s', c.get('playthroughLoops') == 5 and c.get('playthroughFadeSeconds') == 30, c)
    plugin_config(token, lambda c: c.update(PlaythroughLoops=2, PlaythroughFadeSeconds=10))

    # ---------------------------------------------------------------- album tags after the first scan
    tags, pids = album_tags(token, user, 'Cut Album')
    check('Cut Album tags: Nintendo 3DS, Game Rip, Animal Crossing', {'Nintendo 3DS', 'Game Rip', 'Animal Crossing'} <= set(tags), tags)
    check('Cut Album provider ids record each field', pids.get('VgmLoopPlatform') == 'Nintendo 3DS' and pids.get('VgmLoopCategory') == 'Game Rip'
          and pids.get('VgmLoopSeries') == 'Animal Crossing', pids)
    check('Tail Album tags (Ogg): Wii, Official Soundtrack, Super Mario', {'Wii', 'Official Soundtrack', 'Super Mario'} <= set(album_tags(token, user, 'Tail Album')[0]))
    check('MP3 Album tags (ID3 TXXX, FRANCHISE alias): Game Boy, Third-Party Game Rip, Pokemon',
          {'Game Boy', 'Third-Party Game Rip', 'Pokemon'} <= set(album_tags(token, user, 'MP3 Album')[0]), album_tags(token, user, 'MP3 Album')[0])
    check('Big Album tags: Nintendo Switch', 'Nintendo Switch' in album_tags(token, user, 'Big Album')[0])
    check('Plain Album: no tags', album_tags(token, user, 'Plain Album')[0] == [])
    by_tag = [a['Name'] for a in items(token, user, IncludeItemTypes='MusicAlbum', Tags='Game Rip')]
    check('library filter by tag "Game Rip": Cut Album only', by_tag == ['Cut Album'], by_tag)

    # ---------------------------------------------------------------- rescans
    cut_id, tail_id, plain_id = albums['Cut Album']['Id'], albums['Tail Album']['Id'], albums['Plain Album']['Id']
    update_item(token, user, cut_id, lambda i: i.update(Tags=i.get('Tags', []) + ['Favourite']))
    scan(token)
    tags, _ = album_tags(token, user, 'Cut Album')
    check('unchanged files + user tag: rescan keeps everything', {'Nintendo 3DS', 'Game Rip', 'Animal Crossing', 'Favourite'} <= set(tags), tags)

    cut_paths = [t['Path'] for t in tr['Cut Album']]
    for p in cut_paths:
        retag(p, {'RELEASECATEGORY': 'Official Soundtrack'}, clear=['SERIES'])
    scan(token)
    tags, pids = album_tags(token, user, 'Cut Album')
    check('RELEASECATEGORY Game Rip -> Official Soundtrack swaps the tag', 'Official Soundtrack' in tags and 'Game Rip' not in tags, tags)
    check('clearing SERIES removes only the series tag', 'Animal Crossing' not in tags and 'Nintendo 3DS' in tags and 'Favourite' in tags, tags)
    check('records follow: category updated, series record gone', pids.get('VgmLoopCategory') == 'Official Soundtrack' and 'VgmLoopSeries' not in pids, pids)

    update_item(token, user, tail_id, lambda i: i.update(LockedFields=list(set(i.get('LockedFields', []) + ['Tags']))))
    for t in tr['Tail Album']:
        retag(t['Path'], {'PLATFORM': 'Wii U'})
    scan(token)
    check('locked album left alone', set(album_tags(token, user, 'Tail Album')[0]) >= {'Wii'} and 'Wii U' not in album_tags(token, user, 'Tail Album')[0],
          album_tags(token, user, 'Tail Album')[0])

    plugin_config(token, lambda c: c.update(EnableSeriesTags=False))
    for t in tr['MP3 Album']:
        retag(t['Path'], {'FRANCHISE': 'Pokemon Stadium'})
    scan(token)
    tags = album_tags(token, user, 'MP3 Album')[0]
    check('series setting off: series tag left as it was', 'Pokemon' in tags and 'Pokemon Stadium' not in tags, tags)
    plugin_config(token, lambda c: c.update(EnableSeriesTags=True))
    scan(token)
    tags = album_tags(token, user, 'MP3 Album')[0]
    check('series setting back on: tag updated on the next scan', 'Pokemon Stadium' in tags and 'Pokemon' not in tags, tags)

    update_item(token, user, plain_id, lambda i: i.update(Tags=['Nintendo 3DS']))
    plain_path = tr['Plain Album'][0]['Path']
    retag(plain_path, {'PLATFORM': 'Nintendo 3DS'})
    scan(token)
    tags, pids = album_tags(token, user, 'Plain Album')
    check('a tag the user already had is not claimed', tags == ['Nintendo 3DS'] and 'VgmLoopPlatform' not in pids, [tags, pids])
    retag(plain_path, clear=['PLATFORM'])
    scan(token)
    check('...and survives the file tag being removed', album_tags(token, user, 'Plain Album')[0] == ['Nintendo 3DS'])

    # ---------------------------------------------------------------- a user without library access
    s, u = req('POST', '/Users/New', {'Name': 'limited', 'Password': 'e2e'}, token=token)
    if s == 200:
        pol = u['Policy']
        pol.update(EnableAllFolders=False, EnabledFolders=[])
        req('POST', f'/Users/{u["Id"]}/Policy', pol, token=token, expect=204)
    ltoken, _ = login('limited', 'e2e')
    check('no library access: LoopInfo 404', req('GET', f'/VgmLoop/Items/{by_title["Cut 1-1"]["Id"]}/LoopInfo', token=ltoken)[0] == 404)
    check('no library access: Discs 404', discs(albums['Cut Album']['Id'], ltoken)[0] == 404)
    check('no library access: LoopStatus empty', loop_status(ltoken, ids) == [])

    # ---------------------------------------------------------------- cold cache after a restart
    req('POST', '/System/Restart', token=token)
    time.sleep(5)
    token, user = wait_logged_in()
    # Jellyfin answers 503 "loading" for a few seconds after login works again; those never reach the plugin.
    for _ in range(60):
        if req('GET', '/Users/Me', token=token)[0] == 200 and req('GET', f'/VgmLoop/Items/{"0" * 32}/LoopInfo', token=token)[0] == 404:
            break
        time.sleep(1)
    t0 = time.time()
    s, d = discs(albums['Big Album']['Id'])
    cold_discs = time.time() - t0
    t0 = time.time()
    first = loop_status(token, big[:100])
    first_batch = time.time() - t0
    rest = set(first)
    for i in range(100, len(big), 100):
        rest |= set(loop_status(token, big[i:i + 100]))
    cold_all = time.time() - t0
    check('after restart: big album still correct', s == 200 and len(d['discs']) == 10 and len(rest) == 475,
          [s, d[:200] if isinstance(d, bytes) else len(d.get('discs', [])), len(rest)])
    print(f'     cold: Discs {1000 * cold_discs:.0f} ms, first LoopStatus batch {1000 * first_batch:.0f} ms, all 950 {1000 * cold_all:.0f} ms', flush=True)
    t0 = time.time()
    for i in range(0, len(big), 100):
        loop_status(token, big[i:i + 100])
    print(f'     warm: all 950 {1000 * (time.time() - t0):.0f} ms', flush=True)
    check('cold big album: all icons within 5 s', cold_all < 5, round(cold_all, 2))

    failed = [n for n, ok in results if not ok]
    print(f'\n{len(results) - len(failed)}/{len(results)} passed' + (': FAILED ' + '; '.join(failed) if failed else ''))
    with open(os.environ.get('E2E_IDS', os.devnull), 'w') as f:
        json.dump({'token': token, 'user': user, 'albums': {n: a['Id'] for n, a in albums.items()},
                   'tracks': {t['Name']: t['Id'] for n in tr for t in tr[n]}}, f)
    sys.exit(1 if failed else 0)


if __name__ == '__main__':
    main()
