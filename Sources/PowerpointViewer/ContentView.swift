import SwiftUI
import UniformTypeIdentifiers
import AppKit

struct ContentView: View {
    @ObservedObject var store: PresentationStore
    @State private var isDropTargeted = false

    var body: some View {
        Group {
            if store.presentation != nil {
                loadedView
            } else {
                EmptyStateView(store: store, isDropTargeted: $isDropTargeted)
            }
        }
        .frame(minWidth: 720, minHeight: 480)
        .onDrop(of: [.fileURL], isTargeted: $isDropTargeted) { providers in
            handleDrop(providers)
        }
        .overlay {
            if store.isPresenting, let slide = store.currentSlide, let pres = store.presentation {
                PresentationOverlay(store: store, slide: slide, slideSize: pres.size)
            }
        }
        .alert("Could not open presentation",
               isPresented: Binding(get: { store.errorMessage != nil },
                                    set: { if !$0 { store.errorMessage = nil } })) {
            Button("OK", role: .cancel) { store.errorMessage = nil }
        } message: {
            Text(store.errorMessage ?? "")
        }
    }

    // MARK: - Loaded layout

    private var loadedView: some View {
        NavigationSplitView {
            ThumbnailSidebar(store: store)
                .navigationSplitViewColumnWidth(min: 160, ideal: 200, max: 260)
        } detail: {
            VStack(spacing: 0) {
                if let slide = store.currentSlide, let pres = store.presentation {
                    SlideView(slide: slide, slideSize: pres.size)
                        .padding(24)
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                        .background(Color(nsColor: .windowBackgroundColor))
                }
                Divider()
                SlideControls(store: store)
            }
        }
        .navigationTitle(store.fileName ?? "PowerPoint Viewer")
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Button {
                    openPanel()
                } label: {
                    Label("Open", systemImage: "folder")
                }
            }
            ToolbarItem(placement: .primaryAction) {
                Button {
                    store.isPresenting = true
                } label: {
                    Label("Present", systemImage: "play.fill")
                }
            }
        }
    }

    // MARK: - Actions

    private func openPanel() {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        if let pptx = UTType(filenameExtension: "pptx") {
            panel.allowedContentTypes = [pptx]
        }
        if panel.runModal() == .OK, let url = panel.url {
            store.open(url: url)
        }
    }

    private func handleDrop(_ providers: [NSItemProvider]) -> Bool {
        guard let provider = providers.first else { return false }
        _ = provider.loadObject(ofClass: URL.self) { url, _ in
            guard let url, url.pathExtension.lowercased() == "pptx" else { return }
            DispatchQueue.main.async { store.open(url: url) }
        }
        return true
    }
}

// MARK: - Empty state

struct EmptyStateView: View {
    @ObservedObject var store: PresentationStore
    @Binding var isDropTargeted: Bool

    var body: some View {
        VStack(spacing: 20) {
            Image(systemName: "rectangle.on.rectangle.angled")
                .font(.system(size: 64))
                .foregroundStyle(.secondary)
            Text("PowerPoint Viewer")
                .font(.largeTitle.weight(.semibold))
            Text("Drag a .pptx file here, or open one to begin.")
                .foregroundStyle(.secondary)
            if store.isLoading {
                ProgressView().padding(.top, 4)
            } else {
                Button("Open Presentation…") { openPanel() }
                    .controlSize(.large)
                    .buttonStyle(.borderedProminent)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color(nsColor: .windowBackgroundColor))
        .overlay {
            if isDropTargeted {
                RoundedRectangle(cornerRadius: 12)
                    .strokeBorder(Color.accentColor, style: StrokeStyle(lineWidth: 3, dash: [10]))
                    .padding(20)
            }
        }
    }

    private func openPanel() {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        if let pptx = UTType(filenameExtension: "pptx") {
            panel.allowedContentTypes = [pptx]
        }
        if panel.runModal() == .OK, let url = panel.url {
            store.open(url: url)
        }
    }
}

// MARK: - Thumbnails

struct ThumbnailSidebar: View {
    @ObservedObject var store: PresentationStore

    var body: some View {
        ScrollViewReader { proxy in
            List(selection: Binding(
                get: { store.currentIndex },
                set: { if let v = $0 { store.go(to: v) } })
            ) {
                if let pres = store.presentation {
                    ForEach(Array(pres.slides.enumerated()), id: \.offset) { index, slide in
                        HStack(spacing: 8) {
                            Text("\(index + 1)")
                                .font(.caption.monospacedDigit())
                                .foregroundStyle(.secondary)
                                .frame(width: 22, alignment: .trailing)
                            SlideView(slide: slide, slideSize: pres.size)
                                .frame(width: 150, height: 150 * pres.size.height / pres.size.width)
                                .background(Color.white)
                                .clipShape(RoundedRectangle(cornerRadius: 4))
                                .overlay(
                                    RoundedRectangle(cornerRadius: 4)
                                        .strokeBorder(index == store.currentIndex ? Color.accentColor : Color.gray.opacity(0.3),
                                                      lineWidth: index == store.currentIndex ? 2 : 1)
                                )
                        }
                        .padding(.vertical, 2)
                        .tag(index)
                        .id(index)
                    }
                }
            }
            .onChange(of: store.currentIndex) { _, newValue in
                withAnimation { proxy.scrollTo(newValue, anchor: .center) }
            }
        }
    }
}

// MARK: - Bottom controls

struct SlideControls: View {
    @ObservedObject var store: PresentationStore

    var body: some View {
        HStack {
            Button {
                store.goPrevious()
            } label: {
                Image(systemName: "chevron.left")
            }
            .disabled(store.currentIndex <= 0)

            Text("Slide \(store.currentIndex + 1) of \(store.slideCount)")
                .font(.callout.monospacedDigit())
                .frame(minWidth: 140)

            Button {
                store.goNext()
            } label: {
                Image(systemName: "chevron.right")
            }
            .disabled(store.currentIndex >= store.slideCount - 1)

            Spacer()

            Button {
                store.isPresenting = true
            } label: {
                Label("Present", systemImage: "play.fill")
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 8)
        .background(.bar)
    }
}

// MARK: - Present mode

struct PresentationOverlay: View {
    @ObservedObject var store: PresentationStore
    let slide: Slide
    let slideSize: CGSize
    @FocusState private var focused: Bool

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()
            SlideView(slide: slide, slideSize: slideSize)
                .padding(40)
        }
        .focusable()
        .focused($focused)
        .onKeyPress { press in
            switch press.key {
            case .rightArrow, .space, .downArrow, .return:
                store.goNext(); return .handled
            case .leftArrow, .upArrow:
                store.goPrevious(); return .handled
            case .escape:
                exitPresentation(); return .handled
            default:
                return .ignored
            }
        }
        .onTapGesture { store.goNext() }
        .onAppear {
            focused = true
            setFullScreen(true)
        }
        .transition(.opacity)
    }

    private func exitPresentation() {
        setFullScreen(false)
        store.isPresenting = false
    }

    private func setFullScreen(_ on: Bool) {
        guard let window = NSApp.keyWindow ?? NSApp.windows.first else { return }
        let isFull = window.styleMask.contains(.fullScreen)
        if on != isFull {
            window.toggleFullScreen(nil)
        }
    }
}
