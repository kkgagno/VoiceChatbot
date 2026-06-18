import Foundation
import Network

final class NetworkChangeMonitor: @unchecked Sendable {
    private let monitor = NWPathMonitor()
    private let queue = DispatchQueue(label: "com.keithgagnon.VoiceChatbot.network-monitor")
    private var hasDeliveredInitialPath = false

    func start(onChange: @escaping @Sendable () -> Void) {
        monitor.pathUpdateHandler = { [weak self] _ in
            guard let self else { return }
            if hasDeliveredInitialPath {
                onChange()
            } else {
                hasDeliveredInitialPath = true
            }
        }
        monitor.start(queue: queue)
    }

    deinit {
        monitor.cancel()
    }
}
