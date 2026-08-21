import SwiftUI
import UniformTypeIdentifiers
import UIKit

struct PCControlView: View {
    let pc: PairedPC

    @State private var status = "Ready"
    @State private var cpu: Double?
    @State private var ram: Double?
    @State private var temp: Double?
    @State private var apps: [RemoteApp] = []
    @State private var clipboard = ""
    @State private var image: UIImage?
    @State private var showPicker = false
    @State private var shareURL: URL?
    @State private var showShareSheet = false

    var body: some View {
        Form {
            Section(header: Text("Control")) {
                Button("Lock PC") { run("lock") }
                Button("Restart PC") { run("restart") }.foregroundColor(.orange)
                Button("Shutdown PC") { run("shutdown") }.foregroundColor(.red)
            }

            Section(header: Text("PC status")) {
                Button("Refresh CPU / RAM / temperature") { run("status") }
                if let cpu { Text("CPU: \(cpu, specifier: "%.1f")%") }
                if let ram { Text("RAM: \(ram, specifier: "%.1f")%") }
                if let temp { Text("Temperature: \(temp, specifier: "%.1f")°C") }
            }

            Section(header: Text("Screenshot")) {
                Button("Capture screen") { run("screenshot") }
                if let image {
                    Image(uiImage: image)
                        .resizable()
                        .scaledToFit()
                }
            }

            Section(header: Text("Applications")) {
                Button("Refresh applications") { run("apps") }

                if apps.isEmpty {
                    Text("Tap Refresh applications to load visible apps from the PC.")
                        .font(.caption)
                        .foregroundColor(.secondary)
                }

                ForEach(apps) { app in
                    HStack {
                        VStack(alignment: .leading, spacing: 3) {
                            Text(app.name)
                            if !app.title.isEmpty {
                                Text(app.title)
                                    .font(.caption)
                                    .foregroundColor(.secondary)
                                    .lineLimit(2)
                            }
                        }
                        Spacer()
                        Button("Close") {
                            run("close_app", ["pid": "\(app.id)"])
                        }
                        .buttonStyle(.borderless)
                    }
                }
            }

            Section(header: Text("Clipboard")) {
                TextField("Text", text: $clipboard)
                Button("Send iPhone → PC") { run("clipboard_set", ["text": clipboard]) }
                Button("Get PC → iPhone") { run("clipboard_get") }
                Button("Copy received text") {
                    UIPasteboard.general.string = clipboard
                    status = "Copied to iPhone clipboard"
                }
            }

            Section(header: Text("Files")) {
                Button("Send file iPhone → PC") { showPicker = true }
                Button("Get copied file PC → iPhone") { run("clipboard_file_download") }
                Text("For PC → iPhone, copy a file in Windows Explorer first. FaceUnlock will fetch that copied file and open the iOS save/share sheet.")
                    .font(.caption)
                    .foregroundColor(.secondary)
            }

            Section(header: Text("Status")) {
                Text(status)
            }
        }
        .navigationTitle(pc.name)
        .fileImporter(
            isPresented: $showPicker,
            allowedContentTypes: [.data],
            allowsMultipleSelection: false
        ) { result in
            switch result {
            case .success(let urls):
                guard let url = urls.first else {
                    status = "No file selected"
                    return
                }
                sendFile(url)
            case .failure(let error):
                status = error.localizedDescription
            }
        }
        .sheet(isPresented: $showShareSheet, onDismiss: {
            shareURL = nil
        }) {
            if let shareURL {
                ShareSheet(items: [shareURL])
            }
        }
    }

    private func run(_ type: String, _ payload: [String: String]? = nil) {
        Task { @MainActor in
            status = "Working…"
            do {
                let timeout: TimeInterval = (type == "screenshot" || type == "clipboard_file_download") ? 60 : 30
                let response = try await APIClient.shared.runRemote(
                    pcID: pc.id,
                    type: type,
                    payload: payload,
                    timeout: timeout
                )

                if response.status == "ERROR" {
                    status = response.result?.error ?? "Command failed"
                    return
                }

                let result = response.result
                switch type {
                case "status":
                    cpu = result?.cpu_percent
                    ram = result?.ram_percent
                    temp = result?.temperature_c
                    status = "Status updated"

                case "apps":
                    apps = result?.apps ?? []
                    status = apps.isEmpty ? "No visible applications found" : "Loaded \(apps.count) applications"

                case "close_app":
                    if let pidText = payload?["pid"], let pid = Int(pidText) {
                        apps.removeAll { $0.id == pid }
                    }
                    status = "Application closed"

                case "clipboard_get":
                    if result?.available == true {
                        clipboard = result?.text ?? ""
                        status = "Clipboard received"
                    } else {
                        status = "No PC clipboard text available"
                    }

                case "clipboard_set":
                    status = "Clipboard sent to PC"

                case "screenshot":
                    guard result?.available != false else {
                        status = result?.error ?? "Screenshot is unavailable"
                        return
                    }
                    guard let b64 = result?.base64,
                          let data = Data(base64Encoded: b64),
                          let decoded = UIImage(data: data) else {
                        status = "Screenshot data is invalid"
                        return
                    }
                    image = decoded
                    status = "Screenshot updated"

                case "clipboard_file_download":
                    guard result?.available == true else {
                        status = "No copied file found on PC. Copy a file in Windows Explorer first."
                        return
                    }
                    guard let b64 = result?.base64, let data = Data(base64Encoded: b64) else {
                        status = "Downloaded file data is invalid"
                        return
                    }
                    let rawName = result?.name ?? "FaceUnlock-file"
                    let safeName = URL(fileURLWithPath: rawName).lastPathComponent
                    let directory = FileManager.default.temporaryDirectory
                        .appendingPathComponent("FaceUnlockDownloads", isDirectory: true)
                    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
                    let url = directory.appendingPathComponent(safeName)
                    try? FileManager.default.removeItem(at: url)
                    try data.write(to: url, options: .atomic)
                    shareURL = url
                    showShareSheet = true
                    status = "File ready to save: \(safeName)"

                case "lock":
                    status = result?.locked == true ? "PC locked" : "Lock command completed"

                case "restart":
                    status = "Restart command sent"

                case "shutdown":
                    status = "Shutdown command sent"

                default:
                    status = "Done"
                }
            } catch {
                status = error.localizedDescription
            }
        }
    }

    private func sendFile(_ url: URL) {
        Task { @MainActor in
            status = "Reading file…"
            do {
                let access = url.startAccessingSecurityScopedResource()
                defer { if access { url.stopAccessingSecurityScopedResource() } }

                let values = try url.resourceValues(forKeys: [.isRegularFileKey, .fileSizeKey])
                guard values.isRegularFile == true else {
                    status = "Please select a file, not a folder"
                    return
                }

                if let fileSize = values.fileSize, fileSize > 8 * 1024 * 1024 {
                    status = "File exceeds 8 MB remote relay limit"
                    return
                }

                let data = try Data(contentsOf: url)
                guard data.count <= 8 * 1024 * 1024 else {
                    status = "File exceeds 8 MB remote relay limit"
                    return
                }

                let response = try await APIClient.shared.runRemote(
                    pcID: pc.id,
                    type: "file_upload",
                    payload: [
                        "name": url.lastPathComponent,
                        "base64": data.base64EncodedString()
                    ],
                    timeout: 90
                )

                if response.status == "ERROR" {
                    status = response.result?.error ?? "File transfer failed"
                    return
                }

                status = "File sent to PC: \(url.lastPathComponent)"
            } catch {
                status = error.localizedDescription
            }
        }
    }
}

struct ShareSheet: UIViewControllerRepresentable {
    let items: [Any]

    func makeUIViewController(context: Context) -> UIActivityViewController {
        UIActivityViewController(activityItems: items, applicationActivities: nil)
    }

    func updateUIViewController(_ uiViewController: UIActivityViewController, context: Context) { }
}
