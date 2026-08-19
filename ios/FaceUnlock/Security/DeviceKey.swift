import Foundation
import Security

final class DeviceKey {
    static let shared = DeviceKey()
    private let tag = "io.faceunlock.device.signing.p256".data(using: .utf8)!

    private func findPrivateKey() -> SecKey? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassKey,
            kSecAttrApplicationTag as String: tag,
            kSecAttrKeyType as String: kSecAttrKeyTypeECSECPrimeRandom,
            kSecReturnRef as String: true
        ]
        var item: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess else { return nil }
        return (item as! SecKey)
    }

    private func createPrivateKey() throws -> SecKey {
        let access = SecAccessControlCreateWithFlags(nil,
                                                    kSecAttrAccessibleWhenUnlockedThisDeviceOnly,
                                                    [.privateKeyUsage], nil)!
        let privateAttrs: [String: Any] = [
            kSecAttrIsPermanent as String: true,
            kSecAttrApplicationTag as String: tag,
            kSecAttrAccessControl as String: access
        ]
        var attributes: [String: Any] = [
            kSecAttrKeyType as String: kSecAttrKeyTypeECSECPrimeRandom,
            kSecAttrKeySizeInBits as String: 256,
            kSecPrivateKeyAttrs as String: privateAttrs,
            kSecAttrTokenID as String: kSecAttrTokenIDSecureEnclave
        ]
        var error: Unmanaged<CFError>?
        if let key = SecKeyCreateRandomKey(attributes as CFDictionary, &error) { return key }

        // Simulator/older-device fallback: software key, still stored as non-exported Keychain reference.
        attributes.removeValue(forKey: kSecAttrTokenID as String)
        if let key = SecKeyCreateRandomKey(attributes as CFDictionary, &error) { return key }
        throw error!.takeRetainedValue() as Error
    }

    func privateKey() throws -> SecKey { try findPrivateKey() ?? createPrivateKey() }

    func publicKeyPEM() throws -> String {
        let priv = try privateKey()
        guard let pub = SecKeyCopyPublicKey(priv),
              let data = SecKeyCopyExternalRepresentation(pub, nil) as Data? else {
            throw NSError(domain: "FaceUnlock", code: 10, userInfo: [NSLocalizedDescriptionKey: "Unable to export public key"])
        }
        // SecKey returns ANSI X9.63 uncompressed point for EC. Wrap it as SubjectPublicKeyInfo.
        let spkiPrefix = Data([0x30,0x59,0x30,0x13,0x06,0x07,0x2A,0x86,0x48,0xCE,0x3D,0x02,0x01,0x06,0x08,0x2A,0x86,0x48,0xCE,0x3D,0x03,0x01,0x07,0x03,0x42,0x00])
        let b64 = (spkiPrefix + data).base64EncodedString(options: [.lineLength64Characters, .endLineWithLineFeed])
        return "-----BEGIN PUBLIC KEY-----\n\(b64)\n-----END PUBLIC KEY-----\n"
    }

    func sign(_ data: Data) throws -> Data {
        let key = try privateKey()
        var error: Unmanaged<CFError>?
        guard let sig = SecKeyCreateSignature(key, .ecdsaSignatureMessageX962SHA256, data as CFData, &error) as Data? else {
            throw error!.takeRetainedValue() as Error
        }
        return sig
    }
}
