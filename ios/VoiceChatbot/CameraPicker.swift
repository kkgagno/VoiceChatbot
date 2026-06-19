import SwiftUI
import UIKit

struct CameraPicker: UIViewControllerRepresentable {
    @Binding var isPresented: Bool
    let onImage: (UIImage) -> Void

    func makeCoordinator() -> Coordinator {
        Coordinator(isPresented: $isPresented, onImage: onImage)
    }

    func makeUIViewController(context: Context) -> UIImagePickerController {
        let picker = UIImagePickerController()
        picker.sourceType = .camera
        picker.cameraCaptureMode = .photo
        picker.delegate = context.coordinator
        return picker
    }

    func updateUIViewController(_ uiViewController: UIImagePickerController, context: Context) {}

    final class Coordinator: NSObject, UINavigationControllerDelegate, UIImagePickerControllerDelegate {
        @Binding private var isPresented: Bool
        private let onImage: (UIImage) -> Void

        init(isPresented: Binding<Bool>, onImage: @escaping (UIImage) -> Void) {
            _isPresented = isPresented
            self.onImage = onImage
        }

        func imagePickerController(
            _ picker: UIImagePickerController,
            didFinishPickingMediaWithInfo info: [UIImagePickerController.InfoKey: Any]
        ) {
            if let image = info[.originalImage] as? UIImage {
                onImage(image)
            }
            isPresented = false
        }

        func imagePickerControllerDidCancel(_ picker: UIImagePickerController) {
            isPresented = false
        }
    }
}
