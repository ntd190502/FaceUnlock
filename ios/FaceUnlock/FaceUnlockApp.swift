import SwiftUI

@main
struct FaceUnlockApp: App {
    @UIApplicationDelegateAdaptor(AppDelegate.self) var appDelegate
    @Environment(\.scenePhase) private var scenePhase
    @StateObject private var coordinator = UnlockCoordinator.shared

    var body: some Scene {
        WindowGroup {
            ContentView()
                .environmentObject(coordinator)
                .onOpenURL { url in coordinator.handleDeepLink(url) }
        }
        .onChange(of: scenePhase) { phase in
            switch phase {
            case .active:
                coordinator.startForegroundPolling()
            case .inactive:
                break
            case .background:
                coordinator.stopForegroundPolling()
            @unknown default:
                break
            }
        }
    }
}
