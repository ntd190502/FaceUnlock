import SwiftUI
import UniformTypeIdentifiers

struct PCControlView: View {
 let pc: PairedPC
 @State private var status="Ready"; @State private var cpu:Double?; @State private var ram:Double?; @State private var temp:Double?; @State private var apps:[RemoteApp]=[]; @State private var clipboard=""; @State private var image:UIImage?; @State private var showPicker=false
 var body: some View { Form {
  Section(header:Text("Control")){Button("Lock PC"){run("lock")};Button("Restart PC"){run("restart")}.foregroundColor(.orange);Button("Shutdown PC"){run("shutdown")}.foregroundColor(.red)}
  Section(header:Text("PC status")){Button("Refresh CPU / RAM / temperature"){run("status")};if let cpu=cpu{Text("CPU: \(cpu,specifier:"%.1f")%")};if let ram=ram{Text("RAM: \(ram,specifier:"%.1f")%")};if let temp=temp{Text("Temperature: \(temp,specifier:"%.1f")°C")}}
  Section(header:Text("Screenshot")){Button("Capture screen"){run("screenshot")};if let image=image{Image(uiImage:image).resizable().scaledToFit()}}
  Section(header:Text("Applications")){Button("Refresh applications"){run("apps")};ForEach(apps){a in HStack{VStack(alignment:.leading){Text(a.name);if !a.title.isEmpty{Text(a.title).font(.caption).foregroundColor(.secondary)}};Spacer();Button("Close"){run("close_app",["pid":"\(a.id)"])}}}}
  Section(header:Text("Clipboard")){TextField("Text",text:$clipboard);Button("Send iPhone → PC"){run("clipboard_set",["text":clipboard])};Button("Get PC → iPhone"){run("clipboard_get")};Button("Copy received text"){UIPasteboard.general.string=clipboard}}
  Section(header:Text("Files")){Button("Send file iPhone → PC"){showPicker=true};Button("Get copied file PC → iPhone"){run("clipboard_file_download")}}
  Section(header:Text("Status")){Text(status)}
 }.navigationTitle(pc.name).sheet(isPresented:$showPicker){DocumentPicker{url in sendFile(url)}} }
 private func run(_ type:String,_ payload:[String:String]?=nil){Task{@MainActor in status="Working…";do{let r=try await APIClient.shared.runRemote(pcID:pc.id,type:type,payload:payload,timeout:type=="screenshot" ? 45:30);if r.status=="ERROR"{status=r.result?.error ?? "Command failed";return};let x=r.result;cpu=x?.cpu_percent;ram=x?.ram_percent;temp=x?.temperature_c;if let a=x?.apps{apps=a};if type=="clipboard_get",let t=x?.text{clipboard=t};if type=="screenshot",let b=x?.base64,let d=Data(base64Encoded:b){image=UIImage(data:d)};if type=="clipboard_file_download",x?.available==true,let b=x?.base64,let d=Data(base64Encoded:b){let name=x?.name ?? "FaceUnlock-file";let u=FileManager.default.urls(for:.documentDirectory,in:.userDomainMask)[0].appendingPathComponent(name);try d.write(to:u);status="Saved to FaceUnlock Documents: \(name)"}else{status="Done"}}catch{status=error.localizedDescription}}}}
 private func sendFile(_ url:URL){Task{@MainActor in do{let access=url.startAccessingSecurityScopedResource();defer{if access{url.stopAccessingSecurityScopedResource()}};let d=try Data(contentsOf:url);guard d.count<=8*1024*1024 else{status="File exceeds 8 MB remote relay limit";return};_ = try await APIClient.shared.runRemote(pcID:pc.id,type:"file_upload",payload:["name":url.lastPathComponent,"base64":d.base64EncodedString()],timeout:60);status="File sent"}catch{status=error.localizedDescription}}}}
}

struct DocumentPicker:UIViewControllerRepresentable { let onPick:(URL)->Void; func makeCoordinator()->Coordinator{Coordinator(onPick)};func makeUIViewController(context:Context)->UIDocumentPickerViewController{let v=UIDocumentPickerViewController(forOpeningContentTypes:[.item],asCopy:false);v.delegate=context.coordinator;return v};func updateUIViewController(_ uiViewController:UIDocumentPickerViewController,context:Context){};final class Coordinator:NSObject,UIDocumentPickerDelegate{let onPick:(URL)->Void;init(_ f:@escaping(URL)->Void){onPick=f};func documentPicker(_ controller:UIDocumentPickerViewController,didPickDocumentsAt urls:[URL]){if let u=urls.first{onPick(u)}}}}
