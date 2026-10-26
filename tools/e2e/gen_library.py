"""Builds the end-to-end test library with ffmpeg (961 short files). Usage: gen_library.py OUT_DIR"""
import os, subprocess, sys, concurrent.futures as cf
OUT = sys.argv[1]

def enc(path, seconds, freq, tags, codec_args, rate=32000):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    cmd = ['ffmpeg', '-loglevel', 'error', '-y', '-f', 'lavfi', '-i', f'sine=frequency={freq}:duration={seconds}:sample_rate={rate}', '-ac', '2']
    for k, v in tags.items():
        cmd += ['-metadata', f'{k}={v}']
    cmd += codec_args + [path]
    subprocess.run(cmd, check=True)

FLAC = ['-c:a', 'flac']
jobs = []
# 1. FLAC cut rips: 2 named discs, all three album tags. Loop 1 s..3 s ends at the end of the 3 s file.
cut = {'ALBUM': 'Cut Album', 'ALBUMARTIST': 'Nintendo', 'ARTIST': 'Composer A', 'GENRE': 'Soundtrack', 'DATE': '2012',
       'PLATFORM': 'Nintendo 3DS', 'RELEASECATEGORY': 'Game Rip', 'SERIES': 'Animal Crossing',
       'LOOPSTART': '32000', 'LOOPLENGTH': '64000'}
for d, name, n in [(1, 'Town Music', 3), (2, 'Hourly Music', 2)]:
    for t in range(1, n + 1):
        tags = dict(cut, DISCNUMBER=str(d), DISCTOTAL='2', TRACKNUMBER=str(t), TITLE=f'Cut {d}-{t}', DISCSUBTITLE=name)
        jobs.append((f'{OUT}/Cut Album (2012)/Disc {d:02}/{t:02} - Cut {d}-{t}.flac', 3, 300 + 40 * t + 200 * d, tags, FLAC))
# One cut-album track without a loop (plain playback in the same album).
jobs.append((f'{OUT}/Cut Album (2012)/Disc 02/03 - No Loop.flac', 2, 900,
             {k: v for k, v in dict(cut, DISCNUMBER='2', TRACKNUMBER='3', TITLE='No Loop', DISCSUBTITLE='Hourly Music').items() if not k.startswith('LOOP')}, FLAC))

# 2. Ogg Vorbis + Opus with a real 3 s ending after the loop (not cut rips). Disc names, platform, series.
tail = {'ALBUM': 'Tail Album', 'ALBUMARTIST': 'Nintendo', 'GENRE': 'Soundtrack', 'PLATFORM': 'Wii', 'SERIES': 'Super Mario',
        'RELEASECATEGORY': 'Official Soundtrack', 'DISCTOTAL': '2'}
jobs.append((f'{OUT}/Tail Album/Disc 1/01 - Vorbis.ogg', 6, 440, dict(tail, DISCNUMBER='1', TRACKNUMBER='1', TITLE='Vorbis', DISCSUBTITLE='Vorbis Disc', LOOPSTART='32000', LOOPLENGTH='64000'), ['-c:a', 'libvorbis', '-q:a', '2']))
jobs.append((f'{OUT}/Tail Album/Disc 2/01 - Opus.opus', 6, 550, dict(tail, DISCNUMBER='2', TRACKNUMBER='1', TITLE='Opus', DISCSUBTITLE='Opus Disc', LOOPSTART='48000', LOOPLENGTH='96000'), ['-c:a', 'libopus', '-b:a', '64k'], 48000))

# 3. MP3 with ID3: disc names in TSST, album tags in TXXX frames.
mp3 = {'album': 'MP3 Album', 'album_artist': 'Nintendo', 'genre': 'Soundtrack', 'PLATFORM': 'Game Boy', 'RELEASECATEGORY': 'Third-Party Game Rip', 'FRANCHISE': 'Pokemon'}
for d, name in [(1, 'Red Side'), (2, 'Blue Side')]:
    jobs.append((f'{OUT}/MP3 Album/{d}-01 Track.mp3', 2, 600 + 100 * d, dict(mp3, disc=f'{d}/2', track='1', title=f'MP3 {d}', TSST=name), ['-c:a', 'libmp3lame', '-b:a', '96k', '-id3v2_version', '4'], 44100))

# 4. Big album: 950 tracks over 10 discs, every other track loops; each disc named.
for i in range(950):
    d, t = i // 95 + 1, i % 95 + 1
    tags = {'ALBUM': 'Big Album', 'ALBUMARTIST': 'Nintendo', 'DISCNUMBER': str(d), 'TRACKNUMBER': str(t), 'TITLE': f'Big {d}-{t}',
            'DISCSUBTITLE': f'Big Disc {d}', 'PLATFORM': 'Nintendo Switch'}
    if i % 2 == 0:
        tags.update(LOOPSTART='3200', LOOPLENGTH='12800')
    jobs.append((f'{OUT}/Big Album/Disc {d:02}/{t:03} - Big.flac', 0.5, 200 + i, tags, FLAC))

# 5. Control: no plugin tags at all.
jobs.append((f'{OUT}/Plain Album/01 - Plain.flac', 1, 700, {'ALBUM': 'Plain Album', 'ALBUMARTIST': 'Somebody', 'TRACKNUMBER': '1', 'TITLE': 'Plain'}, FLAC))

with cf.ThreadPoolExecutor(8) as ex:
    list(ex.map(lambda j: enc(*j), jobs))
print(len(jobs), 'files')
