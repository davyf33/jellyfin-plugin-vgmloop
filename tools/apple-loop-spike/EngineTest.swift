// Offline tests for VgmLoopEngineCore (the Finamp iOS loop engine). Build together with the core:
//   swiftc -O -o /tmp/EngineTest tools/apple-loop-spike/EngineTest.swift <finamp>/ios/Runner/VgmLoopEngineCore.swift
//   /tmp/EngineTest fixtures/wii_menu.flac 197319 2349639
// Renders with AVAudioEngine manual (offline) rendering at the file's native rate and checks every output sample
// against the expected stream, plus the engine's position mapping.
import AVFoundation
import Foundation

var failures = 0
func check(_ name: String, _ ok: Bool, _ detail: String = "") {
    print((ok ? "PASS " : "FAIL ") + name + (detail.isEmpty ? "" : "  " + detail))
    if !ok { failures += 1 }
}

let args = CommandLine.arguments
guard args.count >= 4, let LS = AVAudioFramePosition(args[2]), let LE = AVAudioFramePosition(args[3]) else {
    print("usage: EngineTest <file> <loopStart> <loopEnd>"); exit(2)
}
let url = URL(fileURLWithPath: args[1])

/// Builds an engine in offline mode, runs `script` at given render frames, returns rendered left-channel samples.
func run(start: AVAudioFramePosition, looping: Bool, frames: AVAudioFramePosition,
         events: [AVAudioFramePosition: (VgmLoopEngineCore) -> Void] = [:],
         probe: ((AVAudioFramePosition, VgmLoopEngineCore) -> Void)? = nil) throws -> (out: [Float], core: VgmLoopEngineCore, ended: AVAudioFramePosition?) {
    let engine = AVAudioEngine()
    let core = try VgmLoopEngineCore(url: url, loopStart: LS, loopEnd: LE, engine: engine)
    try engine.enableManualRenderingMode(.offline, format: core.buffer.format, maximumFrameCount: 512)
    var rendered: AVAudioFramePosition = 0
    core.clockOverride = { rendered }
    core.completionType = .dataRendered // offline rendering never "plays back"
    let endedLock = NSLock()
    var endedAt: AVAudioFramePosition?
    core.onEnded = { endedLock.lock(); endedAt = rendered; endedLock.unlock() }
    core.schedule(from: start, looping: looping)
    try core.start()
    let outBuf = AVAudioPCMBuffer(pcmFormat: engine.manualRenderingFormat, frameCapacity: 512)!
    var out: [Float] = []
    out.reserveCapacity(Int(frames))
    let marks = events.keys.sorted()
    var nextMark = 0
    while rendered < frames {
        // Events fire on 512-frame boundaries (rendered frame counts).
        while nextMark < marks.count && marks[nextMark] <= rendered { events[marks[nextMark]]!(core); nextMark += 1 }
        probe?(rendered, core)
        let n = AVAudioFrameCount(min(512, frames - rendered))
        let st = try engine.renderOffline(n, to: outBuf)
        guard st == .success else { throw NSError(domain: "render", code: Int(st.rawValue)) }
        out.append(contentsOf: UnsafeBufferPointer(start: outBuf.floatChannelData![0], count: Int(outBuf.frameLength)))
        rendered += AVAudioFramePosition(outBuf.frameLength)
    }
    // Completion handlers arrive asynchronously; give them a moment.
    let deadline = Date().addingTimeInterval(1.0)
    while Date() < deadline { endedLock.lock(); let done = endedAt != nil; endedLock.unlock(); if done { break }; usleep(10_000) }
    endedLock.lock(); defer { endedLock.unlock() }
    return (out, core, endedAt)
}

func compare(_ out: [Float], _ expected: (Int) -> Float?, _ from: Int = 0) -> (maxErr: Float, firstBad: Int) {
    var maxErr: Float = 0, firstBad = -1
    for i in from..<out.count {
        let e = abs(out[i] - (expected(i) ?? 0))
        if e > maxErr { maxErr = e }
        if e > 1e-6 && firstBad < 0 { firstBad = i }
    }
    return (maxErr, firstBad)
}

do {
    let probe = try VgmLoopEngineCore(url: url, loopStart: LS, loopEnd: LE)
    let src = UnsafeBufferPointer(start: probe.buffer.floatChannelData![0], count: Int(probe.buffer.frameLength))
    let total = AVAudioFramePosition(probe.buffer.frameLength)
    let len = LE - LS
    let rate = probe.sampleRate
    print("file: \(Int(rate)) Hz, \(total) frames, loop \(LS)..\(LE)")
    probe.teardown()

    // 1. Looping from 1 s before the seam through two seams.
    do {
        let start = LE - AVAudioFramePosition(rate)
        let frames = AVAudioFramePosition(rate) + 2 * len + AVAudioFramePosition(rate)
        var posErrs = 0
        let r = try run(start: start, looping: true, frames: frames) { t, core in
            let expect = t < LE - start ? start + t : LS + (t - (LE - start)) % len
            if core.position(at: t) != expect { posErrs += 1 }
        }
        let c = compare(r.out) { i in
            let p = AVAudioFramePosition(i)
            let s = p < LE - start ? start + p : LS + (p - (LE - start)) % len
            return src[Int(s)]
        }
        check("looping through 2 seams is sample-exact", c.firstBad < 0, "maxErr \(c.maxErr)")
        check("position mapping while looping", posErrs == 0, "\(posErrs) mismatches")
        check("no end while looping", r.ended == nil)
        r.core.teardown()
    }

    // 2. Repeat One -> off in the middle of the second pass: pass finishes, outro follows, then ends.
    do {
        let start = LE - 1000
        let toggleAt = 1000 + len + len / 2 // halfway through the 2nd loop pass
        let outroStart = 1000 + 2 * len     // end of that pass
        let frames = outroStart + (total - LE) + 4096
        let r = try run(start: start, looping: true, frames: frames, events: [AVAudioFramePosition(toggleAt / 512 * 512): { $0.setLooping(false) }])
        let c = compare(r.out) { i in
            let p = AVAudioFramePosition(i)
            if p < 1000 { return src[Int(start + p)] }
            if p < outroStart { return src[Int(LS + (p - 1000) % len)] }
            let o = LE + (p - outroStart)
            return o < total ? src[Int(o)] : 0
        }
        check("loop off mid-pass: rest of pass + outro sample-exact", c.firstBad < 0, "maxErr \(c.maxErr) firstBad \(c.firstBad)")
        check("loop off: onEnded fired after the outro", r.ended != nil && r.ended! >= outroStart + (total - LE) - 512,
              "ended at \(String(describing: r.ended)), expected ~\(outroStart + (total - LE))")
        check("loop off: position runs into the outro", r.core.position(at: outroStart + 10) == min(LE + 10, total))
        r.core.teardown()
    }

    // 3. Not looping from the start: plays to the end and fires onEnded.
    do {
        let start = total - AVAudioFramePosition(rate) / 2
        let r = try run(start: start, looping: false, frames: AVAudioFramePosition(rate))
        let c = compare(r.out) { i in let p = start + AVAudioFramePosition(i); return p < total ? src[Int(p)] : 0 }
        check("linear play to the end is exact", c.firstBad < 0, "maxErr \(c.maxErr)")
        check("linear play fires onEnded", r.ended != nil)
        r.core.teardown()
    }

    // 4. Seek (restart) near the seam while looping keeps looping.
    do {
        let r = try run(start: 0, looping: true, frames: 8192 + 3000, events: [8192: { $0.seek(to: LE - 1000) }])
        let c = compare(r.out, { i in
            let p = AVAudioFramePosition(i) - 8192
            return src[Int(p < 1000 ? LE - 1000 + p : LS + (p - 1000))]
        }, 8192)
        check("seek to just before the seam then wrap", c.firstBad < 0, "maxErr \(c.maxErr) firstBad \(c.firstBad)")
        r.core.teardown()
    }
} catch {
    print("FAIL exception: \(error)")
    failures += 1
}
print(failures == 0 ? "ALL PASS" : "\(failures) FAILED")
exit(failures == 0 ? 0 : 1)
