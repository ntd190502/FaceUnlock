import Foundation

@main
struct LogicalUnlockSelfTest {
    @MainActor
    static func main() async throws {
        let cache = LogicalBiometricApprovalCache()
        var prompts = 0
        let expiry = Date().addingTimeInterval(60)

        _ = try await cache.authorize(keys: ["pc|logical-1", "pc|online-1"], expiresAt: expiry) {
            prompts += 1
        }
        _ = try await cache.authorize(keys: ["pc|logical-1"], expiresAt: expiry) {
            prompts += 1
        }
        _ = try await cache.authorize(keys: ["pc|online-1"], expiresAt: expiry) {
            prompts += 1
        }

        guard prompts == 1 else {
            fatalError("Expected one biometric prompt, got \(prompts)")
        }
        print("Logical unlock biometric self-test PASS: prompt_count=1")
    }
}
