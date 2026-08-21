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
    @State private var showFileSources = false
    @State private var showFileImporter = false
    @State private var showMediaPicker = false
    @State private var mediaSource: UIImagePickerController.SourceType = .photoLibrary
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
                if let image { Image(uiImage: image).resizable().scaledToFit() }
            }
            Section(header: Text("Applications")) {
                Button("Refresh applications") { run("apps") }
                if apps.isEmpty { Text("Tap Refresh applications to load visible apps from the PC.").font(.caption).foregroundColor(.secondary) }
                ForEach(apps) { app in
                    HStack {
                        VStack(alignment: .leading, spacing: 3) { Text(app.name); if !app.title.isEmpty { Text(app.title).font(.caption).foregroundColor(.secondary).lineLimit(2) } }
                        Spacer(); Button("Close") { run("close_app", ["pid": "\(app.id)"]) }.buttonStyle(.borderless)
                    }
                }
            }
            Section(header: Text("Clipboard")) {
                TextField("Text", text: $clipboard)
                Button("Send iPhone → PC") { run("clipboard_set", ["text": clipboard]) }
                Button("Get PC → iPhone") { run("clipboard_get") }
                Button("Copy received text") { UIPasteboard.general.string = clipboard; status = "Copied to iPhone clipboard" }
            }
            Section(header: Text("Files")) {
                Button("Send file iPhone → PC") { showFileSources = true }
                Button("Get copied file PC → iPhone") { run("clipboard_file_download") }
                Text("Choose Photo Library, Camera, or Browse when sending from iPhone. For PC → iPhone, copy a file in Windows Explorer first.").font(.caption).foregroundColor(.secondary)
            }
            Section(header: Text("Status")) { Text(status) }
        }
        .navigationTitle(pc.name)
        .actionSheet(isPresented: $showFileSources) {
            var buttons: [ActionSheet.Button] = [
                .default(Text("Photo Library")) { mediaSource = .photoLibrary; showMediaPicker = true }
            ]
            if UIImagePickerController.isSourceTypeAvailable(.camera) {
                buttons.append(.default(Text("Take Photo or Video")) { mediaSource = .camera; showMediaPicker = true })
            }
            buttons.append(.default(Text("Browse")) { showFileImporter = true })
            buttons.append(.cancel())
            return ActionSheet(title: Text("Choose a source"), buttons: buttons)
        }
        .fileImporter(isPresented: $showFileImporter, allowedContentTypes: [.data], allowsMultipleSelection: false) { result in
            switch result { case .success(let urls): if let url = urls.first { sendFile(url) } else { status = "No file selected" }; case .failure(let error): status = error.localizedDescription }
        }
        .sheet(isPresented: $showMediaPicker) { MediaPicker(sourceType: mediaSource) { url in showMediaPicker = false; if let url { sendFile(url) } } }
        .sheet(isPresented: $showShareSheet, onDismiss: { shareURL = nil }) { if let shareURL { ShareSheet(items: [shareURL]) } }
    }

    private func run(_ type: String, _ payload: [String: String]? = nil) {
        Task { @MainActor in
            status = "Working…"
            do {
                let timeout: TimeInterval = (type == "screenshot" || type == "clipboard_file_download") ? 60 : 30
                let response = try await APIClient.shared.runRemote(pcID: pc.id, type: type, payload: payload, timeout: timeout)
                if response.status == "ERROR" { status = response.result?.error ?? "Command failed"; return }
                let result = response.result
                switch type {
                case "status": cpu=result?.cpu_percent; ram=result?.ram_percent; temp=result?.temperature_c; status="Status updated"
                case "apps": apps=result?.apps ?? []; status=apps.isEmpty ? "No visible applications found" : "Loaded \(apps.count) applications"
                case "close_app": if let p=payload?["pid"],let pid=Int(p){apps.removeAll{$0.id==pid}}; status="Application closed"
                case "clipboard_get": if result?.available==true{clipboard=result?.text ?? "";status="Clipboard received"}else{status="No PC clipboard text available"}
                case "clipboard_set": status="Clipboard sent to PC"
                case "screenshot": guard result?.available != false else { status=result?.error ?? "Screenshot is unavailable";return }; guard let b=result?.base64,let d=Data(base64Encoded:b),let im=UIImage(data:d) else {status="Screenshot data is invalid";return};image=im;status="Screenshot updated"
                case "clipboard_file_download": guard result?.available==true else{status="No copied file found on PC. Copy a file in Windows Explorer first.";return};guard let b=result?.base64,let d=Data(base64Encoded:b)else{status="Downloaded file data is invalid";return};let n=URL(fileURLWithPath:result?.name ?? "FaceUnlock-file").lastPathComponent;let dir=FileManager.default.temporaryDirectory.appendingPathComponent("FaceUnlockDownloads",isDirectory:true);try FileManager.default.createDirectory(at:dir,withIntermediateDirectories:true);let u=dir.appendingPathComponent(n);try? FileManager.default.removeItem(at:u);try d.write(to:u,options:.atomic);shareURL=u;showShareSheet=true;status="File ready to save: \(n)"
                case "lock": status=result?.locked==true ? "PC locked (apps kept running)" : "Lock command completed"
                case "restart": status="Restart command sent"
                case "shutdown": status="Shutdown command sent"
                default: status="Done"
                }
            } catch { status=error.localizedDescription }
        }
    }

    private func sendFile(_ url: URL) {
        Task { @MainActor in
            status="Reading file…"
            do {
                let access=url.startAccessingSecurityScopedResource();defer{if access{url.stopAccessingSecurityScopedResource()}}
                let values=try url.resourceValues(forKeys:[.isRegularFileKey,.fileSizeKey]);guard values.isRegularFile==true else{status="Please select a file, not a folder";return}
                if let s=values.fileSize,s>8*1024*1024{status="File exceeds 8 MB remote relay limit";return}
                let data=try Data(contentsOf:url);guard data.count<=8*1024*1024 else{status="File exceeds 8 MB remote relay limit";return}
                let response=try await APIClient.shared.runRemote(pcID:pc.id,type:"file_upload",payload:["name":url.lastPathComponent,"base64":data.base64EncodedString()],timeout:90)
                if response.status=="ERROR"{status=response.result?.error ?? "File transfer failed";return};status="File sent to PC: \(url.lastPathComponent)"
            } catch { status=error.localizedDescription }
        }
    }
}

struct MediaPicker: UIViewControllerRepresentable {
    let sourceType: UIImagePickerController.SourceType
    let completion: (URL?) -> Void
    func makeCoordinator() -> Coordinator { Coordinator(completion: completion) }
    func makeUIViewController(context: Context) -> UIImagePickerController { let p=UIImagePickerController();p.sourceType=sourceType;p.mediaTypes=[UTType.image.identifier,UTType.movie.identifier];p.delegate=context.coordinator;return p }
    func updateUIViewController(_ uiViewController: UIImagePickerController, context: Context) { }
    final class Coordinator: NSObject, UINavigationControllerDelegate, UIImagePickerControllerDelegate {
        let completion:(URL?)->Void;init(completion:@escaping(URL?)->Void){self.completion=completion}
        func imagePickerControllerDidCancel(_ picker:UIImagePickerController){completion(nil)}
        func imagePickerController(_ picker:UIImagePickerController,didFinishPickingMediaWithInfo info:[UIImagePickerController.InfoKey:Any]){
            if let u=info[.mediaURL] as? URL{completion(u);return}
            guard let image=info[.originalImage] as? UIImage,let data=image.jpegData(compressionQuality:0.92)else{completion(nil);return}
            let u=FileManager.default.temporaryDirectory.appendingPathComponent("FaceUnlock-\(UUID().uuidString).jpg");do{try data.write(to:u,options:.atomic);completion(u)}catch{completion(nil)}
        }
    }
}

struct ShareSheet: UIViewControllerRepresentable { let items:[Any];func makeUIViewController(context:Context)->UIActivityViewController{UIActivityViewController(activityItems:items,applicationActivities:nil)};func updateUIViewController(_ uiViewController:UIActivityViewController,context:Context){} }
