// Feasibility spike: can Apple's AVAudioEngine loop LOOPSTART..LOOPEND sample-exactly?
// Same AVFAudio framework as iOS. Renders offline across the seam and compares with the source samples.
// Usage: swift LoopSpike.swift <file> <loopStart> <loopEnd> [audioDataOffset]
import AVFoundation
import Foundation

func fail(_ msg: String) -> Never { print("FAIL: \(msg)"); exit(1) }

let args = CommandLine.arguments
guard args.count >= 4, let ls = AVAudioFramePosition(args[2]), let le = AVAudioFramePosition(args[3]) else {
    fail("usage: LoopSpike <file> <loopStart> <loopEnd> [audioDataOffset]")
}
var url = URL(fileURLWithPath: args[1])
if args.count >= 5, let offset = Int(args[4]), offset > 0 {
    // Strip a leading ID3v2 tag, as the web player does.
    let data = try! Data(contentsOf: url).dropFirst(offset)
    url = FileManager.default.temporaryDirectory.appendingPathComponent("stripped.flac")
    try! Data(data).write(to: url)
}

// 1. Decode the whole file to PCM at its native rate.
let file: AVAudioFile
do { file = try AVAudioFile(forReading: url) } catch { fail("AVAudioFile can't open \(url.lastPathComponent): \(error)") }
let format = file.processingFormat
let total = AVAudioFrameCount(file.length)
guard let full = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: total) else { fail("alloc") }
try! file.read(into: full)
print("decoded \(url.lastPathComponent): \(format.sampleRate) Hz, \(format.channelCount) ch, \(full.frameLength) frames")

// 2. Split into intro [0, ls) and loop [ls, le) buffers.
func slice(_ from: AVAudioFramePosition, _ to: AVAudioFramePosition) -> AVAudioPCMBuffer {
    let n = AVAudioFrameCount(to - from)
    let b = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: n)!
    b.frameLength = n
    for ch in 0..<Int(format.channelCount) {
        memcpy(b.floatChannelData![ch], full.floatChannelData![ch].advanced(by: Int(from)), Int(n) * MemoryLayout<Float>.size)
    }
    return b
}
guard le <= AVAudioFramePosition(full.frameLength), ls < le else { fail("loop points out of range") }
let intro = slice(0, ls)
let loop = slice(ls, le)

// 3. Engine in offline manual-rendering mode at the native rate: intro once, then the loop buffer with .loops.
let engine = AVAudioEngine()
let player = AVAudioPlayerNode()
engine.attach(player)
engine.connect(player, to: engine.mainMixerNode, format: format)
try! engine.enableManualRenderingMode(.offline, format: format, maximumFrameCount: 4096)
try! engine.start()
player.scheduleBuffer(intro, at: nil, options: [], completionHandler: nil)
player.scheduleBuffer(loop, at: nil, options: .loops, completionHandler: nil)
player.play()

// Render past the first seam and most of a second pass, then compare every sample with the expected stream.
let loopLen = le - ls
let renderFrames = AVAudioFramePosition(ls + loopLen + 48000 * 2)
let out = AVAudioPCMBuffer(pcmFormat: engine.manualRenderingFormat, frameCapacity: 4096)!
var pos: AVAudioFramePosition = 0
var maxErr: Float = 0
var firstBad: AVAudioFramePosition = -1
var seamChecked = 0
while pos < renderFrames {
    let n = AVAudioFrameCount(min(4096, renderFrames - pos))
    let status = try! engine.renderOffline(n, to: out)
    guard status == .success else { fail("render status \(status.rawValue)") }
    for i in 0..<Int(out.frameLength) {
        let p = pos + AVAudioFramePosition(i)
        let src = p < le ? p : ls + (p - le) % loopLen
        for ch in 0..<Int(format.channelCount) {
            let e = abs(out.floatChannelData![ch][i] - full.floatChannelData![ch][Int(src)])
            if e > maxErr { maxErr = e }
            if e > 1e-6 && firstBad < 0 { firstBad = p }
        }
        if abs(p - le) < 2048 { seamChecked += 1 }
    }
    pos += AVAudioFramePosition(out.frameLength)
}
engine.stop()
print("rendered \(pos) frames through the seam at \(le) (\(seamChecked) frames within ±2048 of it)")
print(maxErr == 0 ? "PASS: sample-exact (max error 0)" : (firstBad < 0 ? "PASS: max error \(maxErr)" : "FAIL: max error \(maxErr), first mismatch at frame \(firstBad) (seam \(le))"))
