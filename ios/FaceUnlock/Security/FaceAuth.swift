import LocalAuthentication

struct FaceAuth {
    static func authenticate(reason: String) async throws {
        let context = LAContext()
        context.localizedCancelTitle = "Cancel"
        var error: NSError?
        guard context.canEvaluatePolicy(.deviceOwnerAuthenticationWithBiometrics, error: &error) else {
            throw error ?? NSError(domain: "FaceUnlock", code: 1, userInfo: [NSLocalizedDescriptionKey: "Biometric authentication is unavailable"])
        }
        let ok = try await context.evaluatePolicy(.deviceOwnerAuthenticationWithBiometrics, localizedReason: reason)
        if !ok { throw NSError(domain: "FaceUnlock", code: 2, userInfo: [NSLocalizedDescriptionKey: "Face ID rejected"])}
    }
}
