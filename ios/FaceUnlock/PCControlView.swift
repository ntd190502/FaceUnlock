import SwiftUI
import UIKit

struct PCControlView: View {
    let pc: PairedPC
    @State private var status="Ready"
    @State private var cpu:Double?;@State private var ram:Double?;@State private var temp:Double?
    @State private var files:[HostedFile]=[]
    @State private var shareURL:URL?
    @State private var showShare=false
    @State private var downloadingID:String?

    var body:some View{
        Form{
            Section(header:Text("Control")){
                Button("Sign out PC"){run("signout")}.foregroundColor(.orange)
                Button("Restart PC"){run("restart")}.foregroundColor(.orange)
                Button("Shutdown PC"){run("shutdown")}.foregroundColor(.red)
            }
            Section(header:Text("PC status")){
                Button("Refresh CPU / RAM / temperature"){run("status")}
                if let cpu{Text("CPU total: \(cpu,specifier:"%.1f")%")}
                if let ram{Text("RAM: \(ram,specifier:"%.1f")%")}
                if let temp{Text("Temperature: \(temp,specifier:"%.1f")°C")}
            }
            Section(header:Text("Files")){
                Button("Send file iPhone → PC"){
                    openUploadPage()
                }
                Button("Refresh PC → iPhone files"){refreshFiles()}
                ForEach(files){file in
                    HStack{
                        VStack(alignment:.leading){Text(file.name);Text(ByteCountFormatter.string(fromByteCount:file.size,countStyle:.file)).font(.caption).foregroundColor(.secondary)}
                        Spacer()
                        Button(downloadingID==file.id ? "Downloading…" : "Download"){download(file)}.disabled(downloadingID != nil).buttonStyle(BorderlessButtonStyle())
                        Button("Delete"){delete(file)}.foregroundColor(.red).buttonStyle(BorderlessButtonStyle())
                    }
                }
                if files.isEmpty{Text("No PC → iPhone files currently on Hosting.").font(.caption).foregroundColor(.secondary)}
                Text("iPhone → PC opens the FaceUnlock uploader in your normal web browser so iOS handles file/photo selection directly. PC → iPhone files remain on Hosting until downloaded or deleted.").font(.caption).foregroundColor(.secondary)
            }
            Section(header:Text("Status")){Text(status)}
        }
        .navigationTitle(pc.name)
        .onAppear{refreshFiles()}
        .sheet(isPresented:$showShare,onDismiss:{shareURL=nil}){
            if let shareURL{ShareSheet(items:[shareURL])}else{Text("Downloaded file unavailable")}
        }
    }

    private func openUploadPage(){
        guard let url=APIClient.shared.uploadWebURL(pcID:pc.id) else {
            status="Cannot open upload page: device token is missing"
            return
        }
        status="Opening upload website…"
        UIApplication.shared.open(url,options:[:]){opened in
            DispatchQueue.main.async{
                status = opened ? "Upload website opened in browser" : "Could not open upload website"
            }
        }
    }

    private func run(_ type:String){Task{@MainActor in status="Working…";do{let r=try await APIClient.shared.runRemote(pcID:pc.id,type:type);if r.status=="ERROR"{status=r.result?.error ?? "Command failed";return};if type=="status"{cpu=r.result?.cpu_percent;ram=r.result?.ram_percent;temp=r.result?.temperature_c;status="PC status updated"}else if type=="signout"{status="Sign out command sent"}else if type=="restart"{status="Restart command sent"}else{status="Shutdown command sent"}}catch{status=error.localizedDescription}}}

    private func refreshFiles(){Task{@MainActor in do{files=try await APIClient.shared.hostedFiles(pcID:pc.id);status="Loaded \(files.count) file(s) from Hosting"}catch{status="Refresh files failed: \(error.localizedDescription)"}}}

    private func delete(_ file:HostedFile){Task{@MainActor in do{try await APIClient.shared.deleteHostedFile(file.id);files.removeAll{$0.id==file.id};status="Deleted \(file.name)"}catch{status="Delete failed: \(error.localizedDescription)"}}}

    private func download(_ file:HostedFile){
        downloadingID=file.id;status="Downloading \(file.name)…"
        Task{@MainActor in
            defer{downloadingID=nil}
            do{
                let u=try await APIClient.shared.downloadHostedFile(file)
                files.removeAll{$0.id==file.id}
                shareURL=u
                showShare=true
                status="Downloaded \(file.name). Choose Save to Files or another destination."
            }catch{status="Download failed: \(error.localizedDescription)"}
        }
    }
}

struct ShareSheet:UIViewControllerRepresentable{
    let items:[Any]
    func makeUIViewController(context:Context)->UIActivityViewController{UIActivityViewController(activityItems:items,applicationActivities:nil)}
    func updateUIViewController(_ uiViewController:UIActivityViewController,context:Context){}
}
