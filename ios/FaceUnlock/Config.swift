import Foundation

struct AppConfig: Codable {
    var serverURL: String
    var pcID: String?
    var pcName: String?
    var pcPublicKeyPEM: String?
    var deviceID: String?

    /// Stored only in the iOS Keychain. It is intentionally excluded from the
    /// UserDefaults JSON representation.
    var deviceAPIToken: String? {
        get {
            KeychainHelper.shared.loadString(key: "device_api_token")
        }
        set {
            if let token = newValue, !token.isEmpty {
                _ = KeychainHelper.shared.saveString(
                    key: "device_api_token",
                    value: token
                )
            } else {
                _ = KeychainHelper.shared.delete(key: "device_api_token")
            }
        }
    }

    private enum CodingKeys: String, CodingKey {
        case serverURL, pcID, pcName, pcPublicKeyPEM, deviceID, deviceAPIToken
    }

    init(
        serverURL: String,
        pcID: String? = nil,
        pcName: String? = nil,
        pcPublicKeyPEM: String? = nil,
        deviceID: String? = nil,
        deviceAPIToken: String? = nil
    ) {
        self.serverURL = serverURL
        self.pcID = pcID
        self.pcName = pcName
        self.pcPublicKeyPEM = pcPublicKeyPEM
        self.deviceID = deviceID
        if let token = deviceAPIToken {
            self.deviceAPIToken = token
        }
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        self.serverURL =
            try container.decodeIfPresent(String.self, forKey: .serverURL)
            ?? "https://face.bobabliss.io.vn"
        self.pcID = try container.decodeIfPresent(String.self, forKey: .pcID)
        self.pcName = try container.decodeIfPresent(String.self, forKey: .pcName)
        self.pcPublicKeyPEM =
            try container.decodeIfPresent(String.self, forKey: .pcPublicKeyPEM)
        self.deviceID =
            try container.decodeIfPresent(String.self, forKey: .deviceID)

        // Migration from older releases that persisted the bearer token inside
        // the UserDefaults JSON blob.
        if let legacyToken =
            try container.decodeIfPresent(String.self, forKey: .deviceAPIToken),
           !legacyToken.isEmpty {
            _ = KeychainHelper.shared.saveString(
                key: "device_api_token",
                value: legacyToken
            )
        }
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(serverURL, forKey: .serverURL)
        try container.encodeIfPresent(pcID, forKey: .pcID)
        try container.encodeIfPresent(pcName, forKey: .pcName)
        try container.encodeIfPresent(pcPublicKeyPEM, forKey: .pcPublicKeyPEM)
        try container.encodeIfPresent(deviceID, forKey: .deviceID)
        // deviceAPIToken must never be encoded here.
    }

    static var current: AppConfig {
        get {
            guard let data = UserDefaults.standard.data(forKey: "app_config"),
                  let cfg = try? JSONDecoder().decode(AppConfig.self, from: data)
            else {
                return AppConfig(serverURL: "https://face.bobabliss.io.vn")
            }

            // Finish legacy-token migration immediately. The previous
            // implementation copied the token to Keychain but could leave the
            // plaintext token in UserDefaults until some later config save.
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
