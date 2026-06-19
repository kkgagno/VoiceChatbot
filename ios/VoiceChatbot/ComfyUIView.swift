import AVKit
import CoreTransferable
import PhotosUI
import SwiftUI
import UIKit
import UniformTypeIdentifiers

struct ComfyUIView: View {
    @Bindable var model: AppModel
    @State private var action: ComfyAction = .createImage
    @State private var prompt = ""
    @State private var seconds = 6
    @State private var selectedPhotos = [PhotosPickerItem]()
    @State private var attachments = [PendingAttachment]()
    @State private var importingAudio = false

    var body: some View {
        Form {
            Section("Workflow") {
                Picker("Action", selection: $action) {
                    ForEach(ComfyAction.allCases) { item in
                        Label(item.rawValue, systemImage: item.systemImage).tag(item)
                    }
                }
                .pickerStyle(.menu)

                TextField("Describe the image, edit, or motion", text: $prompt, axis: .vertical)
                    .lineLimit(3...8)

                if action == .createVideo || action == .createVideoWithAudio {
                    Stepper("Duration: \(seconds) seconds", value: $seconds, in: 1...30)
                }
            }

            if action != .createImage {
                Section("Source media") {
                    PhotosPicker(
                        selection: $selectedPhotos,
                        maxSelectionCount: action == .editImage ? 2 : 1,
                        matching: .images
                    ) {
                        Label(
                            attachments.contains(where: { $0.kind == .image })
                                ? "Replace Source Image"
                                : "Choose Source Image",
                            systemImage: "photo"
                        )
                    }

                    if action == .createVideoWithAudio {
                        Button {
                            importingAudio = true
                        } label: {
                            Label(
                                attachments.contains(where: { $0.kind == .audio })
                                    ? "Replace Audio"
                                    : "Choose Audio",
                                systemImage: "waveform"
                            )
                        }
                    }

                    ForEach(attachments) { attachment in
                        HStack {
                            Image(systemName: attachment.kind == .audio ? "waveform" : "photo")
                            Text(attachment.name).lineLimit(1)
                            Spacer()
                            Text(attachment.sizeLabel).foregroundStyle(.secondary)
                        }
                    }
                }
            }

            Section {
                Button {
                    Task {
                        await model.runComfy(
                            action: action,
                            prompt: prompt,
                            attachments: attachments,
                            seconds: seconds
                        )
                    }
                } label: {
                    HStack {
                        if model.isRunningComfy { ProgressView() }
                        Label(
                            model.isRunningComfy ? "Running on PC…" : action.rawValue,
                            systemImage: action.systemImage
                        )
                    }
                    .frame(maxWidth: .infinity)
                }
                .disabled(model.isRunningComfy || !canRun)
            }

            if let output = model.comfyOutput {
                Section("Latest result") {
                    Text(output.message)
                        .font(.subheadline)

                    if let data = output.imageData, let image = UIImage(data: data) {
                        Image(uiImage: image)
                            .resizable()
                            .scaledToFit()
                            .clipShape(RoundedRectangle(cornerRadius: 16))
                        ShareLink(
                            item: ImageTransferable(data: data),
                            preview: SharePreview("VoiceChatbot image", image: Image(uiImage: image))
                        ) {
                            Label("Save or Share Image", systemImage: "square.and.arrow.up")
                        }
                    }

                    if let url = output.videoURL {
                        VideoPlayer(player: AVPlayer(url: url))
                            .frame(minHeight: 260)
                            .clipShape(RoundedRectangle(cornerRadius: 16))
                        ShareLink(item: url) {
                            Label("Save or Share Video", systemImage: "square.and.arrow.up")
                        }
                    }
                }
            }
        }
        .navigationTitle("ComfyUI")
        .scrollDismissesKeyboard(.interactively)
        .onChange(of: action) {
            attachments = []
        }
        .onChange(of: selectedPhotos) { _, items in
            Task { await loadPhotos(items) }
        }
        .fileImporter(
            isPresented: $importingAudio,
            allowedContentTypes: [.audio],
            allowsMultipleSelection: false
        ) { result in
            guard case .success(let urls) = result, let url = urls.first else { return }
            let accessing = url.startAccessingSecurityScopedResource()
            defer { if accessing { url.stopAccessingSecurityScopedResource() } }
            guard let data = try? Data(contentsOf: url) else { return }
            attachments.removeAll { $0.kind == .audio }
            attachments.append(
                PendingAttachment(
                    name: url.lastPathComponent,
                    mimeType: UTType(filenameExtension: url.pathExtension)?.preferredMIMEType ?? "audio/wav",
                    data: data,
                    kind: .audio
                )
            )
        }
    }

    private var canRun: Bool {
        guard !prompt.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return false }
        let hasImage = attachments.contains { $0.kind == .image }
        let hasAudio = attachments.contains { $0.kind == .audio }
        switch action {
        case .createImage:
            return true
        case .editImage, .createVideo:
            return hasImage
        case .createVideoWithAudio:
            return hasImage && hasAudio
        }
    }

    @MainActor
    private func loadPhotos(_ items: [PhotosPickerItem]) async {
        defer { selectedPhotos = [] }
        attachments.removeAll { $0.kind == .image }
        for (index, item) in items.enumerated() {
            guard let data = try? await item.loadTransferable(type: Data.self),
                  let image = UIImage(data: data),
                  let jpeg = image.jpegData(compressionQuality: 0.9)
            else { continue }
            attachments.append(
                PendingAttachment(
                    name: "Comfy-Source-\(index + 1).jpg",
                    mimeType: "image/jpeg",
                    data: jpeg,
                    kind: .image
                )
            )
        }
    }
}

struct ImageTransferable: Transferable {
    let data: Data

    static var transferRepresentation: some TransferRepresentation {
        DataRepresentation(exportedContentType: .png) { item in
            item.data
        }
    }
}
