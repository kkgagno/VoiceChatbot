import SwiftUI
import UIKit

struct KeyboardPinnedContainer<Content: View, Accessory: View>: UIViewControllerRepresentable {
    private let content: Content
    private let accessory: Accessory

    init(
        @ViewBuilder content: () -> Content,
        @ViewBuilder accessory: () -> Accessory
    ) {
        self.content = content()
        self.accessory = accessory()
    }

    func makeUIViewController(context: Context) -> KeyboardPinnedViewController {
        let controller = KeyboardPinnedViewController()
        controller.update(content: AnyView(content), accessory: AnyView(accessory))
        return controller
    }

    func updateUIViewController(_ controller: KeyboardPinnedViewController, context: Context) {
        controller.update(content: AnyView(content), accessory: AnyView(accessory))
    }
}

final class KeyboardPinnedViewController: UIViewController {
    private let contentHost = UIHostingController(rootView: AnyView(EmptyView()))
    private let accessoryHost = UIHostingController(rootView: AnyView(EmptyView()))

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .clear

        addChild(contentHost)
        addChild(accessoryHost)
        view.addSubview(contentHost.view)
        view.addSubview(accessoryHost.view)
        contentHost.didMove(toParent: self)
        accessoryHost.didMove(toParent: self)

        contentHost.view.backgroundColor = .clear
        accessoryHost.view.backgroundColor = .clear
        contentHost.view.translatesAutoresizingMaskIntoConstraints = false
        accessoryHost.view.translatesAutoresizingMaskIntoConstraints = false
        accessoryHost.sizingOptions = .intrinsicContentSize

        let keyboardGuide = view.keyboardLayoutGuide
        keyboardGuide.followsUndockedKeyboard = true

        NSLayoutConstraint.activate([
            contentHost.view.topAnchor.constraint(equalTo: view.topAnchor),
            contentHost.view.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            contentHost.view.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            contentHost.view.bottomAnchor.constraint(equalTo: view.bottomAnchor),

            accessoryHost.view.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            accessoryHost.view.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            accessoryHost.view.bottomAnchor.constraint(equalTo: keyboardGuide.topAnchor)
        ])
    }

    func update(content: AnyView, accessory: AnyView) {
        contentHost.rootView = content
        accessoryHost.rootView = accessory
        accessoryHost.view.invalidateIntrinsicContentSize()
    }
}
