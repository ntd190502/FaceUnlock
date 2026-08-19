import Foundation
import Security

struct SignatureVerifier {
    static func verifyPEM(_ pem: String, message: Data, signatureDER: Data) -> Bool {
        let body = pem
            .replacingOccurrences(of: "-----BEGIN PUBLIC KEY-----", with: "")
            .replacingOccurrences(of: "-----END PUBLIC KEY-----", with: "")
            .replacingOccurrences(of: "\n", with: "")
            .replacingOccurrences(of: "\r", with: "")
        guard let spki = Data(base64Encoded: body), spki.count > 26 else { return false }
        let point = spki.suffix(65)
        let attrs: [String: Any] = [
            kSecAttrKeyType as String: kSecAttrKeyTypeECSECPrimeRandom,
            kSecAttrKeyClass as String: kSecAttrKeyClassPublic,
            kSecAttrKeySizeInBits as String: 256
        ]
        var error: Unmanaged<CFError>?
        guard let key = SecKeyCreateWithData(Data(point) as CFData, attrs as CFDictionary, &error) else { return false }
        return SecKeyVerifySignature(key, .ecdsaSignatureMessageX962SHA256, message as CFData, signatureDER as CFData, &error)
    }
}
