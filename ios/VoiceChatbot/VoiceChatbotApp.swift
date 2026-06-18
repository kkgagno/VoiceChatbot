import SwiftUI

@main
struct VoiceChatbotApp: App {
    @State private var model = AppModel()

    var body: some Scene {
        WindowGroup {
            RootView(model: model)
        }
    }
}

