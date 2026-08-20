import SwiftUI

struct ContentView: View {
    @EnvironmentObject var coordinator: UnlockCoordinator
    @StateObject private var ble = BLEPeripheralManager.shared
    @State private var showScanner = false

    var body: some View {
        let _ = coordinator.configRevision
        let cfg = AppConfig.current
        return NavigationView {
            Form {
                Section(header: Text("Computer")) {
                    HStack {
                        Text("Name")
                        Spacer()
                        Text(cfg.pcName ?? "Not paired")
                            .foregroundColor(.secondary)
                    }
                    HStack {
                        Text("BLE")
                        Spacer()
                        Text(ble.stateText)
                            .foregroundColor(.secondary)
                    }
                }
                Section(header: Text("Paired PCs")) {
                    if cfg.pairedPCs.isEmpty {
                        Text("No paired PCs yet").foregroundColor(.secondary)
                    }
                    ForEach(cfg.pairedPCs) { pc in
                        HStack {
                            Text(pc.name)
                            Spacer()
                            Text(pc.status).foregroundColor(.secondary)
                        }
                    }
                }
                if let session = coordinator.pendingOnlineSessionID {
                    Section(header: Text("Unlock request")) {
                        Text("A paired computer is requesting approval.")
                        Button("Unlock with Face ID") { Task { await coordinator.approveOnline(sessionID: session) } }
                            .disabled(coordinator.isBusy)
                        Button("Reject") { Task { await coordinator.rejectOnline(sessionID: session) } }
                            .foregroundColor(.red)
                    }
                }
                Section(header: Text("Pair / offline fallback")) {
                    Button("Scan QR") { showScanner = true }
                    Button("Restart Bluetooth advertising") { ble.startAdvertising() }
                }
                Section(header: Text("Status")) { Text(coordinator.lastMessage) }
            }
            .navigationTitle("FaceUnlock")
            .sheet(isPresented: $showScanner) {
                QRScannerView { code in
                    showScanner = false
                    Task {
                        if code.contains("faceunlock-pair-v1") {
                            await coordinator.pair(from: code)
                        } else if let data = code.data(using: .utf8),
                                  let p = try? JSONDecoder().decode(OfflineUnlockPayload.self, from: data) {
                            do {
                                try await coordinator.approveOfflineQR(p)
                                coordinator.lastMessage = "Face ID approved. Waiting for the PC over Bluetooth."
                            } catch {
                                coordinator.lastMessage = error.localizedDescription
                            }
                        } else {
                            coordinator.lastMessage = "Unknown QR"
                        }
                    }
                }
                .ignoresSafeArea()
            }
        }
    }
}
