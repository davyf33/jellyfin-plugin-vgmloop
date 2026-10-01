# AVAudioEngine loop spike

Checks that Apple's audio stack (AVFAudio, shared by macOS and iOS) can loop `LOOPSTART..LOOPEND` sample-exactly,
as groundwork for native-app support (e.g. Finamp on iOS). It decodes the file with `AVAudioFile`, schedules the intro
once and the loop section with `.loops` on an `AVAudioPlayerNode`, renders offline across the seam and compares every
sample with the source.

```bash
swiftc -O -o /tmp/LoopSpike tools/apple-loop-spike/LoopSpike.swift
/tmp/LoopSpike file.flac 197319 2349639
```

Results on macOS 26.5 (fixtures from the test suite):

| Input | Decoded frames | Seam |
|---|---|---|
| FLAC 32 kHz | 2349639 | exact (max error 0) |
| FLAC with ID3v2 prefix (no stripping needed) | 2349639 | exact |
| Ogg Vorbis 32 kHz (CoreAudio reads Ogg natively) | 2349639 | exact |
| Ogg Opus 48 kHz (pre-skip 312 trimmed) | 3524459 | exact |

Jellyfin's on-the-fly FLAC transcode (`/Audio/{id}/stream.flac?audioCodec=flac`) of the Ogg files is also
sample-exact (same frame count, offset 0, 24-bit), but it is streamed without a total-samples field, so `AVAudioFile`
reports length 0; a fallback using it would have to read until EOF.
