import Photos
import SwiftUI
import UIKit

struct Krea2View: View {
    @Bindable var model: AppModel
    @State private var prompt = ""
    @State private var enableLora = false
    @State private var selectedLora = ""
    @State private var selectedAspectRatio = Krea2Options.fallback.aspectRatios[0]
    @State private var saveMessage: String?

    var body: some View {
        Form {
            Section("Krea2 Turbo") {
                TextField("Describe the image", text: $prompt, axis: .vertical)
                    .lineLimit(3...8)

                Picker("Picture size", selection: $selectedAspectRatio) {
                    ForEach(currentAspectRatios, id: \.self) { ratio in
                        Text(ratio).tag(ratio)
                    }
                }
                .pickerStyle(.menu)

                Toggle("Enable LoRA?", isOn: $enableLora)

                if enableLora {
                    Picker("Krea2 LoRA", selection: $selectedLora) {
                        if currentLoras.isEmpty {
                            Text("No krea2 LoRAs found").tag("")
                        } else {
                            ForEach(currentLoras, id: \.self) { lora in
                                Text(lora).tag(lora)
                            }
                        }
                    }
                    .pickerStyle(.menu)
                }
            }

            Section {
                Button {
                    Task {
                        await model.runKrea2(
                            prompt: prompt,
                            enableLora: enableLora,
                            loraName: selectedLora,
                            aspectRatio: selectedAspectRatio
                        )
                    }
                } label: {
                    HStack {
                        if model.isRunningKrea2 { ProgressView() }
                        Label(
                            model.isRunningKrea2 ? "Running on PC…" : "Create Krea2 Image",
                            systemImage: "sparkles"
                        )
                    }
                    .frame(maxWidth: .infinity)
                }
                .disabled(!canRun)

                Button {
                    Task { await model.refreshKrea2Options() }
                } label: {
                    HStack {
                        if model.isLoadingKrea2Options { ProgressView() }
                        Label("Refresh LoRAs", systemImage: "arrow.clockwise")
                    }
                }
                .disabled(model.isLoadingKrea2Options)
            }

            if let output = model.krea2Output {
                Section("Latest result") {
                    Text(output.message)
                        .font(.subheadline)

                    if let data = output.imageData, let image = UIImage(data: data) {
                        Image(uiImage: image)
                            .resizable()
                            .scaledToFit()
                            .clipShape(RoundedRectangle(cornerRadius: 16))

                        Button {
                            Task { await saveImageToPhotos(data) }
                        } label: {
                            Label("Save to Photos", systemImage: "photo.badge.arrow.down")
                        }

                        ShareLink(
                            item: ImageTransferable(data: data),
                            preview: SharePreview("Krea2 image", image: Image(uiImage: image))
                        ) {
                            Label("Share or Save to Files", systemImage: "square.and.arrow.up")
                        }
                    }
                }
            }
        }
        .navigationTitle("Krea2")
        .scrollDismissesKeyboard(.interactively)
        .task {
            await model.refreshKrea2Options()
            normalizeSelections()
        }
        .onChange(of: model.krea2Options) {
            normalizeSelections()
        }
        .onChange(of: enableLora) {
            normalizeSelections()
        }
        .alert(
            "Photos",
            isPresented: Binding(
                get: { saveMessage != nil },
                set: { if !$0 { saveMessage = nil } }
            )
        ) {
            Button("OK") { saveMessage = nil }
        } message: {
            Text(saveMessage ?? "")
        }
    }

    private var canRun: Bool {
        guard !model.isRunningKrea2,
              !prompt.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        else { return false }
        return !enableLora || !selectedLora.isEmpty
    }

    private var currentAspectRatios: [String] {
        model.krea2Options.aspectRatios.isEmpty
            ? Krea2Options.fallback.aspectRatios
            : model.krea2Options.aspectRatios
    }

    private var currentLoras: [String] {
        model.krea2Options.loras
    }

    private func normalizeSelections() {
        if !currentAspectRatios.contains(selectedAspectRatio) {
            selectedAspectRatio = currentAspectRatios.first ?? Krea2Options.fallback.aspectRatios[0]
        }

        if selectedLora.isEmpty || !currentLoras.contains(selectedLora) {
            selectedLora = currentLoras.first ?? ""
        }
    }

    @MainActor
    private func saveImageToPhotos(_ data: Data) async {
        let status = await PHPhotoLibrary.requestAuthorization(for: .addOnly)
        guard status == .authorized || status == .limited else {
            saveMessage = "Allow Voice Chatbot to add photos in Settings, then try again."
            return
        }

        do {
            try await PHPhotoLibrary.shared().performChanges {
                let request = PHAssetCreationRequest.forAsset()
                request.addResource(with: .photo, data: data, options: nil)
            }
            saveMessage = "Image saved to Photos."
        } catch {
            saveMessage = "Could not save the image: \(error.localizedDescription)"
        }
    }
}
