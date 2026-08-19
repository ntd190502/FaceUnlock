import SwiftUI

struct ContentView: View {
    @EnvironmentObject var coordinator: UnlockCoordinator
    @StateObject private var push = PushManager.shared
    @StateObject private var ble = BLEPeripheralManager.shared
    @State private var showScanner = false

    var body: some View {
        NavigationStack {
            Form {
                Section("Computer") {
                    LabeledContent("Name", value: AppConfig.current.pcName ?? "Not paired")
                    LabeledContent("BLE", value: ble.stateText)
                    LabeledContent("Push", value: push.tokenHex.isEmpty ? "Not registered" : "Registered")
                }
                if let session = coordinator.pendingOnlineSessionID {
                    Section("Unlock request") {
                        Text("A paired computer is requesting approval.")
                        Button("Unlock with Face ID") { Task { await coordinator.approveOnline(sessionID: session) } }
                            .disabled(coordinator.isBusy)
                        Button("Reject", role: .destructive) { Task { await coordinator.rejectOnline(sessionID: session) } }
                    }
                }
                Section("Pair / offline fallback") {
                    Button("Scan QR") { showScanner = true }
                    Button("Start Bluetooth advertising") { ble.startAdvertising() }
                }
                Section("Status") { Text(coordinator.lastMessage) }
            }
            .navigationTitle("FaceUnlock")
            .sheet(isPresented: $showScanner) {
                QRScannerView { code in
                    showScanner = false
                    Task {
                        if code.contains("faceunlock-pair-v1") { await coordinator.pair(from: code) }
                        else if let data = code.data(using: .utf8), let p = try? JSONDecoder().decode(OfflineUnlockPayload.self, from: data) {
                            do { try await coordinator.approveOfflineQR(p); coordinator.lastMessage = "Face ID approved. Waiting for the PC over Bluetooth." }
                            catch { coordinator.lastMessage = error.localizedDescription }
                        } else { coordinator.lastMessage = "Unknown QR" }
                    }
                }
                .ignoresSafeArea()
            }
        }
    }
}
