import Foundation

@main
struct BLEFrameSelfTest {
    static func main() throws {
        let payload = Data((0..<1024).map { UInt8($0 % 251) })
        let frames = try BLEFrameCodec.encode(
            payload,
            kind: .request,
            messageID: 0x1234,
            maximumFrameBytes: BLEFrameCodec.minimumFrameSize
        )

        precondition(frames.count > 1, "Expected multi-frame BLE payload")
        precondition(frames.allSatisfy { $0.count <= BLEFrameCodec.minimumFrameSize })

        let assembler = BLEFrameAssembler()
        var reassembled: Data?
        for frame in frames.reversed() {
            switch assembler.ingest(frame, expectedKind: .request) {
            case .complete(let data, let framed):
                precondition(framed)
                reassembled = data
            case .waiting:
                break
            case .invalid(let reason):
                fatalError("Unexpected invalid frame: \(reason)")
            }
        }
        precondition(reassembled == payload, "Out-of-order reassembly mismatch")

        let duplicateAssembler = BLEFrameAssembler()
        _ = duplicateAssembler.ingest(frames[0], expectedKind: .request)
        _ = duplicateAssembler.ingest(frames[0], expectedKind: .request)
        var duplicateResult: Data?
        for frame in frames.dropFirst() {
            if case .complete(let data, _) = duplicateAssembler.ingest(frame, expectedKind: .request) {
                duplicateResult = data
            }
        }
        precondition(duplicateResult == payload, "Duplicate-frame handling mismatch")

        let legacy = Data("legacy-faceunlock".utf8)
        if case .complete(let data, let framed) = BLEFrameAssembler().ingest(legacy, expectedKind: .request) {
            precondition(!framed && data == legacy)
        } else {
            fatalError("Legacy compatibility failed")
        }

        do {
            _ = try BLEFrameCodec.encode(
                Data(repeating: 0xAA, count: BLEFrameCodec.maximumMessageBytes + 1),
                kind: .request
            )
            fatalError("Oversized message was accepted")
        } catch {
            // Expected.
        }

        print("BLEFrameCodec Swift self-test PASS: \(frames.count) frames")
    }
}
