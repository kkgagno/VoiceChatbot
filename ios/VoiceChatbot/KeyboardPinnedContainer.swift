import SwiftUI
import UIKit

struct KeyboardPinnedContainer<Content: View, Accessory: View>: UIViewControllerRepresentable {
    @Binding private var accessoryHeight: CGFloat
    private let content: Content
    private let accessory: Accessory

    init(
        accessoryHeight: Binding<CGFloat>,
        @ViewBuilder content: () -> Content,
        @ViewBuilder accessory: () -> Accessory
    ) {
        _accessoryHeight = accessoryHeight
        self.content = content()
        self.accessory = accessory()
    }

    func makeUIViewController(context: Context) -> KeyboardPinnedViewController {
        let controller = KeyboardPinnedViewController()
        controller.onAccessoryHeightChange = { height in
            accessoryHeight = height
        }
        controller.update(content: AnyView(content), accessory: AnyView(accessory))
        return controller
    }

    func updateUIViewController(_ controller: KeyboardPinnedViewController, context: Context) {
        controller.onAccessoryHeightChange = { height in
            accessoryHeight = height
        }
        controller.update(content: AnyView(content), accessory: AnyView(accessory))
    }
}

final class KeyboardPinnedViewController: UIViewController {
    var onAccessoryHeightChange: ((CGFloat) -> Void)?

    private let contentHost = UIHostingController(rootView: AnyView(EmptyView()))
    private let accessoryHost = UIHostingController(rootView: AnyView(EmptyView()))
    private var lastAccessoryHeight: CGFloat = 0

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
        view.setNeedsLayout()
    }

    override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        let height = accessoryHost.view.bounds.height
        guard abs(height - lastAccessoryHeight) > 0.5 else { return }
        lastAccessoryHeight = height
        DispatchQueue.main.async { [weak self] in
            self?.onAccessoryHeightChange?(height)
        }
    }
}
