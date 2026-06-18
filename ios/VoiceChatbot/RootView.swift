import SwiftUI
import UniformTypeIdentifiers

struct RootView: View {
    @Bindable var model: AppModel

    var body: some View {
        TabView {
            NavigationStack {
                ConversationView(model: model)
            }
            .tabItem { Label("Chat", systemImage: "message.fill") }

            NavigationStack {
                TranscriptionView(model: model)
            }
            .tabItem { Label("Transcribe", systemImage: "waveform") }

            NavigationStack {
                ConnectionView(model: model)
            }
            .tabItem { Label("Connection", systemImage: "desktopcomputer") }
        }
        .tint(.cyan)
        .task { await model.refreshStatus() }
    }
}

private struct ConversationView: View {
    @Bindable var model: AppModel
    @FocusState private var messageFieldFocused: Bool

    var body: some View {
        ZStack {
            AppBackground()

            VStack(spacing: 0) {
                ConnectionBanner(model: model)

                if model.messages.isEmpty {
                    Spacer()
                    EmptyConversation(model: model)
                    Spacer()
                } else {
                    ScrollView {
                        LazyVStack(spacing: 14) {
                            ForEach(model.messages) { ChatBubble(entry: $0) }
                        }
                        .padding()
                    }
                    .scrollDismissesKeyboard(.interactively)
                }

                composer
            }
        }
        .navigationTitle("Voice Chatbot")
        .navigationBarTitleDisplayMode(.inline)
        .toolbarBackground(.ultraThinMaterial, for: .navigationBar)
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                SessionToolbarButton(model: model, mode: .conversation)
            }
            ToolbarItemGroup(placement: .keyboard) {
                Spacer()
                Button("Done") {
                    messageFieldFocused = false
                }
            }
        }
        .safeAreaInset(edge: .bottom) {
            if model.isConversationActive {
                ActiveSessionBar(model: model)
            }
        }
    }

    private var composer: some View {
        HStack(alignment: .bottom, spacing: 10) {
            TextField("Message your PC model", text: $model.typedMessage, axis: .vertical)
                .lineLimit(1...5)
                .focused($messageFieldFocused)
                .submitLabel(.send)
                .onSubmit {
                    guard !model.typedMessage.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                        messageFieldFocused = false
                        return
                    }
                    messageFieldFocused = false
                    Task { await model.sendTypedMessage() }
                }
                .padding(.horizontal, 14)
                .padding(.vertical, 11)
                .background(.thinMaterial, in: RoundedRectangle(cornerRadius: 20))

            Button {
                messageFieldFocused = false
                Task { await model.sendTypedMessage() }
            } label: {
                Image(systemName: "arrow.up")
                    .font(.headline.bold())
                    .frame(width: 44, height: 44)
                    .background(.cyan, in: Circle())
                    .foregroundStyle(.black)
            }
            .disabled(model.typedMessage.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            .opacity(model.typedMessage.isEmpty ? 0.45 : 1)
        }
        .padding()
    }
}

private struct EmptyConversation: View {
    @Bindable var model: AppModel

    var body: some View {
        VStack(spacing: 18) {
            ZStack {
                Circle()
                    .fill(.cyan.opacity(0.14))
                    .frame(width: 116, height: 116)
                Image(systemName: "waveform.and.mic")
                    .font(.system(size: 48, weight: .medium))
                    .foregroundStyle(.cyan)
            }

            VStack(spacing: 7) {
                Text("Talk to your PC")
                    .font(.title2.bold())
                Text("Your models, Whisper, and speech stay on the PC. The iPhone is your private voice remote.")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .padding(.horizontal, 30)
            }

            Button {
                model.activeMode = .conversation
                Task { await model.startSession() }
            } label: {
                Label("Start Conversation", systemImage: "mic.fill")
                    .font(.headline)
                    .frame(maxWidth: 260)
                    .padding(.vertical, 14)
            }
            .buttonStyle(.borderedProminent)
            .tint(.cyan)
            .foregroundStyle(.black)
            .disabled(model.status?.ok != true)

            if model.status?.ok != true {
                Text("Connect to your PC first")
                    .font(.caption)
                    .foregroundStyle(.orange)
            }
        }
    }
}

private struct ConnectionBanner: View {
    @Bindable var model: AppModel

    var body: some View {
        HStack(spacing: 10) {
            Circle()
                .fill(model.status?.ok == true ? .green : .orange)
                .frame(width: 9, height: 9)
            VStack(alignment: .leading, spacing: 2) {
                Text(model.status?.ok == true ? "PC connected" : "PC not connected")
                    .font(.subheadline.weight(.semibold))
                if let status = model.status, !status.activeModel.isEmpty {
                    Text(status.activeModel)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
            }
            Spacer()
            if model.isConnecting {
                ProgressView()
            } else {
                Image(systemName: "chevron.right")
                    .font(.caption.bold())
                    .foregroundStyle(.secondary)
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 11)
        .background(.thinMaterial)
    }
}

private struct ActiveSessionBar: View {
    @Bindable var model: AppModel

    var body: some View {
        HStack(spacing: 14) {
            ZStack {
                Circle().fill(.red.opacity(0.18)).frame(width: 42, height: 42)
                Image(systemName: model.conversationState == .paused ? "pause.fill" : "waveform")
                    .foregroundStyle(.red)
            }
            VStack(alignment: .leading, spacing: 2) {
                Text(model.activeMode.rawValue)
                    .font(.subheadline.bold())
                Text(model.conversationState.label)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Spacer()
            Button(model.conversationState == .paused ? "Resume" : "Pause") {
                if model.conversationState == .paused {
                    model.resumeConversation()
                } else {
                    model.pauseConversation()
                }
            }
            .buttonStyle(.bordered)

            Button(role: .destructive) {
                model.stopConversation()
            } label: {
                Image(systemName: "stop.fill")
            }
            .buttonStyle(.borderedProminent)
        }
        .padding()
        .background(.regularMaterial)
    }
}

private struct TranscriptionView: View {
    @Bindable var model: AppModel

    var body: some View {
        ZStack {
            AppBackground()
            VStack(spacing: 0) {
                ConnectionBanner(model: model)

                if model.transcript.isEmpty {
                    Spacer()
                    VStack(spacing: 18) {
                        Image(systemName: "quote.bubble.fill")
                            .font(.system(size: 54))
                            .foregroundStyle(.cyan)
                        Text("Background Transcription")
                            .font(.title2.bold())
                        Text("Start a session, lock the screen, and Whisper on your PC will transcribe speech in timestamped segments.")
                            .font(.subheadline)
                            .foregroundStyle(.secondary)
                            .multilineTextAlignment(.center)
                            .padding(.horizontal, 32)
                        Button {
                            model.activeMode = .transcription
                            Task { await model.startSession() }
                        } label: {
                            Label("Start Transcribing", systemImage: "record.circle")
                                .font(.headline)
                                .frame(maxWidth: 260)
                                .padding(.vertical, 14)
                        }
                        .buttonStyle(.borderedProminent)
                        .tint(.cyan)
                        .foregroundStyle(.black)
                        .disabled(model.status?.ok != true)
                    }
                    Spacer()
                } else {
                    ScrollView {
                        Text(model.transcript)
                            .font(.body.monospaced())
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .textSelection(.enabled)
                            .padding()
                    }
                }
            }
        }
        .navigationTitle("Transcription")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .topBarLeading) {
                SessionToolbarButton(model: model, mode: .transcription)
            }
            ToolbarItemGroup(placement: .topBarTrailing) {
                if !model.transcript.isEmpty {
                    ShareLink(item: model.transcript) {
                        Image(systemName: "square.and.arrow.up")
                    }
                    Button(role: .destructive) {
                        model.clearTranscript()
                    } label: {
                        Image(systemName: "trash")
                    }
                }
            }
        }
        .safeAreaInset(edge: .bottom) {
            if model.isConversationActive {
                ActiveSessionBar(model: model)
            }
        }
    }
}

private struct SessionToolbarButton: View {
    @Bindable var model: AppModel
    let mode: ActiveSessionMode

    var body: some View {
        if model.isConversationActive {
            Button(role: .destructive) {
                model.stopConversation()
            } label: {
                Label("Stop", systemImage: "stop.circle.fill")
            }
        } else {
            Button {
                model.activeMode = mode
                Task { await model.startSession() }
            } label: {
                Label("Start", systemImage: "mic.circle.fill")
            }
            .disabled(model.status?.ok != true)
        }
    }
}

private struct ChatBubble: View {
    let entry: ChatEntry

    var body: some View {
        HStack {
            if entry.role == .user { Spacer(minLength: 52) }
            Text(entry.text)
                .padding(.horizontal, 15)
                .padding(.vertical, 11)
                .foregroundStyle(entry.role == .user ? .black : .primary)
                .background(
                    entry.role == .user ? Color.cyan : Color.secondary.opacity(0.16),
                    in: RoundedRectangle(cornerRadius: 18)
                )
            if entry.role != .user { Spacer(minLength: 52) }
        }
    }
}

private struct ConnectionView: View {
    private enum Field: Hashable {
        case profileName
        case localURL
        case vpnURL
        case pin
    }

    @Bindable var model: AppModel
    @State private var importingCertificate = false
    @FocusState private var focusedField: Field?

    var body: some View {
        ZStack {
            AppBackground()
            ScrollView {
                VStack(spacing: 18) {
                    connectionCard
                    serverCard
                    certificateCard
                    helpCard
                }
                .padding()
            }
            .scrollDismissesKeyboard(.interactively)
        }
        .navigationTitle("Connection")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItemGroup(placement: .keyboard) {
                Spacer()
                Button("Done") {
                    focusedField = nil
                }
            }
        }
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

    private var connectionCard: some View {
        VStack(spacing: 12) {
            Image(systemName: model.status?.ok == true ? "checkmark.circle.fill" : "desktopcomputer.trianglebadge.exclamationmark")
                .font(.system(size: 44))
                .foregroundStyle(model.status?.ok == true ? .green : .orange)
            Text(model.status?.ok == true ? "Connected to PC" : "Set up your PC")
                .font(.title3.bold())
            Text(model.connectionMessage)
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
            if let status = model.status {
                Text([status.activeProvider, status.activeModel].filter { !$0.isEmpty }.joined(separator: " • "))
                    .font(.subheadline.weight(.medium))
                    .lineLimit(2)
            }
            if !model.activeRoute.isEmpty {
                Label(
                    model.activeRoute,
                    systemImage: model.activeRoute == "VPN" ? "lock.shield.fill" : "wifi"
                )
                .font(.caption.weight(.semibold))
                .foregroundStyle(.cyan)
            }
        }
        .frame(maxWidth: .infinity)
        .padding(22)
        .background(.thinMaterial, in: RoundedRectangle(cornerRadius: 24))
    }

    private var serverCard: some View {
        VStack(alignment: .leading, spacing: 14) {
            Label("PC Server", systemImage: "network")
                .font(.headline)
            TextField("Profile name", text: $model.profile.name)
                .textFieldStyle(.roundedBorder)
                .focused($focusedField, equals: .profileName)
                .submitLabel(.next)
                .onSubmit { focusedField = .localURL }
            Text("The app tries Home Wi-Fi first, then VPN automatically.")
                .font(.caption)
                .foregroundStyle(.secondary)
            TextField("Home: https://192.168.1.50:5100", text: $model.profile.localURL)
                .textFieldStyle(.roundedBorder)
                .textInputAutocapitalization(.never)
                .autocorrectionDisabled()
                .keyboardType(.URL)
                .focused($focusedField, equals: .localURL)
                .submitLabel(.next)
                .onSubmit { focusedField = .vpnURL }
            TextField("VPN: https://10.8.0.1:5100", text: $model.profile.vpnURL)
                .textFieldStyle(.roundedBorder)
                .textInputAutocapitalization(.never)
                .autocorrectionDisabled()
                .keyboardType(.URL)
                .focused($focusedField, equals: .vpnURL)
                .submitLabel(.next)
                .onSubmit { focusedField = .pin }
            SecureField("PIN (optional)", text: $model.profile.pin)
                .textFieldStyle(.roundedBorder)
                .focused($focusedField, equals: .pin)
                .submitLabel(.done)
                .onSubmit { focusedField = nil }
            Button {
                focusedField = nil
                Task { await model.applyProfile() }
            } label: {
                HStack {
                    if model.isConnecting { ProgressView().tint(.black) }
                    Text(model.isConnecting ? "Connecting…" : "Save and Test")
                }
                .frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent)
            .tint(.cyan)
            .foregroundStyle(.black)
            .disabled(model.isConnecting)
        }
        .cardStyle()
    }

    private var certificateCard: some View {
        VStack(alignment: .leading, spacing: 12) {
            Label("Secure Certificate", systemImage: "lock.shield")
                .font(.headline)
            HStack {
                Text(model.profile.pinnedCertificateDER == nil ? "Not imported" : "Certificate imported")
                    .foregroundStyle(.secondary)
                Spacer()
                Image(systemName: model.profile.pinnedCertificateDER == nil ? "xmark.circle" : "checkmark.circle.fill")
                    .foregroundStyle(model.profile.pinnedCertificateDER == nil ? .orange : .green)
            }
            Button("Import .cer Certificate") {
                importingCertificate = true
            }
            .buttonStyle(.bordered)
        }
        .cardStyle()
    }

    private var helpCard: some View {
        VStack(alignment: .leading, spacing: 9) {
            Label("Before connecting", systemImage: "info.circle")
                .font(.headline)
            Text("1. Start VoiceChatbot on the Windows PC.")
            Text("2. Enable Phone Remote.")
            Text("3. Use its HTTPS address and matching PIN.")
            Text("4. Import the public .cer certificate.")
        }
        .font(.subheadline)
        .foregroundStyle(.secondary)
        .cardStyle()
    }
}

private struct AppBackground: View {
    var body: some View {
        LinearGradient(
            colors: [Color(.systemBackground), Color.cyan.opacity(0.07), Color(.systemBackground)],
            startPoint: .topLeading,
            endPoint: .bottomTrailing
        )
        .ignoresSafeArea()
    }
}

private extension View {
    func cardStyle() -> some View {
        padding(18)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(.thinMaterial, in: RoundedRectangle(cornerRadius: 22))
    }
}
