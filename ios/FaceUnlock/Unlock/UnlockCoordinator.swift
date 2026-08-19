import Foundation

@MainActor
final class UnlockCoordinator: ObservableObject {
    static let shared = UnlockCoordinator()

    @Published var pendingOnlineSessionID: String?
    @Published var lastMessage = "Ready"
    @Published var isBusy = false

    private var offlineApprovals: [String: Data] = [:]

    private init() {
        _ = BLEPeripheralManager.shared
    }

    func handleDeepLink(_ url: URL) {
        guard url.scheme == "faceunlock",
              url.host == "session",
              let id = URLComponents(url: url, resolvingAgainstBaseURL: false)?
                .queryItems?
                .first(where: { $0.name == "id" })?
                .value,
              !id.isEmpty else {
            lastMessage = "Invalid FaceUnlock link"
            return
        }

        pendingOnlineSessionID = id

        // Telegram -> HTTPS landing page -> faceunlock://session?id=...
        // As soon as the app opens, ask for Face ID.
        guard !isBusy else { return }
        Task { @MainActor in
            await approveOnline(sessionID: id)
        }
    }

    func approveOnline(sessionID: String) async {
        isBusy = true
        defer { isBusy = false }

        do {
            let s = try await APIClient.shared.fetchSession(sessionID)
            try await FaceAuth.authenticate(reason: "Unlock \(s.pc_name)")

            let canonical = Self.canonical(
                sessionID: s.session_id,
                challenge: s.challenge,
                pcID: s.pc_id,
                expiresAt: s.expires_at
            )
            let canonicalData = Data(canonical.utf8)
            let sig = try DeviceKey.shared.sign(canonicalData)
            let sigB64 = sig.base64EncodedString()
            let fp = (try? DeviceKey.shared.publicKeyFingerprint()) ?? "unknown"
            let canonicalHex = canonicalData.map { String(format: "%02x", $0) }.joined()

            print("""
            IOS SIGN:
            session_id=\(s.session_id)
            challenge=\(s.challenge)
            pc_id=\(s.pc_id)
            expires_at=\(s.expires_at)
            canonical UTF8=\(canonical)
            canonical UTF8 hex=\(canonicalHex)
            signature DER base64=\(sigB64)
            device public key fingerprint=\(fp)
            """)

            _ = try await APIClient.shared.approve(sessionID, signature: sig)
            lastMessage = "Approved \(s.pc_name)"
            pendingOnlineSessionID = nil
        } catch {
            lastMessage = error.localizedDescription
        }
    }

    func rejectOnline(sessionID: String) async {
        do {
            _ = try await APIClient.shared.reject(sessionID)
            pendingOnlineSessionID = nil
            lastMessage = "Rejected"
        } catch {
            lastMessage = error.localizedDescription
        }
    }

    func pair(from raw: String) async {
        isBusy = true
        defer { isBusy = false }

        do {
            let payload = try JSONDecoder().decode(PairingPayload.self, from: Data(raw.utf8))
            guard payload.type == "faceunlock-pair-v1" else {
                throw NSError(
                    domain: "FaceUnlock",
                    code: 20,
                    userInfo: [NSLocalizedDescriptionKey: "Unsupported QR"]
                )
            }

            let response = try await APIClient.shared.completePair(payload)

            var cfg = AppConfig.current
            cfg.serverURL = payload.server
            cfg.pcID = response.pc_id
            cfg.pcName = response.pc_name
            cfg.pcPublicKeyPEM = response.pc_public_key_pem
            cfg.deviceID = response.device_id
            cfg.deviceAPIToken = response.device_api_token
            AppConfig.current = cfg

            BLEPeripheralManager.shared.startAdvertising()
            lastMessage = "Paired with \(response.pc_name)"
        } catch {
            lastMessage = error.localizedDescription
        }
    }

    func handleOfflineBLERequest(_ data: Data) async throws -> Data {
        let payload = try JSONDecoder().decode(OfflineUnlockPayload.self, from: data)

        let sig: Data
        if let cached = offlineApprovals.removeValue(forKey: payload.session_id) {
            sig = cached
        } else {
            try await validateAndAuthenticateOffline(payload)
            let canonical = Self.canonical(
                sessionID: payload.session_id,
                challenge: payload.challenge,
                pcID: payload.pc_id,
                expiresAt: payload.expires_at
            )
            sig = try DeviceKey.shared.sign(Data(canonical.utf8))
        }

        return try JSONEncoder().encode([
            "ok":"true",
            "session_id":payload.session_id,
            "signature":sig.base64EncodedString()
        ])
    }

    private var pollingTask: Task<Void, Never>?

    func startForegroundPolling() {
        guard pollingTask == nil else { return }
        pollingTask = Task { @MainActor [weak self] in
            while !Task.isCancelled {
                guard let self = self else { break }
                if !self.isBusy && self.pendingOnlineSessionID == nil && AppConfig.current.deviceAPIToken != nil {
                    if let res = try? await APIClient.shared.pendingUnlock(), res.pending, let sid = res.session_id {
                        self.pendingOnlineSessionID = sid
                        await self.approveOnline(sessionID: sid)
                    }
                }
                try? await Task.sleep(nanoseconds: 2_000_000_000)
            }
        }
    }

    func stopForegroundPolling() {
        pollingTask?.cancel()
        pollingTask = nil
    }

    func approveOfflineQR(_ payload: OfflineUnlockPayload) async throws {
        try await validateAndAuthenticateOffline(payload)

        let canonical = Self.canonical(
            sessionID: payload.session_id,
            challenge: payload.challenge,
            pcID: payload.pc_id,
            expiresAt: payload.expires_at
        )
        offlineApprovals[payload.session_id] = try DeviceKey.shared.sign(Data(canonical.utf8))
        BLEPeripheralManager.shared.startAdvertising()
    }

    private func validateAndAuthenticateOffline(_ payload: OfflineUnlockPayload) async throws {
        guard payload.type == "faceunlock-offline-v1" else {
            throw NSError(
                domain: "FaceUnlock",
                code: 21,
                userInfo: [NSLocalizedDescriptionKey: "Wrong offline payload"]
            )
        }

        guard payload.expires_at >= Int64(Date().timeIntervalSince1970) else {
            throw NSError(
                domain: "FaceUnlock",
                code: 22,
                userInfo: [NSLocalizedDescriptionKey: "Request expired"]
            )
        }

        let cfg = AppConfig.current
        guard payload.pc_id == cfg.pcID, let pcPEM = cfg.pcPublicKeyPEM else {
            throw NSError(
                domain: "FaceUnlock",
                code: 23,
                userInfo: [NSLocalizedDescriptionKey: "PC is not paired"]
            )
        }

        let unsigned = "faceunlock-offline-request-v1|\(payload.session_id)|\(payload.challenge)|\(payload.pc_id)|\(payload.expires_at)"

        guard let sig = Data(base64Encoded: payload.pc_signature),
              SignatureVerifier.verifyPEM(
                pcPEM,
                message: Data(unsigned.utf8),
                signatureDER: sig
              ) else {
            throw NSError(
                domain: "FaceUnlock",
                code: 24,
                userInfo: [NSLocalizedDescriptionKey: "Invalid PC signature"]
            )
        }

        try await FaceAuth.authenticate(reason: "Unlock \(payload.pc_name) offline")
    }

    static func canonical(
        sessionID: String,
        challenge: String,
        pcID: String,
        expiresAt: Int64
    ) -> String {
        "faceunlock-v1|\(sessionID)|\(challenge)|\(pcID)|\(expiresAt)"
    }
}
