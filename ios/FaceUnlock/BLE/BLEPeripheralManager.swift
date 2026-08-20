import CoreBluetooth
import Foundation

@MainActor
final class BLEPeripheralManager: NSObject, ObservableObject, CBPeripheralManagerDelegate {
    static let shared = BLEPeripheralManager()

    static let serviceUUID = CBUUID(string: "7A6AF110-8D20-4C5F-BB31-6CECF28F0110")
    static let requestUUID = CBUUID(string: "7A6AF111-8D20-4C5F-BB31-6CECF28F0110")
    static let responseUUID = CBUUID(string: "7A6AF112-8D20-4C5F-BB31-6CECF28F0110")
    static let deviceUUID = CBUUID(string: "7A6AF113-8D20-4C5F-BB31-6CECF28F0110")

    @Published var stateText = "Starting"

    private var manager: CBPeripheralManager!
    private var requestCharacteristic: CBMutableCharacteristic!
    private var responseCharacteristic: CBMutableCharacteristic!
    private var deviceCharacteristic: CBMutableCharacteristic!
    private var requestAssemblers: [UUID: BLEFrameAssembler] = [:]
    private var lastResponse = Data()

    private struct PendingNotification {
        let data: Data
        let central: CBCentral?
    }
    private var pendingNotifications: [PendingNotification] = []

    override init() {
        super.init()
        manager = CBPeripheralManager(delegate: self, queue: nil,
            options: [CBPeripheralManagerOptionRestoreIdentifierKey: "io.faceunlock.ble"])
    }

    func peripheralManagerDidUpdateState(_ peripheral: CBPeripheralManager) {
        switch peripheral.state {
        case .poweredOn:
            stateText = "Preparing Bluetooth"
            configureService()
        case .poweredOff:
            stateText = "Bluetooth Off"
            requestAssemblers.removeAll()
            pendingNotifications.removeAll()
        case .unauthorized:
            stateText = "Bluetooth Permission Required"
        case .unsupported:
            stateText = "Bluetooth Unsupported"
        default:
            stateText = "Bluetooth unavailable"
        }
    }

    private func configureService() {
        requestAssemblers.removeAll()
        pendingNotifications.removeAll()
        lastResponse = Data()

        requestCharacteristic = CBMutableCharacteristic(type: Self.requestUUID, properties: [.write], value: nil, permissions: [.writeable])
        responseCharacteristic = CBMutableCharacteristic(type: Self.responseUUID, properties: [.read, .notify], value: nil, permissions: [.readable])
        deviceCharacteristic = CBMutableCharacteristic(type: Self.deviceUUID, properties: [.read], value: nil, permissions: [.readable])

        let service = CBMutableService(type: Self.serviceUUID, primary: true)
        service.characteristics = [requestCharacteristic, responseCharacteristic, deviceCharacteristic]
        manager.removeAllServices()
        manager.add(service)
    }

    func peripheralManager(_ peripheral: CBPeripheralManager, didAdd service: CBService, error: Error?) {
        guard error == nil else {
            stateText = error!.localizedDescription
            return
        }
        startAdvertising()
    }

    func startAdvertising() {
        guard manager.state == .poweredOn else {
            stateText = "Bluetooth unavailable"
            return
        }
        if manager.isAdvertising { manager.stopAdvertising() }
        manager.startAdvertising([
            CBAdvertisementDataServiceUUIDsKey: [Self.serviceUUID],
            CBAdvertisementDataLocalNameKey: "FaceUnlock"
        ])
        stateText = "Bluetooth Ready"
    }

    func peripheralManager(_ peripheral: CBPeripheralManager, didReceiveRead request: CBATTRequest) {
        let value: Data
        switch request.characteristic.uuid {
        case Self.deviceUUID: value = currentDeviceIDData()
        case Self.responseUUID: value = lastResponse
        default:
            peripheral.respond(to: request, withResult: .attributeNotFound)
            return
        }

        guard request.offset >= 0, request.offset <= value.count else {
            peripheral.respond(to: request, withResult: .invalidOffset)
            return
        }
        request.value = value.subdata(in: request.offset..<value.count)
        peripheral.respond(to: request, withResult: .success)
    }

    func peripheralManager(_ peripheral: CBPeripheralManager, didReceiveWrite requests: [CBATTRequest]) {
        for req in requests where req.characteristic.uuid == Self.requestUUID {
            guard let data = req.value, !data.isEmpty else {
                peripheral.respond(to: req, withResult: .invalidAttributeValueLength)
                continue
            }

            let centralID = req.central.identifier
            let assembler = requestAssembler(for: centralID)
            switch assembler.ingest(data, expectedKind: .request) {
            case .waiting:
                peripheral.respond(to: req, withResult: .success)

            case .invalid:
                requestAssemblers.removeValue(forKey: centralID)
                peripheral.respond(to: req, withResult: .invalidPdu)

            case .complete(let requestData, let framed):
                // A completed transaction must not leak framing state into the
                // next request from the same Windows central.
                requestAssemblers.removeValue(forKey: centralID)
                peripheral.respond(to: req, withResult: .success)
                let central = req.central
                Task { @MainActor [weak self] in
                    guard let self else { return }
                    do {
                        let response = try await UnlockCoordinator.shared.handleOfflineBLERequest(requestData)
                        self.lastResponse = response
                        self.enqueueResponse(response, central: central, framed: framed)
                    } catch {
                        let response = self.makeErrorResponse(error)
                        self.lastResponse = response
                        self.enqueueResponse(response, central: central, framed: framed)
                    }
                }
            }
        }
    }

    func peripheralManagerIsReady(toUpdateSubscribers peripheral: CBPeripheralManager) {
        flushNotificationQueue()
    }

    func peripheralManager(_ peripheral: CBPeripheralManager, central: CBCentral, didUnsubscribeFrom characteristic: CBCharacteristic) {
        requestAssemblers.removeValue(forKey: central.identifier)
        pendingNotifications.removeAll { $0.central?.identifier == central.identifier }
    }

    func peripheralManager(_ peripheral: CBPeripheralManager, willRestoreState dict: [String: Any]) {
        stateText = peripheral.state == .poweredOn ? "Bluetooth Restored" : "Restoring Bluetooth"
        if peripheral.state == .poweredOn { configureService() }
    }

    private func requestAssembler(for centralID: UUID) -> BLEFrameAssembler {
        if let existing = requestAssemblers[centralID] { return existing }
        let assembler = BLEFrameAssembler()
        requestAssemblers[centralID] = assembler
        return assembler
    }

    private func enqueueResponse(_ response: Data, central: CBCentral?, framed: Bool) {
        if framed {
            do {
                let maxUpdate = central?.maximumUpdateValueLength ?? BLEFrameCodec.minimumFrameSize
                let frameBytes = min(max(BLEFrameCodec.minimumFrameSize, maxUpdate), 180)
                let frames = try BLEFrameCodec.encode(response, kind: .response, maximumFrameBytes: frameBytes)
                pendingNotifications.append(contentsOf: frames.map { PendingNotification(data: $0, central: central) })
            } catch {
                pendingNotifications.append(PendingNotification(data: makeErrorResponse(error), central: central))
            }
        } else {
            pendingNotifications.append(PendingNotification(data: response, central: central))
        }
        flushNotificationQueue()
    }

    private func flushNotificationQueue() {
        guard responseCharacteristic != nil else { return }
        while let next = pendingNotifications.first {
            let centrals: [CBCentral]? = next.central.map { [$0] }
            let accepted = manager.updateValue(next.data, for: responseCharacteristic, onSubscribedCentrals: centrals)
            if !accepted { return }
            pendingNotifications.removeFirst()
        }
    }

    private func currentDeviceIDData() -> Data {
        (AppConfig.current.deviceID ?? "unpaired").data(using: .utf8) ?? Data()
    }

    private func makeErrorResponse(_ error: Error) -> Data {
        struct BLEErrorResponse: Encodable { let ok: String; let error: String }
        let message = String(error.localizedDescription.prefix(512))
        return (try? JSONEncoder().encode(BLEErrorResponse(ok: "false", error: message)))
            ?? Data(#"{"ok":"false","error":"BLE error"}"#.utf8)
    }
}
