import SwiftUI
import UniformTypeIdentifiers
import AppKit

struct ContentView: View {
    @ObservedObject var store: PresentationStore
    @State private var isDropTargeted = false
    /// Captured once so dragging the divider isn't undone by re-layout.
    @State private var sidebarIdealWidth: CGFloat?

    /// Sidebar starts at ~30% of the window so thumbnails are readable at a glance.
    private func defaultSidebarWidth(for windowWidth: CGFloat) -> CGFloat {
        min(max(windowWidth * 0.30, 220), 560)
    }

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
        .confirmationDialog("End the presentation?",
                            isPresented: Binding(get: { store.isConfirmingEnd },
                                                 set: { if !$0 { store.cancelEndPresentation() } }),
                            titleVisibility: .visible) {
            Button("End Presentation", role: .destructive) { store.endPresentation() }
            Button("Keep Presenting", role: .cancel) { store.cancelEndPresentation() }
        } message: {
            Text("The slide window will close and the audience display will return to the desktop.")
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
        GeometryReader { geo in
            NavigationSplitView {
                ThumbnailSidebar(store: store)
                    // The ideal is captured once: recomputing it on every layout
                    // pass would snap the divider back while the user drags it.
                    .navigationSplitViewColumnWidth(
                        min: 170,
                        ideal: sidebarIdealWidth ?? defaultSidebarWidth(for: geo.size.width),
                        max: 900)
            } detail: {
            if store.isPresenting {
                PresenterConsoleView(store: store)
            } else {
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
            }
        .onAppear {
            if sidebarIdealWidth == nil {
                sidebarIdealWidth = defaultSidebarWidth(for: geo.size.width)
            }
        }
        }
        .navigationTitle(store.fileName ?? "SlideViewer")
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
                    if store.isPresenting { store.endPresentation() }
                    else { store.startPresentation() }
                } label: {
                    Label(store.isPresenting ? "End" : "Present",
                          systemImage: store.isPresenting ? "stop.fill" : "play.fill")
                }
            }
        }
    }

    // MARK: - Actions

    private func openPanel() {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        panel.allowedContentTypes = ["pptx", "ppt"].compactMap { UTType(filenameExtension: $0) }
        if panel.runModal() == .OK, let url = panel.url {
            store.open(url: url)
        }
    }

    private func handleDrop(_ providers: [NSItemProvider]) -> Bool {
        guard let provider = providers.first else { return false }
        _ = provider.loadObject(ofClass: URL.self) { url, _ in
            guard let url, ["pptx", "ppt"].contains(url.pathExtension.lowercased()) else { return }
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
            Text("SlideViewer")
                .font(.largeTitle.weight(.semibold))
            Text("Viewer for PowerPoint & PPTX")
                .font(.title3)
                .foregroundStyle(.secondary)
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
        panel.allowedContentTypes = ["pptx", "ppt"].compactMap { UTType(filenameExtension: $0) }
        if panel.runModal() == .OK, let url = panel.url {
            store.open(url: url)
        }
    }
}

// MARK: - Thumbnails

struct ThumbnailSidebar: View {
    @ObservedObject var store: PresentationStore

    /// Thumbnails fill the sidebar's width, so dragging the divider scales them
    /// and the number of visible slides follows from the size the user chose.
    private func thumbnailWidth(size: CGSize) -> CGFloat {
        let numberColumn: CGFloat = 30      // slide number + spacing
        return max(60, size.width - numberColumn - 34)
    }

    var body: some View {
        GeometryReader { geo in
        ScrollViewReader { proxy in
            List(selection: Binding(
                get: { store.currentIndex },
                set: { if let v = $0 { store.go(to: v) } })
            ) {
                if let pres = store.presentation {
                    let aspect = pres.size.width / pres.size.height
                    let width = thumbnailWidth(size: geo.size)
                    ForEach(Array(pres.slides.enumerated()), id: \.offset) { index, slide in
                        let isCurrent = index == store.currentIndex
                        let isNext = index == store.currentIndex + 1
                        HStack(spacing: 8) {
                            Text("\(index + 1)")
                                .font(.caption.monospacedDigit())
                                .foregroundStyle(isCurrent ? Color.accentColor : .secondary)
                                .fontWeight(isCurrent ? .bold : .regular)
                                .frame(width: 22, alignment: .trailing)
                            SlideView(slide: slide, slideSize: pres.size)
                                .frame(width: width, height: width / aspect)
                                .background(Color.white)
                                .clipShape(RoundedRectangle(cornerRadius: 4))
                                .overlay(
                                    RoundedRectangle(cornerRadius: 4)
                                        .strokeBorder(borderColor(isCurrent: isCurrent, isNext: isNext),
                                                      lineWidth: isCurrent ? 3 : 1)
                                )
                            if store.isPresenting && (isCurrent || isNext) {
                                Text(isCurrent ? "LIVE" : "NEXT")
                                    .font(.caption2.weight(.bold))
                                    .foregroundStyle(isCurrent ? Color.red : .secondary)
                            }
                        }
                        .padding(.vertical, 2)
                        .tag(index)
                        .id(index)
                    }
                }
            }
            .onChange(of: store.currentIndex) { _, newValue in
                // Keep the live slide centred so previous and upcoming slides are
                // visible above and below it.
                withAnimation { proxy.scrollTo(newValue, anchor: .center) }
            }
            .onAppear {
                proxy.scrollTo(store.currentIndex, anchor: .center)
            }
        }
        }
    }

    private func borderColor(isCurrent: Bool, isNext: Bool) -> Color {
        if isCurrent { return .red }
        if isNext && store.isPresenting { return .accentColor }
        return .gray.opacity(0.3)
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
                store.startPresentation()
            } label: {
                Label("Present", systemImage: "play.fill")
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 8)
        .background(.bar)
    }
}

// MARK: - Presenter console

/// Shown in the main window while presenting: the slide the audience currently
/// sees, the camera below it, and transport controls. Upcoming slides are read
/// from the sidebar (which keeps the live slide centred) rather than a separate
/// next-slide pane.
struct PresenterConsoleView: View {
    @ObservedObject var store: PresentationStore
    @StateObject private var camera = CameraManager()

    var body: some View {
        VStack(spacing: 0) {
            VSplitView {
                // Current slide (what the audience sees now).
                VStack(alignment: .leading, spacing: 8) {
                    HStack {
                        Label("Now Presenting — Slide \(store.currentIndex + 1) of \(store.slideCount)",
                              systemImage: "dot.radiowaves.left.and.right")
                            .font(.headline)
                            .foregroundStyle(.red)
                        if let slide = store.currentSlide, !slide.buildSteps.isEmpty {
                            Text("Build \(store.buildIndex)/\(slide.buildSteps.count)")
                                .font(.subheadline.monospacedDigit())
                                .foregroundStyle(.secondary)
                                .padding(.leading, 8)
                        }
                        Spacer()
                        if let start = store.presentationStartDate {
                            Label {
                                Text(start, style: .timer)
                                    .font(.headline.monospacedDigit())
                            } icon: {
                                Image(systemName: "stopwatch")
                            }
                            .foregroundStyle(.secondary)
                            .help("Elapsed presentation time")
                        }
                    }
                    if let slide = store.currentSlide, let pres = store.presentation {
                        SlideView(slide: slide, slideSize: pres.size, showShadow: false)
                            .background(Color.black)
                            .clipShape(RoundedRectangle(cornerRadius: 6))
                            .overlay(RoundedRectangle(cornerRadius: 6)
                                .strokeBorder(Color.red.opacity(0.7), lineWidth: 2))
                    }
                    consoleTransport
                        .padding(.top, 4)
                    Spacer(minLength: 0)
                }
                .padding(16)
                .frame(maxWidth: .infinity, minHeight: 240, maxHeight: .infinity, alignment: .top)

                // Camera sits directly under the live slide; drag to resize.
                cameraPane
                    .padding(.horizontal, 16)
                    .padding(.vertical, 12)
                    .frame(maxWidth: .infinity, minHeight: 140, maxHeight: .infinity)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)

            Divider()

            HStack(spacing: 12) {
                Text("← → to navigate · esc to end")
                    .font(.caption)
                    .foregroundStyle(.tertiary)

                Spacer()

                Button(role: .destructive) {
                    store.requestEndPresentation()
                } label: {
                    Label("End Presentation", systemImage: "stop.fill")
                }
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 10)
            .background(.bar)
        }
        .background(Color(nsColor: .underPageBackgroundColor))
        .onDisappear { camera.stop() }
    }

    // MARK: Transport, directly beneath the live slide

    private var consoleTransport: some View {
        HStack(spacing: 12) {
            Spacer()
            Button {
                store.goPrevious()
            } label: {
                Label("Previous", systemImage: "chevron.left")
                    .labelStyle(.iconOnly)
                    .frame(width: 34, height: 24)
            }
            .disabled(!store.canGoPrevious)

            Text("Slide \(store.currentIndex + 1) of \(store.slideCount)")
                .font(.callout.monospacedDigit())
                .frame(minWidth: 130)

            Button {
                store.goNext()
            } label: {
                Label("Next", systemImage: "chevron.right")
                    .labelStyle(.iconOnly)
                    .frame(width: 34, height: 24)
            }
            .disabled(!store.canGoNext)
            Spacer()
        }
    }

    // MARK: Camera pane

    private var cameraPane: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Label("Camera", systemImage: "video")
                    .font(.headline)
                    .foregroundStyle(.secondary)
                Spacer()
                Toggle("", isOn: $camera.isEnabled)
                    .toggleStyle(.switch)
                    .controlSize(.small)
                    .labelsHidden()
            }

            if camera.isEnabled {
                if camera.devices.count > 1 {
                    Picker("", selection: $camera.selectedID) {
                        ForEach(camera.devices, id: \.uniqueID) { device in
                            Text(device.localizedName).tag(device.uniqueID)
                        }
                    }
                    .labelsHidden()
                    .controlSize(.small)
                }

                if camera.authorizationDenied {
                    RoundedRectangle(cornerRadius: 6)
                        .fill(Color.black.opacity(0.85))
                        .overlay(
                            Text("Camera access denied.\nEnable it in System Settings → Privacy & Security → Camera.")
                                .font(.caption)
                                .multilineTextAlignment(.center)
                                .foregroundStyle(.secondary)
                                .padding(8)
                        )
                } else if camera.devices.isEmpty {
                    RoundedRectangle(cornerRadius: 6)
                        .fill(Color.black.opacity(0.85))
                        .overlay(
                            Text("No camera detected")
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        )
                } else {
                    CameraPreviewView(session: camera.session)
                        .clipShape(RoundedRectangle(cornerRadius: 6))
                        .overlay(RoundedRectangle(cornerRadius: 6)
                            .strokeBorder(Color.gray.opacity(0.4), lineWidth: 1))
                }
            } else {
                RoundedRectangle(cornerRadius: 6)
                    .fill(Color.black.opacity(0.5))
                    .overlay(
                        Label("Camera off", systemImage: "video.slash")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    )
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .onChange(of: camera.isEnabled) { _, on in
            if on {
                camera.refreshDevices()
                camera.start()
            } else {
                camera.stop()
            }
        }
        .onChange(of: camera.selectedID) { _, _ in
            if camera.isEnabled { camera.applySelectionAndRun() }
        }
    }
}
