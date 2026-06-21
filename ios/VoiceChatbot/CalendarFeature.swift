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
    let isRecurring: Bool
}

struct CalendarEventDraft: Identifiable, Equatable {
    let id = UUID()
    var title: String
    var startDate: Date
    var endDate: Date
    var notes = ""
}

struct SavedCalendarEvent {
    let title: String
    let startDate: Date
    let calendarTitle: String
}

struct CalendarDeletionItem: Identifiable, Equatable {
    let event: CalendarEventSummary
    var deleteFutureEvents = false

    var id: String { event.id }
}

struct CalendarDeletionDraft: Identifiable, Equatable {
    let id = UUID()
    var items: [CalendarDeletionItem]
}

enum CalendarCommand {
    case create
    case delete
    case list
}

enum CalendarCommandParser {
    static func parse(_ text: String) -> CalendarCommand? {
        var lowered = text
            .folding(options: [.caseInsensitive, .diacriticInsensitive], locale: .current)
            .lowercased()
            .trimmingCharacters(in: .whitespacesAndNewlines)

        // Speech recognition commonly turns "add a calendar event" into
        // "I had a calendar event." Correct that only when the remainder
        // clearly describes a future event, so a genuine statement about a
        // past event is not converted into an action.
        let futureEventMarkers = [
            "tonight", "tomorrow", "next ", " at ", " for ", " on ",
            "this morning", "this afternoon", "this evening"
        ]
        let soundsLikeMisheardAdd =
            lowered.hasPrefix("i had a calendar event")
                || lowered.hasPrefix("i had an event")
                || lowered.hasPrefix("i add a calendar event")
                || lowered.hasPrefix("i add an event")
        if soundsLikeMisheardAdd,
           futureEventMarkers.contains(where: lowered.contains) {
            if lowered.hasPrefix("i had a calendar event") {
                lowered.replaceSubrange(
                    lowered.startIndex..<lowered.index(
                        lowered.startIndex,
                        offsetBy: "i had a calendar event".count
                    ),
                    with: "add a calendar event"
                )
            } else if lowered.hasPrefix("i had an event") {
                lowered.replaceSubrange(
                    lowered.startIndex..<lowered.index(
                        lowered.startIndex,
                        offsetBy: "i had an event".count
                    ),
                    with: "add an event"
                )
            }
        }
        let mentionsCalendar = lowered.contains("calendar")
            || lowered.contains("appointment")
            || lowered.contains("event")
            || lowered.contains("schedule")

        let asksToDelete = ["delete", "remove", "cancel", "erase"]
            .contains { lowered.contains($0) }
        if asksToDelete, mentionsCalendar {
            return .delete
        }

        let calendarQuestion = mentionsCalendar
            || ["am i free", "am i busy", "what do i have", "what have i got", "my schedule"]
                .contains { lowered.contains($0) }
        let asksToList = calendarQuestion
            && ["what", "show", "list", "upcoming", "do i have", "what's", "whats", "free", "busy", "schedule"]
                .contains { lowered.contains($0) }
        if asksToList {
            return .list
        }

        let asksToCreate = ["add", "create", "schedule", "put", "set up", "make"]
            .contains { lowered.contains($0) }
        guard asksToCreate, mentionsCalendar else { return nil }

        return .create
    }
}

struct CalendarAIDraft: Decodable {
    let title: String
    let start: String
    let end: String
    let notes: String?

    func eventDraft() throws -> CalendarEventDraft {
        guard let startDate = Self.parseDate(start),
              let endDate = Self.parseDate(end)
        else {
            throw CalendarFeatureError.invalidAIDraft
        }
        return CalendarEventDraft(
            title: title.trimmingCharacters(in: .whitespacesAndNewlines),
            startDate: startDate,
            endDate: max(endDate, startDate.addingTimeInterval(5 * 60)),
            notes: notes?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        )
    }

    static func decode(from response: String) throws -> CalendarAIDraft {
        let trimmed = response.trimmingCharacters(in: .whitespacesAndNewlines)
        let json: String
        if let start = trimmed.firstIndex(of: "{"),
           let end = trimmed.lastIndex(of: "}") {
            json = String(trimmed[start...end])
        } else {
            throw CalendarFeatureError.invalidAIDraft
        }
        return try JSONDecoder().decode(CalendarAIDraft.self, from: Data(json.utf8))
    }

    private static func parseDate(_ value: String) -> Date? {
        let fractional = ISO8601DateFormatter()
        fractional.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return fractional.date(from: value) ?? ISO8601DateFormatter().date(from: value)
    }
}

struct CalendarAIDeleteSelection: Decodable {
    let eventIDs: [String]
    let futureSeriesEventIDs: [String]?

    static func decode(from response: String) throws -> CalendarAIDeleteSelection {
        let trimmed = response.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let start = trimmed.firstIndex(of: "{"),
              let end = trimmed.lastIndex(of: "}")
        else {
            throw CalendarFeatureError.invalidAIDeleteSelection
        }
        return try JSONDecoder().decode(
            CalendarAIDeleteSelection.self,
            from: Data(trimmed[start...end].utf8)
        )
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

    var destinationCalendarTitle: String {
        store.defaultCalendarForNewEvents?.title ?? "Default Calendar"
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
                    isAllDay: $0.isAllDay,
                    isRecurring: $0.hasRecurrenceRules
                )
            }
        errorMessage = nil
    }

    func save(_ draft: CalendarEventDraft) throws -> SavedCalendarEvent {
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

        guard let identifier = event.eventIdentifier,
              let verified = store.event(withIdentifier: identifier)
        else {
            throw CalendarFeatureError.saveVerificationFailed
        }
        loadUpcoming()
        return SavedCalendarEvent(
            title: verified.title ?? draft.title,
            startDate: verified.startDate,
            calendarTitle: verified.calendar.title
        )
    }

    func delete(_ draft: CalendarDeletionDraft) throws -> Int {
        guard hasFullAccess else {
            throw CalendarFeatureError.accessRequired
        }
        var deleted = 0
        for item in draft.items {
            guard let event = store.event(withIdentifier: item.event.id) else { continue }
            let span: EKSpan = item.deleteFutureEvents && item.event.isRecurring
                ? .futureEvents
                : .thisEvent
            try store.remove(event, span: span, commit: false)
            deleted += 1
        }
        guard deleted > 0 else {
            throw CalendarFeatureError.noMatchingEvents
        }
        try store.commit()
        loadUpcoming()
        return deleted
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

    func modelContext(limit: Int = 100) -> String {
        let records = events.prefix(limit).map { event in
            let start = event.startDate.formatted(date: .numeric, time: .complete)
            let end = event.endDate.formatted(date: .numeric, time: .complete)
            return "- id: \(event.id) | title: \(event.title) | start: \(start) | end: \(end) | all-day: \(event.isAllDay) | recurring: \(event.isRecurring) | calendar: \(event.calendarTitle)"
        }
        let eventText = records.isEmpty
            ? "- No upcoming events found."
            : records.joined(separator: "\n")
        return """
        The following is live calendar data read from the user's iPhone.
        Current local date and time: \(Date.now.formatted(date: .complete, time: .complete))
        Time zone: \(TimeZone.current.identifier)
        Upcoming calendar events:
        \(eventText)
        """
    }
}

enum CalendarFeatureError: LocalizedError {
    case accessRequired
    case noWritableCalendar
    case invalidAIDraft
    case saveVerificationFailed
    case invalidAIDeleteSelection
    case noMatchingEvents

    var errorDescription: String? {
        switch self {
        case .accessRequired:
            "Calendar access is required."
        case .noWritableCalendar:
            "No writable calendar is available on this iPhone."
        case .invalidAIDraft:
            "The AI could not produce a valid calendar event. Please include a date and time and try again."
        case .saveVerificationFailed:
            "The event could not be verified after saving."
        case .invalidAIDeleteSelection:
            "The AI could not identify calendar events to delete."
        case .noMatchingEvents:
            "No matching calendar events were found."
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

                Section("Verify before saving") {
                    LabeledContent("Full date") {
                        Text(draft.startDate.formatted(date: .complete, time: .shortened))
                            .multilineTextAlignment(.trailing)
                    }
                    LabeledContent("Year") {
                        Text(draft.startDate.formatted(.dateTime.year()))
                    }
                    LabeledContent("Time zone") {
                        Text(TimeZone.current.identifier)
                    }
                    LabeledContent("Calendar") {
                        Text(model.calendar.destinationCalendarTitle)
                    }
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

struct CalendarDeletionConfirmationView: View {
    @Bindable var model: AppModel
    @State private var draft: CalendarDeletionDraft
    @Environment(\.dismiss) private var dismiss

    init(model: AppModel, draft: CalendarDeletionDraft) {
        self.model = model
        _draft = State(initialValue: draft)
    }

    var body: some View {
        NavigationStack {
            List {
                Section {
                    Text("Only the events shown below will be deleted.")
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                }

                ForEach($draft.items) { $item in
                    Section {
                        VStack(alignment: .leading, spacing: 6) {
                            Text(item.event.title)
                                .font(.headline)
                            Text(item.event.startDate.formatted(date: .complete, time: .shortened))
                            Text(item.event.calendarTitle)
                                .font(.caption)
                                .foregroundStyle(.cyan)
                        }
                        if item.event.isRecurring {
                            Toggle("Delete this and future events", isOn: $item.deleteFutureEvents)
                            Text(
                                item.deleteFutureEvents
                                    ? "This occurrence and all later occurrences will be deleted."
                                    : "Only this occurrence will be deleted."
                            )
                            .font(.caption)
                            .foregroundStyle(.secondary)
                        }
                    }
                }
            }
            .navigationTitle("Confirm Deletion")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") {
                        model.cancelCalendarDeletion()
                        dismiss()
                    }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button(
                        draft.items.count == 1
                            ? "Delete Event"
                            : "Delete \(draft.items.count) Events",
                        role: .destructive
                    ) {
                        if model.deleteCalendarEvents(draft) {
                            dismiss()
                        }
                    }
                }
            }
        }
    }
}
