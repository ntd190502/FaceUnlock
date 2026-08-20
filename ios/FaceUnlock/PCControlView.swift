import SwiftUI
import UniformTypeIdentifiers

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

    var body: some View {
        Form {
            Section(header: Text("Control")) {
                Button("Lock PC") { run("lock") }
                Button("Restart PC") { run("restart") }.foregroundColor(.orange)
                Button("Shutdown PC") { run("shutdown") }.foregroundColor(.red)
            }
            Section(header: Text("PC status")) {
                Button("Refresh CPU / RAM / temperature") { run("status") }
                if let cpu = cpu { Text("CPU: \(cpu, specifier: "%.1f")%") }
                if let ram = ram { Text("RAM: \(ram, specifier: "%.1f")%") }
                if let temp = temp { Text("Temperature: \(temp, specifier: "%.1f")°C") }
            }
            Section(header: Text("Screenshot")) {
                Button("Capture screen") { run("screenshot") }
                if let image = image { Image(uiImage: image).resizable().scaledToFit() }
            }
            Section(header: Text("Applications")) {
                Button("Refresh applications") { run("apps") }
                ForEach(apps) { app in
                    HStack {
                        VStack(alignment: .leading) {
                            Text(app.name)
                            if !app.title.isEmpty { Text(app.title).font(.caption).foregroundColor(.secondary) }
                        }
                        Spacer()
                        Button("Close") { run("close_app", ["pid": "\(app.id)"]) }
                    }
                }
            }
            Section(header: Text("Clipboard")) {
                TextField("Text", text: $clipboard)
                Button("Send iPhone → PC") { run("clipboard_set", ["text": clipboard]) }
                Button("Get PC → iPhone") { run("clipboard_get") }
                Button("Copy received text") { UIPasteboard.general.string = clipboard }
            }
            Section(header: Text("Files")) {
                Button("Send file iPhone → PC") { showPicker = true }
                Button("Get copied file PC → iPhone") { run("clipboard_file_download") }
            }
            Section(header: Text("Status")) { Text(status) }
        }
        .navigationTitle(pc.name)
        .sheet(isPresented: $showPicker) { DocumentPicker { sendFile($0) } }
    }

    private func run(_ type: String, _ payload: [String: String]? = nil) {
        Task { @MainActor in
            status = "Working…"
            do {
                let response = try await APIClient.shared.runRemote(pcID: pc.id, type: type, payload: payload, timeout: type == "screenshot" ? 45 : 30)
                if response.status == "ERROR" { status = response.result?.error ?? "Command failed"; return }
                let result = response.result
                if type == "status" { cpu = result?.cpu_percent; ram = result?.ram_percent; temp = result?.temperature_c }
                if let list = result?.apps { apps = list }
                if type == "clipboard_get", let text = result?.text { clipboard = text }
                if type == "screenshot", let b64 = result?.base64, let data = Data(base64Encoded: b64) { image = UIImage(data: data) }
                if type == "clipboard_file_download", result?.available == true, let b64 = result?.base64, let data = Data(base64Encoded: b64) {
                    let name = result?.name ?? "FaceUnlock-file"
                    let url = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0].appendingPathComponent(name)
                    try data.write(to: url)
                    status = "Saved to FaceUnlock Documents: \(name)"
                } else { status = "Done" }
            } catch { status = error.localizedDescription }
        }
    }

    private func sendFile(_ url: URL) {
        Task { @MainActor in
            do {
                let access = url.startAccessingSecurityScopedResource()
                defer { if access { url.stopAccessingSecurityScopedResource() } }
                let data = try Data(contentsOf: url)
                guard data.count <= 8 * 1024 * 1024 else { status = "File exceeds 8 MB remote relay limit"; return }
                _ = try await APIClient.shared.runRemote(pcID: pc.id, type: "file_upload", payload: ["name": url.lastPathComponent, "base64": data.base64EncodedString()], timeout: 60)
                status = "File sent"
            } catch { status = error.localizedDescription }
        }
    }
}

struct DocumentPicker: UIViewControllerRepresentable {
    let onPick: (URL) -> Void
    func makeCoordinator() -> Coordinator { Coordinator(onPick) }
    func makeUIViewController(context: Context) -> UIDocumentPickerViewController {
        let picker = UIDocumentPickerViewController(forOpeningContentTypes: [.item], asCopy: false)
        picker.delegate = context.coordinator
        return picker
    }
    func updateUIViewController(_ uiViewController: UIDocumentPickerViewController, context: Context) {}
    final class Coordinator: NSObject, UIDocumentPickerDelegate {
        let onPick: (URL) -> Void
        init(_ onPick: @escaping (URL) -> Void) { self.onPick = onPick }
        func documentPicker(_ controller: UIDocumentPickerViewController, didPickDocumentsAt urls: [URL]) { if let url = urls.first { onPick(url) } }
    }
}
