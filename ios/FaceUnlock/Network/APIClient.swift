import Foundation
import UIKit

final class APIClient {
 static let shared=APIClient()
 private func request<T:Decodable>(_ path:String,method:String="GET",token:String?=nil,body:Encodable?=nil,baseURL:String?=nil) async throws->T{
  let base=baseURL ?? AppConfig.current.serverURL; guard let url=URL(string:base.trimmingCharacters(in:CharacterSet(charactersIn:"/"))+path) else{throw URLError(.badURL)}
  var req=URLRequest(url:url);req.httpMethod=method;req.setValue("application/json",forHTTPHeaderField:"Content-Type");if let token=token{req.setValue("Bearer \(token)",forHTTPHeaderField:"Authorization")};if let body=body{req.httpBody=try JSONEncoder().encode(AnyEncodable(body))}
  let(data,response)=try await URLSession.shared.data(for:req);guard let http=response as? HTTPURLResponse,200..<300 ~= http.statusCode else{throw NSError(domain:"FaceUnlock.API",code:(response as? HTTPURLResponse)?.statusCode ?? -1,userInfo:[NSLocalizedDescriptionKey:String(data:data,encoding:.utf8) ?? "HTTP error"])}
  do{return try JSONDecoder().decode(T.self,from:data)}catch{throw NSError(domain:"FaceUnlock.API",code:-2,userInfo:[NSLocalizedDescriptionKey:"Hosting response could not be decoded safely: \(error.localizedDescription)"])}
 }
 func completePair(_ payload:PairingPayload) async throws->PairCompleteResponse{let req=PairCompleteRequest(pair_id:payload.pair_id,pair_code:payload.pair_code,iphone_name:UIDevice.current.name,iphone_public_key_pem:try DeviceKey.shared.publicKeyPEM());return try await request("/v1/pair/complete",method:"POST",body:req,baseURL:payload.server)}
 func fetchSession(_ id:String) async throws->UnlockSession{guard let t=AppConfig.current.deviceAPIToken else{throw URLError(.userAuthenticationRequired)};return try await request("/v1/unlock/session/\(id)",token:t)}
 func approve(_ id:String,signature:Data) async throws->BasicResponse{guard let t=AppConfig.current.deviceAPIToken else{throw URLError(.userAuthenticationRequired)};return try await request("/v1/unlock/approve/\(id)",method:"POST",token:t,body:ApproveRequest(signature:signature.base64EncodedString(),biometric:"faceid"))}
 func reject(_ id:String) async throws->BasicResponse{guard let t=AppConfig.current.deviceAPIToken else{throw URLError(.userAuthenticationRequired)};return try await request("/v1/unlock/reject/\(id)",method:"POST",token:t,body:EmptyBody())}
 func pendingUnlock() async throws->PendingUnlockResponse{guard let t=AppConfig.current.deviceAPIToken else{throw URLError(.userAuthenticationRequired)};return try await request("/v1/unlock/pending",token:t)}
 func pairedPCs() async throws->PairedPCResponse{guard let t=AppConfig.current.deviceAPIToken else{throw URLError(.userAuthenticationRequired)};return try await request("/v1/device/pcs",token:t)}
 func remoteCommand(pcID:String,type:String,payload:[String:String]?=nil) async throws->RemoteCommandResponse{guard let t=AppConfig.current.deviceAPIToken else{throw URLError(.userAuthenticationRequired)};return try await request("/v1/control/command",method:"POST",token:t,body:RemoteCommandRequest(pc_id:pcID,type:type,payload:payload))}
 func remoteResult(_ id:String) async throws->RemoteResultResponse{guard let t=AppConfig.current.deviceAPIToken else{throw URLError(.userAuthenticationRequired)};return try await request("/v1/control/result/\(id)",token:t)}
 func runRemote(pcID:String,type:String,payload:[String:String]?=nil,timeout:TimeInterval=30) async throws->RemoteResultResponse{let c=try await remoteCommand(pcID:pcID,type:type,payload:payload);let end=Date().addingTimeInterval(timeout);while Date()<end{try await Task.sleep(nanoseconds:700_000_000);let r=try await remoteResult(c.command_id);if r.status=="DONE"||r.status=="ERROR"{return r}};throw URLError(.timedOut)}
}
struct BasicResponse:Codable{let ok:Bool};struct EmptyBody:Codable{}
private struct AnyEncodable:Encodable{let encodeBlock:(Encoder)throws->Void;init(_ wrapped:Encodable){encodeBlock=wrapped.encode};func encode(to encoder:Encoder)throws{try encodeBlock(encoder)}}
