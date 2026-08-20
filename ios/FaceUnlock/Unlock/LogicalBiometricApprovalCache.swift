import Foundation

/// Keeps one successful biometric ceremony bound to one signed logical unlock
/// request while allowing fresh transport-specific crypto challenges.
@MainActor
final class LogicalBiometricApprovalCache {
    private var approvals: [String: Date] = [:]
    private var inFlight: [String: Task<Void, Error>] = [:]

    /// Returns true only for the caller that actually performed authentication.
    func authorize(
        keys: [String],
        expiresAt: Date,
        authenticate: @escaping () async throws -> Void
    ) async throws -> Bool {
        let now = Date()
        approvals = approvals.filter { $0.value > now }
        let normalized = Array(Set(keys.filter { !$0.isEmpty }))
        guard !normalized.isEmpty else {
            throw NSError(
                domain: "FaceUnlock",
                code: 25,
                userInfo: [NSLocalizedDescriptionKey: "Missing logical unlock request"]
            )
        }

        if normalized.contains(where: { approvals[$0].map { $0 > now } == true }) {
            return false
        }

        if let existing = normalized.compactMap({ inFlight[$0] }).first {
            try await existing.value
            let boundedExpiry = min(expiresAt, now.addingTimeInterval(120))
            for key in normalized { approvals[key] = boundedExpiry }
            return false
        }

        let task = Task { try await authenticate() }
        for key in normalized { inFlight[key] = task }
        do {
            try await task.value
        } catch {
            for key in normalized { inFlight.removeValue(forKey: key) }
            throw error
        }
        for key in normalized { inFlight.removeValue(forKey: key) }

        let boundedExpiry = min(expiresAt, now.addingTimeInterval(120))
        for key in normalized { approvals[key] = boundedExpiry }
        return true
    }
}
