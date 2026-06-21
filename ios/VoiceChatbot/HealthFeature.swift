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
        let start = calendar.date(byAdding: .day, value: -days, to: now) ?? now
        let todayStart = calendar.startOfDay(for: now)
        async let todaySteps = cumulative(
            .stepCount,
            unit: .count(),
            start: todayStart,
            end: now
        )
        async let periodSteps = cumulative(
            .stepCount,
            unit: .count(),
            start: start,
            end: now
        )
        async let distance = cumulative(
            .distanceWalkingRunning,
            unit: .mile(),
            start: start,
            end: now
        )
        async let energy = cumulative(
            .activeEnergyBurned,
            unit: .kilocalorie(),
            start: start,
            end: now
        )
        async let heartRates = recentQuantities(
            .heartRate,
            unit: HKUnit.count().unitDivided(by: .minute()),
            start: start,
            end: now,
            limit: 200
        )
        async let restingRates = recentQuantities(
            .restingHeartRate,
            unit: HKUnit.count().unitDivided(by: .minute()),
            start: start,
            end: now,
            limit: 60
        )
        async let weights = recentQuantities(
            .bodyMass,
            unit: .pound(),
            start: start,
            end: now,
            limit: 30
        )
        async let sleep = sleepSamples(start: start, end: now)
        async let workouts = workoutSamples(start: start, end: now)

        let heartValues = try await heartRates
        let restingValues = try await restingRates
        let weightValues = try await weights
        let sleepValues = try await sleep
        let workoutValues = try await workouts

        let context = """
        Live read-only Apple Health data from the user's iPhone and connected devices.
        Current local date/time: \(now.formatted(date: .complete, time: .complete))
        Time zone: \(TimeZone.current.identifier)
        Period covered: \(start.formatted(date: .abbreviated, time: .omitted)) through \(now.formatted(date: .abbreviated, time: .omitted))

        Activity:
        - Steps today: \(Self.number(try await todaySteps, decimals: 0))
        - Steps during period: \(Self.number(try await periodSteps, decimals: 0))
        - Walking/running distance during period: \(Self.number(try await distance, decimals: 2)) miles
        - Active energy during period: \(Self.number(try await energy, decimals: 0)) kcal

        Heart:
        - Recent heart-rate samples (BPM): \(Self.sampleText(heartValues))
        - Recent resting-heart-rate samples (BPM): \(Self.sampleText(restingValues))

        Body:
        - Recent weight samples (lb): \(Self.sampleText(weightValues))

        Sleep:
        \(Self.sleepText(sleepValues))

        Workouts:
        \(Self.workoutText(workoutValues))
        """
        latestSummary = context
        errorMessage = nil
        return context
    }

    private func cumulative(
        _ identifier: HKQuantityTypeIdentifier,
        unit: HKUnit,
        start: Date,
        end: Date
    ) async throws -> Double? {
        guard let type = HKQuantityType.quantityType(forIdentifier: identifier) else {
            return nil
        }
        let predicate = HKQuery.predicateForSamples(
            withStart: start,
            end: end,
            options: .strictStartDate
        )
        return try await withCheckedThrowingContinuation { continuation in
            let query = HKStatisticsQuery(
                quantityType: type,
                quantitySamplePredicate: predicate,
                options: .cumulativeSum
            ) { _, result, error in
                if let error {
                    continuation.resume(throwing: error)
                } else {
                    continuation.resume(
                        returning: result?.sumQuantity()?.doubleValue(for: unit)
                    )
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
        return try await samples(type: type, predicate: predicate, limit: 200)
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

    private static func number(_ value: Double?, decimals: Int) -> String {
        guard let value else { return "No data" }
        return value.formatted(.number.precision(.fractionLength(decimals)))
    }

    private static func sampleText(_ samples: [(Date, Double)]) -> String {
        guard !samples.isEmpty else { return "No data" }
        return samples.prefix(30).map {
            "\($0.0.formatted(date: .abbreviated, time: .shortened)) = \($0.1.formatted(.number.precision(.fractionLength(1))))"
        }.joined(separator: "; ")
    }

    private static func sleepText(_ samples: [HKCategorySample]) -> String {
        let asleepValues: Set<Int> = [
            HKCategoryValueSleepAnalysis.asleep.rawValue,
            HKCategoryValueSleepAnalysis.asleepCore.rawValue,
            HKCategoryValueSleepAnalysis.asleepDeep.rawValue,
            HKCategoryValueSleepAnalysis.asleepREM.rawValue,
            HKCategoryValueSleepAnalysis.asleepUnspecified.rawValue
        ]
        let asleep = samples.filter { asleepValues.contains($0.value) }
        guard !asleep.isEmpty else { return "- No sleep samples found." }
        return asleep.prefix(40).map {
            let hours = $0.endDate.timeIntervalSince($0.startDate) / 3600
            return "- \($0.startDate.formatted(date: .abbreviated, time: .shortened)) to \($0.endDate.formatted(date: .omitted, time: .shortened)): \(hours.formatted(.number.precision(.fractionLength(2)))) hours"
        }.joined(separator: "\n")
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
