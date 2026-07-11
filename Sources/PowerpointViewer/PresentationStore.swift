import SwiftUI
import Combine

/// Observable state for the currently open presentation.
///
/// Owns the lifecycle of the extracted archive: it deletes the previous scratch
/// directory whenever a new file is opened and on deinit.
@MainActor
final class PresentationStore: ObservableObject {
    @Published var presentation: Presentation?
    @Published var currentIndex: Int = 0
    @Published var fileName: String?
    @Published var errorMessage: String?
    @Published var isLoading: Bool = false
    @Published var isPresenting: Bool = false

    private var extractedRoot: URL?

    var slideCount: Int { presentation?.slides.count ?? 0 }

    var currentSlide: Slide? {
        guard let slides = presentation?.slides, slides.indices.contains(currentIndex) else { return nil }
        return slides[currentIndex]
    }

    func open(url: URL) {
        isLoading = true
        errorMessage = nil
        let needsScope = url.startAccessingSecurityScopedResource()

        Task {
            defer { if needsScope { url.stopAccessingSecurityScopedResource() } }
            do {
                let root = try Unzip.extract(url)
                let parser = PPTXParser(extractedRoot: root)
                let pres = try parser.parse()
                guard !pres.slides.isEmpty else {
                    throw PPTXParser.ParseError.missing("any slides")
                }
                await MainActor.run {
                    self.cleanup()
                    self.extractedRoot = root
                    self.presentation = pres
                    self.currentIndex = 0
                    self.fileName = url.lastPathComponent
                    self.isLoading = false
                }
            } catch {
                await MainActor.run {
                    self.errorMessage = (error as? LocalizedError)?.errorDescription
                        ?? error.localizedDescription
                    self.isLoading = false
                }
            }
        }
    }

    func goNext() {
        guard currentIndex < slideCount - 1 else { return }
        currentIndex += 1
    }

    func goPrevious() {
        guard currentIndex > 0 else { return }
        currentIndex -= 1
    }

    func go(to index: Int) {
        guard presentation?.slides.indices.contains(index) == true else { return }
        currentIndex = index
    }

    private func cleanup() {
        if let root = extractedRoot {
            try? FileManager.default.removeItem(at: root)
        }
        extractedRoot = nil
    }
}
