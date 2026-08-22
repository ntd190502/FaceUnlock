import SwiftUI
import SafariServices
import UIKit

struct PCControlView: View {
    let pc: PairedPC
    @State private var status="Ready"
    @State private var cpu:Double?;@State private var ram:Double?;@State private var temp:Double?
    @State private var files:[HostedFile]=[]
    @State private var safariURL:URL?
    @State private var shareURL:URL?
    var body:some View{
        Form{
            Section(header:Text("Control")){
                Button("Sign out PC"){run("signout")}.foregroundColor(.orange)
                Button("Restart PC"){run("restart")}.foregroundColor(.orange)
                Button("Shutdown PC"){run("shutdown")}.foregroundColor(.red)
            }
            Section(header:Text("PC status")){
                Button("Refresh CPU / RAM / temperature"){run("status")}
                if let cpu{Text("CPU: \(cpu,specifier:"%.1f")%")};if let ram{Text("RAM: \(ram,specifier:"%.1f")%")};if let temp{Text("Temperature: \(temp,specifier:"%.1f")°C")}
            }
            Section(header:Text("Files")){
                Button("Send file iPhone → PC"){safariURL=APIClient.shared.uploadWebURL(pcID:pc.id)}
                Button("Refresh PC → iPhone files"){refreshFiles()}
                ForEach(files){file in HStack{VStack(alignment:.leading){Text(file.name);Text(ByteCountFormatter.string(fromByteCount:file.size,countStyle:.file)).font(.caption).foregroundColor(.secondary)};Spacer();Button("Download"){download(file)}.buttonStyle(BorderlessButtonStyle());Button("Delete"){delete(file)}.foregroundColor(.red).buttonStyle(BorderlessButtonStyle())}}
                Text("iPhone → PC opens the FaceUnlock web uploader. PC → iPhone files are uploaded from the PC web page and remain on Hosting until downloaded or deleted.").font(.caption).foregroundColor(.secondary)
            }
            Section(header:Text("Status")){Text(status)}
        }.navigationTitle(pc.name)
        .sheet(item:$safariURL){SafariView(url:$0)}
        .sheet(item:$shareURL){ShareSheet(items:[$0])}
    }
    private func run(_ type:String){Task{@MainActor in status="Working…";do{let r=try await APIClient.shared.runRemote(pcID:pc.id,type:type);if r.status=="ERROR"{status=r.result?.error ?? "Command failed";return};if type=="status"{cpu=r.result?.cpu_percent;ram=r.result?.ram_percent;temp=r.result?.temperature_c;status="Status updated"}else if type=="signout"{status="Sign out command sent"}else if type=="restart"{status="Restart command sent"}else{status="Shutdown command sent"}}catch{status=error.localizedDescription}}}
    private func refreshFiles(){Task{@MainActor in do{files=try await APIClient.shared.hostedFiles(pcID:pc.id);status="Loaded \(files.count) file(s)"}catch{status=error.localizedDescription}}}
    private func delete(_ file:HostedFile){Task{@MainActor in do{try await APIClient.shared.deleteHostedFile(file.id);files.removeAll{$0.id==file.id};status="Deleted \(file.name)"}catch{status=error.localizedDescription}}}
    private func download(_ file:HostedFile){Task{@MainActor in do{let u=try await APIClient.shared.downloadHostedFile(file);files.removeAll{$0.id==file.id};shareURL=u;status="Downloaded \(file.name)"}catch{status=error.localizedDescription}}}
}
struct SafariView:UIViewControllerRepresentable{let url:URL;func makeUIViewController(context:Context)->SFSafariViewController{SFSafariViewController(url:url)};func updateUIViewController(_ uiViewController:SFSafariViewController,context:Context){}}
struct ShareSheet:UIViewControllerRepresentable{let items:[Any];func makeUIViewController(context:Context)->UIActivityViewController{UIActivityViewController(activityItems:items,applicationActivities:nil)};func updateUIViewController(_ uiViewController:UIActivityViewController,context:Context){}}
extension URL: @retroactive Identifiable { public var id:String{absoluteString} }
