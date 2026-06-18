import SwiftUI
import UniformTypeIdentifiers

struct RootView: View {
    @Bindable var model: AppModel

    var body: some View {
        TabView {
            NavigationStack {
                ConversationView(model: model)
            }
            .tabItem {
                Label("Conversation", systemImage: "waveform")
            }

            NavigationStack {
                TranscriptionView(model: model)
            }
            .tabItem {
                Label("Transcribe", systemImage: "text.quote")
            }

            NavigationStack {
                SettingsView(model: model)
            }
            .tabItem {
                Label("Connection", systemImage: "network")
            }
        }
        .task {
            await model.refreshStatus()
        }
    }
}

private struct ConversationView: View {
    @Bindable var model: AppModel

    var body: some View {
        VStack(spacing: 0) {
            statusHeader
            ScrollView {
                LazyVStack(spacing: 12) {
                    ForEach(model.messages) { message in
                        ChatBubble(entry: message)
                    }
                }
                .padding()
            }

            HStack(alignment: .bottom) {
                TextField("Message", text: $model.typedMessage, axis: .vertical)
                    .textFieldStyle(.roundedBorder)
                    .lineLimit(1...5)
                Button {
                    Task { await model.sendTypedMessage() }
                } label: {
                    Image(systemName: "arrow.up.circle.fill")
                        .font(.title)
                }
                .disabled(model.typedMessage.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
            .padding()
        }
        .navigationTitle("Voice Chatbot")
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                conversationButton
            }
        }
    }

    private var statusHeader: some View {
        HStack {
            Circle()
                .fill(model.isConversationActive ? .green : .secondary)
                .frame(width: 10, height: 10)
            Text(model.conversationState.label)
                .font(.subheadline.weight(.semibold))
            Spacer()
            if model.isConversationActive {
                Button(model.conversationState == .paused ? "Resume" : "Pause") {
                    if model.conversationState == .paused {
                        model.resumeConversation()
                    } else {
                        model.pauseConversation()
                    }
                }
                Button("Stop", role: .destructive) {
                    model.stopConversation()
                }
            }
        }
        .padding(.horizontal)
        .padding(.vertical, 10)
        .background(.thinMaterial)
    }

    @ViewBuilder
    private var conversationButton: some View {
        if model.isConversationActive {
            Button("Stop", systemImage: "stop.fill", role: .destructive) {
                model.stopConversation()
            }
        } else {
            Button("Start", systemImage: "mic.fill") {
                model.activeMode = .conversation
                Task { await model.startSession() }
            }
        }
    }
}

private struct TranscriptionView: View {
    @Bindable var model: AppModel

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Circle()
                    .fill(isTranscribing ? .red : .secondary)
                    .frame(width: 10, height: 10)
                Text(isTranscribing ? model.conversationState.label : "Ready")
                    .font(.subheadline.weight(.semibold))
                Spacer()
                if isTranscribing {
                    Button(model.conversationState == .paused ? "Resume" : "Pause") {
                        if model.conversationState == .paused {
                            model.resumeConversation()
                        } else {
                            model.pauseConversation()
                        }
                    }
                    Button("Stop", role: .destructive) {
                        model.stopConversation()
                    }
                }
            }
            .padding()
            .background(.thinMaterial)

            if model.transcript.isEmpty {
                ContentUnavailableView(
                    "No Transcript Yet",
                    systemImage: "waveform.badge.mic",
                    description: Text("Start transcription, then the phone can remain locked while audio segments are transcribed by Whisper on your PC.")
                )
            } else {
                ScrollView {
                    Text(model.transcript)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .textSelection(.enabled)
                        .padding()
                }
            }
        }
        .navigationTitle("Transcription")
        .toolbar {
            ToolbarItemGroup(placement: .topBarTrailing) {
                if !model.transcript.isEmpty {
                    ShareLink(item: model.transcript) {
                        Image(systemName: "square.and.arrow.up")
                    }
                    Button("Clear", systemImage: "trash", role: .destructive) {
                        model.clearTranscript()
                    }
                }
                if isTranscribing {
                    Button("Stop", systemImage: "stop.fill", role: .destructive) {
                        model.stopConversation()
                    }
                } else {
                    Button("Start", systemImage: "record.circle") {
                        model.activeMode = .transcription
                        Task { await model.startSession() }
                    }
                }
            }
        }
    }

    private var isTranscribing: Bool {
        model.isConversationActive && model.activeMode == .transcription
    }
}

private struct ChatBubble: View {
    let entry: ChatEntry

    var body: some View {
        HStack {
            if entry.role == .user { Spacer(minLength: 48) }
            Text(entry.text)
                .padding(12)
                .foregroundStyle(entry.role == .user ? .white : .primary)
                .background(entry.role == .user ? Color.accentColor : Color.secondary.opacity(0.15))
                .clipShape(RoundedRectangle(cornerRadius: 16))
            if entry.role != .user { Spacer(minLength: 48) }
        }
    }
}

private struct SettingsView: View {
    @Bindable var model: AppModel
    @State private var importingCertificate = false

    var body: some View {
        Form {
            Section("PC server") {
                TextField("Profile name", text: $model.profile.name)
                TextField("https://host:5100", text: $model.profile.baseURL)
                    .textInputAutocapitalization(.never)
                    .keyboardType(.URL)
                SecureField("PIN (optional)", text: $model.profile.pin)
            }

            Section("Certificate") {
                LabeledContent(
                    "Pinned certificate",
                    value: model.profile.pinnedCertificateDER == nil ? "Not imported" : "Imported"
                )
                Button("Import .cer certificate") {
                    importingCertificate = true
                }
            } footer: {
                Text("Export the certificate from VoiceChatbot on your PC and import it here. The app will only trust a server presenting that certificate.")
            }

            Section("Connection") {
                if let status = model.status {
                    LabeledContent("Provider", value: status.activeProvider)
                    LabeledContent("Model", value: status.activeModel)
                    LabeledContent("Endpoint", value: status.activeEndpoint)
                }
                Button("Save and test connection") {
                    Task { await model.applyProfile() }
                }
            }

            Section("Background conversation") {
                Text("When you start a conversation, microphone capture uses iOS background audio mode so it can continue while the app is minimized or the screen is locked. Pause or stop from the app or Lock Screen controls.")
            }
        }
        .navigationTitle("Connection")
        .fileImporter(
            isPresented: $importingCertificate,
            allowedContentTypes: [.x509Certificate],
            allowsMultipleSelection: false
        ) { result in
            guard case .success(let urls) = result, let url = urls.first else { return }
            let accessing = url.startAccessingSecurityScopedResource()
            defer { if accessing { url.stopAccessingSecurityScopedResource() } }
            if let data = try? Data(contentsOf: url) {
                model.importCertificate(data)
            }
        }
    }
}
