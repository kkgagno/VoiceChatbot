import Contacts
import MessageUI
import Observation
import SwiftUI

struct ContactSummary: Identifiable, Equatable {
    let id: String
    let name: String
    let phoneNumbers: [String]
}

struct TextMessageDraft: Identifiable, Equatable {
    let id = UUID()
    let contactID: String
    var contactName: String
    var phoneNumber: String
    var body: String
}

enum TextMessageCommandParser {
    static func mightBeCommunicationRequest(_ text: String) -> Bool {
        let lowered = text.lowercased()
        let words = Set(
            lowered
                .components(separatedBy: CharacterSet.alphanumerics.inverted)
                .filter { !$0.isEmpty }
        )
        return !words.isDisjoint(
            with: ["text", "message", "sms", "imessage", "send", "tell", "let", "write", "ask"]
        )
    }
}

struct TextMessageAIIntent: Decodable {
    let isTextMessage: Bool

    static func decode(from response: String) throws -> TextMessageAIIntent {
        let trimmed = response.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let start = trimmed.firstIndex(of: "{"),
              let end = trimmed.lastIndex(of: "}")
        else {
            throw ContactsFeatureError.invalidAIIntent
        }
        return try JSONDecoder().decode(
            TextMessageAIIntent.self,
            from: Data(trimmed[start...end].utf8)
        )
    }
}

struct TextMessageAIDraft: Decodable {
    let contactID: String
    let phoneNumber: String
    let body: String

    static func decode(from response: String) throws -> TextMessageAIDraft {
        let trimmed = response.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let start = trimmed.firstIndex(of: "{"),
              let end = trimmed.lastIndex(of: "}")
        else {
            throw ContactsFeatureError.invalidAIDraft
        }
        return try JSONDecoder().decode(
            TextMessageAIDraft.self,
            from: Data(trimmed[start...end].utf8)
        )
    }
}

@MainActor
@Observable
final class ContactsService {
    private let store = CNContactStore()

    var contacts = [ContactSummary]()
    var authorizationStatus = CNContactStore.authorizationStatus(for: .contacts)
    var errorMessage: String?

    var hasAccess: Bool {
        authorizationStatus == .authorized
    }

    func requestAccessAndLoad() async {
        do {
            if !hasAccess {
                _ = try await store.requestAccess(for: .contacts)
                authorizationStatus = CNContactStore.authorizationStatus(for: .contacts)
            }
            guard hasAccess else {
                errorMessage = "Contacts access was not granted."
                return
            }
            try loadContacts()
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    func loadContacts() throws {
        guard hasAccess else {
            throw ContactsFeatureError.accessRequired
        }
        let keys: [CNKeyDescriptor] = [
            CNContactIdentifierKey as CNKeyDescriptor,
            CNContactGivenNameKey as CNKeyDescriptor,
            CNContactFamilyNameKey as CNKeyDescriptor,
            CNContactOrganizationNameKey as CNKeyDescriptor,
            CNContactPhoneNumbersKey as CNKeyDescriptor
        ]
        let request = CNContactFetchRequest(keysToFetch: keys)
        request.sortOrder = .userDefault
        var loaded = [ContactSummary]()
        try store.enumerateContacts(with: request) { contact, _ in
            let personalName = [contact.givenName, contact.familyName]
                .filter { !$0.isEmpty }
                .joined(separator: " ")
            let name = personalName.isEmpty ? contact.organizationName : personalName
            let numbers = contact.phoneNumbers
                .map(\.value.stringValue)
                .filter { !$0.isEmpty }
            guard !name.isEmpty, !numbers.isEmpty else { return }
            loaded.append(
                ContactSummary(
                    id: contact.identifier,
                    name: name,
                    phoneNumbers: numbers
                )
            )
        }
        contacts = loaded
        errorMessage = nil
    }

    func contact(id: String, phoneNumber: String) -> ContactSummary? {
        contacts.first {
            $0.id == id && $0.phoneNumbers.contains(phoneNumber)
        }
    }

    func modelContext(limit: Int = 1_000) -> String {
        let records = contacts.prefix(limit).map { contact in
            "- id: \(contact.id) | name: \(contact.name) | phones: \(contact.phoneNumbers.joined(separator: ", "))"
        }
        return records.isEmpty
            ? "- No contacts with phone numbers found."
            : records.joined(separator: "\n")
    }
}

enum ContactsFeatureError: LocalizedError {
    case accessRequired
    case invalidAIIntent
    case invalidAIDraft
    case contactNotFound
    case messagingUnavailable

    var errorDescription: String? {
        switch self {
        case .accessRequired:
            "Contacts access is required."
        case .invalidAIIntent:
            "The AI could not determine whether this was a text-message request."
        case .invalidAIDraft:
            "The AI could not prepare a valid text message."
        case .contactNotFound:
            "No exact contact and phone-number match was found."
        case .messagingUnavailable:
            "This device is not currently able to present the Messages composer."
        }
    }
}

struct ContactsView: View {
    @Bindable var model: AppModel
    @State private var searchText = ""

    private var filteredContacts: [ContactSummary] {
        guard !searchText.isEmpty else { return model.contacts.contacts }
        return model.contacts.contacts.filter {
            $0.name.localizedCaseInsensitiveContains(searchText)
                || $0.phoneNumbers.contains { $0.localizedCaseInsensitiveContains(searchText) }
        }
    }

    var body: some View {
        ZStack {
            AppBackground()
            if model.contacts.hasAccess {
                List(filteredContacts) { contact in
                    VStack(alignment: .leading, spacing: 5) {
                        Text(contact.name)
                            .font(.headline)
                        ForEach(contact.phoneNumbers, id: \.self) { number in
                            Text(number)
                                .foregroundStyle(.secondary)
                        }
                    }
                    .padding(.vertical, 4)
                }
                .scrollContentBackground(.hidden)
                .searchable(text: $searchText, prompt: "Search contacts")
            } else {
                ContentUnavailableView {
                    Label("Contacts Access", systemImage: "person.crop.circle.badge.checkmark")
                } description: {
                    Text("Allow access so the AI can resolve names to exact phone numbers.")
                } actions: {
                    Button("Allow Contacts Access") {
                        Task { await model.contacts.requestAccessAndLoad() }
                    }
                    .buttonStyle(.borderedProminent)
                    .tint(.cyan)
                }
            }
        }
        .navigationTitle("Contacts")
        .navigationBarTitleDisplayMode(.inline)
        .task {
            if model.contacts.hasAccess {
                try? model.contacts.loadContacts()
            }
        }
    }
}

struct TextMessageConfirmationView: View {
    @Bindable var model: AppModel
    @State private var draft: TextMessageDraft
    @State private var showingComposer = false
    @Environment(\.dismiss) private var dismiss

    init(model: AppModel, draft: TextMessageDraft) {
        self.model = model
        _draft = State(initialValue: draft)
    }

    var body: some View {
        NavigationStack {
            Form {
                Section("Recipient") {
                    LabeledContent("Contact", value: draft.contactName)
                    LabeledContent("Number", value: draft.phoneNumber)
                }
                Section("Message") {
                    TextEditor(text: $draft.body)
                        .frame(minHeight: 140)
                }
                Section {
                    Text("Apple requires you to tap Send in the native Messages composer.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
            .navigationTitle("Confirm Text")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") {
                        model.cancelTextMessage()
                        dismiss()
                    }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Open Messages") {
                        showingComposer = true
                    }
                    .disabled(
                        draft.body.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                            || !MFMessageComposeViewController.canSendText()
                    )
                }
            }
            .sheet(isPresented: $showingComposer) {
                MessageComposerView(draft: draft) { result in
                    showingComposer = false
                    model.completeTextMessage(result: result, draft: draft)
                    if case .sent = result {
                        dismiss()
                    }
                }
            }
        }
    }
}

struct MessageComposerView: UIViewControllerRepresentable {
    let draft: TextMessageDraft
    let completion: (MessageComposeResult) -> Void

    func makeCoordinator() -> Coordinator {
        Coordinator(completion: completion)
    }

    func makeUIViewController(context: Context) -> MFMessageComposeViewController {
        let controller = MFMessageComposeViewController()
        controller.messageComposeDelegate = context.coordinator
        controller.recipients = [draft.phoneNumber]
        controller.body = draft.body
        return controller
    }

    func updateUIViewController(
        _ uiViewController: MFMessageComposeViewController,
        context: Context
    ) {}

    final class Coordinator: NSObject, MFMessageComposeViewControllerDelegate {
        let completion: (MessageComposeResult) -> Void

        init(completion: @escaping (MessageComposeResult) -> Void) {
            self.completion = completion
        }

        func messageComposeViewController(
            _ controller: MFMessageComposeViewController,
            didFinishWith result: MessageComposeResult
        ) {
            completion(result)
        }
    }
}
