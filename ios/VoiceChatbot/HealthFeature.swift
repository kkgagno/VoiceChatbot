import HealthKit
import Observation
import SwiftUI

enum HealthQuestionParser {
    static func isHealthQuestion(_ text: String) -> Bool {
        let lowered = text
            .folding(options: [.caseInsensitive, .diacriticInsensitive], locale: .current)
            .lowercased()
        let phrases = [
            "health data", "health summary", "apple health",
            "step count", "steps", "walking distance", "running distance",
            "active energy", "calories burned",
            "heart rate", "resting heart rate", "pulse",
            "sleep", "slept", "workout", "exercise",
            "weight", "body mass"
        ]
        return phrases.contains { lowered.contains($0) }
    }

    static func isLikelyFollowUp(_ text: String) -> Bool {
        let lowered = text
            .folding(options: [.caseInsensitive, .diacriticInsensitive], locale: .current)
            .lowercased()
            .trimmingCharacters(in: .whitespacesAndNewlines)
        let unrelatedTopics = [
            "weather", "calendar", "appointment", "send a text", "send a message",
            "text ", "message ", "youtube", "image", "video", "model", "comfy"
        ]
        if unrelatedTopics.contains(where: lowered.contains) {
            return false
        }
        let followUpPhrases = [
            "is that", "was that", "does that", "what about", "how about",
            "why", "normal", "healthy", "good", "bad", "better", "worse",
            "yesterday", "last night", "two nights ago", "compared", "trend",
            "should i", "could that", "what does that mean"
        ]
        return followUpPhrases.contains(where: lowered.contains)
            || lowered.split(separator: " ").count <= 12
    }
}

struct HealthDiscussionTurn {
    let role: String
    let text: String
}

@MainActor
@Observable
final class HealthService {
    private let store = HKHealthStore()

    var isAuthorized = false
    var isLoading = false
    var errorMessage: String?
    var latestSummary = ""

    private var readTypes: Set<HKObjectType> {
        var types = Set<HKObjectType>()
        [
            HKQuantityTypeIdentifier.stepCount,
            .distanceWalkingRunning,
            .activeEnergyBurned,
            .heartRate,
            .restingHeartRate,
            .bodyMass
        ].compactMap(HKObjectType.quantityType(forIdentifier:)).forEach {
            types.insert($0)
        }
        if let sleep = HKObjectType.categoryType(forIdentifier: .sleepAnalysis) {
            types.insert(sleep)
        }
        types.insert(HKObjectType.workoutType())
        return types
    }

    func requestAccess() async {
        guard HKHealthStore.isHealthDataAvailable() else {
            errorMessage = "Health data is not available on this device."
            return
        }
        do {
            try await store.requestAuthorization(toShare: [], read: readTypes)
            isAuthorized = true
            errorMessage = nil
        } catch {
            isAuthorized = false
            errorMessage = error.localizedDescription
        }
    }

    func modelContext(days: Int = 30) async throws -> String {
        if !isAuthorized {
            await requestAccess()
        }
        guard isAuthorized else {
            throw HealthFeatureError.accessRequired
        }

        isLoading = true
        defer { isLoading = false }

        let calendar = Calendar.current
        let now = Date.now
        let end = now
        let start = calendar.date(
            byAdding: .day,
            value: -(max(1, days) - 1),
            to: calendar.startOfDay(for: now)
        ) ?? now
        async let dailySteps = dailyCumulative(
            .stepCount,
            unit: .count(),
            start: start,
            end: end
        )
        async let dailyDistance = dailyCumulative(
            .distanceWalkingRunning,
            unit: .mile(),
            start: start,
            end: end
        )
        async let dailyEnergy = dailyCumulative(
            .activeEnergyBurned,
            unit: .kilocalorie(),
            start: start,
            end: end
        )
        async let heartRates = recentQuantities(
            .heartRate,
            unit: HKUnit.count().unitDivided(by: .minute()),
            start: start,
            end: end,
            limit: 60
        )
        async let restingRates = recentQuantities(
            .restingHeartRate,
            unit: HKUnit.count().unitDivided(by: .minute()),
            start: start,
            end: end,
            limit: 45
        )
        async let weights = recentQuantities(
            .bodyMass,
            unit: .pound(),
            start: start,
            end: end,
            limit: 20
        )
        async let sleep = sleepSamples(start: start, end: end)
        async let workouts = workoutSamples(start: start, end: end)

        let stepValues = try await dailySteps
        let distanceValues = try await dailyDistance
        let energyValues = try await dailyEnergy
        let heartValues = try await heartRates
        let restingValues = try await restingRates
        let weightValues = try await weights
        let sleepValues = try await sleep
        let workoutValues = try await workouts
        let activityRows = Self.dailyActivityText(
            steps: stepValues,
            distance: distanceValues,
            energy: energyValues,
            start: start,
            end: end
        )

        let context = """
        Live read-only Apple Health data from the user's iPhone and connected devices.
        Current local date/time: \(now.formatted(date: .complete, time: .complete))
        Time zone: \(TimeZone.current.identifier)
        Period covered: \(start.formatted(date: .abbreviated, time: .omitted)) through \(now.formatted(date: .abbreviated, time: .omitted))

        Daily activity (one row per local calendar day; missing means no readable sample):
        \(activityRows)

        Heart:
        - Recent heart-rate samples (BPM): \(Self.sampleText(heartValues, limit: 15))
        - Recent resting-heart-rate samples (BPM): \(Self.sampleText(restingValues, limit: 20))

        Body:
        - Recent weight samples (lb): \(Self.sampleText(weightValues, limit: 12))

        Sleep by night (the date is the morning/wake-up date):
        \(Self.sleepText(sleepValues))

        Workouts:
        \(Self.workoutText(workoutValues))
        """
        latestSummary = context
        errorMessage = nil
        return context
    }

    private func dailyCumulative(
        _ identifier: HKQuantityTypeIdentifier,
        unit: HKUnit,
        start: Date,
        end: Date
    ) async throws -> [Date: Double] {
        guard let type = HKQuantityType.quantityType(forIdentifier: identifier) else {
            return [:]
        }
        let predicate = HKQuery.predicateForSamples(
            withStart: start,
            end: end,
            options: .strictStartDate
        )
        return try await withCheckedThrowingContinuation { continuation in
            let query = HKStatisticsCollectionQuery(
                quantityType: type,
                quantitySamplePredicate: predicate,
                options: .cumulativeSum,
                anchorDate: Calendar.current.startOfDay(for: start),
                intervalComponents: DateComponents(day: 1)
            )
            query.initialResultsHandler = { _, result, error in
                if let error {
                    continuation.resume(throwing: error)
                } else {
                    var values = [Date: Double]()
                    result?.enumerateStatistics(from: start, to: end) { statistics, _ in
                        if let quantity = statistics.sumQuantity() {
                            values[Calendar.current.startOfDay(for: statistics.startDate)] =
                                quantity.doubleValue(for: unit)
                        }
                    }
                    continuation.resume(returning: values)
                }
            }
            store.execute(query)
        }
    }

    private func recentQuantities(
        _ identifier: HKQuantityTypeIdentifier,
        unit: HKUnit,
        start: Date,
        end: Date,
        limit: Int
    ) async throws -> [(Date, Double)] {
        guard let type = HKQuantityType.quantityType(forIdentifier: identifier) else {
            return []
        }
        let predicate = HKQuery.predicateForSamples(
            withStart: start,
            end: end,
            options: .strictStartDate
        )
        return try await withCheckedThrowingContinuation { continuation in
            let query = HKSampleQuery(
                sampleType: type,
                predicate: predicate,
                limit: limit,
                sortDescriptors: [
                    NSSortDescriptor(key: HKSampleSortIdentifierStartDate, ascending: false)
                ]
            ) { _, samples, error in
                if let error {
                    continuation.resume(throwing: error)
                    return
                }
                let values = (samples as? [HKQuantitySample] ?? []).map {
                    ($0.startDate, $0.quantity.doubleValue(for: unit))
                }
                continuation.resume(returning: values)
            }
            store.execute(query)
        }
    }

    private func sleepSamples(start: Date, end: Date) async throws -> [HKCategorySample] {
        guard let type = HKCategoryType.categoryType(forIdentifier: .sleepAnalysis) else {
            return []
        }
        let predicate = HKQuery.predicateForSamples(
            withStart: start,
            end: end,
            options: []
        )
        return try await samples(type: type, predicate: predicate, limit: 1_000)
    }

    private func workoutSamples(start: Date, end: Date) async throws -> [HKWorkout] {
        let predicate = HKQuery.predicateForSamples(
            withStart: start,
            end: end,
            options: .strictStartDate
        )
        let values: [HKWorkout] = try await samples(
            type: HKObjectType.workoutType(),
            predicate: predicate,
            limit: 100
        )
        return values
    }

    private func samples<T: HKSample>(
        type: HKSampleType,
        predicate: NSPredicate,
        limit: Int
    ) async throws -> [T] {
        try await withCheckedThrowingContinuation { continuation in
            let query = HKSampleQuery(
                sampleType: type,
                predicate: predicate,
                limit: limit,
                sortDescriptors: [
                    NSSortDescriptor(key: HKSampleSortIdentifierStartDate, ascending: false)
                ]
            ) { _, samples, error in
                if let error {
                    continuation.resume(throwing: error)
                } else {
                    continuation.resume(returning: samples as? [T] ?? [])
                }
            }
            store.execute(query)
        }
    }

    private static func sampleText(
        _ samples: [(Date, Double)],
        limit: Int
    ) -> String {
        guard !samples.isEmpty else { return "No data" }
        return samples.prefix(limit).map {
            "\($0.0.formatted(date: .abbreviated, time: .shortened)) = \($0.1.formatted(.number.precision(.fractionLength(1))))"
        }.joined(separator: "; ")
    }

    private static func dailyActivityText(
        steps: [Date: Double],
        distance: [Date: Double],
        energy: [Date: Double],
        start: Date,
        end: Date
    ) -> String {
        let calendar = Calendar.current
        var rows = [String]()
        var day = calendar.startOfDay(for: start)
        let lastDay = calendar.startOfDay(for: end)
        while day <= lastDay {
            let stepText = steps[day].map {
                $0.formatted(.number.precision(.fractionLength(0)))
            } ?? "missing"
            let distanceText = distance[day].map {
                $0.formatted(.number.precision(.fractionLength(2)))
            } ?? "missing"
            let energyText = energy[day].map {
                $0.formatted(.number.precision(.fractionLength(0)))
            } ?? "missing"
            rows.append(
                "- \(day.formatted(date: .complete, time: .omitted)): "
                    + "\(stepText) steps; \(distanceText) miles; \(energyText) active kcal"
            )
            guard let next = calendar.date(byAdding: .day, value: 1, to: day) else {
                break
            }
            day = next
        }
        return rows.joined(separator: "\n")
    }

    private static func sleepText(_ samples: [HKCategorySample]) -> String {
        guard !samples.isEmpty else { return "- No sleep samples found." }
        let calendar = Calendar.current
        let grouped = Dictionary(grouping: samples) {
            calendar.startOfDay(for: $0.endDate)
        }
        return grouped.keys.sorted(by: >).prefix(30).map { wakeDate in
            let night = grouped[wakeDate] ?? []
            let core = duration(
                night.filter { $0.value == HKCategoryValueSleepAnalysis.asleepCore.rawValue }
            )
            let deep = duration(
                night.filter { $0.value == HKCategoryValueSleepAnalysis.asleepDeep.rawValue }
            )
            let rem = duration(
                night.filter { $0.value == HKCategoryValueSleepAnalysis.asleepREM.rawValue }
            )
            let unspecified = duration(
                night.filter {
                    $0.value == HKCategoryValueSleepAnalysis.asleep.rawValue
                        || $0.value == HKCategoryValueSleepAnalysis.asleepUnspecified.rawValue
                }
            )
            let awake = duration(
                night.filter { $0.value == HKCategoryValueSleepAnalysis.awake.rawValue }
            )
            let stagedTotal = core + deep + rem
            let total = stagedTotal > 0 ? stagedTotal : unspecified
            let start = night.map(\.startDate).min()
            let end = night.map(\.endDate).max()
            let interval: String
            if let start, let end {
                interval =
                    "\(start.formatted(date: .abbreviated, time: .shortened))–"
                    + "\(end.formatted(date: .omitted, time: .shortened))"
            } else {
                interval = "times unavailable"
            }
            return "- Night ending \(wakeDate.formatted(date: .complete, time: .omitted)): "
                + "\(hours(total)) asleep; core \(hours(core)); deep \(hours(deep)); "
                + "REM \(hours(rem)); awake \(hours(awake)); interval \(interval)"
        }.joined(separator: "\n")
    }

    private static func duration(_ samples: [HKCategorySample]) -> TimeInterval {
        samples.reduce(0) { $0 + $1.endDate.timeIntervalSince($1.startDate) }
    }

    private static func hours(_ seconds: TimeInterval) -> String {
        (seconds / 3600).formatted(.number.precision(.fractionLength(2))) + "h"
    }

    private static func workoutText(_ workouts: [HKWorkout]) -> String {
        guard !workouts.isEmpty else { return "- No workouts found." }
        return workouts.prefix(30).map {
            let minutes = $0.duration / 60
            return "- \($0.startDate.formatted(date: .abbreviated, time: .shortened)): activity \($0.workoutActivityType.rawValue), \(minutes.formatted(.number.precision(.fractionLength(0)))) minutes"
        }.joined(separator: "\n")
    }
}

enum HealthFeatureError: LocalizedError {
    case accessRequired

    var errorDescription: String? {
        "Health access is required."
    }
}

struct HealthView: View {
    @Bindable var model: AppModel

    var body: some View {
        ZStack {
            AppBackground()
            if model.health.isAuthorized {
                List {
                    Section("Read-only access") {
                        Label("Steps and distance", systemImage: "figure.walk")
                        Label("Heart rate", systemImage: "heart.fill")
                        Label("Sleep", systemImage: "bed.double.fill")
                        Label("Workouts and active energy", systemImage: "figure.run")
                        Label("Weight", systemImage: "scalemass.fill")
                    }
                    Section {
                        Text("Ask in Chat: “How was my sleep last night?” or “Compare my steps this week.”")
                            .foregroundStyle(.secondary)
                    }
                    if let error = model.health.errorMessage {
                        Section {
                            Text(error).foregroundStyle(.red)
                        }
                    }
                }
                .scrollContentBackground(.hidden)
            } else {
                ContentUnavailableView {
                    Label("Apple Health", systemImage: "heart.text.square.fill")
                } description: {
                    Text("Allow read-only access to the health categories you choose.")
                } actions: {
                    Button("Choose Health Data") {
                        Task { await model.health.requestAccess() }
                    }
                    .buttonStyle(.borderedProminent)
                    .tint(.cyan)
                }
            }
        }
        .navigationTitle("Health")
        .navigationBarTitleDisplayMode(.inline)
    }
}
