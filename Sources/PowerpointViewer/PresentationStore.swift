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
    private let presentationWindow = PresentationWindowController()

    var slideCount: Int { presentation?.slides.count ?? 0 }

    var currentSlide: Slide? {
        guard let slides = presentation?.slides, slides.indices.contains(currentIndex) else { return nil }
        return slides[currentIndex]
    }

    /// The slide after the current one, or nil at the end of the deck.
    var nextSlide: Slide? {
        guard let slides = presentation?.slides, slides.indices.contains(currentIndex + 1) else { return nil }
        return slides[currentIndex + 1]
    }

    // MARK: - Presentation mode

    /// When the current presentation started; drives the console's elapsed timer.
    @Published var presentationStartDate: Date?

    /// Set while the "End presentation?" confirmation is on screen. Esc asks
    /// rather than ending outright — an accidental keypress mid-service should
    /// not drop the audience back to the desktop.
    @Published var isConfirmingEnd = false

    /// Called by Esc from either window.
    func requestEndPresentation() {
        guard isPresenting else { return }
        isConfirmingEnd = true
        presentationWindow.setConfirming(true)
    }

    func cancelEndPresentation() {
        isConfirmingEnd = false
        presentationWindow.setConfirming(false)
    }

    func startPresentation() {
        guard presentation != nil, !isPresenting else { return }
        isPresenting = true
        buildIndex = 0
        presentationStartDate = Date()
        presentationWindow.onEnd = { [weak self] in self?.requestEndPresentation() }
        presentationWindow.show(store: self)
    }

    func endPresentation() {
        isConfirmingEnd = false
        presentationWindow.close()
        isPresenting = false
        presentationStartDate = nil
    }

    func open(url: URL) {
        isLoading = true
        errorMessage = nil
        let needsScope = url.startAccessingSecurityScopedResource()

        Task {
            defer { if needsScope { url.stopAccessingSecurityScopedResource() } }
            do {
                // Convert legacy binary .ppt to .pptx first if needed.
                let source = try LegacyPPT.resolve(url)
                let root = try Unzip.extract(source)
                let parser = PPTXParser(extractedRoot: root)
                let pres = try parser.parse()
                guard !pres.slides.isEmpty else {
                    throw PPTXParser.ParseError.missing("any slides")
                }
                await MainActor.run {
                    self.endPresentation()
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

    /// Number of build steps already executed on the current slide (presenting).
    @Published var buildIndex: Int = 0

    /// Shapes currently invisible on the presented slide: entrance targets whose
    /// step hasn't run yet, plus exit targets whose step has.
    var hiddenShapeIDs: Set<String> {
        guard isPresenting, let slide = currentSlide, !slide.buildSteps.isEmpty else { return [] }
        var hidden: Set<String> = []
        for (i, step) in slide.buildSteps.enumerated() {
            if i >= buildIndex {
                hidden.formUnion(step.reveals)
            } else {
                hidden.formUnion(step.hides)
                hidden.subtract(step.reveals)
            }
        }
        return hidden
    }

    /// Paragraphs currently invisible on the presented slide, keyed by shape id —
    /// paragraph-level entrance targets whose step hasn't run yet.
    var hiddenParagraphs: [String: Set<Int>] {
        guard isPresenting, let slide = currentSlide, !slide.buildSteps.isEmpty else { return [:] }
        var hidden: [String: Set<Int>] = [:]
        for (i, step) in slide.buildSteps.enumerated() where i >= buildIndex {
            for (spid, paras) in step.paragraphReveals {
                hidden[spid, default: []].formUnion(paras)
            }
        }
        return hidden
    }

    var canGoNext: Bool {
        if isPresenting, let slide = currentSlide, buildIndex < slide.buildSteps.count { return true }
        return currentIndex < slideCount - 1
    }

    var canGoPrevious: Bool {
        if isPresenting && buildIndex > 0 { return true }
        return currentIndex > 0
    }

    func goNext() {
        // While presenting, a click first plays the slide's next build step.
        if isPresenting, let slide = currentSlide, buildIndex < slide.buildSteps.count {
            buildIndex += 1
            return
        }
        guard currentIndex < slideCount - 1 else { return }
        currentIndex += 1
        buildIndex = 0
    }

    func goPrevious() {
        if isPresenting, buildIndex > 0 {
            buildIndex -= 1
            return
        }
        guard currentIndex > 0 else { return }
        currentIndex -= 1
        // Landing back on a slide shows it fully built, like PowerPoint.
        buildIndex = isPresenting ? (currentSlide?.buildSteps.count ?? 0) : 0
    }

    func go(to index: Int) {
        guard presentation?.slides.indices.contains(index) == true else { return }
        currentIndex = index
        buildIndex = 0
    }

    private func cleanup() {
        if let root = extractedRoot {
            try? FileManager.default.removeItem(at: root)
        }
        extractedRoot = nil
    }
}
