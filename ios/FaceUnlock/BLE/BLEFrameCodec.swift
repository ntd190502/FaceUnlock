import Foundation

enum BLEFrameKind: UInt8 {
    case request = 1
    case response = 2
}

struct BLEFramePacket {
    let kind: BLEFrameKind
    let messageID: UInt16
    let chunkIndex: UInt16
    let chunkCount: UInt16
    let payload: Data
}

enum BLEFrameDecodeResult {
    case legacy(Data)
    case frame(BLEFramePacket)
    case invalid(String)
}

enum BLEAssemblyResult {
    case waiting
    case complete(Data, framed: Bool)
    case invalid(String)
}

/// Small application-level framing protocol for FaceUnlock BLE.
///
/// Frame layout (network byte order):
///   0..1  magic "FU"
///   2     high nibble = version (1), low nibble = kind (1=request, 2=response)
///   3..4  message id UInt16
///   5..6  chunk index UInt16 (zero based)
///   7..8  chunk count UInt16
///   9..   payload
///
/// The default frame size is 20 bytes so writes are valid even at the
/// minimum ATT MTU. Receivers also accept larger frames, which lets iOS use
/// CBCentral.maximumUpdateValueLength for more efficient response notifications.
enum BLEFrameCodec {
    static let headerSize = 9
    static let minimumFrameSize = 20
    static let maximumMessageBytes = 16 * 1024
    static let assemblyTimeout: TimeInterval = 15

    private static let magic0: UInt8 = 0x46 // F
    private static let magic1: UInt8 = 0x55 // U
    private static let version: UInt8 = 1

    static func encode(
        _ payload: Data,
        kind: BLEFrameKind,
        messageID: UInt16 = UInt16.random(in: 1...UInt16.max),
        maximumFrameBytes: Int = minimumFrameSize
    ) throws -> [Data] {
        guard payload.count <= maximumMessageBytes else {
            throw NSError(
                domain: "FaceUnlock.BLE",
                code: 40,
                userInfo: [NSLocalizedDescriptionKey: "BLE message exceeds \(maximumMessageBytes) bytes"]
            )
        }

        let frameBytes = max(minimumFrameSize, maximumFrameBytes)
        let payloadPerFrame = frameBytes - headerSize
        guard payloadPerFrame > 0 else {
            throw NSError(
                domain: "FaceUnlock.BLE",
                code: 41,
                userInfo: [NSLocalizedDescriptionKey: "Invalid BLE frame size"]
            )
        }

        let count = max(1, (payload.count + payloadPerFrame - 1) / payloadPerFrame)
        guard count <= Int(UInt16.max) else {
            throw NSError(
                domain: "FaceUnlock.BLE",
                code: 42,
                userInfo: [NSLocalizedDescriptionKey: "Too many BLE chunks"]
            )
        }

        var frames: [Data] = []
        frames.reserveCapacity(count)

        for index in 0..<count {
            let start = index * payloadPerFrame
            let end = min(payload.count, start + payloadPerFrame)
            let chunk = start < end ? payload.subdata(in: start..<end) : Data()

            var frame = Data()
            frame.reserveCapacity(headerSize + chunk.count)
            frame.append(magic0)
            frame.append(magic1)
            frame.append((version << 4) | (kind.rawValue & 0x0F))
            appendUInt16(messageID, to: &frame)
            appendUInt16(UInt16(index), to: &frame)
            appendUInt16(UInt16(count), to: &frame)
            frame.append(chunk)
            frames.append(frame)
        }

        return frames
    }

    static func decode(_ data: Data) -> BLEFrameDecodeResult {
        guard data.count >= 2 else {
            return .legacy(data)
        }

        guard data[0] == magic0, data[1] == magic1 else {
            // Backward compatibility with the pre-framing implementation.
            return .legacy(data)
        }

        guard data.count >= headerSize else {
            return .invalid("Truncated BLE frame")
        }

        let versionAndKind = data[2]
        let frameVersion = versionAndKind >> 4
        let kindRaw = versionAndKind & 0x0F

        guard frameVersion == version else {
            return .invalid("Unsupported BLE frame version \(frameVersion)")
        }
        guard let kind = BLEFrameKind(rawValue: kindRaw) else {
            return .invalid("Unknown BLE frame kind")
        }

        let messageID = readUInt16(data, offset: 3)
        let chunkIndex = readUInt16(data, offset: 5)
        let chunkCount = readUInt16(data, offset: 7)

        guard messageID != 0, chunkCount > 0, chunkIndex < chunkCount else {
            return .invalid("Invalid BLE frame indexes")
        }

        return .frame(
            BLEFramePacket(
                kind: kind,
                messageID: messageID,
                chunkIndex: chunkIndex,
                chunkCount: chunkCount,
                payload: data.subdata(in: headerSize..<data.count)
            )
        )
    }

    private static func appendUInt16(_ value: UInt16, to data: inout Data) {
        data.append(UInt8((value >> 8) & 0xFF))
        data.append(UInt8(value & 0xFF))
    }

    private static func readUInt16(_ data: Data, offset: Int) -> UInt16 {
        (UInt16(data[offset]) << 8) | UInt16(data[offset + 1])
    }
}

final class BLEFrameAssembler {
    private struct State {
        let kind: BLEFrameKind
        let chunkCount: UInt16
        let createdAt: Date
        var chunks: [UInt16: Data]
        var totalBytes: Int
    }

    private var states: [UInt16: State] = [:]

    func ingest(_ data: Data, expectedKind: BLEFrameKind) -> BLEAssemblyResult {
        cleanupExpired()

        switch BLEFrameCodec.decode(data) {
        case .legacy(let legacy):
            guard legacy.count <= BLEFrameCodec.maximumMessageBytes else {
                return .invalid("Legacy BLE message is too large")
            }
            return .complete(legacy, framed: false)

        case .invalid(let reason):
            return .invalid(reason)

        case .frame(let frame):
            guard frame.kind == expectedKind else {
                return .invalid("Unexpected BLE frame kind")
            }

            var state: State
            if let existing = states[frame.messageID] {
                guard existing.kind == frame.kind,
                      existing.chunkCount == frame.chunkCount else {
                    states.removeValue(forKey: frame.messageID)
                    return .invalid("BLE frame metadata changed mid-message")
                }
                state = existing
            } else {
                state = State(
                    kind: frame.kind,
                    chunkCount: frame.chunkCount,
                    createdAt: Date(),
                    chunks: [:],
                    totalBytes: 0
                )
            }

            if state.chunks[frame.chunkIndex] == nil {
                state.chunks[frame.chunkIndex] = frame.payload
                state.totalBytes += frame.payload.count
            }

            guard state.totalBytes <= BLEFrameCodec.maximumMessageBytes else {
                states.removeValue(forKey: frame.messageID)
                return .invalid("BLE message exceeds maximum size")
            }

            if state.chunks.count == Int(state.chunkCount) {
                var complete = Data()
                complete.reserveCapacity(state.totalBytes)
                for idx in UInt16(0)..<state.chunkCount {
                    guard let part = state.chunks[idx] else {
                        states[frame.messageID] = state
                        return .waiting
                    }
                    complete.append(part)
                }
                states.removeValue(forKey: frame.messageID)
                return .complete(complete, framed: true)
            }

            states[frame.messageID] = state
            return .waiting
        }
    }

    func reset() {
        states.removeAll()
    }

    private func cleanupExpired() {
        let cutoff = Date().addingTimeInterval(-BLEFrameCodec.assemblyTimeout)
        states = states.filter { $0.value.createdAt >= cutoff }
    }
}
