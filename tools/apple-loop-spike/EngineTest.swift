// Offline tests for VgmLoopEngineCore (the Finamp iOS loop engine). Build together with the core:
//   mkdir -p /tmp/et && cp tools/apple-loop-spike/EngineTest.swift /tmp/et/main.swift
//   swiftc -O -o /tmp/EngineTest /tmp/et/main.swift <finamp>/ios/Runner/VgmLoopEngineCore.swift
//   /tmp/EngineTest fixtures/wii_menu.flac 197319 2349639
// Renders with AVAudioEngine manual (offline) rendering at the file's native rate and checks every output sample
// against the expected stream, plus the engine's position mapping and end detection.
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
let decoded = try! VgmLoopEngineCore.decode(url: URL(fileURLWithPath: args[1]))

/// Renders `frames` offline; `events` run at render-block boundaries. Returns left-channel samples.
func run(start: AVAudioFramePosition, looping: Bool, frames: AVAudioFramePosition,
         events: [AVAudioFramePosition: (VgmLoopEngineCore) -> Void] = [:],
         probe: ((AVAudioFramePosition, VgmLoopEngineCore) -> Void)? = nil) throws -> (out: [Float], core: VgmLoopEngineCore, ended: AVAudioFramePosition?) {
    let engine = AVAudioEngine()
    let player = AVAudioPlayerNode()
    engine.attach(player)
    let core = try VgmLoopEngineCore(buffer: decoded, loopStart: LS, loopEnd: LE, engine: engine, player: player)
    try engine.enableManualRenderingMode(.offline, format: core.buffer.format, maximumFrameCount: 512)
    var rendered: AVAudioFramePosition = 0
    core.clockOverride = { rendered }
    core.completionType = .dataRendered // offline rendering never "plays back"
    var endedAt: AVAudioFramePosition?
    core.schedule(from: start, looping: looping)
    try core.start()
    let outBuf = AVAudioPCMBuffer(pcmFormat: engine.manualRenderingFormat, frameCapacity: 512)!
    var out: [Float] = []
    out.reserveCapacity(Int(frames))
    let marks = events.keys.sorted()
    var nextMark = 0
    while rendered < frames {
        while nextMark < marks.count && marks[nextMark] <= rendered { events[marks[nextMark]]!(core); nextMark += 1 }
        core.tick()
        probe?(rendered, core)
        if endedAt == nil && core.ended { endedAt = rendered }
        let n = AVAudioFrameCount(min(512, frames - rendered))
        let st = try engine.renderOffline(n, to: outBuf)
        guard st == .success else { throw NSError(domain: "render", code: Int(st.rawValue)) }
        out.append(contentsOf: UnsafeBufferPointer(start: outBuf.floatChannelData![0], count: Int(outBuf.frameLength)))
        rendered += AVAudioFramePosition(outBuf.frameLength)
    }
    if endedAt == nil && core.ended { endedAt = rendered }
    return (out, core, endedAt)
}

func compare(_ out: [Float], from: Int = 0, _ expected: (Int) -> Float) -> (maxErr: Float, firstBad: Int) {
    var maxErr: Float = 0, firstBad = -1
    for i in from..<out.count {
        let e = abs(out[i] - expected(i))
        if e > maxErr { maxErr = e }
        if e > 1e-6 && firstBad < 0 { firstBad = i }
    }
    return (maxErr, firstBad)
}

do {
    let src = UnsafeBufferPointer(start: decoded.floatChannelData![0], count: Int(decoded.frameLength))
    let total = AVAudioFramePosition(decoded.frameLength)
    let len = LE - LS
    let rate = decoded.format.sampleRate
    let lead = AVAudioFramePosition(VgmLoopEngineCore.followUpLead * rate)
    let s = { (p: AVAudioFramePosition) -> Float in p < total ? src[Int(p)] : 0 }
    print("file: \(Int(rate)) Hz, \(total) frames, loop \(LS)..\(LE)")

    // 1. Looping from 1 s before the seam through two seams.
    do {
        let start = LE - AVAudioFramePosition(rate)
        let first = LE - start
        var posErrs = 0
        let r = try run(start: start, looping: true, frames: first + 2 * len + AVAudioFramePosition(rate)) { t, core in
            let expect = t < first ? start + t : LS + (t - first) % len
            if core.position(at: t) != expect { posErrs += 1 }
        }
        let c = compare(r.out) { i in let p = AVAudioFramePosition(i); return s(p < first ? start + p : LS + (p - first) % len) }
        check("looping through 2 seams is sample-exact", c.firstBad < 0, "maxErr \(c.maxErr)")
        check("position mapping while looping", posErrs == 0, "\(posErrs) mismatches")
        check("no end while looping", r.ended == nil)
    }

    // 2. Looping off in the middle of the second pass: pass finishes, outro follows, then ended.
    do {
        let start = LE - 1000
        let outroStart = 1000 + 2 * len
        let frames = outroStart + (total - LE) + 4096
        let r = try run(start: start, looping: true, frames: frames, events: [(1000 + len + len / 2) / 512 * 512: { $0.setLooping(false) }])
        let c = compare(r.out) { i in
            let p = AVAudioFramePosition(i)
            if p < 1000 { return s(start + p) }
            if p < outroStart { return s(LS + (p - 1000) % len) }
            return s(LE + (p - outroStart))
        }
        check("loop off mid-pass: rest of pass + outro sample-exact", c.firstBad < 0, "maxErr \(c.maxErr) firstBad \(c.firstBad)")
        check("loop off: ended after the outro", r.ended != nil && r.ended! >= outroStart + (total - LE) - 512, "\(String(describing: r.ended))")
    }

    // 3. Looping off during the first pass (before the follow-up is committed): no gap, outro follows the first pass.
    do {
        let start: AVAudioFramePosition = 0
        let toggle = (LE - lead) / 2 / 512 * 512
        let frames = LE + (total - LE) + 4096
        let r = try run(start: start, looping: true, frames: frames, events: [toggle: { $0.setLooping(false) }])
        let c = compare(r.out) { i in s(AVAudioFramePosition(i)) } // linear: intro, first pass, outro
        check("loop off during first pass: plays straight through, no gap", c.firstBad < 0, "maxErr \(c.maxErr) firstBad \(c.firstBad)")
        check("loop off during first pass: ended at the file end", r.ended != nil && r.ended! >= total - 512, "\(String(describing: r.ended))")
    }

    // 4. Looping on during the first pass after starting without looping: loops at the seam, no gap.
    do {
        let start: AVAudioFramePosition = LE - AVAudioFramePosition(rate) * 5
        let first = LE - start
        let r = try run(start: start, looping: false, frames: first + len / 2, events: [AVAudioFramePosition(rate) / 512 * 512: { $0.setLooping(true) }])
        let c = compare(r.out) { i in let p = AVAudioFramePosition(i); return s(p < first ? start + p : LS + (p - first)) }
        check("loop on during first pass: loops seamlessly", c.firstBad < 0, "maxErr \(c.maxErr) firstBad \(c.firstBad)")
        check("loop on during first pass: no end", r.ended == nil)
    }

    // 5. Starting inside the outro (past loopEnd) plays to the end and ends.
    if LE < total - AVAudioFramePosition(rate) / 2 {
        let start = total - AVAudioFramePosition(rate) / 2
        let r = try run(start: start, looping: true, frames: AVAudioFramePosition(rate))
        let c = compare(r.out) { i in s(start + AVAudioFramePosition(i)) }
        check("outro start: linear to the end", c.firstBad < 0, "maxErr \(c.maxErr)")
        check("outro start: ended", r.ended != nil)
    }

    // 6. Seek (restart) to just before the seam while looping keeps looping.
    do {
        let r = try run(start: 0, looping: true, frames: 8192 + 3000, events: [8192: { $0.restart(at: LE - 1000, looping: true) }])
        let c = compare(r.out, from: 8192) { i in
            let p = AVAudioFramePosition(i) - 8192
            return s(p < 1000 ? LE - 1000 + p : LS + (p - 1000))
        }
        check("seek to just before the seam then wrap", c.firstBad < 0, "maxErr \(c.maxErr) firstBad \(c.firstBad)")
    }
} catch {
    print("FAIL exception: \(error)")
    failures += 1
}
print(failures == 0 ? "ALL PASS" : "\(failures) FAILED")
exit(failures == 0 ? 0 : 1)
