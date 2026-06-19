import SwiftUI

struct ModelsView: View {
    @Bindable var model: AppModel
    let showChat: () -> Void

    private let commands = [
        ("Stop Current Model", "Hermes stop current running llama.cpp model", "stop.circle"),
        ("GPT-OSS 120B", "Hermes start gpt-oss:120b", "cpu"),
        ("Gemma", "Hermes start gemma", "cpu"),
        ("Gemma 4B", "Hermes start gemma 4b", "cpu"),
        ("Gemma 12B", "Hermes start gemma 12b", "cpu"),
        ("Gemma Speculative", "Hermes start gemma speculative", "bolt"),
        ("Gemma 26B A4B", "Hermes start gemma 26b a4b", "cpu"),
        ("Mistral", "Hermes start mistral", "cpu"),
        ("Qwen", "Hermes start qwen", "cpu"),
        ("LFM", "Hermes start lfm", "cpu")
    ]

    var body: some View {
        List {
            Section {
                LabeledContent("Provider", value: model.status?.activeProvider ?? "Unknown")
                LabeledContent("Active model", value: model.status?.activeModel ?? "None")
                LabeledContent("Route", value: model.activeRoute.isEmpty ? "Not connected" : model.activeRoute)
            } header: {
                Text("Current")
            }

            Section {
                ForEach(commands, id: \.1) { item in
                    Button {
                        showChat()
                        Task { await model.runModelCommand(item.1) }
                    } label: {
                        HStack {
                            Image(systemName: item.2)
                                .frame(width: 28)
                                .foregroundStyle(item.0.hasPrefix("Stop") ? .red : .cyan)
                            VStack(alignment: .leading, spacing: 3) {
                                Text(item.0)
                                    .foregroundStyle(.primary)
                                Text(item.1)
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                            }
                            Spacer()
                            Image(systemName: "chevron.right")
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }
                    }
                }
            } header: {
                Text("Switch llama.cpp model")
            } footer: {
                Text("Tapping sends the same Hermes command used by Model Help and returns to Chat while the PC switches models.")
            }

            Section("ComfyUI service") {
                Button {
                    showChat()
                    Task { await model.runModelCommand("Hermes start comfyui") }
                } label: {
                    Label("Start ComfyUI", systemImage: "play.circle")
                }
                Button {
                    showChat()
                    Task { await model.runModelCommand("Hermes stop comfyui") }
                } label: {
                    Label("Stop ComfyUI", systemImage: "stop.circle")
                        .foregroundStyle(.red)
                }
            }
        }
        .navigationTitle("Models")
        .refreshable { await model.refreshStatus() }
    }
}
