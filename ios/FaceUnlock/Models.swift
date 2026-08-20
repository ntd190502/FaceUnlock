import Foundation

struct PairingPayload: Codable { let type:String; let server:String; let pair_id:String; let pair_code:String; let pc_id:String; let pc_name:String; let pc_public_key_pem:String }
struct OfflineUnlockPayload: Codable { let type:String; let session_id:String; let pc_id:String; let pc_name:String; let challenge:String; let expires_at:Int64; let pc_signature:String; let logical_request_id:String?; let online_session_id:String? }
struct UnlockSession: Codable { let session_id:String; let challenge:String; let pc_id:String; let pc_name:String; let expires_at:Int64; let status:String }
struct ApproveRequest: Codable { let signature:String; let biometric:String }
struct PairCompleteRequest: Codable { let pair_id:String; let pair_code:String; let iphone_name:String; let iphone_public_key_pem:String }
struct PairCompleteResponse: Codable { let ok:Bool; let device_id:String; let device_api_token:String?; let pc_id:String; let pc_name:String; let pc_public_key_pem:String }
struct PendingUnlockResponse: Codable { let ok:Bool; let pending:Bool; let session_id:String? }
struct PairedPC: Codable,Identifiable { let id:String; let name:String; let status:String; let last_used_at:String? }
struct PairedPCResponse: Codable { let ok:Bool; let pcs:[PairedPC] }

struct RemoteCommandRequest: Codable { let pc_id:String; let type:String; let payload:[String:String]? }
struct RemoteCommandResponse: Codable { let ok:Bool; let command_id:String; let status:String; let expires_at:Int64 }
struct RemoteResultResponse: Codable { let ok:Bool; let command_id:String; let type:String; let status:String; let result:RemoteResult? }
struct RemoteResult: Codable {
 let cpu_percent:Double?; let ram_percent:Double?; let temperature_c:Double?; let locked:Bool?; let accepted:Bool?; let closed:Bool?; let text:String?; let available:Bool?; let mime:String?; let base64:String?; let name:String?; let size:Int?; let error:String?; let apps:[RemoteApp]?
}
struct RemoteApp: Codable,Identifiable { let id:Int; let name:String; let title:String }
