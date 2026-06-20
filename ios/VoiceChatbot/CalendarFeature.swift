import EventKit
import Observation
import SwiftUI

struct CalendarEventSummary: Identifiable, Equatable {
    let id: String
    let title: String
    let startDate: Date
    let endDate: Date
    let calendarTitle: String
    let isAllDay: Bool
}

struct CalendarEventDraft: Identifiable, Equatable {
    let id = UUID()
    var title: String
    var startDate: Date
    var endDate: Date
    var notes = ""
}

enum CalendarCommand {
    case create(CalendarEventDraft)
    case list
}

enum CalendarCommandParser {
    static func parse(_ text: String, now: Date = .now) -> CalendarCommand? {
        let lowered = text.lowercased()
        let mentionsCalendar = lowered.contains("calendar")
            || lowered.contains("appointment")
            || lowered.contains("event")

        let asksToList = mentionsCalendar
            && ["what", "show", "list", "upcoming", "do i have", "what's", "whats"]
                .contains { lowered.contains($0) }
        if asksToList {
            return .list
        }

        let asksToCreate = ["add", "create", "schedule", "put"]
            .contains { lowered.contains($0) }
        guard asksToCreate, mentionsCalendar else { return nil }

        guard let detector = try? NSDataDetector(
            types: NSTextCheckingResult.CheckingType.date.rawValue
        ) else { return nil }
        let fullRange = NSRange(text.startIndex..<text.endIndex, in: text)
        guard let match = detector.firstMatch(in: text, range: fullRange),
              var startDate = match.date
        else { return nil }

        if startDate < now.addingTimeInterval(-60) {
            startDate = Calendar.current.date(byAdding: .year, value: 1, to: startDate) ?? startDate
        }

        let title = extractedTitle(from: text, dateRange: match.range)
        return .create(
            CalendarEventDraft(
                title: title.isEmpty ? "New event" : title,
                startDate: startDate,
                endDate: startDate.addingTimeInterval(60 * 60)
            )
        )
    }

    private static func extractedTitle(from text: String, dateRange: NSRange) -> String {
        if let expression = try? NSRegularExpression(
            pattern: #"\b(?:for|called|titled|named)\s+"#,
            options: .caseInsensitive
        ) {
            let range = NSRange(text.startIndex..<text.endIndex, in: text)
            if let match = expression.matches(in: text, range: range).last,
               let markerRange = Range(match.range, in: text) {
                return cleanedTitle(String(text[markerRange.upperBound...]))
            }
        }

        var remainder = text
        if let swiftRange = Range(dateRange, in: remainder) {
            remainder.removeSubrange(swiftRange)
        }
        remainder = remainder.replacingOccurrences(
            of: #"\b(add|create|schedule|put|a|an|the|to|on|my|calendar|event|appointment)\b"#,
            with: " ",
            options: [.regularExpression, .caseInsensitive]
        )
        return cleanedTitle(remainder)
    }

    private static func cleanedTitle(_ value: String) -> String {
        let words = value
            .replacingOccurrences(of: #"\s+"#, with: " ", options: .regularExpression)
            .trimmingCharacters(in: .whitespacesAndNewlines.union(.punctuationCharacters))
        guard let first = words.first else { return "" }
        return String(first).uppercased() + String(words.dropFirst())
    }
}

@MainActor
@Observable
final class CalendarService {
    private let store = EKEventStore()

    var events = [CalendarEventSummary]()
    var authorizationStatus = EKEventStore.authorizationStatus(for: .event)
    var errorMessage: String?
    var isLoading = false

    var hasFullAccess: Bool {
        authorizationStatus == .fullAccess
    }

    func requestAccessAndLoad() async {
        do {
            if !hasFullAccess {
                _ = try await store.requestFullAccessToEvents()
                authorizationStatus = EKEventStore.authorizationStatus(for: .event)
            }
            guard hasFullAccess else {
                errorMessage = "Calendar access was not granted."
                return
            }
            loadUpcoming()
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    func loadUpcoming(days: Int = 30) {
        guard hasFullAccess else { return }
        isLoading = true
        defer { isLoading = false }

        let start = Date.now.addingTimeInterval(-60)
        let end = Calendar.current.date(byAdding: .day, value: days, to: start) ?? start
        let predicate = store.predicateForEvents(withStart: start, end: end, calendars: nil)
        events = store.events(matching: predicate)
            .sorted { $0.startDate < $1.startDate }
            .map {
                CalendarEventSummary(
                    id: $0.eventIdentifier ?? UUID().uuidString,
                    title: $0.title ?? "Untitled event",
                    startDate: $0.startDate,
                    endDate: $0.endDate,
                    calendarTitle: $0.calendar.title,
                    isAllDay: $0.isAllDay
                )
            }
        errorMessage = nil
    }

    func save(_ draft: CalendarEventDraft) throws {
        guard hasFullAccess else {
            throw CalendarFeatureError.accessRequired
        }
        guard let calendar = store.defaultCalendarForNewEvents else {
            throw CalendarFeatureError.noWritableCalendar
        }

        let event = EKEvent(eventStore: store)
        event.title = draft.title
        event.startDate = draft.startDate
        event.endDate = max(draft.endDate, draft.startDate.addingTimeInterval(5 * 60))
        event.notes = draft.notes.isEmpty ? nil : draft.notes
        event.calendar = calendar
        try store.save(event, span: .thisEvent, commit: true)
        loadUpcoming()
    }

    func spokenSummary(limit: Int = 8) -> String {
        let upcoming = Array(events.prefix(limit))
        guard !upcoming.isEmpty else {
            return "You have no events on your calendar in the next 30 days."
        }
        let lines = upcoming.map { event in
            let when = event.startDate.formatted(date: .abbreviated, time: .shortened)
            return "\(event.title), \(when)"
        }
        return "Your upcoming events are: " + lines.joined(separator: "; ") + "."
    }
}

enum CalendarFeatureError: LocalizedError {
    case accessRequired
    case noWritableCalendar

    var errorDescription: String? {
        switch self {
        case .accessRequired:
            "Calendar access is required."
        case .noWritableCalendar:
            "No writable calendar is available on this iPhone."
        }
    }
}

struct CalendarView: View {
    @Bindable var model: AppModel

    var body: some View {
        ZStack {
            AppBackground()
            Group {
                if model.calendar.hasFullAccess {
                    eventList
                } else {
                    permissionView
                }
            }
        }
        .navigationTitle("Calendar")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            if model.calendar.hasFullAccess {
                ToolbarItem(placement: .topBarTrailing) {
                    Button {
                        model.pendingCalendarEvent = CalendarEventDraft(
                            title: "New event",
                            startDate: .now,
                            endDate: .now.addingTimeInterval(60 * 60)
                        )
                    } label: {
                        Image(systemName: "plus")
                    }
                    .accessibilityLabel("Add calendar event")
                }
            }
        }
        .task {
            if model.calendar.hasFullAccess {
                model.calendar.loadUpcoming()
            }
        }
        .refreshable {
            model.calendar.loadUpcoming()
        }
    }

    private var permissionView: some View {
        ContentUnavailableView {
            Label("Calendar Access", systemImage: "calendar.badge.plus")
        } description: {
            Text("Allow access to view upcoming events and create events after you confirm them.")
        } actions: {
            Button("Allow Calendar Access") {
                Task { await model.calendar.requestAccessAndLoad() }
            }
            .buttonStyle(.borderedProminent)
            .tint(.cyan)
        }
    }

    private var eventList: some View {
        List {
            if let error = model.calendar.errorMessage {
                Text(error)
                    .foregroundStyle(.red)
            }
            if model.calendar.events.isEmpty {
                ContentUnavailableView(
                    "No Upcoming Events",
                    systemImage: "calendar",
                    description: Text("Nothing is scheduled during the next 30 days.")
                )
                .listRowBackground(Color.clear)
            } else {
                ForEach(model.calendar.events) { event in
                    VStack(alignment: .leading, spacing: 5) {
                        Text(event.title)
                            .font(.headline)
                        Text(
                            event.isAllDay
                                ? event.startDate.formatted(date: .abbreviated, time: .omitted)
                                : event.startDate.formatted(date: .abbreviated, time: .shortened)
                        )
                        .foregroundStyle(.secondary)
                        Text(event.calendarTitle)
                            .font(.caption)
                            .foregroundStyle(.cyan)
                    }
                    .padding(.vertical, 4)
                }
            }
        }
        .scrollContentBackground(.hidden)
    }
}

struct CalendarConfirmationView: View {
    @Bindable var model: AppModel
    @State private var draft: CalendarEventDraft
    @Environment(\.dismiss) private var dismiss

    init(model: AppModel, draft: CalendarEventDraft) {
        self.model = model
        _draft = State(initialValue: draft)
    }

    var body: some View {
        NavigationStack {
            Form {
                Section("Event") {
                    TextField("Title", text: $draft.title)
                    DatePicker("Starts", selection: $draft.startDate)
                    DatePicker(
                        "Ends",
                        selection: $draft.endDate,
                        in: draft.startDate...
                    )
                    TextField("Notes", text: $draft.notes, axis: .vertical)
                        .lineLimit(2...5)
                }

                Section {
                    Text("Nothing is added until you tap Add Event.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
            .navigationTitle("Confirm Event")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") {
                        model.cancelCalendarEvent()
                        dismiss()
                    }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Add Event") {
                        if model.saveCalendarEvent(draft) {
                            dismiss()
                        }
                    }
                    .disabled(draft.title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                }
            }
        }
    }
}
