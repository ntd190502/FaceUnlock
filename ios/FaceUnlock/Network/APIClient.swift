import Foundation
import UIKit

final class APIClient {
    static let shared = APIClient()

    private func request<T: Decodable>(_ path: String,
                                       method: String = "GET",
                                       token: String? = nil,
                                       body: Encodable? = nil,
                                       baseURL: String? = nil) async throws -> T {
        let base = baseURL ?? AppConfig.current.serverURL
        guard let url = URL(string: base.trimmingCharacters(in: CharacterSet(charactersIn: "/")) + path) else {
            throw URLError(.badURL)
        }
        var req = URLRequest(url: url)
        req.httpMethod = method
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        if let token = token { req.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization") }
        if let body = body { req.httpBody = try JSONEncoder().encode(AnyEncodable(body)) }

        let (data, response) = try await URLSession.shared.data(for: req)
        guard let http = response as? HTTPURLResponse, 200..<300 ~= http.statusCode else {
            throw NSError(domain: "FaceUnlock.API",
                          code: (response as? HTTPURLResponse)?.statusCode ?? -1,
                          userInfo: [NSLocalizedDescriptionKey: String(data: data, encoding: .utf8) ?? "HTTP error"])
        }
        return try JSONDecoder().decode(T.self, from: data)
    }

    func completePair(_ payload: PairingPayload) async throws -> PairCompleteResponse {
        let req = PairCompleteRequest(pair_id: payload.pair_id,
                                      pair_code: payload.pair_code,
                                      iphone_name: UIDevice.current.name,
                                      iphone_public_key_pem: try DeviceKey.shared.publicKeyPEM())
        return try await request("/v1/pair/complete", method: "POST", body: req, baseURL: payload.server)
    }

    func fetchSession(_ id: String) async throws -> UnlockSession {
        guard let token = AppConfig.current.deviceAPIToken else { throw URLError(.userAuthenticationRequired) }
        return try await request("/v1/unlock/session/\(id)", token: token)
    }

    func approve(_ id: String, signature: Data) async throws -> BasicResponse {
        guard let token = AppConfig.current.deviceAPIToken else { throw URLError(.userAuthenticationRequired) }
        return try await request("/v1/unlock/approve/\(id)", method: "POST", token: token,
                                 body: ApproveRequest(signature: signature.base64EncodedString(), biometric: "faceid"))
    }

    func reject(_ id: String) async throws -> BasicResponse {
        guard let token = AppConfig.current.deviceAPIToken else { throw URLError(.userAuthenticationRequired) }
        return try await request("/v1/unlock/reject/\(id)", method: "POST", token: token, body: EmptyBody())
    }
    func pendingUnlock() async throws -> PendingUnlockResponse {
        guard let token = AppConfig.current.deviceAPIToken else {
            throw URLError(.userAuthenticationRequired)
        }
        return try await request("/v1/unlock/pending", token: token)
    }

}

struct BasicResponse: Codable { let ok: Bool }
struct EmptyBody: Codable {}

private struct AnyEncodable: Encodable {
    let encodeBlock: (Encoder) throws -> Void
    init(_ wrapped: Encodable) { encodeBlock = wrapped.encode }
    func encode(to encoder: Encoder) throws { try encodeBlock(encoder) }
}
