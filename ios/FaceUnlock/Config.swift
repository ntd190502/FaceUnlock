import Foundation

struct AppConfig: Codable {
    var serverURL: String
    var pcID: String?
    var pcName: String?
    var pcPublicKeyPEM: String?
    var deviceID: String?
    var pairedPCs: [PairedPC] = []
    /// Offline verification keys keyed by PC ID. Legacy single-PC fields are
    /// retained for backward compatibility and the currently selected PC.
    var pairedPCPublicKeys: [String: String] = [:]

    var deviceAPIToken: String? {
        get { KeychainHelper.shared.loadString(key: "device_api_token") }
        set {
            if let token = newValue, !token.isEmpty {
                _ = KeychainHelper.shared.saveString(key: "device_api_token", value: token)
            } else {
                _ = KeychainHelper.shared.delete(key: "device_api_token")
            }
        }
    }

    private enum CodingKeys: String, CodingKey {
        case serverURL, pcID, pcName, pcPublicKeyPEM, deviceID, deviceAPIToken, pairedPCs, pairedPCPublicKeys
    }

    init(serverURL: String, pcID: String? = nil, pcName: String? = nil, pcPublicKeyPEM: String? = nil, deviceID: String? = nil, deviceAPIToken: String? = nil) {
        self.serverURL = serverURL
        self.pcID = pcID
        self.pcName = pcName
        self.pcPublicKeyPEM = pcPublicKeyPEM
        self.deviceID = deviceID
        if let pcID, let pcPublicKeyPEM { pairedPCPublicKeys[pcID] = pcPublicKeyPEM }
        if let token = deviceAPIToken { self.deviceAPIToken = token }
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        serverURL = try container.decodeIfPresent(String.self, forKey: .serverURL) ?? "https://face.bobabliss.io.vn"
        pcID = try container.decodeIfPresent(String.self, forKey: .pcID)
        pcName = try container.decodeIfPresent(String.self, forKey: .pcName)
        pcPublicKeyPEM = try container.decodeIfPresent(String.self, forKey: .pcPublicKeyPEM)
        deviceID = try container.decodeIfPresent(String.self, forKey: .deviceID)
        pairedPCs = try container.decodeIfPresent([PairedPC].self, forKey: .pairedPCs) ?? []
        pairedPCPublicKeys = try container.decodeIfPresent([String: String].self, forKey: .pairedPCPublicKeys) ?? [:]
        if let pcID, let pcPublicKeyPEM, pairedPCPublicKeys[pcID] == nil { pairedPCPublicKeys[pcID] = pcPublicKeyPEM }

        if let legacyToken = try container.decodeIfPresent(String.self, forKey: .deviceAPIToken), !legacyToken.isEmpty {
            _ = KeychainHelper.shared.saveString(key: "device_api_token", value: legacyToken)
        }
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(serverURL, forKey: .serverURL)
        try container.encodeIfPresent(pcID, forKey: .pcID)
        try container.encodeIfPresent(pcName, forKey: .pcName)
        try container.encodeIfPresent(pcPublicKeyPEM, forKey: .pcPublicKeyPEM)
        try container.encodeIfPresent(deviceID, forKey: .deviceID)
        try container.encode(pairedPCs, forKey: .pairedPCs)
        try container.encode(pairedPCPublicKeys, forKey: .pairedPCPublicKeys)
    }

    func publicKey(forPC pcID: String) -> String? {
        pairedPCPublicKeys[pcID] ?? (self.pcID == pcID ? pcPublicKeyPEM : nil)
    }

    static var current: AppConfig {
        get {
            guard let data = UserDefaults.standard.data(forKey: "app_config"),
                  let cfg = try? JSONDecoder().decode(AppConfig.self, from: data)
            else { return AppConfig(serverURL: "https://face.bobabliss.io.vn") }

            if let object = try? JSONSerialization.jsonObject(with: data),
               let dictionary = object as? [String: Any],
               dictionary["deviceAPIToken"] != nil,
               let sanitized = try? JSONEncoder().encode(cfg) {
                UserDefaults.standard.set(sanitized, forKey: "app_config")
            }
            return cfg
        }
        set {
            if let data = try? JSONEncoder().encode(newValue) {
                UserDefaults.standard.set(data, forKey: "app_config")
            }
        }
    }
}
