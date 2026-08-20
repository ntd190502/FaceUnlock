import Foundation

struct PairingPayload: Codable {
    let type: String
    let server: String
    let pair_id: String
    let pair_code: String
    let pc_id: String
    let pc_name: String
    let pc_public_key_pem: String
}

struct OfflineUnlockPayload: Codable {
    let type: String
    let session_id: String
    let pc_id: String
    let pc_name: String
    let challenge: String
    let expires_at: Int64
    let pc_signature: String
    let logical_request_id: String?
    let online_session_id: String?
}

struct UnlockSession: Codable {
    let session_id: String
    let challenge: String
    let pc_id: String
    let pc_name: String
    let expires_at: Int64
    let status: String
}

struct ApproveRequest: Codable {
    let signature: String
    let biometric: String
}

struct PairCompleteRequest: Codable {
    let pair_id: String
    let pair_code: String
    let iphone_name: String
    let iphone_public_key_pem: String
}

struct PairCompleteResponse: Codable {
    let ok: Bool
    let device_id: String
    let device_api_token: String
    let pc_id: String
    let pc_name: String
    let pc_public_key_pem: String
}

struct PendingUnlockResponse: Codable {
    let ok: Bool
    let pending: Bool
    let session_id: String?
}
